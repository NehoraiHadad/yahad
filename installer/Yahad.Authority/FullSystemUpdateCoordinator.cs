using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal enum FullSystemCompletion {Healthy,HandedToReplacementAuthority,RestoredPrevious,RecoveryRequired}

// Trusted native composition supplies this session ONLY after acquiring actual
// idle acknowledgment, stopped process/job proof and the exclusive data+update
// leases. No endpoint request carries a session, schema observation or path.
internal interface IFullSystemExecutionSession:IAsyncDisposable
{
    Task<FullSystemInstalledState> ObserveSourceAsync(CancellationToken cancellation);
    Task PrepareBootstrapAndBackupAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task<FullSystemCompletion> ReplaceBootstrapAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task PrepareCandidateDataAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task CommitAndStartProbationAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task ReleaseEffectsAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    // Reconcile observes underlying bootstrap/data journals and the current
    // native generation. Once an effects intent exists it must preserve the new
    // data, even if its acknowledgement or the old process was lost.
    Task<FullSystemCompletion> ReconcileAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task<FullSystemCompletion> RestoreUnadmittedSourceAsync(CancellationToken cancellation);
    bool CandidateStartWasRecorded{get;}
    bool EffectsMayHaveBeenReleased{get;}
    void RetainUnresolvedOwnership();
}
internal interface IFullSystemExecutionKernel
{
    // Null means actual busy refusal before any stop or exclusive admission.
    Task<IFullSystemExecutionSession?> TryAcquireIdleAsync(string operationId,CancellationToken cancellation);
    Task<IFullSystemExecutionSession> ReacquireForRecoveryAsync(VerifiedSystemOperation operation,CancellationToken cancellation);
    Task RequireNoOtherUnfinishedAsync(string operationId,CancellationToken cancellation);
}

// The existing protected router/scheduler calls this single execution path for
// full enrollments. Source transport and scheduling are retained. A successful helper handoff ends this Authority's work; the new
// Authority resumes from the SAME protected admission and native journals.
internal sealed class FullSystemUpdateCoordinator(FullSystemUpdatePolicy policy,IFullSystemExecutionKernel kernel)
{
    private readonly SemaphoreSlim serial=new(1,1);
    private string phase="idle";private bool candidateStartIssued;
    private string? currentOperation;
    private IFullSystemExecutionSession? unresolved;private string? unresolvedOperation;
    internal string Phase=>Volatile.Read(ref phase);
    internal string? CurrentOperationId=>Volatile.Read(ref currentOperation);
    internal bool CandidateStartIssued=>Volatile.Read(ref candidateStartIssued);
    internal async Task ExecuteAsync(FullSystemDispatchRequest request,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(request.OperationId);InstalledActivationResolver.Digest(request.SourceReceiptDigest);
        await serial.WaitAsync(cancellation);IFullSystemExecutionSession? session=null;VerifiedSystemOperation? operation=null;
        try
        {
            if(unresolved is not null)throw new IOException("FULL_SYSTEM_UNRESOLVED_OWNERSHIP");
            currentOperation=request.OperationId;
            candidateStartIssued=false;phase="preparing";
            await kernel.RequireNoOtherUnfinishedAsync(request.OperationId,cancellation);
            while(session is null)
            {
                RequireAdmission(request,cancellation);
                session=await kernel.TryAcquireIdleAsync(request.OperationId,cancellation);
                if(session is null){phase="waiting_for_idle";await Task.Delay(TimeSpan.FromSeconds(5),cancellation);}
            }
            var observed=await session.ObserveSourceAsync(cancellation);
            if(request.ManualApproval is {} manual)
            {
                if(manual.OperationId!=request.OperationId||manual.SourceReceiptDigest!=request.SourceReceiptDigest||
                    observed!=new FullSystemInstalledState(manual.FromVersion,manual.BootstrapEpoch,manual.ActivationDigest,manual.SourceSchema,manual.BootstrapVersion))
                    throw new UnauthorizedAccessException("FULL_SYSTEM_MANUAL_SOURCE_CHANGED");
                operation=policy.AuthorizeManual(manual);
            }
            else operation=policy.Authorize(request.OperationId,request.SourceReceiptDigest,observed);
            phase="backing_up";await session.PrepareBootstrapAndBackupAsync(operation,cancellation);
            policy.RequireCurrent(operation);phase="updating_components";
            var outcome=await session.ReplaceBootstrapAsync(operation,cancellation);candidateStartIssued=session.CandidateStartWasRecorded;
            if(outcome==FullSystemCompletion.HandedToReplacementAuthority){phase="restarting_updater";return;}
            if(outcome!=FullSystemCompletion.Healthy)throw new IOException("FULL_SYSTEM_BOOTSTRAP_NOT_HEALTHY");
            await CompleteCandidateAsync(session,operation,cancellation);
        }
        catch
        {
            if(session is not null)
            {
                candidateStartIssued|=session.CandidateStartWasRecorded;
                if(operation is not null)
                {
                    using var recovery=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    try
                    {
                        var outcome=await session.ReconcileAsync(operation,recovery.Token);
                        phase=outcome==FullSystemCompletion.RestoredPrevious?"previous_restored":outcome==FullSystemCompletion.Healthy?"healthy":"recovery_required";
                        if(phase=="recovery_required")Retain(session,request.OperationId);
                    }
                    catch{phase="recovery_required";Retain(session,request.OperationId);}
                }
                else
                {
                    using var restore=new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    try{phase=await session.RestoreUnadmittedSourceAsync(restore.Token)==FullSystemCompletion.RestoredPrevious?"previous_restored":"recovery_required";}
                    catch{phase="recovery_required";}
                    if(phase=="recovery_required")Retain(session,request.OperationId);
                }
            }
            else phase=unresolved is null?"available":"recovery_required";
            throw;
        }
        finally{try{if(session is not null)await DisposeOwnedAsync(session,request.OperationId);}finally{serial.Release();}}
    }
    private void RequireAdmission(FullSystemDispatchRequest request,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();_=policy.Enrollment;
        if(request.ManualApproval is null)_=policy.CurrentGrant();
    }
    private async Task CompleteCandidateAsync(IFullSystemExecutionSession session,VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        policy.RequireCurrent(operation);phase="preparing_data";await session.PrepareCandidateDataAsync(operation,cancellation);
        policy.RequireCurrent(operation);phase="starting_runtime";await session.CommitAndStartProbationAsync(operation,cancellation);
        candidateStartIssued|=session.CandidateStartWasRecorded;
        // The session records durable effects intent BEFORE sending its one
        // bound release command. A lost ACK is never an invitation to retry it.
        policy.RequireCurrent(operation);phase="releasing_effects";await session.ReleaseEffectsAsync(operation,cancellation);phase="healthy";
    }
    internal async Task ResumeAsync(string operationId,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation);IFullSystemExecutionSession? session=null;
        try
        {
            if(unresolved is not null&&unresolvedOperation!=operationId)throw new IOException("FULL_SYSTEM_UNRESOLVED_OPERATION_BINDING");
            currentOperation=operationId;
            phase="recovering";var operation=policy.RemintBootstrapRecovery(operationId);
            await kernel.RequireNoOtherUnfinishedAsync(operationId,cancellation);
            session=unresolved??await kernel.ReacquireForRecoveryAsync(operation,cancellation);candidateStartIssued=session.CandidateStartWasRecorded;
            // Never infer that a native action happened from the last intent.
            // The concrete recovery adapter observes publication, jobs, leases,
            // SQLite and effects evidence before choosing forward repair.
            var outcome=await session.ReconcileAsync(operation,cancellation);
            phase=outcome switch{FullSystemCompletion.Healthy=>"healthy",FullSystemCompletion.RestoredPrevious=>"previous_restored",FullSystemCompletion.HandedToReplacementAuthority=>"restarting_updater",_=>"recovery_required"};
            if(outcome==FullSystemCompletion.RecoveryRequired)Retain(session,operationId);
            else if(ReferenceEquals(unresolved,session)){unresolved=null;unresolvedOperation=null;}
        }
        catch{phase="recovery_required";if(session is not null)Retain(session,operationId);throw;}
        finally{try{if(session is not null)await DisposeOwnedAsync(session,operationId);}finally{serial.Release();}}
    }
    private void Retain(IFullSystemExecutionSession session,string operationId)
    {
        if(unresolved is not null&&!ReferenceEquals(unresolved,session))throw new IOException("FULL_SYSTEM_OWNERSHIP_ALREADY_RETAINED");
        if(unresolved is null){unresolved=session;unresolvedOperation=operationId;session.RetainUnresolvedOwnership();}
    }
    private async Task DisposeOwnedAsync(IFullSystemExecutionSession session,string operationId)
    {
        try{await session.DisposeAsync();}
        catch{phase="recovery_required";Retain(session,operationId);throw;}
    }
}
