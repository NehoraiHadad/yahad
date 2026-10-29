using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal sealed class NativeSetupHost(InstallPackage package):ISetupHostOperations,ILanSetupHostOperations,IDisposable
{
    private InstallerState? state;private NativeServiceInstall? service;private string? staging,setupToken;private bool existing,reinstalling;private WindowsFileBoundary.DataLease? preservedLease;private bool rootCreated,binariesCreated;
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static string ServiceSid()=>((SecurityIdentifier)new NTAccount("NT SERVICE",InstallLocations.ServiceName).Translate(typeof(SecurityIdentifier))).Value;
    private static InstallerState ReadState(string? selected=null)
    {
        using var file=WindowsFileBoundary.OpenInstallerMetadata(selected??InstallLocations.StateFile);var state=NativeJson.Read<InstallerState>(file.Small(16777216));
        if(state.Format!=1||!Guid.TryParseExact(state.InstallationId,"N",out _)||!Guid.TryParseExact(state.DataId,"N",out _)||!state.ServiceSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||!state.OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||state.Port is <1024 or >65535||state.Files.Length is <10 or >50000||state.PayloadSchema is <1 or >64)throw new IOException("INSTALL_STATE_INVALID");
        foreach(var member in state.Files)InstallPackage.Name(member.Path);return state;
    }
    private void SaveState(string phase)
    {state=state! with{Phase=phase};var bytes=Json(state);if(File.Exists(InstallLocations.StateFile))NativeInstallFiles.ReplaceOwned(InstallLocations.StateFile,bytes);else NativeInstallFiles.WriteNew(InstallLocations.StateFile,bytes);}
    public Task PrepareAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        NativeInstallFiles.RequireAdministrator();if(File.Exists(InstallLocations.RemovalStateFile))throw new IOException("UNINSTALL_INCOMPLETE");SafeDirectory.NoReparseAncestors(InstallLocations.State);SafeDirectory.NoReparseAncestors(InstallLocations.Binaries);
        if(Directory.Exists(InstallLocations.State))
        {
            state=ReadState();if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");
            if(state.Phase=="committed")
            {if(state.PackageDigest!=package.Manifest.ZipSha256)throw new IOException("INSTALL_UPGRADE_REQUIRED");service=NativeServiceInstall.OpenOwned();RequireExistingHost();existing=true;return;}
            if(state.Phase is "uninstalled" or "reinstalling")
            {
                PreservedInstallation.Require(state,package.Manifest,owner,name=>NativeInstallFiles.ReadOwned(Path.Combine(InstallLocations.State,name),state.ServiceSid));
                var wasRemoved=state.Phase=="uninstalled";
                if(wasRemoved&&(NativeServiceInstall.Exists()||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)))throw new IOException("INSTALL_COLLISION");
                preservedLease=WindowsFileBoundary.AcquireInstallerDataLease(Path.Combine(InstallLocations.State,"datasets",state.DataId,"data"),new SecurityIdentifier(state.ServiceSid));
                PreservedInstallation.VerifyDirectory(Path.Combine(InstallLocations.State,"payloads",state.PayloadDigest),state.Files,"payload/");
                SaveState("reinstalling");
                service=NativeServiceInstall.Exists()?NativeServiceInstall.OpenOwned():NativeServiceInstall.Create(owner.Sid);
                service.RequireStopped();if(ServiceSid()!=state.ServiceSid)throw new IOException("INSTALL_SERVICE_IDENTITY");
                if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,NativeInstallFiles.Descriptor(state.ServiceSid,ownerSid:owner.Sid));
                reinstalling=true;
            }
            else
            {
            if(state.Phase!="preparing"||state.PackageDigest!=package.Manifest.ZipSha256||state.PayloadDigest!=package.Manifest.PayloadDigest||!Directory.Exists(InstallLocations.Binaries))throw new IOException("EXISTING_DATA_PRESERVED");
            if(!NativeServiceInstall.Exists())throw new IOException("INSTALL_REPAIR_REQUIRED");service=NativeServiceInstall.OpenOwned();if(ServiceSid()!=state.ServiceSid)throw new IOException("INSTALL_SERVICE_IDENTITY");
            }
        }
        else
        {
            if(Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)||NativeServiceInstall.Exists())throw new IOException("INSTALL_COLLISION");
            NativeInstallFiles.NewDirectory(InstallLocations.State,NativeInstallFiles.Descriptor());rootCreated=true;
            service=NativeServiceInstall.Create(owner.Sid);var sid=ServiceSid();
            NativeInstallFiles.SetOwnedDirectory(InstallLocations.State,NativeInstallFiles.Descriptor(sid));
            NativeInstallFiles.NewDirectory(InstallLocations.Binaries,NativeInstallFiles.Descriptor(sid,ownerSid:owner.Sid));binariesCreated=true;
            var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();var port=Enumerable.Range(47123,17).FirstOrDefault(p=>!listeners.Any(e=>e.Port==p));if(port==0)throw new IOException("NO_LOCAL_PORT");
            state=new(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),package.Manifest.ZipSha256,package.Manifest.PayloadDigest,package.Manifest.SourceCommit,owner.Sid,port,"preparing",package.Manifest.Files,sid,owner.ImageSha256);SaveState("preparing");
        }
        cancellation.ThrowIfCancellationRequested();
        var available=new DriveInfo(Path.GetPathRoot(InstallLocations.State)!).AvailableFreeSpace;
        if(available<package.Manifest.Files.Sum(f=>f.Size)+new FileInfo(Environment.ProcessPath!).Length+134217728)throw new IOException("DISK_SPACE");
        staging=Path.Combine(InstallLocations.State,"stage-"+Guid.NewGuid().ToString("N"));NativeInstallFiles.NewDirectory(staging,NativeInstallFiles.Descriptor(state!.ServiceSid));
        package.Extract(staging,p=>progress(new("extracting",10+p*55/100)),cancellation,dir=>NativeInstallFiles.EnsureStageDirectory(staging,dir,NativeInstallFiles.Descriptor(state.ServiceSid)),NativeInstallFiles.CreateNewFile);
        var manifest=NativeJson.Read<JsonElement>(File.ReadAllBytes(Path.Combine(staging,"payload/bundle.json")));
        if(manifest.GetProperty("version").GetString()!="0.5.3"||manifest.GetProperty("target").GetString()!="win-x64"||manifest.GetProperty("profile").GetString()!="full")throw new IOException("PAYLOAD_VERSION");
        var build=NativeJson.Read<JsonElement>(File.ReadAllBytes(Path.Combine(staging,"payload/legal/build-inputs.json")));var payloadSchema=PayloadSchema.Read(build);
        if(reinstalling&&state.PayloadSchema!=payloadSchema)throw new IOException("PAYLOAD_SCHEMA");
        if(state.PayloadSchema!=payloadSchema){state=state with{PayloadSchema=payloadSchema};SaveState(state.Phase);}
    },cancellation);
    public Task PublishServiceAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        if(existing)return;var s=state??throw new IOException("INSTALL_NOT_PREPARED");if(staging is null)throw new IOException("INSTALL_NOT_STAGED");cancellation.ThrowIfCancellationRequested();
        var immutable=NativeInstallFiles.Descriptor(s.ServiceSid);var mutable=NativeInstallFiles.Descriptor(s.ServiceSid,true);
        void DirectoryNew(string relative,string descriptor){var path=Path.Combine(InstallLocations.State,relative);if(!Directory.Exists(path))NativeInstallFiles.NewDirectory(path,descriptor);else SafeDirectory.NoReparseAncestors(path);}
        DirectoryNew("datasets",immutable);DirectoryNew("datasets/"+s.DataId,immutable);DirectoryNew("datasets/"+s.DataId+"/data",mutable);DirectoryNew("temporary",mutable);DirectoryNew("Secrets",immutable);DirectoryNew("payloads",immutable);
        var selected=Path.Combine(InstallLocations.State,"datasets",s.DataId);var data=Path.Combine(selected,"data");
        if(!reinstalling&&Directory.EnumerateFileSystemEntries(data).Any(p=>Path.GetFileName(p)!="process.lock"))throw new IOException("EXISTING_DATA_PRESERVED");
        void PublishDirectory(string source,string destination,string prefix)
        {
            if(!Directory.Exists(destination)){Directory.Move(source,destination);return;}
            PreservedInstallation.VerifyDirectory(destination,s.Files,prefix);
        }
        PublishDirectory(Path.Combine(staging,"host"),Path.Combine(InstallLocations.Binaries,"host"),"host/");
        PublishDirectory(Path.Combine(staging,"payload"),Path.Combine(InstallLocations.State,"payloads",s.PayloadDigest),"payload/");
        if(!File.Exists(InstallLocations.InstallerImage))
        {
            var copy=Path.Combine(staging,"Yahad.Setup.exe");
            using(var input=new FileStream(Environment.ProcessPath!,FileMode.Open,FileAccess.Read,FileShare.Read))
            using(var output=NativeInstallFiles.CreateNewFile(copy)){input.CopyTo(output);output.Flush(true);}
            if(Hash(File.ReadAllBytes(copy))!=s.SetupSha256)throw new IOException("INSTALL_BOOTSTRAP_CHANGED");File.Move(copy,InstallLocations.InstallerImage);
        }
        if(Hash(File.ReadAllBytes(InstallLocations.InstallerImage))!=s.SetupSha256)throw new IOException("INSTALL_BOOTSTRAP_CHANGED");
        if(reinstalling)
        {
            PreservedInstallation.Require(s,package.Manifest,owner,name=>NativeInstallFiles.ReadOwned(Path.Combine(InstallLocations.State,name),s.ServiceSid));
            RegisterUninstall();SaveState("committed");existing=true;progress(new("installing_service",80));return;
        }
        var keyPath=Path.Combine(selected,"master-key");if(!File.Exists(keyPath))NativeInstallFiles.WriteNew(keyPath,Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        var key=NativeInstallFiles.ReadOwned(keyPath,s.ServiceSid);if(Convert.FromBase64String(Encoding.UTF8.GetString(key)).Length!=32)throw new IOException("INSTALL_KEY_INVALID");
        var policyPath=Path.Combine(selected,"update-policy");var pinPath=Path.Combine(selected,"update-pin");
        if(!File.Exists(policyPath))NativeInstallFiles.WriteNew(policyPath,Json(new{format=1,authority="installer",mode="notify",automaticWindowsUpdatesConfigured=false}));
        if(!File.Exists(pinPath))NativeInstallFiles.WriteNew(pinPath,Json(new{format=1,release=s.PayloadDigest}));
        var seedPath=Path.Combine(InstallLocations.State,"initial-seed.json");byte[] seedBytes;
        if(!File.Exists(seedPath))
        {
            seedBytes=Json(new FirstInstallSeed(1,s.InstallationId,s.DataId,s.PayloadDigest,Guid.NewGuid().ToString("N")));NativeInstallFiles.WriteNew(seedPath,seedBytes);
            NativeInstallFiles.SetOwnedFile(seedPath,NativeInstallFiles.Descriptor().Replace("D:P","D:P(A;;0x1300a9;;;"+s.ServiceSid+")"));
        }
        else{using var seed=WindowsFileBoundary.OpenInstallerInitialSeed(seedPath,new SecurityIdentifier(s.ServiceSid));seedBytes=seed.Small(4096);var record=NativeJson.Read<FirstInstallSeed>(seedBytes);if(record.InstallationId!=s.InstallationId||record.DataId!=s.DataId||record.ReleaseDigest!=s.PayloadDigest)throw new IOException("INSTALL_SEED_CHANGED");}
        var retryPath=Path.Combine(InstallLocations.State,"initial-retry.json");byte[] retryBytes;
        if(!File.Exists(retryPath))
        {
            retryBytes=Json(new FirstInstallSeed(1,s.InstallationId,s.DataId,s.PayloadDigest,Guid.NewGuid().ToString("N")));NativeInstallFiles.WriteNew(retryPath,retryBytes);
            NativeInstallFiles.SetOwnedFile(retryPath,NativeInstallFiles.Descriptor().Replace("D:P","D:P(A;;0x1300a9;;;"+s.ServiceSid+")"));
        }
        else{using var retry=WindowsFileBoundary.OpenInstallerInitialSeed(retryPath,new SecurityIdentifier(s.ServiceSid));retryBytes=retry.Small(4096);var record=NativeJson.Read<FirstInstallSeed>(retryBytes);if(record.InstallationId!=s.InstallationId||record.DataId!=s.DataId||record.ReleaseDigest!=s.PayloadDigest)throw new IOException("INSTALL_SEED_CHANGED");}
        setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        var capability=Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds()});var capabilityPath=Path.Combine(InstallLocations.State,"Secrets/setup-capability.json");
        if(File.Exists(capabilityPath))NativeInstallFiles.ReplaceOwned(capabilityPath,capability);else NativeInstallFiles.WriteNew(capabilityPath,capability);
        var intent=Json(new InstalledOwnerIntent(1,s.InstallationId,1,Guid.NewGuid().ToString("N"),s.PayloadDigest,s.DataId,"activate",s.OwnerSid,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),null));
        var web=Json(new InstalledWeb(1,s.InstallationId,"http://127.0.0.1:"+s.Port,s.Port));
        var activation=Json(new InstalledActivation(1,s.InstallationId,1,s.PayloadDigest,s.DataId,s.PayloadSchema,s.PayloadSchema,Hash(key),Hash(NativeInstallFiles.ReadOwned(policyPath,s.ServiceSid)),Hash(NativeInstallFiles.ReadOwned(pinPath,s.ServiceSid)),Hash(intent),Hash(web),"committed",null));
        void Record(string name,byte[] bytes){var path=Path.Combine(InstallLocations.State,name);if(File.Exists(path))NativeInstallFiles.ReplaceOwned(path,bytes);else NativeInstallFiles.WriteNew(path,bytes);}
        Record("owner-intent.json",intent);Record("web.json",web);Record("activation.json",activation);Record("epoch.json",Json(new InstalledEpoch(1,s.InstallationId,1,Hash(activation))));
        Record("bootstrap.json",Json(new{schema=1,service=InstallLocations.ServiceName,installationId=s.InstallationId,activationReceiptDigest=Hash(activation)}));
        Record("installation.json",Json(new FirstInstallReceipt(2,s.InstallationId,s.DataId,s.PayloadDigest,"0.5.3",Hash(seedBytes),s.Port,s.PackageDigest,"committed",Hash(retryBytes))));
        RegisterUninstall();SaveState("committed");progress(new("installing_service",80));
    },cancellation);
    public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation)
    {
        var s=state??throw new IOException("INSTALL_NOT_PREPARED");if(s.Phase!="committed")throw new IOException("INSTALL_NOT_COMMITTED");
        preservedLease?.Dispose();preservedLease=null;service!.EnableAutomaticStart();
        await service.StartAsync(_=>progress(new("starting_server",90)),cancellation);
        var url="http://127.0.0.1:"+s.Port+"/";using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        using var response=await client.GetAsync(url+"health/ready",cancellation);response.EnsureSuccessStatusCode();var body=await response.Content.ReadAsByteArrayAsync(cancellation);if(body.Length>16384||!NativeJson.Read<JsonElement>(body).GetProperty("ok").GetBoolean())throw new IOException("SERVER_NOT_READY");
        var statusResponse=await client.GetAsync(url+"api/setup/status",cancellation);statusResponse.EnsureSuccessStatusCode();
        var statusBytes=await statusResponse.Content.ReadAsByteArrayAsync(cancellation);if(statusBytes.Length>16384)throw new IOException("SERVER_NOT_READY");
        var setupState=NativeJson.Read<JsonElement>(statusBytes).GetProperty("state").GetString();
        if(setupState=="complete")setupToken=null;
        else if(existing)
        {
            var capPath=Path.Combine(InstallLocations.State,"Secrets/setup-capability.json");
            if(setupState=="closed")
            {
                // Only the OS-authenticated original owner may renew the existing
                // short-lived web bootstrap. No database, key or parent is reset.
                setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
                NativeInstallFiles.ReplaceOwned(capPath,Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds()}));
                await service.StopAsync(cancellation);await service.StartAsync(_=>progress(new("starting_server",95)),cancellation);
                using var again=await client.GetAsync(url+"api/setup/status",cancellation);again.EnsureSuccessStatusCode();
                var next=await again.Content.ReadAsByteArrayAsync(cancellation);if(next.Length>16384||NativeJson.Read<JsonElement>(next).GetProperty("state").GetString()!="ready")throw new IOException("SERVER_NOT_READY");
            }
            else if(setupState=="ready")
            {var cap=NativeJson.Read<JsonElement>(NativeInstallFiles.ReadOwned(capPath,s.ServiceSid));if(cap.GetProperty("expiresAt").GetInt64()<=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())throw new IOException("SERVER_NOT_READY");setupToken=cap.GetProperty("token").GetString();}
            else throw new IOException("SERVER_NOT_READY");
        }
        var lan=await new NativeLanInstall(s).ObserveAsync(cancellation);
        return new(true,"READY",url,setupToken,Lan:lan);
    }
    public async Task ConfigureLanAsync(OwnerProcess owner,LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        var s=state??throw new IOException("INSTALL_NOT_PREPARED");if(s.OwnerSid!=owner.Sid)throw new IOException("INSTALL_OWNER_CHANGED");
        var network=new NativeLanInstall(s);network.Preflight(request);
        await service!.StopAsync(cancellation);service.RequireCleanStop();
        await network.ConfigureAsync(request,progress,cancellation);
    }
    public async Task<SetupResult> UninstallAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData)
    {
        NativeInstallFiles.RequireAdministrator();UninstallChoice.Require(mode);
        var resumed=File.Exists(InstallLocations.RemovalStateFile);state=ReadState(resumed?InstallLocations.RemovalStateFile:null);
        if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");UninstallChoice.RequireSameIntent(state.Phase,mode);
        if(state.Phase is not("committed" or "preparing" or "reinstalling" or "uninstalled" or "removing_keep_data" or "removing_all_data"))throw new IOException("INSTALL_REPAIR_REQUIRED");
        if(resumed&&!state.Phase.StartsWith("removing_",StringComparison.Ordinal))throw new IOException("UNINSTALL_INTENT_INVALID");
        var s=state;var sid=new SecurityIdentifier(s.ServiceSid);
        if(resumed&&File.Exists(InstallLocations.StateFile))
        {var other=ReadState();if(!UninstallChoice.SameInstallation(s,other))throw new IOException("UNINSTALL_INTENT_INVALID");}
        if(Directory.Exists(InstallLocations.Binaries))SafeDirectory.NoReparseAncestors(InstallLocations.Binaries);
        else if(s.Phase is "uninstalled" or "removing_all_data" or "removing_keep_data")NativeInstallFiles.NewDirectory(InstallLocations.Binaries,NativeInstallFiles.Descriptor(s.ServiceSid,ownerSid:s.OwnerSid));
        else throw new IOException("INSTALL_REPAIR_REQUIRED");
        var allowed=s.Files.Where(f=>f.Path.StartsWith("host/",StringComparison.Ordinal)).ToDictionary(f=>Path.Combine(InstallLocations.Binaries,f.Path.Replace('/',Path.DirectorySeparatorChar)),f=>f.Sha256,StringComparer.OrdinalIgnoreCase);
        allowed[InstallLocations.InstallerImage]=s.SetupSha256;
        var directories=new List<string>();var actual=new List<string>();
        void Walk(string root)
        {
            SafeDirectory.NoReparseAncestors(root);
            foreach(var entry in Directory.EnumerateFileSystemEntries(root))
            {
                if((File.GetAttributes(entry)&FileAttributes.ReparsePoint)!=0)throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");
                if(Directory.Exists(entry)){Walk(entry);directories.Add(entry);}
                else if(entry==InstallLocations.RemovalStateFile){using var journal=WindowsFileBoundary.OpenInstallerMetadata(entry);}
                else{if(!allowed.TryGetValue(entry,out var digest)||Hash(File.ReadAllBytes(entry))!=digest)throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");actual.Add(entry);}
            }
        }
        Walk(InstallLocations.Binaries);
        using(var registration=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall"))
        {if(registration is not null&&registration.GetValue("InstallLocation") as string!=InstallLocations.Binaries)throw new IOException("UNINSTALL_REGISTRATION_CHANGED");}
        if(NativeServiceInstall.Exists())
        {service=NativeServiceInstall.OpenOwned();progress(new("stopping_server",10));await service.StopAsync(cancellation);service.RequireCleanStop();}
        else if(s.Phase is not("uninstalled" or "removing_all_data" or "removing_keep_data"))throw new IOException("INSTALL_REPAIR_REQUIRED");
        LanSetupResult? lanRemoval=null;
        using var dataPin=Directory.Exists(InstallLocations.State)?WindowsFileBoundary.PinInstallerRemovalRoot(InstallLocations.State,sid):null;
        var data=Path.Combine(InstallLocations.State,"datasets",s.DataId,"data");
        WindowsFileBoundary.DataLease? lease=Directory.Exists(data)?WindowsFileBoundary.AcquireInstallerDataLease(data,sid):null;
        try
        {
            var tree=mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State)?RemovalTree.Inspect(InstallLocations.State,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,sid)):null;
            cancellation.ThrowIfCancellationRequested();state=s with{Phase=UninstallChoice.Phase(mode)};
            // The receipt stays beside both removable trees, including final binary cleanup.
            // A crash after partial data
            // removal cannot erase the owner/selection needed for an honest retry.
            var intent=Json(state);
            if(File.Exists(InstallLocations.RemovalStateFile))NativeInstallFiles.ReplaceOwned(InstallLocations.RemovalStateFile,intent);else NativeInstallFiles.WriteNew(InstallLocations.RemovalStateFile,intent);
            NativeInstallFiles.SetOwnedFile(InstallLocations.RemovalStateFile,NativeInstallFiles.Descriptor(s.ServiceSid));
            if(File.Exists(InstallLocations.StateFile))SaveState(state.Phase);
            // Record the owner's removal choice durably before any LAN CA or
            // proxy binaries can be deleted. A retry cannot switch modes.
            lanRemoval=await new NativeLanInstall(s).UninstallAsync(mode,cancellation);
            if(service is not null){service.Delete();service.Dispose();service=null;await NativeServiceInstall.WaitUntilRemovedAsync(cancellation);}
            lease?.Dispose();lease=null;
            if(mode==UninstallMode.DeleteData&&tree is not null)
            {tree=RemovalTree.Inspect(InstallLocations.State,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,sid,requireDeleteAccess:path!=InstallLocations.State));progress(new("deleting_data",25));tree.DeleteContents((path,directory)=>WindowsFileBoundary.DeleteRemovalMember(path,directory,sid),p=>progress(new("deleting_data",25+p*50/100)),cancellation);}
        }
        finally{lease?.Dispose();}
        // Root pin must be released only after service removal and all protected
        // children have gone. No authorized service can restart into this tree.
        dataPin?.Dispose();
        if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))WindowsFileBoundary.DeleteRemovalMember(InstallLocations.State,true,sid);
        foreach(var file in actual){cancellation.ThrowIfCancellationRequested();SafeDirectory.NoReparseAncestors(file);if(File.Exists(file)){if(Hash(File.ReadAllBytes(file))!=allowed[file])throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");File.Delete(file);}}
        foreach(var directory in directories.OrderByDescending(p=>p.Length))Directory.Delete(directory);
        Registry.LocalMachine.DeleteSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall",false);
        if(mode==UninstallMode.KeepData)SaveState("uninstalled");
        Directory.Delete(InstallLocations.Binaries);File.Delete(InstallLocations.RemovalStateFile);
        progress(new("removed",100));return new(true,mode==UninstallMode.DeleteData?"REMOVED_ALL":"REMOVED","http://127.0.0.1:"+s.Port+"/",DataPreserved:mode==UninstallMode.KeepData,Lan:lanRemoval);
    }
    private void RequireExistingHost()
    {
        var record=state!.Files.Single(f=>f.Path=="host/Yahad.ServiceHost.exe");SafeDirectory.NoReparseAncestors(InstallLocations.ServiceImage);
        if(Hash(File.ReadAllBytes(InstallLocations.ServiceImage))!=record.Sha256)throw new IOException("SERVICE_IMAGE_CHANGED");
    }
    private static void RegisterUninstall()
    {
        using var key=Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall",true);
        key.SetValue("DisplayName","יחד");key.SetValue("DisplayVersion","0.5.3");key.SetValue("Publisher","Yahad");key.SetValue("InstallLocation",InstallLocations.Binaries);
        key.SetValue("UninstallString","\""+InstallLocations.InstallerImage+"\" --uninstall");key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }
    public void Dispose()
    {
        // Only empty directories made by this invocation are removed after a
        // failure before the durable ownership record. Data is never traversed.
        if(state is null)
        {
            try{service?.Delete();}catch{}
            if(binariesCreated)try{Directory.Delete(InstallLocations.Binaries,false);}catch{}
            if(rootCreated)try{Directory.Delete(InstallLocations.State,false);}catch{}
        }
        preservedLease?.Dispose();preservedLease=null;service?.Dispose();
    }
}
