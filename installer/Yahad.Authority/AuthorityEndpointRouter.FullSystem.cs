using System.Text.Json;
using Yahad.Service;

using Yahad.Install;

namespace Yahad.Authority;

internal sealed partial class AuthorityEndpointRouter
{
    private FullSystemModeControl? systemControl;
    private Func<FullSystemInstalledState>? observeSystem;
    private Func<FullSystemDispatchRequest,CancellationToken,Task>? executeSystem;
    private Func<string>? observedSystemPhase;private Func<bool>? systemCandidateStartIssued;
    private FullSystemGrantProposal? systemGrantProposal;private FullSystemInstallProposal? systemInstallProposal;
    private Task? systemExecution;private CancellationTokenSource? systemCancellation;private FullSystemDispatchRequest? systemRunning;
    private string? completedFullOperationId;
    internal string? FullSystemOperationId=>systemRunning?.OperationId??completedFullOperationId;
    internal void ConfigureFullSystem(FullSystemModeControl control,Func<FullSystemInstalledState> observe,Func<FullSystemDispatchRequest,CancellationToken,Task> execute,Func<string> phase,Func<bool> candidateStartIssued)
    {
        if(systemControl is not null||anchor.FullSystemPolicyDigest is null||control.PolicyDigest!=anchor.FullSystemPolicyDigest)throw new IOException("AUTHORITY_FULL_SYSTEM_CONFIGURATION");
        _=control.RequireEnrollment();systemControl=control;observeSystem=observe;executeSystem=execute;observedSystemPhase=phase;systemCandidateStartIssued=candidateStartIssued;
    }
    private string? SystemActivationPhase
    {
        get{var observed=observedSystemPhase?.Invoke();if(observed=="recovery_required")return observed;if(systemExecution is {IsCompleted:false})return observed is null or "idle"?phase:observed;return systemExecution is not null||completedFullOperationId is not null?phase:null;}
    }
    private FullSystemModeControl RequireSystemControl()=>systemControl??throw new IOException("AUTHORITY_FULL_SYSTEM_UNCONFIGURED");
    private EndpointReply CheckSystemScheduledLocked(string id,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();var control=RequireSystemControl();var mode=intake.Mode();
        if(Execution is {IsCompleted:false}||confirmation is not null||ActivationPhase=="recovery_required")return Reply(id,"pending");
        var target=control.Target();if(target is null){phase="idle";return Reply(id,"observed");}
        if(mode.Mode=="notify"){phase="available";return Reply(id,"observed");}
        try{control.RequireAutomatic();}catch(UnauthorizedAccessException){phase="available";return Reply(id,"approval_required");}
        requestId=id;WriteOnce("rpc-"+id,new{Id=id,Action="check",Target=target.Fingerprint,TargetVersion=target.Version,Profile=FullSystemUpdatePolicy.Profile});StartSystem(new(id,target.Fingerprint,null));return Reply(id,"queued");
    }
    private EndpointReply NotifySystemLocked(AuthorityRequest request)
    {
        var mode=intake.Mode();if(!mode.ModeControlAllowed)throw new UnauthorizedAccessException("AUTHORITY_MODE_LOCKED");
        RequireSystemControl().Notify();if(systemRunning?.ManualApproval is null)systemCancellation?.Cancel();
        WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="notify",Profile=FullSystemUpdatePolicy.Profile});
        if(confirmation?.Scope=="delegate-system")
        {WriteOnce("cancel-"+confirmation.RequestId,new{Id=confirmation.RequestId});WriteOnce("done-"+confirmation.RequestId,new{Id=confirmation.RequestId,Phase="cancelled"});confirmation=null;systemGrantProposal=null;}
        if(Execution is not {IsCompleted:false}&&confirmation is null&&phase!="recovery_required")phase="idle";
        WriteOnce("done-"+request.Id,new{Id=request.Id,Phase=phase});return Reply(request.Id,"queued");
    }
    private EndpointReply DispatchSystemLocked(AuthorityRequest request)
    {
        var control=RequireSystemControl();var mode=intake.Mode();
        if(request.Action=="set_mode")
        {
            if(!mode.ModeControlAllowed)throw new UnauthorizedAccessException("AUTHORITY_MODE_LOCKED");
            if(mode.Mode=="auto"&&control.Status().State=="active")
            {WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="auto",Profile=FullSystemUpdatePolicy.Profile});WriteOnce("done-"+request.Id,new{Id=request.Id,Phase=phase});return Reply(request.Id,"queued");}
            requestId=request.Id;systemGrantProposal=control.ProposeGrant();var challenge=ledger.SystemChallenge(AuthorityProtocol.Digest(systemGrantProposal),"delegate-system");
            WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Mode="auto",challenge.Digest,Profile=FullSystemUpdatePolicy.Profile});
            confirmation=new(1,anchor.InstallationId,request.Id,challenge.Id,challenge.Digest,challenge.Scope,"","","", "","none",challenge.ExpiresUnix,null,control.ConsentView(systemGrantProposal));phase="awaiting_local_approval";return Reply(request.Id,"queued");
        }
        var target=control.Target()??throw new IOException("FULL_SYSTEM_NO_TARGET");
        if(request.TargetDigest is {} expected&&expected!=target.Fingerprint)throw new IOException("FULL_SYSTEM_TARGET_CHANGED");
        if(request.Action=="check")return CheckSystemScheduledLocked(request.Id,default);
        requestId=request.Id;systemInstallProposal=null;systemGrantProposal=null;
        if(mode.Mode=="auto")
        {
            try{control.RequireAutomatic();WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Target=target.Fingerprint,TargetVersion=target.Version,Profile=FullSystemUpdatePolicy.Profile});StartSystem(new(request.Id,target.Fingerprint,null));return Reply(request.Id,"queued");}
            catch(UnauthorizedAccessException){/* Exact one-time Owner approval remains available. */}
        }
        systemInstallProposal=control.ProposeInstall(request.Id,target.Fingerprint,observeSystem!());var approval=ledger.SystemChallenge(AuthorityProtocol.Digest(systemInstallProposal),"activate-system");
        WriteOnce("rpc-"+request.Id,new{request.Id,request.Action,Target=target.Fingerprint,TargetVersion=target.Version,approval.Digest,Profile=FullSystemUpdatePolicy.Profile});
        confirmation=new(1,anchor.InstallationId,request.Id,approval.Id,approval.Digest,approval.Scope,systemInstallProposal.FromVersion,systemInstallProposal.ToVersion,"",intake.Head().DataId,"none",approval.ExpiresUnix,null);phase="awaiting_local_approval";return Reply(request.Id,"queued");
    }
    private EndpointReply AnswerSystemLocked(AuthorityPeer peer,OwnerAnswer answer,string replyId)
    {
        var control=RequireSystemControl();
        if(systemGrantProposal is {} grant)
        {control.AnswerGrant(peer,answer,grant);phase=answer.Approve?"idle":"cancelled";confirmation=null;systemGrantProposal=null;Done();return Reply(replyId,"queued");}
        var proposal=systemInstallProposal??throw new IOException("AUTHORITY_SYSTEM_PROPOSAL");if(answer.Approve)control.RequireInstallUnchanged(proposal,observeSystem!());
        ledger.ApproveSystem(peer,answer,"activate-system",AuthorityProtocol.Digest(proposal));confirmation=null;systemInstallProposal=null;
        if(answer.Approve)StartSystem(new(proposal.OperationId,proposal.SourceReceiptDigest,proposal));else{phase="cancelled";Done();}return Reply(replyId,"queued");
    }
    private void StartSystem(FullSystemDispatchRequest request)
    {systemCancellation=new();systemRunning=request;phase="preparing";systemExecution=schedule(()=>RunSystemAsync(request,systemCancellation.Token));}
    private async Task RunSystemAsync(FullSystemDispatchRequest request,CancellationToken cancellation)
    {
        string? error=null;
        try{await executeSystem!(request,cancellation);phase=observedSystemPhase!();}
        catch(OperationCanceledException){phase=ReconciledPhase("cancelled");error=phase=="healthy"?null:"configuration";}
        catch(UnauthorizedAccessException){phase=ReconciledPhase("available");error=phase=="healthy"?null:"configuration";}
        catch{phase=ReconciledPhase("failed");error=phase=="healthy"?null:"health";}
        finally{if(phase!="restarting_updater"&&!File.Exists(files.PathOf(".authority/done-"+request.OperationId+".json")))WriteOnce("done-"+request.OperationId,new{Id=request.OperationId,Phase=phase,Error=error});}
    }
    private string ReconciledPhase(string fallback)
    {var observed=observedSystemPhase?.Invoke();return observed is "healthy" or "previous_restored" or "recovery_required"?observed:fallback;}
    // Replacement Authority calls only after its coordinator reconciles the
    // protected operation. No HTTP/Owner frame can claim a completion outcome.
    internal async Task CompleteFullSystemRequestAsync(string operationId,string outcome,CancellationToken cancellation=default)
    {
        if(outcome is not("healthy" or "previous_restored" or "recovery_required" or "cancelled"))throw new IOException("AUTHORITY_SYSTEM_COMPLETION");
        InstalledActivationResolver.Id(operationId);await serial.WaitAsync(cancellation);
        try
        {
            var intent=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/rpc-"+operationId+".json"),32768));var record=FullSystemOperationJournal.Read(files,anchor.InstallationId,operationId);
            if(intent.GetProperty("Profile").GetString()!=FullSystemUpdatePolicy.Profile||intent.GetProperty("Target").GetString()!=record.SourceReceiptDigest)throw new IOException("AUTHORITY_SYSTEM_COMPLETION_BINDING");
            if(!File.Exists(files.PathOf(".authority/done-"+operationId+".json")))WriteOnce("done-"+operationId,new{Id=operationId,Phase=outcome});phase=outcome;completedFullOperationId=operationId;
        }
        finally{serial.Release();}
    }
    // Only native boot reconciliation calls this AFTER it has stopped and
    // proved the ordinary source healthy and durably recorded cancellation.
    // An unadmitted request cannot be reminted as a full operation or approval.
    internal async Task CompleteUnadmittedFullRequestAsync(string operationId,CancellationToken cancellation=default)
    {
        InstalledActivationResolver.Id(operationId);await serial.WaitAsync(cancellation);
        try
        {
            var intent=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/rpc-"+operationId+".json"),32768));
            var done=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/done-"+operationId+".json"),4096));
            if(intent.GetProperty("Id").GetString()!=operationId||intent.GetProperty("Profile").GetString()!=FullSystemUpdatePolicy.Profile||intent.GetProperty("Action").GetString() is not("check" or "install")||done.GetProperty("Id").GetString()!=operationId||done.GetProperty("Phase").GetString()!="cancelled")throw new IOException("AUTHORITY_UNADMITTED_COMPLETION");
            InstalledActivationResolver.Digest(intent.GetProperty("Target").GetString()??"");
            foreach(var relative in new[]{".authority/system-operations/"+operationId+".json",".authority/system-native/"+operationId+".json",".authority/updates/"+operationId,".authority/bootstrap-handoffs/"+operationId})
            {var path=files.PathOf(relative);if(File.Exists(path)||Directory.Exists(path))throw new IOException("AUTHORITY_UNADMITTED_EFFECTS");}
            var capsulePath=files.PathOf(".authority/bootstrap-helper-active.json");
            if(File.Exists(capsulePath))
            {
                var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(capsulePath,65536));SystemBootstrapHelperApplication.RequireCapsule(capsule);
                if(capsule.OperationId==operationId)throw new IOException("AUTHORITY_UNADMITTED_HELPER_EFFECTS");
                // Historical completed capsules are preserved. The native boot
                // kernel separately gates every unresolved different operation.
            }
            var others=Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Take(1001).ToArray();if(others.Length>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
            if(others.Any(p=>!File.Exists(files.PathOf(".authority/done-"+Path.GetFileNameWithoutExtension(p)[4..]+".json"))))throw new IOException("AUTHORITY_OTHER_UNFINISHED");
            completedFullOperationId=operationId;phase="cancelled";
        }
        finally{serial.Release();}
    }
}
