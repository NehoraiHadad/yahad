using Yahad.Install;

namespace Yahad.Service;

// The single Runtime service's launch configuration. Setup writes it as an
// administrator-owned, application-read-only file; nothing else selects what
// the service launches. The binaries are the version directory of this host's own
// release (the service ImagePath), never a path named by a file.
internal sealed record InstalledLaunchConfiguration(int Format,string InstallationId,string DataId,int Port,string Origin);
internal static class InstalledLaunch
{
    internal const string FileName="installed.json";
    // This enables the existing connector, not an account connection. A new family
    // remains unlinked until its authenticated parent uses POST /api/parent/connect
    // in the existing web UI (origin + CSRF checked). Linked families retain their
    // previously requested reconnect behavior. No native QR/account setup is added.
    internal const string Connector="baileys",ConnectorApproval="I_APPROVE_UNOFFICIAL_LINK";
    internal static int Run()
    {
        ServiceController? controller=null;
        return ScmController.Dispatch(new NativeScmApi(),ServiceDefinition.Name,Environment.Exit,()=>controller=new ServiceController(new InstalledLaunchResolver(),reportFailure:
            error=>ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Runtime,error,controller!.StartupSteps)));
    }
    internal static byte[] Encode(InstalledLaunchConfiguration value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value);
    internal static InstalledLaunchConfiguration Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("INSTALLED_LAUNCH_BOUND");
        var value=NativeJson.Read<InstalledLaunchConfiguration>(bytes);
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
}
internal sealed class InstalledLaunchResolver:IServiceStartAuthorizer
{
    // File work and taking the lease must not occupy the SCM entry/control thread.
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>
    {
        cancellation.ThrowIfCancellationRequested();
        var state=InstalledLayout.StateRoot;var binaries=InstalledLayout.VersionRoot(ApplicationRelease.Version);
        var config=InstalledLaunch.Parse(NativeInstallFiles.ReadRecord(Path.Combine(state,InstalledLaunch.FileName),4096));
        var origin=InstalledLaunch.Origin(config.Origin,config.Port);
        var node=Path.Combine(binaries,"payload","runtime","node","node.exe");var app=Path.Combine(binaries,"payload","app");
        // Only temporary/ and data/ are application-writable.
        var dataset=Path.Combine(state,"datasets",config.DataId);var key=Path.Combine(dataset,"master-key");
        var temporary=Path.Combine(state,"temporary");
        var data=Path.Combine(dataset,"data");
        // The data lease taken here is held until the service stops.
        var lease=WindowsFileBoundary.AcquireDataLease(data);
        try
        {
            var environment=new Dictionary<string,string>{{"HOST","127.0.0.1"},{"PORT",config.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)},
                {"APP_ORIGIN",origin.GetLeftPart(UriPartial.Authority)},{"COOKIE_SECURE",origin.Scheme=="https"?"true":"false"},{"DATA_DIR",data},{"MASTER_KEY_FILE",key},
                {"CONNECTOR",InstalledLaunch.Connector},{"ALLOW_REAL_WHATSAPP",InstalledLaunch.ConnectorApproval},{"SETUP_CAPABILITY_FILE",Path.Combine(state,"Secrets","setup-capability.json")}};
            // LAN HTTPS keeps Secure cookies. Only the fixed local loopback origin
            // is added for bootstrap; remote origins are never inferred here.
            if(origin.Scheme=="https"&&NetworkPlan.IsPrivateLanV4(origin.Host))environment["APP_ADDITIONAL_ORIGINS"]=$"http://127.0.0.1:{config.Port}";
            var spec=new VerifiedProcessSpec(node,["server/main.mjs"],app,temporary,Path.Combine(binaries,"payload","runtime","ffmpeg","ffmpeg.exe"),environment,lease);
            return new AuthorizedServiceStart(new ServiceProcessLauncher(spec,()=>new LocalChildControl()),lease);
        }
        catch{lease.Dispose();throw;}
    },cancellation);
}
