using Yahad.Install;

namespace Yahad.Service;

// The single Runtime service's launch configuration. Setup writes it as an
// administrator-owned, application-read-only file; nothing else selects what
// the service launches. The binaries are this host's own infrastructure tree (the
// service ImagePath) and the app tree of the verified active pointer, never a path
// named by a file.
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
    internal static byte[] Encode(InstalledLaunchConfiguration value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,ServiceHostJsonContext.Default.InstalledLaunchConfiguration);
    internal static InstalledLaunchConfiguration Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("INSTALLED_LAUNCH_BOUND");
        var value=NativeJson.Read(bytes,ServiceHostJsonContext.Default.InstalledLaunchConfiguration);
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
// What the parent is shown about the home network: the join page Setup set up
// (the plain-HTTP help page, or the HTTPS one when the owner turned that off) and
// the home certificate's fingerprint, to compare on a new device. Setup writes it
// beside installed.json whenever it enables the home network or moves the proxy.
// A separate file, so installed.json keeps the format older hosts read.
internal sealed record InstalledHomeNetwork(int Format,string JoinPage,string RootSha256);
internal static class InstalledHomeNetworkRecord
{
    internal const string FileName="home-network.json";
    internal static byte[] Encode(InstalledHomeNetwork value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(value,ServiceHostJsonContext.Default.InstalledHomeNetwork);
    internal static InstalledHomeNetwork Parse(byte[] bytes,Uri origin)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("HOME_NETWORK_BOUND");
        var value=NativeJson.Read(bytes,ServiceHostJsonContext.Default.InstalledHomeNetwork);
        if(value.Format!=1)throw new IOException("HOME_NETWORK_FORMAT");
        if(!System.Text.RegularExpressions.Regex.IsMatch(value.RootSha256,"^([0-9A-F]{2}:){31}[0-9A-F]{2}$"))throw new IOException("HOME_NETWORK_FINGERPRINT");
        // Only the two pages the proxy serves for this very address.
        if(value.JoinPage!="http://"+origin.Host+"/"&&value.JoinPage!=origin.GetLeftPart(UriPartial.Authority)+LanDeployment.HelpPath)throw new IOException("HOME_NETWORK_JOIN_PAGE");
        return value;
    }
    // A record for another address (the home network was moved or turned off) or a
    // damaged one only leaves the parent without these hints; it never stops the service.
    internal static InstalledHomeNetwork? TryRead(string path,Uri origin)
    {
        try{return File.Exists(path)?Parse(NativeInstallFiles.ReadRecord(path,4096),origin):null;}
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException){return null;}
    }
}
internal sealed class InstalledLaunchResolver:IServiceStartAuthorizer
{
    // File work and taking the lease must not occupy the SCM entry/control thread.
    // The pointer and both trees are verified before the lease is taken, so an
    // ACTIVE_POINTER_INVALID refusal never leaves the data lease held.
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>Task.Run(()=>
    {
        cancellation.ThrowIfCancellationRequested();
        var state=InstalledLayout.StateRoot;
        var pointer=ActivePointer.ReadVerified(app:true);
        var config=InstalledLaunch.Parse(NativeInstallFiles.ReadRecord(Path.Combine(state,InstalledLaunch.FileName),4096));
        var origin=InstalledLaunch.Origin(config.Origin,config.Port);
        var node=InstalledLayout.Node(InfrastructureRelease.Version);var app=InstalledLayout.AppDirectory(pointer.App.Version);
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
            if(origin.Scheme=="https"&&NetworkPlan.IsPrivateLanV4(origin.Host))
            {
                environment["APP_ADDITIONAL_ORIGINS"]=$"http://127.0.0.1:{config.Port}";
                if(InstalledHomeNetworkRecord.TryRead(Path.Combine(state,InstalledHomeNetworkRecord.FileName),origin) is {} home)
                {environment["APP_JOIN_PAGE"]=home.JoinPage;environment["APP_HOME_CERTIFICATE_SHA256"]=home.RootSha256;}
            }
            var spec=new VerifiedProcessSpec(node,["server/main.mjs"],app,temporary,
                InstalledLayout.PlatformImage(InfrastructureRelease.Version),environment,lease);
            return new AuthorizedServiceStart(new ServiceProcessLauncher(spec,()=>new LocalChildControl()),lease);
        }
        catch{lease.Dispose();throw;}
    },cancellation);
}
