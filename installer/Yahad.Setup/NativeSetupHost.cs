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

// The installer (docs/WINDOWS-APP-PACKAGE-PLAN.md 4.1, 8.3): the Runtime service running
// infra\<infra>\host\Yahad.ServiceHost.exe and the Updater service running
// infra\<infra>\updater\Yahad.Updater.exe, with the app in apps\<app>\, all under
// %ProgramFiles%\Yahad.FirstInstall, active.json naming the pair, and the Yahad\Repair
// task on the tree's own engine. The package is present only for an install: removal
// is payload-free (4.4). No method is entered without the elevated in-process Setup
// operation, its recorded owner and the global install lock.
internal sealed class NativeSetupHost(InstallPackage? package,UpdateSetupChoice? requested):ISetupHostOperations,IDisposable
{
    private const string RegistryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall";
    private InstallerState? state;private NativeService? service,updater;private string? setupToken;private int? reportedSchema;
    // resumed: the services exist and are provisioned. committed: nothing to record.
    private bool resumed,committed;
    // The owner's home-name option on this run's Enable request, null without one; and the label they chose for it, if any.
    private bool? homeNameChoice;private string? homeNameLabel;
    private UninstallPhase operationPhase=UninstallPhase.FullPrepare;
    // The state root and the binary root are read-only to the Runtime service and
    // everything below inherits that; data and temporary are its only writable trees.
    private static string InstallAcl=>NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid);
    private static string DataAcl=>NativeInstallFiles.DataAcl(InstallLocations.ServiceSid);
    // Service SIDs follow from the name, so the proxy's entry exists from install. It
    // is on the binary root only: the state root holds the master key and the secrets.
    // So is Users: Apps & Features starts the uninstall image from an unelevated shell,
    // which must read the image's manifest before it can ask for elevation at all.
    internal static string BinaryAcl=>NativeInstallFiles.InstallAcl(InstallLocations.ServiceSid,InstallLocations.ServiceSidFor(LanProxyPaths.ServiceName),"BU");
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",state!.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string MasterKey=>Path.Combine(Dataset,"master-key");
    // A kept database the new app migrates is copied here before the first start (InstallBackup).
    private string MigrationBackup=>Path.Combine(Dataset,"install-backup");
    private static string Secrets=>Path.Combine(InstallLocations.State,"Secrets");
    private static string Capability=>Path.Combine(Secrets,"setup-capability.json");
    private string LocalUrl=>"http://127.0.0.1:"+state!.Port+"/";
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private InstallPackage Package=>package??throw new IOException("PACKAGE_NOT_EMBEDDED");
    private string Infrastructure=>state!.Active.Infrastructure.Version;
    internal SetupOperationException ReportFailure(Exception cause)=>new(operationPhase,cause);

    internal static InstallerState ReadState()
    {
        InstallLocations.RequireStateRoot();
        return InstallerState.Read(InstallLocations.StateFile);
    }
    private void Save(string phase)
    {state=state! with{Phase=phase};NativeInstallFiles.Publish(InstallLocations.StateFile,state.Encode());}

    public Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        requested?.Require(DateTimeOffset.UtcNow);var package=Package;
        // A pre-pair layout is refused before anything is read further or written.
        ExistingInstallation.RequireNotLegacy();
        var recorded=File.Exists(InstallLocations.StateFile)?ReadState():null;
        if(File.Exists(InstallLocations.RemovalStateFile))throw new IOException("UNINSTALL_INCOMPLETE");
        if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
        var target=package.Pair();
        if(recorded is not null)
        {
            state=recorded;state.Consent.RequireCompatibleRetry(requested);
            ClearResidue();
            switch(state.Phase)
            {
                case "committed":
                    // The same pair, byte for byte: a reinstall that only re-checks and restarts.
                    // Anything newer went through UpgradeHost; anything else is refused by name.
                    if(!state.Active.SameTrees(target)){_=UpgradeRules.Decide(state.Active,target);throw new IOException("INSTALL_UPGRADE_REQUIRED");}
                    service=NativeService.Open(Infrastructure);updater=NativeService.Open(Infrastructure,ServiceSpec.Updater);
                    InspectBinaries(Inventory(),true,true,"INSTALL_TARGET_CHANGED");resumed=committed=true;return;
                case "uninstalled":
                    // The family, its data, key and identity stay; only the pair is new. The kept
                    // database must be one this app opens or migrates from (8.3).
                    if(SemVer.Compare(target.App.Version,state.Active.App.Version)<0)throw new IOException("INSTALL_DOWNGRADE_REFUSED");
                    package.RequireFrom(state.Active);if(!package.AcceptsSchema(state.Active.App.DatabaseSchema))throw new IOException("RELEASE_UNSUPPORTED_FROM");
                    if(AnyServiceExists()||Path.Exists(InstallLocations.Binaries))throw new IOException("INSTALL_COLLISION");
                    // Until the first healthy start the record keeps the schema the data is at.
                    state=state with{Active=target with{App=target.App with{DatabaseSchema=state.Active.App.DatabaseSchema}},ReleaseKeySha256=EmbeddedKeyDigest,Previous=null};
                    Save("preparing");break;
                case "preparing":
                case "provisioned":
                    if(!state.Active.SameTrees(target))throw new IOException("EXISTING_DATA_PRESERVED");
                    resumed=state.Phase=="provisioned";break;
                default:throw new IOException("INSTALL_STATE_INVALID");
            }
        }
        else
        {
            if(requested is null)throw new IOException("UPDATE_CONSENT_REQUIRED");
            if(Path.Exists(InstallLocations.State)||Path.Exists(InstallLocations.Binaries)||AnyServiceExists())throw new IOException("INSTALL_COLLISION");
            var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();var port=Enumerable.Range(47123,17).FirstOrDefault(p=>!listeners.Any(e=>e.Port==p));if(port==0)throw new IOException("NO_LOCAL_PORT");
            var installation=Guid.NewGuid().ToString("N");
            state=new(InstallerState.CurrentFormat,installation,Guid.NewGuid().ToString("N"),owner.Sid,port,"preparing",FirstOwnerBinding.Create(installation,owner.Sid),requested,
                InstallLocations.ServiceSid,target,EmbeddedKeyDigest);
            NativeInstallFiles.NewDirectory(InstallLocations.State,InstallAcl);Save("preparing");
        }
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<package.Manifest.RequiredFreeBytes())throw new IOException("DISK_SPACE");
        if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,BinaryAcl);
        // A resumed extraction holds only this pair's members and their uncommitted
        // partials; the partials go, committed members stay.
        var (_,partials)=InspectBinaries(Inventory(),false,false,"INSTALL_TARGET_CHANGED");
        foreach(var partial in partials)WindowsFileBoundary.DeleteNoFollow(Path.Combine(InstallLocations.Binaries,partial.Replace('/',Path.DirectorySeparatorChar)),false);
        Extract(package,TreeKind.Infrastructure,InstalledLayout.InfraRoot(Infrastructure),p=>progress(new("extracting",10+p*25/100)),cancellation);
        Extract(package,TreeKind.App,InstalledLayout.AppRoot(state.Active.App.Version),p=>progress(new("extracting",35+p*25/100)),cancellation);
        // The pointer comes last: it names two whole trees.
        var pointer=state.Active.Pointer().Encode();
        if(File.Exists(InstalledLayout.ActivePointerFile)){if(!VerifyOwned(InstalledLayout.ActivePointerFile,new(InstalledLayout.ActivePointerName,pointer.Length,TreeRecord.Sha256(pointer))))throw new IOException("INSTALL_TARGET_CHANGED");}
        else NativeInstallFiles.PublishNew(InstalledLayout.ActivePointerFile,pointer);
    },cancellation);
    private static void Extract(InstallPackage package,TreeKind kind,string root,Action<int> progress,CancellationToken cancellation)
    {
        NativeInstallFiles.EnsureDirectoryUnder(InstallLocations.Binaries,root);
        package.Extract(kind,root,progress,cancellation,new ExtractTarget(directory=>NativeInstallFiles.EnsureDirectoryUnder(root,directory),NativeInstallFiles.CreateNewFile,NativeInstallFiles.CommitNew,VerifyOwned));
    }

    private static bool AnyServiceExists()=>NativeService.Exists()||NativeService.Exists(ServiceSpec.Updater);
    private static string? EmbeddedKeyDigest=>TrustedReleaseKey.Embedded is {} key?TrustedReleaseKey.Digest(key):null;
    private InstallMember[] Inventory()=>RemovalIntent.Inventory(state!);
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

    public Task ProvisionAsync(Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        operationPhase=UninstallPhase.FullProvision;
        if(resumed)
        {
            service??=NativeService.Open(Infrastructure);updater??=NativeService.Open(Infrastructure,ServiceSpec.Updater);return;
        }
        EnsureDirectory(Path.Combine(InstallLocations.State,"datasets"));EnsureDirectory(Dataset);EnsureDirectory(DataPath,DataAcl);
        EnsureDirectory(Path.Combine(InstallLocations.State,"temporary"),DataAcl);EnsureDirectory(Secrets);
        if(!File.Exists(MasterKey))NativeInstallFiles.PublishNew(MasterKey,Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        // A kept installation keeps its launch file.
        if(!File.Exists(InstallLocations.LaunchFile))NativeInstallFiles.PublishNew(InstallLocations.LaunchFile,InstalledLaunch.Encode(new(1,state!.InstallationId,state.DataId,state.Port,"http://127.0.0.1:"+state.Port)));
        RenewCapability();
        // The root of trust for every later upgrade. A build without a key writes none.
        if(state!.ReleaseKeySha256 is {} digest)
        {
            var record=new InstallMember(TrustedReleaseKey.InstalledName,TrustedReleaseKey.SpkiBytes,digest);
            if(File.Exists(InstallLocations.ReleaseKeyFile)){if(!VerifyOwned(InstallLocations.ReleaseKeyFile,record))throw new IOException("INSTALL_TARGET_CHANGED");}
            else NativeInstallFiles.PublishNew(InstallLocations.ReleaseKeyFile,TrustedReleaseKey.Embedded??throw new IOException("INSTALL_TARGET_CHANGED"));
        }
        progress(new("installing_service",75));
        UpgradeFiles.EnsureUpdaterState(state.Consent.Automatic);
        // The Runtime first: the Updater's service ACL names the Runtime's account.
        service??=NativeService.Exists()?NativeService.OpenForProvisioning(Infrastructure,state.OwnerSid):NativeService.Create(Infrastructure,state.OwnerSid);
        updater??=NativeService.Exists(ServiceSpec.Updater)?NativeService.OpenForProvisioning(Infrastructure,state.OwnerSid,ServiceSpec.Updater):NativeService.Create(Infrastructure,state.OwnerSid,ServiceSpec.Updater);
        // Repair and removal run the installed tree's own payload-free engine.
        RepairTask.Register(Infrastructure,DateTimeOffset.Now);Register(state);
        Save("provisioned");progress(new("installing_service",80));
    },cancellation);
    private void RenewCapability()
    {
        setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        NativeInstallFiles.Publish(Capability,Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds(),firstOwner=state!.FirstOwner}));
    }
    internal static void Register(InstallerState state)
    {
        using var key=Registry.LocalMachine.CreateSubKey(RegistryKey,true);key.SetValue("DisplayName","יחד");key.SetValue("DisplayVersion",state.Active.App.Version);key.SetValue("Publisher","Yahad");
        key.SetValue("InstallLocation",InstallLocations.Binaries);key.SetValue("UninstallString","\""+InstalledLayout.EngineImage(state.Active.Infrastructure.Version)+"\" --uninstall");
        key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }

    public async Task ConfigureLanAsync(LanSetupRequest request,bool homeName,string? label,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullNetwork;homeNameChoice=request.Action==LanSetupAction.Enable?homeName:null;homeNameLabel=homeNameChoice==true?label:null;
        var network=new NativeLanInstall(state!);network.Preflight(request);
        // The launch file changes only while the Runtime is stopped.
        await service!.StopAsync(cancellation);network.RecoverWebJournal();await network.ConfigureAsync(request,progress,cancellation);
    }
    // A kept database the new app migrates (8.3): copied whole before the first start,
    // and put back before any later start of this same attempt, so a start that failed
    // halfway through a migration never leaves the next start a half-migrated database.
    // A copy bound to anything but this attempt is replaced, never restored.
    private async Task PrepareDataAsync(CancellationToken cancellation)
    {
        if(state!.Active.App.DatabaseSchema==Package.Manifest.App.DatabaseSchema)return;
        await service!.StopAsync(cancellation);service.ConfirmStopped();
        if(InstallBackup.BoundTo(MigrationBackup,state))DatabaseBackup.Restore(DataPath,MigrationBackup);
        else InstallBackup.Create(DataPath,MigrationBackup,state);
    }
    public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullStart;
        var network=new NativeLanInstall(state!);
        if(resumed){await service!.StopAsync(cancellation);network.RecoverWebJournal();}
        await PrepareDataAsync(cancellation);
        service!.EnableStartup();updater!.EnableStartup();progress(new("starting_server",85));await service.StartAsync(cancellation);
        // RUNNING is reported only after the application answered its own health check
        // as this app, at the schema this app writes.
        using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        var target=Package.Manifest.App;
        if(!await AskAsync(client,LocalUrl+"health/ready",seen=>UpgradeRules.HealthPasses(new(seen.GetProperty("ok").GetBoolean(),seen.GetProperty("version").GetString(),seen.GetProperty("databaseSchema").GetInt32()),target.Version,target.DatabaseSchema),cancellation))
            throw new IOException("SERVER_NOT_READY");
        reportedSchema=target.DatabaseSchema;
        Task<string?> SetupState()=>AskAsync(client,LocalUrl+"api/setup/status",seen=>seen.GetProperty("state").GetString(),cancellation);
        var setup=await SetupState();
        if(setup=="complete")setupToken=null;
        else if(resumed&&setup=="closed")
        {
            // An expired first-owner capability is renewed only while stopped.
            await service.StopAsync(cancellation);RenewCapability();await service.StartAsync(cancellation);
            if(await SetupState()!="ready")throw new IOException("SERVER_NOT_READY");
        }
        else if(resumed&&setup=="ready")setupToken=NativeJson.Read(NativeInstallFiles.ReadRecord(Capability),SetupJsonContext.Default.JsonElement).GetProperty("token").GetString();
        else if(setup!="ready")throw new IOException("SERVER_NOT_READY");
        await updater.StartAsync(cancellation);
        var lan=await network.ObserveAsync(cancellation);
        // The Runtime reads home-name.json only when it starts: after the name is committed it is started again.
        async Task RestartRuntimeAsync(CancellationToken restart)
        {
            await service.StopAsync(restart);await service.StartAsync(restart);
            if(!await AskAsync(client,LocalUrl+"health/ready",seen=>UpgradeRules.HealthPasses(new(seen.GetProperty("ok").GetBoolean(),seen.GetProperty("version").GetString(),seen.GetProperty("databaseSchema").GetInt32()),target.Version,target.DatabaseSchema),restart))
                throw new IOException("SERVER_NOT_READY");
        }
        if(lan is not null&&await network.HomeNameAsync(homeNameChoice,homeNameLabel,RestartRuntimeAsync,cancellation) is {} name)lan=lan with{HomeName=name.Name,HomeNameFailure=name.Failure};
        return new(true,"READY",LocalUrl,setupToken,Lan:lan);
    }
    // One question to the Runtime just started. However it fails to answer as this app
    // (refused, the client's own 10-second timeout, an error status, a body that is not
    // the expected record) it is not ready: SERVER_NOT_READY, with the cause kept for
    // the detail. Only the operation's own deadline stays INSTALL_TIMEOUT.
    internal static async Task<T> AskAsync<T>(HttpClient client,string url,Func<JsonElement,T> read,CancellationToken cancellation)
    {
        try
        {
            using var response=await client.GetAsync(url,cancellation);response.EnsureSuccessStatusCode();
            var bytes=await response.Content.ReadAsByteArrayAsync(cancellation);if(bytes.Length>16384)throw new IOException("SERVER_NOT_READY");
            return read(NativeJson.Read(bytes,SetupJsonContext.Default.JsonElement));
        }
        catch(Exception error)when(!cancellation.IsCancellationRequested&&error is not IOException{Message:"SERVER_NOT_READY"}&&
            error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException or FormatException or IOException)
        {throw new IOException("SERVER_NOT_READY",error);}
    }
    // The only place a first install records "committed", with the schema the Runtime
    // reported healthy. The migration copy is no longer the data's only safe state.
    public Task CommitAsync(Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        var schema=reportedSchema??throw new IOException("SERVER_NOT_READY");
        if(!committed){state=state! with{Active=state.Active with{App=state.Active.App with{DatabaseSchema=schema}}};Save("committed");}
        UpgradeFiles.Remove(MigrationBackup);
    },cancellation);

    private static void SaveRemoval(RemovalIntent intent)
    {
        var path=InstallLocations.RemovalStateFile;
        foreach(var partial in Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".partial-*"))File.Delete(partial);
        NativeInstallFiles.Publish(path,intent.Encode());
    }
    public async Task<SetupResult> UninstallAsync(Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData)
    {
        operationPhase=UninstallPhase.FullUninstall;
        // Payload-free (4.4): everything removed is named by the record or the receipt.
        // A pre-pair layout is refused before anything is written.
        ExistingInstallation.RequireNotLegacy();
        if(!File.Exists(InstallLocations.RemovalStateFile)&&!File.Exists(InstallLocations.StateFile))throw new IOException("INSTALL_STATE_INVALID");
        RemovalIntent? intent=File.Exists(InstallLocations.RemovalStateFile)?RemovalIntent.Read(InstallLocations.RemovalStateFile):null;
        var recorded=File.Exists(InstallLocations.StateFile)?ReadState():null;
        if(File.Exists(InstallLocations.UpgradeStateFile))throw new IOException("UPGRADE_INCOMPLETE");
        if(intent is not null)
        {
            state=intent.State;
            if(intent.Mode!=mode)throw new IOException("UNINSTALL_MODE_CHANGED");
            if(recorded is not null)intent.RequireSameInstallation(recorded);
        }
        else state=recorded!;
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
            service=await NativeService.OpenForRemovalAsync(Infrastructure,cancellation);
            updater=await NativeService.OpenForRemovalAsync(Infrastructure,cancellation,ServiceSpec.Updater);
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
                    _=InspectBinaries(Inventory(),state.Phase is "committed" or "provisioned",true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
                    if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))_=RemovalTree.Inspect(InstallLocations.State,WindowsFileBoundary.RequireNoLink);
                    operationPhase=UninstallPhase.WriteRemovalReceipt;
                    state=state with{Phase=UninstallChoice.Phase(mode)};intent=new(InstallerState.CurrentFormat,state,mode,"prepared");SaveRemoval(intent);Save(state.Phase);
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
                // Windows deletes a marked service once its last handle closes; 15 seconds
                // without that is Windows still releasing it, not the operation's deadline.
                using(var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(15));
                    try{while(AnyServiceExists())await Task.Delay(100,wait.Token);}
                    catch(OperationCanceledException error)when(!cancellation.IsCancellationRequested){throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING",error);}
                }
                operationPhase=UninstallPhase.SaveRemovalState;intent=intent with{Stage="services_removed"};SaveRemoval(intent);
            }
        }
        if(!intent!.AtLeast("data_removed"))
        {
            if(mode==UninstallMode.DeleteData)
            {
                if(Directory.Exists(InstallLocations.State))
                {
                    operationPhase=UninstallPhase.DeleteData;
                    var tree=RemovalTree.Inspect(InstallLocations.State,WindowsFileBoundary.RequireNoLink);
                    tree.DeleteContents(PrivateCopy.DeleteMember,p=>progress(new("deleting_data",25+p*50/100)),cancellation);
                    operationPhase=UninstallPhase.DeleteDataRoot;WindowsFileBoundary.DeleteNoFollow(InstallLocations.State,true);
                }
            }
            else if(mode==UninstallMode.KeepData)
            {
                // The mode stays with the data; a downloaded release does not. An uncommitted
                // reinstall may have begun a migration while its record still names the kept
                // schema: the data goes back to that attempt's copy, then no copy is kept.
                operationPhase=UninstallPhase.SavePreservedState;UpgradeFiles.Remove(Path.Combine(InstalledLayout.UpdaterRoot,"staging"));
                if(InstallBackup.BoundTo(MigrationBackup,state))DatabaseBackup.Restore(DataPath,MigrationBackup);
                UpgradeFiles.Remove(MigrationBackup);Save("uninstalled");
            }
            intent=intent with{Stage="data_removed"};SaveRemoval(intent);
        }
        if(!intent.AtLeast("binaries_removed"))
        {
            operationPhase=UninstallPhase.DeleteBinaries;
            var(tree,_)=InspectBinaries(intent.Binaries,false,true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
            if(tree is not null)
            {
                tree.DeleteContents(PrivateCopy.DeleteMember,p=>progress(new("deleting_files",75+p*20/100)),cancellation);
                WindowsFileBoundary.DeleteNoFollow(InstallLocations.Binaries,true);
            }
            intent=intent with{Stage="binaries_removed"};SaveRemoval(intent);
        }
        // Setup's own failure records go with the family's data. Removed here, after every
        // step that can fail and record one, and on every resumed attempt: a failure after
        // data_removed writes a new record, which the retry that completes removal deletes.
        if(mode==UninstallMode.DeleteData){operationPhase=UninstallPhase.DeleteDataRoot;SetupFailureLog.Remove(cancellation);}
        operationPhase=UninstallPhase.RemoveRegistration;Registry.LocalMachine.DeleteSubKey(RegistryKey,false);
        // The protected receipt is deleted last: a failed final step still has
        // the complete owner, mode and inventory for the same bounded retry.
        operationPhase=UninstallPhase.DeleteReceipt;File.Delete(InstallLocations.RemovalStateFile);
        progress(new("removed",100));
        return new(true,mode==UninstallMode.DeleteData?"REMOVED_ALL":"REMOVED",LocalUrl,DataPreserved:mode==UninstallMode.KeepData);
    }
    public void Dispose(){service?.Dispose();service=null;updater?.Dispose();updater=null;}
}
