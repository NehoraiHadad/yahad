using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// Supplied only by installed activation admission after protected identity and
// observed recovery checks. No executable/argument JSON or CLI factory.
internal sealed record VerifiedProcessSpec(string Image,string ImageSha256,string[] Arguments,string WorkingDirectory,
    string PrivateTemporaryDirectory,string PrivateFFmpeg,IReadOnlyDictionary<string,string> AppEnvironment,WindowsFileBoundary.DataLease? DataLease=null,Action? BeforeMutableStart=null,InstalledStateFiles? StateFiles=null,Action? AfterHealthy=null);
internal readonly record struct ChildReady(uint PeerPid,string Type);
internal interface IChildControl:IDisposable
{
    string Endpoint {get;}
    Task<ChildReady> ReadyAsync(int expectedRootPid,CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}
internal interface IChildControlFactory {IChildControl Create();}
internal interface IUpdateIdleSession
{
    Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation);
    Task CancelUpdateAsync(string id,CancellationToken cancellation);
}

internal sealed class LocalChildControlFactory:IChildControlFactory
{
    public IChildControl Create()=>new LocalChildControl();
    private sealed class LocalChildControl:IChildControl,IUpdateIdleSession
    {
        private readonly string name="Yahad."+Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;
        private readonly SemaphoreSlim commands=new(1,1);
        internal LocalChildControl(){pipe=LocalControlPipe.Create(name);}
        public string Endpoint=>@"\\.\pipe\"+name;
        public async Task<ChildReady> ReadyAsync(int expectedRootPid,CancellationToken cancellation)
        {
            await pipe.WaitForConnectionAsync(cancellation);
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var peer)||peer!=(uint)expectedRootPid)throw new IOException("SERVICE_CHILD_PEER");
            using var frame=new MemoryStream();var one=new byte[1];var ended=false;
            while(frame.Length<1024)
            {
                if(await pipe.ReadAsync(one,cancellation)!=1)throw new IOException("SERVICE_CHILD_EOF");
                if(one[0]==10){ended=true;break;}frame.WriteByte(one[0]);
            }
            if(!ended)throw new IOException("SERVICE_CHILD_FRAME");
            using var json=JsonDocument.Parse(frame.ToArray());
            return new(peer,json.RootElement.GetProperty("type").GetString()??"");
        }
        public async Task StopAsync(CancellationToken cancellation)
        {await commands.WaitAsync(cancellation);try{await pipe.WriteAsync("{\"type\":\"stop\"}\n"u8.ToArray(),cancellation);await pipe.FlushAsync(cancellation);}finally{commands.Release();}}
        public async Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation)
        {
            if(!Guid.TryParseExact(id,"N",out var parsed)||parsed.ToString("N")!=id)throw new IOException("SERVICE_UPDATE_ID");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await commands.WaitAsync(deadline.Token);
            try
            {
                await SendAsync("prepare-update",id,deadline.Token);
                // Once a command was written, retain ownership of its bounded
                // response even if the caller revokes. Cancelling halfway through
                // a frame cannot be treated as a fresh framing boundary.
                using var responseDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var frame=new MemoryStream();var one=new byte[1];
                while(frame.Length<1024)
                {
                    if(await pipe.ReadAsync(one,responseDeadline.Token)!=1)throw new IOException("SERVICE_UPDATE_EOF");
                    if(one[0]==10)
                    {
                        var reply=NativeJson.Read<JsonElement>(frame.ToArray());
                        if(reply.EnumerateObject().Count()!=2||reply.GetProperty("id").GetString()!=id)throw new IOException("SERVICE_UPDATE_REPLY");
                        var idle=reply.GetProperty("type").GetString() switch{"update-idle"=>true,"update-busy"=>false,_=>throw new IOException("SERVICE_UPDATE_REPLY")};
                        cancellation.ThrowIfCancellationRequested();return idle;
                    }
                    frame.WriteByte(one[0]);
                }
                throw new IOException("SERVICE_UPDATE_FRAME");
            }
            finally{commands.Release();}
        }
        public async Task CancelUpdateAsync(string id,CancellationToken cancellation)
        {
            if(!Guid.TryParseExact(id,"N",out var parsed)||parsed.ToString("N")!=id)throw new IOException("SERVICE_UPDATE_ID");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(5));
            await commands.WaitAsync(deadline.Token);
            try
            {
                await SendAsync("cancel-update",id,deadline.Token);
                // A prepare response may have arrived after its caller timed out.
                // Drain only that same operation, then require an explicit cancel
                // acknowledgement. A successful write alone cannot prove idle.
                for(var attempt=0;attempt<2;attempt++)
                {
                    using var frame=new MemoryStream();var one=new byte[1];var ended=false;
                    while(frame.Length<1024){if(await pipe.ReadAsync(one,deadline.Token)!=1)throw new IOException("SERVICE_UPDATE_EOF");if(one[0]==10){ended=true;break;}frame.WriteByte(one[0]);}
                    if(!ended)throw new IOException("SERVICE_UPDATE_FRAME");var reply=NativeJson.Read<JsonElement>(frame.ToArray());
                    if(reply.EnumerateObject().Count()!=2||reply.GetProperty("id").GetString()!=id)throw new IOException("SERVICE_UPDATE_REPLY");
                    var type=reply.GetProperty("type").GetString();if(type=="update-cancelled")return;
                    if(attempt!=0||type is not("update-idle" or "update-busy"))throw new IOException("SERVICE_UPDATE_CANCEL_UNCONFIRMED");
                }
                throw new IOException("SERVICE_UPDATE_CANCEL_UNCONFIRMED");
            }
            finally{commands.Release();}
        }
        private async Task SendAsync(string type,string id,CancellationToken cancellation)
        {await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new{type,id}),cancellation);await pipe.WriteAsync(new byte[]{10},cancellation);await pipe.FlushAsync(cancellation);}
        public void Dispose()=>pipe.Dispose();
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint pid);
    }
}

internal sealed class ServiceProcessLauncher(VerifiedProcessSpec spec,IChildControlFactory channels,JobProcessFaultInjection? fault=null):IPrivateAppLauncher
{
    private static readonly HashSet<string> Allowed=new(StringComparer.Ordinal)
    {"HOST","PORT","APP_ORIGIN","APP_ADDITIONAL_ORIGINS","COOKIE_SECURE","DATA_DIR","MASTER_KEY_FILE","CONNECTOR","ALLOW_REAL_WHATSAPP","DIAGNOSTICS_ENABLED","SETUP_CAPABILITY_FILE","YAHAD_NATIVE_PROBATION_NONCE","YAHAD_NATIVE_PROBATION_ACTIVATION","YAHAD_NATIVE_PROBATION_OPERATION"};
    public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var image=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.Image));
        var ffmpeg=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.PrivateFFmpeg));
        SafeDirectory.NoReparseAncestors(image);SafeDirectory.NoReparseAncestors(spec.WorkingDirectory);SafeDirectory.NoReparseAncestors(spec.PrivateTemporaryDirectory);
        var pinned=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read);
        IChildControl? channel=null;LaunchLease? handoff=null;
        try
        {
            if(Convert.ToHexStringLower(SHA256.HashData(pinned))!=spec.ImageSha256)throw new IOException("SERVICE_IMAGE_CHANGED");
            var environment=LaunchEnvironment.Clean(spec.PrivateTemporaryDirectory);
            foreach(var pair in spec.AppEnvironment)
            {
                if(!Allowed.Contains(pair.Key))throw new IOException("SERVICE_ENVIRONMENT_KEY");
                environment.Add(pair.Key,pair.Value);
            }
            environment.Add("NODE_ENV","production");environment.Add("FFMPEG_PATH",ffmpeg);
            channel=channels.Create();environment.Add("YAHAD_NATIVE_PIPE",channel.Endpoint);
            if(spec.DataLease is not null)
            {
                handoff=new LaunchLease(spec.PrivateTemporaryDirectory,environment,spec.StateFiles);
            }
            else if(spec.BeforeMutableStart is not null)throw new IOException("SERVICE_HANDOFF_MISSING");
            void Handoff(Microsoft.Win32.SafeHandles.SafeProcessHandle process,int pid)
            {spec.BeforeMutableStart?.Invoke();if(handoff is not null)handoff.Publish(spec.DataLease!,process,pid,spec.AppEnvironment["DATA_DIR"]);}
            var job=fault is null?JobProcess.Start(image,spec.Arguments,spec.WorkingDirectory,environment,Handoff):
                JobProcess.StartWithFault(image,spec.Arguments,spec.WorkingDirectory,environment,Handoff,fault);
            return Task.FromResult<IContainedAppSession>(new Session(job,channel,pinned,handoff));
        }
        catch(JobStartQuiescenceUnconfirmedException failure)
        {
            // Return ownership before reporting failed health. AppLifecycle can
            // now retry cleanup and preserve the grant/lease if still unknown.
            var failed=new Session(failure.OwnedProcess,channel!,pinned,handoff,failure);
            if(fault is not null)fault.FailedStartSession=failed;
            return Task.FromResult<IContainedAppSession>(failed);
        }
        catch{handoff?.Dispose();channel?.Dispose();pinned.Dispose();throw;}
    }
    public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
    {
        var native=(Session)session;if(native.StartFailure is not null)throw new IOException("SERVICE_PROCESS_START_FAILED",native.StartFailure);
        // A root that ends before reporting can never report. Observe its exit
        // alongside the channel so it fails now with its exit code, instead of
        // holding startup open until the whole admission budget expires. If the
        // exit cannot be observed, the channel alone decides, as before.
        using var settle=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var reporting=native.Channel.ReadyAsync(native.Job.ProcessId,settle.Token);var exited=native.Job.WaitAsync(settle.Token);
        if(await Task.WhenAny(reporting,exited)==exited&&exited.IsCompletedSuccessfully&&!reporting.IsCompleted)
        {
            settle.Cancel();try{await reporting;}catch{}
            if(!reporting.IsCompletedSuccessfully)throw new ChildExitedException(exited.Result);
        }
        ChildReady ready;
        try{ready=await reporting;}
        finally{settle.Cancel();try{await exited;}catch{}}
        if(ready.PeerPid!=(uint)native.Job.ProcessId||ready.Type!="ready")throw new IOException("SERVICE_CHILD_READY");
        spec.AfterHealthy?.Invoke();
    }
    private sealed class LaunchLease:IDisposable
    {
        private readonly string directory,path,nonce;private IDisposable? pin;private readonly InstalledStateFiles? files;
        internal LaunchLease(string temporary,Dictionary<string,string> environment,InstalledStateFiles? files)
        {
            this.files=files;directory=Path.Combine(temporary,"launch-"+Guid.NewGuid().ToString("N"));if(files is null)Directory.CreateDirectory(directory);else files.CreateDirectory(directory);
            using var check=WindowsFileBoundary.PinPrivateDirectory(directory);
            path=Path.Combine(directory,"lease.json");nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            environment.Add("YAHAD_NATIVE_LEASE_FILE",path);environment.Add("YAHAD_NATIVE_LEASE_NONCE",nonce);
        }
        internal void Publish(WindowsFileBoundary.DataLease lease,Microsoft.Win32.SafeHandles.SafeProcessHandle child,int pid,string data)
        {
            var remote=lease.DuplicateTo(child);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new HostLeaseGrant(1,pid,remote,data,nonce));
            using(var output=files?.Create(path)??new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
            pin=WindowsFileBoundary.OpenPrivateRead(path);
        }
        public void Dispose(){pin?.Dispose();if(File.Exists(path))File.Delete(path);Directory.Delete(directory,false);}
    }
    private sealed class Session(JobProcess job,IChildControl channel,FileStream pinned,LaunchLease? handoff,Exception? startFailure=null):IContainedAppSession,IUpdateIdleSession
    {
        internal JobProcess Job=>job;internal IChildControl Channel=>channel;internal Exception? StartFailure=>startFailure;
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>channel.StopAsync(cancellation);
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation)=>channel is IUpdateIdleSession idle?idle.PrepareUpdateAsync(id,cancellation):Task.FromResult(false);
        public Task CancelUpdateAsync(string id,CancellationToken cancellation)=>channel is IUpdateIdleSession idle?idle.CancelUpdateAsync(id,cancellation):Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync(){channel.Dispose();job.Dispose();pinned.Dispose();handoff?.Dispose();return ValueTask.CompletedTask;}
    }
}
