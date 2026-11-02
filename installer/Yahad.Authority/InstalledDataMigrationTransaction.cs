using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;
internal sealed record InstalledDataCandidate(string DataId,string Directory,string OperationId,string ReleaseDigest,InstalledMigration? Migration);
internal enum FullDataRecoveryResult {RestoredPrevious,HealthyCurrent,ForwardRepairRequired,RecoveryRequired}
internal sealed record InstalledDataRecovery(InstallationBinding? Binding,FullDataRecoveryResult Result,bool PreservedNewData);

// The full enrollment adds an explicit data-transition operation to the SAME
// backup/journal/writer machinery. It never upgrades a legacy routine grant.
internal sealed class InstalledDataMigrationTransaction:IDisposable,IProbationEffectsJournal
{
    private readonly InstalledStateFiles files;private readonly InstalledAuthorityAnchor anchor;private readonly FullSystemUpdatePolicy policy;
    private readonly VerifiedSystemOperation system;private readonly Func<DateTimeOffset> clock;private readonly InstalledActivation before;
    private readonly byte[] beforeBytes;private readonly SystemBootstrapJournal bootstrap;
    private readonly InstalledUpdateJournal journal;private RetainedRuntimeQuiescence sourceGeneration;private WindowsFileBoundary.DataLease sourceLease;
    private readonly NativeSqliteInspector inspector;private readonly string sourceDataId;private readonly string? sourceRestore;
    private NativeRecoveryFixture? recovery;private NativeBackupResult? backup;private WindowsFileBoundary.DataLease? cloneLease;
    private VerifiedMigrationSpec? spec;private VerifiedMigration? verified;private ProtectedOperation? operation;private NativeOfflineMigration? execution;
    private ProbationEffectsCoordinator? effects;private bool disposed,healthy;
    private NativeRestoreResult? compatibleClone;private InstalledDataCandidate? candidate;private bool sourceLeaseReleased;
    private IAuthorityProbationLifecycle? probationRuntime;private ProbationRuntimeBinding? probationBinding;
    private RetainedRuntimeQuiescence? recoveryGeneration;
    private InstallationBinding? terminalRestartBinding;private bool terminalRestartCompleted,restartWasTerminal;private int terminalRestartAttempt,currentRestartAttempt;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,InstalledDataMigrationTransaction> failedOwners=new();
    internal InstalledUpdateRecord Evidence=>journal.Current??throw new IOException("DATA_MIGRATION_NOT_BEGUN");
    internal VerifiedMigrationSpec? CurrentSpec=>spec;
    internal InstalledDataCandidate? Candidate=>candidate;
    internal bool RequiresMigration=>system.TargetSchema>system.SourceSchema;
    internal WindowsFileBoundary.DataLease DuplicateSourceLease()=>sourceLease.DuplicateForDirectory(SourceDirectory(files,before));
    internal WindowsFileBoundary.DataLease DuplicateSerialLease()=>journal.DuplicateSerialLease();
    internal bool EffectsMayHaveBeenReleased=>journal.EffectsMayHaveBeenReleased||journal.FullRollbackEffectsMayHaveBeenReleased;
    internal bool CandidateStartWasRecorded=>journal.CandidateStartWasRecorded;
    private InstalledDataMigrationTransaction(InstalledStateFiles files,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,VerifiedSystemOperation system,
        Func<DateTimeOffset> clock,InstalledActivation before,byte[] beforeBytes,SystemBootstrapJournal bootstrap,InstalledUpdateJournal journal,RetainedRuntimeQuiescence sourceGeneration,WindowsFileBoundary.DataLease sourceLease,NativeSqliteInspector inspector)
    {this.files=files;this.anchor=anchor;this.policy=policy;this.system=system;this.clock=clock;this.before=before;this.beforeBytes=beforeBytes;this.bootstrap=bootstrap;this.journal=journal;this.sourceGeneration=sourceGeneration;this.sourceLease=sourceLease;this.inspector=inspector;sourceDataId=before.Recovery?.SourceDataId??before.DataId;sourceRestore=before.Recovery?.OperationId;}
    internal static InstalledDataMigrationTransaction Begin(InstalledStateFiles files,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,
        VerifiedSystemOperation system,SystemBootstrapJournal bootstrap,RetainedRuntimeQuiescence retainedSourceGeneration,
        WindowsFileBoundary.DataLease retainedSerialLease,WindowsFileBoundary.DataLease retainedSourceLease,Func<DateTimeOffset> clock)
    {
        policy.RequireCurrent(system);var complete=bootstrap.Current??throw new IOException("DATA_MIGRATION_BOOTSTRAP_REQUIRED");
        if(complete.Phase is not(SystemBootstrapPhase.Prepared or SystemBootstrapPhase.CustodyConfirmed or SystemBootstrapPhase.Complete)||complete.OperationId!=system.OperationId||complete.InstallationId!=system.InstallationId||complete.ManifestDigest!=system.ManifestDigest||
            complete.GrantId!=system.GrantId||complete.GrantBindingDigest!=system.GrantBindingDigest||system.TargetSchema<system.SourceSchema)throw new IOException("DATA_MIGRATION_BOOTSTRAP_BINDING");
        var head=new InstalledIntakeSelection(files,anchor,clock).Head();var originalBytes=files.Read(files.PathOf("activation.json"),16384);var headDigest=InstalledActivationResolver.Hash(originalBytes);
        if(headDigest!=system.PreviousActivationDigest||head.InstallationId!=system.InstallationId||system.SourceSchema<head.MinimumSchema||system.SourceSchema>head.MaximumSchema)throw new IOException("DATA_MIGRATION_SOURCE_HEAD");
        _=RequirePayload(files,system);
        if(string.IsNullOrEmpty(files.ProbeExecutable)||string.IsNullOrEmpty(files.ProbeDigest))throw new IOException("DATA_MIGRATION_IMMUTABLE_PROBE_REQUIRED");
        var source=SourceDirectory(files,head);var adoptedSource=retainedSourceLease.DuplicateForDirectory(source);InstalledUpdateJournal? journal=null;NativeSqliteInspector? inspector=null;
        try
        {
            inspector=new(files.ProbeExecutable,files.ProbeDigest);journal=new(files,anchor.InstallationId,retainedSerialLease);
            journal.Begin(new(1,anchor.InstallationId,system.OperationId,head.DataId,headDigest,head.ReleaseDigest,system.ApplicationBundleDigest,system.SourceSchema,
                head.MinimumSchema,head.MaximumSchema,system.TargetSchema,system.TargetSchema,head.PolicyDigest,head.PinDigest,head.MasterKeyDigest,system.TargetSchema>system.SourceSchema?system.TargetSchema:null,FullSystemPolicyDigest:system.PolicyDigest));
            return new(files,anchor,policy,system,clock,head,originalBytes,bootstrap,journal,retainedSourceGeneration.Duplicate(),adoptedSource,inspector);
        }
        catch{inspector?.Dispose();journal?.Dispose();adoptedSource.Dispose();throw;}
    }
    private static string SourceDirectory(InstalledStateFiles files,InstalledActivation head)=>head.Recovery is { } r
        ?files.PathOf("datasets/"+r.SourceDataId+"/restore-"+r.OperationId+"/completed/data"):files.PathOf("datasets/"+head.DataId+"/data");
    private static JsonElement RequirePayload(InstalledStateFiles files,VerifiedSystemOperation system)
    {
        var bytes=files.Read(files.PathOf("payloads/"+system.ApplicationBundleDigest+"/bundle.json"),16*1024*1024);
        if(InstalledActivationResolver.Hash(bytes)!=system.ApplicationBundleDigest)throw new IOException("DATA_MIGRATION_APP_BUNDLE_BINDING");var manifest=NativeJson.Read<JsonElement>(bytes);
        if(manifest.GetProperty("version").GetString()!=system.ToVersion)throw new IOException("DATA_MIGRATION_VERSION_BINDING");
        if(system.TargetSchema==system.SourceSchema)return manifest;
        var declaration=manifest.GetProperty("migration");
        if(declaration.GetProperty("format").GetInt32()!=1||declaration.GetProperty("entry").GetString()!=OfflineMigrationPlan.Entry||
            declaration.GetProperty("targetSchema").GetInt32()!=system.TargetSchema||!declaration.GetProperty("fromSchemas").EnumerateArray().Any(x=>x.GetInt32()==system.SourceSchema)||manifest.GetProperty("version").GetString()!=system.ToVersion)
            throw new IOException("DATA_MIGRATION_DECLARATION");
        var records=manifest.GetProperty("files").EnumerateArray().Where(x=>x.GetProperty("path").GetString()==OfflineMigrationPlan.Entry).ToArray();
        if(records.Length!=1||records[0].GetProperty("size").GetInt64()<1)throw new IOException("DATA_MIGRATION_ENTRY_MISSING");InstalledActivationResolver.Digest(records[0].GetProperty("sha256").GetString()!);return manifest;
    }
    internal async Task BackupAsync(CancellationToken cancellation=default)
    {
        policy.RequireCurrent(system);if(Evidence.Checkpoint!=UpdateCheckpoint.Prepared)throw new IOException("DATA_MIGRATION_BACKUP_ORDER");
        await sourceGeneration.RequireAsync(cancellation);journal.Append(UpdateCheckpoint.Quiesced);
        recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceDataId,sourceGeneration.Duplicate(),inspector,sourceRestore,cancellation,sourceLease);
        backup=await recovery.BackupAsync(cancellation:cancellation);if(backup.ObservedSchema!=system.SourceSchema)throw new IOException("DATA_MIGRATION_SOURCE_SCHEMA");journal.Append(UpdateCheckpoint.BackupVerified,backup);
    }
    private void RequireBootstrapComplete()
    {
        var current=bootstrap.Current??throw new IOException("DATA_MIGRATION_BOOTSTRAP_REQUIRED");
        if(current.Phase!=SystemBootstrapPhase.Complete||current.OperationId!=system.OperationId||current.ManifestDigest!=system.ManifestDigest||files.ProbeDigest!=system.Components.Single(x=>x.Kind==SystemComponentKind.Probe).EntrySha256)throw new IOException("DATA_MIGRATION_BOOTSTRAP_NOT_COMPLETE");
    }
    internal void DetachForBootstrap(SystemBootstrapHelperSession helper)
    {
        if(Evidence.Checkpoint!=UpdateCheckpoint.BackupVerified)throw new IOException("DATA_MIGRATION_CUSTODY_REQUIRED");helper.RequireReadyCustody(system);
        recovery?.Dispose();recovery=null;inspector.Dispose();sourceLease.Dispose();sourceGeneration.Dispose();journal.Dispose();disposed=true;
    }
    internal static async Task<InstalledDataMigrationTransaction> ResumeAfterBootstrapAsync(InstalledStateFiles files,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,
        VerifiedSystemOperation system,SystemBootstrapJournal bootstrap,RetainedRuntimeQuiescence retainedSourceGeneration,
        WindowsFileBoundary.DataLease retainedSerialLease,WindowsFileBoundary.DataLease retainedSourceLease,Func<DateTimeOffset> clock,CancellationToken cancellation=default)
        =>await ResumeForRecoveryAsync(files,anchor,policy,system,bootstrap,retainedSourceGeneration,retainedSerialLease,retainedSourceLease,clock,files.Read(files.PathOf("activation.json"),16384),cancellation);
    internal static async Task<InstalledDataMigrationTransaction> ResumeForRecoveryAsync(InstalledStateFiles files,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,
        VerifiedSystemOperation system,SystemBootstrapJournal bootstrap,RetainedRuntimeQuiescence retainedSourceGeneration,
        WindowsFileBoundary.DataLease retainedSerialLease,WindowsFileBoundary.DataLease retainedSourceLease,Func<DateTimeOffset> clock,byte[] verifiedPreviousActivationBytes,CancellationToken cancellation=default)
    {
        policy.RequireRecoveryEvidence(system);var bytes=verifiedPreviousActivationBytes.ToArray();var head=NativeJson.Read<InstalledActivation>(bytes);
        if(InstalledActivationResolver.Hash(bytes)!=system.PreviousActivationDigest||head.InstallationId!=system.InstallationId)throw new IOException("DATA_MIGRATION_RESUME_SOURCE_HEAD");await retainedSourceGeneration.RequireAsync(cancellation);
        if(failedOwners.TryGetValue(system.OperationId,out var retained))
        {
            if(retained.disposed||retained.files.Root!=files.Root||AuthorityProtocol.Digest(retained.system)!=AuthorityProtocol.Digest(system))throw new IOException("DATA_MIGRATION_RETAINED_OWNER_BINDING");
            retained.RequireRecoveryBootstrap();
            if(retained.sourceLeaseReleased)
            {retained.sourceLease=retainedSourceLease.DuplicateForDirectory(SourceDirectory(files,head));retained.sourceLeaseReleased=false;}
            var refreshed=retainedSourceGeneration.Duplicate();retained.sourceGeneration.Dispose();retained.sourceGeneration=refreshed;return retained;
        }
        var lease=retainedSourceLease.DuplicateForDirectory(SourceDirectory(files,head));InstalledUpdateJournal? journal=null;NativeSqliteInspector? inspector=null;InstalledDataMigrationTransaction? resumed=null;
        try
        {
            journal=new(files,anchor.InstallationId,retainedSerialLease);var record=journal.Resume(system.OperationId);var p=record.Plan;
            if(p.FullSystemPolicyDigest!=system.PolicyDigest||p.PreviousActivationDigest!=system.PreviousActivationDigest||p.PreviousRelease!=head.ReleaseDigest||p.CandidateRelease!=system.ApplicationBundleDigest||p.DataId!=head.DataId||p.SchemaBefore!=system.SourceSchema||p.CandidateMaximumSchema!=system.TargetSchema||p.MasterKeyDigest!=head.MasterKeyDigest||p.PolicyDigest!=head.PolicyDigest||p.PinDigest!=head.PinDigest)throw new IOException("DATA_MIGRATION_RESUME_JOURNAL_BINDING");
            if(record.BackupId is null&&(journal.CandidateStartWasRecorded||record.MigrationAttempt is not null))throw new IOException("DATA_MIGRATION_RESUME_VERIFIED_BACKUP_REQUIRED");
            inspector=new(files.ProbeExecutable,files.ProbeDigest);resumed=new(files,anchor,policy,system,clock,head,bytes,bootstrap,journal,retainedSourceGeneration.Duplicate(),lease,inspector);resumed.RequireRecoveryBootstrap();
            resumed.recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,resumed.sourceDataId,resumed.sourceGeneration.Duplicate(),inspector,resumed.sourceRestore,cancellation,lease);
            if(record.BackupId is {} backupId&&record.BackupReceiptDigest is {} backupDigest)
            {if(!journal.FullRollbackWasRecorded)await resumed.recovery.RequireSourceMatchesBackupAsync(backupId,backupDigest,system.SourceSchema,cancellation);resumed.backup=new(backupId,backupDigest,system.SourceSchema);}
            var current=NativeJson.Read<InstalledActivation>(files.Read(files.PathOf("activation.json"),16384));
            if(current.InstallationId!=head.InstallationId||current.MasterKeyDigest!=head.MasterKeyDigest||current.PolicyDigest!=head.PolicyDigest||current.PinDigest!=head.PinDigest||
                current.ReleaseDigest!=head.ReleaseDigest&&current.ReleaseDigest!=system.ApplicationBundleDigest)throw new IOException("DATA_MIGRATION_RESUME_CURRENT_HEAD");
            var opPath=resumed.OperationPath;
            if(Path.Exists(opPath))
            {
                resumed.operation=NativeJson.Read<ProtectedOperation>(files.Read(opPath,32768));var op=resumed.operation;
                if(record.MigrationAttempt is not {} boundAttempt||op.ExpectedActivationDigest!=system.PreviousActivationDigest||op.ExpectedEpoch!=head.Epoch||op.ReleaseDigest!=system.ApplicationBundleDigest||op.InstallationId!=system.InstallationId||op.Recovery is null||op.DataId!=boundAttempt.NewDataId||op.Nonce!=boundAttempt.OperationId||op.Recovery.OperationId!=boundAttempt.CloneOperationId)throw new IOException("DATA_MIGRATION_RESUME_OPERATION");
                var directory=files.PathOf("datasets/"+op.Recovery.SourceDataId+"/restore-"+op.Recovery.OperationId+"/completed/data");
                resumed.candidate=new(op.DataId,directory,op.Nonce,op.ReleaseDigest,op.Migration);
                if(op.Migration is {} migrated){resumed.spec=NativeMigrationAdmission.SpecForInstalled(files,migrated);resumed.verified=NativeMigrationAdmission.VerifyCommittedEvidence(files,resumed.spec,migrated.ReceiptDigest);}
                else resumed.compatibleClone=new(op.Recovery.OperationId,Path.GetDirectoryName(directory)!,system.SourceSchema,true);
            }
            return resumed;
        }
        catch{if(resumed is not null)resumed.Failure();else{journal?.Dispose();inspector?.Dispose();lease.Dispose();}throw;}
    }
    private string OperationPath=>files.PathOf(".authority/full-data-operation-"+system.OperationId+".json");
    private void RequireRecoveryBootstrap()
    {
        policy.RequireRecoveryEvidence(system);var state=bootstrap.Current??throw new IOException("DATA_RECOVERY_BOOTSTRAP_REQUIRED");
        if(state.OperationId!=system.OperationId||state.ManifestDigest!=system.ManifestDigest||state.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)&&!(state.Phase==SystemBootstrapPhase.Prepared&&Evidence.BackupId is null))throw new IOException("DATA_RECOVERY_BOOTSTRAP_UNFINISHED");
        var probe=state.Phase==SystemBootstrapPhase.Complete?system.Components.Single(x=>x.Kind==SystemComponentKind.Probe).EntrySha256:state.Before.Components.Single(x=>x.Kind==SystemComponentKind.Probe).EntrySha256;
        if(probe is null||files.ProbeDigest!=probe)throw new IOException("DATA_RECOVERY_PROBE_BINDING");
    }
    private string ReturnOperationPath=>files.PathOf(".authority/full-data-return-"+system.OperationId+".json");
    private async Task ResumeInterruptedControlAsync(IChildControlFactory channels,CancellationToken cancellation)
    {
        var candidates=new[]{operation,Path.Exists(ReturnOperationPath)?NativeJson.Read<ProtectedOperation>(files.Read(ReturnOperationPath,32768)):null}.Where(x=>x is not null).Cast<ProtectedOperation>().ToArray();
        var paths=new List<string>();var ordinary=files.PathOf("pending.json");if(Path.Exists(ordinary))paths.Add(ordinary);
        foreach(var op in candidates){var partial=ordinary+".partial-"+op.Nonce;if(Path.Exists(partial))paths.Add(partial);}
        if(paths.Count==0)return;if(paths.Count!=1)throw new IOException("FULL_DATA_PENDING_AMBIGUOUS");
        var pending=NativeJson.Read<ProtectedPending>(files.Read(paths[0],128*1024));
        var exact=candidates.SingleOrDefault(x=>AuthorityProtocol.Digest(x)==AuthorityProtocol.Digest(pending.Operation))??throw new IOException("FULL_DATA_PENDING_UNKNOWN_OPERATION");
        IOwnerOperationAuthentication auth;
        if(exact==operation)
        {
            var compatible=RequiresMigration?null:new FullCompatibleClone(exact.Nonce,sourceDataId,exact.Recovery!.OperationId,exact.DataId,backup!.BackupId,backup.ReceiptDigest,sourceRestore);
            auth=new FullSystemOperationAuthentication(policy,system,exact,anchor.OwnerSid,clock,beforeBytes,compatible,pending.Owner);
        }
        else
        {
            if(!pending.Before.TryGetValue("activation.json",out var encoded))throw new IOException("FULL_DATA_RETURN_BEFORE_REQUIRED");
            auth=new FullSystemSelectionAuthentication(policy,system,journal,beforeBytes,Convert.FromBase64String(encoded),exact,clock,pending.Owner);
        }
        var head=new InstalledActivation(1,exact.InstallationId,exact.ExpectedEpoch,exact.ReleaseDigest,exact.DataId,exact.MinimumSchema,exact.MaximumSchema,exact.MasterKeyDigest,exact.PolicyDigest,exact.PinDigest,"","","committed",exact.Recovery,exact.Migration);
        var directory=SourceDirectory(files,head);using var lease=VerifiedMigrationSpec.Same(directory,SourceDirectory(files,before))?sourceLease.DuplicateForDirectory(directory):cloneLease?.DuplicateForDirectory(directory)??files.Lease(directory);
        var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,(recoveryGeneration??sourceGeneration).RequireAsync,auth,channels,clock);
        writer.UseCommitDataLease(path=>lease.DuplicateForDirectory(path));await writer.ResumeAsync(exact,cancellation:cancellation);
    }
    internal async Task<InstalledDataCandidate> PrepareCandidateAsync(IMigrationProcessFactory? factory,string runtimeSid,string appContainerSid,
        Func<VerifiedMigrationSpec,CancellationToken,Task> grantSandbox,Func<VerifiedMigrationSpec,CancellationToken,Task> normalizeNewScope,CancellationToken cancellation=default)
    {
        RequireBootstrapComplete();policy.RequireCurrent(system);if(candidate is not null)return candidate;
        if(recovery is null||backup is null)throw new IOException("DATA_MIGRATION_BACKUP_REQUIRED");
        if(RequiresMigration)
        {
            var result=await MigrateAsync(factory??throw new IOException("DATA_MIGRATION_RUNTIME_REQUIRED"),runtimeSid,appContainerSid,grantSandbox,normalizeNewScope,cancellation);
            var selected=spec??throw new IOException("DATA_MIGRATION_SPEC_REQUIRED");var evidence=new InstalledMigration(selected.Plan,selected.Nonce,result.ReceiptDigest,selected.NodeDigest,selected.ExpectedRuntimeSid,selected.ExpectedAppContainerSid,sourceRestore);
            return candidate=new(selected.Plan.NewDataId,selected.DataDirectory,selected.Plan.OperationId,system.ApplicationBundleDigest,evidence);
        }
        var previous=Evidence.MigrationAttempt;var reuse=Evidence.Checkpoint==UpdateCheckpoint.CloneVerified&&previous is not null;
        var attempt=reuse?previous!:new MigrationAttempt((previous?.Number??0)+1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
        if(attempt.Number>3)throw new IOException("DATA_MIGRATION_ATTEMPTS_QUARANTINED");if(!reuse)journal.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:attempt);
        cloneLease?.Dispose();cloneLease=null;
        compatibleClone=await recovery.RestorePlannedToNewDirectoryAsync(backup.BackupId,system.SourceSchema,system.SourceSchema,attempt.CloneOperationId,cancellation:cancellation);
        _=await recovery.PublishInstalledRestoreAsync(backup.BackupId,attempt.CloneOperationId,system.SourceSchema,system.SourceSchema,cancellation:cancellation);
        var directory=Path.Combine(compatibleClone.RestoredDirectory,"data");cloneLease=files.Lease(directory);
        if(!reuse)journal.Append(UpdateCheckpoint.CloneVerified);return candidate=new(attempt.NewDataId,directory,attempt.OperationId,system.ApplicationBundleDigest,null);
    }
    internal async Task<VerifiedMigration> MigrateAsync(IMigrationProcessFactory factory,string runtimeSid,string appContainerSid,
        Func<VerifiedMigrationSpec,CancellationToken,Task> grantSandbox,Func<VerifiedMigrationSpec,CancellationToken,Task> normalizeNewScope,CancellationToken cancellation=default)
    {
        RequireBootstrapComplete();if(!RequiresMigration)throw new IOException("DATA_MIGRATION_NOT_REQUIRED");if(recovery is null||backup is null)throw new IOException("DATA_MIGRATION_BACKUP_REQUIRED");policy.RequireCurrent(system);
        if(verified is not null&&spec is not null)return NativeMigrationAdmission.VerifyCompleted(files,spec,verified.ReceiptDigest);
        if(Evidence.Checkpoint==UpdateCheckpoint.MigrationVerified&&Evidence.MigrationAttempt is {} completed&&Evidence.MigrationReceiptDigest is {} receipt)
        {
            var completedDescriptor=NativeJson.Read<MigrationLaunchDescriptor>(files.Read(files.PathOf("migration-staging/"+completed.OperationId+"/descriptor.json"),32768));
            spec=NativeMigrationAdmission.SpecForLaunch(files,completedDescriptor);
            if(spec.Plan.SystemOperationId!=system.OperationId||spec.Plan.CloneOperationId!=completed.CloneOperationId||spec.Plan.NewDataId!=completed.NewDataId||spec.Plan.BackupId!=backup.BackupId||spec.Plan.BackupReceiptDigest!=backup.ReceiptDigest||spec.Plan.PayloadDigest!=system.ApplicationBundleDigest||spec.Plan.SourceSchema!=system.SourceSchema||spec.Plan.TargetSchema!=system.TargetSchema)throw new IOException("DATA_MIGRATION_RESUME_COMPLETION_BINDING");
            verified=NativeMigrationAdmission.VerifyCompleted(files,spec,receipt);cloneLease=files.Lease(spec.DataDirectory);return verified;
        }
        var attempt=(Evidence.MigrationAttempt?.Number??0)+1;if(attempt>3)throw new IOException("DATA_MIGRATION_ATTEMPTS_QUARANTINED");
        // Failed intermediate SQL copies are NEVER retried in place. Existing
        // migrations commit individual steps; allocate fresh identities from
        // the SAME authenticated backup and retain every unsuccessful copy.
        cloneLease?.Dispose();cloneLease=null;var identity=new MigrationAttempt(attempt,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"));
        journal.Append(UpdateCheckpoint.MigrationIntent,migrationAttempt:identity);
        var manifest=RequirePayload(files,system);var entry=manifest.GetProperty("files").EnumerateArray().Single(x=>x.GetProperty("path").GetString()==OfflineMigrationPlan.Entry);
        var plan=new OfflineMigrationPlan(1,anchor.InstallationId,identity.OperationId,sourceDataId,identity.CloneOperationId,identity.NewDataId,backup.BackupId,backup.ReceiptDigest,
            system.SourceSchema,system.TargetSchema,system.ApplicationBundleDigest,entry.GetProperty("sha256").GetString()!,before.MasterKeyDigest,before.PolicyDigest,before.PinDigest,system.OperationId);
        var clone=await recovery.RestorePlannedToNewDirectoryAsync(backup.BackupId,system.SourceSchema,system.SourceSchema,identity.CloneOperationId,cancellation:cancellation);
        _=await recovery.PublishInstalledRestoreAsync(backup.BackupId,clone.OperationId,system.SourceSchema,system.SourceSchema,cancellation:cancellation);
        cloneLease=files.Lease(Path.Combine(clone.RestoredDirectory,"data"));
        var nonce=Guid.NewGuid().ToString("N");var node=manifest.GetProperty("files").EnumerateArray().Single(x=>x.GetProperty("path").GetString()=="runtime/node/node.exe");
        var descriptor=new MigrationLaunchDescriptor(plan,nonce,node.GetProperty("sha256").GetString()!,runtimeSid,appContainerSid,sourceRestore);
        spec=NativeMigrationAdmission.SpecForLaunch(files,descriptor);spec.Validate();PrepareWorkspace(spec,manifest,descriptor,cancellation);
        execution=new NativeOfflineMigration();
        try
        {
            verified=await execution.RunAsync(files,recovery,spec,cloneLease,factory,c=>grantSandbox(spec,c),c=>normalizeNewScope(spec,c),cancellation);
            policy.RequireCurrent(system);journal.Append(UpdateCheckpoint.MigrationVerified,migrationReceiptDigest:verified.ReceiptDigest);return verified;
        }
        catch(MigrationOwnershipException owned){owned.KeepAlive(this);failedOwners.TryAdd(system.OperationId,this);throw;}
    }
    private void PrepareWorkspace(VerifiedMigrationSpec target,JsonElement manifest,MigrationLaunchDescriptor descriptor,CancellationToken cancellation)
    {
        var workspace=files.PathOf("migration-staging");if(!Directory.Exists(workspace))files.CreateDirectory(workspace);
        workspace=Path.Combine(workspace,target.Plan.OperationId);if(Directory.Exists(workspace))throw new IOException("DATA_MIGRATION_WORKSPACE_REPLAY");files.CreateDirectory(workspace);
        void DirectoryFor(string directory){if(Directory.Exists(directory))return;var parent=Path.GetDirectoryName(directory)!;DirectoryFor(parent);files.CreateDirectory(directory);}
        DirectoryFor(target.PayloadRoot);DirectoryFor(Path.GetDirectoryName(target.RequestFile)!);DirectoryFor(target.TemporaryDirectory);
        var source=files.PathOf("payloads/"+system.ApplicationBundleDigest);long total=0;var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var file in manifest.GetProperty("files").EnumerateArray())
        {
            cancellation.ThrowIfCancellationRequested();var name=file.GetProperty("path").GetString()!;var size=file.GetProperty("size").GetInt64();var digest=file.GetProperty("sha256").GetString()!;
            if(name.Length is <1 or >240||name.Contains('\\')||name.Contains(':')||name.StartsWith('/')||name.Split('/').Any(x=>x is "" or "." or "..")||!names.Add(name)||names.Count>50000||size<0||size>512L*1024*1024||(total+=size)>2L*1024*1024*1024)throw new IOException("DATA_MIGRATION_SHADOW_SCOPE");
            InstalledActivationResolver.Digest(digest);var destination=Path.Combine(target.PayloadRoot,name.Replace('/',Path.DirectorySeparatorChar));DirectoryFor(Path.GetDirectoryName(destination)!);
            using var input=files.Open(Path.Combine(source,name.Replace('/',Path.DirectorySeparatorChar)));if(input.Length!=size)throw new IOException("DATA_MIGRATION_SHADOW_SIZE");using var output=files.Create(destination);using var hashed=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);var buffer=new byte[1024*1024];long position=0;
            int read;while((read=input.Read(position,buffer))>0){cancellation.ThrowIfCancellationRequested();output.Write(buffer,0,read);hashed.AppendData(buffer,0,read);position+=read;}
            if(position!=size||Convert.ToHexStringLower(hashed.GetHashAndReset())!=digest)throw new IOException("DATA_MIGRATION_SHADOW_BYTES");output.Flush(true);
        }
        void Put(string path,object value){using var output=files.Create(path);output.Write(JsonSerializer.SerializeToUtf8Bytes(value));output.Flush(true);}
        Put(Path.Combine(workspace,"descriptor.json"),descriptor);Put(target.RequestFile,MigrationChildRequest.From(target));
    }
    internal ProtectedOperation PrepareOperation()
    {
        RequireBootstrapComplete();policy.RequireCurrent(system);if(candidate is null)throw new IOException("DATA_CANDIDATE_REQUIRED");
        if(RequiresMigration){if(verified is null||spec is null||Evidence.Checkpoint!=UpdateCheckpoint.MigrationVerified)throw new IOException("DATA_MIGRATION_VERIFIED_REQUIRED");_=NativeMigrationAdmission.VerifyCompleted(files,spec,verified.ReceiptDigest);}
        else if(Evidence.Checkpoint!=UpdateCheckpoint.CloneVerified||compatibleClone is null)throw new IOException("DATA_CLONE_VERIFIED_REQUIRED");
        if(InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=system.PreviousActivationDigest)throw new IOException("DATA_MIGRATION_HEAD_CHANGED");
        var webBytes=files.Read(files.PathOf("web.json"),16384);if(InstalledActivationResolver.Hash(webBytes)!=before.WebDigest)throw new IOException("DATA_MIGRATION_WEB_CHANGED");var web=NativeJson.Read<InstalledWeb>(webBytes);
        var cloneOperation=RequiresMigration?spec!.Plan.CloneOperationId:compatibleClone!.OperationId;
        operation=new(1,RequiresMigration?"migrate":"recover",anchor.InstallationId,before.Epoch,system.PreviousActivationDigest,system.ApplicationBundleDigest,candidate.DataId,system.TargetSchema,system.TargetSchema,before.MasterKeyDigest,before.PolicyDigest,before.PinDigest,web,
            new(sourceDataId,backup!.BackupId,cloneOperation,backup.ReceiptDigest,sourceRestore),clock().AddMinutes(10).ToUnixTimeSeconds(),candidate.OperationId,candidate.Migration);
        if(Path.Exists(OperationPath)){if(!files.Read(OperationPath,32768).SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(operation)))throw new IOException("DATA_MIGRATION_OPERATION_REPLAY");}
        else{using var output=files.Create(OperationPath);output.Write(JsonSerializer.SerializeToUtf8Bytes(operation));output.Flush(true);}return operation;
    }
    internal async Task<InstallationBinding> CommitAsync(IChildControlFactory channels,CancellationToken cancellation=default)
    {
        var selected=operation??PrepareOperation();policy.RequireCurrent(system);await sourceGeneration.RequireAsync(cancellation);journal.Append(UpdateCheckpoint.ActivationIntent);
        var compatible=RequiresMigration?null:new FullCompatibleClone(selected.Nonce,sourceDataId,compatibleClone!.OperationId,selected.DataId,backup!.BackupId,backup.ReceiptDigest,sourceRestore);
        var auth=new FullSystemOperationAuthentication(policy,system,selected,anchor.OwnerSid,clock,beforeBytes,compatible);
        var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,sourceGeneration.RequireAsync,auth,channels,clock);
        writer.UseCommitDataLease(path=>(cloneLease??throw new IOException("DATA_MIGRATION_CLONE_LEASE_REQUIRED")).DuplicateForDirectory(path));
        return await writer.CommitAsync(selected,cancellation:cancellation);
    }
    internal async Task StartProbationAsync(IAuthorityProbationLifecycle runtime,ProbationRuntimeBinding binding,CancellationToken cancellation=default)
    {
        if(operation is null||candidate is null)throw new IOException("DATA_MIGRATION_OPERATION_REQUIRED");policy.RequireCurrent(system);
        binding.Validate();
        if(binding.OperationId!=operation.Nonce||binding.ReleaseDigest!=operation.ReleaseDigest||binding.DataId!=operation.DataId||
            binding.Binding!=new InstallationBinding(anchor.InstallationId,InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))))throw new IOException("DATA_MIGRATION_PROBATION_BINDING");
        if(probationRuntime is not null)throw new IOException("DATA_MIGRATION_PROBATION_REPLAY");probationRuntime=runtime;probationBinding=binding;
        PublishProbation(binding);journal.Append(UpdateCheckpoint.CandidateStartIntent);cloneLease?.Dispose();cloneLease=null;
        await runtime.StartProbationAsync(binding,cancellation);
        if(runtime.Phase!="healthy")throw new IOException("DATA_MIGRATION_PROBATION_UNHEALTHY");journal.Append(UpdateCheckpoint.ProbationHealthy);
    }
    internal async Task ReleaseEffectsAsync(CancellationToken cancellation=default)
    {
        if(operation is null||candidate is null||probationRuntime is null||probationBinding is null||Evidence.Checkpoint!=UpdateCheckpoint.ProbationHealthy)throw new IOException("DATA_MIGRATION_PROBATION_BINDING");
        policy.RequireCurrent(system);effects=new(probationBinding,this,probationRuntime);await effects.ReleaseAsync(cancellation);journal.Append(UpdateCheckpoint.Healthy);healthy=true;Dispose();
    }
    internal async Task ReleaseForwardRepairEffectsAsync(IAuthorityProbationLifecycle runtime,ProbationRuntimeBinding binding,CancellationToken cancellation=default)
    {
        policy.RequireCurrent(system);binding.Validate();var selected=operation??throw new IOException("FORWARD_REPAIR_CANDIDATE_REQUIRED");
        if(!journal.EffectsMayHaveBeenReleased||runtime.Phase!="healthy"||binding.OperationId!=selected.Nonce||binding.ReleaseDigest!=selected.ReleaseDigest||binding.DataId!=selected.DataId||binding.Binding.InstallationId!=anchor.InstallationId)throw new IOException("FORWARD_REPAIR_BINDING");
        var activation=files.Read(files.PathOf("activation.json"),16384);var head=NativeJson.Read<InstalledActivation>(activation);
        if(InstalledActivationResolver.Hash(activation)!=binding.Binding.ActivationReceiptDigest||head.Epoch!=selected.ExpectedEpoch+1||head.ReleaseDigest!=selected.ReleaseDigest||head.DataId!=selected.DataId||head.Recovery!=selected.Recovery||head.Migration!=selected.Migration||head.MasterKeyDigest!=before.MasterKeyDigest||head.PolicyDigest!=before.PolicyDigest||head.PinDigest!=before.PinDigest)throw new IOException("FORWARD_REPAIR_HEAD_CHANGED");
        var records=Directory.EnumerateFiles(files.PathOf(".authority"),"full-forward-repair-"+system.OperationId+"-*.json").Order(StringComparer.Ordinal).ToArray();
        if(records.Length is <1 or >3)throw new IOException("FORWARD_REPAIR_ATTEMPT_BOUND");var nonces=new HashSet<string>(StringComparer.Ordinal);NativeFullSystemForwardRepair? latest=null;
        for(var i=0;i<records.Length;i++)
        {
            if(Path.GetFileName(records[i])!=$"full-forward-repair-{system.OperationId}-{i+1:D2}.json")throw new IOException("FORWARD_REPAIR_SEQUENCE");
            var value=NativeJson.Read<NativeFullSystemForwardRepair>(files.Read(records[i],32768));value.Binding.Validate();
            if(value.Format!=1||value.InstallationId!=system.InstallationId||value.SystemOperationId!=system.OperationId||value.Attempt!=i+1||!value.Activation.SequenceEqual(activation)||value.Binding.Binding!=binding.Binding||value.Binding.OperationId!=selected.Nonce||value.Binding.ReleaseDigest!=selected.ReleaseDigest||value.Binding.DataId!=selected.DataId||!nonces.Add(value.Binding.Nonce))throw new IOException("FORWARD_REPAIR_ATTEMPT_BINDING");latest=value;
        }
        if(latest?.Binding!=binding||NativeJson.Read<ProbationRuntimeBinding>(files.Read(files.PathOf(InstalledProbationDescriptor.Relative(binding.Nonce)),8192))!=binding)throw new IOException("FORWARD_REPAIR_GENERATION_CHANGED");
        var requestId=Guid.NewGuid().ToString("N");var path=files.PathOf(".authority/full-forward-release-"+system.OperationId+"-"+binding.Nonce+".json");
        using(var output=files.Create(path)){output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Binding=binding,RequestId=requestId}));output.Flush(true);}
        journal.Append(UpdateCheckpoint.ForwardRepairEffectsIntent);await runtime.ReleaseEffectsAsync(binding,requestId,cancellation);journal.Append(UpdateCheckpoint.Healthy);healthy=true;Dispose();
    }
    public Task RecordIntentAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();policy.RequireCurrent(system);InstalledActivationResolver.Id(requestId);
        if(probationBinding is null||binding!=probationBinding||Evidence.Checkpoint!=UpdateCheckpoint.ProbationHealthy)throw new IOException("DATA_MIGRATION_EFFECTS_BINDING");
        var path=files.PathOf(".authority/migration-effects-"+system.OperationId+".json");using(var output=files.Create(path)){output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Binding=binding,RequestId=requestId}));output.Flush(true);}
        journal.Append(UpdateCheckpoint.EffectsReleaseIntent);return Task.CompletedTask;
    }
    internal async Task<InstalledDataRecovery> ReconcileAsync(IAuthorityProbationLifecycle runtime,IChildControlFactory channels,
        RetainedRuntimeQuiescence stoppedCurrentGeneration,CancellationToken cancellation=default)
    {
        policy.RequireRecoveryEvidence(system);await stoppedCurrentGeneration.RequireAsync(cancellation);recoveryGeneration?.Dispose();recoveryGeneration=stoppedCurrentGeneration.Duplicate();
        // An ACK can be lost after a provider handoff. The durable intent is
        // sufficient to forbid returning a newer schema to an older DB copy.
        if(journal.EffectsMayHaveBeenReleased&&RequiresMigration)return new(null,FullDataRecoveryResult.ForwardRepairRequired,true);
        if(backup is null)return await RestartUnchangedBeforeBackupAsync(runtime,cancellation);
        recovery??=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceDataId,sourceGeneration.Duplicate(),inspector,sourceRestore,cancellation,sourceLease);
        await ResumeInterruptedControlAsync(channels,cancellation);
        var currentBytes=files.Read(files.PathOf("activation.json"),16384);var current=NativeJson.Read<InstalledActivation>(currentBytes);var digest=InstalledActivationResolver.Hash(currentBytes);
        var returningOperation=Path.Exists(ReturnOperationPath)?NativeJson.Read<ProtectedOperation>(files.Read(ReturnOperationPath,32768)):null;
        var returned=returningOperation is {} saved&&current.Epoch==saved.ExpectedEpoch+1&&current.ReleaseDigest==saved.ReleaseDigest&&current.DataId==saved.DataId&&current.Recovery==saved.Recovery&&current.Migration==saved.Migration&&
            Path.Exists(files.PathOf(".authority/commit-"+saved.Nonce+".json"))&&NativeJson.Read<ProtectedCommit>(files.Read(files.PathOf(".authority/commit-"+saved.Nonce+".json"),4096)).ActivationDigest==digest;
        var alreadyPrevious=digest==system.PreviousActivationDigest||returned;
        if(!alreadyPrevious)
        {
            var expected=operation??throw new IOException("DATA_RECOVERY_CANDIDATE_OPERATION_REQUIRED");
            if(current.InstallationId!=anchor.InstallationId||current.ReleaseDigest!=expected.ReleaseDigest||current.DataId!=expected.DataId||current.Recovery!=expected.Recovery||current.Migration!=expected.Migration||current.Epoch!=expected.ExpectedEpoch+1||
                current.MasterKeyDigest!=before.MasterKeyDigest||current.PolicyDigest!=before.PolicyDigest||current.PinDigest!=before.PinDigest)throw new IOException("DATA_RECOVERY_CURRENT_HEAD_CHANGED");
        }
        InstalledActivation selected=returned?current:journal.EffectsMayHaveBeenReleased?before with{DataId=current.DataId,Recovery=current.Recovery,Migration=current.Migration}:before;
        if(!journal.EffectsMayHaveBeenReleased&&!journal.FullRollbackWasRecorded&&!returned)await recovery.RequireSourceMatchesBackupAsync(backup.BackupId,backup.ReceiptDigest,system.SourceSchema,cancellation);
        var selectedDirectory=SourceDirectory(files,selected);
        WindowsFileBoundary.DataLease? returnLease=null;
        try
        {
            returnLease=VerifiedMigrationSpec.Same(selectedDirectory,SourceDirectory(files,before))?sourceLease.DuplicateForDirectory(selectedDirectory):
                cloneLease?.DuplicateForDirectory(selectedDirectory)??files.Lease(selectedDirectory);
            InstallationBinding binding;
            if(alreadyPrevious)binding=new(anchor.InstallationId,digest);
            else
            {
                var web=NativeJson.Read<InstalledWeb>(files.Read(files.PathOf("web.json"),16384));
                var returning=new ProtectedOperation(1,"activate",anchor.InstallationId,current.Epoch,digest,selected.ReleaseDigest,selected.DataId,selected.MinimumSchema,selected.MaximumSchema,selected.MasterKeyDigest,selected.PolicyDigest,selected.PinDigest,web,selected.Recovery,clock().AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"),selected.Migration);
                if(returningOperation is not null)throw new IOException("DATA_RECOVERY_RETURN_OPERATION_REQUIRES_RECONCILIATION");
                using(var output=files.Create(ReturnOperationPath)){output.Write(JsonSerializer.SerializeToUtf8Bytes(returning));output.Flush(true);}
                var auth=new FullSystemSelectionAuthentication(policy,system,journal,beforeBytes,currentBytes,returning,clock);
                var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,recoveryGeneration.RequireAsync,auth,channels,clock);
                var commitLease=returnLease;writer.UseCommitDataLease(path=>commitLease.DuplicateForDirectory(path));binding=await writer.CommitAsync(returning,cancellation:cancellation);
            }
            if(Evidence.Checkpoint!=UpdateCheckpoint.FullRollbackStartIntent)journal.Append(UpdateCheckpoint.FullRollbackStartIntent);
            // Persist selection and start intent while all actual data gates
            // are still retained; only the fenced runtime may take the lease.
            var probation=new ProbationRuntimeBinding(binding,Guid.NewGuid().ToString("N"),selected.ReleaseDigest,selected.DataId,Guid.NewGuid().ToString("N"));
            PublishProbation(probation);returnLease.Dispose();returnLease=null;cloneLease?.Dispose();cloneLease=null;recovery.Dispose();recovery=null;sourceLease.Dispose();sourceLeaseReleased=true;
            await runtime.StartProbationAsync(probation,cancellation);if(runtime.Phase!="healthy")throw new IOException("DATA_RECOVERY_RETURN_UNHEALTHY");
            var releaseId=Guid.NewGuid().ToString("N");var releasePath=files.PathOf(".authority/full-return-effects-"+system.OperationId+"-"+releaseId+".json");
            using(var output=files.Create(releasePath)){output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Format=1,Binding=probation,RequestId=releaseId}));output.Flush(true);}
            journal.Append(UpdateCheckpoint.FullRollbackEffectsIntent);await runtime.ReleaseEffectsAsync(probation,releaseId,cancellation);journal.Append(UpdateCheckpoint.FullRollbackComplete);healthy=true;Dispose();
            return new(binding,FullDataRecoveryResult.RestoredPrevious,EffectsMayHaveBeenReleased);
        }
        catch{returnLease?.Dispose();Failure();throw;}
    }
    private async Task<InstalledDataRecovery> RestartUnchangedBeforeBackupAsync(IAuthorityProbationLifecycle runtime,CancellationToken cancellation)
    {
        policy.RequireRecoveryEvidence(system);
        if(journal.CandidateStartWasRecorded||Evidence.MigrationAttempt is not null||Path.Exists(OperationPath)||Path.Exists(ReturnOperationPath)||Path.Exists(files.PathOf("pending.json"))||Directory.EnumerateFileSystemEntries(files.Root).Any(p=>Path.GetFileName(p).Contains(".partial-",StringComparison.Ordinal))||
            InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=system.PreviousActivationDigest)throw new IOException("DATA_PREBACKUP_RESTART_HEAD_CHANGED");
        recovery??=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceDataId,sourceGeneration.Duplicate(),inspector,sourceRestore,cancellation,sourceLease);
        var sourceRoot=Path.GetDirectoryName(SourceDirectory(files,before))!;var key=before.Recovery is null?Path.Combine(sourceRoot,"master-key"):Path.Combine(sourceRoot,"identity","master-key");
        var policyPath=before.Recovery is null?Path.Combine(sourceRoot,"update-policy"):Path.Combine(sourceRoot,"policy","update-policy");var pin=before.Recovery is null?Path.Combine(sourceRoot,"update-pin"):Path.Combine(sourceRoot,"policy","update-pin");
        if(InstalledActivationResolver.Hash(files.Read(key,4096))!=before.MasterKeyDigest||InstalledActivationResolver.Hash(files.Read(policyPath,1024*1024))!=before.PolicyDigest||InstalledActivationResolver.Hash(files.Read(pin,1024*1024))!=before.PinDigest||await recovery.InspectCurrentSchemaAsync(cancellation)!=system.SourceSchema)throw new IOException("DATA_PREBACKUP_RESTART_IDENTITY_CHANGED");
        if(Evidence.Checkpoint!=UpdateCheckpoint.PreviousStartIntent)
        {if(Evidence.Checkpoint==UpdateCheckpoint.PreviousEffectsReleaseIntent)journal.Append(UpdateCheckpoint.RecoveryRequired);journal.Append(UpdateCheckpoint.PreviousStartIntent);}
        var binding=new InstallationBinding(anchor.InstallationId,system.PreviousActivationDigest);var probation=new ProbationRuntimeBinding(binding,Guid.NewGuid().ToString("N"),before.ReleaseDigest,before.DataId,Guid.NewGuid().ToString("N"));PublishProbation(probation);
        recovery.Dispose();recovery=null;sourceLease.Dispose();sourceLeaseReleased=true;await runtime.StartProbationAsync(probation,cancellation);if(runtime.Phase!="healthy")throw new IOException("DATA_PREBACKUP_RESTART_UNHEALTHY");
        var id=Guid.NewGuid().ToString("N");journal.Append(UpdateCheckpoint.PreviousEffectsReleaseIntent);await runtime.ReleaseEffectsAsync(probation,id,cancellation);journal.Append(UpdateCheckpoint.Aborted);healthy=true;Dispose();return new(binding,FullDataRecoveryResult.RestoredPrevious,false);
    }
    private sealed record TerminalRestartEvidence(int Format,string InstallationId,string SystemOperationId,int Attempt,byte[] Activation,InstallationBinding Binding);
    private InstalledActivation RequireAvailabilityHead(InstallationBinding expected,bool terminal)
    {
        policy.RequireRecoveryEvidence(system);
        if(terminal)
        {if(Evidence.Checkpoint is not(UpdateCheckpoint.Healthy or UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted))throw new IOException("TERMINAL_RESTART_NOT_TERMINAL");}
        else if(!journal.EffectsMayHaveBeenReleased||Evidence.Checkpoint is not(UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.ForwardRepairEffectsIntent or UpdateCheckpoint.CurrentAvailabilityStartIntent))throw new IOException("CURRENT_RESTART_EFFECTS_REQUIRED");
        if(expected.InstallationId!=anchor.InstallationId||Path.Exists(files.PathOf("pending.json"))||Directory.EnumerateFileSystemEntries(files.Root).Any(p=>Path.GetFileName(p).Contains(".partial-",StringComparison.Ordinal)))throw new IOException("TERMINAL_RESTART_PENDING_CONTROL");
        var bytes=files.Read(files.PathOf("activation.json"),16384);var digest=InstalledActivationResolver.Hash(bytes);var head=new InstalledIntakeSelection(files,anchor,clock).Head();
        if(digest!=expected.ActivationReceiptDigest||head.MasterKeyDigest!=before.MasterKeyDigest||head.PolicyDigest!=before.PolicyDigest||head.PinDigest!=before.PinDigest)throw new IOException("TERMINAL_RESTART_HEAD_CHANGED");
        if(InstalledActivationResolver.Hash(files.Read(files.PathOf("owner-intent.json"),16384))!=head.OwnerIntentDigest||InstalledActivationResolver.Hash(files.Read(files.PathOf("web.json"),16384))!=head.WebDigest)throw new IOException("TERMINAL_RESTART_CONTROL_IDENTITY");
        if(!terminal||Evidence.Checkpoint==UpdateCheckpoint.Healthy)
        {
            var selected=operation??throw new IOException("TERMINAL_RESTART_CANDIDATE_REQUIRED");
            if(head.Epoch!=selected.ExpectedEpoch+1||head.ReleaseDigest!=selected.ReleaseDigest||head.DataId!=selected.DataId||head.Recovery!=selected.Recovery||head.Migration!=selected.Migration||head.MinimumSchema!=selected.MinimumSchema||head.MaximumSchema!=selected.MaximumSchema)throw new IOException("TERMINAL_RESTART_CANDIDATE_CHANGED");
        }
        else if(Evidence.Checkpoint==UpdateCheckpoint.Aborted)
        {if(digest!=system.PreviousActivationDigest)throw new IOException("TERMINAL_RESTART_ABORT_HEAD_CHANGED");}
        else if(Path.Exists(ReturnOperationPath))
        {
            var returned=NativeJson.Read<ProtectedOperation>(files.Read(ReturnOperationPath,32768));var receipt=NativeJson.Read<ProtectedCommit>(files.Read(files.PathOf(".authority/commit-"+returned.Nonce+".json"),4096));
            if(head.Epoch!=returned.ExpectedEpoch+1||head.ReleaseDigest!=returned.ReleaseDigest||head.DataId!=returned.DataId||head.Recovery!=returned.Recovery||head.Migration!=returned.Migration||head.MinimumSchema!=returned.MinimumSchema||head.MaximumSchema!=returned.MaximumSchema||receipt.ActivationDigest!=digest||receipt.Epoch!=head.Epoch)throw new IOException("TERMINAL_RESTART_RETURN_HEAD_CHANGED");
        }
        else if(digest!=system.PreviousActivationDigest)throw new IOException("TERMINAL_RESTART_ORIGINAL_CHANGED");
        return head;
    }
    internal async Task PrepareTerminalRestartAsync(InstallationBinding expected,RetainedRuntimeQuiescence stoppedCurrent,
        WindowsFileBoundary.DataLease actualCurrentLease,CancellationToken cancellation=default)
        =>await PrepareAvailabilityRestartAsync(expected,stoppedCurrent,actualCurrentLease,true,cancellation);
    internal async Task PrepareCurrentAvailabilityRestartAsync(InstallationBinding expected,RetainedRuntimeQuiescence stoppedCurrent,
        WindowsFileBoundary.DataLease actualCurrentLease,CancellationToken cancellation=default)
        =>await PrepareAvailabilityRestartAsync(expected,stoppedCurrent,actualCurrentLease,false,cancellation);
    private async Task PrepareAvailabilityRestartAsync(InstallationBinding expected,RetainedRuntimeQuiescence stoppedCurrent,
        WindowsFileBoundary.DataLease actualCurrentLease,bool terminal,CancellationToken cancellation)
    {
        var head=RequireAvailabilityHead(expected,terminal);await stoppedCurrent.RequireAsync(cancellation);var directory=SourceDirectory(files,head);
        using var leaseProof=actualCurrentLease.DuplicateForDirectory(directory);
        var prefix=terminal?"full-terminal-restart-":"full-current-restart-";var previousAttempt=terminal?terminalRestartAttempt:currentRestartAttempt;
        var entries=Directory.EnumerateFiles(files.PathOf(".authority"),prefix+system.OperationId+"-*.json").Order(StringComparer.Ordinal).Take(4).ToArray();
        if(entries.Length is <1 or >3||entries.Length<=previousAttempt)throw new IOException("TERMINAL_RESTART_ATTEMPT_BOUND");var raw=files.Read(files.PathOf("activation.json"),16384);
        for(var i=0;i<entries.Length;i++)
        {
            if(Path.GetFileName(entries[i])!=$"{prefix}{system.OperationId}-{i+1:D2}.json")throw new IOException("TERMINAL_RESTART_SEQUENCE");
            var marker=NativeJson.Read<TerminalRestartEvidence>(files.Read(entries[i],65536));
            if(marker.Format!=1||marker.InstallationId!=anchor.InstallationId||marker.SystemOperationId!=system.OperationId||marker.Attempt!=i+1||marker.Binding!=expected||!marker.Activation.SequenceEqual(raw))throw new IOException("TERMINAL_RESTART_INTENT_BINDING");
        }
        var selectedRoot=Path.GetDirectoryName(directory)!;var recovered=head.Recovery is not null;
        var key=recovered?Path.Combine(selectedRoot,"identity","master-key"):Path.Combine(selectedRoot,"master-key");var dataPolicy=recovered?Path.Combine(selectedRoot,"policy","update-policy"):Path.Combine(selectedRoot,"update-policy");var pin=recovered?Path.Combine(selectedRoot,"policy","update-pin"):Path.Combine(selectedRoot,"update-pin");
        if(InstalledActivationResolver.Hash(files.Read(key,4096))!=head.MasterKeyDigest||InstalledActivationResolver.Hash(files.Read(dataPolicy,1024*1024))!=head.PolicyDigest||InstalledActivationResolver.Hash(files.Read(pin,1024*1024))!=head.PinDigest)throw new IOException("TERMINAL_RESTART_DATA_IDENTITY");
        var retained=new List<IDisposable>();try
        {
            if(head.Migration is {} migration)_=NativeMigrationAdmission.VerifyCommittedEvidence(files,NativeMigrationAdmission.SpecForInstalled(files,migration),migration.ReceiptDigest);
            else if(head.Recovery is {} restored)_=NativeRecoveryAdmission.VerifyCommittedEvidence(files.PathOf("datasets/"+restored.SourceDataId),restored.SourceDirectory(files.Root),restored.BackupId,restored.OperationId,restored.ReceiptDigest,selectedRoot,key,dataPolicy,pin,retained,files);
        }
        finally{foreach(var item in retained)item.Dispose();}
        using(var probe=new NativeSqliteInspector(files.ProbeExecutable,files.ProbeDigest))
        using(var current=await NativeRecoveryFixture.OpenInstalledAsync(files,head.Recovery?.SourceDataId??head.DataId,stoppedCurrent.Duplicate(),probe,head.Recovery?.OperationId,cancellation,leaseProof))
        {var observed=await current.InspectCurrentSchemaAsync(cancellation);if(observed<head.MinimumSchema||observed>head.MaximumSchema||observed!=(!terminal||Evidence.Checkpoint==UpdateCheckpoint.Healthy?system.TargetSchema:system.SourceSchema))throw new IOException("TERMINAL_RESTART_SCHEMA_CHANGED");}
        _=RequireAvailabilityHead(expected,terminal);await stoppedCurrent.RequireAsync(cancellation);
        if(!terminal)journal.Append(UpdateCheckpoint.CurrentAvailabilityStartIntent);
        // The kernel still owns its actual current-data lease here. It releases
        // that final duplicate immediately before ordinary current-version boot.
        // No terminal record authorizes replaying an old effects command.
        cloneLease?.Dispose();cloneLease=null;recovery?.Dispose();recovery=null;sourceLease.Dispose();sourceLeaseReleased=true;terminalRestartBinding=expected;restartWasTerminal=terminal;terminalRestartCompleted=false;if(terminal)terminalRestartAttempt=entries.Length;else currentRestartAttempt=entries.Length;
    }
    internal void CompleteTerminalRestart(IAuthorityRuntimeLifecycle runtime,InstallationBinding expected)
    {
        if(!restartWasTerminal||terminalRestartBinding!=expected||terminalRestartCompleted||runtime.Phase!="healthy"||runtime.CleanupReconciliationRequired)throw new IOException("TERMINAL_RESTART_FRESH_HEALTH_REQUIRED");
        _=RequireAvailabilityHead(expected,true);terminalRestartCompleted=true;healthy=true;Dispose();
    }
    internal void CompleteCurrentAvailabilityRestart(IAuthorityRuntimeLifecycle runtime,InstallationBinding expected)
    {
        if(restartWasTerminal||terminalRestartBinding!=expected||terminalRestartCompleted||Evidence.Checkpoint!=UpdateCheckpoint.CurrentAvailabilityStartIntent||runtime.Phase!="healthy"||runtime.CleanupReconciliationRequired)throw new IOException("CURRENT_RESTART_FRESH_HEALTH_REQUIRED");
        _=RequireAvailabilityHead(expected,false);journal.Append(UpdateCheckpoint.Healthy);terminalRestartCompleted=true;healthy=true;Dispose();
    }
    private void PublishProbation(ProbationRuntimeBinding binding)
    {
        binding.Validate();files.EnsureDirectory(files.PathOf("runtime-staging"));files.EnsureDirectory(files.PathOf("runtime-staging/"+binding.Nonce));
        var path=files.PathOf(InstalledProbationDescriptor.Relative(binding.Nonce));var bytes=JsonSerializer.SerializeToUtf8Bytes(binding);
        if(Path.Exists(path)){if(!files.Read(path,8192).SequenceEqual(bytes))throw new IOException("DATA_PROBATION_DESCRIPTOR_REPLAY");return;}
        using var output=files.Create(path);output.Write(bytes);output.Flush(true);
    }
    internal void Failure()
    {if(disposed)return;try{if(Evidence.Checkpoint is not(UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.Healthy or UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted))journal.Append(UpdateCheckpoint.RecoveryRequired);}finally{failedOwners.TryAdd(system.OperationId,this);}}
    public void Dispose()
    {
        if(disposed)return;if(!healthy){Failure();return;}cloneLease?.Dispose();recovery?.Dispose();inspector.Dispose();sourceLease.Dispose();sourceGeneration.Dispose();recoveryGeneration?.Dispose();journal.Dispose();disposed=true;failedOwners.TryRemove(system.OperationId,out _);
    }
}
