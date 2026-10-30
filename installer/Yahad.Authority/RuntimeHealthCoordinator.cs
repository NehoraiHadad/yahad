using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Retains the existing controller and all of its ownership through failed/late
// admission and cleanup. A committed record is never a health observation.
internal sealed class RuntimeHealthCoordinator
{
    private readonly Func<InstallationBinding,IServiceStartAuthorizer> authorizer;
    private readonly TimeSpan startup,stop,cleanup;
    private readonly SemaphoreSlim serial=new(1,1);
    private ServiceController? controller;private Task<ServiceRunResult>? execution;
    private bool started,stopRequested;private string phase="awaiting_runtime";
    internal string Phase=>Volatile.Read(ref phase);
    internal ServiceRunResult? LastResult=>execution is {IsCompletedSuccessfully:true}?execution.Result:null;
    internal bool CleanupReconciliationRequired=>Phase=="recovery_required";
    internal bool OwnsRuntimeResources=>execution is {IsCompleted:false}||controller?.CleanupReconciliationRequired==true;
    internal RuntimeHealthCoordinator(Func<InstallationBinding,IServiceStartAuthorizer> authorizer,TimeSpan? startup=null,TimeSpan? stop=null,TimeSpan? cleanup=null)
    {
        this.authorizer=authorizer;this.startup=startup??TimeSpan.FromMinutes(5);this.stop=stop??TimeSpan.FromSeconds(20);this.cleanup=cleanup??TimeSpan.FromSeconds(30);
        if(this.startup<=TimeSpan.Zero||this.startup>TimeSpan.FromMinutes(5)||this.stop<=TimeSpan.Zero||this.stop>TimeSpan.FromSeconds(30)||this.cleanup<=TimeSpan.Zero||this.cleanup>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException();
    }
    internal async Task StartCommittedAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();if(started)throw new IOException("RUNTIME_START_ALREADY_ISSUED");started=true;
            var healthy=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            phase="starting_runtime";
            try
            {
                controller=new(new ExactReleaseAuthorizer(authorizer(binding),expected),startup,stop);
                execution=controller.RunAsync(report=>{if(report.State==ServiceState.Running)healthy.TrySetResult();});
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(startup);
                var completed=await Task.WhenAny(healthy.Task,execution).WaitAsync(deadline.Token);
                deadline.Token.ThrowIfCancellationRequested();
                if(completed!=healthy.Task||execution.IsCompleted)throw new IOException("RUNTIME_DID_NOT_REMAIN_HEALTHY");
                phase="healthy";_ =ObserveExitAsync(execution);
            }
            catch
            {
                phase="quiescing_runtime";var confirmed=await CleanAsync();
                phase=confirmed?(cancellation.IsCancellationRequested?"cancelled":"failed"):"recovery_required";throw;
            }
        }
        finally{serial.Release();}
    }
    private async Task ObserveExitAsync(Task<ServiceRunResult> owned)
    {
        try
        {
            var result=await owned;
            if(ReferenceEquals(owned,execution)&&Phase=="healthy")phase=!result.QuiescenceConfirmed||controller?.CleanupReconciliationRequired==true?"recovery_required":stopRequested&&result.Failure==ServiceFailure.None?"stopped":"failed";
        }
        catch{if(ReferenceEquals(owned,execution))phase="recovery_required";}
    }
    internal async Task StopAsync(CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            if(!started)throw new IOException("RUNTIME_NOT_STARTED");phase="quiescing_runtime";stopRequested=true;
            if(!await CleanAsync(cancellation)){phase="recovery_required";throw new IOException("RUNTIME_CLEANUP_UNCONFIRMED");}
            var result=execution is null?new ServiceRunResult(ServiceFailure.None,true):await execution;
            phase=result.Failure==ServiceFailure.None?"stopped":"failed";
        }
        finally{serial.Release();}
    }
    internal async Task RetryCleanupAsync(CancellationToken cancellation=default)
    {
        await serial.WaitAsync(cancellation);
        try{if(!started)throw new IOException("RUNTIME_NOT_STARTED");if(!await CleanAsync(cancellation)){phase="recovery_required";throw new IOException("RUNTIME_CLEANUP_UNCONFIRMED");}phase="failed";}
        finally{serial.Release();}
    }
    private async Task<bool> CleanAsync(CancellationToken cancellation=default)
    {
        if(controller is null)return true;
        controller.Control(ServiceDefinition.Stop);
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(cleanup);
            if(execution is not null)await execution.WaitAsync(deadline.Token);
            await controller.RetryAdmissionCleanupAsync(deadline.Token);
            return !controller.CleanupReconciliationRequired;
        }
        catch{return false;} // Preserve the exact controller/task/grants for explicit retry.
    }
    private sealed class ExactReleaseAuthorizer(IServiceStartAuthorizer inner,AppRelease expected):IServiceStartAuthorizer,IObservedAdmission
    {
        public TimeSpan AdmissionBudget=>inner is IObservedAdmission o?o.AdmissionBudget:TimeSpan.FromSeconds(25);
        public event Action? ObservedWork{add{if(inner is IObservedAdmission o)o.ObservedWork+=value;}remove{if(inner is IObservedAdmission o)o.ObservedWork-=value;}}
        public async Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
        {
            var grant=await inner.AuthorizeAsync(cancellation);
            return new(grant.Release,grant.Launcher,new ReleaseBarrier(grant.Recovery,grant.Release,expected),grant);
        }
    }
    private sealed class ReleaseBarrier(IAppRecoveryBarrier inner,AppRelease actual,AppRelease expected):IAppRecoveryBarrier
    {
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
        {if(actual!=expected||release!=expected)throw new IOException("RUNTIME_COMMITTED_RELEASE_MISMATCH");return inner.RequireStartAllowedAsync(release,cancellation);}
    }
}

internal sealed class InstalledBindingStartAuthorizer(IInstalledActivationResolver resolver,InstallationBinding binding):IServiceStartAuthorizer,IObservedAdmission
{
    public TimeSpan AdmissionBudget=>resolver is IObservedAdmission o?o.AdmissionBudget:TimeSpan.FromSeconds(25);
    public event Action? ObservedWork{add{if(resolver is IObservedAdmission o)o.ObservedWork+=value;}remove{if(resolver is IObservedAdmission o)o.ObservedWork-=value;}}
    public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)=>resolver.ResolveAsync(binding,cancellation);
}
