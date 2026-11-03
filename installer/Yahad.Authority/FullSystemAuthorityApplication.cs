using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal enum FullSystemBootDisposition {NoPending,Healthy,PreviousRestored,RecoveryRequired,HandedToReplacementAuthority}

// Additional composition of the existing Authority/router/scheduler and SAME
// intake transport. The ordinary SCM application owns all lifecycle instances
// before start; Program remains inactive until installer/native acceptance.
internal sealed class FullSystemAuthorityApplication:IDisposable
{
    private readonly InstalledAuthorityComposition composition;private readonly NativeCandidateIntake intake;
    private readonly SystemCandidateIntake systemIntake;
    internal FullSystemUpdatePolicy Policy{get;}internal FullSystemModeControl Control{get;}
    internal InstalledFullSystemExecutionKernel Kernel{get;}internal FullSystemUpdateCoordinator Coordinator{get;}
    private FullSystemAuthorityApplication(InstalledAuthorityComposition composition,InstalledStateFiles binaries,
        Func<WindowsScmRuntimeLifecycle> current,Func<WindowsScmRuntimeLifecycle> create,Action<WindowsScmRuntimeLifecycle> track,Func<DateTimeOffset> clock)
    {
        this.composition=composition;var files=composition.Files;var anchor=composition.Anchor;
        if(anchor.FullSystemPolicyDigest is null)throw new UnauthorizedAccessException("FULL_SYSTEM_EXPLICIT_CONSENT_REQUIRED");
        intake=new(files,anchor,clock);FullSystemUpdatePolicy? policy=null;
        try
        {
            systemIntake=new(files,anchor,new InstalledSystemSlots(binaries),intake,()=>{if(policy is null)throw new IOException("FULL_SYSTEM_POLICY_NOT_READY");_=policy.Enrollment;},ObserveCandidateEnvironment);
            Policy=policy=FullSystemUpdatePolicy.Open(files,anchor.InstallationId,anchor.FullSystemPolicyDigest,clock,systemIntake);
            Kernel=new(files,binaries,anchor,Policy,current,create,track,clock);Coordinator=new(Policy,Kernel);
            Control=new(files,anchor,Policy,systemIntake,clock);
            composition.Router.ConfigureFullSystem(Control,Kernel.PublishedStateForProposal,Coordinator.ExecuteAsync,()=>Coordinator.Phase=="recovery_required"?"recovery_required":
                Coordinator.CurrentOperationId==composition.Router.FullSystemOperationId?(Coordinator.Phase=="healthy"&&current().Phase!="healthy"?"recovery_required":Coordinator.Phase):"idle",
                ()=>Coordinator.CurrentOperationId==composition.Router.FullSystemOperationId&&Coordinator.CandidateStartIssued);
        }
        catch{intake.Dispose();throw;}
    }
    internal static FullSystemAuthorityApplication Create(InstalledAuthorityComposition composition,Func<WindowsScmRuntimeLifecycle> current,
        Func<WindowsScmRuntimeLifecycle> create,Action<WindowsScmRuntimeLifecycle> track,Func<DateTimeOffset> clock)
    {
        var files=composition.Files;var binaries=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,files.Roles??throw new IOException("FULL_SYSTEM_NATIVE_ROLES_REQUIRED"),files.ProbeExecutable,files.ProbeDigest);
        return new(composition,binaries,current,create,track,clock);
    }
    internal static FullSystemHelperComposition CreateHelperComposition(WindowsFileBoundary.DataLease? retainedSerialLease=null)
        =>new(retainedSerialLease);
    internal static FullSystemHelperComposition CreateProbeComposition()=>new(null,true);
    internal async Task<FullSystemBootDisposition> BootBeforeRuntimeAsync(CancellationToken cancellation)
    {
        string? pending=null;
        try
        {
            await NativeCleanupOwnershipGate.RetryRetainedAsync(cancellation);
            NativeCleanupOwnershipGate.RequireClear();
            pending=Kernel.PendingOperation();
            if(pending is null)
            {
                var orphaned=Kernel.UnadmittedRequests();if(orphaned.Count==0)return FullSystemBootDisposition.NoPending;
                await Kernel.ReconcileUnadmittedRequestsAsync(orphaned,cancellation);
                foreach(var id in orphaned)await composition.Router.CompleteUnadmittedFullRequestAsync(id,cancellation);
                return FullSystemBootDisposition.PreviousRestored;
            }
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await Coordinator.ResumeAsync(pending,deadline.Token);
            var outcome=Coordinator.Phase switch{"healthy"=>FullSystemBootDisposition.Healthy,"previous_restored"=>FullSystemBootDisposition.PreviousRestored,"restarting_updater"=>FullSystemBootDisposition.HandedToReplacementAuthority,_=>FullSystemBootDisposition.RecoveryRequired};
            if(outcome!=FullSystemBootDisposition.HandedToReplacementAuthority&&File.Exists(composition.Files.PathOf(".authority/rpc-"+pending+".json")))await composition.Router.CompleteFullSystemRequestAsync(pending,Coordinator.Phase,cancellation);
            if(outcome==FullSystemBootDisposition.RecoveryRequired)composition.Router.MarkBootRecoveryRequired();return outcome;
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
        catch
        {
            composition.Router.MarkBootRecoveryRequired();
            if(pending is not null&&File.Exists(composition.Files.PathOf(".authority/rpc-"+pending+".json")))
                await composition.Router.CompleteFullSystemRequestAsync(pending,"recovery_required",cancellation);
            return FullSystemBootDisposition.RecoveryRequired;
        }
    }
    internal async Task<ScheduledUpdateCandidate?> CheckSourceAsync(CancellationToken cancellation)
    {
        // A recovery's immutable source/previous payload cannot be rotated out
        // by another download while its native/data journals are unfinished.
        if(Kernel.PendingOperation() is not null)throw new IOException("FULL_SYSTEM_SOURCE_RECOVERY_PENDING");
        return await Control.CheckSourceAsync(systemIntake,ObserveCandidateEnvironment,cancellation);
    }
    private SystemCandidateEnvironment ObserveCandidateEnvironment()
        =>ObserveCandidateEnvironment(composition.Files,composition.Anchor);
    internal static SystemCandidateEnvironment ObserveCandidateEnvironment(InstalledStateFiles files,InstalledAuthorityAnchor anchor)
    {
        // Receipt revalidation during recovery must also work between the
        // four-record writer's publication boundaries. The final execution
        // admission separately observes the fully committed head and real DB.
        var head=NativeJson.Read<InstalledActivation>(files.Read(files.PathOf("activation.json"),16384));
        if(head.InstallationId!=anchor.InstallationId||head.MinimumSchema<1||head.MaximumSchema<head.MinimumSchema||head.MaximumSchema>100000)throw new IOException("FULL_SYSTEM_SOURCE_ENVIRONMENT");
        var bytes=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024);
        if(InstalledActivationResolver.Hash(bytes)!=head.ReleaseDigest)throw new IOException("FULL_SYSTEM_SOURCE_CURRENT_PAYLOAD");
        var version=NativeJson.Read<JsonElement>(bytes).GetProperty("version").GetString()!;
        return new(version,head.MaximumSchema,Math.Max(1,Environment.OSVersion.Version.Build),new Dictionary<string,int>
        {{"system",1},{"handoff",1},{"authorityRuntime",1},{"activation",1},{"journal",1},{"recovery",1},{"policy",2}});
    }
    public void Dispose()=>intake.Dispose();
}

// Constructed by the fixed initial helper entry or the installer-provisioned
// A/B boot service. It performs no source download, service provisioning or ACL
// repair. The existing helper owns all adopted/transferred native handles.
internal sealed class FullSystemHelperComposition:IDisposable
{
    private readonly InstalledStateFiles files,binaries;private readonly InstalledAuthorityAnchor anchor;private readonly NativeCandidateIntake intake;
    private readonly WindowsFileBoundary.DataLease? providedSerial;private readonly FullSystemUpdatePolicy policy;
    private SystemBootstrapHelperCustody? custody;private WindowsFileBoundary.DataLease? recoveringSerial;
    private SystemBootstrapAuthorityChannel? channel;private string? channelOperation;
    internal SystemBootstrapHelperHooks Hooks{get;}
    internal FullSystemHelperComposition(WindowsFileBoundary.DataLease? retainedSerial,bool probe=false)
    {
        providedSerial=retainedSerial;anchor=InstalledAuthorityReader.Read(probe);var roles=SystemBootstrapHelperNative.Roles();
        var baseline=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,roles,InstalledUpdaterLayout.ProbeExecutable,anchor.ProbeSha256??throw new IOException("FULL_HELPER_BASELINE_PROBE_REQUIRED"));
        binaries=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,roles,baseline.ProbeExecutable,baseline.ProbeDigest);
        var selected=InstalledOperationalProbe.Require(baseline,binaries,anchor.InstallationId,anchor.FullSystemPolicyDigest,baseline.ProbeExecutable,baseline.ProbeDigest);
        files=InstalledStateFiles.Installed(baseline.Root,roles,selected.Executable,selected.Sha256);intake=new(files,anchor,()=>DateTimeOffset.UtcNow);
        FullSystemUpdatePolicy? opening=null;
        try
        {
            var source=new SystemCandidateIntake(files,anchor,new InstalledSystemSlots(binaries),intake,()=>{if(opening is null)throw new IOException("FULL_HELPER_POLICY_NOT_READY");_=opening.Enrollment;},
                ()=>FullSystemAuthorityApplication.ObserveCandidateEnvironment(files,anchor));
            policy=opening=FullSystemUpdatePolicy.Open(files,anchor.InstallationId,anchor.FullSystemPolicyDigest,()=>DateTimeOffset.UtcNow,source);
            Hooks=new(policy,(op,previous,c)=>Channel(op).ObserveAuthorityAsync(op,previous,c),(op,c)=>Channel(op).ObserveRuntimeAsync(op,c),
                RequireDataUnchangedAsync,(op,c)=>SystemBootstrapCandidateProbe.VerifyAsync(files,binaries,op,policy,c),CaptureCustodyAsync,
                async(op,c)=>{policy.RequireCurrent(op);await RequireDataUnchangedAsync(op,c);policy.RequireCurrent(op);},
                (owned,op,c)=>Channel(op).TransferAsync(owned,op,c),RecoverActualCustodyAsync);
        }
        catch{intake.Dispose();throw;}
    }
    private NativeFullSystemPlan Original(VerifiedSystemOperation operation)
    {
        var value=NativeJson.Read<NativeFullSystemPlan>(files.Read(files.PathOf(".authority/system-native/"+operation.OperationId+".json"),65536));
        if(value.Format!=1||value.InstallationId!=operation.InstallationId||value.OperationId!=operation.OperationId||value.PreviousActivation is null||value.PreviousActivation.Length is <2 or >16384||InstalledActivationResolver.Hash(value.PreviousActivation)!=operation.PreviousActivationDigest)
            throw new IOException("FULL_HELPER_ORIGINAL_PLAN_BINDING");
        var head=NativeJson.Read<InstalledActivation>(value.PreviousActivation);if(head.InstallationId!=operation.InstallationId||head.Phase!="committed")throw new IOException("FULL_HELPER_ORIGINAL_HEAD");return value;
    }
    private static string DirectoryOf(InstalledStateFiles files,InstalledActivation head)
    {
        InstalledActivationResolver.Id(head.DataId);
        if(head.Recovery is not {} restore)return files.PathOf("datasets/"+head.DataId+"/data");
        InstalledActivationResolver.Id(restore.SourceDataId);InstalledActivationResolver.Id(restore.OperationId);
        return files.PathOf("datasets/"+restore.SourceDataId+"/restore-"+restore.OperationId+"/completed/data");
    }
    private WindowsFileBoundary.DataLease Serial=>custody?.Serial??recoveringSerial??providedSerial??throw new IOException("FULL_HELPER_ACTUAL_SERIAL_REQUIRED");
    private SystemBootstrapAuthorityChannel Channel(VerifiedSystemOperation operation)
    {
        if(channel is not null){if(channelOperation!=operation.OperationId)throw new IOException("FULL_HELPER_CHANNEL_OPERATION_CHANGED");return channel;}
        using var journal=new SystemBootstrapJournal(files,operation,Serial);var before=journal.Current?.Before??throw new IOException("FULL_HELPER_PREPARATION_REQUIRED");
        channelOperation=operation.OperationId;return channel=new(files,binaries,policy,before);
    }
    private async Task CaptureCustodyAsync(SystemBootstrapHelperCustody owned,string directory,VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        policy.RequireRecoveryEvidence(operation);var head=NativeJson.Read<InstalledActivation>(Original(operation).PreviousActivation);
        if(directory!=DirectoryOf(files,head))throw new IOException("FULL_HELPER_SOURCE_DIRECTORY");
        await owned.Runtime.RequireAsync(cancellation);using(var held=owned.Data.DuplicateForDirectory(directory)){}custody=owned;
        await RequireDataUnchangedAsync(operation,cancellation);
    }
    private async Task RequireDataUnchangedAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        policy.RequireRecoveryEvidence(operation);var owned=custody??throw new IOException("FULL_HELPER_CUSTODY_REQUIRED");var plan=Original(operation);var head=NativeJson.Read<InstalledActivation>(plan.PreviousActivation);
        if(!files.Read(files.PathOf("activation.json"),16384).AsSpan().SequenceEqual(plan.PreviousActivation))throw new IOException("FULL_HELPER_SOURCE_HEAD_CHANGED");
        using var journal=new InstalledUpdateJournal(files,anchor.InstallationId,owned.Serial);journal.RequireNoOtherUnfinished(operation.OperationId);var record=journal.Resume(operation.OperationId);var p=record.Plan;
        if(p.PreviousActivationDigest!=operation.PreviousActivationDigest||p.PreviousRelease!=head.ReleaseDigest||p.CandidateRelease!=operation.ApplicationBundleDigest||p.SchemaBefore!=operation.SourceSchema||p.FullSystemPolicyDigest!=operation.PolicyDigest||record.BackupId is null||record.BackupReceiptDigest is null||journal.CandidateStartWasRecorded||journal.EffectsMayHaveBeenReleased)
            throw new IOException("FULL_HELPER_VERIFIED_BACKUP_REQUIRED");
        await owned.Runtime.RequireAsync(cancellation);using var inspector=new NativeSqliteInspector(files.ProbeExecutable,files.ProbeDigest);
        using var recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,head.Recovery?.SourceDataId??head.DataId,owned.Runtime.Duplicate(),inspector,head.Recovery?.OperationId,cancellation,owned.Data);
        await recovery.RequireSourceMatchesBackupAsync(record.BackupId,record.BackupReceiptDigest,operation.SourceSchema,cancellation);
        if(!files.Read(files.PathOf("activation.json"),16384).AsSpan().SequenceEqual(plan.PreviousActivation))throw new IOException("FULL_HELPER_SOURCE_HEAD_CHANGED");
    }
    private async Task<SystemBootstrapRecoveredCustody> RecoverActualCustodyAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        policy.RequireRecoveryEvidence(operation);var plan=Original(operation);var head=NativeJson.Read<InstalledActivation>(plan.PreviousActivation);var directory=DirectoryOf(files,head);
        WindowsFileBoundary.DataLease? serial=null,data=null;RetainedRuntimeQuiescence? proof=null;
        try
        {
            serial=providedSerial?.DuplicateForDirectory(files.PathOf(".authority/update-serial"))??files.Lease(files.PathOf(".authority/update-serial"));recoveringSerial=serial;
            data=files.Lease(directory);var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(files.PathOf(".authority/bootstrap-helper-active.json"),65536));SystemBootstrapHelperApplication.RequireCapsule(capsule);
            if(capsule.OperationId!=operation.OperationId||capsule.ManifestDigest!=operation.ManifestDigest||capsule.DataDirectory!=directory)throw new IOException("FULL_HELPER_COLD_CAPSULE_BINDING");
            bool forward;
            using(var journal=new SystemBootstrapJournal(files,operation,serial))
            {
                var phase=journal.Current?.Phase??throw new IOException("FULL_HELPER_COLD_JOURNAL_REQUIRED");
                if(phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)throw new IOException("FULL_HELPER_DATA_ONLY_RECOVERY_REQUIRED");
                forward=phase is SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished||phase==SystemBootstrapPhase.RecoveryRequired&&journal.HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!journal.HasPhase(SystemBootstrapPhase.RollbackIntent);
            }
            if(forward)try{policy.RequireCurrent(operation);}catch(UnauthorizedAccessException){forward=false;}
            if(forward)await Channel(operation).ObserveAuthorityAsync(operation,false,cancellation);
            else
            {
                await SystemBootstrapNative.StopAuthorityForColdRepairAsync(files,policy,operation,capsule,data,serial,cancellation);
                await SystemBootstrapNative.RepairPreviousBootstrapAsync(files,binaries,anchor,policy,operation,capsule,data,serial,cancellation);
                await Channel(operation).ObservePreviousForRepairAsync(operation,cancellation);
            }
            proof=await Channel(operation).ObserveRuntimeAsync(operation,cancellation);await proof.RequireAsync(cancellation);
            var recovered=SystemBootstrapHelperCustody.FromRecoveredNativeOwners(proof,data,serial);proof=null;data=null;serial=null;recoveringSerial=recovered.Serial;
            return new(recovered,directory);
        }
        finally{proof?.Dispose();data?.Dispose();serial?.Dispose();if(custody is null)recoveringSerial=null;}
    }
    public void Dispose(){channel?.Dispose();intake.Dispose();}
}
