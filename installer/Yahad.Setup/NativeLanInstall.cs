using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed record LanOwnedState(string Phase,string AdapterId,string Address,bool HttpHelp,string RootSha256);
internal sealed record LanRemovalIntent(UninstallMode Mode,LanOwnedState State);

// Production adapter, entered only from the elevated in-process Setup operation.
// Nothing in a plan, test, command line or a web request invokes this class.
internal sealed class NativeLanInstall(InstallerState application)
{
    private static string StateFile=>Path.Combine(LanProxyPaths.State,"owner.json");
    private static string JournalFile=>Path.Combine(LanProxyPaths.State,"web-change.json");
    private static string PreparationFile=>Path.Combine(InstallLocations.State,"lan-preparation.json");
    private static string RemovalFile=>InstallLocations.Binaries+".lan-uninstall.json";
    private static string RootCertificate=>Path.Combine(LanProxyPaths.Data,"pki","authorities","local","root.crt");
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Record(string path,byte[] bytes)=>NativeInstallFiles.Publish(path,bytes);
    // A retry resumes the choice the owner made. The receipt is kept outside the
    // tree it describes, so deleting owner.json cannot erase the removal choice.
    internal static void RequireSameSelection(LanSetupRequest prepared,LanSetupRequest request)
    {if(prepared!=request)throw new IOException("LAN_PREPARATION_CHANGED");}
    internal static void RequireRemovalMode(LanRemovalIntent intent,UninstallMode requested)
    {if(intent.Mode!=requested)throw new IOException("UNINSTALL_MODE_CHANGED");}
    private bool PreparationMatches(LanSetupRequest request,bool requireSameSelection)
    {
        if(!File.Exists(PreparationFile))return false;
        var prepared=NativeJson.Read<LanSetupRequest>(NativeInstallFiles.ReadRecord(PreparationFile,8192));
        if(requireSameSelection)RequireSameSelection(prepared,request);
        return true;
    }
    private static void RemovePreparation()
    {if(File.Exists(PreparationFile))File.Delete(PreparationFile);}
    private LanOwnedState? Read(bool removing=false,bool preparing=false)
    {
        if(File.Exists(RemovalFile))
        {
            if(!removing)throw new IOException("LAN_UNINSTALL_INCOMPLETE");
            return NativeJson.Read<LanRemovalIntent>(NativeInstallFiles.ReadRecord(RemovalFile,1048576)).State;
        }
        if(removing&&!File.Exists(StateFile)&&File.Exists(PreparationFile))
        {
            var pending=NativeJson.Read<LanSetupRequest>(NativeInstallFiles.ReadRecord(PreparationFile,8192));
            if(!NativeService.Exists(ServiceSpec.Lan)&&!Directory.Exists(LanProxyPaths.State))return null;
            return new("preparing",pending.AdapterId!,pending.Address!,pending.HttpHelp,"");
        }
        if(!Directory.Exists(LanProxyPaths.State))return null;
        WindowsFileBoundary.RequireAdministratorOwned(LanProxyPaths.State);
        if(!File.Exists(StateFile)){if(preparing)return null;throw new IOException("LAN_OWNERSHIP_MISSING");}
        return NativeJson.Read<LanOwnedState>(NativeInstallFiles.ReadRecord(StateFile,1048576));
    }
    private LanDeploymentPlan Plan(LanOwnedState state)=>LanDeployment.Create(application.InstallationId,
        [new(state.AdapterId,"selected",state.Address)],state.AdapterId,state.Address,application.Port,
        LanProxyPaths.Caddy(application.Version),LanProxyPaths.Data,LanProxyPaths.PublicJoin,state.HttpHelp);
    private void Save(LanOwnedState state)=>Record(StateFile,Json(state));
    // The caller has stopped the Runtime service: a pending change of the launch
    // file goes back to the bytes it replaced.
    internal void RecoverWebJournal()
    {
        if(!File.Exists(JournalFile))return;
        Record(InstallLocations.LaunchFile,NativeInstallFiles.ReadRecord(JournalFile));File.Delete(JournalFile);
    }
    private void ChangeOrigin(string origin)
    {
        var before=NativeInstallFiles.ReadRecord(InstallLocations.LaunchFile);Record(JournalFile,before);
        Record(InstallLocations.LaunchFile,InstalledLaunch.Encode(InstalledLaunch.Parse(before) with{Origin=origin}));
    }
    private static void CompleteWebChange(){if(File.Exists(JournalFile))File.Delete(JournalFile);}
    private static async Task StartProxyAsync(NativeService proxy,CancellationToken cancellation)
    {using var bound=CancellationTokenSource.CreateLinkedTokenSource(cancellation);bound.CancelAfter(TimeSpan.FromSeconds(50));await proxy.StartAsync(bound.Token);}
    private static async Task StopProxyAsync(NativeService proxy,CancellationToken cancellation)
    {using var bound=CancellationTokenSource.CreateLinkedTokenSource(cancellation);bound.CancelAfter(TimeSpan.FromSeconds(35));await proxy.StopAsync(bound.Token);}
    private static void RequireConfirmedStop(NativeService proxy){var state=proxy.Observed();LanProxyPolicy.RequireConfirmedStopped(state.State,state.Pid,state.Win32,state.Specific);}
    private NativeService OpenProxyForConfiguration()
    {
        if(!NativeService.Exists(ServiceSpec.Lan))return NativeService.Create(application.Version,application.OwnerSid,ServiceSpec.Lan);
        var proxy=NativeService.Open(application.Version,ServiceSpec.Lan);
        try{proxy.ProvisionIfNeverStarted(application.OwnerSid);return proxy;}catch{proxy.Dispose();throw;}
    }
    private static void MakeDirectory(string path,string? acl=null){if(!Directory.Exists(path))NativeInstallFiles.NewDirectory(path,acl);}
    private byte[] PayloadBytes(string relative,int bound=65536)
    {
        var member=application.Files.SingleOrDefault(f=>f.Path=="payload/"+relative)??throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        if(member.Size>bound)throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        var path=Path.Combine(InstallLocations.VersionRoot(application.Version),"payload",relative.Replace('/',Path.DirectorySeparatorChar));
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length!=member.Size)throw new IOException("LAN_PAYLOAD_CHANGED");using var memory=new MemoryStream();file.CopyTo(memory);var bytes=memory.ToArray();
        if(Hash(bytes)!=member.Sha256)throw new IOException("LAN_PAYLOAD_CHANGED");return bytes;
    }
    private string RequirePayload()
    {
        var marker=NativeJson.Read<JsonElement>(PayloadBytes("app/deploy/lan-capabilities.json",4096));
        if(marker.GetProperty("format").GetInt32()!=1||!marker.GetProperty("localHttps").GetBoolean()||
            !marker.GetProperty("exactAdditionalOrigins").GetBoolean()||!marker.GetProperty("ownerLoopbackBootstrap").GetBoolean()||
            marker.GetProperty("publicHelpTemplate").GetString()!="deploy/lan-help.html")throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        return new UTF8Encoding(false,true).GetString(PayloadBytes("app/deploy/lan-help.html"));
    }
    private static byte[] CertificateBytes()
    {
        // The proxy writes this tree, so a link it left there is never followed.
        using var file=WindowsFileBoundary.OpenRead(RootCertificate);
        if(file.Length is <64 or >65536)throw new IOException("LAN_CERTIFICATE_INVALID");return file.Small(65536);
    }
    private static async Task ProbeAsync(LanOwnedState owned,LanPublicRoot root,bool requireApplication,CancellationToken cancellation)
    {
        using var trusted=X509CertificateLoader.LoadCertificate(root.Der);
        using var handler=new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false};
        // Explicit private CA for this one request. No OS/browser trust changes,
        // no global validation bypass, and IP SAN + validity + server EKU checked.
        handler.ServerCertificateCustomValidationCallback=(_,certificate,remoteChain,errors)=>
        {
            if(certificate is null||(errors&SslPolicyErrors.RemoteCertificateNameMismatch)!=0||errors.HasFlag(SslPolicyErrors.RemoteCertificateNotAvailable))return false;
            using var chain=new X509Chain();chain.ChainPolicy.TrustMode=X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(trusted);chain.ChainPolicy.RevocationMode=X509RevocationMode.NoCheck;
            if(remoteChain is not null)foreach(var element in remoteChain.ChainElements.Cast<X509ChainElement>().Skip(1))chain.ChainPolicy.ExtraStore.Add(element.Certificate);
            chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.1"));return chain.Build(certificate);
        };
        using var client=new HttpClient(handler){Timeout=TimeSpan.FromSeconds(5)};
        using var response=await client.GetAsync("https://"+owned.Address+"/",HttpCompletionOption.ResponseHeadersRead,cancellation);
        if(requireApplication&&!response.IsSuccessStatusCode)throw new IOException("LAN_HTTPS_CONNECTION_FAILED");
    }
    private static async Task<LanPublicRoot> WaitForCertificateAsync(LanOwnedState owned,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            if(File.Exists(RootCertificate))
            {
                var root=LanCertificate.ExportPublicRoot(CertificateBytes());
                if(owned.RootSha256!=""&&owned.RootSha256!=root.Sha256Fingerprint)throw new IOException("LAN_CA_CHANGED");
                try{await ProbeAsync(owned,root,false,deadline.Token);return root;}catch(HttpRequestException){}catch(TaskCanceledException)when(!deadline.IsCancellationRequested){}
            }
            await Task.Delay(250,deadline.Token);
        }
    }
    internal void Preflight(LanSetupRequest request)
    {
        if(request.Action==LanSetupAction.Enable)
        {
            RequirePayload();NetworkPlan.LanOrigin(LanSetupRequest.Choices(),request.AdapterId!,request.Address!);
            NativeNetworkProfile.RequirePrivateProfile(request.AdapterId!);
            var resuming=PreparationMatches(request,!File.Exists(StateFile));Read(preparing:resuming);
        }
        else Read();
    }
    internal async Task ConfigureAsync(LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        var resuming=request.Action==LanSetupAction.Enable&&PreparationMatches(request,!File.Exists(StateFile));var owned=Read(preparing:resuming);
        if(request.Action==LanSetupAction.Disable){if(owned is not null)await DisableAsync(owned,cancellation);return;}
        var template=RequirePayload();
        if(owned is null&&!resuming&&NativeService.Exists(ServiceSpec.Lan))throw new IOException("LAN_INSTALL_COLLISION");
        if(owned is null&&!resuming)Record(PreparationFile,Json(request));
        using var proxy=OpenProxyForConfiguration();
        var sid=InstallLocations.ServiceSidFor(LanProxyPaths.ServiceName);
        await StopProxyAsync(proxy,cancellation);RequireConfirmedStop(proxy);proxy.DisableStartup();
        if(owned is not null)NativeLanFirewall.Remove(Plan(owned).FirewallRules);
        MakeDirectory(LanProxyPaths.State,NativeInstallFiles.InstallAcl(sid));
        MakeDirectory(LanProxyPaths.Data,NativeInstallFiles.DataAcl(sid));MakeDirectory(LanProxyPaths.Temporary,NativeInstallFiles.DataAcl(sid));MakeDirectory(LanProxyPaths.PublicJoin);
        var next=new LanOwnedState("preparing",request.AdapterId!,request.Address!,request.HttpHelp,owned?.RootSha256??"");
        var plan=Plan(next);Save(next);
        try
        {
            // Caddy may resume its own unpublished first initialization. Once
            // any root was shown/exported, never silently replace that authority.
            if(Directory.EnumerateFileSystemEntries(LanProxyPaths.Data).Any()&&!File.Exists(RootCertificate)&&
                (next.RootSha256!=""||File.Exists(Path.Combine(LanProxyPaths.PublicJoin,"certificate.crt"))))throw new IOException("LAN_CA_STORAGE_DAMAGED");
            Record(LanProxyPaths.Config,Encoding.UTF8.GetBytes(plan.Caddyfile));
            Record(LanProxyPaths.Receipt,Json(new LanProxyReceipt(next.AdapterId,next.Address)));
            // Only public help is on HTTP; no token, login, family or app route.
            Record(Path.Combine(LanProxyPaths.PublicJoin,"index.html"),Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><p>הכנת החיבור עדיין לא הושלמה. חזרו למסך המחשב המארח.</p>"));
            NativeLanFirewall.RequireNoDrift(plan.FirewallRules);progress(new("preparing_home_certificate",82));
            await StartProxyAsync(proxy,cancellation);
            var root=await WaitForCertificateAsync(next,cancellation);await StopProxyAsync(proxy,cancellation);RequireConfirmedStop(proxy);
            // Export a newly encoded public DER, never original PEM/file bytes.
            Record(Path.Combine(LanProxyPaths.PublicJoin,"certificate.crt"),root.Der);
            if(!template.Contains("__LAN_HTTPS_ORIGIN__",StringComparison.Ordinal)||!template.Contains("__ROOT_SHA256__",StringComparison.Ordinal))throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
            var page=template.Replace("__LAN_HTTPS_ORIGIN__",WebUtility.HtmlEncode(plan.Origin),StringComparison.Ordinal)
                .Replace("__ROOT_SHA256__",WebUtility.HtmlEncode(root.Sha256Fingerprint),StringComparison.Ordinal);
            Record(Path.Combine(LanProxyPaths.PublicJoin,"index.html"),Encoding.UTF8.GetBytes(page));
            next=next with{RootSha256=root.Sha256Fingerprint};Save(next);
            ChangeOrigin(plan.Origin);
            LanDeployment.RequireSelectedAdapter(plan,LanSetupRequest.Choices());NativeNetworkProfile.RequirePrivateProfile(next.AdapterId);
            NativeLanFirewall.Add(plan.FirewallRules);proxy.EnableStartup();await StartProxyAsync(proxy,cancellation);
            Save(next with{Phase="enabled"});CompleteWebChange();RemovePreparation();
        }
        catch(Exception failure)
        {
            // Preserve CA and family state on failure. Never fall back to HTTP
            // or a broad bind. Failure to stop/remove/restore remains a failure.
            try
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(45));
                proxy.DisableStartup();await StopProxyAsync(proxy,cleanup.Token);RequireConfirmedStop(proxy);
                NativeLanFirewall.Remove(plan.FirewallRules);RecoverWebJournal();Save(next with{Phase="paused"});RemovePreparation();
            }
            catch(Exception incomplete){throw SetupFailureDetail.Besides(failure,incomplete);}
            throw;
        }
    }
    private async Task DisableAsync(LanOwnedState owned,CancellationToken cancellation)
    {
        await StopProxyIfPresentAsync(cancellation);
        NativeLanFirewall.Remove(Plan(owned).FirewallRules);ChangeOrigin("http://127.0.0.1:"+application.Port);Save(owned with{Phase="disabled"});CompleteWebChange();RemovePreparation();
    }
    private async Task StopProxyIfPresentAsync(CancellationToken cancellation)
    {
        if(!NativeService.Exists(ServiceSpec.Lan))return;
        using var proxy=NativeService.Open(application.Version,ServiceSpec.Lan);proxy.DisableStartup();await StopProxyAsync(proxy,cancellation);RequireConfirmedStop(proxy);
    }
    // An upgrade moves the proxy with the release: stopped, then its image and its
    // firewall rule, and started again only if it should be running.
    internal static Task StopForUpgradeAsync(NativeService proxy)=>StopProxyAsync(proxy,CancellationToken.None);
    internal void Move(NativeService proxy,string? otherVersion){proxy.SwitchImage(application.Version);MoveFirewall(otherVersion);}
    internal void MoveFirewall(string? otherVersion)
    {
        var owned=Read(removing:true,preparing:true);if(owned is null)return;
        NativeLanFirewall.Move(Plan(owned).FirewallRules,otherVersion is null?null:LanProxyPaths.Caddy(otherVersion));
    }
    private static bool ShouldRun(NativeService proxy)
    {
        var receipt=File.Exists(LanProxyPaths.Receipt)?NativeJson.Read<LanProxyReceipt>(NativeInstallFiles.ReadRecord(LanProxyPaths.Receipt,4096)):null;
        return LanProxyPolicy.ShouldRun(proxy.StartType(),receipt,LanProxyPolicy.Addresses());
    }
    // Serving TLS under the family's own authority, not merely a running process.
    internal async Task StartAfterUpgradeAsync(NativeService proxy)
    {
        if(!ShouldRun(proxy))return;
        await StartProxyAsync(proxy,CancellationToken.None);
        if(Read(removing:true,preparing:true) is {} owned)await WaitForCertificateAsync(owned,CancellationToken.None);
    }
    // Back on the release it ran from, or on a repair: a proxy that cannot serve says
    // nothing about the Runtime, must not hold up its recovery, and may be retried.
    internal static async Task<bool> TryRestartAsync(NativeService proxy)
    {
        if(proxy.IsRunning()||!ShouldRun(proxy))return false;
        try{await StartProxyAsync(proxy,CancellationToken.None);return true;}
        catch(Exception failure)when(failure is OperationCanceledException or Win32Exception or IOException{Message:"SERVICE_START_FAILED"}){return false;}
    }
    internal async Task<LanSetupResult?> ObserveAsync(CancellationToken cancellation)
    {
        var owned=Read();if(owned is null||owned.Phase=="disabled")return null;
        if(owned.Phase!="enabled"||File.Exists(JournalFile))throw new IOException("LAN_SETUP_PAUSED");
        var root=LanCertificate.ExportPublicRoot(CertificateBytes());if(root.Sha256Fingerprint!=owned.RootSha256)throw new IOException("LAN_CA_CHANGED");
        if(root.Der.Length>12000)throw new IOException("LAN_CERTIFICATE_INVALID");
        var plan=Plan(owned);LanDeployment.RequireSelectedAdapter(plan,LanSetupRequest.Choices());
        NativeNetworkProfile.RequirePrivateProfile(owned.AdapterId);NativeLanFirewall.RequirePresentAndMatching(plan.FirewallRules);
        if(!NativeService.Exists(ServiceSpec.Lan))throw new IOException("LAN_SETUP_PAUSED");
        using var proxy=NativeService.Open(application.Version,ServiceSpec.Lan);await StartProxyAsync(proxy,cancellation);
        await ProbeAsync(owned,root,true,cancellation);
        return new(plan.Origin,plan.HelpOrigin,root.Sha256Fingerprint,Convert.ToBase64String(root.Der));
    }
    internal async Task UninstallAsync(UninstallMode mode,CancellationToken cancellation)
    {
        var owned=Read(removing:true);if(owned is null){RemovePreparation();return;}
        if(File.Exists(RemovalFile))RequireRemovalMode(NativeJson.Read<LanRemovalIntent>(NativeInstallFiles.ReadRecord(RemovalFile,1048576)),mode);
        else Record(RemovalFile,Json(new LanRemovalIntent(mode,owned)));
        // A partially removed state may no longer contain owner.json or the CA.
        // The external receipt above remains until both removable roots are gone.
        if(Directory.Exists(LanProxyPaths.State)&&File.Exists(StateFile))await DisableAsync(owned,cancellation);
        else
        {
            await StopProxyIfPresentAsync(cancellation);
            NativeLanFirewall.Remove(Plan(owned).FirewallRules);
            if(mode==UninstallMode.KeepData&&Directory.Exists(LanProxyPaths.State))Save(owned with{Phase="disabled"});
        }
        if(NativeService.Exists(ServiceSpec.Lan))
        {
            using(var proxy=NativeService.Open(application.Version,ServiceSpec.Lan)){RequireConfirmedStop(proxy);proxy.Delete();}
            using var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation);wait.CancelAfter(TimeSpan.FromSeconds(10));
            while(NativeService.Exists(ServiceSpec.Lan))await Task.Delay(100,wait.Token);
        }
        // Public roots/CA state survive data-preserving uninstall.
        if(mode==UninstallMode.DeleteData&&Directory.Exists(LanProxyPaths.State))
        {
            var tree=RemovalTree.Inspect(LanProxyPaths.State,WindowsFileBoundary.RequireNoLink);
            tree.DeleteContents(WindowsFileBoundary.DeleteNoFollow,_=>{},cancellation);
            WindowsFileBoundary.DeleteNoFollow(LanProxyPaths.State,true);
        }
        File.Delete(RemovalFile);
        RemovePreparation();
    }
}
