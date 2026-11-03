using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Concrete fixed-path file and fixed-service adapter. There is deliberately NO
// ChangeServiceConfig/registry/security-repair API. Installer provisioning owns
// the initial ACLs and fixed SCM recovery command. This adapter cannot provision
// a service or select another account/path. It is not selected by Program.
internal sealed class SystemBootstrapNative:ISystemBootstrapKernel
{
    private readonly InstalledStateFiles state,binaries;private readonly InstalledAuthorityAnchor anchor;
    private readonly RetainedRuntimeQuiescence? runtime;private readonly SafeProcessHandle? oldAuthority,oldHelper;
    private readonly WindowsFileBoundary.DataLease dataLease;private readonly string dataDirectory;
    private readonly Func<VerifiedSystemOperation,bool,CancellationToken,Task> authorityHealth;
    private readonly Func<VerifiedSystemOperation,CancellationToken,Task<RetainedRuntimeQuiescence>> runtimeBootstrap;
    private readonly Func<VerifiedSystemOperation,CancellationToken,Task> dataUnchanged;
    private readonly Func<VerifiedSystemOperation,CancellationToken,Task> helperHealth;
    private readonly Action<VerifiedSystemOperation> requireCurrent;
    private ISystemBootstrapHelperPublication? helperPublication;
    private RetainedRuntimeQuiescence? helperQuiescence;
    internal void ConfigureHelperExitObservation(RetainedRuntimeQuiescence value)
    {if(helperQuiescence is not null||oldHelper is null||value.RootProcessId!=(int)GetProcessId(oldHelper))throw new IOException("SYSTEM_HELPER_TREE_BINDING");helperQuiescence=value;}
    internal void ConfigureHelperPublication(ISystemBootstrapHelperPublication value)
    {if(helperPublication is not null)throw new IOException("SYSTEM_HELPER_PUBLICATION_ALREADY_CONFIGURED");helperPublication=value;}
    private readonly SecurityIdentifier authoritySid,runtimeSid;
    // Handles/leases are transferred actual native owners, not request fields.
    // The full coordinator must duplicate them into the bounded helper BEFORE
    // stopping old Authority. JobProcess.Start alone does not survive its owning
    // KILL_ON_CLOSE job; no success is inferred from spawning a process.
    internal SystemBootstrapNative(InstalledStateFiles state,InstalledStateFiles binaries,InstalledAuthorityAnchor anchor,
        RetainedRuntimeQuiescence? runtime,SafeProcessHandle? oldAuthority,SafeProcessHandle? oldHelper,
        WindowsFileBoundary.DataLease dataLease,string dataDirectory,
        Func<VerifiedSystemOperation,bool,CancellationToken,Task> authorityHealth,
        Func<VerifiedSystemOperation,CancellationToken,Task<RetainedRuntimeQuiescence>> runtimeBootstrap,
        Func<VerifiedSystemOperation,CancellationToken,Task> dataUnchanged,Func<VerifiedSystemOperation,CancellationToken,Task> helperHealth,Action<VerifiedSystemOperation> requireCurrent)
    {
        this.state=state;this.binaries=binaries;this.anchor=anchor;this.runtime=runtime;this.oldAuthority=oldAuthority;this.oldHelper=oldHelper;
        this.dataLease=dataLease;this.dataDirectory=dataDirectory;this.authorityHealth=authorityHealth;this.runtimeBootstrap=runtimeBootstrap;this.dataUnchanged=dataUnchanged;this.helperHealth=helperHealth;
        this.requireCurrent=requireCurrent;
        if(state.Roles is null||binaries.Roles is null||state.Root!=InstalledUpdaterLayout.StateRoot||binaries.Root!=InstalledUpdaterLayout.BinaryRoot)throw new IOException("SYSTEM_BOOTSTRAP_INSTALLED_ROOTS");
        authoritySid=state.Roles.Authority;runtimeSid=state.Roles.Runtime;RequireAuthority();
    }
    private void RequireAuthority()=>_=InstalledAuthorityReader.RequireUpdater();
    private void Bind(VerifiedSystemOperation operation)
    {RequireAuthority();if(operation.InstallationId!=anchor.InstallationId)throw new UnauthorizedAccessException("SYSTEM_BOOTSTRAP_INSTALLATION");}
    // Cold repair has deliberately LESS authority than an admitted update: it
    // restores only the two previous fixed service folders, touches no data,
    // and cannot claim runtime quiescence. Fresh real waiting-host admission is
    // still mandatory before backup restore/migration/normal reconciliation.
    internal static async Task StopAuthorityForColdRepairAsync(InstalledStateFiles files,FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,
        SystemBootstrapHelperCapsule original,WindowsFileBoundary.DataLease sourceLease,WindowsFileBoundary.DataLease serialLease,CancellationToken cancellation)
    {
        SystemBootstrapHelperApplication.RequireCapsule(original);var admitted=policy.RemintBootstrapRecovery(operation.OperationId);
        if(admitted.ManifestDigest!=operation.ManifestDigest||admitted.GrantBindingDigest!=operation.GrantBindingDigest||original.OperationId!=operation.OperationId||original.ManifestDigest!=operation.ManifestDigest)throw new IOException("SYSTEM_REPAIR_ADMISSION");
        _=SystemBootstrapHelperApplication.RequireGoEvidence(files,original);
        using var source=sourceLease.DuplicateForDirectory(original.DataDirectory);using var serial=serialLease.DuplicateForDirectory(files.PathOf(".authority/update-serial"));
        var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
        if(binding!=new InstallationBinding(operation.InstallationId,operation.PreviousActivationDigest)||InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=operation.PreviousActivationDigest)throw new IOException("SYSTEM_REPAIR_DATA_HEAD_CHANGED");
        // An application cannot legitimately be active while this exact source
        // data lease is exclusively held here. Stop only the two fixed services;
        // these requests do not claim app-job quiescence or authorize data work.
        using(var service=WindowsFixedRuntimeService.RuntimeControl())
        {
            var status=service.Status();
            if(status.State!=1)
            {
                if(status.Pid==0)throw new IOException("SYSTEM_REPAIR_RUNTIME_PID_UNAVAILABLE");using var process=OpenProcess(0x00101000,false,status.Pid);if(process.IsInvalid)throw new Win32Exception();
                var created=NativeCustodyTransfer.Created(process);if(service.Status().Pid!=status.Pid)throw new IOException("SYSTEM_REPAIR_RUNTIME_GENERATION_CHANGED");
                if(status.State!=3)service.Stop();await WaitExited(process,cancellation);
                if(NativeCustodyTransfer.Created(process)!=created||service.Status().State!=1)throw new IOException("SYSTEM_REPAIR_RUNTIME_NOT_STOPPED");
            }
        }
        using(var service=NativeOwnService.Authority())await service.StopAndWaitUnlessSelfAsync(cancellation);
    }
    internal static async Task RepairPreviousBootstrapAsync(InstalledStateFiles files,InstalledStateFiles binaries,InstalledAuthorityAnchor anchor,
        FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,SystemBootstrapHelperCapsule original,
        WindowsFileBoundary.DataLease sourceLease,WindowsFileBoundary.DataLease serialLease,CancellationToken cancellation)
    {
        SystemBootstrapHelperApplication.RequireCapsule(original);
        var admitted=policy.RemintBootstrapRecovery(operation.OperationId);
        if(admitted.ManifestDigest!=operation.ManifestDigest||admitted.GrantId!=operation.GrantId||admitted.GrantBindingDigest!=operation.GrantBindingDigest||original.OperationId!=operation.OperationId||original.ManifestDigest!=operation.ManifestDigest)throw new IOException("SYSTEM_REPAIR_ADMISSION");
        _=SystemBootstrapHelperApplication.RequireGoEvidence(files,original);
        using var source=sourceLease.DuplicateForDirectory(original.DataDirectory);using var serial=serialLease.DuplicateForDirectory(files.PathOf(".authority/update-serial"));
        using var journal=new SystemBootstrapJournal(files,operation,serial);
        var before=journal.Current?.Before??throw new IOException("SYSTEM_REPAIR_PUBLISHED_PREPARATION_REQUIRED");
        if(journal.Current.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)throw new IOException("SYSTEM_REPAIR_ALREADY_TERMINAL");
        ValidateSnapshot(operation,before);
        static void Stopped()
        {
            using var authority=WindowsFixedRuntimeService.AuthorityRead();using var runtime=WindowsFixedRuntimeService.RuntimeControl();
            if(authority.Status()!=((uint)1,(uint)0)||runtime.Status()!=((uint)1,(uint)0))throw new IOException("SYSTEM_REPAIR_FIXED_SERVICES_MUST_BE_STOPPED");
        }
        Stopped();await WaitOriginalExit(original.OldAuthorityPid,original.OldAuthorityCreated,cancellation);
        await WaitOriginalExit(original.Runtime.RootPid,original.Runtime.RootCreated,cancellation);
        await WaitOriginalExit(original.HelperPid,original.HelperCreated,cancellation);Stopped();
        using var self=SystemBootstrapHelperNative.OpenSelf();SystemBootstrapHelperNative.RequireHelperImage(self);
        Task Forbidden(VerifiedSystemOperation op,CancellationToken c)=>throw new IOException("SYSTEM_REPAIR_NO_FORWARD_EXECUTION");
        var repair=new SystemBootstrapNative(files,binaries,anchor,null,null,self,source,original.DataDirectory,
            (op,previous,c)=>Forbidden(op,c),(op,c)=>throw new IOException("SYSTEM_REPAIR_NO_RUNTIME_OBSERVATION"),Forbidden,Forbidden,op=>throw new IOException("SYSTEM_REPAIR_NO_FORWARD_EXECUTION"));
        // Re-observe the exact unchanged control head under the exclusive source
        // lease; this repair never opens SQLite or invokes candidate code.
        void Head()
        {
            Stopped();using var held=source.DuplicateForDirectory(original.DataDirectory);
            var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
            if(binding!=new InstallationBinding(operation.InstallationId,operation.PreviousActivationDigest)||InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=operation.PreviousActivationDigest)throw new IOException("SYSTEM_REPAIR_DATA_HEAD_CHANGED");
        }
        Head();var manifest=binaries.Read(Child(repair.Slot(before.ManifestDigest),"system-manifest.json"),8*1024*1024);
        if(Convert.ToHexStringLower(SHA256.HashData(manifest))!=before.ManifestDigest)throw new IOException("SYSTEM_REPAIR_PREVIOUS_MANIFEST");
        foreach(var component in before.Components)repair.VerifyClosure(repair.SlotComponent(before.ManifestDigest,component.Kind),component.Files,false,cancellation);
        journal.Append(SystemBootstrapPhase.RepairIntent,before);
        foreach(var kind in new[]{SystemComponentKind.RuntimeHost,SystemComponentKind.Authority})
        {
            var old=before.Components.Single(c=>c.Kind==kind);var candidate=operation.Components.Single(c=>c.Kind==kind);Head();
            repair.PublishFiles(operation.OperationId,kind,repair.Active(kind),repair.SlotComponent(before.ManifestDigest,kind),old.Files,
                candidate.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray(),cancellation,Head);
        }
        Head();journal.Append(SystemBootstrapPhase.RepairPublished,before);
        // The actual old Authority must enter its existing controlled bootstrap
        // recovery branch and authenticate the real waiting runtime host next.
        using var previousAuthority=NativeOwnService.Authority();previousAuthority.StartIfStopped();
    }
    private static async Task WaitOriginalExit(int pid,long created,CancellationToken cancellation)
    {
        if(pid<1||created<=0)throw new IOException("SYSTEM_REPAIR_ORIGINAL_GENERATION");
        using var process=OpenProcess(0x00101000,false,(uint)pid);
        if(process.IsInvalid){var error=Marshal.GetLastWin32Error();if(error==87)return;throw new Win32Exception(error);}
        // Reuse of a PID never authorizes killing or waiting on the new process.
        if(NativeCustodyTransfer.Created(process)!=created)return;await WaitExited(process,cancellation);
    }
    // Nonsecret, Authority-written / Runtime-readable slot selection. The
    // immutable baseline anchor and private handoff journals keep their roles.
    private string CurrentPath=>state.PathOf("system-bootstrap-current.json");
    private string Slot(string digest){FixedSystemComponents.Digest(digest);return binaries.PathOf(FixedSystemComponents.SlotsDirectory+"/"+digest);}
    private static string Child(string root,string relative)
    {
        FixedSystemComponents.Member(relative);var path=Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar)));
        if(!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("SYSTEM_BOOTSTRAP_PATH");return path;
    }
    private string Active(SystemComponentKind kind)
        =>kind is SystemComponentKind.Helper or SystemComponentKind.Probe?throw new IOException("SYSTEM_COMPONENT_HAS_NO_FIXED_ACTIVE_DIRECTORY"):Child(binaries.Root,FixedSystemComponents.ActiveDirectory(kind)??throw new IOException("SYSTEM_PROBE_IS_OPERATIONAL_SLOT"));
    // Helper runs from the protected A/B selection, never the original archive
    // prefix. Probe likewise runs from its selected operational slot.
    internal static IEnumerable<SystemBootstrapComponentState> FixedActiveComponents(IEnumerable<SystemBootstrapComponentState> components)
        =>components.Where(c=>c.Kind is not(SystemComponentKind.Helper or SystemComponentKind.Probe));
    private string SlotComponent(string digest,SystemComponentKind kind)=>Child(Slot(digest),FixedSystemComponents.SlotPrefix(kind));
    public async Task<SystemBootstrapSnapshot> PrepareAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        Bind(operation);cancellation.ThrowIfCancellationRequested();var previous=NativeJson.Read<SystemBootstrapSnapshot>(state.Read(CurrentPath,8*1024*1024));ValidateSnapshot(operation,previous);
        await (helperPublication??throw new IOException("SYSTEM_BOOTSTRAP_A_B_PROVISIONING_REQUIRED")).RequireReadyAsync(operation,previous,cancellation);
        foreach(var component in operation.Components)
            VerifyClosure(SlotComponent(operation.ManifestDigest,component.Kind),component.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray(),false,cancellation);
        foreach(var component in previous.Components)
            VerifyClosure(SlotComponent(previous.ManifestDigest,component.Kind),component.Files,false,cancellation);
        foreach(var component in FixedActiveComponents(previous.Components))VerifyClosure(Active(component.Kind),component.Files,true,cancellation);
        var required=operation.Components.Sum(c=>c.Files.Sum(f=>f.Size))+32L*1024*1024;
        if(new DriveInfo(Path.GetPathRoot(binaries.Root)!).AvailableFreeSpace<required)throw new IOException("SYSTEM_BOOTSTRAP_DISK_SPACE");
        using(var held=dataLease.DuplicateForDirectory(dataDirectory)){}
        return previous;
    }
    private static void ValidateSnapshot(VerifiedSystemOperation operation,SystemBootstrapSnapshot value)
    {
        FixedSystemComponents.Digest(value.ManifestDigest);if(value.OperationalProbeDigest is {} probe)FixedSystemComponents.Digest(probe);
        if(value.Format!=1||value.InstallationId!=operation.InstallationId||value.Epoch!=operation.ExpectedBootstrapEpoch||value.Version!=operation.BootstrapFromVersion||value.Components.Length!=4||value.Components.Select(c=>c.Kind).Distinct().Count()!=4)throw new IOException("SYSTEM_BOOTSTRAP_PREVIOUS_SELECTION");
        foreach(var component in value.Components)
        {
            _=new SystemComponentDescriptor(component.Kind,FixedSystemComponents.SlotPrefix(component.Kind),component.EntryPoint,component.EntrySha256,
                component.Files.Select(f=>new SystemComponentFile(f.RelativePath,f.Size,f.Sha256)));
        }
    }
    public Task VerifyHelperCandidateAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {Bind(operation);return helperHealth(operation,cancellation);} // Must include actual bounded probe-job cleanup, not a describe JSON claim alone.
    public async Task RequireCustodyAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        Bind(operation);using(var retained=dataLease.DuplicateForDirectory(dataDirectory)){}
        using(var service=WindowsFixedRuntimeService.RuntimeControl())service.Stop();
        using(var service=NativeOwnService.Authority())await service.StopAndWaitUnlessSelfAsync(cancellation);
        if(oldAuthority is not null)await WaitExited(oldAuthority,cancellation);await (runtime??throw new IOException("SYSTEM_REPAIR_HAS_NO_RUNTIME_OBSERVATION")).RequireAsync(cancellation);
    }
    public async Task RequireDataUnchangedAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        Bind(operation);using var retained=dataLease.DuplicateForDirectory(dataDirectory);
        var binding=WindowsInstallationReader.Parse(state.Read(state.PathOf("bootstrap.json"),1024));
        if(binding!=new InstallationBinding(operation.InstallationId,operation.PreviousActivationDigest)||InstalledActivationResolver.Hash(state.Read(state.PathOf("activation.json"),16384))!=operation.PreviousActivationDigest)
            throw new IOException("SYSTEM_BOOTSTRAP_DATA_HEAD_CHANGED");
        await dataUnchanged(operation,cancellation); // Independent DB/file/schema observation held by the full transaction.
    }
    public Task PublishComponentAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,SystemComponentKind kind,CancellationToken cancellation)
    {
        Bind(operation);requireCurrent(operation);ValidateSnapshot(operation,before);var wanted=operation.Components.Single(c=>c.Kind==kind);
        if(kind==SystemComponentKind.Helper)return (helperPublication??throw new IOException("SYSTEM_BOOTSTRAP_A_B_PROVISIONING_REQUIRED")).PublishAsync(operation,before,cancellation);
        if(kind==SystemComponentKind.Probe)
        {VerifyClosure(SlotComponent(operation.ManifestDigest,kind),wanted.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray(),false,cancellation);return Task.CompletedTask;}
        var old=before.Components.Single(c=>c.Kind==kind);
        PublishFiles(operation.OperationId,kind,Active(kind),SlotComponent(operation.ManifestDigest,kind),wanted.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray(),old.Files,cancellation,()=>requireCurrent(operation));
        return Task.CompletedTask;
    }
    public async Task StartAndObserveAuthorityAsync(VerifiedSystemOperation operation,bool previous,CancellationToken cancellation)
    {
        Bind(operation);if(!previous)requireCurrent(operation);using var service=NativeOwnService.Authority();service.StartIfStopped();
        await authorityHealth(operation,previous,cancellation);
        using var pinned=WindowsFixedRuntimeService.AuthorityRead();var status=pinned.Status();using var process=pinned.Pin(status.Pid,0x00101000);process.RequireCurrent();
    }
    public async Task ObserveRuntimeBootstrapAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        Bind(operation);using var stopped=await runtimeBootstrap(operation,cancellation);await stopped.RequireAsync(cancellation);
        // The callback must perform actual waiting-host admission/stop, never
        // release Node launch, and return its retained kernel observation.
    }
    public Task WaitForOldHelperExitAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {Bind(operation);var helper=oldHelper??throw new IOException("SYSTEM_HELPER_EXIT_OBSERVATION_UNAVAILABLE");if(GetProcessId(helper)==(uint)Environment.ProcessId)throw new IOException("SYSTEM_HELPER_CANNOT_REPLACE_ITSELF");return (helperQuiescence??throw new IOException("SYSTEM_HELPER_TREE_OBSERVATION_UNAVAILABLE")).RequireAsync(cancellation);}
    public async Task PublishSelectionAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        Bind(operation);requireCurrent(operation);cancellation.ThrowIfCancellationRequested();var observed=NativeJson.Read<SystemBootstrapSnapshot>(state.Read(CurrentPath,8*1024*1024));
        var components=operation.Components.Select(c=>new SystemBootstrapComponentState(c.Kind,c.EntryPoint,c.EntrySha256,c.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray())).ToArray();
        var wanted=new SystemBootstrapSnapshot(1,operation.InstallationId,operation.ExpectedBootstrapEpoch+1,operation.ToVersion,operation.ManifestDigest,operation.ManifestDigest,components,operation.PolicyDigest,operation.SourceCommit);
        foreach(var component in components)VerifyClosure(SlotComponent(operation.ManifestDigest,component.Kind),component.Files,false,cancellation);
        foreach(var component in FixedActiveComponents(components))VerifyClosure(Active(component.Kind),component.Files,true,cancellation);
        await RequirePublishedHelpersAsync(state,operation,RequireRecoveryHelperClosureAsync,cancellation);
        requireCurrent(operation);
        if(JsonSerializer.Serialize(observed)==JsonSerializer.Serialize(wanted))return;
        ValidateSnapshot(operation,observed);requireCurrent(operation);WriteState(CurrentPath,wanted);
    }
    internal static async Task RequirePublishedHelpersAsync(InstalledStateFiles files,VerifiedSystemOperation operation,
        Func<RecoveryHelperSlot,RecoveryHelperClosure,CancellationToken,Task> requireClosure,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        var selected=SystemBootstrapRecoverySlots.Read(files,operation.InstallationId,operation.PolicyDigest);
        var helper=operation.Components.Single(c=>c.Kind==SystemComponentKind.Helper);
        var wanted=new RecoveryHelperClosure(operation.ManifestDigest,operation.ToVersion,helper.EntrySha256,
            helper.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray());
        static string Inventory(RecoveryHelperClosure value)=>JsonSerializer.Serialize(value.Files.OrderBy(f=>f.RelativePath,StringComparer.Ordinal));
        // Require the completed HelperIntent, not merely a bootable survivor.
        // Preparation already admits interrupted/rollback A/B states through
        // RequireReadyAsync; CommitCurrent may publish only the full candidate.
        if(selected.PendingOperationId is not null||selected.Slots.Any(s=>s.Phase!=RecoveryHelperSlotPhase.Verified||
            s.Closure.ManifestDigest!=wanted.ManifestDigest||s.Closure.Version!=wanted.Version||s.Closure.EntrySha256!=wanted.EntrySha256||Inventory(s.Closure)!=Inventory(wanted)))
            throw new IOException("SYSTEM_BOOTSTRAP_HELPER_SELECTION_INCOMPLETE");
        foreach(var slot in selected.Slots)
        {cancellation.ThrowIfCancellationRequested();await requireClosure(slot.Slot,slot.Closure,cancellation);}
        cancellation.ThrowIfCancellationRequested();
        if(JsonSerializer.Serialize(selected)!=JsonSerializer.Serialize(SystemBootstrapRecoverySlots.Read(files,operation.InstallationId,operation.PolicyDigest)))
            throw new IOException("SYSTEM_BOOTSTRAP_HELPER_SELECTION_CHANGED");
    }
    public async Task RestorePreviousAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
    {
        Bind(operation);ValidateSnapshot(operation,before);
        foreach(var component in before.Components.Where(c=>c.Kind is not(SystemComponentKind.Probe or SystemComponentKind.Helper)))
        {
            var candidate=operation.Components.Single(c=>c.Kind==component.Kind);
            PublishFiles(operation.OperationId,component.Kind,Active(component.Kind),SlotComponent(before.ManifestDigest,component.Kind),component.Files,
                candidate.Files.Select(f=>new SystemBootstrapFileState(f.RelativePath,f.Size,f.Sha256)).ToArray(),cancellation);
        }
        await (helperPublication??throw new IOException("SYSTEM_BOOTSTRAP_A_B_PROVISIONING_REQUIRED")).RestoreAsync(operation,before,cancellation);
        WriteState(CurrentPath,before);await Task.CompletedTask;
    }
    internal Task RequireRecoveryHelperClosureAsync(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)
    {RequireAuthority();VerifyClosure(BootstrapRecoveryLayout.Directory(slot),closure.Files,true,cancellation);return Task.CompletedTask;}
    internal async Task PublishRecoveryHelperSlotAsync(VerifiedSystemOperation operation,RecoveryHelperSlot slot,RecoveryHelperClosure closure,IReadOnlyList<RecoveryHelperClosure> prior,bool rollback,CancellationToken cancellation)
    {
        Bind(operation);if(!rollback)requireCurrent(operation);await RequireDataUnchangedAsync(operation,cancellation);
        var source=SlotComponent(closure.ManifestDigest,SystemComponentKind.Helper);var bytes=binaries.Read(Child(Slot(closure.ManifestDigest),"system-manifest.json"),8*1024*1024);
        if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=closure.ManifestDigest)throw new IOException("HELPER_SLOT_SOURCE_MANIFEST");var manifest=NativeJson.Read<JsonElement>(bytes);
        if(manifest.GetProperty("version").GetString()!=closure.Version)throw new IOException("HELPER_SLOT_SOURCE_VERSION");
        var component=manifest.GetProperty("components").EnumerateArray().Single(c=>c.GetProperty("kind").GetString()==nameof(SystemComponentKind.Helper));
        var members=component.GetProperty("files").EnumerateArray().Select(f=>new SystemBootstrapFileState(f.GetProperty("path").GetString()!,f.GetProperty("size").GetInt64(),f.GetProperty("sha256").GetString()!)).ToArray();
        if(component.GetProperty("entrySha256").GetString()!=closure.EntrySha256||JsonSerializer.Serialize(members.OrderBy(f=>f.RelativePath))!=JsonSerializer.Serialize(closure.Files.OrderBy(f=>f.RelativePath)))throw new IOException("HELPER_SLOT_SOURCE_CLOSURE");
        void Guard()
        {
            Bind(operation);if(!rollback)requireCurrent(operation);using var held=dataLease.DuplicateForDirectory(dataDirectory);
            var binding=WindowsInstallationReader.Parse(state.Read(state.PathOf("bootstrap.json"),1024));if(binding.ActivationReceiptDigest!=operation.PreviousActivationDigest)throw new IOException("HELPER_SLOT_DATA_HEAD");
            (runtime??throw new IOException("HELPER_SLOT_RUNTIME_OBSERVATION_REQUIRED")).RequireAsync(cancellation).GetAwaiter().GetResult();
        }
        Guard();PublishFiles(operation.OperationId,SystemComponentKind.Helper,BootstrapRecoveryLayout.Directory(slot),source,closure.Files,prior.SelectMany(c=>c.Files).ToArray(),cancellation,Guard);
    }
    public void RequireNotQuarantined(VerifiedSystemOperation operation)
    {Bind(operation);if(ReadFailure(operation)>=2)throw new IOException("SYSTEM_BOOTSTRAP_RELEASE_QUARANTINED");}
    public void RecordCandidateFailure(VerifiedSystemOperation operation)
    {
        Bind(operation);var path=FailurePath(operation);var count=ReadFailure(operation);WriteState(path,new FailureCounter(1,operation.ManifestDigest,Math.Min(30,count+1)));
    }
    private int ReadFailure(VerifiedSystemOperation operation)
    {
        var path=FailurePath(operation);
        if(Directory.EnumerateFiles(Path.GetDirectoryName(path)!,Path.GetFileName(path)+".partial-*").Any())throw new IOException("SYSTEM_BOOTSTRAP_FAILURE_STATE");
        try{_=File.GetAttributes(path);}catch(FileNotFoundException){return 0;}
        var value=NativeJson.Read<FailureCounter>(state.Read(path,4096));
        if(value.Format!=1||value.ManifestDigest!=operation.ManifestDigest||value.Count is <0 or >30)throw new IOException("SYSTEM_BOOTSTRAP_FAILURE_STATE");return value.Count;
    }
    private string FailurePath(VerifiedSystemOperation operation)
    {
        var directory=state.PathOf(".authority/bootstrap-failures");if(!Directory.Exists(directory))state.CreateDirectory(directory);return Path.Combine(directory,operation.ManifestDigest+".json");
    }
    private void WriteState<T>(string path,T value)
    {
        var temporary=path+".partial-"+Guid.NewGuid().ToString("N");using(var file=state.Create(temporary)){file.Write(JsonSerializer.SerializeToUtf8Bytes(value));file.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+path,9))throw new IOException("SYSTEM_BOOTSTRAP_STATE_PUBLICATION");
    }
    private void PublishFiles(string operation,SystemComponentKind kind,string destination,string source,SystemBootstrapFileState[] wanted,SystemBootstrapFileState[] other,CancellationToken cancellation,Action? authorize=null)
    {
        VerifyClosure(source,wanted,false,cancellation);
        var allowed=other.GroupBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.Select(f=>f.Sha256).ToHashSet(StringComparer.Ordinal),StringComparer.OrdinalIgnoreCase);
        foreach(var file in wanted.OrderBy(f=>f.RelativePath==FixedSystemComponents.EntryPoint(kind)?1:0).ThenBy(f=>f.RelativePath,StringComparer.Ordinal))
        {
            cancellation.ThrowIfCancellationRequested();var target=Child(destination,file.RelativePath);var candidate=Child(source,file.RelativePath);
            var observed=TryHash(target,kind);if(observed==file.Sha256)continue;
            if(observed is not null&&(!allowed.TryGetValue(file.RelativePath,out var prior)||!prior.Contains(observed)))throw new IOException("SYSTEM_BOOTSTRAP_UNKNOWN_ACTIVE_FILE");
            EnsureParents(destination,Path.GetDirectoryName(target)!,kind);var temporary=Artifact(operation,kind,"staging");
            using(var input=binaries.Open(candidate))using(var output=CreateBinary(temporary,kind))
            {
                var buffer=new byte[1024*1024];long offset=0;int count;while((count=input.Read(offset,buffer))>0){cancellation.ThrowIfCancellationRequested();output.Write(buffer,0,count);offset+=count;}
                if(offset!=file.Size)throw new IOException("SYSTEM_BOOTSTRAP_COPY_CHANGED");output.Flush(true);
            }
            if(TryHash(temporary,kind)!=file.Sha256)throw new IOException("SYSTEM_BOOTSTRAP_COPY_HASH");
            authorize?.Invoke();
            if(observed is null)
            {if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+target,8))throw new Win32Exception();}
            else
            {
                // Keep a backup name for every ReplaceFile attempt. Documented
                // partial-name outcomes are reconciled against the two verified
                // inventories; neither return code nor missing path is success.
                var backup=Artifact(operation,kind,"prior");
                if(!ReplaceFileW(@"\\?\"+target,@"\\?\"+temporary,@"\\?\"+backup,0,IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
            }
            if(TryHash(target,kind)!=file.Sha256)throw new IOException("SYSTEM_BOOTSTRAP_PUBLISHED_HASH");
        }
        foreach(var old in other.Where(f=>!wanted.Any(w=>string.Equals(w.RelativePath,f.RelativePath,StringComparison.OrdinalIgnoreCase))).DistinctBy(f=>f.RelativePath,StringComparer.OrdinalIgnoreCase))
        {
            var target=Child(destination,old.RelativePath);var observed=TryHash(target,kind);if(observed is null)continue;if(!allowed[old.RelativePath].Contains(observed))throw new IOException("SYSTEM_BOOTSTRAP_UNKNOWN_OBSOLETE_FILE");
            authorize?.Invoke();
            // Preserve bytes outside the active closure; never recursively delete.
            if(!MoveFileExW(@"\\?\"+target,@"\\?\"+Artifact(operation,kind,"retired"),8))throw new Win32Exception();
        }
        VerifyClosure(destination,wanted,true,cancellation);
    }
    private string Artifact(string operation,SystemComponentKind kind,string purpose)
    {
        InstalledActivationResolver.Id(operation);var parent=binaries.Root;
        foreach(var part in new[]{"system-backups",operation,kind.ToString()})
        {parent=Path.Combine(parent,part);if(!Directory.Exists(parent))binaries.CreateDirectory(parent);using var held=binaries.PinDirectory(parent);}
        return Path.Combine(parent,purpose+"-"+Guid.NewGuid().ToString("N")+".bin");
    }
    private void VerifyClosure(string directory,SystemBootstrapFileState[] files,bool live,CancellationToken cancellation)
    {
        var expected=new HashSet<string>(files.Select(f=>f.RelativePath),StringComparer.OrdinalIgnoreCase);if(expected.Count!=files.Length)throw new IOException("SYSTEM_BOOTSTRAP_DUPLICATE_FILE");
        foreach(var file in files)
        {
            cancellation.ThrowIfCancellationRequested();FixedSystemComponents.Member(file.RelativePath);FixedSystemComponents.Digest(file.Sha256);
            var path=Child(directory,file.RelativePath);string hash;long length;
            if(live){using var input=OpenBinary(path);length=input.Length;hash=Convert.ToHexStringLower(SHA256.HashData(input));}
            else{using var input=binaries.Open(path);length=input.Length;using var hashSource=new SlotStream(input);hash=Convert.ToHexStringLower(SHA256.HashData(hashSource));}
            if(length!=file.Size||hash!=file.Sha256)throw new IOException("SYSTEM_BOOTSTRAP_INVENTORY_HASH");
        }
        var actual=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Walk(string parent,int depth)
        {
            if(depth>64)throw new IOException("SYSTEM_BOOTSTRAP_DIRECTORY_BOUND");
            foreach(var path in Directory.EnumerateFileSystemEntries(parent))
            {
                var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("SYSTEM_BOOTSTRAP_REPARSE");
                if((attributes&FileAttributes.Directory)!=0)Walk(path,depth+1);
                else if(!actual.Add(Path.GetRelativePath(directory,path).Replace('\\','/'))||actual.Count>files.Length)throw new IOException("SYSTEM_BOOTSTRAP_SLOT_CLOSURE");
            }
        }
        Walk(directory,0);if(!actual.SetEquals(expected))throw new IOException("SYSTEM_BOOTSTRAP_SLOT_CLOSURE");
    }
    private string? TryHash(string path,SystemComponentKind kind)
    {try{using var stream=OpenBinary(path);return Convert.ToHexStringLower(SHA256.HashData(stream));}catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){return null;}catch(FileNotFoundException){return null;}}
    private FileStream OpenBinary(string path)
    {
        RequireAuthority();ValidateParents(Path.GetDirectoryName(path)!);var handle=CreateFileW(@"\\?\"+path,0x80020000,1,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}
        try{ValidateHandle(handle);return new(handle,FileAccess.Read);}catch{handle.Dispose();throw;}
    }
    private void ValidateHandle(SafeFileHandle handle,bool directory=false)
    {
        if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception();if((info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory||!directory&&info.Links!=1)throw new IOException("SYSTEM_BOOTSTRAP_FILE_KIND");
        var code=GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var security);if(code!=0)throw new Win32Exception((int)code);
        try
        {
            var bytes=new byte[GetSecurityDescriptorLength(security)];Marshal.Copy(security,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);
            if(!binaries.Roles!.IsUpdater(acl.Owner!)&&acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544")||acl.DiscretionaryAcl is null||directory&&(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new IOException("SYSTEM_BOOTSTRAP_FILE_OWNER");
            foreach(GenericAce raw in acl.DiscretionaryAcl)
            {
                if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("SYSTEM_BOOTSTRAP_FILE_ACE");if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
                if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("SYSTEM_BOOTSTRAP_FILE_ACE");if(ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544")continue;
                var maximum=binaries.Roles!.IsUpdater(ace.SecurityIdentifier)?0x1301bf:ace.SecurityIdentifier==runtimeSid?0x1200a9:0;
                if(maximum==0||(ace.AccessMask&~maximum)!=0)throw new IOException("SYSTEM_BOOTSTRAP_FILE_ACCESS");
            }
        }
        finally{LocalFree(security);}
    }
    private FileStream CreateBinary(string path,SystemComponentKind kind)
    {
        RequireAuthority();ValidateParents(Path.GetDirectoryName(path)!);var descriptor=new RawSecurityDescriptor(Sddl(kind,false));var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);
        var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=pin.AddrOfPinnedObject()};var memory=Marshal.AllocHGlobal(attributes.Length);
        try
        {
            Marshal.StructureToPtr(attributes,memory,false);var handle=CreateFileW(@"\\?\"+path,0x40020000,0,memory,1,0x80000000,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}return new(handle,FileAccess.Write);
        }
        finally{Marshal.FreeHGlobal(memory);pin.Free();}
    }
    private string Sddl(SystemComponentKind kind,bool directory)
    {
        var flags=directory?"OICI":"";return $"O:{InstalledAuthorityReader.RequireUpdater()}D:P(A;{flags};FA;;;SY)(A;{flags};FA;;;BA)"+
            string.Concat(binaries.Roles!.UpdaterActors.Select(sid=>$"(A;{flags};0x1301bf;;;{sid.Value})"))+$"(A;{flags};0x1200a9;;;{runtimeSid.Value})";
    }
    private void EnsureParents(string root,string directory,SystemComponentKind kind)
    {
        NoReparseParents(root);var relative=Path.GetRelativePath(root,directory);if(relative==".")return;FixedSystemComponents.Member(relative.Replace('\\','/'));var current=root;
        foreach(var part in relative.Split(Path.DirectorySeparatorChar))
        {current=Path.Combine(current,part);if(Directory.Exists(current)){NoReparseParents(current);continue;}var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(Sddl(kind,true));new DirectoryInfo(current).Create(acl);}
    }
    private static void NoReparseParents(string directory)
    {for(var current=new DirectoryInfo(directory);current is not null;current=current.Parent){if((File.GetAttributes(current.FullName)&FileAttributes.ReparsePoint)!=0)throw new IOException("SYSTEM_BOOTSTRAP_REPARSE");}}
    private void ValidateParents(string directory)
    {
        NoReparseParents(directory);var current=Path.GetFullPath(directory);
        while(current.Equals(binaries.Root,StringComparison.OrdinalIgnoreCase)||current.StartsWith(binaries.Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
        {
            using var handle=CreateFileW(@"\\?\"+current,0x20080,7,IntPtr.Zero,3,0x02200000,IntPtr.Zero);if(handle.IsInvalid)throw new Win32Exception();ValidateHandle(handle,true);
            current=Path.GetDirectoryName(current)??throw new IOException("SYSTEM_BOOTSTRAP_PARENT_BOUND");
        }
    }
    private static async Task WaitExited(SafeProcessHandle process,CancellationToken cancellation)
    {while(true){cancellation.ThrowIfCancellationRequested();var status=WaitForSingleObject(process,0);if(status==0)return;if(status!=258)throw new Win32Exception();await Task.Delay(20,cancellation);}}
    private sealed record FailureCounter(int Format,string ManifestDigest,int Count);
    private sealed class SlotStream(WindowsFileBoundary.PrivateReadLease source):Stream
    {
        private long offset;public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;public override long Length=>source.Length;public override long Position{get=>offset;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int start,int count){var read=source.Read(offset,buffer.AsSpan(start,count));offset+=read;return read;}
        public override void Flush(){}public override long Seek(long o,SeekOrigin s)=>throw new NotSupportedException();public override void SetLength(long v)=>throw new NotSupportedException();public override void Write(byte[] b,int o,int c)=>throw new NotSupportedException();
    }
    private sealed class NativeOwnService:IDisposable
    {
        private readonly ServiceHandle manager,service;
        private NativeOwnService(){manager=new(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();service=new(OpenServiceW(manager,InstalledUpdaterLayout.AuthorityServiceName,0x35));if(service.IsInvalid){manager.Dispose();throw new Win32Exception();}}
        internal static NativeOwnService Authority()=>new();
        private void RequireConfiguration()
        {
            QueryServiceConfigW(service,IntPtr.Zero,0,out var size);if(size is <44 or >16384)throw new IOException("SYSTEM_AUTHORITY_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)size);
            try{if(!QueryServiceConfigW(service,memory,size,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<ServiceConfig>(memory);
                if(config.Type!=16||Marshal.PtrToStringUni(config.Account)!=@"NT SERVICE\"+InstalledUpdaterLayout.AuthorityServiceName||Marshal.PtrToStringUni(config.Image)!="\""+InstalledUpdaterLayout.AuthorityExecutable+"\" --service")throw new UnauthorizedAccessException("SYSTEM_AUTHORITY_CONFIGURATION");}
            finally{Marshal.FreeHGlobal(memory);}
        }
        private ServiceStatus Status(){RequireConfiguration();if(!QueryServiceStatusEx(service,0,out var value,(uint)Marshal.SizeOf<ServiceStatus>(),out _))throw new Win32Exception();if(value.Type!=16)throw new IOException("SYSTEM_AUTHORITY_SERVICE_TYPE");return value;}
        internal async Task StopAndWaitUnlessSelfAsync(CancellationToken cancellation)
        {
            var current=Status();if(current.State==1)return;if(current.Pid==0||current.Pid==(uint)Environment.ProcessId)throw new IOException("SYSTEM_HANDOFF_HELPER_REQUIRED");
            using var process=current.State is 2 or 4?WindowsAuthorityServiceIdentity.Pin(current.Pid):OpenProcess(0x00101000,false,current.Pid);
            if(process.IsInvalid)throw new Win32Exception();var again=Status();if(again.Pid!=current.Pid)throw new IOException("SYSTEM_AUTHORITY_GENERATION_CHANGED");
            if(current.State!=3&&!ControlService(service,1,out _)){var error=Marshal.GetLastWin32Error();if(error!=1062)throw new Win32Exception(error);}await WaitExited(process,cancellation);
        }
        internal void StartIfStopped()
        {var current=Status();if(current.State is 2 or 4){if(!WindowsAuthorityServiceIdentity.IsCurrentServer(current.Pid))throw new UnauthorizedAccessException("SYSTEM_AUTHORITY_CONFIGURATION");return;}if(current.State!=1)throw new IOException("SYSTEM_AUTHORITY_NOT_STOPPED");if(!StartServiceW(service,0,IntPtr.Zero))throw new Win32Exception();}
        public void Dispose(){service.Dispose();manager.Dispose();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential,Pack=4)]private struct FileInfo{public uint Attributes;public long Creation,Access,Write;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatus{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceConfig{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid{internal ServiceHandle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInfo info);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle handle,int type,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ReplaceFileW(string target,string replacement,string backup,uint flags,IntPtr exclude,IntPtr reserved);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint rights,bool inherit,uint pid);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(ServiceHandle manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out ServiceStatus status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(ServiceHandle service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(ServiceHandle service,uint control,out ServiceStatus status);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool StartServiceW(ServiceHandle service,uint count,IntPtr args);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
