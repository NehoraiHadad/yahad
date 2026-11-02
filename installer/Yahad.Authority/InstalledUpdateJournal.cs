using System.Security.Cryptography;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record InstalledUpdatePlan(int Format,string InstallationId,string OperationId,string DataId,
    string PreviousActivationDigest,string PreviousRelease,string CandidateRelease,int SchemaBefore,
    int PreviousMinimumSchema,int PreviousMaximumSchema,int CandidateMinimumSchema,int CandidateMaximumSchema,
    string PolicyDigest,string PinDigest,string? MasterKeyDigest=null,int? MigrationTargetSchema=null,string? NewDataId=null,string? FullSystemPolicyDigest=null);
internal sealed record MigrationAttempt(int Number,string OperationId,string CloneOperationId,string NewDataId);
internal sealed record InstalledUpdateRecord(int Format,int Sequence,string PreviousDigest,InstalledUpdatePlan Plan,
    UpdateCheckpoint Checkpoint,string? BackupId,string? BackupReceiptDigest,string? RestoreOperationId,string? MigrationReceiptDigest=null,MigrationAttempt? MigrationAttempt=null);

// Protected, append-only transaction evidence, independent of the replaceable
// five-record activation transaction. The kernel lease serializes every update
// and is retained across the whole operation, including offline backup/recovery.
internal sealed class InstalledUpdateJournal:IDisposable
{
    private readonly InstalledStateFiles files;private readonly string installation,root;
    private readonly WindowsFileBoundary.DataLease operationLease;private readonly object serial=new();
    private List<InstalledUpdateRecord> records=[];private string previous="";private bool disposed;
    internal InstalledUpdateRecord? Current=>records.LastOrDefault();
    internal InstalledUpdateJournal(InstalledStateFiles files,string installationId,WindowsFileBoundary.DataLease? retainedSerialLease=null)
    {
        InstalledActivationResolver.Id(installationId);this.files=files;installation=installationId;
        foreach(var relative in new[]{".authority",".authority/update-serial",".authority/updates"})
        {var path=files.PathOf(relative);if(!Directory.Exists(path))files.CreateDirectory(path);using var pin=files.PinDirectory(path);}
        root=files.PathOf(".authority/updates");operationLease=retainedSerialLease?.DuplicateForDirectory(files.PathOf(".authority/update-serial"))??files.Lease(files.PathOf(".authority/update-serial"));
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Id(string value)=>InstalledActivationResolver.Id(value);
    private static void Digest(string value)=>InstalledActivationResolver.Digest(value);
    private void Validate(InstalledUpdatePlan plan)=>Validate(plan,installation);
    private static void Validate(InstalledUpdatePlan plan,string installation)
    {
        Id(plan.InstallationId);Id(plan.OperationId);Id(plan.DataId);
        foreach(var digest in new[]{plan.PreviousActivationDigest,plan.PreviousRelease,plan.CandidateRelease,plan.PolicyDigest,plan.PinDigest})Digest(digest);
        if(plan.MasterKeyDigest is not null)Digest(plan.MasterKeyDigest);
        if(plan.FullSystemPolicyDigest is not null)Digest(plan.FullSystemPolicyDigest);
        if(plan.MigrationTargetSchema is { } target)
        {
            if(plan.FullSystemPolicyDigest is null)throw new IOException("UPDATE_JOURNAL_FULL_POLICY_REQUIRED");
            if(plan.NewDataId is not null)Id(plan.NewDataId);
            if(target<=plan.SchemaBefore||target>100000||target!=plan.CandidateMinimumSchema||target!=plan.CandidateMaximumSchema||plan.NewDataId==plan.DataId)throw new IOException("UPDATE_JOURNAL_MIGRATION_PLAN");
        }
        else if(plan.NewDataId is not null)throw new IOException("UPDATE_JOURNAL_UNEXPECTED_NEW_DATA");
        if(plan.Format!=1||plan.InstallationId!=installation||plan.PreviousRelease==plan.CandidateRelease||
            plan.SchemaBefore is <1 or >100000||plan.PreviousMinimumSchema<1||plan.PreviousMaximumSchema<plan.PreviousMinimumSchema||plan.PreviousMaximumSchema>100000||
            plan.CandidateMinimumSchema<1||plan.CandidateMaximumSchema<plan.CandidateMinimumSchema||plan.CandidateMaximumSchema>100000||
            plan.SchemaBefore<plan.PreviousMinimumSchema||plan.SchemaBefore>plan.PreviousMaximumSchema||plan.MigrationTargetSchema is null&&(plan.SchemaBefore<plan.CandidateMinimumSchema||plan.SchemaBefore>plan.CandidateMaximumSchema))
            throw new IOException("UPDATE_JOURNAL_PLAN");
    }
    private string DirectoryOf(string id){Id(id);return Path.Combine(root,id);}
    private static bool Terminal(UpdateCheckpoint checkpoint)=>checkpoint is UpdateCheckpoint.Healthy or UpdateCheckpoint.RollbackComplete or UpdateCheckpoint.Aborted or UpdateCheckpoint.FullRollbackComplete;
    internal InstalledUpdateRecord Begin(InstalledUpdatePlan plan,Action<string>? fault=null)
    {
        lock(serial)
        {
            ObjectDisposedException.ThrowIf(disposed,this);Validate(plan);if(records.Count!=0)throw new IOException("UPDATE_JOURNAL_ALREADY_OPEN");
            var failures=0;var directories=Directory.EnumerateDirectories(root).Take(1001).ToArray();if(directories.Length>1000)throw new IOException("UPDATE_JOURNAL_CATALOG_BOUND");
            foreach(var directory in directories)
            {
                var existing=Read(Path.GetFileName(directory)).Records;var last=existing[^1];
                if(!Terminal(last.Checkpoint))throw new IOException("UPDATE_JOURNAL_UNFINISHED_OPERATION");
                if(last.Plan.CandidateRelease==plan.CandidateRelease&&(last.Checkpoint==UpdateCheckpoint.RollbackComplete||last.Checkpoint==UpdateCheckpoint.FullRollbackComplete&&existing.Any(x=>x.Checkpoint==UpdateCheckpoint.CandidateStartIntent)))failures++;
            }
            if(failures>=3)throw new IOException("UPDATE_RELEASE_QUARANTINED");
            var destination=DirectoryOf(plan.OperationId);if(Directory.Exists(destination)||File.Exists(destination))throw new IOException("UPDATE_JOURNAL_REPLAY");
            files.CreateDirectory(destination);return AppendCore(new(1,1,"",plan,UpdateCheckpoint.Prepared,null,null,null),fault);
        }
    }
    internal InstalledUpdateRecord Resume(string operationId)
    {
        lock(serial)
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(records.Count!=0)throw new IOException("UPDATE_JOURNAL_ALREADY_OPEN");
            var loaded=Read(operationId);records=loaded.Records;previous=loaded.Digest;return records[^1];
        }
    }
    internal void RequireNoUnfinished()
        =>RequireNoOtherUnfinished(null);
    internal void RequireNoOtherUnfinished(string? allowedOperationId)
    {
        if(allowedOperationId is not null)Id(allowedOperationId);
        lock(serial)
        {
            ObjectDisposedException.ThrowIf(disposed,this);
            var directories=Directory.EnumerateDirectories(root).Take(1001).ToArray();if(directories.Length>1000)throw new IOException("UPDATE_JOURNAL_CATALOG_BOUND");
            foreach(var directory in directories)
            {var record=Read(Path.GetFileName(directory)).Records[^1];if(!Terminal(record.Checkpoint)&&record.Plan.OperationId!=allowedOperationId)throw new IOException("UPDATE_JOURNAL_UNFINISHED_OPERATION");}
        }
    }
    // Observation grants no lock, writer, directory creation or execution
    // capability. Boot may inspect while the bounded helper owns the serial
    // lease; the exact same complete chain parser remains authoritative.
    internal static InstalledUpdateRecord Inspect(InstalledStateFiles files,string installationId,string operationId)
    {
        Id(installationId);Id(operationId);using var pin=files.PinDirectory(files.Root);
        return Read(files,installationId,files.PathOf(".authority/updates"),operationId).Records[^1];
    }
    private (List<InstalledUpdateRecord> Records,string Digest) Read(string operationId)=>Read(files,installation,root,operationId);
    private static (List<InstalledUpdateRecord> Records,string Digest) Read(InstalledStateFiles files,string installation,string root,string operationId)
    {
        Id(operationId);var directory=Path.Combine(root,operationId);using var pin=files.PinDirectory(directory);
        var paths=Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).ToArray();
        if(paths.Length is <1 or >128||paths.Any(path=>Directory.Exists(path)||Path.GetFileName(path).Contains(".partial",StringComparison.Ordinal)))throw new IOException("UPDATE_JOURNAL_RECONCILIATION_REQUIRED");
        var loaded=new List<InstalledUpdateRecord>();var digest="";
        foreach(var path in paths)
        {
            if(Path.GetFileName(path)!=$"{loaded.Count+1:D5}.json")throw new IOException("UPDATE_JOURNAL_SEQUENCE");
            var bytes=files.Read(path,32768);var record=NativeJson.Read<InstalledUpdateRecord>(bytes);Validate(record.Plan,installation);
            if(record.Format!=1||record.Sequence!=loaded.Count+1||record.PreviousDigest!=digest||record.Plan.OperationId!=operationId)throw new IOException("UPDATE_JOURNAL_CHAIN");
            ValidateTransition(loaded,record);loaded.Add(record);digest=Hash(bytes);
        }
        return(loaded,digest);
    }
    private static void ValidateTransition(List<InstalledUpdateRecord> prior,InstalledUpdateRecord record)
    {
        if(!Enum.IsDefined(record.Checkpoint))throw new IOException("UPDATE_JOURNAL_CHECKPOINT");
        if(record.BackupId is not null)Id(record.BackupId);if(record.BackupReceiptDigest is not null)Digest(record.BackupReceiptDigest);if(record.RestoreOperationId is not null)Id(record.RestoreOperationId);
        if(record.MigrationReceiptDigest is not null)Digest(record.MigrationReceiptDigest);
        if(record.Plan.MigrationTargetSchema is null&&(record.MigrationReceiptDigest is not null||record.MigrationAttempt is not null&&record.Plan.FullSystemPolicyDigest is null))throw new IOException("UPDATE_JOURNAL_UNEXPECTED_MIGRATION");
        if(record.Plan.MigrationTargetSchema is not null&&record.Checkpoint is(UpdateCheckpoint.MigrationIntent or UpdateCheckpoint.MigrationVerified or UpdateCheckpoint.ActivationIntent or UpdateCheckpoint.CandidateStartIntent or UpdateCheckpoint.Healthy)&&record.MigrationAttempt is null)throw new IOException("UPDATE_JOURNAL_MIGRATION_ATTEMPT_REQUIRED");
        if(record.MigrationAttempt is { } attempt)
        {
            Id(attempt.OperationId);Id(attempt.CloneOperationId);Id(attempt.NewDataId);
            if(attempt.Number is <1 or >3||attempt.NewDataId==record.Plan.DataId)throw new IOException("UPDATE_JOURNAL_MIGRATION_ATTEMPT");
            var previous=prior.LastOrDefault()?.MigrationAttempt;
            if(attempt!=previous&&(record.Checkpoint is not(UpdateCheckpoint.MigrationIntent or UpdateCheckpoint.CloneIntent)||attempt.Number!=(previous?.Number??0)+1||prior.Any(x=>x.MigrationAttempt is { } old&&(old.OperationId==attempt.OperationId||old.CloneOperationId==attempt.CloneOperationId||old.NewDataId==attempt.NewDataId))))throw new IOException("UPDATE_JOURNAL_MIGRATION_ATTEMPT_REPLAY");
        }
        if((record.BackupId is null)!=(record.BackupReceiptDigest is null))throw new IOException("UPDATE_JOURNAL_BACKUP");
        if(prior.Count==0)
        {if(record.Checkpoint!=UpdateCheckpoint.Prepared||record.BackupId is not null||record.RestoreOperationId is not null||record.MigrationReceiptDigest is not null||record.MigrationAttempt is not null)throw new IOException("UPDATE_JOURNAL_INITIAL");return;}
        var last=prior[^1];if(record.Plan!=last.Plan||last.BackupId is not null&&(record.BackupId!=last.BackupId||record.BackupReceiptDigest!=last.BackupReceiptDigest)||last.RestoreOperationId is not null&&record.RestoreOperationId!=last.RestoreOperationId||last.MigrationReceiptDigest is not null&&record.MigrationReceiptDigest!=last.MigrationReceiptDigest)throw new IOException("UPDATE_JOURNAL_BINDING");
        var valid=(last.Checkpoint,record.Checkpoint) switch
        {
            (UpdateCheckpoint.Prepared,UpdateCheckpoint.Quiesced)=>true,
            (UpdateCheckpoint.Quiesced,UpdateCheckpoint.BackupVerified)=>true,
            (UpdateCheckpoint.BackupVerified,UpdateCheckpoint.ActivationIntent)=>record.Plan.MigrationTargetSchema is null&&record.Plan.FullSystemPolicyDigest is null,
            (UpdateCheckpoint.BackupVerified,UpdateCheckpoint.CloneIntent)=>record.Plan.FullSystemPolicyDigest is not null&&record.Plan.MigrationTargetSchema is null,
            (UpdateCheckpoint.CloneIntent or UpdateCheckpoint.RecoveryRequired,UpdateCheckpoint.CloneIntent)=>record.Plan.FullSystemPolicyDigest is not null&&record.Plan.MigrationTargetSchema is null&&record.MigrationAttempt!=last.MigrationAttempt,
            (UpdateCheckpoint.CloneIntent,UpdateCheckpoint.CloneVerified)=>record.Plan.FullSystemPolicyDigest is not null&&record.MigrationAttempt is not null,
            (UpdateCheckpoint.CloneVerified,UpdateCheckpoint.ActivationIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.BackupVerified,UpdateCheckpoint.MigrationIntent)=>record.Plan.MigrationTargetSchema is not null,
            (UpdateCheckpoint.MigrationIntent or UpdateCheckpoint.RecoveryRequired,UpdateCheckpoint.MigrationIntent)=>record.Plan.MigrationTargetSchema is not null&&last.MigrationReceiptDigest is null&&record.MigrationAttempt!=last.MigrationAttempt,
            (UpdateCheckpoint.MigrationIntent,UpdateCheckpoint.MigrationVerified)=>record.MigrationReceiptDigest is not null,
            (UpdateCheckpoint.MigrationVerified,UpdateCheckpoint.ActivationIntent)=>record.MigrationReceiptDigest is not null,
            (UpdateCheckpoint.ActivationIntent,UpdateCheckpoint.CandidateStartIntent)=>true,
            (UpdateCheckpoint.CandidateStartIntent,UpdateCheckpoint.Healthy)=>record.Plan.FullSystemPolicyDigest is null,
            (UpdateCheckpoint.CandidateStartIntent,UpdateCheckpoint.ProbationHealthy)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.ProbationHealthy,UpdateCheckpoint.EffectsReleaseIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.EffectsReleaseIntent,UpdateCheckpoint.Healthy)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.ForwardRepairEffectsIntent,UpdateCheckpoint.ForwardRepairEffectsIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.ForwardRepairEffectsIntent,UpdateCheckpoint.Healthy)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.ForwardRepairEffectsIntent or UpdateCheckpoint.CurrentAvailabilityStartIntent,UpdateCheckpoint.CurrentAvailabilityStartIntent)=>record.Plan.FullSystemPolicyDigest is not null&&prior.Any(x=>x.Checkpoint is UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.ForwardRepairEffectsIntent),
            (UpdateCheckpoint.CurrentAvailabilityStartIntent,UpdateCheckpoint.Healthy)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.ActivationIntent or UpdateCheckpoint.CandidateStartIntent or UpdateCheckpoint.ProbationHealthy or UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.CloneIntent or UpdateCheckpoint.CloneVerified or UpdateCheckpoint.MigrationIntent or UpdateCheckpoint.MigrationVerified or UpdateCheckpoint.BackupVerified or UpdateCheckpoint.FullRollbackEffectsIntent,UpdateCheckpoint.FullRollbackStartIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.FullRollbackStartIntent,UpdateCheckpoint.FullRollbackComplete)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.FullRollbackStartIntent,UpdateCheckpoint.FullRollbackEffectsIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.FullRollbackEffectsIntent,UpdateCheckpoint.FullRollbackComplete)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.ActivationIntent or UpdateCheckpoint.CandidateStartIntent or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.Healthy,UpdateCheckpoint.RollbackIntent)=>true,
            (UpdateCheckpoint.RollbackIntent,UpdateCheckpoint.RollbackComplete)=>true,
            (UpdateCheckpoint.RollbackIntent,UpdateCheckpoint.RollbackStartIntent)=>true,
            (UpdateCheckpoint.RollbackStartIntent,UpdateCheckpoint.RollbackComplete)=>true,
            (UpdateCheckpoint.Prepared or UpdateCheckpoint.Quiesced or UpdateCheckpoint.BackupVerified or UpdateCheckpoint.ActivationIntent or UpdateCheckpoint.RecoveryRequired,UpdateCheckpoint.PreviousStartIntent)=>!prior.Any(x=>x.Checkpoint is UpdateCheckpoint.CandidateStartIntent or UpdateCheckpoint.RollbackIntent or UpdateCheckpoint.RollbackStartIntent),
            (UpdateCheckpoint.PreviousStartIntent,UpdateCheckpoint.Aborted)=>true,
            (UpdateCheckpoint.PreviousStartIntent,UpdateCheckpoint.PreviousEffectsReleaseIntent)=>record.Plan.FullSystemPolicyDigest is not null,
            (UpdateCheckpoint.PreviousEffectsReleaseIntent,UpdateCheckpoint.Aborted)=>record.Plan.FullSystemPolicyDigest is not null,
            (_,UpdateCheckpoint.RecoveryRequired)=>!Terminal(last.Checkpoint)&&last.Checkpoint!=UpdateCheckpoint.RecoveryRequired,
            _=>false
        };
        if(!valid)throw new IOException("UPDATE_JOURNAL_TRANSITION");
        if(record.Checkpoint is not(UpdateCheckpoint.Prepared or UpdateCheckpoint.Quiesced or UpdateCheckpoint.RecoveryRequired or UpdateCheckpoint.PreviousStartIntent or UpdateCheckpoint.PreviousEffectsReleaseIntent or UpdateCheckpoint.Aborted)&&record.BackupId is null)throw new IOException("UPDATE_JOURNAL_BACKUP_REQUIRED");
        if(record.RestoreOperationId is not null&&record.Checkpoint is not(UpdateCheckpoint.RollbackIntent or UpdateCheckpoint.RollbackStartIntent or UpdateCheckpoint.RollbackComplete or UpdateCheckpoint.RecoveryRequired))throw new IOException("UPDATE_JOURNAL_RESTORE_SCOPE");
        if(record.Checkpoint==UpdateCheckpoint.RollbackComplete&&prior.Any(r=>r.Checkpoint==UpdateCheckpoint.CandidateStartIntent)&&record.RestoreOperationId is null)throw new IOException("UPDATE_JOURNAL_NEW_DATA_RESTORE_REQUIRED");
    }
    internal InstalledUpdateRecord Append(UpdateCheckpoint checkpoint,NativeBackupResult? backup=null,string? restoreOperationId=null,Action<string>? fault=null,string? migrationReceiptDigest=null,MigrationAttempt? migrationAttempt=null)
    {
        lock(serial)
        {
            ObjectDisposedException.ThrowIf(disposed,this);var last=Current??throw new IOException("UPDATE_JOURNAL_NOT_OPEN");
            var record=new InstalledUpdateRecord(1,records.Count+1,previous,last.Plan,checkpoint,backup?.BackupId??last.BackupId,backup?.ReceiptDigest??last.BackupReceiptDigest,restoreOperationId??last.RestoreOperationId,migrationReceiptDigest??last.MigrationReceiptDigest,migrationAttempt??last.MigrationAttempt);
            if(backup is not null&&backup.ObservedSchema!=last.Plan.SchemaBefore)throw new IOException("UPDATE_JOURNAL_BACKUP_SCHEMA");
            ValidateTransition(records,record);return AppendCore(record,fault);
        }
    }
    private InstalledUpdateRecord AppendCore(InstalledUpdateRecord record,Action<string>? fault)
    {
        if(records.Count>=128)throw new IOException("UPDATE_JOURNAL_BOUND");
        var bytes=JsonSerializer.SerializeToUtf8Bytes(record);if(bytes.Length>32768)throw new IOException("UPDATE_JOURNAL_BOUND");
        var destination=Path.Combine(DirectoryOf(record.Plan.OperationId),$"{record.Sequence:D5}.json");var partial=destination+".partial-"+Guid.NewGuid().ToString("N");
        using(var file=files.Create(partial)){file.Write(bytes);fault?.Invoke("before-update-journal-flush");file.Flush(true);}
        if(!MoveFileExW(@"\\?\"+partial,@"\\?\"+destination,8))throw new Win32Exception();
        records.Add(record);previous=Hash(bytes);fault?.Invoke("journal-"+record.Checkpoint);return record;
    }
    internal UpdateJournal RecoveryDecisionEvidence()
    {
        var record=Current??throw new IOException("UPDATE_JOURNAL_NOT_OPEN");var p=record.Plan;
        return new(record.Checkpoint,p.PreviousRelease,p.CandidateRelease,p.SchemaBefore,p.PreviousMinimumSchema,p.PreviousMaximumSchema,p.CandidateMinimumSchema,p.CandidateMaximumSchema,p.PolicyDigest,p.PinDigest,record.BackupReceiptDigest);
    }
    internal bool CandidateStartWasRecorded=>records.Any(x=>x.Checkpoint==UpdateCheckpoint.CandidateStartIntent);
    internal bool FullRollbackWasRecorded=>records.Any(x=>x.Checkpoint==UpdateCheckpoint.FullRollbackStartIntent);
    internal bool FullRollbackEffectsMayHaveBeenReleased=>records.Any(x=>x.Checkpoint is UpdateCheckpoint.FullRollbackEffectsIntent or UpdateCheckpoint.PreviousEffectsReleaseIntent);
    internal bool EffectsMayHaveBeenReleased=>records.Any(x=>x.Checkpoint is UpdateCheckpoint.EffectsReleaseIntent or UpdateCheckpoint.ForwardRepairEffectsIntent);
    internal WindowsFileBoundary.DataLease DuplicateSerialLease()=>operationLease.DuplicateForDirectory(files.PathOf(".authority/update-serial"));
    public void Dispose(){lock(serial){if(disposed)return;operationLease.Dispose();disposed=true;}}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool MoveFileExW(string existing,string destination,uint flags);
}
