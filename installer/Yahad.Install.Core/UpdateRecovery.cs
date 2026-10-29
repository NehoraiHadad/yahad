namespace Yahad.Install;

public enum UpdateCheckpoint
{
    Prepared, Quiesced, BackupVerified, ActivationIntent, CandidateStartIntent,
    Healthy, RecoveryRequired, RollbackIntent, RollbackComplete
}
public enum RecoveryAction
{
    WaitForQuiescence, Refuse, StartPreviousUnchanged, StartCandidate,
    RestorePreviousPayloadOnly, RestoreVerifiedBackupOffline, ReconcileInterruptedRestore
}
public sealed record UpdateJournal(UpdateCheckpoint Checkpoint, string PreviousRelease, string CandidateRelease,
    int SchemaBefore, int PreviousMinimumSchema, int PreviousMaximumSchema,
    int CandidateMinimumSchema, int CandidateMaximumSchema, string PolicyDigest, string PinDigest,
    string? VerifiedBackupReceiptDigest);
public sealed record RecoveryFacts(bool ExclusiveDataLeaseOwned, bool EntireProcessTreeEmpty,
    string ActiveRelease, int ObservedSchema, string PolicyDigest, string PinDigest,
    string? VerifiedBackupReceiptDigest, bool BackupIncludesDataKeysAndPolicy, bool ExplicitOwnerRollback);
public sealed record RecoveryPlan(RecoveryAction Action, string Reason, string? Release,
    bool DatabaseRestore, bool RequiresDurableRollbackIntent);

/// <summary>
/// Pure recovery decision logic for the sole updater. It never reads/writes a DB,
/// changes a release pointer, installs SCM services or overrides policy/pins.
/// The real adapter must authenticate receipts and durably journal intent BEFORE
/// starting a candidate or restoring files; observations are not caller claims.
/// </summary>
public static class UpdateRecovery
{
    private static bool ReleaseId(string value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-z0-9][a-z0-9.-]{0,63}$");
    private static bool Digest(string? value) => value is not null &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^[a-f0-9]{64}$");
    public static RecoveryPlan Decide(UpdateJournal journal, RecoveryFacts facts)
    {
        RecoveryPlan Refuse(string reason) => new(RecoveryAction.Refuse, reason, null, false, false);
        if (!Enum.IsDefined(journal.Checkpoint) || !ReleaseId(journal.PreviousRelease) || !ReleaseId(journal.CandidateRelease) ||
            journal.PreviousRelease == journal.CandidateRelease || journal.SchemaBefore < 1 || facts.ObservedSchema < 1 ||
            journal.PreviousMinimumSchema < 1 || journal.PreviousMaximumSchema < journal.PreviousMinimumSchema ||
            journal.CandidateMinimumSchema < 1 || journal.CandidateMaximumSchema < journal.CandidateMinimumSchema ||
            !Digest(journal.PolicyDigest) || !Digest(journal.PinDigest) ||
            journal.VerifiedBackupReceiptDigest is not null && !Digest(journal.VerifiedBackupReceiptDigest)) return Refuse("JOURNAL_INVALID");
        if (!facts.ExclusiveDataLeaseOwned || !facts.EntireProcessTreeEmpty)
            return new(RecoveryAction.WaitForQuiescence, "OFFLINE_DATA_BOUNDARY_REQUIRED", null, false, false);
        if (facts.PolicyDigest != journal.PolicyDigest || facts.PinDigest != journal.PinDigest) return Refuse("POLICY_OR_PIN_CHANGED");
        if (facts.ActiveRelease != journal.PreviousRelease && facts.ActiveRelease != journal.CandidateRelease) return Refuse("ACTIVE_RELEASE_UNKNOWN");
        var previousCanRead = facts.ObservedSchema >= journal.PreviousMinimumSchema && facts.ObservedSchema <= journal.PreviousMaximumSchema;
        var backupCanRead = journal.SchemaBefore >= journal.PreviousMinimumSchema && journal.SchemaBefore <= journal.PreviousMaximumSchema;
        var receiptMatches = Digest(journal.VerifiedBackupReceiptDigest) &&
            facts.VerifiedBackupReceiptDigest == journal.VerifiedBackupReceiptDigest && facts.BackupIncludesDataKeysAndPolicy;
        if (journal.Checkpoint == UpdateCheckpoint.RollbackIntent)
            return new(RecoveryAction.ReconcileInterruptedRestore, "RESTORE_OUTCOME_UNCERTAIN", null, false, false);
        if (journal.Checkpoint is UpdateCheckpoint.Prepared or UpdateCheckpoint.Quiesced or UpdateCheckpoint.BackupVerified)
            return facts.ActiveRelease == journal.PreviousRelease && previousCanRead
                ? new(RecoveryAction.StartPreviousUnchanged, "CANDIDATE_NOT_STARTED", journal.PreviousRelease, false, false)
                : Refuse("PREACTIVATION_STATE_CHANGED");
        if (journal.Checkpoint == UpdateCheckpoint.RollbackComplete)
            return receiptMatches && facts.ActiveRelease == journal.PreviousRelease && previousCanRead
                ? new(RecoveryAction.StartPreviousUnchanged, "VERIFIED_ROLLBACK_COMPLETE", journal.PreviousRelease, false, false)
                : Refuse("ROLLBACK_STATE_UNVERIFIED");
        if (journal.Checkpoint == UpdateCheckpoint.Healthy && !facts.ExplicitOwnerRollback)
            return facts.ActiveRelease == journal.CandidateRelease && facts.ObservedSchema >= journal.CandidateMinimumSchema && facts.ObservedSchema <= journal.CandidateMaximumSchema
                ? new(RecoveryAction.StartCandidate, "VERIFIED_ACTIVE_CANDIDATE", journal.CandidateRelease, false, false)
                : Refuse("ACTIVE_CANDIDATE_INCOMPATIBLE");
        if (!facts.ExplicitOwnerRollback) return Refuse("EXPLICIT_RECOVERY_REQUIRED");
        if (!receiptMatches || !backupCanRead) return Refuse("VERIFIED_COMPATIBLE_BACKUP_REQUIRED");
        // ActivationIntent is durable BEFORE pointer changes. CandidateStartIntent
        // must be durable BEFORE execution, so only this phase can prove no new
        // schema writer has run. Every later/uncertain phase requires offline restore.
        if (journal.Checkpoint == UpdateCheckpoint.ActivationIntent && previousCanRead && facts.ObservedSchema == journal.SchemaBefore)
            return new(RecoveryAction.RestorePreviousPayloadOnly, "NO_CANDIDATE_START_INTENT", journal.PreviousRelease, false, true);
        return new(RecoveryAction.RestoreVerifiedBackupOffline, "OWNER_REQUESTED_VERIFIED_RECOVERY", journal.PreviousRelease, true, true);
    }
}
