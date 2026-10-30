using System.ComponentModel;
using System.IO.Pipes;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Yahad.Install;

/// <summary>Unsigned candidate fixture only. There is deliberately no production-intake factory.</summary>
public sealed class DeveloperRuntimeLayout : IDisposable
{
    private readonly List<FileStream> pinned=[];
    public string Root { get; }
    public string App => Path.Combine(Root,"app");
    public string Node => Path.Combine(Root,"runtime","node","node.exe");
    public string FFmpeg => Path.Combine(Root,"runtime","ffmpeg","ffmpeg.exe");
    public string Supervisor => Path.Combine(App,"src","native-supervisor.ts");
    public string Loader => Path.Combine(App,"node_modules","tsx","dist","loader.mjs");
    private DeveloperRuntimeLayout(string root) { Root=root; }
    public static DeveloperRuntimeLayout Open(string root,string manifestFile)
    {
        root=Path.GetFullPath(root);
        if(root.StartsWith(@"\\")||new FileInfo(manifestFile).Length>8*1024*1024)throw new IOException("FIXTURE_PAYLOAD_REQUIRED");
        for(var ancestor=new DirectoryInfo(root);ancestor is not null;ancestor=ancestor.Parent)
            if((ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("PAYLOAD_REPARSE");
        using var document=JsonDocument.Parse(File.ReadAllBytes(manifestFile));var m=document.RootElement;
        if(m.GetProperty("kind").GetString()!="windows-runtime-candidate"||m.GetProperty("profile").GetString()!="full"||m.GetProperty("target").GetString()!="win-x64"||m.GetProperty("familyInstallationReady").GetBoolean())throw new IOException("DEVELOPER_CANDIDATE_ONLY");
        var records=m.GetProperty("files").EnumerateArray().ToDictionary(r=>r.GetProperty("path").GetString()!,r=>r.GetProperty("sha256").GetString()!,StringComparer.OrdinalIgnoreCase);
        string[] required=["runtime/node/node.exe","runtime/ffmpeg/ffmpeg.exe","app/src/main.ts","app/src/native-supervisor.ts","app/node_modules/tsx/dist/loader.mjs","app/native/Yahad.Platform.exe"];
        var selected=required.Concat(records.Keys.Where(p=>p.StartsWith("runtime/ffmpeg/",StringComparison.Ordinal)||p.StartsWith("app/native/",StringComparison.Ordinal))).Distinct(StringComparer.OrdinalIgnoreCase);
        var result=new DeveloperRuntimeLayout(root);
        try
        {
            foreach(var relative in selected)
            {
                if(!records.TryGetValue(relative,out var expected)||relative.Split('/').Any(p=>p is "" or "." or ".."||p.Contains(':')||p.Contains('\\')))throw new IOException("PAYLOAD_RECORD");
                var path=Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar));
                for(var ancestor=new FileInfo(path).Directory;ancestor is not null&&ancestor.FullName.StartsWith(root,StringComparison.OrdinalIgnoreCase);ancestor=ancestor.Parent)
                    if((ancestor.Attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("PAYLOAD_REPARSE");
                if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0)throw new IOException("PAYLOAD_REPARSE");
                var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);result.pinned.Add(stream);
                if(Convert.ToHexStringLower(SHA256.HashData(stream))!=expected)throw new IOException("PAYLOAD_CHECKSUM");
            }
            return result;
        }
        catch { result.Dispose();throw; }
    }
    public void Dispose(){foreach(var stream in pinned)stream.Dispose();pinned.Clear();}
}

public sealed record NativeFixtureSettings(string TemporaryDirectory,string DataDirectory,string MasterKeyPath,int Port);

/// <summary>Real contained mock-app launch for tests; no SCM or production trust/backup observations.</summary>
public sealed class NativeFixtureLauncher(DeveloperRuntimeLayout layout,NativeFixtureSettings settings) : IPrivateAppLauncher
{
    public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(settings.Port is <1024 or >65535)throw new IOException("FIXTURE_PORT");
        var name="Yahad."+Guid.NewGuid().ToString("N");
        var pipe=LocalControlPipe.Create(name);
        try
        {
            var env=LaunchEnvironment.Clean(settings.TemporaryDirectory);
            foreach(var pair in new Dictionary<string,string>
            {
                ["NODE_ENV"]="production",["FFMPEG_PATH"]=layout.FFmpeg,["HOST"]="127.0.0.1",["PORT"]=settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["APP_ORIGIN"]=$"http://127.0.0.1:{settings.Port}",["COOKIE_SECURE"]="false",["DATA_DIR"]=settings.DataDirectory,
                ["MASTER_KEY_FILE"]=settings.MasterKeyPath,["CONNECTOR"]="mock",["ALLOW_REAL_WHATSAPP"]="no",["DIAGNOSTICS_ENABLED"]="false",
                ["SETUP_CAPABILITY_FILE"]=Path.Combine(settings.TemporaryDirectory,"absent-capability"),["YAHAD_NATIVE_PIPE"]=@"\\.\pipe\"+name
            })env.Add(pair.Key,pair.Value);
            var job=JobProcess.Start(layout.Node,["--import",new Uri(layout.Loader).AbsoluteUri,layout.Supervisor],layout.App,env);
            return Task.FromResult<IContainedAppSession>(new NativeSession(job,pipe,settings.Port));
        }
        catch {pipe.Dispose();throw;}
    }
    public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation) => ((NativeSession)session).ReadyAsync(cancellation);

    private sealed class NativeSession(JobProcess job,NamedPipeServerStream pipe,int port) : IContainedAppSession
    {
        public async Task ReadyAsync(CancellationToken cancellation)
        {
            await pipe.WaitForConnectionAsync(cancellation);
            if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var peer)||peer!=(uint)job.ProcessId)throw new IOException("NATIVE_IPC_PEER");
            using var frame=new MemoryStream();var one=new byte[1];
            while(frame.Length<1024)
            {
                if(await pipe.ReadAsync(one,cancellation)!=1)throw new IOException("NATIVE_IPC_EOF");
                if(one[0]==10)break;frame.WriteByte(one[0]);
            }
            if(frame.Length>=1024)throw new IOException("NATIVE_IPC_FRAME");
            using var json=JsonDocument.Parse(frame.ToArray());
            if(json.RootElement.GetProperty("type").GetString()!="ready")throw new IOException("NATIVE_IPC_STATE");
            using var client=new HttpClient(new HttpClientHandler{UseProxy=false});
            using var response=await client.GetAsync($"http://127.0.0.1:{port}/health/ready",cancellation);response.EnsureSuccessStatusCode();
            using var health=JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellation));
            if(!health.RootElement.GetProperty("ok").GetBoolean())throw new IOException("NATIVE_APP_UNHEALTHY");
        }
        public async Task RequestGracefulStopAsync(CancellationToken cancellation)
        {await pipe.WriteAsync("{\"type\":\"stop\"}\n"u8.ToArray(),cancellation);await pipe.FlushAsync(cancellation);}
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)=>job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)=>job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync(){pipe.Dispose();job.Dispose();return ValueTask.CompletedTask;}
        [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetNamedPipeClientProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe,out uint processId);
    }
}
