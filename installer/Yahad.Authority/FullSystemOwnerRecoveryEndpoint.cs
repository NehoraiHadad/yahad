using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Uses the SAME protected operation and coordinator as automatic boot recovery.
// The native Owner can request another safe observation while the web app is
// stopped, without supplying paths, schemas, releases or restore instructions.
internal sealed class FullSystemOwnerRecoveryEndpoint(InstalledStateFiles files,InstalledAuthorityAnchor anchor,OwnerApprovalLedger ledger,
    Func<string?> pending,Func<string,CancellationToken,Task> resume,Func<string> observedPhase,
    Func<string,string,CancellationToken,Task> complete,Func<DateTimeOffset> clock):IOwnerRecoveryEndpoint
{
    public event Action? Completed;
    private readonly SemaphoreSlim serial=new(1,1);private Task? execution;private CancellationTokenSource? stop;
    private OperationConfirmation? confirmation;private RecoveryProposal? proposal;private string phase="recovery_required";private string? error;
    internal Task? Execution=>execution;
    private sealed record RecoveryProposal(int Format,string InstallationId,string OperationId,string AdmissionDigest,string ObservationDigest,string AttemptNonce);
    public async Task<EndpointReply> DispatchAsync(AuthorityPeer owner,AuthorityFrame frame,CancellationToken cancellation)
    {
        if(owner.Sid!=anchor.OwnerSid||owner.ProcessId<1||owner.Created<=0||owner.Session<1||!owner.Interactive)throw new UnauthorizedAccessException("OWNER_RECOVERY_PEER");
        if(frame.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");InstalledActivationResolver.Id(frame.Id);
        await serial.WaitAsync(cancellation);
        try
        {
            if(frame.Method=="recovery")
            {if(frame.Body.ValueKind!=JsonValueKind.Object||frame.Body.EnumerateObject().Any())throw new IOException("AUTHORITY_EMPTY_BODY_REQUIRED");return Reply(frame.Id,"observed");}
            if(frame.Method=="recover")
            {
                if(execution is {IsCompleted:false})return Reply(frame.Id,"pending");
                if(confirmation is not null)
                {
                    if(proposal is null||confirmation.ExpiresUnix<=clock().ToUnixTimeSeconds()||pending()!=proposal.OperationId||ObservationDigest(proposal.OperationId)!=proposal.ObservationDigest)
                    {confirmation=null;proposal=null;phase="recovery_required";}
                    else return Reply(frame.Id,"pending");
                }
                var request=NativeJson.Read<Begin>(JsonSerializer.SerializeToUtf8Bytes(frame.Body));InstalledActivationResolver.Id(request.OperationId);
                if(pending()!=request.OperationId)throw new IOException("OWNER_RECOVERY_OPERATION_UNAVAILABLE");
                var record=FullSystemOperationJournal.Read(files,anchor.InstallationId,request.OperationId);
                proposal=new(1,anchor.InstallationId,request.OperationId,AuthorityProtocol.Digest(record),ObservationDigest(request.OperationId),Guid.NewGuid().ToString("N"));
                var challenge=ledger.SystemChallenge(AuthorityProtocol.Digest(proposal),"reconcile-system");
                confirmation=new(1,anchor.InstallationId,request.OperationId,challenge.Id,challenge.Digest,challenge.Scope,
                    record.Observed.Version,record.TargetVersion,"","","full_reconcile_preserve_current",challenge.ExpiresUnix,null);
                phase="awaiting_local_approval";error=null;return Reply(frame.Id,"queued");
            }
            if(frame.Method!="recovery-answer"||confirmation is null||proposal is null||execution is {IsCompleted:false})throw new IOException("OWNER_RECOVERY_NO_CONFIRMATION");
            var answer=AuthorityProtocol.Answer(JsonSerializer.SerializeToUtf8Bytes(frame.Body));
            if(answer.Challenge!=confirmation.Challenge||answer.OperationDigest!=confirmation.Digest)
                throw new UnauthorizedAccessException("OWNER_RECOVERY_CONFIRMATION_CHANGED");
            if(answer.Approve&&(pending()!=proposal.OperationId||AuthorityProtocol.Digest(FullSystemOperationJournal.Read(files,anchor.InstallationId,proposal.OperationId))!=proposal.AdmissionDigest||ObservationDigest(proposal.OperationId)!=proposal.ObservationDigest))
            {confirmation=null;proposal=null;phase="recovery_required";throw new UnauthorizedAccessException("OWNER_RECOVERY_TARGET_CHANGED");}
            ledger.ApproveSystem(owner,answer,"reconcile-system",AuthorityProtocol.Digest(proposal));var approved=proposal;confirmation=null;proposal=null;
            if(!answer.Approve){phase="recovery_required";return Reply(frame.Id,"denied");}
            // Approval is already durable. The service now owns acceptance;
            // losing this client's response must not cancel or repeat the work.
            _=await ledger.AuthenticateAsync(answer.OperationDigest,"reconcile-system",CancellationToken.None);
            stop=new();phase="recovering";execution=Task.Run(()=>RunAsync(approved.OperationId,stop.Token));return Reply(frame.Id,"queued");
        }
        finally{serial.Release();}
    }
    private async Task RunAsync(string operation,CancellationToken cancellation)
    {
        try
        {
            await resume(operation,cancellation);var result=observedPhase();
            if(result is not("healthy" or "previous_restored"))throw new IOException("OWNER_FULL_RECOVERY_NOT_HEALTHY");
            await complete(operation,result,cancellation);phase="healthy";error=null;Completed?.Invoke();
        }
        catch{phase="recovery_required";error="recovery_failed";}
    }
    private string ObservationDigest(string operation)
    {
        var observations=new List<object>();
        foreach(var name in new[]{"activation.json","bootstrap.json","epoch.json","web.json","pending.json"})
        {
            var path=files.PathOf(name);
            try{observations.Add(new{name,digest=InstalledActivationResolver.Hash(files.Read(path,65536))});}
            catch(FileNotFoundException){observations.Add(new{name,digest="absent"});}
            catch(System.ComponentModel.Win32Exception e)when(e.NativeErrorCode is 2 or 3){observations.Add(new{name,digest="absent"});}
        }
        foreach(var relative in new[]{".authority/updates/"+operation,".authority/bootstrap-handoffs/"+operation})
        {
            var directory=files.PathOf(relative);if(!Directory.Exists(directory))continue;using var pin=files.PinDirectory(directory);
            var paths=Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal).Take(257).ToArray();
            if(paths.Length>256||paths.Any(Directory.Exists))throw new IOException("OWNER_FULL_RECOVERY_EVIDENCE_BOUND");
            foreach(var path in paths)observations.Add(new{name=relative+"/"+Path.GetFileName(path),digest=InstalledActivationResolver.Hash(files.Read(path,8*1024*1024))});
        }
        return AuthorityProtocol.Digest(observations);
    }
    private EndpointReply Reply(string id,string state)
    {
        OwnerRecoveryItem[] operations=[];
        try{if(pending() is {} operation){InstalledActivationResolver.Id(operation);operations=[new(operation,"interrupted")];}}
        catch{error="recovery_evidence_unavailable";}
        var summary=new OwnerRecoverySummary(operations,confirmation?.RequestId,phase,error);
        return new(1,id,state,phase,"unavailable",null,confirmation,LastRequest:JsonSerializer.SerializeToElement(summary));
    }
    public async Task StopAsync(CancellationToken cancellation){stop?.Cancel();if(execution is {} work)await work.WaitAsync(cancellation);}
    private sealed record Begin(string OperationId);
}
