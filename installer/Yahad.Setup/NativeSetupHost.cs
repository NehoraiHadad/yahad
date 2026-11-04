using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

// The installer: the Runtime service running versions\<version>\host\Yahad.ServiceHost.exe
// and the Updater service running versions\<version>\updater\Yahad.Updater.exe, both
// under %ProgramFiles%\Yahad.FirstInstall, and the Yahad\Repair task.
// No method is entered without the elevated in-process Setup operation, its
// recorded owner and the global install lock.
internal sealed class NativeSetupHost(InstallPackage package,UpdateSetupChoice? requested):ISetupHostOperations,IDisposable
{
    private const string RegistryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall";
    private InstallerState? state;private NativeService? service,updater;private string? setupToken;private bool existing;
    private UninstallPhase operationPhase=UninstallPhase.FullPrepare;
    // The state root and the binary root are read-only to the Runtime service and
    // everything below inherits that; data and temporary are its only writable trees.
    private static string InstallAcl=>NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid);
    private static string DataAcl=>NativeInstallFiles.DataAcl(InstallLocations.ServiceSid);
    // Service SIDs follow from the name, so the proxy's entry exists from install. It
    // is on the binary root only: the state root holds the master key and the secrets.
    internal static string BinaryAcl=>NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid,InstallLocations.ServiceSidFor(LanProxyPaths.ServiceName));
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",state!.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string MasterKey=>Path.Combine(Dataset,"master-key");
    private static string Secrets=>Path.Combine(InstallLocations.State,"Secrets");
    private static string Capability=>Path.Combine(Secrets,"setup-capability.json");
    private string LocalUrl=>"http://127.0.0.1:"+state!.Port+"/";
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    internal SetupOperationException ReportFailure(Exception cause)=>new(operationPhase,cause);

    internal static InstallerState ReadState()
    {
        InstallLocations.RequireStateRoot();
        var value=NativeJson.Read<InstallerState>(NativeInstallFiles.ReadRecord(InstallLocations.StateFile));value.Require();return value;
    }
    private void Save(string phase)
    {state=state! with{Phase=phase};NativeInstallFiles.Publish(InstallLocations.StateFile,Json(state));}

    public Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        requested?.Require(DateTimeOffset.UtcNow);
        if(File.Exists(InstallLocations.RemovalStateFile))throw new IOException("UNINSTALL_INCOMPLETE");
        if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
        var manifest=package.Manifest;var version=manifest.Version;
        if(File.Exists(InstallLocations.StateFile))
        {
            state=ReadState();state.Consent.RequireCompatibleRetry(requested);
            ClearResidue();
            switch(state.Phase)
            {
                case "committed":
                    if(ReleaseManifest.Compare(version,state.Version)<0)throw new IOException("INSTALL_DOWNGRADE_REFUSED");
                    // A newer Setup has already moved this installation (UpgradeHost), so this
                    // is the same version built differently, which nothing can move between.
                    if(state.Version!=version||state.PackageDigest!=manifest.ZipSha256)throw new IOException("INSTALL_UPGRADE_REQUIRED");
                    service=NativeService.Open(version);updater=NativeService.Open(version,ServiceSpec.Updater);
                    InspectBinaries(Inventory(),true,true,"INSTALL_TARGET_CHANGED");existing=true;return;
                case "uninstalled":
                    if(ReleaseManifest.Compare(version,state.Version)<0)throw new IOException("INSTALL_DOWNGRADE_REFUSED");
                    if(AnyServiceExists()||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries))throw new IOException("INSTALL_COLLISION");
                    // The family, its data, key and identity stay; only the
                    // release and the image that installs it are new.
                    state=state with{Version=version,PackageDigest=manifest.ZipSha256,PayloadDigest=manifest.PayloadDigest,SourceCommit=manifest.SourceCommit,SetupSha256=owner.ImageSha256,Files=manifest.Files,ReleaseKeySha256=EmbeddedKeyDigest,Previous=null};
                    Save("preparing");break;
                case "preparing":
                    if(state.PackageDigest!=manifest.ZipSha256||state.Version!=version)throw new IOException("EXISTING_DATA_PRESERVED");break;
                default:throw new IOException("INSTALL_STATE_INVALID");
            }
        }
        else
        {
            if(requested is null)throw new IOException("UPDATE_CONSENT_REQUIRED");
            if(File.Exists(InstallLocations.State)||Directory.Exists(InstallLocations.State)||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)||AnyServiceExists())throw new IOException("INSTALL_COLLISION");
            var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();var port=Enumerable.Range(47123,17).FirstOrDefault(p=>!listeners.Any(e=>e.Port==p));if(port==0)throw new IOException("NO_LOCAL_PORT");
            var installation=Guid.NewGuid().ToString("N");
            state=new(InstallerState.CurrentFormat,installation,Guid.NewGuid().ToString("N"),version,manifest.ZipSha256,manifest.PayloadDigest,manifest.SourceCommit,owner.Sid,port,"preparing",
                owner.ImageSha256,FirstOwnerBinding.Create(installation,owner.Sid),requested,manifest.Files,InstallLocations.ServiceSid,EmbeddedKeyDigest);
            NativeInstallFiles.NewDirectory(InstallLocations.State,InstallAcl);Save("preparing");
        }
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<checked(manifest.Files.Sum(f=>f.Size)*3+536870912L))throw new IOException("DISK_SPACE");
        if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,BinaryAcl);
        // A resumed extraction holds only this package's members and their
        // uncommitted partials; the partials go, committed members stay.
        var (_,partials)=InspectBinaries(Inventory(),false,false,"INSTALL_TARGET_CHANGED");
        foreach(var partial in partials)WindowsFileBoundary.DeleteNoFollow(Path.Combine(InstallLocations.Binaries,partial.Replace('/',Path.DirectorySeparatorChar)),false);
        var root=InstallLocations.VersionRoot(version);NativeInstallFiles.EnsureDirectoryUnder(InstallLocations.Binaries,root);
        package.Extract(root,p=>progress(new("extracting",10+p*50/100)),cancellation,new ExtractTarget(
            directory=>NativeInstallFiles.EnsureDirectoryUnder(root,directory),NativeInstallFiles.CreateNewFile,
            NativeInstallFiles.CommitNew,VerifyOwned));
    },cancellation);

    private static bool AnyServiceExists()=>NativeService.Exists()||NativeService.Exists(ServiceSpec.Updater);
    private static string? EmbeddedKeyDigest=>TrustedReleaseKey.Embedded is {} key?TrustedReleaseKey.Digest(key):null;
    private InstallMember[] Inventory(){var image=InstallLocations.InstallerImage(state!.Version);return RemovalIntent.Inventory(state,File.Exists(image)?new FileInfo(image).Length:0);}
    private static readonly string EmptySha256=Convert.ToHexStringLower(SHA256.HashData([]));
    // A present immutable member must be byte-identical to its record.
    private static bool VerifyOwned(string path,InstallMember record)
    {
        using var file=WindowsFileBoundary.OpenRead(path);
        return file.Length==record.Size&&(record.Size==0?record.Sha256==EmptySha256:file.Sha256(record.Size)==record.Sha256);
    }
    // The binary root holds exactly the recorded members (or, when not
    // complete, a subset with their owned partials). Anything else refuses.
    private static (RemovalTree? Tree,string[] Partials) InspectBinaries(InstallMember[] inventory,bool complete,bool verify,string code)
    {
        if(!Directory.Exists(InstallLocations.Binaries)){if(complete)throw new IOException(code);return(null,[]);}
        var tree=RemovalTree.Inspect(InstallLocations.Binaries,WindowsFileBoundary.RequireNoLink);
        string Relative(string path)=>Path.GetRelativePath(InstallLocations.Binaries,path).Replace('\\','/');
        string[] partials;
        try{partials=InstallPackage.RequireExtractedInventory(inventory.Select(f=>f.Path),tree.Files.Select(Relative),tree.Directories.Select(Relative));}
        catch(IOException error)when(error.Message=="INSTALL_TARGET_CHANGED"){throw new IOException(code);}
        if(complete&&partials.Length!=0)throw new IOException(code);
        var present=new HashSet<string>(tree.Files.Select(Relative),StringComparer.OrdinalIgnoreCase);
        foreach(var member in inventory)
        {
            if(!present.Contains(member.Path)){if(complete)throw new IOException(code);continue;}
            if(verify&&!VerifyOwned(Path.Combine(InstallLocations.Binaries,member.Path.Replace('/',Path.DirectorySeparatorChar)),member))throw new IOException(code);
        }
        return(tree,partials);
    }
    // Interrupted record replacements leave only owned partials beside them.
    private void ClearResidue()
    {foreach(var directory in new[]{InstallLocations.State,Secrets,Dataset})FullInstallerResidue.Clear(directory);}
    private static void EnsureDirectory(string path,string? acl=null){if(!Directory.Exists(path))NativeInstallFiles.NewDirectory(path,acl);}

    public Task PublishServiceAsync(Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        operationPhase=UninstallPhase.FullProvision;
        if(existing)return;
        EnsureDirectory(Path.Combine(InstallLocations.State,"datasets"));EnsureDirectory(Dataset);EnsureDirectory(DataPath,DataAcl);
        EnsureDirectory(Path.Combine(InstallLocations.State,"temporary"),DataAcl);EnsureDirectory(Secrets);
        if(!File.Exists(MasterKey))NativeInstallFiles.PublishNew(MasterKey,Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        // A kept installation keeps its launch file.
        if(!File.Exists(InstallLocations.LaunchFile))NativeInstallFiles.PublishNew(InstallLocations.LaunchFile,InstalledLaunch.Encode(new(1,state!.InstallationId,state.DataId,state.Port,"http://127.0.0.1:"+state.Port)));
        RenewCapability();
        var image=InstallLocations.InstallerImage(state!.Version);
        var setup=new InstallMember(RemovalIntent.SetupMember(state.Version),new FileInfo(Environment.ProcessPath!).Length,state.SetupSha256);
        if(File.Exists(image)){if(!VerifyOwned(image,setup))throw new IOException("INSTALL_TARGET_CHANGED");}
        else CopyVerified(Environment.ProcessPath!,image,setup);
        // The root of trust for every later upgrade. A build without a key writes none.
        if(state.ReleaseKeySha256 is {} digest)
        {
            var record=new InstallMember(TrustedReleaseKey.InstalledName,TrustedReleaseKey.SpkiBytes,digest);
            if(File.Exists(InstallLocations.ReleaseKeyFile)){if(!VerifyOwned(InstallLocations.ReleaseKeyFile,record))throw new IOException("INSTALL_TARGET_CHANGED");}
            else NativeInstallFiles.PublishNew(InstallLocations.ReleaseKeyFile,TrustedReleaseKey.Embedded??throw new IOException("INSTALL_TARGET_CHANGED"));
        }
        progress(new("installing_service",75));
        UpgradeFiles.EnsureUpdaterState(state.Consent.Automatic);
        // The Runtime first: the Updater's service ACL names the Runtime's account.
        service??=NativeService.Exists()?NativeService.OpenForProvisioning(state.Version,state.OwnerSid):NativeService.Create(state.Version,state.OwnerSid);
        updater??=NativeService.Exists(ServiceSpec.Updater)?NativeService.OpenForProvisioning(state.Version,state.OwnerSid,ServiceSpec.Updater):NativeService.Create(state.Version,state.OwnerSid,ServiceSpec.Updater);
        RepairTask.Register(state.Version,DateTimeOffset.Now);
        Register(state);Save("committed");progress(new("installing_service",80));
    },cancellation);
    internal static void CopyVerified(string source,string target,InstallMember record)
    {
        var partial=target+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=NativeInstallFiles.CreateNewFile(partial))
            {
                if(input.Length!=record.Size||Convert.ToHexStringLower(SHA256.HashData(input))!=record.Sha256)throw new IOException("INSTALL_TARGET_CHANGED");
                input.Position=0;input.CopyTo(output);output.Flush(true);
            }
            NativeInstallFiles.CommitNew(partial,target);
        }
        catch{try{File.Delete(partial);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
    }
    private void RenewCapability()
    {
        setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        NativeInstallFiles.Publish(Capability,Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds(),firstOwner=state!.FirstOwner}));
    }
    internal static void Register(InstallerState state)
    {
        using var key=Registry.LocalMachine.CreateSubKey(RegistryKey,true);key.SetValue("DisplayName","יחד");key.SetValue("DisplayVersion",state.Version);key.SetValue("Publisher","Yahad");
        key.SetValue("InstallLocation",InstallLocations.Binaries);key.SetValue("UninstallString","\""+InstallLocations.InstallerImage(state.Version)+"\" --uninstall");
        key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }

    public async Task ConfigureLanAsync(LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullNetwork;
        var network=new NativeLanInstall(state!);network.Preflight(request);
        // The launch file changes only while the Runtime is stopped.
        await service!.StopAsync(cancellation);network.RecoverWebJournal();await network.ConfigureAsync(request,progress,cancellation);
    }
    public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullStart;
        var network=new NativeLanInstall(state!);
        if(existing){await service!.StopAsync(cancellation);network.RecoverWebJournal();}
        service!.EnableStartup();updater!.EnableStartup();progress(new("starting_server",85));await service.StartAsync(cancellation);
        // RUNNING is reported only after the application answered its own health check.
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        using(var health=await client.GetAsync(LocalUrl+"health/ready",cancellation))
        {
            health.EnsureSuccessStatusCode();var bytes=await health.Content.ReadAsByteArrayAsync(cancellation);
            if(bytes.Length>16384||!NativeJson.Read<JsonElement>(bytes).GetProperty("ok").GetBoolean())throw new IOException("SERVER_NOT_READY");
        }
        async Task<string?> SetupState()
        {
            using var response=await client.GetAsync(LocalUrl+"api/setup/status",cancellation);response.EnsureSuccessStatusCode();
            var result=await response.Content.ReadAsByteArrayAsync(cancellation);if(result.Length>16384)throw new IOException("SERVER_NOT_READY");
            return NativeJson.Read<JsonElement>(result).GetProperty("state").GetString();
        }
        var setup=await SetupState();
        if(setup=="complete")setupToken=null;
        else if(existing&&setup=="closed")
        {
            // An expired first-owner capability is renewed only while stopped.
            await service.StopAsync(cancellation);RenewCapability();await service.StartAsync(cancellation);
            if(await SetupState()!="ready")throw new IOException("SERVER_NOT_READY");
        }
        else if(existing&&setup=="ready")setupToken=NativeJson.Read<JsonElement>(NativeInstallFiles.ReadRecord(Capability)).GetProperty("token").GetString();
        else if(setup!="ready")throw new IOException("SERVER_NOT_READY");
        await updater.StartAsync(cancellation);
        var lan=await network.ObserveAsync(cancellation);
        return new(true,"READY",LocalUrl,setupToken,Lan:lan);
    }

    private static void SaveRemoval(RemovalIntent intent)
    {
        var path=InstallLocations.RemovalStateFile;
        foreach(var partial in Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".partial-*"))File.Delete(partial);
        NativeInstallFiles.Publish(path,Json(intent));
    }
    public async Task<SetupResult> UninstallAsync(Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData)
    {
        operationPhase=UninstallPhase.FullUninstall;
        if(!File.Exists(InstallLocations.RemovalStateFile)&&!File.Exists(InstallLocations.StateFile))throw new IOException("INSTALL_STATE_INVALID");
        if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
        RemovalIntent? intent=null;
        if(File.Exists(InstallLocations.RemovalStateFile))
        {
            intent=NativeJson.Read<RemovalIntent>(NativeInstallFiles.ReadRecord(InstallLocations.RemovalStateFile));
            intent.Require();state=intent.State;
            if(intent.Mode!=mode)throw new IOException("UNINSTALL_MODE_CHANGED");
            if(File.Exists(InstallLocations.StateFile))intent.RequireSameInstallation(ReadState());
        }
        else state=ReadState();
        if(intent is null&&state.Phase.StartsWith("removing_",StringComparison.Ordinal))throw new IOException("INSTALL_STATE_INVALID");
        if(intent is null&&state.Phase=="uninstalled")
        {
            if(AnyServiceExists()||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries))throw new IOException("INSTALL_COLLISION");
            if(mode==UninstallMode.KeepData)return new(true,"REMOVED",LocalUrl);
        }
        RepairTask.Remove();
        if(intent?.AtLeast("services_removed")==true){if(AnyServiceExists())throw new IOException("UNINSTALL_SERVICE_REAPPEARED");}
        else
        {
            service=await NativeService.OpenForRemovalAsync(state.Version,cancellation);
            updater=await NativeService.OpenForRemovalAsync(state.Version,cancellation,ServiceSpec.Updater);
            operationPhase=UninstallPhase.StopService;
            // The Updater first, so nothing can start an upgrade behind the removal.
            if(updater is not null){updater.FenceForRemoval();await updater.StopAsync(cancellation);}
            if(service is not null){service.FenceForRemoval();await service.StopAsync(cancellation);}
            new NativeLanInstall(state).RecoverWebJournal();progress(new("stopping_server",10));
            operationPhase=UninstallPhase.AcquireDataLease;
            // STOPPED is not proof of writer cleanup: the data lease is held
            // across every check and destructive step that follows.
            using(var data=Directory.Exists(DataPath)?WindowsFileBoundary.AcquireDataLease(DataPath):null)
            {
                service?.ConfirmStopped();updater?.ConfirmStopped();
                if(intent is null)
                {
                    operationPhase=UninstallPhase.InspectData;
                    var inventory=Inventory();_=InspectBinaries(inventory,state.Phase=="committed",true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
                    if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))_=RemovalTree.Inspect(InstallLocations.State,WindowsFileBoundary.RequireNoLink);
                    operationPhase=UninstallPhase.WriteRemovalReceipt;
                    state=state with{Phase=UninstallChoice.Phase(mode)};intent=new(InstallerState.CurrentFormat,state,mode,inventory,"prepared");SaveRemoval(intent);Save(state.Phase);
                }
                if(!intent.AtLeast("lan_removed"))
                {
                    operationPhase=UninstallPhase.RemoveLan;await new NativeLanInstall(state).UninstallAsync(mode,cancellation);
                    intent=intent with{Stage="lan_removed"};SaveRemoval(intent);
                }
                operationPhase=UninstallPhase.RemoveService;
                if(updater is not null){updater.Delete();updater.Dispose();updater=null;}
                if(service is not null){service.Delete();service.Dispose();service=null;}
                operationPhase=UninstallPhase.WaitForServiceRemoval;
                using(var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {wait.CancelAfter(TimeSpan.FromSeconds(15));while(AnyServiceExists())await Task.Delay(100,wait.Token);}
                operationPhase=UninstallPhase.SaveRemovalState;intent=intent with{Stage="services_removed"};SaveRemoval(intent);
            }
        }
        if(!intent!.AtLeast("data_removed"))
        {
            if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))
            {
                operationPhase=UninstallPhase.DeleteData;
                var tree=RemovalTree.Inspect(InstallLocations.State,WindowsFileBoundary.RequireNoLink);
                tree.DeleteContents(WindowsFileBoundary.DeleteNoFollow,p=>progress(new("deleting_data",25+p*50/100)),cancellation);
                operationPhase=UninstallPhase.DeleteDataRoot;WindowsFileBoundary.DeleteNoFollow(InstallLocations.State,true);
            }
            else if(mode==UninstallMode.KeepData)
            {
                // The mode stays with the data; a downloaded release does not.
                operationPhase=UninstallPhase.SavePreservedState;UpgradeFiles.Remove(Path.Combine(InstalledLayout.UpdaterRoot,"staging"));Save("uninstalled");
            }
            intent=intent with{Stage="data_removed"};SaveRemoval(intent);
        }
        if(!intent.AtLeast("binaries_removed"))
        {
            operationPhase=UninstallPhase.DeleteBinaries;
            var(tree,_)=InspectBinaries(intent.Binaries,false,true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
            if(tree is not null)
            {
                tree.DeleteContents(WindowsFileBoundary.DeleteNoFollow,p=>progress(new("deleting_files",75+p*20/100)),cancellation);
                WindowsFileBoundary.DeleteNoFollow(InstallLocations.Binaries,true);
            }
            intent=intent with{Stage="binaries_removed"};SaveRemoval(intent);
        }
        operationPhase=UninstallPhase.RemoveRegistration;Registry.LocalMachine.DeleteSubKey(RegistryKey,false);
        // The protected receipt is deleted last: a failed final step still has
        // the complete owner, mode and inventory for the same bounded retry.
        operationPhase=UninstallPhase.DeleteReceipt;File.Delete(InstallLocations.RemovalStateFile);
        progress(new("removed",100));
        return new(true,mode==UninstallMode.DeleteData?"REMOVED_ALL":"REMOVED",LocalUrl,DataPreserved:mode==UninstallMode.KeepData);
    }
    public void Dispose(){service?.Dispose();service=null;updater?.Dispose();updater=null;}
}
