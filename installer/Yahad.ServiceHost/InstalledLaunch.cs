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
        // JSON null passes the strict reader for a non-nullable string.
        if(value.JoinPage is null||value.RootSha256 is null)throw new IOException("HOME_NETWORK_FIELDS");
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
        catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or ArgumentException or System.ComponentModel.Win32Exception){return null;}
    }
}
// The home name the Runtime may serve as its origin (docs/HOME-NAME.md 3). Setup writes it
// last, once the proxy serves the name; its presence is the commit point of the name path.
// A separate file, so installed.json keeps the format older hosts read; an older host
// ignores it and runs on the IP origin.
internal sealed record InstalledHomeName(int Format,string Name,string Address);
internal static class InstalledHomeNameRecord
{
    internal const string FileName="home-name.json",CapabilitiesFile="deploy/lan-capabilities.json";
    internal static byte[] Encode(InstalledHomeName value)=>System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(Require(value),ServiceHostJsonContext.Default.InstalledHomeName);
    internal static InstalledHomeName Parse(byte[] bytes)
    {
        if(bytes.Length is <1 or >4096)throw new IOException("HOME_NAME_BOUND");
        return Require(NativeJson.Read(bytes,ServiceHostJsonContext.Default.InstalledHomeName));
    }
    private static InstalledHomeName Require(InstalledHomeName value)
    {
        if(value.Format!=1)throw new IOException("HOME_NAME_FORMAT");
        // JSON null passes the strict reader for a non-nullable string.
        if(!HomeNameRelease.IsName(value.Name)||value.Address is null||!NetworkPlan.IsPrivateLanV4(value.Address))throw new IOException("HOME_NAME_FIELDS");
        return value;
    }
    // Whether the app tree declares it serves a name origin: "homeName": true, nothing less.
    internal static bool Declared(byte[] capabilities)
    {
        if(capabilities.Length is <1 or >4096)return false;
        using var document=System.Text.Json.JsonDocument.Parse(capabilities,new System.Text.Json.JsonDocumentOptions{MaxDepth=4,AllowDuplicateProperties=false});
        return document.RootElement.ValueKind==System.Text.Json.JsonValueKind.Object&&document.RootElement.TryGetProperty("homeName",out var value)&&value.ValueKind==System.Text.Json.JsonValueKind.True;
    }
    /// <summary>
    /// The name, only when all hold: the origin is private-IP HTTPS, this build has a zone, the record is valid
    /// and names that very address, and the active app declares "homeName": true. Anything unreadable or
    /// unparsable is the IP origin exactly as before, never a refused start.
    /// </summary>
    internal static InstalledHomeName? Select(Uri origin,Func<byte[]?> record,Func<byte[]?> capabilities)
    {
        if(origin.Scheme!="https"||!origin.IsDefaultPort||!NetworkPlan.IsPrivateLanV4(origin.Host)||!HomeNameRelease.Configured)return null;
        try
        {
            if(record() is not {} bytes||Parse(bytes) is not {} name||name.Address!=origin.Host)return null;
            return capabilities() is {} declared&&Declared(declared)?name:null;
        }
        catch(Exception error)when(error is not OutOfMemoryException){return null;}
    }
    internal static byte[]? TryReadFile(string path,int maximum)
    {
        try{return File.Exists(path)?NativeInstallFiles.ReadRecord(path,maximum):null;}
        catch(Exception error)when(error is not OutOfMemoryException){return null;}
    }
}
internal static class InstalledLanEnvironment
{
    // LAN HTTPS keeps Secure cookies. Only the fixed local loopback origin is added for
    // bootstrap; remote origins are never inferred here. With the home name, the name is the
    // origin and the address it stands for stays admitted, and it is the home address the
    // app checks the join page and fingerprint against.
    internal static void Apply(Dictionary<string,string> environment,Uri origin,int port,InstalledHomeNetwork? home,InstalledHomeName? name)
    {
        var loopback=$"http://127.0.0.1:{port.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        environment["APP_ADDITIONAL_ORIGINS"]=loopback;
        if(home is not null){environment["APP_JOIN_PAGE"]=home.JoinPage;environment["APP_HOME_CERTIFICATE_SHA256"]=home.RootSha256;}
        if(name is null)return;
        var address="https://"+origin.Host;
        environment["APP_ORIGIN"]="https://"+name.Name;environment["APP_ADDITIONAL_ORIGINS"]=address+","+loopback;environment["APP_HOME_ADDRESS"]=address;
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
            if(origin.Scheme=="https"&&NetworkPlan.IsPrivateLanV4(origin.Host))
            {
                // The capabilities come from the app tree verified above, never from a path a record names.
                var name=InstalledHomeNameRecord.Select(origin,()=>InstalledHomeNameRecord.TryReadFile(Path.Combine(state,InstalledHomeNameRecord.FileName),4096),
                    ()=>InstalledHomeNameRecord.TryReadFile(Path.Combine(app,InstalledHomeNameRecord.CapabilitiesFile.Replace('/',Path.DirectorySeparatorChar)),4096));
                InstalledLanEnvironment.Apply(environment,origin,config.Port,InstalledHomeNetworkRecord.TryRead(Path.Combine(state,InstalledHomeNetworkRecord.FileName),origin),name);
            }
            var spec=new VerifiedProcessSpec(node,["server/main.mjs"],app,temporary,
                InstalledLayout.PlatformImage(InfrastructureRelease.Version),environment,lease);
            return new AuthorizedServiceStart(new ServiceProcessLauncher(spec,()=>new LocalChildControl()),lease);
        }
        catch{lease.Dispose();throw;}
    },cancellation);
}
