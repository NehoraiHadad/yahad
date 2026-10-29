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

internal sealed record LanOwnedFile(string Path,long Size,string Sha256);
internal sealed record LanOwnedState(int Format,string InstallationId,string OwnerSid,string ServiceSid,string Phase,
    string AdapterId,string Address,int AppPort,bool HttpHelp,string RootSha256,LanFirewallRule[] Rules,LanOwnedFile[] Files);
internal sealed record LanPreparation(int Format,string InstallationId,string OwnerSid,string PayloadDigest,LanSetupRequest Request,string? ServiceSid=null);
internal sealed record LanRemovalIntent(int Format,UninstallMode Mode,LanOwnedState State);

// Production adapter, entered only from the authenticated elevated installer.
// Nothing in a plan, test, command line or a web request invokes this class.
internal sealed class NativeLanInstall(InstallerState application)
{
    private static string StateFile=>Path.Combine(LanProxyPaths.State,"owner.json");
    private static string JournalFile=>Path.Combine(LanProxyPaths.State,"web-change.json");
    private static string PreparationFile=>Path.Combine(InstallLocations.State,"lan-preparation.json");
    private static string RemovalFile=>LanProxyPaths.Binaries+".uninstall.json";
    private static string RootCertificate=>Path.Combine(LanProxyPaths.Data,"pki","authorities","local","root.crt");
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Record(string path,byte[] bytes)
    {if(File.Exists(path))NativeInstallFiles.ReplaceOwned(path,bytes);else NativeInstallFiles.WriteNew(path,bytes);}
    private bool PreparationMatches(LanSetupRequest request,bool requireSameSelection)
    {
        if(!File.Exists(PreparationFile))return false;
        using var file=WindowsFileBoundary.OpenInstallerMetadata(PreparationFile);var intent=NativeJson.Read<LanPreparation>(file.Small(8192));
        LanRecoveryPolicy.RequirePreparation(intent,application,requireSameSelection?request:null);return true;
    }
    private static void RemovePreparation()
    {if(File.Exists(PreparationFile)){using(var pin=WindowsFileBoundary.OpenInstallerMetadata(PreparationFile)){}File.Delete(PreparationFile);}}
    private LanOwnedState? Read(bool removing=false,bool preparing=false)
    {
        LanOwnedState owned;
        if(File.Exists(RemovalFile))
        {
            if(!removing)throw new IOException("LAN_UNINSTALL_INCOMPLETE");
            using var removal=WindowsFileBoundary.OpenInstallerMetadata(RemovalFile);var intent=NativeJson.Read<LanRemovalIntent>(removal.Small(1048576));
            LanRecoveryPolicy.RequireRemoval(intent,application);owned=intent.State;
        }
        else
        {
            if(removing&&!File.Exists(StateFile)&&File.Exists(PreparationFile))
            {
                using var prep=WindowsFileBoundary.OpenInstallerMetadata(PreparationFile);var pending=NativeJson.Read<LanPreparation>(prep.Small(8192));
                LanRecoveryPolicy.RequirePreparation(pending,application);
                if(!LanServiceRegistration.Exists()&&!Directory.Exists(LanProxyPaths.State)&&!Directory.Exists(LanProxyPaths.Binaries))return null;
                var sid=pending.ServiceSid;
                if(LanServiceRegistration.Exists()){using var registration=LanServiceRegistration.OpenOwned();sid=LanProxyPaths.ServiceSid();}
                if(sid is null)throw new IOException("LAN_OWNERSHIP_MISSING");
                owned=new(1,application.InstallationId,application.OwnerSid,sid,"preparing",pending.Request.AdapterId!,pending.Request.Address!,application.Port,pending.Request.HttpHelp,"",[],ExpectedBinaries());
                owned=owned with{Rules=Plan(owned).FirewallRules.ToArray()};
            }
            else
            {
                if(!Directory.Exists(LanProxyPaths.State))return null;
                if(!File.Exists(StateFile)){if(preparing)return null;throw new IOException("LAN_OWNERSHIP_MISSING");}
                using var file=WindowsFileBoundary.OpenInstallerMetadata(StateFile);owned=NativeJson.Read<LanOwnedState>(file.Small(1048576));
            }
        }
        if(owned.Format!=1||owned.InstallationId!=application.InstallationId||owned.OwnerSid!=application.OwnerSid||
            owned.ServiceSid==application.ServiceSid||!owned.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||
            owned.AppPort!=application.Port||owned.Phase is not("preparing" or "enabled" or "paused" or "disabled")||
            !NetworkPlan.IsPrivateLanV4(owned.Address)||owned.Files.Length>50000)throw new IOException("LAN_OWNERSHIP_CHANGED");
        var plan=Plan(owned);if(!owned.Rules.SequenceEqual(plan.FirewallRules))throw new IOException("LAN_FIREWALL_RECEIPT_CHANGED");
        foreach(var member in owned.Files){InstallPackage.Name(member.Path);if(member.Size<0||member.Sha256.Length!=64)throw new IOException("LAN_OWNERSHIP_CHANGED");}
        return owned;
    }
    private static LanDeploymentPlan Plan(LanOwnedState state)=>LanDeployment.Create(state.InstallationId,
        [new(state.AdapterId,"selected",state.Address)],state.AdapterId,state.Address,state.AppPort,
        LanProxyPaths.Caddy,LanProxyPaths.Data,LanProxyPaths.PublicJoin,state.HttpHelp);
    private void Save(LanOwnedState state)=>Record(StateFile,Json(state));
    private byte[] ReadApp(string name)=>NativeInstallFiles.ReadOwned(Path.Combine(InstallLocations.State,name),application.ServiceSid);
    private void WriteAppSnapshot(LanWebSnapshot snapshot)
    {var parts=LanWebConfiguration.Parts(snapshot);for(var i=0;i<parts.Length;i++)NativeInstallFiles.ReplaceOwned(Path.Combine(InstallLocations.State,LanWebConfiguration.Names[i]),parts[i]);}
    private void RecoverWebJournal()
    {
        if(!File.Exists(JournalFile))return;
        using var file=WindowsFileBoundary.OpenInstallerMetadata(JournalFile);var journal=NativeJson.Read<LanWebJournal>(file.Small(131072));
        LanWebConfiguration.RequireRecoverable(journal,application,ReadApp);WriteAppSnapshot(journal.Before);
        file.Dispose();File.Delete(JournalFile);
    }
    private void ChangeOrigin(string origin)
    {
        RecoverWebJournal();var before=LanWebConfiguration.Read(ReadApp);var after=LanWebConfiguration.WithOrigin(before,application,origin);
        Record(JournalFile,Json(new LanWebJournal(1,application.InstallationId,before,after)));WriteAppSnapshot(after);
    }
    private static void CompleteWebChange(){if(File.Exists(JournalFile)){using(var pin=WindowsFileBoundary.OpenInstallerMetadata(JournalFile)){}File.Delete(JournalFile);}}
    private static void MakeDirectory(string path,string descriptor,string serviceSid)
    {
        if(!Directory.Exists(path))NativeInstallFiles.NewDirectory(path,descriptor);
        else if(path==LanProxyPaths.Data||path==LanProxyPaths.Temporary)WindowsFileBoundary.ValidateRemovalMember(path,true,new System.Security.Principal.SecurityIdentifier(serviceSid));
        else{using var pin=WindowsFileBoundary.PinInstallerRemovalRoot(path,new System.Security.Principal.SecurityIdentifier(serviceSid));}
    }
    private byte[] PayloadBytes(string relative,int bound=65536)
    {
        var member=application.Files.SingleOrDefault(f=>f.Path=="payload/"+relative)??throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        if(member.Size>bound)throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        var path=Path.Combine(InstallLocations.State,"payloads",application.PayloadDigest,relative.Replace('/',Path.DirectorySeparatorChar));
        SafeDirectory.NoReparseAncestors(path);using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
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
    private LanOwnedFile[] ExpectedBinaries()=>application.Files.Where(f=>f.Path.StartsWith("host/",StringComparison.Ordinal))
        .Select(f=>new LanOwnedFile(f.Path,f.Size,f.Sha256)).Append(CaddyMember()).ToArray();
    private LanOwnedFile CaddyMember()
    {
        var member=application.Files.SingleOrDefault(f=>f.Path=="payload/runtime/caddy/caddy.exe")??throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
        return new("caddy.exe",member.Size,member.Sha256);
    }
    private void PublishBinaries(LanOwnedState owned)
    {
        var descriptor=NativeInstallFiles.Descriptor(owned.ServiceSid);
        foreach(var member in owned.Files)
        {
            var target=Path.Combine(LanProxyPaths.Binaries,member.Path.Replace('/',Path.DirectorySeparatorChar));
            NativeInstallFiles.EnsureStageDirectory(LanProxyPaths.Binaries,Path.GetDirectoryName(target)!,descriptor);
            if(File.Exists(target)){VerifyBinary(target,member);continue;}
            var source=member.Path=="caddy.exe"?Path.Combine(InstallLocations.State,"payloads",application.PayloadDigest,"runtime","caddy","caddy.exe"):
                Path.Combine(InstallLocations.Binaries,member.Path.Replace('/',Path.DirectorySeparatorChar));
            SafeDirectory.NoReparseAncestors(source);using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(input.Length!=member.Size||Convert.ToHexStringLower(SHA256.HashData(input))!=member.Sha256)throw new IOException("LAN_PAYLOAD_CHANGED");
            input.Position=0;using(var output=NativeInstallFiles.CreateNewFile(target)){input.CopyTo(output);output.Flush(true);}VerifyBinary(target,member);
        }
    }
    private static void VerifyBinary(string path,LanOwnedFile member)
    {
        SafeDirectory.NoReparseAncestors(path);using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=member.Size||Convert.ToHexStringLower(SHA256.HashData(input))!=member.Sha256)throw new IOException("LAN_BINARY_CHANGED");
    }
    private static byte[] CertificateBytes()
    {
        SafeDirectory.NoReparseAncestors(RootCertificate);using var file=new FileStream(RootCertificate,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length is <64 or >65536)throw new IOException("LAN_CERTIFICATE_INVALID");using var output=new MemoryStream();file.CopyTo(output);return output.ToArray();
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
        NativeInstallFiles.RequireAdministrator();request.Require();
        if(request.Action==LanSetupAction.Enable)
        {
            RequirePayload();NetworkPlan.LanOrigin(LanSetupRequest.Choices(),request.AdapterId!,request.Address!);
            NativeLanFirewall.RequirePrivateProfile(request.AdapterId!);
            var resuming=PreparationMatches(request,!File.Exists(StateFile));Read(preparing:resuming);
        }
        else Read();
    }
    internal async Task ConfigureAsync(LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        NativeInstallFiles.RequireAdministrator();request.Require();
        var resuming=request.Action==LanSetupAction.Enable&&PreparationMatches(request,!File.Exists(StateFile));var owned=Read(preparing:resuming);
        if(request.Action==LanSetupAction.KeepCurrent)return;
        if(request.Action==LanSetupAction.Disable){if(owned is not null)await DisableAsync(owned,cancellation);return;}
        var template=RequirePayload();var choices=LanSetupRequest.Choices();NetworkPlan.LanOrigin(choices,request.AdapterId!,request.Address!);
        NativeLanFirewall.RequirePrivateProfile(request.AdapterId!);
        if(owned is null&&!resuming&&(Directory.Exists(LanProxyPaths.Binaries)||File.Exists(LanProxyPaths.Binaries)||LanServiceRegistration.Exists()))throw new IOException("LAN_INSTALL_COLLISION");
        if(owned is null&&!resuming)Record(PreparationFile,Json(new LanPreparation(1,application.InstallationId,application.OwnerSid,application.PayloadDigest,request)));
        if(owned is not null)RecoverWebJournal();
        using var registration=LanServiceRegistration.Exists()?LanServiceRegistration.OpenOwned():LanServiceRegistration.Create(application.OwnerSid);
        var sid=LanProxyPaths.ServiceSid();if(owned is not null&&owned.ServiceSid!=sid)throw new IOException("LAN_OWNERSHIP_CHANGED");
        if(owned is null)Record(PreparationFile,Json(new LanPreparation(1,application.InstallationId,application.OwnerSid,application.PayloadDigest,request,sid)));
        await registration.StopAsync(cancellation);registration.RequireConfirmedStop();registration.DisableAutomaticStart();
        if(owned is not null)NativeLanFirewall.Remove(owned.Rules);
        var immutable=NativeInstallFiles.Descriptor(sid);MakeDirectory(LanProxyPaths.State,immutable,sid);MakeDirectory(LanProxyPaths.Binaries,immutable,sid);
        MakeDirectory(LanProxyPaths.Data,NativeInstallFiles.Descriptor(sid,true),sid);MakeDirectory(LanProxyPaths.Temporary,NativeInstallFiles.Descriptor(sid,true),sid);MakeDirectory(LanProxyPaths.PublicJoin,immutable,sid);
        var next=new LanOwnedState(1,application.InstallationId,application.OwnerSid,sid,"preparing",request.AdapterId!,request.Address!,application.Port,request.HttpHelp,owned?.RootSha256??"",[],ExpectedBinaries());
        var plan=Plan(next);next=next with{Rules=plan.FirewallRules.ToArray()};Save(next);
        try
        {
            PublishBinaries(next);
            // Caddy may resume its own unpublished first initialization. Once
            // any root was shown/exported, never silently replace that authority.
            if(Directory.EnumerateFileSystemEntries(LanProxyPaths.Data).Any()&&!File.Exists(RootCertificate)&&
                (next.RootSha256!=""||File.Exists(Path.Combine(LanProxyPaths.PublicJoin,"certificate.crt"))))throw new IOException("LAN_CA_STORAGE_DAMAGED");
            Record(LanProxyPaths.Config,Encoding.UTF8.GetBytes(plan.Caddyfile));
            Record(LanProxyPaths.Receipt,Json(new LanProxyReceipt(1,application.InstallationId,sid,next.AdapterId,next.Address,next.AppPort,CaddyMember().Sha256,Hash(Encoding.UTF8.GetBytes(plan.Caddyfile)))));
            // Only public help is on HTTP; no token, login, family or app route.
            Record(Path.Combine(LanProxyPaths.PublicJoin,"index.html"),Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><p>הכנת החיבור עדיין לא הושלמה. חזרו למסך המחשב המארח.</p>"));
            NativeLanFirewall.RequireNoDrift(next.Rules);progress(new("preparing_home_certificate",82));
            await registration.StartAsync(_=>{},cancellation);
            var root=await WaitForCertificateAsync(next,cancellation);await registration.StopAsync(cancellation);registration.RequireConfirmedStop();
            // Export a newly encoded public DER, never original PEM/file bytes.
            Record(Path.Combine(LanProxyPaths.PublicJoin,"certificate.crt"),root.Der);
            if(!template.Contains("__LAN_HTTPS_ORIGIN__",StringComparison.Ordinal)||!template.Contains("__ROOT_SHA256__",StringComparison.Ordinal))throw new IOException("LAN_PAYLOAD_NOT_SUPPORTED");
            var page=template.Replace("__LAN_HTTPS_ORIGIN__",WebUtility.HtmlEncode(plan.Origin),StringComparison.Ordinal)
                .Replace("__ROOT_SHA256__",WebUtility.HtmlEncode(root.Sha256Fingerprint),StringComparison.Ordinal);
            Record(Path.Combine(LanProxyPaths.PublicJoin,"index.html"),Encoding.UTF8.GetBytes(page));
            next=next with{RootSha256=root.Sha256Fingerprint};Save(next);
            ChangeOrigin(plan.Origin);
            LanDeployment.RequireSelectedAdapter(plan,LanSetupRequest.Choices());NativeLanFirewall.RequirePrivateProfile(next.AdapterId);
            NativeLanFirewall.Add(next.Rules);registration.EnableAutomaticStart();await registration.StartAsync(_=>{},cancellation);
            Save(next with{Phase="enabled"});CompleteWebChange();RemovePreparation();
        }
        catch
        {
            // Preserve CA and family state on failure. Never fall back to HTTP
            // or a broad bind. Failure to stop/remove/restore remains a failure.
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(45));
            registration.DisableAutomaticStart();await registration.StopAsync(cleanup.Token);registration.RequireConfirmedStop();
            NativeLanFirewall.Remove(next.Rules);RecoverWebJournal();Save(next with{Phase="paused"});RemovePreparation();throw;
        }
    }
    private async Task DisableAsync(LanOwnedState owned,CancellationToken cancellation)
    {
        if(LanServiceRegistration.Exists())
        {using var service=LanServiceRegistration.OpenOwned();service.DisableAutomaticStart();await service.StopAsync(cancellation);service.RequireConfirmedStop();}
        NativeLanFirewall.Remove(owned.Rules);ChangeOrigin("http://127.0.0.1:"+application.Port);Save(owned with{Phase="disabled"});CompleteWebChange();RemovePreparation();
    }
    internal async Task<LanSetupResult?> ObserveAsync(CancellationToken cancellation)
    {
        var owned=Read();if(owned is null||owned.Phase=="disabled")return null;
        if(owned.Phase!="enabled"||File.Exists(JournalFile))throw new IOException("LAN_SETUP_PAUSED");
        var root=LanCertificate.ExportPublicRoot(CertificateBytes());if(root.Sha256Fingerprint!=owned.RootSha256)throw new IOException("LAN_CA_CHANGED");
        if(root.Der.Length>12000)throw new IOException("LAN_CERTIFICATE_INVALID");
        var plan=Plan(owned);LanDeployment.RequireSelectedAdapter(plan,LanSetupRequest.Choices());
        NativeLanFirewall.RequirePrivateProfile(owned.AdapterId);NativeLanFirewall.RequirePresentAndMatching(owned.Rules);
        if(!LanServiceRegistration.Exists())throw new IOException("LAN_SETUP_PAUSED");
        using var registration=LanServiceRegistration.OpenOwned();await registration.StartAsync(_=>{},cancellation);
        await ProbeAsync(owned,root,true,cancellation);
        return new(plan.Origin,plan.HelpOrigin,root.Sha256Fingerprint,true,CertificateDerBase64:Convert.ToBase64String(root.Der));
    }
    internal async Task<LanSetupResult?> UninstallAsync(UninstallMode mode,CancellationToken cancellation)
    {
        var owned=Read(removing:true);if(owned is null){RemovePreparation();return null;}
        if(File.Exists(RemovalFile))
        {using var pin=WindowsFileBoundary.OpenInstallerMetadata(RemovalFile);var intent=NativeJson.Read<LanRemovalIntent>(pin.Small(1048576));LanRecoveryPolicy.RequireRemoval(intent,application,mode);}
        else
        {
            Record(RemovalFile,Json(new LanRemovalIntent(1,mode,owned)));
            NativeInstallFiles.SetOwnedFile(RemovalFile,NativeInstallFiles.Descriptor(owned.ServiceSid));
        }
        // A partially removed state may no longer contain owner.json or the CA.
        // The external receipt above remains until both removable roots are gone.
        if(Directory.Exists(LanProxyPaths.State)&&File.Exists(StateFile))await DisableAsync(owned,cancellation);
        else
        {
            if(LanServiceRegistration.Exists()){using var service=LanServiceRegistration.OpenOwned();service.DisableAutomaticStart();await service.StopAsync(cancellation);service.RequireConfirmedStop();}
            NativeLanFirewall.Remove(owned.Rules);
            if(mode==UninstallMode.KeepData&&Directory.Exists(LanProxyPaths.State))Save(owned with{Phase="disabled"});
        }
        var result=new LanSetupResult("https://"+owned.Address,owned.HttpHelp?"http://"+owned.Address:null,owned.RootSha256,false);
        if(LanServiceRegistration.Exists())
        {using(var service=LanServiceRegistration.OpenOwned()){service.RequireConfirmedStop();service.Delete();}await LanServiceRegistration.WaitUntilRemovedAsync(cancellation);}
        // Refuse unknown/modified binaries rather than deleting a coincidentally
        // named installation. Public roots/CA state survive data-preserving uninstall.
        if(Directory.Exists(LanProxyPaths.Binaries))
        {
            var expected=owned.Files.ToDictionary(f=>Path.Combine(LanProxyPaths.Binaries,f.Path.Replace('/',Path.DirectorySeparatorChar)),StringComparer.OrdinalIgnoreCase);
            var tree=RemovalTree.Inspect(LanProxyPaths.Binaries,(path,directory)=>
            {if(!directory){if(!expected.TryGetValue(path,out var member))throw new IOException("LAN_BINARY_CHANGED");VerifyBinary(path,member);}});
            tree.DeleteContents((path,directory)=>{SafeDirectory.NoReparseAncestors(path);if(directory)Directory.Delete(path);else File.Delete(path);},_=>{},cancellation);
            Directory.Delete(LanProxyPaths.Binaries);
        }
        if(mode==UninstallMode.DeleteData&&Directory.Exists(LanProxyPaths.State))
        {
            var sid=new System.Security.Principal.SecurityIdentifier(owned.ServiceSid);
            using(var pin=WindowsFileBoundary.PinInstallerRemovalRoot(LanProxyPaths.State,sid))
            {
                var tree=RemovalTree.Inspect(LanProxyPaths.State,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,sid));
                tree.DeleteContents((path,directory)=>WindowsFileBoundary.DeleteRemovalMember(path,directory,sid),_=>{},cancellation);
            }
            WindowsFileBoundary.DeleteRemovalMember(LanProxyPaths.State,true,sid);
        }
        using(var pin=WindowsFileBoundary.OpenInstallerMetadata(RemovalFile)){}File.Delete(RemovalFile);
        RemovePreparation();
        return result;
    }
}
