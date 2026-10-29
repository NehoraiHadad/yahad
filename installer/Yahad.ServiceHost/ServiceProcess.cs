using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;

namespace Yahad.Service;

// These values must be supplied by the still-unconfigured installed activation
// resolver after authenticated release/recovery admission. No JSON/CLI factory.
internal sealed record VerifiedProcessSpec(string Image,string ImageSha256,string[] Arguments,string WorkingDirectory,
    string PrivateTemporaryDirectory,string PrivateFFmpeg,IReadOnlyDictionary<string,string> AppEnvironment);
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
    {"HOST","PORT","APP_ORIGIN","COOKIE_SECURE","DATA_DIR","MASTER_KEY_FILE","CONNECTOR","ALLOW_REAL_WHATSAPP","DIAGNOSTICS_ENABLED","SETUP_CAPABILITY_FILE"};
    public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var image=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.Image));
        var ffmpeg=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(spec.PrivateFFmpeg));
        SafeDirectory.NoReparseAncestors(image);SafeDirectory.NoReparseAncestors(spec.WorkingDirectory);SafeDirectory.NoReparseAncestors(spec.PrivateTemporaryDirectory);
        var pinned=new FileStream(image,FileMode.Open,FileAccess.Read,FileShare.Read);
        IChildControl? channel=null;
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
            var job=JobProcess.Start(image,spec.Arguments,spec.WorkingDirectory,environment);
            return Task.FromResult<IContainedAppSession>(new Session(job,channel,pinned));
        }
        catch{channel?.Dispose();pinned.Dispose();throw;}
    }
    public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
    {
        var native=(Session)session;var ready=await native.Channel.ReadyAsync(native.Job.ProcessId,cancellation);
        if(ready.PeerPid!=(uint)native.Job.ProcessId||ready.Type!="ready")throw new IOException("SERVICE_CHILD_READY");
    }
    private sealed class Session(JobProcess job,IChildControl channel,FileStream pinned):IContainedAppSession
    {
        internal JobProcess Job=>job;internal IChildControl Channel=>channel;
        public Task RequestGracefulStopAsync(CancellationToken cancellation)=>channel.StopAsync(cancellation);
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync(){channel.Dispose();job.Dispose();pinned.Dispose();return ValueTask.CompletedTask;}
    }
}
