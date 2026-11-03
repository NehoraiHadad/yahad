using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record RuntimeStartIntent(InstallationBinding Binding,string ReleaseDigest,string Nonce,string Purpose="admit");

// This ownership adapter is deliberately not a native implementation. Its factory
// must be side-effect-free: the lifecycle retains the session BEFORE starting it.
// All handles created by StartWaitingAsync, including failed starts, stay owned by
// this session. RequireQuiescence observes the retained host AND entire owned job.
internal interface IRetainedScmRuntimeSession:IDisposable
{
    IRuntimeGateKernel Kernel { get; }
    Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken cancellation);
    void RequireAuthenticatedCurrent(RuntimeAdmissionBinding admission);
    Task ReleaseLaunchAsync(RuntimeAdmissionBinding admission,CancellationToken cancellation);
    Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken cancellation);
    Task WaitForExitAsync(CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
    void RequireQuiescence();
}

internal interface IRetainedScmUpdatePreparation
{
    Task<bool> PrepareUpdateAsync(RuntimeAdmissionBinding admission,string id,CancellationToken cancellation);
    Task CancelUpdateAsync(RuntimeAdmissionBinding admission,string id,CancellationToken cancellation);
}

internal interface IRetainedScmRecoveryObservation
{
    // Duplicate the real admitted handles while the waiting host is still alive;
    // transfer that ownership after the observed stop, rather than inventing a job.
    void RetainRecoveryObservation();
    RetainedRuntimeQuiescence AcquireRecoveryQuiescence();
}

internal sealed class WindowsScmRuntimeLifecycle:IAuthorityProbationLifecycle,IAuthorityRuntimeUpdatePreparation
{
    private readonly Func<IRetainedScmRuntimeSession> create;private readonly Func<string> nonce;
    private readonly TimeSpan startup,cleanup;private readonly SemaphoreSlim serial=new(1,1);
    private IRetainedScmRuntimeSession? owned;private RuntimeAuthorityGate? gate;private RuntimeAdmissionBinding? admission;
    private bool started,retainStopped;private string phase="awaiting_runtime";
    private ProbationRuntimeBinding? probation;private bool effectsIssued;private string? unknownEffectsIdleFence;
    public string Phase=>Volatile.Read(ref phase);
    public bool CleanupReconciliationRequired=>Phase=="recovery_required";
    internal bool OwnsRuntimeResources=>owned is not null;
    internal RuntimeAuthorityGate RetainedGate=>gate??throw new IOException("RUNTIME_GATE_UNAVAILABLE");
    internal IRetainedScmRuntimeSession RetainedSession=>owned??throw new IOException("RUNTIME_SESSION_UNAVAILABLE");
    internal WindowsScmRuntimeLifecycle(Func<IRetainedScmRuntimeSession> create,TimeSpan? startup=null,TimeSpan? cleanup=null,Func<string>? nonce=null)
    {
        this.create=create;this.nonce=nonce??(()=>Guid.NewGuid().ToString("N"));
        this.startup=startup??TimeSpan.FromMinutes(5);this.cleanup=cleanup??TimeSpan.FromSeconds(30);
        if(this.startup<=TimeSpan.Zero||this.startup>TimeSpan.FromMinutes(5)||this.cleanup<=TimeSpan.Zero||this.cleanup>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException();
    }
    public Task StartCommittedAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default)
        =>StartCoreAsync(binding,expected,null,cancellation);
    public Task StartProbationAsync(ProbationRuntimeBinding binding,CancellationToken cancellation=default)
    {binding.Validate();return StartCoreAsync(binding.Binding,new(binding.ReleaseDigest),binding,cancellation);}
    private async Task StartCoreAsync(InstallationBinding binding,AppRelease expected,ProbationRuntimeBinding? probationBinding,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(started)throw new IOException("RUNTIME_START_ALREADY_ISSUED");started=true;
            probation=probationBinding;var intent=new RuntimeStartIntent(binding,expected.Id,probation?.Nonce??nonce(),probation is null?"admit":"probation");
            // Validate before any native operation or session creation.
            new RuntimeAdmissionBinding(binding,intent.ReleaseDigest,intent.Nonce,1,1).Validate();
            phase="starting_runtime";
            try
            {
                owned=create();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(startup);
                admission=await owned.StartWaitingAsync(intent,deadline.Token);admission.Validate();
                if(admission.Binding!=binding||admission.ReleaseDigest!=intent.ReleaseDigest||admission.Nonce!=intent.Nonce)throw new UnauthorizedAccessException("RUNTIME_ADMISSION_BINDING");
                owned.RequireAuthenticatedCurrent(admission);
                gate=new(owned.Kernel);gate.Admit(Generation(admission));
                RequireCurrent();await owned.ReleaseLaunchAsync(admission,deadline.Token);
                var ready=await owned.WaitReadyAsync(deadline.Token);deadline.Token.ThrowIfCancellationRequested();
                if(ready!=admission)throw new UnauthorizedAccessException("RUNTIME_READY_BINDING");
                RequireCurrent();phase="healthy";_ =ObserveExitAsync(owned);
            }
            catch
            {
                phase="quiescing_runtime";var empty=await CleanAsync(CancellationToken.None);
                phase=empty?(cancellation.IsCancellationRequested?"cancelled":"failed"):"recovery_required";throw;
            }
        }
        finally{serial.Release();}
    }
    public async Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(probation!=binding||effectsIssued||phase!="healthy"||owned is not IProbationEffectsRuntime runtime)throw new IOException("PROBATION_EFFECTS_STATE");
            RequireCurrent();effectsIssued=true;phase="releasing_effects";
            try{await runtime.ReleaseEffectsAsync(binding,requestId,cancellation);RequireCurrent();phase="healthy";}
            catch{unknownEffectsIdleFence=null;phase="effects_outcome_unknown";throw;}
        }
        finally{serial.Release();}
    }
    // Reboot recovery has no old application job to adopt. Create and authenticate
    // an actual SCM waiting host, assign it, then stop without ever emitting an
    // admission/launch message. No payload resolver or application is executed.
    internal async Task PrepareOfflineRecoveryAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(started)throw new IOException("RUNTIME_START_ALREADY_ISSUED");started=true;
            var intent=new RuntimeStartIntent(binding,expected.Id,nonce());
            new RuntimeAdmissionBinding(binding,intent.ReleaseDigest,intent.Nonce,1,1).Validate();phase="preparing_offline_recovery";
            try
            {
                owned=create();using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(startup);
                admission=await owned.StartWaitingAsync(intent,deadline.Token);admission.Validate();
                if(admission.Binding!=binding||admission.ReleaseDigest!=intent.ReleaseDigest||admission.Nonce!=intent.Nonce)throw new UnauthorizedAccessException("RUNTIME_ADMISSION_BINDING");
                owned.RequireAuthenticatedCurrent(admission);gate=new(owned.Kernel);gate.Admit(Generation(admission));RequireCurrent();
                (owned as IRetainedScmRecoveryObservation??throw new IOException("RUNTIME_RECOVERY_OWNER_UNCONFIGURED")).RetainRecoveryObservation();
                retainStopped=true;phase="quiescing_runtime";
                if(!await CleanAsync(deadline.Token))throw new IOException("RUNTIME_CLEANUP_UNCONFIRMED");
                deadline.Token.ThrowIfCancellationRequested();phase="offline_recovery_ready";
            }
            catch
            {
                phase="quiescing_runtime";var empty=await CleanAsync(CancellationToken.None);
                phase=empty?"failed":"recovery_required";throw;
            }
        }
        finally{serial.Release();}
    }
    internal RetainedRuntimeQuiescence AcquireRecoveryQuiescence()
    {
        var current=Phase;
        if(current is not("healthy" or "effects_outcome_unknown" or "offline_recovery_ready"))throw new IOException("RUNTIME_RECOVERY_OBSERVATION_STATE");
        if(current!="offline_recovery_ready")RequireCurrent();
        return (owned as IRetainedScmRecoveryObservation??throw new IOException("RUNTIME_RECOVERY_OWNER_UNCONFIGURED")).AcquireRecoveryQuiescence();
    }
    private static RuntimeGeneration Generation(RuntimeAdmissionBinding value)=>new(value.Binding.InstallationId,value.ReleaseDigest,value.Nonce,value.ProcessId,value.Created);
    private async Task ObserveExitAsync(IRetainedScmRuntimeSession session)
    {
        try{await session.WaitForExitAsync(CancellationToken.None);}
        catch{ /* Lost observation cannot preserve a healthy verdict. */ }
        // Serialize against the release callback's unknown-outcome write. A
        // host that exits during release cannot have its recovery verdict
        // overwritten by the later lost-ACK catch. Retain this exact owner;
        // the durable effects intent remains conservative throughout cleanup.
        await serial.WaitAsync();
        try
        {
            if(ReferenceEquals(session,owned)&&Phase is "healthy" or "releasing_effects" or "effects_outcome_unknown")
            {unknownEffectsIdleFence=null;phase="recovery_required";}
        }
        finally{serial.Release();}
    }
    private void RequireCurrent()
    {owned!.RequireAuthenticatedCurrent(admission!);gate!.RequireLaunch(Generation(admission!));}
    internal RuntimeAdmissionBinding? ObserveInstallerReadiness(InstallationBinding expected,AppRelease release)
    {
        serial.Wait();
        try
        {
            if(phase!="healthy")return null;
            RequireCurrent();
            if(admission!.Binding!=expected||admission.ReleaseDigest!=release.Id)throw new IOException("INSTALL_READINESS_BINDING");
            return admission;
        }
        finally{serial.Release();}
    }
    public async Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(id);await serial.WaitAsync(cancellation);
        try{if(phase is not("healthy" or "effects_outcome_unknown"))throw new IOException("RUNTIME_NOT_HEALTHY");unknownEffectsIdleFence=null;RequireCurrent();
            var result=await (owned as IRetainedScmUpdatePreparation??throw new IOException("RUNTIME_IDLE_CHANNEL_UNCONFIGURED")).PrepareUpdateAsync(admission!,id,cancellation);
            RequireCurrent();
            // An idle ACK contracts the SAME admitted process into a fence. It
            // says nothing about whether provider/HTTP effects were released.
            if(phase=="effects_outcome_unknown"&&result)unknownEffectsIdleFence=id;return result;}
        finally{serial.Release();}
    }
    public async Task CancelUpdateAsync(string id,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(id);await serial.WaitAsync(cancellation);
        try{if(phase is not("healthy" or "effects_outcome_unknown"))throw new IOException("RUNTIME_NOT_HEALTHY");unknownEffectsIdleFence=null;RequireCurrent();
            await (owned as IRetainedScmUpdatePreparation??throw new IOException("RUNTIME_IDLE_CHANNEL_UNCONFIGURED")).CancelUpdateAsync(admission!,id,cancellation);RequireCurrent();}
        finally{serial.Release();}
    }
    public async Task StopAsync(CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(!started)throw new IOException("RUNTIME_NOT_STARTED");
            if(phase=="effects_outcome_unknown"&&unknownEffectsIdleFence is null)throw new IOException("RUNTIME_UNKNOWN_EFFECTS_IDLE_REQUIRED");
            // Unknown effects may stop only at the idle fence of the SAME current
            // generation. A healthy stop needs no fence, so it never requires a
            // still-RUNNING generation: Setup's removal STOP and SCM SHUTDOWN reach
            // the Runtime concurrently, leaving it STOP_PENDING. Refusing that
            // left this owned job to kill the app mid-shutdown. The session binds
            // any STOP it sends to the pinned host; quiescence is proven on the
            // owned host and job.
            if(phase=="effects_outcome_unknown")RequireCurrent();
            unknownEffectsIdleFence=null;retainStopped=true;phase="quiescing_runtime";
            if(!await CleanAsync(cancellation)){phase="recovery_required";throw new IOException("RUNTIME_CLEANUP_UNCONFIRMED");}
            phase="stopped";
        }
        finally{serial.Release();}
    }
    public async Task RetryCleanupAsync(CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(phase!="recovery_required"||owned is null)throw new IOException("RUNTIME_NO_OWNED_CLEANUP");
            if(!await CleanAsync(cancellation))throw new IOException("RUNTIME_CLEANUP_UNCONFIRMED");phase="failed";
        }
        finally{serial.Release();}
    }
    private async Task<bool> CleanAsync(CancellationToken cancellation)
    {
        if(owned is null)return true;
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(cleanup);
            await owned.StopAsync(deadline.Token);deadline.Token.ThrowIfCancellationRequested();
            owned.RequireQuiescence();if(gate is not null)gate.RequireQuiescence();
            if(!retainStopped){owned.Dispose();owned=null;}return true;
        }
        catch{return false;} // Keep exact native owner for an observed cleanup retry.
    }
    // The next transaction may still use RetainedGate and duplicate a recovery
    // observation. Release only after that transaction no longer needs it.
    internal void ReleaseStoppedResources()
    {
        if(Phase is not("stopped" or "failed" or "offline_recovery_ready"))throw new IOException("RUNTIME_STOP_OBSERVATION_REQUIRED");
        if(owned is null)return;owned.RequireQuiescence();gate?.RequireQuiescence();owned.Dispose();owned=null;
    }
}
