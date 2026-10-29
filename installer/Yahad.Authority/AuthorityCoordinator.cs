using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record CatalogOperation(int Format,ProtectedOperation Operation,OperationFacts Facts);
internal sealed class PrivateOperationCatalog(string privateRoot)
{
    // Synthetic-root integration of the real file boundary. A production intake
    // publisher and immutable installed anchor are deliberately unconfigured.
    internal CatalogOperation Read(string digest)
    {
        SafeDirectory.ValidateSession(privateRoot);InstalledActivationResolver.Digest(digest);
        using var file=WindowsFileBoundary.OpenPrivateRead(Path.Combine(privateRoot,digest+".json"));var entry=NativeJson.Read<CatalogOperation>(file.Small(32768));
        if(entry.Format!=1||entry.Operation.ReleaseDigest!=digest)throw new IOException("AUTHORITY_CATALOG_BINDING");return entry;
    }
}
internal sealed class AuthorityCoordinator(string runtimeSid,PrivateOperationCatalog catalog,OwnerApprovalLedger ledger,
    ProtectedStateTransactionWriter writer,RuntimeAuthorityGate gate,RuntimeHealthCoordinator? runtimeHealth=null)
{
    private CatalogOperation? selected;private string? requestId;private bool committed;private string? delegated;
    private string phase="idle";private InstallationBinding? committedBinding;
    internal string Phase=>committed&&runtimeHealth is not null&&runtimeHealth.Phase!="awaiting_runtime"?runtimeHealth.Phase:phase;
    internal ApprovalChallenge? Submit(AuthorityPeer runtime,byte[] requestFrame,string? delegationId=null)
    {
        if(runtime.Sid!=runtimeSid||runtime.ProcessId<1||runtime.Created<=0||runtime.Interactive)throw new UnauthorizedAccessException("AUTHORITY_RUNTIME_PEER");
        var request=AuthorityProtocol.Request(requestFrame);
        if(request.Action!="install")throw new IOException("AUTHORITY_ACTION_NOT_IMPLEMENTED");
        if(requestId is not null)throw new IOException("AUTHORITY_REQUEST_PENDING");
        selected=catalog.Read(request.TargetDigest!);requestId=request.Id;delegated=delegationId;
        try
        {
            if(delegationId is null){var c=ledger.Challenge(selected.Operation);phase="awaiting_local_approval";return c;}
            ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegationId);phase="preparing";return null;
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
    internal Task StartRuntimeAsync(CancellationToken cancellation=default)
    {
        if(!committed||committedBinding is null||selected is null)throw new IOException("AUTHORITY_COMMIT_REQUIRED");
        if(runtimeHealth is null)throw new IOException("AUTHORITY_RUNTIME_UNCONFIGURED");
        return runtimeHealth.StartCommittedAsync(committedBinding,new(selected.Operation.ReleaseDigest),cancellation);
    }
    internal Task StopRuntimeAsync(CancellationToken cancellation=default)=>runtimeHealth?.StopAsync(cancellation)??throw new IOException("AUTHORITY_RUNTIME_UNCONFIGURED");
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
