using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal interface IRuntimeBootstrapObservationSource
{
    bool OwnsResources{get;}bool CleanupRequired{get;}
    Task PrepareAsync(InstallationBinding binding,AppRelease release,CancellationToken cancellation);
    RetainedRuntimeQuiescence AcquireObservation();
    Task RetryCleanupAsync(CancellationToken cancellation);
    void ReleaseStoppedResources();
}
internal sealed class RuntimeBootstrapOwnershipException(RuntimeBootstrapObservationOwner owner,Exception failure):IOException("RUNTIME_BOOTSTRAP_CLEANUP_OWNER_RETAINED",failure)
{internal Task RetryCleanupAsync(CancellationToken cancellation)=>owner.RetryCleanupAsync(cancellation);}

internal sealed class RuntimeBootstrapObservationOwner(IRuntimeBootstrapObservationSource source)
{
    private RetainedRuntimeQuiescence? observation;
    internal async Task<RetainedRuntimeQuiescence> ObserveAsync(InstallationBinding binding,AppRelease release,CancellationToken cancellation)
    {
        NativeCleanupOwnershipGate.RequireClear();NativeCleanupOwnershipGate.Retain(this,CleanupCoreAsync,active:true); // BEFORE any native start.
        try
        {
            await source.PrepareAsync(binding,release,cancellation);observation=source.AcquireObservation();await observation.RequireAsync(cancellation);
            source.ReleaseStoppedResources();var result=observation;observation=null;NativeCleanupOwnershipGate.CompleteObserved(this);return result;
        }
        catch(Exception failure)
        {
            if(!source.OwnsResources&&observation is null){NativeCleanupOwnershipGate.CompleteObserved(this);throw;}
            NativeCleanupOwnershipGate.MarkUnresolved(this);
            throw new RuntimeBootstrapOwnershipException(this,failure);
        }
    }
    private async Task CleanupCoreAsync(CancellationToken cancellation)
    {
        if(source.CleanupRequired)await source.RetryCleanupAsync(cancellation);
        if(observation is not null)await observation.RequireAsync(cancellation);
        if(source.OwnsResources)source.ReleaseStoppedResources();observation?.Dispose();observation=null;
    }
    internal Task RetryCleanupAsync(CancellationToken cancellation)=>NativeCleanupOwnershipGate.RetryAsync(this,cancellation);
    internal static RuntimeBootstrapObservationOwner Installed()=>new(new InstalledSource(new(()=>new NativeScmRuntimeSession())));
    private sealed class InstalledSource(WindowsScmRuntimeLifecycle lifecycle):IRuntimeBootstrapObservationSource
    {
        public bool OwnsResources=>lifecycle.OwnsRuntimeResources;public bool CleanupRequired=>lifecycle.CleanupReconciliationRequired;
        public Task PrepareAsync(InstallationBinding binding,AppRelease release,CancellationToken cancellation)=>lifecycle.PrepareOfflineRecoveryAsync(binding,release,cancellation);
        public RetainedRuntimeQuiescence AcquireObservation()=>lifecycle.AcquireRecoveryQuiescence();
        public Task RetryCleanupAsync(CancellationToken cancellation)=>lifecycle.RetryCleanupAsync(cancellation);
        public void ReleaseStoppedResources()=>lifecycle.ReleaseStoppedResources();
    }
}
