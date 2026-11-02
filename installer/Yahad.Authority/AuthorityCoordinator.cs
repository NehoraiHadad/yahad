using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record CatalogOperation(int Format,ProtectedOperation Operation,OperationFacts Facts);
internal sealed class PrivateOperationCatalog
{
    private readonly string? root;private readonly CatalogOperation? frozen;
    internal PrivateOperationCatalog(string privateRoot){root=privateRoot;}
    internal PrivateOperationCatalog(CatalogOperation selected){frozen=selected;}
    internal CatalogOperation Read(string digest)
    {
        InstalledActivationResolver.Digest(digest);if(frozen is not null){if(frozen.Operation.ReleaseDigest!=digest)throw new IOException("AUTHORITY_CATALOG_BINDING");return frozen;}
        SafeDirectory.ValidateSession(root!);using var file=WindowsFileBoundary.OpenPrivateRead(Path.Combine(root!,digest+".json"));var entry=NativeJson.Read<CatalogOperation>(file.Small(32768));
        if(entry.Format!=1||entry.Operation.ReleaseDigest!=digest)throw new IOException("AUTHORITY_CATALOG_BINDING");return entry;
    }
}
internal sealed class AuthorityCoordinator(string runtimeSid,PrivateOperationCatalog catalog,OwnerApprovalLedger ledger,
    ProtectedStateTransactionWriter writer,RuntimeAuthorityGate gate,IAuthorityRuntimeLifecycle? runtimeHealth=null)
{
    private CatalogOperation? selected;private string? requestId;private bool committed;private string? delegated;
    private string phase="idle";private InstallationBinding? committedBinding;
    internal bool RequiresRuntimeCleanup=>runtimeHealth?.CleanupReconciliationRequired==true;
    internal bool IsRoutine=>delegated is not null;
    internal string Phase=>committed&&runtimeHealth is not null&&runtimeHealth.Phase!="awaiting_runtime"?runtimeHealth.Phase:phase;
    internal ApprovalChallenge? Submit(AuthorityPeer runtime,byte[] requestFrame,string? delegationId=null)
    {
        if(runtime.Sid!=runtimeSid||runtime.ProcessId<1||runtime.Created<=0||runtime.Interactive)throw new UnauthorizedAccessException("AUTHORITY_RUNTIME_PEER");
        var request=AuthorityProtocol.Request(requestFrame);
        if(request.Action!="install")throw new IOException("AUTHORITY_ACTION_NOT_IMPLEMENTED");
        return SubmitCore(request.Id,request.TargetDigest!,delegationId);
    }
    // Only the authority's protected-intake scheduler calls this. It does not
    // manufacture a runtime principal and has no external RPC route.
    internal ApprovalChallenge? SubmitRoutine(string id,string targetDigest,string delegationId)
    {
        InstalledActivationResolver.Id(id);InstalledActivationResolver.Digest(targetDigest);InstalledActivationResolver.Id(delegationId);
        return SubmitCore(id,targetDigest,delegationId);
    }
    private ApprovalChallenge? SubmitCore(string id,string targetDigest,string? delegationId)
    {
        if(requestId is not null)throw new IOException("AUTHORITY_REQUEST_PENDING");
        selected=catalog.Read(targetDigest);requestId=id;delegated=delegationId;
        try
        {
            if(delegationId is null){var c=ledger.Challenge(selected.Operation);phase="awaiting_local_approval";return c;}
            ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegationId,reserve:false);phase="preparing";return null;
        }
        catch{selected=null;requestId=null;throw;}
    }
    internal async Task<InstallationBinding> CommitAsync(CancellationToken cancellation=default)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        _ =await ledger.AuthenticateAsync(AuthorityProtocol.Digest(selected.Operation),selected.Operation.Kind,cancellation);
        gate.RequireQuiescence();phase="activating";
        try{var result=await writer.CommitAsync(selected.Operation,cancellation:cancellation);committed=true;committedBinding=result;phase="awaiting_runtime";return result;}
        catch{phase="recovery_required";throw;}
    }
    internal async Task RequireApprovalAsync(CancellationToken cancellation)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        cancellation.ThrowIfCancellationRequested();
        if(delegated is not null)ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegated,reserve:false);
        else _=await ledger.AuthenticateAsync(AuthorityProtocol.Digest(selected.Operation),selected.Operation.Kind,cancellation);
        cancellation.ThrowIfCancellationRequested();
    }
    // Called after an observed idle fence and before disruption. Busy retries do
    // scope validation only; the bounded operation budget is spent at this point.
    internal async Task ReserveApprovalAsync(CancellationToken cancellation)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        cancellation.ThrowIfCancellationRequested();
        if(delegated is not null)ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegated,reserve:true);
        _=await ledger.AuthenticateAsync(AuthorityProtocol.Digest(selected.Operation),selected.Operation.Kind,cancellation);
        cancellation.ThrowIfCancellationRequested();
    }
    internal Task StartRuntimeAsync(CancellationToken cancellation=default)
    {
        if(!committed||committedBinding is null||selected is null)throw new IOException("AUTHORITY_COMMIT_REQUIRED");
        if(runtimeHealth is null)throw new IOException("AUTHORITY_RUNTIME_UNCONFIGURED");
        return runtimeHealth.StartCommittedAsync(committedBinding,new(selected.Operation.ReleaseDigest),cancellation);
    }
    internal Task StopRuntimeAsync(CancellationToken cancellation=default)=>runtimeHealth?.StopAsync(cancellation)??throw new IOException("AUTHORITY_RUNTIME_UNCONFIGURED");
    internal Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation=default)
        =>runtimeHealth is IAuthorityRuntimeUpdatePreparation idle?idle.PrepareUpdateAsync(id,cancellation):throw new IOException("RUNTIME_IDLE_CHANNEL_UNCONFIGURED");
    internal Task CancelUpdateAsync(string id,CancellationToken cancellation=default)
        =>runtimeHealth is IAuthorityRuntimeUpdatePreparation idle?idle.CancelUpdateAsync(id,cancellation):throw new IOException("RUNTIME_IDLE_CHANNEL_UNCONFIGURED");
    internal void UseCommitDataLease(Func<string,WindowsFileBoundary.DataLease> provider)=>writer.UseCommitDataLease(provider);
    internal Task RetryRuntimeCleanupAsync(CancellationToken cancellation=default)=>runtimeHealth?.RetryCleanupAsync(cancellation)??throw new IOException("AUTHORITY_RUNTIME_UNCONFIGURED");
    internal async Task<InstallationBinding> CommitAndStartAsync(CancellationToken cancellation=default)
    {var binding=await CommitAsync(cancellation);await StartRuntimeAsync(cancellation);return binding;}
    internal async Task<InstallationBinding> ReconcileAndStartAsync(CancellationToken cancellation=default)
    {var binding=await ReconcileAsync(cancellation);await StartRuntimeAsync(cancellation);return binding;}
    internal async Task<InstallationBinding> ReconcileAsync(CancellationToken cancellation=default)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        try
        {
            gate.RequireQuiescence();if(delegated is not null)ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegated,true);phase="activating";
            var result=await writer.ResumeAsync(selected.Operation,cancellation:cancellation);committed=true;committedBinding=result;phase="awaiting_runtime";return result;
        }
        catch{phase="recovery_required";throw;}
    }
}
