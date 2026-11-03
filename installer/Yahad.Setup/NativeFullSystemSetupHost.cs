using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

// Separate full-profile adapter. The legacy receipt/uninstall path is not
// reinterpreted as updater enrollment. No method is entered without the normal
// elevated installer, authenticated original Owner and global install lock.
internal sealed class NativeFullSystemSetupHost(InstallPackage package,UpdateSetupChoice? requested):ISetupHostOperations,ILanSetupHostOperations,IDisposable
{
    private FullInstallState? state;private FullInstallFiles? files,binaries;private SystemCandidateDescriptor? system;
    private readonly Dictionary<FullServiceKind,NativeFullService> services=[];private string? staging,setupToken;private bool existing,restoring;
    private WindowsFileBoundary.DataLease? maintenance;
    private InstallerReadinessRequest? readiness;
    private bool diagnosticsAllowed;private UninstallPhase operationPhase=UninstallPhase.FullPrepare;
    internal SetupOperationException ReportFailure(Exception cause)
    {var failure=new SetupOperationException(operationPhase,cause);if(diagnosticsAllowed&&state is not null)failure.DiagnosticRecorded=FullInstallDiagnostics.TryRecord(state,failure);return failure;}
    private static string Hash(byte[] value)=>Convert.ToHexStringLower(SHA256.HashData(value));
    private static byte[] Json<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value);
    private static readonly FullServiceKind[] Kinds=[FullServiceKind.Runtime,FullServiceKind.Authority,FullServiceKind.RecoveryA,FullServiceKind.RecoveryB];
    private static FullInstallState ReadState(string? selected=null)
    {
        using var file=WindowsFileBoundary.OpenInstallerMetadata(selected??InstallLocations.StateFile);var bytes=file.Small(16777216);
        using var json=JsonDocument.Parse(bytes);if(json.RootElement.GetProperty("Format").GetInt32()!=3)throw new IOException("UPDATE_LEGACY_PROFILE_REQUIRES_REMOVAL");
        var value=NativeJson.Read<FullInstallState>(bytes);ValidateState(value);return value;
    }
    internal static void ValidateState(FullInstallState s)
    {
        InstalledActivationResolver.Id(s.InstallationId);InstalledActivationResolver.Id(s.DataId);InstalledActivationResolver.Digest(s.PackageDigest);InstalledActivationResolver.Digest(s.PayloadDigest);InstalledActivationResolver.Digest(s.SystemManifestDigest);InstalledActivationResolver.Digest(s.SetupSha256);
        if(s.Format!=3||!s.OwnerSid.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!new SecurityIdentifier(s.OwnerSid).IsAccountSid()||s.Port is <1024 or >65535||s.Schema is <1 or >64||!System.Text.RegularExpressions.Regex.IsMatch(s.SourceCommit,"^[a-f0-9]{40}$")||s.Files.Length is <10 or >50000||s.Phase is not("preparing" or "committed" or "uninstalled" or "restoring" or "removing_keep_data" or "removing_all_data"))throw new IOException("UPDATE_INSTALL_STATE");
        s.FirstOwner.Require(s.InstallationId,s.OwnerSid);if(s.Consent.ConsentVersion!=2||!s.Consent.Accepted||s.Consent.ApprovedUnix<=0)throw new IOException("UPDATE_CONSENT_REQUIRED");
        if((s.RuntimeSid is null)!=(s.AuthoritySid is null))throw new IOException("UPDATE_INSTALL_ROLES");
        if(s.RuntimeSid is not null&&(s.AuthoritySid==s.RuntimeSid||!s.RuntimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||!s.AuthoritySid!.StartsWith("S-1-5-80-",StringComparison.Ordinal)))throw new IOException("UPDATE_INSTALL_ROLES");
        foreach(var f in s.Files){InstallPackage.Name(f.Path);InstalledActivationResolver.Digest(f.Sha256);if(f.Size<0)throw new IOException("UPDATE_INSTALL_FILES");}
        if(s.PolicyDigest is not null)InstalledActivationResolver.Digest(s.PolicyDigest);
        if(s.PlanDigest is not null)InstalledActivationResolver.Digest(s.PlanDigest);
        if(s.PreservedBinaries is { } retained){if(retained.Length is <1 or >50000)throw new IOException("UPDATE_INSTALL_FILES");foreach(var f in retained){FullInstallFiles.BinaryMember(f.Path);InstalledActivationResolver.Digest(f.Sha256);if(f.Size<0||f.Size>FullInstallFiles.MaximumBinaryBytes(f.Path))throw new IOException("UPDATE_INSTALL_FILES");}}
    }
    private void Save(string phase)
    {
        state=state! with{Phase=phase};ValidateState(state);
        var descriptor=NativeInstallFiles.Descriptor();
        if(state.RuntimeSid is not null)descriptor=FullUpdateInstallerPlan.Descriptor(new(new(state.AuthoritySid!),new(state.RuntimeSid)),InstalledFileRole.Anchor,false);
        NativeInstallFiles.PublishProtectedFile(InstallLocations.StateFile,Json(state),descriptor);
    }
    private static string? PendingStage()
    {
        var stages=Directory.EnumerateFileSystemEntries(InstallLocations.State,"setup-stage-*").Take(2).ToArray();
        if(stages.Length>1)throw new IOException("INSTALL_STAGING_CHANGED");if(stages.Length==0)return null;
        var name=Path.GetFileName(stages[0]);if(!Guid.TryParseExact(name[12..],"N",out var id)||name!="setup-stage-"+id.ToString("N"))throw new IOException("INSTALL_STAGING_CHANGED");
        return stages[0];
    }
    public Task PrepareAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(()=>
    {
        NativeInstallFiles.RequireAdministrator();requested?.Require(DateTimeOffset.UtcNow);
        if(File.Exists(InstallLocations.RemovalStateFile))throw new IOException("UNINSTALL_INCOMPLETE");
        SafeDirectory.NoReparseAncestors(InstallLocations.State);SafeDirectory.NoReparseAncestors(InstallLocations.Binaries);
        if(Directory.Exists(InstallLocations.State))
        {
            state=ReadState();if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");state.Consent.RequireCompatibleRetry(requested);diagnosticsAllowed=true;
            if(state.Phase=="committed")
            {
                RequireRoles();OpenServices();
                if(NativeLanInstall.PendingFullApplication(state,files!) is null)RequireCurrent();else{RequireExistingEnrollment();_=CurrentBootstrap();}
                existing=true;return;
            }
            if(state.Phase is "uninstalled" or "restoring")
            {
                if(state.PreservedBinaries is null||state.Phase=="uninstalled"&&(Kinds.Any(NativeFullService.Exists)||Directory.Exists(InstallLocations.Binaries)))throw new IOException("UPDATE_RESTORE_COLLISION");
                RequireRoles();Save("restoring");restoring=true;return;
            }
            if(state.Phase!="preparing"||state.PackageDigest!=package.Manifest.ZipSha256)throw new IOException("EXISTING_DATA_PRESERVED");
            if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,NativeInstallFiles.Descriptor());
        }
        else
        {
            if(requested is null)throw new IOException("UPDATE_CONSENT_REQUIRED");
            if(Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)||Kinds.Any(NativeFullService.Exists))throw new IOException("INSTALL_COLLISION");
            system=FullUpdateInstallerPlan.ReadPackage(package);
            var listeners=IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners();var port=Enumerable.Range(47123,17).FirstOrDefault(p=>!listeners.Any(e=>e.Port==p));if(port==0)throw new IOException("NO_LOCAL_PORT");
            var installation=Guid.NewGuid().ToString("N");state=new(3,installation,Guid.NewGuid().ToString("N"),package.Manifest.ZipSha256,package.Manifest.PayloadDigest,package.Manifest.SourceCommit,system.SystemManifestDigest,owner.Sid,port,system.DatabaseTargetSchema,"preparing",owner.ImageSha256,FirstOwnerBinding.Create(installation,owner.Sid),requested,package.Manifest.Files);
            NativeInstallFiles.NewDirectory(InstallLocations.State,NativeInstallFiles.Descriptor());Save("preparing");
            diagnosticsAllowed=true;
            NativeInstallFiles.NewDirectory(InstallLocations.Binaries,NativeInstallFiles.Descriptor());
        }
        system??=FullUpdateInstallerPlan.ReadPackage(package);
        if(new DriveInfo(Path.GetPathRoot(InstallLocations.State)!).AvailableFreeSpace<checked(package.Manifest.Files.Sum(f=>f.Size)*3+536870912L))throw new IOException("DISK_SPACE");
        var pending=PendingStage();
        if(pending is not null)
        {
            staging=pending;package.VerifyStagingInventory(staging,cancellation);
            package.Extract(staging,p=>progress(new("extracting",10+p*50/100)),cancellation,dir=>NativeInstallFiles.EnsureStageDirectory(staging,dir,NativeInstallFiles.Descriptor()),NativeInstallFiles.CreateNewFile,true);return;
        }
        staging=Path.Combine(InstallLocations.State,"setup-stage-"+Guid.NewGuid().ToString("N"));NativeInstallFiles.NewDirectory(staging,NativeInstallFiles.Descriptor());
        package.Extract(staging,p=>progress(new("extracting",10+p*50/100)),cancellation,dir=>NativeInstallFiles.EnsureStageDirectory(staging,dir,NativeInstallFiles.Descriptor()),NativeInstallFiles.CreateNewFile);
    },cancellation);
    private void OpenServices()
    {foreach(var kind in Kinds)if(!services.ContainsKey(kind)){var service=NativeFullService.Open(kind);try{service.RequireProtection(state!.OwnerSid,state.AuthoritySid!);services.Add(kind,service);}catch{service.Dispose();throw;}}}
    private void ProvisionServices()
    {
        foreach(var kind in Kinds)
        {
            if(services.ContainsKey(kind))continue;
            services.Add(kind,NativeFullService.Exists(kind)?NativeFullService.OpenForProvisioning(kind,state!.OwnerSid,state.AuthoritySid):NativeFullService.Create(kind));
        }
        var runtime=NativeFullService.Sid(FullServiceKind.Runtime);var authority=NativeFullService.Sid(FullServiceKind.Authority);
        foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())
            if(NativeFullService.Sid(slot==RecoveryHelperSlot.A?FullServiceKind.RecoveryA:FullServiceKind.RecoveryB)!=UpdaterServicePrincipals.Recovery(slot).Value)
                throw new IOException("UPDATE_HELPER_SERVICE_IDENTITY");
        if(state!.RuntimeSid is not null&&(state.RuntimeSid!=runtime||state.AuthoritySid!=authority))throw new IOException("UPDATE_SERVICE_IDENTITY");
        state=state with{RuntimeSid=runtime,AuthoritySid=authority,ServicesCompleted=true};Save(state.Phase);
        foreach(var service in services.Values)service.Protect(state.OwnerSid,authority);
        RequireRoles();
    }
    private void RequireRoles()
    {files=new(state!);binaries=new(state!,true);}
    public Task PublishServiceAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation)=>Task.Run(async()=>
    {
        operationPhase=UninstallPhase.FullProvision;
        if(existing)
        {
            if(state?.PackageDigest==package.Manifest.ZipSha256&&PendingStage() is { } pending)
            {
                AcquireMaintenance();await StopAllAsync(cancellation,true);progress(new("cleaning_installation",80));
                package.CleanVerifiedStaging(pending,p=>progress(new("cleaning_installation",80+p*4/100)),cancellation);
            }
            return;
        }
        if(state is null)throw new IOException("INSTALL_NOT_PREPARED");
        if(restoring){RestoreBinaries(cancellation);ProvisionServices();RequireCurrent();Register();Save("committed");existing=true;return;}
        if(staging is null||system is null)throw new IOException("INSTALL_NOT_STAGED");
        ProvisionServices();
        var copied=0;var copyCount=state.Files.Count(f=>f.Path.StartsWith("payload/",StringComparison.Ordinal))+system.Components.Sum(c=>c.Files.Count*(c.Kind==SystemComponentKind.Helper?3:2))+1;
        void Copied(){if(++copied%100==0||copied==copyCount)progress(new("installing_service",70+copied*9/copyCount));}
        NativeInstallFiles.SetOwnedDirectory(InstallLocations.State,FullUpdateInstallerPlan.Descriptor(files!.Roles,InstalledFileRole.Control,true));
        NativeInstallFiles.SetOwnedDirectory(InstallLocations.Binaries,FullUpdateInstallerPlan.Descriptor(files.Roles,InstalledFileRole.Control,true));
        foreach(var relative in new[]{"datasets","datasets/"+state.DataId,"datasets/"+state.DataId+"/data","temporary","Secrets","intake","grants","authority-temporary",".authority",".authority/update-serial",".authority/updates"})files.EnsureDirectory(relative);
        if(Directory.EnumerateFileSystemEntries(files.PathOf("datasets/"+state.DataId+"/data")).Any(p=>Path.GetFileName(p)!="process.lock"))throw new IOException("EXISTING_DATA_PRESERVED");
        foreach(var member in state.Files.Where(f=>f.Path.StartsWith("payload/",StringComparison.Ordinal)))
        {cancellation.ThrowIfCancellationRequested();binaries!.CopyVerified(Path.Combine(staging,member.Path.Replace('/',Path.DirectorySeparatorChar)),"payloads/"+state.PayloadDigest+"/"+member.Path[8..],member);Copied();}
        foreach(var component in system.Components)
        {
            foreach(var member in component.Files)
            {
                cancellation.ThrowIfCancellationRequested();var original=component.SlotRelativeDirectory+"/"+member.RelativePath;var record=new InstallMember(original,member.Size,member.Sha256);var source=Path.Combine(staging,original.Replace('/',Path.DirectorySeparatorChar));
                binaries!.CopyVerified(source,"system-slots/"+system.SystemManifestDigest+"/"+original,record);
                Copied();
                var destination=component.Kind==SystemComponentKind.Probe?"bootstrap/probe":FixedSystemComponents.ActiveDirectory(component.Kind)!;
                if(component.Kind==SystemComponentKind.Helper)
                    foreach(var slot in new[]{"bootstrap/handoff-a","bootstrap/handoff-b"}){binaries.CopyVerified(source,slot+"/"+member.RelativePath,record);Copied();}
                else{binaries.CopyVerified(source,destination+"/"+member.RelativePath,record);Copied();}
            }
        }
        binaries!.Publish("system-slots/"+system.SystemManifestDigest+"/system-manifest.json",system.ManifestBytes);
        var setup=new InstallMember("Yahad.Setup.exe",new FileInfo(Environment.ProcessPath!).Length,state.SetupSha256);binaries.CopyVerified(Environment.ProcessPath!,"Yahad.Setup.exe",setup);
        Copied();
        var dataset="datasets/"+state.DataId+"/";var keyName=dataset+"master-key";
        if(!File.Exists(files.PathOf(keyName)))files.Publish(keyName,Encoding.UTF8.GetBytes(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
        var key=files.Read(keyName);if(Convert.FromBase64String(Encoding.UTF8.GetString(key)).Length!=32)throw new IOException("INSTALL_KEY_INVALID");
        var policy=Json(new{format=2,authority="installer",profile="full-system-v1",repository=NativeCandidateIntake.Repository,trust="https-github-unsigned"});
        var pin=Json(new{format=1,release=state.PayloadDigest});files.Publish(dataset+"update-policy",policy);files.Publish(dataset+"update-pin",pin);
        FullUpdateInstallPlan plan;byte[] planBytes;
        if(File.Exists(files.PathOf("full-installer-plan.json")))
        {
            planBytes=files.Read("full-installer-plan.json");plan=NativeJson.Read<FullUpdateInstallPlan>(planBytes);
        }
        else
        {
            if(state.PlanDigest is not null||state.PolicyDigest is not null)throw new IOException("UPDATE_INSTALLER_PLAN_MISSING");
            plan=FullUpdateInstallerPlan.Prepare(state.InstallationId,state.OwnerSid,state.FirstOwner,state.Consent,system,Hash(policy),Hash(pin));planBytes=Json(plan);files.Publish("full-installer-plan.json",planBytes);
        }
        FullUpdateInstallerPlan.ValidatePinned(plan,state,system,Hash(policy),Hash(pin));
        if(state.PlanDigest is not null&&state.PlanDigest!=Hash(planBytes)||state.PolicyDigest is not null&&state.PolicyDigest!=plan.Anchor.FullSystemPolicyDigest)throw new IOException("UPDATE_INSTALLER_PLAN_CHANGED");
        state=state with{PlanDigest=Hash(planBytes),PolicyDigest=plan.Anchor.FullSystemPolicyDigest};Save(state.Phase);
        foreach(var record in plan.Records)files.Publish(record.Path,record.Bytes,true);
        var seedName="initial-seed.json";var retryName="initial-retry.json";
        if(!File.Exists(files.PathOf(seedName)))files.Publish(seedName,Json(new FirstInstallSeed(1,state.InstallationId,state.DataId,state.PayloadDigest,Guid.NewGuid().ToString("N"))));
        if(!File.Exists(files.PathOf(retryName)))files.Publish(retryName,Json(new FirstInstallSeed(1,state.InstallationId,state.DataId,state.PayloadDigest,Guid.NewGuid().ToString("N"))));
        RenewCapability();
        var web=Json(new InstalledWeb(1,state.InstallationId,"http://127.0.0.1:"+state.Port,state.Port));
        var activation=Json(new InstalledActivation(1,state.InstallationId,1,state.PayloadDigest,state.DataId,state.Schema,state.Schema,Hash(key),Hash(policy),Hash(pin),Hash(web),"committed",null));
        files.Publish("web.json",web,true);files.Publish("activation.json",activation,true);files.Publish("epoch.json",Json(new InstalledEpoch(1,state.InstallationId,1,Hash(activation))),true);
        files.Publish("bootstrap.json",Json(new{schema=1,service=InstallLocations.ServiceName,installationId=state.InstallationId,activationReceiptDigest=Hash(activation)}),true);
        files.Publish("installation.json",Json(new FirstInstallReceipt(2,state.InstallationId,state.DataId,state.PayloadDigest,package.Manifest.Version,Hash(files.Read(seedName)),state.Port,state.PackageDigest,"committed",Hash(files.Read(retryName)))));
        Register();Save("committed");progress(new("installing_service",80));
        progress(new("cleaning_installation",80));
        package.CleanVerifiedStaging(staging,p=>progress(new("cleaning_installation",80+p*4/100)),cancellation);staging=null;
    },cancellation);
    private void RequireExistingEnrollment()
    {
        var s=state!;var anchor=InstalledAuthorityReader.Parse(files!.Read(InstalledUpdaterLayout.AnchorFile,4096));
        if(anchor.InstallationId!=s.InstallationId||anchor.FullSystemPolicyDigest!=s.PolicyDigest||Hash(files.Read("intake/full-system-enrollment.json"))!=s.PolicyDigest)throw new IOException("UPDATE_ENROLLMENT_CHANGED");
        var enrollment=NativeJson.Read<NativeUpdateEnrollment>(files.Read("intake/enrollment.json"));
        if(enrollment.InstallationId!=s.InstallationId||enrollment.FamilyId!=s.FirstOwner.familyId||enrollment.UserId!=s.FirstOwner.parentUserId||enrollment.PolicyDigest!=anchor.IntakePolicyDigest||enrollment.PinDigest!=anchor.IntakePinDigest)throw new IOException("UPDATE_OWNER_BINDING");
    }
    private void RenewCapability()
    {
        setupToken=Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+','-').Replace('/','_');
        files!.Publish("Secrets/setup-capability.json",Json(new{token=setupToken,expiresAt=DateTimeOffset.UtcNow.AddMinutes(15).ToUnixTimeMilliseconds(),firstOwner=state!.FirstOwner}),true);
    }
    private InstalledActivation Head()
    {
        var bytes=files!.Read("activation.json",16384);var value=NativeJson.Read<InstalledActivation>(bytes);var digest=Hash(bytes);
        if(value.Format!=1||value.InstallationId!=state!.InstallationId||value.Phase!="committed"||NativeJson.Read<InstalledEpoch>(files.Read("epoch.json"))!=new InstalledEpoch(1,state.InstallationId,value.Epoch,digest)||WindowsInstallationReader.Parse(files.Read("bootstrap.json"))!=new InstallationBinding(state.InstallationId,digest))throw new IOException("UPDATE_CURRENT_BINDING");return value;
    }
    private SystemBootstrapSnapshot CurrentBootstrap()
    {
        var current=NativeJson.Read<SystemBootstrapSnapshot>(files!.Read("system-bootstrap-current.json",8*1024*1024));
        if(current.Format!=1||current.InstallationId!=state!.InstallationId||current.Epoch<1||current.PolicyDigest!=state.PolicyDigest||current.OperationalProbeDigest!=current.ManifestDigest||current.Components.Length!=4||current.Components.Select(c=>c.Kind).Distinct().Count()!=4)throw new IOException("UPDATE_CURRENT_BOOTSTRAP");
        return current;
    }
    private void RequireCurrent()
    {
        RequireExistingEnrollment();_=Head();var current=CurrentBootstrap();var anchor=InstalledAuthorityReader.Parse(files!.Read(InstalledUpdaterLayout.AnchorFile));
        var baseline=state!.Files.Single(f=>f.Path=="bootstrap/probe/node/node.exe");if(anchor.ProbeSha256!=baseline.Sha256)throw new IOException("UPDATE_PROBE_PIN_CHANGED");
        var stateFiles=InstalledStateFiles.Installed(InstallLocations.State,files.Roles,InstalledUpdaterLayout.ProbeExecutable,baseline.Sha256);
        var binaryFiles=InstalledStateFiles.Installed(InstallLocations.Binaries,files.Roles,InstalledUpdaterLayout.ProbeExecutable,baseline.Sha256);
        _=InstalledOperationalProbe.Require(stateFiles,binaryFiles,state.InstallationId,state.PolicyDigest,InstalledUpdaterLayout.ProbeExecutable,baseline.Sha256,current.Epoch);
        foreach(var member in CurrentBinaryInventory())binaries!.VerifyFile(member.Path,member);
    }
    private InstallMember[] CurrentBinaryInventory()
    {
        var current=CurrentBootstrap();var result=new Dictionary<string,InstallMember>(StringComparer.OrdinalIgnoreCase);
        void Add(string path,long size,string hash){FullInstallFiles.BinaryMember(path);if(result.TryGetValue(path,out var old)&&old!=new InstallMember(path,size,hash))throw new IOException("UPDATE_BINARY_COLLISION");result[path]=new(path,size,hash);}
        foreach(var component in current.Components)
        {
            _=new SystemComponentDescriptor(component.Kind,FixedSystemComponents.SlotPrefix(component.Kind),component.EntryPoint,component.EntrySha256,component.Files.Select(f=>new SystemComponentFile(f.RelativePath,f.Size,f.Sha256)));
            if(component.Kind is SystemComponentKind.Helper or SystemComponentKind.Probe)continue;
            foreach(var file in component.Files)Add(FixedSystemComponents.ActiveDirectory(component.Kind)+"/"+file.RelativePath,file.Size,file.Sha256);
        }
        var slotFiles=InstalledStateFiles.Installed(InstallLocations.State,files!.Roles,InstalledUpdaterLayout.ProbeExecutable,state!.Files.Single(f=>f.Path=="bootstrap/probe/node/node.exe").Sha256);
        var helpers=SystemBootstrapRecoverySlots.Read(slotFiles,state.InstallationId,state.PolicyDigest!);
        if(helpers.PendingOperationId is not null)throw new IOException("UPDATE_RECOVERY_REQUIRED");
        foreach(var slot in helpers.Slots)
        {if(slot.Phase!=RecoveryHelperSlotPhase.Verified)throw new IOException("UPDATE_RECOVERY_REQUIRED");foreach(var member in slot.Closure.Files)Add("bootstrap/handoff-"+slot.Slot.ToString().ToLowerInvariant()+"/"+member.RelativePath,member.Size,member.Sha256);}
        foreach(var member in state.Files.Where(f=>f.Path.StartsWith("bootstrap/probe/",StringComparison.Ordinal)))Add(member.Path,member.Size,member.Sha256);
        var slots=binaries!.PathOf("system-slots");var count=0;
        foreach(var directory in Directory.EnumerateDirectories(slots))
        {
            if(++count>128)throw new IOException("UPDATE_SLOT_BOUND");var digest=Path.GetFileName(directory);InstalledActivationResolver.Digest(digest);
            var relative="system-slots/"+digest+"/system-manifest.json";var manifest=binaries.Read(relative,8*1024*1024);if(Hash(manifest)!=digest)throw new IOException("UPDATE_SLOT_MANIFEST");Add(relative,manifest.Length,digest);
            using var json=JsonDocument.Parse(manifest);var root=json.RootElement;
            if(root.GetProperty("format").GetInt32()!=2||root.GetProperty("kind").GetString()!="windows-system-candidate"||root.GetProperty("profile").GetString()!="full-system-v1"||root.GetProperty("target").GetString()!="win-x64")throw new IOException("UPDATE_SLOT_MANIFEST");
            foreach(var component in root.GetProperty("components").EnumerateArray())
            {
                var kind=Enum.Parse<SystemComponentKind>(component.GetProperty("kind").GetString()!,false);var members=component.GetProperty("files").EnumerateArray().Select(f=>new SystemComponentFile(f.GetProperty("path").GetString()!,f.GetProperty("size").GetInt64(),f.GetProperty("sha256").GetString()!)).ToArray();
                var descriptor=new SystemComponentDescriptor(kind,component.GetProperty("prefix").GetString()!,component.GetProperty("entryPoint").GetString()!,component.GetProperty("entrySha256").GetString()!,members);
                foreach(var member in descriptor.Files)Add("system-slots/"+digest+"/"+descriptor.SlotRelativeDirectory+"/"+member.RelativePath,member.Size,member.Sha256);
            }
        }
        // Every retained application payload is an owned binary-root closure:
        // its bundle.json is named by its digest and lists every other member.
        var payloads=binaries.PathOf("payloads");var releases=0;var head=Head().ReleaseDigest;var headPresent=false;
        foreach(var directory in Directory.Exists(payloads)?Directory.EnumerateDirectories(payloads):[])
        {
            if(++releases>128)throw new IOException("UPDATE_PAYLOAD_BOUND");var digest=Path.GetFileName(directory);InstalledActivationResolver.Digest(digest);headPresent|=digest==head;
            var relative="payloads/"+digest+"/bundle.json";var manifest=binaries.Read(relative,16*1024*1024);if(Hash(manifest)!=digest)throw new IOException("UPDATE_PAYLOAD_MANIFEST");Add(relative,manifest.Length,digest);
            using var json=JsonDocument.Parse(manifest);
            foreach(var member in json.RootElement.GetProperty("files").EnumerateArray())
                Add("payloads/"+digest+"/"+member.GetProperty("path").GetString(),member.GetProperty("size").GetInt64(),member.GetProperty("sha256").GetString()!);
        }
        if(!headPresent)throw new IOException("UPDATE_PAYLOAD_MISSING");
        if(result.Count>50000)throw new IOException("UPDATE_BINARY_BOUND");
        Add("Yahad.Setup.exe",new FileInfo(InstallLocations.InstallerImage).Length,state.SetupSha256);return result.Values.ToArray();
    }
    private InstallerState LanView()
    =>NativeLanInstall.PendingFullApplication(state!,files!)??NativeLanInstall.FullApplication(state!,Head());
    private NativeLanInstall Network()=>new(LanView(),files,
        relative=>relative=="caddy.exe"?binaries!.PathOf("payloads/"+state!.PayloadDigest+"/runtime/caddy/caddy.exe"):binaries!.PathOf("system-slots/"+state!.SystemManifestDigest+"/bootstrap/servicehost/"+relative[5..]),
        relative=>binaries!.Read("payloads/"+state!.PayloadDigest+"/"+relative,65536));
    public async Task ConfigureLanAsync(OwnerProcess owner,LanSetupRequest request,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullNetwork;if(state?.OwnerSid!=owner.Sid)throw new IOException("INSTALL_OWNER_CHANGED");var network=Network();network.Preflight(request);
        AcquireMaintenance();await StopAllAsync(cancellation,true);network.RecoverConfigurationOnly();RequireCurrent();await network.ConfigureAsync(request,progress,cancellation);
    }
    private void AcquireMaintenance()
    {
        if(maintenance is not null)return;
        files!.ClearInstallerMetadataResidue();
        files!.EnsureDirectory(".authority/updates");files.EnsureDirectory(".authority/update-serial");
        var lease=files.Lease(files.PathOf(".authority/update-serial"));
        try
        {
            NativeLanInstall.ClearFullInstallerPartials(state!,files);
            RequireInstallerQuiescence(lease);
            maintenance=lease;
        }
        catch{lease.Dispose();throw;}
    }
    private void RequireInstallerQuiescence(WindowsFileBoundary.DataLease lease)
    {
        var anchor=InstalledAuthorityReader.Parse(files!.Read(InstalledUpdaterLayout.AnchorFile));
        var probe=state!.Files.Single(f=>f.Path=="bootstrap/probe/node/node.exe").Sha256;
        InstalledFullSystemExecutionKernel.RequireInstallerQuiescence(InstalledStateFiles.Installed(InstallLocations.State,files.Roles,InstalledUpdaterLayout.ProbeExecutable,probe),InstalledStateFiles.Installed(InstallLocations.Binaries,files.Roles,InstalledUpdaterLayout.ProbeExecutable,probe),anchor,lease);
    }
    private void ValidateRemovalMember(string path,bool directory,WindowsFileBoundary.DataLease data)
    {
        if(!directory)
        {
            void Validate(System.Security.AccessControl.RawSecurityDescriptor acl)=>files!.Roles.Validate(acl,files.Role(path),false);
            if(maintenance!.TryValidateHeldGate(path,Validate)||data.TryValidateHeldGate(path,Validate))return;
        }
        files!.Validate(path,directory);
    }
    private async Task StopAllForRemovalAsync(CancellationToken cancellation)
    {
        if(maintenance is null)throw new IOException("UPDATE_SERVICE_QUIESCENCE_UNCONFIRMED");
        foreach(var service in services.Values)service.FenceForRemoval();
        // Request every accepted STOP first: a START_PENDING Authority settles
        // only once the Runtime it is starting stops or becomes ready.
        foreach(var kind in StopOrder)if(services.TryGetValue(kind,out var service))service.RequestStop();
        foreach(var kind in StopOrder)
            if(services.TryGetValue(kind,out var service))await service.StopForRemovalAsync(cancellation);
    }
    private static readonly FullServiceKind[] StopOrder=[FullServiceKind.RecoveryA,FullServiceKind.RecoveryB,FullServiceKind.Authority,FullServiceKind.Runtime];
    private async Task StopAllAsync(CancellationToken cancellation,bool fence)
    {
        foreach(var service in services.Values)if(fence)service.FenceForRemoval();
        foreach(var kind in StopOrder)if(services.TryGetValue(kind,out var service))service.RequestStop();
        foreach(var kind in StopOrder)if(services.TryGetValue(kind,out var service))await service.StopAsync(cancellation);
    }
    public async Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress,CancellationToken cancellation)
    {
        operationPhase=UninstallPhase.FullStart;if(state?.Phase!="committed")throw new IOException("INSTALL_NOT_COMMITTED");
        if(existing){AcquireMaintenance();await StopAllAsync(cancellation,true);Network().RecoverConfigurationOnly();}
        RequireCurrent();
        maintenance?.Dispose();maintenance=null;
        foreach(var service in services.Values)service.EnableStartup();progress(new("starting_server",85));await StartAuthenticatedAsync(cancellation);
        var url="http://127.0.0.1:"+state.Port+"/";using var client=new HttpClient(new HttpClientHandler{UseProxy=false,AllowAutoRedirect=false}){Timeout=TimeSpan.FromSeconds(10)};
        using var health=await client.GetAsync(url+"health/ready",cancellation);health.EnsureSuccessStatusCode();var bytes=await health.Content.ReadAsByteArrayAsync(cancellation);
        if(bytes.Length>16384||!NativeJson.Read<JsonElement>(bytes).GetProperty("ok").GetBoolean())throw new IOException("SERVER_NOT_READY");
        async Task<string?> SetupState(){using var response=await client.GetAsync(url+"api/setup/status",cancellation);response.EnsureSuccessStatusCode();var result=await response.Content.ReadAsByteArrayAsync(cancellation);if(result.Length>16384)throw new IOException("SERVER_NOT_READY");return NativeJson.Read<JsonElement>(result).GetProperty("state").GetString();}
        var setup=await SetupState();
        if(setup=="complete")setupToken=null;
        else if(existing&&setup=="closed")
        {AcquireMaintenance();await StopAllAsync(cancellation,true);RenewCapability();maintenance?.Dispose();maintenance=null;foreach(var service in services.Values)service.EnableStartup();await StartAuthenticatedAsync(cancellation);if(await SetupState()!="ready")throw new IOException("SERVER_NOT_READY");}
        else if(existing&&setup=="ready")setupToken=NativeJson.Read<JsonElement>(files!.Read("Secrets/setup-capability.json",1024)).GetProperty("token").GetString();
        else if(setup!="ready")throw new IOException("SERVER_NOT_READY");
        var lan=await Network().ObserveAsync(cancellation);RequireNativeReadiness();FullInstallDiagnostics.DeleteOwned(state);return new(true,"READY",url,setupToken,Lan:lan,FullProfileInstallationId:state.InstallationId);
    }
    private async Task StartAuthenticatedAsync(CancellationToken cancellation)
    {
        readiness=new(1,state!.InstallationId,Guid.NewGuid().ToString("N"),Hash(files!.Read("activation.json",16384)));
        files.Publish(InstallerReadiness.RequestFile,Json(readiness),true);
        await services[FullServiceKind.Authority].StartAsync(cancellation);
        RequireNativeReadiness();
        foreach(var kind in new[]{FullServiceKind.RecoveryA,FullServiceKind.RecoveryB})await services[kind].StartOneShotAsync(state.OwnerSid,state.AuthoritySid!,cancellation);
    }
    private void RequireNativeReadiness()
    {
        var request=readiness??throw new IOException("INSTALL_READINESS_BINDING");var head=Head();
        var process=services[FullServiceKind.Authority].RunningIdentity();
        var runtime=services[FullServiceKind.Runtime].RunningIdentity();
        var observed=NativeJson.Read<InstallerReadinessObservation>(files!.Read(".authority/install-ready-"+request.Nonce+".json",2048));
        if(Hash(files.Read("activation.json",16384))!=request.ActivationDigest)throw new IOException("INSTALL_READINESS_BINDING");
        InstallerReadiness.RequireObserved(request,observed,head.ReleaseDigest,process.Pid,process.Created,runtime.Pid,runtime.Created);
    }
    private void Register()
    {
        var head=Head();using var payload=JsonDocument.Parse(binaries!.Read("payloads/"+head.ReleaseDigest+"/bundle.json"));
        var version=payload.RootElement.GetProperty("version").GetString()??throw new IOException("PAYLOAD_VERSION");SystemCandidateManifest.Version(version);
        using var key=Registry.LocalMachine.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall",true);key.SetValue("DisplayName","יחד");key.SetValue("DisplayVersion",version);key.SetValue("Publisher","Yahad");key.SetValue("InstallLocation",InstallLocations.Binaries);key.SetValue("UninstallString","\""+InstallLocations.InstallerImage+"\" --uninstall");key.SetValue("NoModify",1,RegistryValueKind.DWord);key.SetValue("NoRepair",1,RegistryValueKind.DWord);
    }
    private void RestoreBinaries(CancellationToken cancellation)
    {
        var retained=state!.PreservedBinaries??throw new IOException("UPDATE_PRESERVED_BOOTSTRAP_REQUIRED");
        if(!Directory.Exists(InstallLocations.Binaries))NativeInstallFiles.NewDirectory(InstallLocations.Binaries,FullUpdateInstallerPlan.Descriptor(files!.Roles,InstalledFileRole.Control,true));binaries=new(state,true);binaries.Validate(InstallLocations.Binaries,true);
        foreach(var member in retained){cancellation.ThrowIfCancellationRequested();var relative=".authority/preserved-installer/"+state.InstallationId+"/"+member.Path;files!.VerifyFile(relative,member);binaries.CopyVerified(files.PathOf(relative),member.Path,member);}
    }
    private void SaveRemoval(FullRemovalIntent intent)
    {
        intent.Require();var path=InstallLocations.RemovalStateFile;var prefix=Path.GetFileName(path)+".partial-";
        var partials=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,prefix+"*").Take(33).ToArray();if(partials.Length>32)throw new IOException("UNINSTALL_INTENT_INVALID");
        foreach(var partial in partials)
        {
            var suffix=Path.GetFileName(partial)[prefix.Length..];if(!Guid.TryParseExact(suffix,"N",out var id)||id.ToString("N")!=suffix)throw new IOException("UNINSTALL_INTENT_INVALID");
            using(var held=WindowsFileBoundary.OpenInstallerRoleRead(partial,files!.Roles,InstalledFileRole.Anchor,administrativeOwner:true)){}
            WindowsFileBoundary.DeleteInstallerRoleMember(partial,false,files!.Roles,InstalledFileRole.Anchor,administrativeOwner:true);
        }
        var descriptor=FullUpdateInstallerPlan.Descriptor(files!.Roles,InstalledFileRole.Anchor,false);var bytes=Json(intent);
        if(File.Exists(path))NativeInstallFiles.PublishProtectedFile(path,bytes,descriptor);else NativeInstallFiles.PublishNewProtectedFile(path,bytes,descriptor);
    }
    private RemovalTree? InspectRemovalBinaries(InstallMember[] inventory,bool complete)
    {
        if(!Directory.Exists(InstallLocations.Binaries)){if(complete)throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");return null;}
        var expected=inventory.ToDictionary(f=>f.Path,StringComparer.OrdinalIgnoreCase);
        var tree=RemovalTree.Inspect(InstallLocations.Binaries,binaries!.Validate);
        foreach(var path in tree.Files)
        {
            var relative=Path.GetRelativePath(InstallLocations.Binaries,path).Replace('\\','/');
            if(!expected.TryGetValue(relative,out var member))throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");binaries.VerifyFile(relative,member);
        }
        if(complete&&tree.Files.Length!=expected.Count)throw new IOException("UNINSTALL_UNOWNED_FILE_PRESERVED");return tree;
    }
    public async Task<SetupResult> UninstallAsync(OwnerProcess owner,Action<SetupProgress> progress,CancellationToken cancellation,UninstallMode mode=UninstallMode.KeepData)
    {
        operationPhase=UninstallPhase.FullUninstall;NativeInstallFiles.RequireAdministrator();UninstallChoice.Require(mode);
        if(NativePreparingCancellationHost.Selected())return await PreparingInstallCancellation.RunAsync(owner.Sid,mode,new NativePreparingCancellationHost(),progress,cancellation,s=>{state=s;diagnosticsAllowed=true;},()=>diagnosticsAllowed=false);
        FullRemovalIntent? intent=null;
        if(File.Exists(InstallLocations.RemovalStateFile))
        {
            using var receipt=WindowsFileBoundary.OpenInstallerMetadata(InstallLocations.RemovalStateFile);intent=NativeJson.Read<FullRemovalIntent>(receipt.Small(16777216));intent.Require();state=intent.State;
            if(intent.Mode!=mode)throw new IOException("UNINSTALL_MODE_CHANGED");
            if(File.Exists(InstallLocations.StateFile))intent.RequireSameInstallation(ReadState());
        }
        else state=ReadState();
        if(state.OwnerSid!=owner.Sid)throw new UnauthorizedAccessException("INSTALL_OWNER_CHANGED");diagnosticsAllowed=true;
        // A partial first installation must first finish through the same exact
        // source package. Never invent missing role, enrollment or image proof.
        if(intent is null&&state.Phase is not("committed" or "uninstalled"))throw new IOException("UPDATE_INSTALL_INCOMPLETE_REPAIR_FIRST");
        RequireRoles();
        if(intent?.WasPreserved==true||intent is null&&state.Phase=="uninstalled")
        {
            if(Directory.Exists(InstallLocations.Binaries)||File.Exists(InstallLocations.Binaries)||Kinds.Any(NativeFullService.Exists))throw new IOException("UPDATE_RESTORE_COLLISION");
            var retained=state.PreservedBinaries??throw new IOException("UPDATE_PRESERVED_BOOTSTRAP_REQUIRED");
            if(intent is null)
            {
                RequireExistingEnrollment();_=Head();_=CurrentBootstrap();
                foreach(var member in retained)files!.VerifyFile(".authority/preserved-installer/"+state.InstallationId+"/"+member.Path,member);
                if(mode==UninstallMode.KeepData)return new(true,"REMOVED","http://127.0.0.1:"+state.Port+"/",FullProfileInstallationId:state.InstallationId);
                state=state with{Phase=UninstallChoice.Phase(mode)};intent=new(4,state,mode,retained,LanView(),"prepared",true);SaveRemoval(intent);Save(state.Phase);
            }
            if(!intent.AtLeast("services_removed"))
            {
                // Successful KeepData already durably observed/removes all
                // executable services and binaries. No update can begin here.
                maintenance=files!.Lease(files.PathOf(".authority/update-serial"));
                using(var data=files.Lease(CurrentDataPath(Head())))_=await new NativeLanInstall(intent.LanApplication,files).UninstallAsync(mode,cancellation);
                intent=intent with{Stage="services_removed"};SaveRemoval(intent);
            }
        }
        using(var registration=Registry.LocalMachine.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall"))
        {if(registration is not null&&registration.GetValue("InstallLocation") as string!=InstallLocations.Binaries)throw new IOException("UNINSTALL_REGISTRATION_CHANGED");}
        foreach(var kind in Kinds)
        {
            if(intent?.AtLeast("services_removed")==true){if(NativeFullService.Exists(kind))throw new IOException("UNINSTALL_SERVICE_REAPPEARED");continue;}
            var service=await NativeFullService.OpenForRemovalAsync(kind,cancellation);if(service is null)continue;
            try{service.RequireProtection(state.OwnerSid,state.AuthoritySid!);services.Add(kind,service);}catch{service.Dispose();throw;}
        }
        if(intent?.AtLeast("services_removed")!=true)
        {
            AcquireMaintenance();operationPhase=UninstallPhase.StopService;await StopAllForRemovalAsync(cancellation);Network().RecoverConfigurationOnly();progress(new("stopping_server",10));RequireCurrent();
            operationPhase=UninstallPhase.AcquireDataLease;
            using(var data=files!.Lease(CurrentDataPath(Head())))
            {
                // STOPPED/error is not proof of writer cleanup. Both actual
                // leases and the existing durable journal/handoff inspection
                // remain held/rechecked before intent or destructive action.
                RequireInstallerQuiescence(maintenance!);
                foreach(var service in services.Values){service.RequireProtection(state.OwnerSid,state.AuthoritySid!);service.RequireFencedStoppedForRemoval();}
                if(intent is null)
                {
                    operationPhase=UninstallPhase.InspectData;
                    var inventory=CurrentBinaryInventory();_=InspectRemovalBinaries(inventory,true);
                    if(mode==UninstallMode.DeleteData)_=RemovalTree.Inspect(InstallLocations.State,(path,directory)=>ValidateRemovalMember(path,directory,data));
                    operationPhase=UninstallPhase.WriteRemovalReceipt;
                    state=state with{Phase=UninstallChoice.Phase(mode)};intent=new(4,state,mode,inventory,LanView(),"prepared");SaveRemoval(intent);Save(state.Phase);
                }
                if(!intent.AtLeast("lan_removed"))
                {
                    operationPhase=UninstallPhase.RemoveLan;
                    _=await new NativeLanInstall(intent.LanApplication,files).UninstallAsync(mode,cancellation);
                    if(mode==UninstallMode.KeepData)
                    {
                        foreach(var member in intent.Binaries){cancellation.ThrowIfCancellationRequested();binaries!.VerifyFile(member.Path,member);files.CopyVerified(binaries.PathOf(member.Path),".authority/preserved-installer/"+state.InstallationId+"/"+member.Path,member);}
                        state=state with{PreservedBinaries=intent.Binaries};Save(state.Phase);
                    }
                    intent=intent with{State=state,Stage="lan_removed"};SaveRemoval(intent);
                }
                RequireInstallerQuiescence(maintenance!);operationPhase=UninstallPhase.RemoveService;
                foreach(var service in services.Values){service.RequireProtection(state.OwnerSid,state.AuthoritySid!);service.RequireFencedStoppedForRemoval();}
                foreach(var service in services.Values)service.Delete();foreach(var service in services.Values)service.Dispose();services.Clear();
                operationPhase=UninstallPhase.WaitForServiceRemoval;
                using(var wait=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {wait.CancelAfter(TimeSpan.FromSeconds(15));while(Kinds.Any(NativeFullService.Exists))await Task.Delay(100,wait.Token);}
                operationPhase=UninstallPhase.SaveRemovalState;intent=intent with{Stage="services_removed"};SaveRemoval(intent);
            }
        }
        maintenance?.Dispose();maintenance=null;
        if(!intent!.AtLeast("data_removed"))
        {
            if(mode==UninstallMode.DeleteData&&Directory.Exists(InstallLocations.State))
            {
                var tree=RemovalTree.Inspect(InstallLocations.State,files!.Validate);tree.DeleteContents(files.Delete,p=>progress(new("deleting_data",25+p*50/100)),cancellation);files.Delete(InstallLocations.State,true);
            }
            else if(mode==UninstallMode.KeepData){state=state with{PreservedBinaries=intent.Binaries};Save("uninstalled");}
            intent=intent with{Stage="data_removed"};SaveRemoval(intent);
        }
        if(!intent.AtLeast("binaries_removed"))
        {
            var tree=InspectRemovalBinaries(intent.Binaries,false);
            if(tree is not null){tree.DeleteContents(binaries!.Delete,_=>{},cancellation);binaries.Delete(InstallLocations.Binaries,true);}
            intent=intent with{Stage="binaries_removed"};SaveRemoval(intent);
        }
        Registry.LocalMachine.DeleteSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Yahad.FirstInstall",false);
        // Protected receipt deletion remains last. A failed final delete still
        // has a complete owner/mode/closure plan for the same bounded retry.
        FullInstallDiagnostics.DeleteOwned(state);WindowsFileBoundary.DeleteInstallerRoleMember(InstallLocations.RemovalStateFile,false,files!.Roles,InstalledFileRole.Anchor);diagnosticsAllowed=false;
        progress(new("removed",100));return new(true,mode==UninstallMode.DeleteData?"REMOVED_ALL":"REMOVED","http://127.0.0.1:"+state.Port+"/",DataPreserved:mode==UninstallMode.KeepData,FullProfileInstallationId:state.InstallationId);
    }
    public void Dispose(){maintenance?.Dispose();maintenance=null;foreach(var service in services.Values)service.Dispose();services.Clear();}
    internal static string CurrentDataPath(InstalledActivation head)
    {
        InstalledActivationResolver.Id(head.DataId);
        if(head.Recovery is not {} recovery)return Path.Combine(InstallLocations.State,"datasets",head.DataId,"data");
        InstalledActivationResolver.Id(recovery.SourceDataId);InstalledActivationResolver.Id(recovery.OperationId);
        return Path.Combine(InstallLocations.State,"datasets",recovery.SourceDataId,"restore-"+recovery.OperationId,"completed","data");
    }
}
