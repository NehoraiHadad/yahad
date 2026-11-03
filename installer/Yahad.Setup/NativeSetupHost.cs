using System.IO;
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

// The runtime-only installer: one Windows service, Yahad.Acceptance, running
// %ProgramFiles%\Yahad.FirstInstall\versions\<version>\host\Yahad.ServiceHost.exe.
// No method is entered without the elevated in-process Setup operation, its
// recorded owner and the global install lock.
internal sealed class NativeSetupHost(InstallPackage package,UpdateSetupChoice? requested):ISetupHostOperations,ILanSetupHostOperations,IDisposable
{
    private const string RegistryKey=@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall";
    private InstallerState? state;private NativeRuntimeService? service;private string? setupToken;private bool existing;
    private bool diagnosticsAllowed;private UninstallPhase operationPhase=UninstallPhase.FullPrepare;
    private static SecurityIdentifier Application=>new(InstallLocations.ServiceSid);
    // The state root, the binary root and every immutable member are readable
    // by the Runtime service only; data and temporary are its only writable trees.
    private static string Immutable=>NativeInstallFiles.Descriptor(InstallLocations.ServiceSid);
    private static string Mutable=>NativeInstallFiles.Descriptor(InstallLocations.ServiceSid,true);
    private static string ImmutableFile=>NativeInstallFiles.FileDescriptor(InstallLocations.ServiceSid);
    private static string AdminFile=>NativeInstallFiles.FileDescriptor();
    private string Dataset=>Path.Combine(InstallLocations.State,"datasets",state!.DataId);
    private string DataPath=>Path.Combine(Dataset,"data");
    private string MasterKey=>Path.Combine(Dataset,"master-key");
    private static string Secrets=>Path.Combine(InstallLocations.State,"Secrets");
    private static string Capability=>Path.Combine(Secrets,"setup-capability.json");
    private string LocalUrl=>"http://127.0.0.1:"+state!.Port+"/";
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    internal SetupOperationException ReportFailure(Exception cause)
    {var failure=new SetupOperationException(operationPhase,cause);if(diagnosticsAllowed&&state is not null)failure.DiagnosticRecorded=FullInstallDiagnostics.TryRecord(state,failure);return failure;}

    // Formats 3, 4 and 5 are the retired four-service installation. It is never
    // adopted: its own Setup must remove it (keeping data) before this one runs.
    internal static void RequireCurrentFormat(byte[] bytes)
    {
        int format;
        try{using var json=JsonDocument.Parse(bytes);format=json.RootElement.GetProperty("Format").GetInt32();}
        catch(Exception error)when(error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException){throw new IOException("INSTALL_STATE_INVALID");}
        if(InstallerState.LegacyFormats.Contains(format))throw new IOException("LEGACY_INSTALL_UNINSTALL_FIRST");
        if(format!=InstallerState.CurrentFormat)throw new IOException("INSTALL_STATE_INVALID");
    }
    internal static InstallerState ReadState()
    {
        using var file=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.StateFile);var bytes=file.Small(16777216);
        RequireCurrentFormat(bytes);var value=NativeJson.Read<InstallerState>(bytes);value.Require();return value;
    }
    private void Save(string phase)
    {state=state! with{Phase=phase};state.Require();NativeInstallFiles.PublishProtectedFile(InstallLocations.StateFile,Json(state),AdminFile);}

    public Task PrepareAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        NativeInstallFiles.RequireAdministrator();requested?.Require(DateTimeOffset.UtcNow);
        if(File.Exists(InstallLocations.RemovalStateFile))throw new IOException("UNINSTALL_INCOMPLETE");
        SafeDirectory.NoReparseAncestors(InstallLocations.State);SafeDirectory.NoReparseAncestors(InstallLocations.Binaries);
        var manifest=package.Manifest;var version=manifest.Version;InstallLocations.RequireVersion(version);
        if(File.Exists(InstallLocations.StateFile))
        {
            state=ReadState();if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");
            state.Consent.RequireCompatibleRetry(requested);diagnosticsAllowed=true;
            using(WindowsFileBoundary.PinInstallerRemovalRoot(InstallLocations.State,Application)){}
            ClearResidue();
            switch(state.Phase)
            {
                case "committed":
                    if(InstallLocations.Ordered(version)<InstallLocations.Ordered(state.Version))throw new IOException("INSTALL_DOWNGRADE_REFUSED");
                    // An installed release is changed only by removing it with
                    // its data kept and installing the new one; never in place.
                    if(state.Version!=version||state.PackageDigest!=manifest.ZipSha256)throw new IOException("INSTALL_UPGRADE_REQUIRED");
                    service=NativeRuntimeService.Open(version,state.OwnerSid);
                    InspectBinaries(Inventory(),true,true,"INSTALL_TARGET_CHANGED");existing=true;return;
                case "uninstalled":
                    if(InstallLocations.Ordered(version)<InstallLocations.Ordered(state.Version))throw new IOException("INSTALL_DOWNGRADE_REFUSED");
                    if(NativeRuntimeService.Exists()||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries))throw new IOException("INSTALL_COLLISION");
                    // The family, its data, key and identity stay; only the
                    // release and the image that installs it are new.
                    state=state with{Version=version,PackageDigest=manifest.ZipSha256,PayloadDigest=manifest.PayloadDigest,SourceCommit=manifest.SourceCommit,SetupSha256=owner.ImageSha256,Files=manifest.Files};
                    Save("preparing");break;
                case "preparing":
                    if(state.PackageDigest!=manifest.ZipSha256||state.Version!=version)throw new IOException("EXISTING_DATA_PRESERVED");break;
                default:throw new IOException("INSTALL_STATE_INVALID");
            }
        }
        else
        {
            if(requested is null)throw new IOException("UPDATE_CONSENT_REQUIRED");
            if(File.Exists(InstallLocations.State)||Directory.Exists(InstallLocations.State)||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)||NativeRuntimeService.Exists())throw new IOException("INSTALL_COLLISION");
            var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();var port=Enumerable.Range(47123,17).FirstOrDefault(p=>!listeners.Any(e=>e.Port==p));if(port==0)throw new IOException("NO_LOCAL_PORT");
            var installation=Guid.NewGuid().ToString("N");
            state=new(InstallerState.CurrentFormat,installation,Guid.NewGuid().ToString("N"),version,manifest.ZipSha256,manifest.PayloadDigest,manifest.SourceCommit,owner.Sid,port,"preparing",
                owner.ImageSha256,FirstOwnerBinding.Create(installation,owner.Sid),requested,manifest.Files,InstallLocations.ServiceSid);
            NativeInstallFiles.NewDirectory(InstallLocations.State,Immutable);Save("preparing");diagnosticsAllowed=true;
        }
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.Binaries)!).AvailableFreeSpace<checked(manifest.Files.Sum(f=>f.Size)*3+536870912L))throw new IOException("DISK_SPACE");
        if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,Immutable);
        else{using(WindowsFileBoundary.PinInstallerRemovalRoot(InstallLocations.Binaries,Application)){}}
        // A resumed extraction holds only this package's members and their
        // uncommitted partials; the partials go, committed members stay.
        var (_,partials)=InspectBinaries(Inventory(),false,false,"INSTALL_TARGET_CHANGED");
        foreach(var partial in partials)WindowsFileBoundary.DeleteRemovalMember(Path.Combine(InstallLocations.Binaries,partial.Replace('/',Path.DirectorySeparatorChar)),false,Application);
        var root=InstallLocations.VersionRoot(version);NativeInstallFiles.EnsureStageDirectory(InstallLocations.Binaries,root,Immutable);
        package.Extract(root,p=>progress(new("extracting",10+p*50/100)),cancellation,new ExtractTarget(
            directory=>NativeInstallFiles.EnsureStageDirectory(root,directory,Immutable),path=>NativeInstallFiles.CreateNewFile(path,ImmutableFile),
            NativeInstallFiles.CommitNew,VerifyOwned));
    },cancellation);

    private InstallMember[] Inventory()=>RemovalIntent.Inventory(state!,File.Exists(InstallLocations.InstallerImage)?new FileInfo(InstallLocations.InstallerImage).Length:0);
    private static readonly string EmptySha256=Convert.ToHexStringLower(SHA256.HashData([]));
    // A present immutable member must be administrator-owned, read-only to the
    // Runtime and byte-identical to its record.
    private static bool VerifyOwned(string path,InstallMember record)
    {
        using var file=WindowsFileBoundary.OpenBootstrapForInstaller(path,Application);
        return file.Length==record.Size&&(record.Size==0?record.Sha256==EmptySha256:file.Sha256(record.Size)==record.Sha256);
    }
    // The binary root holds exactly the recorded members (or, when not
    // complete, a subset with their owned partials). Anything else refuses.
    private static (RemovalTree? Tree,string[] Partials) InspectBinaries(InstallMember[] inventory,bool complete,bool verify,string code)
    {
        if(!Directory.Exists(InstallLocations.Binaries)){if(complete)throw new IOException(code);return(null,[]);}
        var tree=RemovalTree.Inspect(InstallLocations.Binaries,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,Application));
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
    {foreach(var directory in new[]{InstallLocations.State,Secrets,Dataset})FullInstallerResidue.Clear(directory,Application);}
    private static void EnsureDirectory(string path,bool mutable)
    {
        if(!Directory.Exists(path)){NativeInstallFiles.NewDirectory(path,mutable?Mutable:Immutable);return;}
        if(mutable)WindowsFileBoundary.ValidateRemovalMember(path,true,Application);else{using(WindowsFileBoundary.PinInstallerRemovalRoot(path,Application)){}}
    }

    public Task PublishServiceAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        operationPhase=UninstallPhase.FullProvision;
        if(existing)return;
        if(state?.Phase!="preparing")throw new IOException("INSTALL_NOT_PREPARED");
        EnsureDirectory(Path.Combine(InstallLocations.State,"datasets"),false);EnsureDirectory(Dataset,false);EnsureDirectory(DataPath,true);
        EnsureDirectory(Path.Combine(InstallLocations.State,"temporary"),true);EnsureDirectory(Secrets,false);
        if(!File.Exists(MasterKey))NativeInstallFiles.PublishNewProtectedFile(MasterKey,Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))),ImmutableFile);
        if(Convert.FromBase64String(Encoding.UTF8.GetString(NativeInstallFiles.ReadOwned(MasterKey,InstallLocations.ServiceSid))).Length!=32)throw new IOException("INSTALL_KEY_INVALID");
        if(File.Exists(InstallLocations.LaunchFile))
        {
            // A kept installation keeps its launch file; it must still be this one.
            var launch=InstalledLaunch.Parse(NativeInstallFiles.ReadOwned(InstallLocations.LaunchFile,InstallLocations.ServiceSid));
            if(launch.InstallationId!=state.InstallationId||launch.DataId!=state.DataId||launch.Port!=state.Port)throw new IOException("INSTALL_STATE_INVALID");
        }
        else NativeInstallFiles.PublishNewProtectedFile(InstallLocations.LaunchFile,InstalledLaunch.Encode(new(1,state.InstallationId,state.DataId,state.Port,"http://127.0.0.1:"+state.Port)),ImmutableFile);
        RenewCapability();
        var setup=new InstallMember(RemovalIntent.SetupMember,new FileInfo(Environment.ProcessPath!).Length,state.SetupSha256);
        if(File.Exists(InstallLocations.InstallerImage)){if(!VerifyOwned(InstallLocations.InstallerImage,setup))throw new IOException("INSTALL_TARGET_CHANGED");}
        else CopyVerified(Environment.ProcessPath!,InstallLocations.InstallerImage,setup);
        progress(new("installing_service",75));
        service??=NativeRuntimeService.Exists()?NativeRuntimeService.OpenForProvisioning(state.Version,state.OwnerSid):NativeRuntimeService.Create(state.Version,state.OwnerSid);
        Register();Save("committed");progress(new("installing_service",80));
    },cancellation);
    private static void CopyVerified(string source,string target,InstallMember record)
    {
        var partial=target+".partial-"+Guid.NewGuid().ToString("N");
        try
        {
            using(var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read))using(var output=NativeInstallFiles.CreateNewFile(partial,ImmutableFile))
            {
                if(input.Length!=record.Size||Convert.ToHexStringLower(SHA256.HashData(input))!=record.Sha256)throw new IOException("INSTALL_TARGET_CHANGED");
                input.Position=0;input.CopyTo(output);output.Flush(true);
            }
            NativeInstallFiles.CommitNew(partial,target);
        }
        catch{try{File.Delete(partial);}catch(IOException){}catch(UnauthorizedAccessException){}throw;}
        if(!VerifyOwned(target,record))throw new IOException("INSTALL_TARGET_CHANGED");
    }
    private void RenewCapability()
    {
        setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        NativeInstallFiles.PublishProtectedFile(Capability,Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds(),firstOwner=state!.FirstOwner}),ImmutableFile);
    }
    private void Register()
    {
        using var key=Registry.LocalMachine.CreateSubKey(RegistryKey,true);key.SetValue("DisplayName","יחד");key.SetValue("DisplayVersion",state!.Version);key.SetValue("Publisher","Yahad");
        key.SetValue("InstallLocation",InstallLocations.Binaries);key.SetValue("UninstallString","\""+InstallLocations.InstallerImage+"\" --uninstall");
        key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }

    public async Task ConfigureLanAsync(SetupOwner owner,LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullNetwork;if(state?.OwnerSid!=owner.Sid)throw new IOException("INSTALL_OWNER_CHANGED");
        if(state.Phase!="committed"||service is null)throw new IOException("INSTALL_NOT_COMMITTED");
        var network=new NativeLanInstall(state);network.Preflight(request);
        // The launch file changes only while the Runtime is stopped.
        await service.StopAsync(cancellation);network.RecoverConfigurationOnly();await network.ConfigureAsync(request,progress,cancellation);
    }
    public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullStart;if(state?.Phase!="committed"||service is null)throw new IOException("INSTALL_NOT_COMMITTED");
        var network=new NativeLanInstall(state);
        if(existing){await service.StopAsync(cancellation);network.RecoverConfigurationOnly();}
        service.EnableStartup();progress(new("starting_server",85));await service.StartAsync(cancellation);
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
        else if(existing&&setup=="ready")setupToken=NativeJson.Read<JsonElement>(NativeInstallFiles.ReadOwned(Capability,InstallLocations.ServiceSid)).GetProperty("token").GetString();
        else if(setup!="ready")throw new IOException("SERVER_NOT_READY");
        var lan=await network.ObserveAsync(cancellation);FullInstallDiagnostics.DeleteOwned(state);
        return new(true,"READY",LocalUrl,setupToken,Lan:lan,FullProfileInstallationId:state.InstallationId);
    }

    private static void SaveRemoval(RemovalIntent intent)
    {
        intent.Require();var path=InstallLocations.RemovalStateFile;var prefix=Path.GetFileName(path)+".partial-";
        var partials=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,prefix+"*").Take(33).ToArray();if(partials.Length>32)throw new IOException("UNINSTALL_INTENT_INVALID");
        foreach(var partial in partials)
        {
            var suffix=Path.GetFileName(partial)[prefix.Length..];if(!Guid.TryParseExact(suffix,"N",out var id)||id.ToString("N")!=suffix)throw new IOException("UNINSTALL_INTENT_INVALID");
            using(WindowsFileBoundary.OpenInstallerMetadata(partial)){}File.Delete(partial);
        }
        NativeInstallFiles.PublishProtectedFile(path,Json(intent),AdminFile);
    }
    private static void ValidateStateMember(string path,bool directory,WindowsFileBoundary.DataLease? data)
    {
        // The held process.lock cannot be opened again; its identity and ACL
        // are checked through the lease itself.
        if(!directory&&data?.TryValidateHeldGate(path,acl=>WindowsFileBoundary.ValidateAcl(acl,Application,false))==true)return;
        WindowsFileBoundary.ValidateRemovalMember(path,directory,Application);
    }
    public async Task<SetupResult> UninstallAsync(SetupOwner owner,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData)
    {
        operationPhase=UninstallPhase.FullUninstall;NativeInstallFiles.RequireAdministrator();UninstallChoice.Require(mode);
        RemovalIntent? intent=null;
        if(File.Exists(InstallLocations.RemovalStateFile))
        {
            using(var receipt=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.RemovalStateFile))
            {var bytes=receipt.Small(16777216);RequireCurrentFormat(bytes);intent=NativeJson.Read<RemovalIntent>(bytes);}
            intent.Require();state=intent.State;
            if(intent.Mode!=mode)throw new IOException("UNINSTALL_MODE_CHANGED");
            if(File.Exists(InstallLocations.StateFile))intent.RequireSameInstallation(ReadState());
        }
        else state=ReadState();
        if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");diagnosticsAllowed=true;
        if(intent is null&&state.Phase.StartsWith("removing_",StringComparison.Ordinal))throw new IOException("INSTALL_STATE_INVALID");
        if(intent is null&&state.Phase=="uninstalled")
        {
            if(NativeRuntimeService.Exists()||Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries))throw new IOException("INSTALL_COLLISION");
            if(mode==UninstallMode.KeepData)return new(true,"REMOVED",LocalUrl,FullProfileInstallationId:state.InstallationId);
        }
        operationPhase=UninstallPhase.ValidateRegistration;
        using(var registration=Registry.LocalMachine.OpenSubKey(RegistryKey))
        {if(registration is not null&&registration.GetValue("InstallLocation") as string!=InstallLocations.Binaries)throw new IOException("UNINSTALL_REGISTRATION_CHANGED");}
        if(intent?.AtLeast("services_removed")==true){if(NativeRuntimeService.Exists())throw new IOException("UNINSTALL_SERVICE_REAPPEARED");}
        else
        {
            service=await NativeRuntimeService.OpenForRemovalAsync(state.Version,state.OwnerSid,cancellation);
            operationPhase=UninstallPhase.StopService;
            if(service is not null){service.FenceForRemoval();service.RequestStop();await service.StopForRemovalAsync(cancellation);}
            new NativeLanInstall(state).RecoverConfigurationOnly();progress(new("stopping_server",10));
            operationPhase=UninstallPhase.AcquireDataLease;
            // STOPPED is not proof of writer cleanup: the data lease is held
            // across every check and destructive step that follows.
            using(var data=Directory.Exists(DataPath)?WindowsFileBoundary.AcquireInstallerDataLease(DataPath,Application):null)
            {
                service?.RequireFencedStoppedForRemoval();
                if(intent is null)
                {
                    operationPhase=UninstallPhase.InspectData;
                    var inventory=Inventory();_=InspectBinaries(inventory,state.Phase=="committed",true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
                    if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))_=RemovalTree.Inspect(InstallLocations.State,(path,directory)=>ValidateStateMember(path,directory,data));
                    operationPhase=UninstallPhase.WriteRemovalReceipt;
                    state=state with{Phase=UninstallChoice.Phase(mode)};intent=new(InstallerState.CurrentFormat,state,mode,inventory,"prepared");SaveRemoval(intent);Save(state.Phase);
                }
                if(!intent.AtLeast("lan_removed"))
                {
                    operationPhase=UninstallPhase.RemoveLan;_=await new NativeLanInstall(state).UninstallAsync(mode,cancellation);
                    intent=intent with{Stage="lan_removed"};SaveRemoval(intent);
                }
                operationPhase=UninstallPhase.RemoveService;
                if(service is not null){service.RequireFencedStoppedForRemoval();service.Delete();service.Dispose();service=null;}
                operationPhase=UninstallPhase.WaitForServiceRemoval;
                using(var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {wait.CancelAfter(TimeSpan.FromSeconds(15));while(NativeRuntimeService.Exists())await Task.Delay(100,wait.Token);}
                operationPhase=UninstallPhase.SaveRemovalState;intent=intent with{Stage="services_removed"};SaveRemoval(intent);
            }
        }
        if(!intent!.AtLeast("data_removed"))
        {
            if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))
            {
                operationPhase=UninstallPhase.DeleteData;
                var tree=RemovalTree.Inspect(InstallLocations.State,(path,directory)=>WindowsFileBoundary.ValidateRemovalMember(path,directory,Application));
                tree.DeleteContents((path,directory)=>WindowsFileBoundary.DeleteRemovalMember(path,directory,Application),p=>progress(new("deleting_data",25+p*50/100)),cancellation);
                operationPhase=UninstallPhase.DeleteDataRoot;WindowsFileBoundary.DeleteRemovalMember(InstallLocations.State,true,Application);
            }
            else if(mode==UninstallMode.KeepData){operationPhase=UninstallPhase.SavePreservedState;Save("uninstalled");}
            intent=intent with{Stage="data_removed"};SaveRemoval(intent);
        }
        if(!intent.AtLeast("binaries_removed"))
        {
            operationPhase=UninstallPhase.DeleteBinaries;
            var(tree,_)=InspectBinaries(intent.Binaries,false,true,"UNINSTALL_UNOWNED_FILE_PRESERVED");
            if(tree is not null)
            {
                tree.DeleteContents((path,directory)=>WindowsFileBoundary.DeleteRemovalMember(path,directory,Application),p=>progress(new("deleting_files",75+p*20/100)),cancellation);
                WindowsFileBoundary.DeleteRemovalMember(InstallLocations.Binaries,true,Application);
            }
            intent=intent with{Stage="binaries_removed"};SaveRemoval(intent);
        }
        operationPhase=UninstallPhase.RemoveRegistration;Registry.LocalMachine.DeleteSubKey(RegistryKey,false);
        // The protected receipt is deleted last: a failed final step still has
        // the complete owner, mode and inventory for the same bounded retry.
        operationPhase=UninstallPhase.DeleteDiagnostic;FullInstallDiagnostics.DeleteOwned(state);
        operationPhase=UninstallPhase.DeleteReceipt;using(WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.RemovalStateFile)){}File.Delete(InstallLocations.RemovalStateFile);diagnosticsAllowed=false;
        progress(new("removed",100));
        return new(true,mode==UninstallMode.DeleteData?"REMOVED_ALL":"REMOVED",LocalUrl,DataPreserved:mode==UninstallMode.KeepData,FullProfileInstallationId:state.InstallationId);
    }
    public void Dispose(){service?.Dispose();service=null;}
}
