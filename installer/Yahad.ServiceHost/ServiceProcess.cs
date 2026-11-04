using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// Supplied only by installed activation admission. No executable/argument JSON
// or CLI factory.
internal sealed record VerifiedProcessSpec(string Image,string[] Arguments,string WorkingDirectory,
    string PrivateTemporaryDirectory,string PrivateFFmpeg,IReadOnlyDictionary<string,string> AppEnvironment,WindowsFileBoundary.DataLease DataLease);
internal readonly record struct ChildReady(uint PeerPid,string Type);
internal interface IChildControl:IDisposable
{
    string Endpoint {get;}
    Task<ChildReady> ReadyAsync(CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}

internal sealed class LocalChildControl:IChildControl
{
    private readonly string name="Yahad."+Guid.NewGuid().ToString("N");
    private readonly NamedPipeServerStream pipe;
    internal LocalChildControl(){pipe=LocalControlPipe.Create(name);}
    public string Endpoint=>@"\\.\pipe\"+name;
    public async Task<ChildReady> ReadyAsync(CancellationToken cancellation)
    {
        await pipe.WaitForConnectionAsync(cancellation);
        if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var peer))throw new IOException("SERVICE_CHILD_PEER");
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

internal sealed class ServiceProcessLauncher(VerifiedProcessSpec spec,Func<IChildControl> channels):IPrivateAppLauncher
{
    public Task<IContainedAppSession> StartAsync(CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        IChildControl? channel=null;LaunchLease? handoff=null;
        try
        {
            var environment=LaunchEnvironment.Clean(spec.PrivateTemporaryDirectory);
            foreach(var pair in spec.AppEnvironment)environment.Add(pair.Key,pair.Value);
            environment.Add("NODE_ENV","production");environment.Add("FFMPEG_PATH",spec.PrivateFFmpeg);
            channel=channels();environment.Add("YAHAD_NATIVE_PIPE",channel.Endpoint);
            var lease=handoff=new LaunchLease(spec.PrivateTemporaryDirectory,environment);
            var job=JobProcess.Start(spec.Image,spec.Arguments,spec.WorkingDirectory,environment,(process,pid)=>lease.Publish(spec.DataLease,process,pid,spec.AppEnvironment["DATA_DIR"]));
            return Task.FromResult<IContainedAppSession>(new Session(job,channel,lease));
        }
        catch{handoff?.Dispose();channel?.Dispose();throw;}
    }
    public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
    {
        var native=(Session)session;
        // A root that ends before reporting can never report. Observe its exit
        // alongside the channel so it fails now with its exit code, instead of
        // holding startup open until the whole admission budget expires. If the
        // exit cannot be observed, the channel alone decides, as before.
        using var settle=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        var reporting=native.Channel.ReadyAsync(settle.Token);var exited=native.Job.WaitAsync(settle.Token);
        if(await Task.WhenAny(reporting,exited)==exited&&exited.IsCompletedSuccessfully&&!reporting.IsCompleted)
        {
            settle.Cancel();try{await reporting;}catch{}
            if(!reporting.IsCompletedSuccessfully)throw new ChildExitedException(exited.Result);
        }
        ChildReady ready;
        try{ready=await reporting;}
        finally{settle.Cancel();try{await exited;}catch{}}
        if(ready.PeerPid!=(uint)native.Job.ProcessId)throw new IOException("SERVICE_CHILD_PEER");
        if(ready.Type!="ready")throw new IOException("SERVICE_CHILD_READY");
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
    private sealed class Session(JobProcess job,IChildControl channel,LaunchLease handoff):IContainedAppSession
    {
        internal JobProcess Job=>job;internal IChildControl Channel=>channel;
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>channel.StopAsync(cancellation);
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync(){channel.Dispose();job.Dispose();handoff.Dispose();return ValueTask.CompletedTask;}
    }
}
