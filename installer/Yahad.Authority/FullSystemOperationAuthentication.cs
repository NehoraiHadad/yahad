using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;
internal sealed record FullCompatibleClone(string OperationId,string SourceDataId,string CloneOperationId,string NewDataId,string BackupId,string BackupReceiptDigest,string? SourceRestoreOperationId=null);

// A new explicit full-system policy is the issuer. This adapter cannot renew,
// reinterpret, or bypass any legacy same-schema routine delegation.
internal sealed class FullSystemOperationAuthentication:IOperationAuthentication
{
    private readonly FullSystemUpdatePolicy policy;private readonly VerifiedSystemOperation system;private readonly ProtectedOperation operation;
    private readonly Func<DateTimeOffset> clock;private readonly VerifiedOperation issued;private readonly bool reconciling;
    internal FullSystemOperationAuthentication(FullSystemUpdatePolicy policy,VerifiedSystemOperation system,ProtectedOperation operation,
        Func<DateTimeOffset> clock,byte[] previousActivationBytes,FullCompatibleClone compatible,VerifiedOperation? reconciliation=null)
    {
        this.policy=policy;this.system=system;this.operation=operation;this.clock=clock;
        if(InstalledActivationResolver.Hash(previousActivationBytes)!=system.PreviousActivationDigest)throw new UnauthorizedAccessException("FULL_SYSTEM_SOURCE_ACTIVATION");
        var previous=NativeJson.Read<InstalledActivation>(previousActivationBytes);
        if(previous.InstallationId!=system.InstallationId||operation.ExpectedEpoch!=previous.Epoch||operation.ExpectedActivationDigest!=system.PreviousActivationDigest||
            operation.MasterKeyDigest!=previous.MasterKeyDigest||operation.PolicyDigest!=previous.PolicyDigest||operation.PinDigest!=previous.PinDigest)throw new UnauthorizedAccessException("FULL_SYSTEM_SOURCE_IDENTITY");
        foreach(var id in new[]{compatible.OperationId,compatible.SourceDataId,compatible.CloneOperationId,compatible.NewDataId,compatible.BackupId})InstalledActivationResolver.Id(id);InstalledActivationResolver.Digest(compatible.BackupReceiptDigest);
        // A schema bump uses this same clone operation: the candidate release's
        // Store migrates the clone when it opens it. Only a downgrade is refused.
        if(system.TargetSchema<system.SourceSchema||operation.Kind!="recover"||operation.InstallationId!=system.InstallationId||operation.ReleaseDigest!=system.ApplicationBundleDigest||
            operation.Nonce!=compatible.OperationId||operation.DataId!=compatible.NewDataId||operation.DataId==previous.DataId||compatible.SourceDataId!=(previous.Recovery?.SourceDataId??previous.DataId)||
            compatible.SourceRestoreOperationId!=previous.Recovery?.OperationId||operation.MinimumSchema!=system.TargetSchema||operation.MaximumSchema!=system.TargetSchema||operation.Recovery!=new InstalledRecovery(compatible.SourceDataId,compatible.BackupId,compatible.CloneOperationId,compatible.BackupReceiptDigest,compatible.SourceRestoreOperationId))throw new UnauthorizedAccessException("FULL_SYSTEM_COMPATIBLE_OPERATION_SCOPE");
        reconciling=reconciliation is not null;if(reconciling)policy.RequireRecoveryEvidence(system);else policy.RequireCurrent(system);var now=clock().ToUnixTimeSeconds();
        var digest=AuthorityProtocol.Digest(operation);
        if(reconciliation is {} prior&&(prior.Subject!=policy.Subject||prior.InstallationId!=system.InstallationId||prior.OperationDigest!=digest||prior.Scope!=operation.Kind||prior.ApprovedUnix<=0||prior.ApprovedUnix>now))throw new UnauthorizedAccessException("FULL_SYSTEM_RECONCILIATION_APPROVAL");
        if(!reconciling&&(operation.ExpiresUnix<=now||operation.ExpiresUnix>now+600))throw new UnauthorizedAccessException("FULL_SYSTEM_OPERATION_WINDOW");
        issued=new(policy.Subject,system.InstallationId,digest,operation.Kind,reconciliation?.ApprovedUnix??now,reconciling?now+600:operation.ExpiresUnix);
    }
    public Task<VerifiedOperation> AuthenticateAsync(string digest,string requiredScope,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();if(digest!=issued.OperationDigest||reconciling&&requiredScope!="reconcile"||requiredScope!=issued.Scope&&requiredScope!="reconcile")throw new UnauthorizedAccessException("FULL_SYSTEM_OPERATION_SCOPE");
        RequireCurrent(issued);return Task.FromResult(issued with{Scope=requiredScope});
    }
    public void RequireCurrent(VerifiedOperation approval)
    {
        if(reconciling)policy.RequireRecoveryEvidence(system);else policy.RequireCurrent(system);var now=clock().ToUnixTimeSeconds();
        if(approval.Subject!=issued.Subject||approval.InstallationId!=issued.InstallationId||approval.OperationDigest!=issued.OperationDigest||approval.ApprovedUnix!=issued.ApprovedUnix||approval.ValidUntilUnix!=issued.ValidUntilUnix||
            approval.Scope!=issued.Scope&&approval.Scope!="reconcile"||issued.ValidUntilUnix<=now||!reconciling&&operation.ExpiresUnix<=now)throw new UnauthorizedAccessException("FULL_SYSTEM_OPERATION_REVOKED_OR_EXPIRED");
    }
}

// Only this separately enrolled full-policy issuer may change the data
// selection on a safe automatic return. Legacy activate authentication has no
// such capability. The transaction supplies the journal and protected heads;
// no recovery flag or path is accepted from app requests.
internal sealed class FullSystemSelectionAuthentication:IFullSystemSelectionAuthentication
{
    private readonly FullSystemUpdatePolicy policy;private readonly VerifiedSystemOperation system;private readonly InstalledUpdateJournal journal;
    private readonly ProtectedOperation operation;private readonly InstalledActivation target,current;private readonly VerifiedOperation issued;private readonly Func<DateTimeOffset> clock;private readonly bool reconciling;
    internal FullSystemSelectionAuthentication(FullSystemUpdatePolicy policy,VerifiedSystemOperation system,InstalledUpdateJournal journal,
        byte[] previousActivationBytes,byte[] currentActivationBytes,ProtectedOperation operation,Func<DateTimeOffset> clock,VerifiedOperation? reconciliation=null)
    {
        this.policy=policy;this.system=system;this.journal=journal;this.operation=operation;this.clock=clock;
        if(InstalledActivationResolver.Hash(previousActivationBytes)!=system.PreviousActivationDigest)throw new IOException("FULL_RETURN_ORIGINAL_HEAD");
        var previous=NativeJson.Read<InstalledActivation>(previousActivationBytes);current=NativeJson.Read<InstalledActivation>(currentActivationBytes);
        var evidence=journal.Current??throw new IOException("FULL_RETURN_JOURNAL_REQUIRED");var p=evidence.Plan;
        if(p.OperationId!=system.OperationId||p.FullSystemPolicyDigest!=system.PolicyDigest||p.PreviousActivationDigest!=system.PreviousActivationDigest||p.CandidateRelease!=system.ApplicationBundleDigest||p.PreviousRelease!=previous.ReleaseDigest||
            current.InstallationId!=system.InstallationId||current.ReleaseDigest!=system.ApplicationBundleDigest&&InstalledActivationResolver.Hash(currentActivationBytes)!=system.PreviousActivationDigest)throw new IOException("FULL_RETURN_JOURNAL_BINDING");
        if(journal.EffectsMayHaveBeenReleased&&system.SourceSchema!=system.TargetSchema)throw new IOException("FULL_RETURN_FORWARD_REPAIR_REQUIRED");
        target=journal.EffectsMayHaveBeenReleased?previous with{DataId=current.DataId,Recovery=current.Recovery}:previous;
        if(operation.Kind!="activate"||operation.InstallationId!=system.InstallationId||operation.ExpectedActivationDigest!=InstalledActivationResolver.Hash(currentActivationBytes)||operation.ExpectedEpoch!=current.Epoch||
            operation.ReleaseDigest!=target.ReleaseDigest||operation.DataId!=target.DataId||operation.Recovery!=target.Recovery||
            operation.MasterKeyDigest!=target.MasterKeyDigest||operation.PolicyDigest!=target.PolicyDigest||operation.PinDigest!=target.PinDigest||operation.MinimumSchema!=target.MinimumSchema||operation.MaximumSchema!=target.MaximumSchema||
            current.MasterKeyDigest!=target.MasterKeyDigest||current.PolicyDigest!=target.PolicyDigest||current.PinDigest!=target.PinDigest)throw new IOException("FULL_RETURN_SELECTION_BINDING");
        policy.RequireRecoveryEvidence(system);var now=clock().ToUnixTimeSeconds();reconciling=reconciliation is not null;var digest=AuthorityProtocol.Digest(operation);
        if(reconciliation is {} prior&&(prior.Subject!=policy.Subject||prior.InstallationId!=system.InstallationId||prior.OperationDigest!=digest||prior.Scope!="activate"||prior.ApprovedUnix<=0||prior.ApprovedUnix>now))throw new UnauthorizedAccessException("FULL_RETURN_RECONCILIATION_APPROVAL");
        if(!reconciling&&(operation.ExpiresUnix<=now||operation.ExpiresUnix>now+600))throw new IOException("FULL_RETURN_WINDOW");
        issued=new(policy.Subject,system.InstallationId,digest,"activate",reconciliation?.ApprovedUnix??now,reconciling?now+600:operation.ExpiresUnix);
    }
    public void RequireSelection(ProtectedOperation selected,InstalledActivation observed)
    {if(selected!=operation||observed!=current)throw new IOException("FULL_RETURN_SELECTION_CHANGED");RequireCurrent(issued);}
    public Task<VerifiedOperation> AuthenticateAsync(string digest,string scope,CancellationToken cancellation)
    {cancellation.ThrowIfCancellationRequested();if(digest!=issued.OperationDigest||scope!=(reconciling?"reconcile":"activate"))throw new UnauthorizedAccessException("FULL_RETURN_SCOPE");RequireCurrent(issued);return Task.FromResult(issued with{Scope=scope});}
    public void RequireCurrent(VerifiedOperation approval)
    {policy.RequireRecoveryEvidence(system);if(approval!=issued&&!(reconciling&&approval==(issued with{Scope="reconcile"}))||issued.ValidUntilUnix<=clock().ToUnixTimeSeconds()||journal.EffectsMayHaveBeenReleased&&system.SourceSchema!=system.TargetSchema)throw new UnauthorizedAccessException("FULL_RETURN_NO_LONGER_AUTHORIZED");}
}
