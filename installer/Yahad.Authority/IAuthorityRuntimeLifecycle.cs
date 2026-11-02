using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// A local fixture controller and a separate SCM runtime share lifecycle ordering,
// not execution identity. Installed composition must supply the latter.
internal interface IAuthorityRuntimeLifecycle
{
    string Phase { get; }
    bool CleanupReconciliationRequired { get; }
    Task StartCommittedAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default);
    Task StopAsync(CancellationToken cancellation=default);
    Task RetryCleanupAsync(CancellationToken cancellation=default);
}

internal interface IAuthorityRuntimeUpdatePreparation
{
    Task<bool> PrepareUpdateAsync(string id,CancellationToken cancellation);
    Task CancelUpdateAsync(string id,CancellationToken cancellation);
}

internal interface IAuthorityRuntimeRecoveryObservation
{
    RetainedRuntimeQuiescence AcquireRecoveryQuiescence();
}
