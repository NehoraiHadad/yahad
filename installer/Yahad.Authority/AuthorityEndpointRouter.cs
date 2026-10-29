using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal enum AuthorityEndpointRole {Runtime,Owner}
internal sealed record AuthorityFrame(int Format,string Id,string Method,JsonElement Body);
internal sealed record AuthorityTarget(string Version,string Fingerprint);
internal sealed record OperationConfirmation(int Format,string InstallationId,string RequestId,string Challenge,string Digest,string Scope,string FromVersion,string ToVersion,string Origin,string DataId,string Recovery,long ExpiresUnix,RoutineDelegation? Delegation);
internal sealed record EndpointReply(int Format,string Id,string State,string Phase,string Mode,AuthorityTarget? Target,OperationConfirmation? Confirmation);
internal sealed record RoutedIntent(int Format,string Id,string Action,string Target,CatalogOperation Catalog);

// The native endpoint and in-memory fixtures call this same concrete router.
// Peer identity is supplied by the OS adapter; the bounded frame cannot carry it.
internal sealed class AuthorityEndpointRouter
{
    private readonly InstalledStateFiles files;private readonly InstalledAuthorityAnchor anchor;private readonly string runtimeSid;
    private readonly InstalledIntakeSelection intake;private readonly OwnerApprovalLedger ledger;private readonly Func<CatalogOperation,AuthorityCoordinator> factory;
    private readonly Func<CancellationToken,Task> quiesce;private readonly SemaphoreSlim serial=new(1,1);
    private ProbeOwnershipException? unresolvedProbe;
    private IntakeSelection? selected;private AuthorityCoordinator? active,previous,running;private OperationConfirmation? confirmation;private RoutineDelegation? proposedDelegation;
    private string? requestId;private string phase="idle";private Task? execution;private CancellationTokenSource? operationCancellation;
    internal string Phase=>phase=="healthy"?active?.Phase??"recovery_required":execution is {IsCompleted:false}&&active?.Phase!="awaiting_local_approval"?active?.Phase??phase:phase;
    internal Task? Execution=>execution;
    internal AuthorityEndpointRouter(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string runtimeSid,InstalledIntakeSelection intake,OwnerApprovalLedger ledger,Func<CatalogOperation,AuthorityCoordinator> factory,Func<CancellationToken,Task> quiesce)
    {this.files=files;this.anchor=anchor;this.runtimeSid=runtimeSid;this.intake=intake;this.ledger=ledger;this.factory=factory;this.quiesce=quiesce;files.EnsureDirectory(files.PathOf(".authority"));if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Any(p=>!File.Exists(files.PathOf(".authority/done-"+Path.GetFileNameWithoutExtension(p)[4..]+".json"))))phase="recovery_required";}
    internal async Task<EndpointReply> DispatchAsync(AuthorityEndpointRole role,AuthorityPeer peer,byte[] frame,CancellationToken cancellation=default)
    {
        if(frame.Length is <2 or >32768)throw new IOException("AUTHORITY_FRAME_BOUND");var f=NativeJson.Read<AuthorityFrame>(frame);InstalledActivationResolver.Id(f.Id);
        if(f.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");RequirePeer(role,peer);
        await serial.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if(f.Method=="status"||f.Method=="confirmation")
            {if(f.Body.ValueKind!=JsonValueKind.Object||f.Body.EnumerateObject().Any())throw new IOException("AUTHORITY_EMPTY_BODY_REQUIRED");if(f.Method=="confirmation"&&role!=AuthorityEndpointRole.Owner)throw new UnauthorizedAccessException("OWNER_ENDPOINT_REQUIRED");return Reply(f.Id,"observed",f.Method=="confirmation");}
            if(role==AuthorityEndpointRole.Owner&&f.Method=="answer")return Answer(peer,f);
            if(role!=AuthorityEndpointRole.Runtime)throw new UnauthorizedAccessException("RUNTIME_ENDPOINT_REQUIRED");
            if(f.Method=="cancel")
            {
                var id=NativeJson.Read<CancelRequest>(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(id.RequestId!=requestId)throw new IOException("AUTHORITY_CANCEL_ID");
                operationCancellation?.Cancel();confirmation=null;WriteOnce("cancel-"+id.RequestId,new{Id=id.RequestId});
                if(execution is null||execution.IsCompleted){phase="cancelled";Done();}return Reply(f.Id,"queued");
            }
            if(f.Method!="request")throw new IOException("AUTHORITY_METHOD");
            var request=AuthorityProtocol.Request(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(request.Id!=f.Id)throw new IOException("AUTHORITY_REQUEST_ID");
            if(File.Exists(files.PathOf(".authority/rpc-"+request.Id+".json")))
            {
                var prior=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/rpc-"+request.Id+".json"),32768));
                if(prior.GetProperty("Action").GetString()!=request.Action||request.Action=="set_mode"&&prior.GetProperty("Mode").GetString()!=request.Mode||prior.TryGetProperty("Target",out var target)&&request.Action=="install"&&target.GetString()!=request.TargetDigest)throw new IOException("AUTHORITY_REPLAY_BINDING");
                return Reply(f.Id,request.Id==requestId?"pending":"recorded");
            }
            if(execution is {IsCompleted:false}||confirmation is not null||phase=="recovery_required")return Reply(f.Id,"pending");
            if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Take(1001).Count()>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
            var mode=intake.Mode();
            if(request.Action=="set_mode")
            {
                if(!mode.ModeControlAllowed)throw new UnauthorizedAccessException("AUTHORITY_MODE_LOCKED");requestId=request.Id;
                if(request.Mode=="notify"){WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="notify"});PublishMode(mode with{Mode="notify",DelegationId=null});phase="idle";Done();return Reply(f.Id,"queued");}
                proposedDelegation=intake.DelegationTemplate();var challenge=ledger.DelegationChallenge(proposedDelegation);WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="auto",challenge.Digest});
                confirmation=new(1,anchor.InstallationId,request.Id,challenge.Id,challenge.Digest,"delegate","","",proposedDelegation.Origin,proposedDelegation.DataId,proposedDelegation.Recovery is null?"none":"existing",challenge.ExpiresUnix,proposedDelegation);phase="awaiting_local_approval";return Reply(f.Id,"queued");
            }
            var candidate=intake.Select(request.TargetDigest);
            if(request.Action=="check"&&mode.Mode=="notify"){phase="available";return Reply(f.Id,"observed");}
            selected=candidate;requestId=request.Id;var next=factory(candidate.Catalog);
            WriteOnce("rpc-"+request.Id,new RoutedIntent(1,request.Id,request.Action,candidate.Target,candidate.Catalog));
            ApprovalChallenge? challengeForOperation;
            try{challengeForOperation=next.Submit(peer,JsonSerializer.SerializeToUtf8Bytes(request with{Action="install",TargetDigest=candidate.Catalog.Operation.ReleaseDigest}),mode.Mode=="auto"?mode.DelegationId:null);}catch{phase="recovery_required";throw;}
            previous=running;active=next;
            if(challengeForOperation is { } c)
            {confirmation=new(1,anchor.InstallationId,request.Id,c.Id,c.Digest,c.Scope,candidate.Catalog.Facts.CurrentVersion,candidate.Catalog.Facts.TargetVersion,candidate.Catalog.Facts.Origin,candidate.Catalog.Operation.DataId,candidate.Catalog.Operation.Recovery is null?"none":"existing",c.ExpiresUnix,null);phase="awaiting_local_approval";}
            else StartOperation();return Reply(f.Id,"queued");
        }
        finally{serial.Release();}
    }
    private EndpointReply Answer(AuthorityPeer peer,AuthorityFrame f)
    {
        if(confirmation is null||phase!="awaiting_local_approval")throw new IOException("AUTHORITY_NO_CONFIRMATION");var answer=AuthorityProtocol.Answer(JsonSerializer.SerializeToUtf8Bytes(f.Body));
        if(answer.Challenge!=confirmation.Challenge||answer.OperationDigest!=confirmation.Digest||File.Exists(files.PathOf(".authority/cancel-"+confirmation.RequestId+".json")))throw new IOException("AUTHORITY_CONFIRMATION_CHANGED");
        if(proposedDelegation is not null)
        {
            ledger.Approve(peer,answer,proposedDelegation);
            if(answer.Approve){PublishMode(intake.Mode() with{Mode="auto",DelegationId=proposedDelegation.Id});phase="idle";}else phase="cancelled";
            confirmation=null;proposedDelegation=null;Done();return Reply(f.Id,"queued");
        }
        intake.RequireUnchanged(selected??throw new IOException("AUTHORITY_SELECTION_REQUIRED"));ledger.Approve(peer,answer);
        confirmation=null;if(answer.Approve)StartOperation();else {phase="cancelled";Done();}return Reply(f.Id,"queued");
    }
    private void StartOperation()
    {operationCancellation=new();phase="preparing";execution=Task.Run(()=>RunOperationAsync(operationCancellation.Token));}
    private async Task RunOperationAsync(CancellationToken cancellation)
    {
        try
        {
            intake.RequireUnchanged(selected!);
            if(previous is not null&&previous.Phase=="healthy")await previous.StopRuntimeAsync(cancellation);else await quiesce(cancellation);
            intake.RequireUnchanged(selected!);await active!.CommitAndStartAsync(cancellation);phase=active.Phase;if(phase=="healthy")running=active;WriteOnce("done-"+requestId,new{Id=requestId,Phase=phase});
        }
        catch(ProbeOwnershipException owned){unresolvedProbe=owned;phase="recovery_required";}
        catch(OperationCanceledException){phase="recovery_required";}
        catch{phase="recovery_required";}
    }
    internal async Task StopAsync(CancellationToken cancellation=default)
    {
        operationCancellation?.Cancel();if(execution is { } task)await task.WaitAsync(cancellation);
        var pending=phase=="recovery_required";
        foreach(var owned in new[]{active,previous,running}.Distinct())
        {
            if(owned?.Phase=="healthy")await owned.StopRuntimeAsync(cancellation);
            else if(owned?.Phase=="recovery_required")pending=true;
        }
        phase=pending?"recovery_required":active?.Phase??phase;
    }
    internal async Task RetryOwnedCleanupAsync(CancellationToken cancellation=default)
    {
        if(unresolvedProbe is { } probe){await probe.RetryCleanupAsync(cancellation);unresolvedProbe=null;}
        foreach(var owned in new[]{active,previous,running}.Distinct())if(owned?.RequiresRuntimeCleanup==true)await owned.RetryRuntimeCleanupAsync(cancellation);
        // Cleanup alone does not reconcile a durable interrupted transaction.
        phase="recovery_required";
    }
    private void RequirePeer(AuthorityEndpointRole role,AuthorityPeer peer)
    {if(peer.ProcessId<1||peer.Created<=0||role==AuthorityEndpointRole.Owner&&(peer.Sid!=anchor.OwnerSid||peer.Session<1||!peer.Interactive)||role==AuthorityEndpointRole.Runtime&&(peer.Sid!=runtimeSid||peer.Interactive||peer.Session!=0))throw new UnauthorizedAccessException("AUTHORITY_ENDPOINT_PEER");}
    private EndpointReply Reply(string id,string state,bool owner=false)
    {
        AuthorityTarget? target=null;try{var available=intake.Select();target=new(available.Catalog.Facts.TargetVersion,available.Target);}catch{}
        return new(1,id,state,Phase,intake.Mode().Mode,target,owner?confirmation:null);
    }
    private void WriteOnce<T>(string name,T record)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(record);if(bytes.Length>32768)throw new IOException("AUTHORITY_RECORD_BOUND");using var file=files.Create(files.PathOf(".authority/"+name+".json"));file.Write(bytes);file.Flush(true);}
    private void Done(){if(requestId is not null&&!File.Exists(files.PathOf(".authority/done-"+requestId+".json")))WriteOnce("done-"+requestId,new{Id=requestId,Phase=phase});}
    private void PublishMode(NativeUpdateMode mode)
    {
        var temporary=files.PathOf("intake/mode.json.partial-"+Guid.NewGuid().ToString("N"));using(var file=files.Create(temporary)){file.Write(JsonSerializer.SerializeToUtf8Bytes(mode));file.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+files.PathOf("intake/mode.json"),9))throw new IOException("AUTHORITY_MODE_PUBLICATION");
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll",CharSet=System.Runtime.InteropServices.CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
    private sealed record CancelRequest(string RequestId);
}
