using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal enum AuthorityEndpointRole {Runtime,Owner}
internal sealed record AuthorityFrame(int Format,string Id,string Method,JsonElement Body);
internal sealed record AuthorityTarget(string Version,string Fingerprint);
internal sealed record OperationConfirmation(int Format,string InstallationId,string RequestId,string Challenge,string Digest,string Scope,string FromVersion,string ToVersion,string Origin,string DataId,string Recovery,long ExpiresUnix,RoutineDelegation? Delegation,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] FullSystemConsentView? SystemConsent=null);
internal sealed record EndpointReply(int Format,string Id,string State,string Phase,string Mode,AuthorityTarget? Target,OperationConfirmation? Confirmation,RoutineDelegationStatus? Delegation=null,NativeUpdateView? View=null,JsonElement? LastRequest=null,JsonElement? LastModeRequest=null,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] FullSystemGrantStatus? SystemGrant=null);
internal sealed record RoutedIntent(int Format,string Id,string Action,string Target,CatalogOperation Catalog);

// The native endpoint and in-memory fixtures call this same concrete router.
// Peer identity is supplied by the OS adapter; the bounded frame cannot carry it.
internal sealed partial class AuthorityEndpointRouter
{
    private readonly InstalledStateFiles files;private readonly InstalledAuthorityAnchor anchor;private readonly string runtimeSid;
    private readonly InstalledIntakeSelection intake;private readonly OwnerApprovalLedger ledger;private readonly Func<CatalogOperation,AuthorityCoordinator> factory;
    private readonly Func<CancellationToken,Task> quiesce;private readonly SemaphoreSlim serial=new(1,1);
    private readonly Func<Func<Task>,Task> schedule;
    private readonly Func<string,CancellationToken,Task<bool>> prepareUpdate;
    private readonly Func<string,CancellationToken,Task> cancelPreparation;
    private readonly Func<CatalogOperation,string,InstalledUpdateTransaction>? transaction;
    private IOwnerRecoveryEndpoint? recoveryEndpoint;
    private Action<DateTimeOffset>? defer;
    private Func<string,CancellationToken,Task>? sourceCheck;
    private Task? sourceExecution;private CancellationTokenSource? sourceCancellation;
    internal Task? SourceExecution=>sourceExecution;
    internal void ConfigureSourceCheck(Func<string,CancellationToken,Task> check)
    {if(sourceCheck is not null)throw new IOException("AUTHORITY_CHECK_CONFIGURED");sourceCheck=check;}
    internal void ConfigureRecovery(IOwnerRecoveryEndpoint endpoint)
    {
        if(recoveryEndpoint is not null)throw new IOException("AUTHORITY_RECOVERY_CONFIGURED");recoveryEndpoint=endpoint;
        endpoint.Completed+=()=>
        {
            serial.Wait();try{active=null;previous=null;running=null;selected=null;confirmation=null;proposedDelegation=null;requestId=null;phase="idle";}finally{serial.Release();}
        };
    }
    internal void ConfigureDeferral(Action<DateTimeOffset> deferral)
    {if(defer is not null)throw new IOException("AUTHORITY_DEFERRAL_CONFIGURED");defer=deferral;}
    private ProbeOwnershipException? unresolvedProbe;
    private IntakeSelection? selected;private AuthorityCoordinator? active,previous,running;private OperationConfirmation? confirmation;private RoutineDelegation? proposedDelegation;
    private string? requestId;private string phase="idle";private Task? execution;private CancellationTokenSource? operationCancellation;
    private bool candidateStartIssued;
    internal bool CandidateStartIssued=>systemExecution is not null?systemCandidateStartIssued?.Invoke()==true:Volatile.Read(ref candidateStartIssued);
    internal string ActivationPhase=>SystemActivationPhase??(phase=="recovery_required"?phase:phase=="backing_up"?phase:phase=="healthy"?active?.Phase??"recovery_required":execution is {IsCompleted:false}&&active?.Phase!="awaiting_local_approval"?active?.Phase??phase:phase);
    internal string Phase=>ActivationPhase!="recovery_required"&&sourceExecution is {IsCompleted:false}&&Execution is not {IsCompleted:false}?"checking":ActivationPhase;
    internal Task? Execution=>systemExecution??execution;
    internal void MarkBootRecoveryRequired()=>phase="recovery_required";
    internal AuthorityEndpointRouter(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string runtimeSid,InstalledIntakeSelection intake,OwnerApprovalLedger ledger,Func<CatalogOperation,AuthorityCoordinator> factory,Func<CancellationToken,Task> quiesce,Func<Func<Task>,Task>? schedule=null,Func<string,CancellationToken,Task<bool>>? prepareUpdate=null,Func<string,CancellationToken,Task>? cancelPreparation=null,Func<CatalogOperation,string,InstalledUpdateTransaction>? transaction=null)
    {this.files=files;this.anchor=anchor;this.runtimeSid=runtimeSid;this.intake=intake;this.ledger=ledger;this.factory=factory;this.quiesce=quiesce;this.schedule=schedule??(work=>Task.Run(work));this.prepareUpdate=prepareUpdate??((_,_)=>Task.FromResult(false));this.cancelPreparation=cancelPreparation??((_,_)=>Task.CompletedTask);this.transaction=transaction;files.EnsureDirectory(files.PathOf(".authority"));if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Any(p=>!File.Exists(files.PathOf(".authority/done-"+Path.GetFileNameWithoutExtension(p)[4..]+".json"))))phase="recovery_required";}
    // Called only by the protected Authority scheduler. No fabricated runtime
    // peer and no IPC method can enter this path.
    internal Task<EndpointReply> CheckScheduledAsync(CancellationToken cancellation)=>CheckScheduledAsync(cancellation,null);
    internal async Task<EndpointReply> CheckScheduledAsync(CancellationToken cancellation,string? explicitId)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            var id=explicitId??Guid.NewGuid().ToString("N");InstalledActivationResolver.Id(id);var mode=intake.Mode();
            if(mode.IsSystem)return CheckSystemScheduledLocked(id,cancellation);
            if(execution is {IsCompleted:false}||confirmation is not null||phase=="recovery_required")return Reply(id,"pending");
            var candidate=intake.Select();
            if(mode.Mode=="notify")
            {phase="available";if(explicitId is not null){WriteOnce("rpc-"+id,new RoutedIntent(1,id,"check",candidate.Target,candidate.Catalog));WriteOnce("done-"+id,new{Id=id,Phase=phase});}return Reply(id,"observed");}
            selected=candidate;requestId=id;proposedDelegation=null;var next=factory(candidate.Catalog);
            WriteOnce("rpc-"+id,new RoutedIntent(1,id,"check",candidate.Target,candidate.Catalog));
            try{next.SubmitRoutine(id,candidate.Catalog.Operation.ReleaseDigest,mode.DelegationId!);}
            catch(RoutineApprovalRequiredException){phase="available";Done();return Reply(id,"approval_required");}
            previous=running;active=next;StartOperation();return Reply(id,"queued");
        }
        finally{serial.Release();}
    }
    internal async Task<EndpointReply> DispatchAsync(AuthorityEndpointRole role,AuthorityPeer peer,byte[] frame,CancellationToken cancellation=default)
    {
        if(frame.Length is <2 or >32768)throw new IOException("AUTHORITY_FRAME_BOUND");var f=NativeJson.Read<AuthorityFrame>(frame);InstalledActivationResolver.Id(f.Id);
        if(f.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");RequirePeer(role,peer);
        await serial.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if(role==AuthorityEndpointRole.Owner&&f.Method is "recovery" or "recover" or "recovery-answer")
                return await (recoveryEndpoint??throw new IOException("AUTHORITY_RECOVERY_UNCONFIGURED")).DispatchAsync(peer,f,cancellation);
            if(f.Method=="status"||f.Method=="confirmation")
            {if(f.Body.ValueKind!=JsonValueKind.Object||f.Body.EnumerateObject().Any())throw new IOException("AUTHORITY_EMPTY_BODY_REQUIRED");if(f.Method=="confirmation"&&role!=AuthorityEndpointRole.Owner)throw new UnauthorizedAccessException("OWNER_ENDPOINT_REQUIRED");return Reply(f.Id,"observed",f.Method=="confirmation");}
            if(role==AuthorityEndpointRole.Owner&&f.Method=="answer")return Answer(peer,f);
            if(role!=AuthorityEndpointRole.Runtime)throw new UnauthorizedAccessException("RUNTIME_ENDPOINT_REQUIRED");
            if(f.Method=="defer")
            {
                var requested=NativeJson.Read<NativeDeferral>(JsonSerializer.SerializeToUtf8Bytes(f.Body));
                (defer??throw new IOException("AUTHORITY_DEFERRAL_UNCONFIGURED"))(DateTimeOffset.FromUnixTimeSeconds(requested.UntilUnix));
                return Reply(f.Id,"queued");
            }
            if(f.Method=="cancel")
            {
                var id=NativeJson.Read<CancelRequest>(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(id.RequestId!=requestId)throw new IOException("AUTHORITY_CANCEL_ID");
                operationCancellation?.Cancel();systemCancellation?.Cancel();confirmation=null;proposedDelegation=null;systemGrantProposal=null;systemInstallProposal=null;WriteOnce("cancel-"+id.RequestId,new{Id=id.RequestId});
                if(Execution is null||Execution.IsCompleted){phase="cancelled";Done();}return Reply(f.Id,"queued");
            }
            if(f.Method!="request")throw new IOException("AUTHORITY_METHOD");
            var request=AuthorityProtocol.Request(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(request.Id!=f.Id)throw new IOException("AUTHORITY_REQUEST_ID");
            if(File.Exists(files.PathOf(".authority/check-request-"+request.Id+".json")))
            {if(request.Action!="check")throw new IOException("AUTHORITY_REPLAY_BINDING");return Reply(f.Id,sourceExecution is {IsCompleted:false}?"pending":"recorded");}
            if(File.Exists(files.PathOf(".authority/rpc-"+request.Id+".json")))
            {
                var prior=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/rpc-"+request.Id+".json"),32768));
                if(prior.GetProperty("Action").GetString()!=request.Action||request.Action=="set_mode"&&prior.GetProperty("Mode").GetString()!=request.Mode||prior.TryGetProperty("Target",out var target)&&request.Action=="install"&&target.GetString()!=request.TargetDigest)throw new IOException("AUTHORITY_REPLAY_BINDING");
                return Reply(f.Id,request.Id==requestId?"pending":"recorded");
            }
            var mode=intake.Mode();
            if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Take(1001).Count()>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
            if(mode.IsSystem&&request.Action=="set_mode"&&request.Mode=="notify")return NotifySystemLocked(request);
            if(!mode.IsSystem&&request.Action=="set_mode"&&request.Mode=="notify")
            {
                if(!mode.ModeControlAllowed)throw new UnauthorizedAccessException("AUTHORITY_MODE_LOCKED");
                // Contraction remains available during pending approval/work.
                // Revoke first: a crash cannot restore an existing capability.
                // A busy ledger must not leave an accepted unfinished RPC.
                // Revocation itself is idempotent and can only reduce scope.
                if(execution is {IsCompleted:false}&&active?.IsRoutine==true)operationCancellation?.Cancel();
                if(mode.DelegationId is { } delegated)ledger.DisableRoutine(delegated);
                WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="notify"});
                PublishMode(mode with{Mode="notify",DelegationId=null});
                if(confirmation?.Scope=="delegate")
                {
                    WriteOnce("cancel-"+confirmation.RequestId,new{Id=confirmation.RequestId});
                    WriteOnce("done-"+confirmation.RequestId,new{Id=confirmation.RequestId,Phase="cancelled"});
                    confirmation=null;proposedDelegation=null;
                }
                if(execution is not {IsCompleted:false}&&confirmation is null&&phase!="recovery_required")phase="idle";
                WriteOnce("done-"+request.Id,new{Id=request.Id,Phase=phase});return Reply(f.Id,"queued");
            }
            if(Execution is {IsCompleted:false}||sourceExecution is {IsCompleted:false}||confirmation is not null||ActivationPhase=="recovery_required")return Reply(f.Id,"pending");
            if(mode.IsSystem&&(request.Action!="check"||sourceCheck is null))return DispatchSystemLocked(request);
            if(request.Action=="check"&&sourceCheck is not null)
            {
                if(Directory.EnumerateFiles(files.PathOf(".authority"),"check-request-*.json").Take(1001).Count()>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
                WriteOnce("check-request-"+request.Id,new{request.Id,request.Action});sourceCancellation=new();
                sourceExecution=Task.Run(()=>RunSourceCheckAsync(request.Id,sourceCancellation.Token));return Reply(f.Id,"queued");
            }
            if(request.Action=="set_mode")
            {
                if(!mode.ModeControlAllowed)throw new UnauthorizedAccessException("AUTHORITY_MODE_LOCKED");requestId=request.Id;
                proposedDelegation=intake.DelegationTemplate();var challenge=ledger.DelegationChallenge(proposedDelegation);WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="auto",challenge.Digest});
                confirmation=new(1,anchor.InstallationId,request.Id,challenge.Id,challenge.Digest,"delegate","","",proposedDelegation.Origin,proposedDelegation.DataId,proposedDelegation.Recovery is null?"none":"existing",challenge.ExpiresUnix,proposedDelegation);phase="awaiting_local_approval";return Reply(f.Id,"queued");
            }
            var candidate=intake.Select(request.TargetDigest);
            if(request.Action=="check"&&mode.Mode=="notify")
            {requestId=request.Id;WriteOnce("rpc-"+request.Id,new RoutedIntent(1,request.Id,request.Action,candidate.Target,candidate.Catalog));phase="available";Done();return Reply(f.Id,"observed");}
            proposedDelegation=null;selected=candidate;requestId=request.Id;var next=factory(candidate.Catalog);
            WriteOnce("rpc-"+request.Id,new RoutedIntent(1,request.Id,request.Action,candidate.Target,candidate.Catalog));
            ApprovalChallenge? challengeForOperation;
            try{challengeForOperation=next.Submit(peer,JsonSerializer.SerializeToUtf8Bytes(request with{Action="install",TargetDigest=candidate.Catalog.Operation.ReleaseDigest}),mode.Mode=="auto"?mode.DelegationId:null);}
            catch(RoutineApprovalRequiredException)
            {
                // No transaction or runtime stop has started. Owner renewal or
                // an exact manual approval remains possible; this is not an
                // interrupted data transaction requiring recovery.
                if(request.Action=="install")challengeForOperation=next.Submit(peer,JsonSerializer.SerializeToUtf8Bytes(request with{TargetDigest=candidate.Catalog.Operation.ReleaseDigest}));
                else{phase="available";Done();return Reply(f.Id,"approval_required");}
            }
            catch{phase="recovery_required";throw;}
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
        if(systemGrantProposal is not null||systemInstallProposal is not null)return AnswerSystemLocked(peer,answer,f.Id);
        if(proposedDelegation is not null)
        {
            ledger.Approve(peer,answer,proposedDelegation);
            if(answer.Approve)
            {
                var mode=intake.Mode();if(mode.DelegationId is { } prior&&prior!=proposedDelegation.Id)ledger.DisableRoutine(prior);
                PublishMode(mode with{Mode="auto",DelegationId=proposedDelegation.Id});phase="idle";
            }
            else phase="cancelled";
            confirmation=null;proposedDelegation=null;Done();return Reply(f.Id,"queued");
        }
        intake.RequireUnchanged(selected??throw new IOException("AUTHORITY_SELECTION_REQUIRED"));ledger.Approve(peer,answer);
        confirmation=null;if(answer.Approve)StartOperation();else {phase="cancelled";Done();}return Reply(f.Id,"queued");
    }
    private void StartOperation()
    {operationCancellation=new();candidateStartIssued=false;phase="preparing";execution=schedule(()=>RunOperationAsync(operationCancellation.Token));}
    private async Task RunSourceCheckAsync(string id,CancellationToken cancellation)
    {
        try{await sourceCheck!(id,cancellation);}
        catch(OperationCanceledException){await CompleteSourceCheckAsync(id,"cancelled");}
        catch{await CompleteSourceCheckAsync(id,"network");}
    }
    internal async Task CompleteSourceCheckAsync(string id,string? error)
    {
        InstalledActivationResolver.Id(id);await serial.WaitAsync();
        try
        {
            // A dispatched activation owns its own durable completion/recovery.
            if(File.Exists(files.PathOf(".authority/rpc-"+id+".json")))return;
            WriteOnce("rpc-"+id,new{Id=id,Action="check"});
            if(phase!="recovery_required")
            {
                if(error is null){try{if(intake.Mode().IsSystem){phase=RequireSystemControl().Target() is null?"idle":"available";}else{_=intake.Select();phase="available";}}catch{phase=running?.Phase=="healthy"?"healthy":"idle";}}
                else phase=error=="cancelled"?"cancelled":"failed";
            }
            WriteOnce("done-"+id,new{Id=id,Phase=phase,Error=error is "network"?"network":error is null?null:"internal"});
        }
        finally{serial.Release();}
    }
    private async Task RunOperationAsync(CancellationToken cancellation)
    {
        var disrupted=false;var preparationAttempted=false;var prepared=false;InstalledUpdateTransaction? update=null;
        try
        {
            intake.RequireUnchanged(selected!);
            await active!.RequireApprovalAsync(cancellation);cancellation.ThrowIfCancellationRequested();
            // Keep the same manually approved operation while the parent closes
            // clean browser windows. Waiting cannot mint another approval or
            // renew its expiry. Automatic checks can retry through the scheduler.
            while(true)
            {
                preparationAttempted=true;
                prepared=previous is not null&&previous.Phase=="healthy"?await previous.PrepareUpdateAsync(requestId!,cancellation):await prepareUpdate(requestId!,cancellation);
                if(!prepared)
                {
                    if(active.IsRoutine){phase="available";Done();return;}
                    phase="preparing";await Task.Delay(TimeSpan.FromSeconds(5),cancellation);
                    intake.RequireUnchanged(selected!);await active.RequireApprovalAsync(cancellation);continue;
                }
                // Revocation/expiry can race idle admission. Revalidate before stop.
                await active.RequireApprovalAsync(cancellation);intake.RequireUnchanged(selected!);
                break;
            }
            await active.ReserveApprovalAsync(cancellation);
            update=transaction?.Invoke(selected!.Catalog,requestId!);
            disrupted=true;
            if(previous is not null&&previous.Phase=="healthy")await previous.StopRuntimeAsync(cancellation);else await quiesce(cancellation);
            if(update is not null){phase="backing_up";await update.BackupAfterQuiescenceAsync(cancellation);active.UseCommitDataLease(update.DuplicateLease);}
            intake.RequireUnchanged(selected!);update?.ActivationIntent();await active!.CommitAsync(cancellation);
            update?.CandidateStartIntent();candidateStartIssued=true;await active.StartRuntimeAsync(cancellation);phase=active.Phase;
            // Completion is visible only after observed health. Publish RPC Done
            // before the terminal journal: an interrupted nonterminal journal
            // independently gates boot even when its RPC already has Done.
            UpdateCompletionProtocol.Complete(()=>WriteOnce("done-"+requestId,new{Id=requestId,Phase=phase}),
                ()=>{if(phase=="healthy")update?.Healthy();},()=>{if(phase=="healthy")running=active;});
        }
        catch(ProbeOwnershipException owned){unresolvedProbe=owned;phase="recovery_required";}
        catch(OperationCanceledException){phase=disrupted?"recovery_required":"cancelled";if(!disrupted)Done();}
        catch(UnauthorizedAccessException)when(!disrupted){phase="cancelled";Done();}
        catch{phase="recovery_required";}
        finally
        {
            if(update is not null&&phase!="healthy"){try{update.Failure();}catch{phase="recovery_required";}}
            if(preparationAttempted&&!disrupted)
            {try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));if(previous is not null)await previous.CancelUpdateAsync(requestId!,cleanup.Token);else await cancelPreparation(requestId!,cleanup.Token);}catch{phase="recovery_required";}}
        }
    }
    internal async Task StopAsync(CancellationToken cancellation=default)
    {
        sourceCancellation?.Cancel();if(sourceExecution is{} source)await source.WaitAsync(cancellation);
        if(recoveryEndpoint is not null)await recoveryEndpoint.StopAsync(cancellation);
        operationCancellation?.Cancel();if(execution is { } task)await task.WaitAsync(cancellation);
        systemCancellation?.Cancel();if(systemExecution is { } system)await system.WaitAsync(cancellation);
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
        var mode=intake.Mode();AuthorityTarget? target=null;try{if(mode.IsSystem)target=RequireSystemControl().Target();else{var available=intake.Select();target=new(available.Catalog.Facts.TargetVersion,available.Target);}}catch{}
        var delegation=!mode.IsSystem&&mode.DelegationId is { } delegated?ledger.RoutineStatus(delegated):null;
        NativeUpdateView? view=null;try{view=NativeUpdateEnrollmentReader.View(files,anchor,intake,()=>DateTimeOffset.UtcNow);}catch{}
        FullSystemGrantStatus? systemStatus=null;if(mode.IsSystem)try{systemStatus=RequireSystemControl().Status();}catch{systemStatus=new("unavailable",FullSystemUpdatePolicy.Profile,true,FullSystemUpdatePolicy.RequiredScopes);}
        return new(1,id,state,Phase,mode.Mode,target,owner?confirmation:null,delegation,view,NativeRequestProjection.Read(files,false),NativeRequestProjection.Read(files,true),systemStatus);
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
    private sealed record NativeDeferral(long UntilUnix);
}
