using System.Security.Cryptography;
using Yahad.Install;

namespace Yahad.Service;

// The single Runtime service's launch configuration. Setup writes it as an
// administrator-owned, application-read-only file; nothing else selects what
// the service launches. The binaries are the version directory this host was
// started from (the service ImagePath), never a path named by a file.
internal sealed record InstalledLaunchConfiguration(int Format,string InstallationId,string DataId,int Port,string Origin);
internal static class InstalledLaunch
{
    internal const string FileName="installed.json";
    internal static string StateRoot=>InstalledLayout.StateRoot;
    internal static string VersionsRoot=>Path.Combine(InstalledLayout.BinaryRoot,"versions");
    internal static string VersionRoot(string version)=>Path.Combine(VersionsRoot,version);
    internal static int Run()
    {
        var root=StateRoot;var reader=new InstalledLaunchReader(root);
        var resolver=new InstalledLaunchResolver(root,RunningVersionRoot(),new LocalChildControlFactory());
        return new ServiceApplication(new NativeScmApi(),new InstalledStartAuthorizer(new WindowsServiceTokenReader(),reader,resolver),Environment.Exit,
            (error,steps)=>ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,error,steps)).Run();
    }
    // The host runs from <versions>\<release>\host\. Its own release names the
    // only version directory it may launch; a copy elsewhere is refused.
    internal static string RunningVersionRoot()
    {
        var actual=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..")));
        var expected=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(VersionRoot(ApplicationRelease.Version)));
        if(!actual.Equals(expected,StringComparison.OrdinalIgnoreCase))throw new IOException("INSTALLED_LAUNCH_IMAGE_LOCATION");
        return expected;
    }
    internal static byte[] Encode(InstalledLaunchConfiguration value)
    {var bytes=System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Validate(value));Parse(bytes);return bytes;}
    internal static InstalledLaunchConfiguration Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("INSTALLED_LAUNCH_BOUND");
        return Validate(NativeJson.Read<InstalledLaunchConfiguration>(bytes));
    }
    internal static InstalledLaunchConfiguration Validate(InstalledLaunchConfiguration value)
    {
        if(value.Format!=1)throw new IOException("INSTALLED_LAUNCH_FORMAT");
        Id(value.InstallationId);Id(value.DataId);
        if(value.Port is <1024 or >65535)throw new IOException("INSTALLED_LAUNCH_PORT");
        Origin(value.Origin,value.Port);return value;
    }
    // https is the LAN origin; plain http only as the fixed local loopback
    // origin of this port. Remote http origins are never admitted.
    internal static Uri Origin(string value,int port)
    {
        if(!Uri.TryCreate(value,UriKind.Absolute,out var origin)||origin.UserInfo!=""||origin.Query!=""||origin.Fragment!=""||origin.AbsolutePath!="/"||
            origin.GetLeftPart(UriPartial.Authority)!=value.TrimEnd('/')||
            !(origin.Scheme=="https"||origin.Scheme=="http"&&origin.Host=="127.0.0.1"&&origin.Port==port))throw new IOException("INSTALLED_LAUNCH_ORIGIN");
        return origin;
    }
    internal static void Id(string id){if(!Guid.TryParseExact(id,"N",out var parsed)||parsed.ToString("N")!=id)throw new IOException("INSTALLED_LAUNCH_IDENTIFIER");}
    internal static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
}
internal sealed class InstalledLaunchReader(string root):IInstallationReader
{
    public InstallationBinding Read()
    {
        using var file=WindowsFileBoundary.OpenInstalledRead(Path.Combine(root,InstalledLaunch.FileName));var bytes=file.Small(4096);
        return new(InstalledLaunch.Parse(bytes).InstallationId,InstalledLaunch.Hash(bytes));
    }
}
internal sealed class InstalledLaunchResolver(string root,string versionRoot,IChildControlFactory channels,
    RuntimeConnectorProfile connectorProfile=RuntimeConnectorProfile.Production,Func<VerifiedProcessSpec,IPrivateAppLauncher>? processFactory=null):IInstalledActivationResolver
{
    public Task<AuthorizedServiceStart> ResolveAsync(InstallationBinding binding,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var held=new HeldResources();
        try
        {
            var state=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));held.Add(WindowsFileBoundary.PinSingleServiceDirectory(state,true));
            var file=WindowsFileBoundary.OpenInstalledRead(Path.Combine(state,InstalledLaunch.FileName));held.Add(file);var bytes=file.Small(4096);
            if(InstalledLaunch.Hash(bytes)!=binding.ActivationReceiptDigest)throw new IOException("INSTALLED_LAUNCH_CHANGED");
            var config=InstalledLaunch.Parse(bytes);if(config.InstallationId!=binding.InstallationId)throw new IOException("INSTALLED_LAUNCH_CHANGED");
            var origin=InstalledLaunch.Origin(config.Origin,config.Port);
            // Administrator-owned, application read-only binaries for the whole run.
            var binaries=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(versionRoot));held.Add(WindowsFileBoundary.PinSingleServiceDirectory(binaries,true));
            var node=Path.Combine(binaries,"payload","runtime","node","node.exe");var app=Path.Combine(binaries,"payload","app");
            held.Add(WindowsFileBoundary.OpenInstalledRead(node));held.Add(WindowsFileBoundary.PinSingleServiceDirectory(app,true));
            // Administrator-owned dataset and key; only temporary/ and data/ are application-writable.
            var dataset=Path.Combine(state,"datasets",config.DataId);held.Add(WindowsFileBoundary.PinSingleServiceDirectory(dataset,true));
            var key=Path.Combine(dataset,"master-key");held.Add(WindowsFileBoundary.OpenInstalledRead(key));
            var temporary=Path.Combine(state,"temporary");held.Add(WindowsFileBoundary.PinSingleServiceDirectory(temporary,false));
            var data=Path.Combine(dataset,"data");var lease=WindowsFileBoundary.AcquireDataLease(data);held.Add(lease);
            var connector=RuntimeConnectorSettings.For(connectorProfile);
            var environment=new Dictionary<string,string>{{"HOST","127.0.0.1"},{"PORT",config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                {"APP_ORIGIN",origin.GetLeftPart(UriPartial.Authority)},{"COOKIE_SECURE",origin.Scheme=="https"?"true":"false"},{"DATA_DIR",data},{"MASTER_KEY_FILE",key},
                {"CONNECTOR",connector.Connector},{"ALLOW_REAL_WHATSAPP",connector.Approval},{"SETUP_CAPABILITY_FILE",Path.Combine(state,"Secrets","setup-capability.json")}};
            // LAN HTTPS keeps Secure cookies. Only the fixed local loopback origin
            // is added for bootstrap; remote origins are never inferred here.
            if(origin.Scheme=="https"&&NetworkPlan.IsPrivateLanV4(origin.Host))environment["APP_ADDITIONAL_ORIGINS"]=$"http://127.0.0.1:{config.Port}";
            var release=new AppRelease(ApplicationRelease.Version);
            var spec=new VerifiedProcessSpec(node,["server/main.mjs"],app,temporary,Path.Combine(binaries,"payload","runtime","ffmpeg","ffmpeg.exe"),environment,lease,held.BeginMutableStart);
            var launcher=processFactory is null?new ServiceProcessLauncher(spec,channels):processFactory(spec)??throw new IOException("INSTALLED_PROCESS_FACTORY_NULL");
            held.Release=release.Id;
            return Task.FromResult(new AuthorizedServiceStart(release,launcher,held,held));
        }
        catch{held.Dispose();throw;}
    }
    // Every pin taken during admission is held until the service stops. The
    // start barrier admits exactly one start of exactly this release.
    private sealed class HeldResources:IAppRecoveryBarrier,IDisposable
    {
        private readonly List<IDisposable> resources=[];private bool disposed,started;internal string Release="";
        internal void Add(IDisposable item)=>resources.Add(item);
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();ObjectDisposedException.ThrowIf(disposed,this);
            if(started||release.Id!=Release)throw new IOException("INSTALLED_ALREADY_STARTED");return Task.CompletedTask;
        }
        internal void BeginMutableStart(){ObjectDisposedException.ThrowIf(disposed,this);if(started)throw new IOException("INSTALLED_ALREADY_STARTED");started=true;}
        public void Dispose(){if(disposed)return;disposed=true;for(var i=resources.Count-1;i>=0;i--)resources[i].Dispose();}
    }
}
