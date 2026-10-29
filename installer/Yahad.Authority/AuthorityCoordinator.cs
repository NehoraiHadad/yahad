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
    ProtectedStateTransactionWriter writer,RuntimeAuthorityGate gate)
{
    private CatalogOperation? selected;private string? requestId;private bool committed;private string? delegated;
    internal string Phase{get;private set;}="idle";
    internal ApprovalChallenge? Submit(AuthorityPeer runtime,byte[] requestFrame,string? delegationId=null)
    {
        if(runtime.Sid!=runtimeSid||runtime.ProcessId<1||runtime.Created<=0||runtime.Interactive)throw new UnauthorizedAccessException("AUTHORITY_RUNTIME_PEER");
        var request=AuthorityProtocol.Request(requestFrame);
        if(request.Action!="install")throw new IOException("AUTHORITY_ACTION_NOT_IMPLEMENTED");
        if(requestId is not null)throw new IOException("AUTHORITY_REQUEST_PENDING");
        selected=catalog.Read(request.TargetDigest!);requestId=request.Id;delegated=delegationId;
        try
        {
            if(delegationId is null){var c=ledger.Challenge(selected.Operation);Phase="awaiting_local_approval";return c;}
            ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegationId);Phase="preparing";return null;
        }
        catch{selected=null;requestId=null;throw;}
    }
    internal async Task<InstallationBinding> CommitAsync(CancellationToken cancellation=default)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        _ =await ledger.AuthenticateAsync(AuthorityProtocol.Digest(selected.Operation),selected.Operation.Kind,cancellation);
        gate.RequireQuiescence();Phase="activating";
        try{var result=await writer.CommitAsync(selected.Operation,cancellation:cancellation);committed=true;Phase="healthy";return result;}
        catch{Phase="recovery_required";throw;}
    }
    internal async Task<InstallationBinding> ReconcileAsync(CancellationToken cancellation=default)
    {
        if(selected is null||committed)throw new IOException("AUTHORITY_NO_PENDING_OPERATION");
        gate.RequireQuiescence();if(delegated is not null)ledger.AuthorizeRoutine(selected.Operation,selected.Facts,delegated,true);
        var result=await writer.ResumeAsync(selected.Operation,cancellation:cancellation);committed=true;Phase="healthy";return result;
    }
}
