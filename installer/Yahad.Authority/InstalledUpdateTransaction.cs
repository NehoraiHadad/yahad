using System.Collections.Concurrent;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// One adapter over the existing encrypted recovery engine and protected state
// writer. The router calls these hooks around STOP / COMMIT / START. No method
// performs activation or silently restores a database after a candidate ran.
internal sealed class InstalledUpdateTransaction:IDisposable
{
    private sealed record RecoveryPreparation(int Format,string OperationId,string DataId,string RestoreOperationId);
    private static readonly ConcurrentDictionary<string,InstalledUpdateTransaction> failedOwners=new();
    private readonly InstalledStateFiles files;private readonly InstalledAuthorityAnchor anchor;private readonly InstalledUpdateJournal journal;
    private readonly NativeSqliteInspector inspector;private readonly InstalledActivation before;private readonly string sourceData,sourceRestore;
    private RetainedRuntimeQuiescence? retained,recoveryRuntime;private NativeRecoveryFixture? recovery;private WindowsFileBoundary.DataLease? restoredLease;
    private CatalogOperation? preparedRecovery;private NativeRestoreResult? restored;private bool finished,disposed;
    internal bool HasRetainedResources=>!disposed;
    internal bool CanRestartPreviousUnchanged
    {
        get
        {
            if(disposed||journal.CandidateStartWasRecorded||Evidence.RestoreOperationId is not null)return false;
            try{return InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))==Evidence.Plan.PreviousActivationDigest&&
                !File.Exists(files.PathOf("pending.json"))&&!Directory.EnumerateFiles(files.Root,"*.partial-*").Any();}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception){return false;}
        }
    }
    internal InstalledUpdateRecord Evidence=>journal.Current??throw new IOException("UPDATE_TRANSACTION_NOT_BEGUN");
    private InstalledUpdateTransaction(InstalledStateFiles files,InstalledAuthorityAnchor anchor,InstalledUpdateJournal journal,
        NativeSqliteInspector inspector,InstalledActivation before,RetainedRuntimeQuiescence retained)
    {
        this.files=files;this.anchor=anchor;this.journal=journal;this.inspector=inspector;this.before=before;this.retained=retained;
        sourceData=before.Recovery?.SourceDataId??before.DataId;sourceRestore=before.Recovery?.OperationId??"";
    }
    internal static void RequireNoUnfinished(InstalledStateFiles files,InstalledAuthorityAnchor anchor)
    {using var journal=new InstalledUpdateJournal(files,anchor.InstallationId);journal.RequireNoUnfinished();}
    internal static InstalledUpdateTransaction Begin(InstalledStateFiles files,InstalledAuthorityAnchor anchor,CatalogOperation selection,
        string operationId,RetainedRuntimeQuiescence retained,Func<DateTimeOffset> clock)
    {
        InstalledUpdateJournal? journal=null;NativeSqliteInspector? inspector=null;
        try
        {
            InstalledActivationResolver.Id(operationId);var intake=new InstalledIntakeSelection(files,anchor,clock);var before=intake.Head();
            var current=InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384));var operation=selection.Operation;
            if(selection.Format!=1||operation.Kind!="activate"||operation.InstallationId!=anchor.InstallationId||operation.ExpectedActivationDigest!=current||operation.DataId!=before.DataId||operation.PolicyDigest!=before.PolicyDigest||operation.PinDigest!=before.PinDigest||operation.MasterKeyDigest!=before.MasterKeyDigest||operation.ReleaseDigest==before.ReleaseDigest)
                throw new IOException("UPDATE_TRANSACTION_SELECTION");
            var manifestBytes=files.Read(files.PathOf("payloads/"+before.ReleaseDigest+"/bundle.json"),16*1024*1024);
            if(InstalledActivationResolver.Hash(manifestBytes)!=before.ReleaseDigest)throw new IOException("UPDATE_TRANSACTION_PREVIOUS_PAYLOAD");
            var manifest=NativeJson.Read<JsonElement>(manifestBytes);var nodeRecords=manifest.GetProperty("files").EnumerateArray().Where(x=>x.GetProperty("path").GetString()=="runtime/node/node.exe").ToArray();
            if(nodeRecords.Length!=1)throw new IOException("UPDATE_TRANSACTION_PREVIOUS_NODE");var nodeDigest=nodeRecords[0].GetProperty("sha256").GetString()!;InstalledActivationResolver.Digest(nodeDigest);
            if(!string.IsNullOrEmpty(files.ProbeExecutable)&&!string.IsNullOrEmpty(files.ProbeDigest))
                inspector=new(files.ProbeExecutable,files.ProbeDigest);
            else if(files.Roles is not null)
            {
                throw new IOException("UPDATE_TRANSACTION_IMMUTABLE_PROBE_REQUIRED");
            }
            else inspector=new(files.PathOf("payloads/"+before.ReleaseDigest+"/runtime/node/node.exe"),nodeDigest);
            journal=new(files,anchor.InstallationId);journal.Begin(new(1,anchor.InstallationId,operationId,before.DataId,current,before.ReleaseDigest,operation.ReleaseDigest,
                before.MaximumSchema,before.MinimumSchema,before.MaximumSchema,operation.MinimumSchema,operation.MaximumSchema,before.PolicyDigest,before.PinDigest,before.MasterKeyDigest));
            return new(files,anchor,journal,inspector,before,retained);
        }
        catch{inspector?.Dispose();journal?.Dispose();retained.Dispose();throw;}
    }
    internal async Task BackupAfterQuiescenceAsync(CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(Evidence.Checkpoint!=UpdateCheckpoint.Prepared)throw new IOException("UPDATE_TRANSACTION_BACKUP_ORDER");
        await (retained??throw new IOException("UPDATE_TRANSACTION_RUNTIME_OWNER")).RequireAsync(cancellation);
        journal.Append(UpdateCheckpoint.Quiesced);
        recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceData,retained,inspector,sourceRestore.Length==0?null:sourceRestore,cancellation);
        var backup=await recovery.BackupAsync(cancellation:cancellation);journal.Append(UpdateCheckpoint.BackupVerified,backup);
    }
    internal void ActivationIntent()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=Evidence.Plan.PreviousActivationDigest)throw new IOException("UPDATE_TRANSACTION_HEAD_CHANGED");
        journal.Append(UpdateCheckpoint.ActivationIntent);
    }
    internal WindowsFileBoundary.DataLease DuplicateDataLease(string expectedDirectory)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Evidence.Checkpoint==UpdateCheckpoint.RollbackIntent&&restoredLease is not null)return restoredLease.DuplicateForDirectory(expectedDirectory);
        if(Evidence.Checkpoint!=UpdateCheckpoint.ActivationIntent||recovery is null)throw new IOException("UPDATE_TRANSACTION_COMMIT_LEASE_ORDER");
        return recovery.DuplicateDataLease(expectedDirectory);
    }
    internal WindowsFileBoundary.DataLease DuplicateLease(string expectedDirectory)=>DuplicateDataLease(expectedDirectory);
    internal void CandidateStartIntent()
    {
        ObjectDisposedException.ThrowIf(disposed,this);var active=NativeJson.Read<InstalledActivation>(files.Read(files.PathOf("activation.json"),16384));
        if(active.InstallationId!=anchor.InstallationId||active.ReleaseDigest!=Evidence.Plan.CandidateRelease||active.DataId!=before.DataId||active.MinimumSchema!=Evidence.Plan.CandidateMinimumSchema||active.MaximumSchema!=Evidence.Plan.CandidateMaximumSchema||active.PolicyDigest!=before.PolicyDigest||active.PinDigest!=before.PinDigest||active.MasterKeyDigest!=before.MasterKeyDigest||active.Phase!="committed")throw new IOException("UPDATE_TRANSACTION_CANDIDATE_BINDING");
        journal.Append(UpdateCheckpoint.CandidateStartIntent);
        // Flush start intent BEFORE releasing the lease, which the runtime
        // resolver must acquire. No execution is allowed before this boundary.
        recovery?.Dispose();recovery=null;retained?.Dispose();retained=null;
    }
    internal void Healthy()
    {
        ObjectDisposedException.ThrowIf(disposed,this);journal.Append(UpdateCheckpoint.Healthy);finished=true;Dispose();
    }
    internal void Failure()
    {
        if(disposed||finished)return;
        try{if(Evidence.Checkpoint!=UpdateCheckpoint.RecoveryRequired)journal.Append(UpdateCheckpoint.RecoveryRequired);}
        finally{failedOwners.TryAdd(Evidence.Plan.OperationId,this);}
    }
    internal static async Task<InstalledUpdateTransaction> ResumeAsync(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string operationId,
        RetainedRuntimeQuiescence stoppedGeneration,Func<DateTimeOffset> clock,CancellationToken cancellation=default)
    {
        InstalledUpdateJournal? opened=null;NativeSqliteInspector? probe=null;
        try
        {
            await stoppedGeneration.RequireAsync(cancellation);
            if(failedOwners.TryGetValue(operationId,out var owned))
            {
                if(owned.files.Root!=files.Root||owned.anchor!=anchor||owned.disposed)throw new IOException("UPDATE_RECOVERY_OWNER_BINDING");
                var previousObservation=owned.recoveryRuntime;
                // The previous observation may also be the exact capability
                // retained by the backup engine or primary generation owner.
                // Replacing the latest proof must not dispose those aliases.
                if(previousObservation is not null&&!ReferenceEquals(previousObservation,stoppedGeneration)&&!ReferenceEquals(previousObservation,owned.retained)&&owned.recovery?.OwnsRuntimeObservation(previousObservation)!=true)previousObservation.Dispose();
                owned.recoveryRuntime=stoppedGeneration;owned.RequireRecoveryHead(clock);return owned;
            }
            opened=new(files,anchor.InstallationId);var evidence=opened.Resume(operationId);
            if(evidence.Checkpoint==UpdateCheckpoint.RollbackComplete)throw new IOException("UPDATE_RECOVERY_ALREADY_COMPLETE");
            var head=new InstalledIntakeSelection(files,anchor,clock).Head();var p=evidence.Plan;
            if(p.MasterKeyDigest is null||head.MasterKeyDigest!=p.MasterKeyDigest||head.DataId!=p.DataId||head.PolicyDigest!=p.PolicyDigest||head.PinDigest!=p.PinDigest||head.ReleaseDigest!=p.PreviousRelease&&head.ReleaseDigest!=p.CandidateRelease)throw new IOException("UPDATE_RECOVERY_HEAD_CHANGED");
            if(!string.IsNullOrEmpty(files.ProbeExecutable)&&!string.IsNullOrEmpty(files.ProbeDigest))probe=new(files.ProbeExecutable,files.ProbeDigest);
            else if(files.Roles is not null)
            {
                throw new IOException("UPDATE_TRANSACTION_IMMUTABLE_PROBE_REQUIRED");
            }
            else
            {
                var bytes=files.Read(files.PathOf("payloads/"+p.PreviousRelease+"/bundle.json"),16*1024*1024);
                if(InstalledActivationResolver.Hash(bytes)!=p.PreviousRelease)throw new IOException("UPDATE_TRANSACTION_PREVIOUS_PAYLOAD");
                var manifest=NativeJson.Read<JsonElement>(bytes);var node=manifest.GetProperty("files").EnumerateArray().Single(x=>x.GetProperty("path").GetString()=="runtime/node/node.exe");
                probe=new(files.PathOf("payloads/"+p.PreviousRelease+"/runtime/node/node.exe"),node.GetProperty("sha256").GetString()!);
            }
            var result=new InstalledUpdateTransaction(files,anchor,opened,probe,head,stoppedGeneration){recoveryRuntime=stoppedGeneration};
            failedOwners.TryAdd(operationId,result);return result;
        }
        catch{probe?.Dispose();opened?.Dispose();stoppedGeneration.Dispose();throw;}
    }
    private InstalledActivation RequireRecoveryHead(Func<DateTimeOffset> clock)
    {
        var head=new InstalledIntakeSelection(files,anchor,clock).Head();var p=Evidence.Plan;
        if(p.MasterKeyDigest is null||head.MasterKeyDigest!=p.MasterKeyDigest||head.PolicyDigest!=p.PolicyDigest||head.PinDigest!=p.PinDigest||head.DataId!=p.DataId||head.ReleaseDigest!=p.PreviousRelease&&head.ReleaseDigest!=p.CandidateRelease)throw new IOException("UPDATE_RECOVERY_HEAD_CHANGED");
        return head;
    }
    internal CatalogOperation PrepareProtectedRecover(Func<DateTimeOffset> clock)
    {
        ObjectDisposedException.ThrowIf(disposed,this);var e=Evidence;if(e.BackupId is null||e.BackupReceiptDigest is null)throw new IOException("UPDATE_RECOVERY_VERIFIED_BACKUP_REQUIRED");
        if(e.Checkpoint==UpdateCheckpoint.RollbackComplete)throw new IOException("UPDATE_RECOVERY_ALREADY_COMPLETE");
        var path=files.PathOf(".authority/update-recovery-"+e.Plan.OperationId+".json");RecoveryPreparation preparation;
        if(File.Exists(path))preparation=NativeJson.Read<RecoveryPreparation>(files.Read(path,8192));
        else
        {
            preparation=new(1,e.Plan.OperationId,Guid.NewGuid().ToString("N"),e.RestoreOperationId??Guid.NewGuid().ToString("N"));
            using var output=files.Create(path);output.Write(JsonSerializer.SerializeToUtf8Bytes(preparation));output.Flush(true);
        }
        InstalledActivationResolver.Id(preparation.DataId);InstalledActivationResolver.Id(preparation.RestoreOperationId);
        if(preparation.Format!=1||preparation.OperationId!=e.Plan.OperationId||e.RestoreOperationId is not null&&preparation.RestoreOperationId!=e.RestoreOperationId)throw new IOException("UPDATE_RECOVERY_PREPARATION_BINDING");
        var head=RequireRecoveryHead(clock);var headDigest=InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384));
        var webBytes=files.Read(files.PathOf("web.json"),16384);if(InstalledActivationResolver.Hash(webBytes)!=head.WebDigest)throw new IOException("UPDATE_RECOVERY_WEB_BINDING");var web=NativeJson.Read<InstalledWeb>(webBytes);
        var op=new ProtectedOperation(1,"recover",anchor.InstallationId,head.Epoch,headDigest,e.Plan.PreviousRelease,preparation.DataId,e.Plan.PreviousMinimumSchema,e.Plan.PreviousMaximumSchema,
            e.Plan.MasterKeyDigest!,e.Plan.PolicyDigest,e.Plan.PinDigest,web,new(sourceData,e.BackupId,preparation.RestoreOperationId,e.BackupReceiptDigest,sourceRestore.Length==0?null:sourceRestore),clock().AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
        var previousManifest=files.Read(files.PathOf("payloads/"+e.Plan.PreviousRelease+"/bundle.json"),16*1024*1024);
        if(InstalledActivationResolver.Hash(previousManifest)!=e.Plan.PreviousRelease)throw new IOException("UPDATE_TRANSACTION_PREVIOUS_PAYLOAD");
        var previous=NativeJson.Read<JsonElement>(previousManifest).GetProperty("version").GetString()!;
        var currentManifest=NativeJson.Read<JsonElement>(files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),16*1024*1024));
        preparedRecovery=new(1,op,new(currentManifest.GetProperty("version").GetString()!,previous,"stable",web.Origin,0,e.Plan.PolicyDigest,e.Plan.PinDigest));return preparedRecovery;
    }
    internal CatalogOperation PreparePreviousRestart(Func<DateTimeOffset> clock)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(!CanRestartPreviousUnchanged)throw new IOException("UPDATE_PREVIOUS_RESTART_CANDIDATE_MAY_HAVE_RUN");
        var head=RequireRecoveryHead(clock);var bytes=files.Read(files.PathOf("activation.json"),16384);
        if(InstalledActivationResolver.Hash(bytes)!=Evidence.Plan.PreviousActivationDigest||head.ReleaseDigest!=Evidence.Plan.PreviousRelease||File.Exists(files.PathOf("pending.json"))||Directory.EnumerateFiles(files.Root,"*.partial-*").Any())throw new IOException("UPDATE_PREVIOUS_RESTART_STATE_CHANGED");
        var webBytes=files.Read(files.PathOf("web.json"),16384);if(InstalledActivationResolver.Hash(webBytes)!=head.WebDigest)throw new IOException("UPDATE_RECOVERY_WEB_BINDING");var web=NativeJson.Read<InstalledWeb>(webBytes);
        var manifest=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),16*1024*1024);
        if(InstalledActivationResolver.Hash(manifest)!=head.ReleaseDigest)throw new IOException("UPDATE_TRANSACTION_PREVIOUS_PAYLOAD");var version=NativeJson.Read<JsonElement>(manifest).GetProperty("version").GetString()!;
        var op=new ProtectedOperation(1,"activate",anchor.InstallationId,head.Epoch,Evidence.Plan.PreviousActivationDigest,head.ReleaseDigest,head.DataId,head.MinimumSchema,head.MaximumSchema,head.MasterKeyDigest,head.PolicyDigest,head.PinDigest,web,head.Recovery,clock().AddMinutes(10).ToUnixTimeSeconds(),Guid.NewGuid().ToString("N"));
        preparedRecovery=new(1,op,new(version,version,"stable",web.Origin,0,head.PolicyDigest,head.PinDigest));return preparedRecovery;
    }
    internal async Task PreviousStartIntentAsync(OwnerApprovalLedger ledger,CatalogOperation approved,CancellationToken cancellation=default)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(preparedRecovery is null||preparedRecovery!=approved||approved.Operation.Kind!="activate"||!CanRestartPreviousUnchanged)throw new IOException("UPDATE_PREVIOUS_RESTART_APPROVAL_BINDING");
        var operation=approved.Operation;var owner=await ledger.AuthenticateAsync(AuthorityProtocol.Digest(operation),"activate",cancellation);
        if(owner.Subject!=anchor.OwnerSid||owner.InstallationId!=anchor.InstallationId)throw new UnauthorizedAccessException("UPDATE_RECOVERY_LOCAL_OWNER_REQUIRED");
        if(InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=Evidence.Plan.PreviousActivationDigest||File.Exists(files.PathOf("pending.json")))throw new IOException("UPDATE_PREVIOUS_RESTART_STATE_CHANGED");
        await (recoveryRuntime??retained??throw new IOException("UPDATE_RECOVERY_RUNTIME_OWNER")).RequireAsync(cancellation);
        recovery??=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceData,retained??recoveryRuntime!,inspector,sourceRestore.Length==0?null:sourceRestore,cancellation);
        if(await recovery.InspectCurrentSchemaAsync(cancellation)!=Evidence.Plan.SchemaBefore)throw new IOException("UPDATE_PREVIOUS_RESTART_SCHEMA_CHANGED");
        journal.Append(UpdateCheckpoint.PreviousStartIntent);ReleaseOfflineDataOwners();
    }
    internal void MarkPreviousHealthy()
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(Evidence.Checkpoint!=UpdateCheckpoint.PreviousStartIntent||InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=Evidence.Plan.PreviousActivationDigest)throw new IOException("UPDATE_PREVIOUS_RESTART_HEALTH_BINDING");
        journal.Append(UpdateCheckpoint.Aborted);finished=true;Dispose();
    }
    internal async Task<NativeRestoreResult> RestoreNewAsync(OwnerApprovalLedger ledger,CatalogOperation approved,CancellationToken cancellation=default,Action<string>? fault=null)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(preparedRecovery is null||approved!=preparedRecovery||approved.Operation.Kind!="recover")throw new IOException("UPDATE_RECOVERY_APPROVAL_BINDING");
        var operation=approved.Operation;var owner=await ledger.AuthenticateAsync(AuthorityProtocol.Digest(operation),"recover",cancellation);
        if(owner.Subject!=anchor.OwnerSid||owner.InstallationId!=anchor.InstallationId||owner.OperationDigest!=AuthorityProtocol.Digest(operation))throw new UnauthorizedAccessException("UPDATE_RECOVERY_LOCAL_OWNER_REQUIRED");
        await (recoveryRuntime??retained??throw new IOException("UPDATE_RECOVERY_RUNTIME_OWNER")).RequireAsync(cancellation);
        if(recovery is null)recovery=await NativeRecoveryFixture.OpenInstalledAsync(files,sourceData,retained??recoveryRuntime!,inspector,sourceRestore.Length==0?null:sourceRestore,cancellation);
        var selection=operation.Recovery!;
        if(Evidence.Checkpoint!=UpdateCheckpoint.RollbackIntent)journal.Append(UpdateCheckpoint.RollbackIntent,restoreOperationId:selection.OperationId);
        else if(Evidence.RestoreOperationId!=selection.OperationId)throw new IOException("UPDATE_RECOVERY_RESTORE_REPLAY");
        if(restoredLease is not null)
        {
            if(restoredLease is null||restored is null||restored.OperationId!=selection.OperationId)throw new IOException("UPDATE_RECOVERY_RETAINED_OUTPUT_BINDING");
            var expected=files.PathOf("datasets/"+sourceData+"/restore-"+selection.OperationId+"/completed");
            if(!string.Equals(restored.RestoredDirectory,expected,StringComparison.OrdinalIgnoreCase))throw new IOException("UPDATE_RECOVERY_RETAINED_OUTPUT_BINDING");
            // Keep the exclusive gate continuously. Re-extraction/promotion
            // would open its process.lock against our own FileShare.None lease.
            // The duplicate proves the actual pinned directory identity; the
            // existing admission verifier rehashes the complete output while
            // deliberately excluding only that retained kernel-lock metadata.
            using var proof=restoredLease.DuplicateForDirectory(Path.Combine(expected,"data"));
            var pins=new List<IDisposable>();
            try
            {
                var source=sourceRestore.Length==0?files.PathOf("datasets/"+sourceData+"/data"):files.PathOf("datasets/"+sourceData+"/restore-"+sourceRestore+"/completed/data");
                var schema=NativeRecoveryAdmission.VerifyCompleted(files.PathOf("datasets/"+sourceData),source,selection.BackupId,selection.OperationId,selection.ReceiptDigest,expected,
                    Path.Combine(expected,"identity","master-key"),Path.Combine(expected,"policy","update-policy"),Path.Combine(expected,"policy","update-pin"),pins,files);
                if(schema<operation.MinimumSchema||schema>operation.MaximumSchema)throw new IOException("UPDATE_RECOVERY_RETAINED_SCHEMA");return restored;
            }
            finally{foreach(var pin in pins)pin.Dispose();}
        }
        if(restored is not null&&(restored.OperationId!=selection.OperationId||!string.Equals(restored.RestoredDirectory,files.PathOf("datasets/"+sourceData+"/restore-"+selection.OperationId+"/completed"),StringComparison.OrdinalIgnoreCase)))throw new IOException("UPDATE_RECOVERY_RETAINED_OUTPUT_BINDING");
        // A retry always uses the exact persisted restore operation. The first
        // attempt must create its journal with that ID, never adopt another.
        restored=await recovery.RestorePlannedToNewDirectoryAsync(selection.BackupId,operation.MinimumSchema,operation.MaximumSchema,selection.OperationId,cancellation:cancellation);
        if(restored.OperationId!=selection.OperationId)throw new IOException("UPDATE_RECOVERY_RESTORE_ID_CHANGED");
        restored=await recovery.PublishInstalledRestoreAsync(selection.BackupId,selection.OperationId,operation.MinimumSchema,operation.MaximumSchema,fault,cancellation);
        restoredLease??=files.Lease(Path.Combine(restored.RestoredDirectory,"data"));return restored;
    }
    private void RequireCommittedRestore(InstallationBinding binding)
    {
        ObjectDisposedException.ThrowIf(disposed,this);if(restored is null||preparedRecovery is null)throw new IOException("UPDATE_RECOVERY_NOT_RESTORED");
        var bytes=files.Read(files.PathOf("activation.json"),16384);var head=NativeJson.Read<InstalledActivation>(bytes);var operation=preparedRecovery.Operation;
        if(binding!=new InstallationBinding(anchor.InstallationId,InstalledActivationResolver.Hash(bytes))||head.ReleaseDigest!=operation.ReleaseDigest||head.DataId!=operation.DataId||head.Recovery!=operation.Recovery||head.PolicyDigest!=operation.PolicyDigest||head.PinDigest!=operation.PinDigest||head.MasterKeyDigest!=operation.MasterKeyDigest||head.Phase!="committed")throw new IOException("UPDATE_RECOVERY_COMMIT_BINDING");
    }
    private void ReleaseOfflineDataOwners()
    {restoredLease?.Dispose();restoredLease=null;recovery?.Dispose();recovery=null;retained?.Dispose();retained=null;recoveryRuntime?.Dispose();recoveryRuntime=null;}
    internal void ReleaseForRollbackStart(InstallationBinding binding)
    {
        RequireCommittedRestore(binding);if(Evidence.Checkpoint!=UpdateCheckpoint.RollbackIntent)throw new IOException("UPDATE_RECOVERY_START_ORDER");
        journal.Append(UpdateCheckpoint.RollbackStartIntent,restoreOperationId:restored!.OperationId);ReleaseOfflineDataOwners();
    }
    internal void MarkRollbackComplete(InstallationBinding binding)
    {
        RequireCommittedRestore(binding);if(Evidence.Checkpoint!=UpdateCheckpoint.RollbackStartIntent)throw new IOException("UPDATE_RECOVERY_HEALTH_ORDER");
        journal.Append(UpdateCheckpoint.RollbackComplete,restoreOperationId:restored!.OperationId);finished=true;Dispose();
    }
    public void Dispose()
    {
        if(disposed)return;
        if(!finished){Failure();return;} // Keep exact owners, including uncertain probe sessions, rooted for explicit recovery.
        restoredLease?.Dispose();restoredLease=null;recovery?.Dispose();recovery=null;retained?.Dispose();retained=null;recoveryRuntime?.Dispose();recoveryRuntime=null;inspector.Dispose();journal.Dispose();disposed=true;
        failedOwners.TryRemove(Evidence.Plan.OperationId,out _);
    }
}
