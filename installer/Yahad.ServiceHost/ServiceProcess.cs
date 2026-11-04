using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// Supplied only by installed activation admission after protected identity and
// observed recovery checks. No executable/argument JSON or CLI factory.
internal sealed record VerifiedProcessSpec(string Image,string[] Arguments,string WorkingDirectory,
    string PrivateTemporaryDirectory,string PrivateFFmpeg,IReadOnlyDictionary<string,string> AppEnvironment,WindowsFileBoundary.DataLease? DataLease=null,Action? BeforeMutableStart=null);
internal readonly record struct ChildReady(uint PeerPid,string Type);
internal interface IChildControl:IDisposable
{
    string Endpoint {get;}
    Task<ChildReady> ReadyAsync(int expectedRootPid,CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}
internal interface IChildControlFactory {IChildControl Create();}

internal sealed class LocalChildControlFactory:IChildControlFactory
{
    public IChildControl Create()=>new LocalChildControl();
    private sealed class LocalChildControl:IChildControl
    {
        private readonly string name="Yahad."+Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream pipe;
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
        {await pipe.WriteAsync("{\"type\":\"stop\"}\n"u8.ToArray(),cancellation);await pipe.FlushAsync(cancellation);}
        public void Dispose()=>pipe.Dispose();
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint pid);
    }
}

internal sealed class ServiceProcessLauncher(VerifiedProcessSpec spec,IChildControlFactory channels):IPrivateAppLauncher
{
    private static readonly HashSet<string> Allowed=new(StringComparer.Ordinal)
    {"HOST","PORT","APP_ORIGIN","APP_ADDITIONAL_ORIGINS","COOKIE_SECURE","DATA_DIR","MASTER_KEY_FILE","CONNECTOR","ALLOW_REAL_WHATSAPP","DIAGNOSTICS_ENABLED","SETUP_CAPABILITY_FILE","YAHAD_NATIVE_PROBATION_NONCE","YAHAD_NATIVE_PROBATION_ACTIVATION","YAHAD_NATIVE_PROBATION_OPERATION"};
    public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var image=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.Image));
        var ffmpeg=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.PrivateFFmpeg));
        IChildControl? channel=null;LaunchLease? handoff=null;
        try
        {
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
                handoff=new LaunchLease(spec.PrivateTemporaryDirectory,environment);
            }
            else if(spec.BeforeMutableStart is not null)throw new IOException("SERVICE_HANDOFF_MISSING");
            void Handoff(Microsoft.Win32.SafeHandles.SafeProcessHandle process,int pid)
            {spec.BeforeMutableStart?.Invoke();if(handoff is not null)handoff.Publish(spec.DataLease!,process,pid,spec.AppEnvironment["DATA_DIR"]);}
            var job=JobProcess.Start(image,spec.Arguments,spec.WorkingDirectory,environment,Handoff);
            return Task.FromResult<IContainedAppSession>(new Session(job,channel,handoff));
        }
        catch(JobStartQuiescenceUnconfirmedException failure)
        {
            // Return ownership before reporting failed health. AppLifecycle can
            // now retry cleanup and preserve the grant/lease if still unknown.
            var failed=new Session(failure.OwnedProcess,channel!,handoff,failure);
            return Task.FromResult<IContainedAppSession>(failed);
        }
        catch{handoff?.Dispose();channel?.Dispose();throw;}
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
    }
    private sealed class LaunchLease:IDisposable
    {
        private readonly string directory,path,nonce;
        internal LaunchLease(string temporary,Dictionary<string,string> environment)
        {
            directory=Path.Combine(temporary,"launch-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(directory);
            path=Path.Combine(directory,"lease.json");nonce=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            environment.Add("YAHAD_NATIVE_LEASE_FILE",path);environment.Add("YAHAD_NATIVE_LEASE_NONCE",nonce);
        }
        internal void Publish(WindowsFileBoundary.DataLease lease,Microsoft.Win32.SafeHandles.SafeProcessHandle child,int pid,string data)
        {
            var remote=lease.DuplicateTo(child);
            var bytes=JsonSerializer.SerializeToUtf8Bytes(new HostLeaseGrant(1,pid,remote,data,nonce));
            using(var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough)){output.Write(bytes);output.Flush(true);}
        }
        public void Dispose(){if(File.Exists(path))File.Delete(path);Directory.Delete(directory,false);}
    }
    private sealed class Session(JobProcess job,IChildControl channel,LaunchLease? handoff,Exception? startFailure=null):IContainedAppSession
    {
        internal JobProcess Job=>job;internal IChildControl Channel=>channel;internal Exception? StartFailure=>startFailure;
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>channel.StopAsync(cancellation);
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync(){channel.Dispose();job.Dispose();handoff?.Dispose();return ValueTask.CompletedTask;}
    }
}
