namespace Yahad.Install;

public enum AppLifecycleState { Stopped, Starting, Running, Quiescing, Activating, RecoveryRequired }

/// <summary>A release identity, never an arbitrary executable supplied by UI.</summary>
public sealed record AppRelease(string Id);

/// <summary>The launcher must validate/pin its private payload and contain all descendants.</summary>
public interface IContainedAppSession : IAsyncDisposable
{
    Task RequestGracefulStopAsync(CancellationToken cancellation);
    Task<int> WaitForRootExitAsync(CancellationToken cancellation);
    Task WaitForTreeEmptyAsync(CancellationToken cancellation);
    Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation);
}
public interface IPrivateAppLauncher
{
    Task<IContainedAppSession> StartAsync(AppRelease release, CancellationToken cancellation);
    Task WaitForHealthyAsync(IContainedAppSession session, CancellationToken cancellation);
}
public interface IAppRecoveryBarrier
{
    // Includes durable update/recovery journal, data schema, policy/pins and backup barriers.
    Task RequireStartAllowedAsync(AppRelease release, CancellationToken cancellation);
}

/// <summary>Only a completed graceful shutdown plus an empty native job can issue this grant.</summary>
public sealed class QuiescentApp
{
    public AppRelease PreviousRelease { get; }
    internal QuiescentApp(AppRelease previous) { PreviousRelease = previous; }
}
public interface IAppUpdateAuthority
{
    // The existing updater owns signature/policy verification, versioned payloads,
    // backups, durable activation and recovery. The installer never copies a live DB.
    Task<AppRelease> ActivateAsync(AppRelease requested, QuiescentApp quiescence, CancellationToken cancellation);
}

/// <summary>Host/update boundary; no SCM, filesystem activation or database implementation.</summary>
public sealed class AppLifecycle : IAsyncDisposable
{
    private readonly IPrivateAppLauncher launcher;
    private readonly IAppRecoveryBarrier recovery;
    private readonly IAppUpdateAuthority updater;
    private readonly TimeSpan deadline;
    private readonly SemaphoreSlim serial = new(1, 1);
    private IContainedAppSession? session;
    private AppRelease active;
    private bool disposed;
    public bool QuiescenceReconciliationRequired => session is not null && State == AppLifecycleState.RecoveryRequired;
    public AppLifecycleState State { get; private set; } = AppLifecycleState.Stopped;

    public AppLifecycle(AppRelease active, IPrivateAppLauncher launcher, IAppRecoveryBarrier recovery,
        IAppUpdateAuthority updater, TimeSpan deadline)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(deadline));
        this.active = active; this.launcher = launcher; this.recovery = recovery; this.updater = updater; this.deadline = deadline;
    }
    private CancellationTokenSource Bound(CancellationToken cancellation)
    { var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation); bounded.CancelAfter(deadline); return bounded; }
    private void Require(AppLifecycleState state)
    { ObjectDisposedException.ThrowIf(disposed, this); if (State != state) throw new InvalidOperationException("LIFECYCLE_STATE"); }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            Require(AppLifecycleState.Stopped);
            using var bounded = Bound(cancellation);
            try { await StartCoreAsync(bounded.Token); }
            catch { await FailAndCleanAsync(); throw; }
        }
        finally { serial.Release(); }
    }
    private async Task StartCoreAsync(CancellationToken cancellation)
    {
        State = AppLifecycleState.Starting;
        await recovery.RequireStartAllowedAsync(active, cancellation);
        session = await launcher.StartAsync(active, cancellation);
        await launcher.WaitForHealthyAsync(session, cancellation).WaitAsync(cancellation);
        State = AppLifecycleState.Running;
    }
    private async Task<QuiescentApp> QuiesceAsync(CancellationToken cancellation)
    {
        State = AppLifecycleState.Quiescing;
        var running = session ?? throw new InvalidOperationException("SESSION_REQUIRED");
        await running.RequestGracefulStopAsync(cancellation).WaitAsync(cancellation);
        if (await running.WaitForRootExitAsync(cancellation).WaitAsync(cancellation) != 0) throw new InvalidOperationException("UNCLEAN_APP_EXIT");
        await running.WaitForTreeEmptyAsync(cancellation).WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await running.DisposeAsync(); session = null;
        return new(active);
    }
    public async Task StopAsync(CancellationToken cancellation = default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            Require(AppLifecycleState.Running);
            using var bounded = Bound(cancellation);
            try { await QuiesceAsync(bounded.Token); State = AppLifecycleState.Stopped; }
            catch { await FailAndCleanAsync(); throw; }
        }
        finally { serial.Release(); }
    }
    public async Task UpdateAsync(AppRelease requested, CancellationToken cancellation = default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            Require(AppLifecycleState.Running);
            using var bounded = Bound(cancellation);
            try
            {
                var grant = await QuiesceAsync(bounded.Token);
                bounded.Token.ThrowIfCancellationRequested();
                State = AppLifecycleState.Activating;
                active = await updater.ActivateAsync(requested, grant, bounded.Token);
                await StartCoreAsync(bounded.Token);
            }
            // Activation/start may already have changed the schema. Never relaunch
            // the old app or overwrite data as a generic catch/rollback action.
            catch { await FailAndCleanAsync(); throw; }
        }
        finally { serial.Release(); }
    }
    private async Task FailAndCleanAsync()
    {
        State = AppLifecycleState.RecoveryRequired;
        if (session is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await session.TerminateAndWaitForTreeEmptyAsync(cleanup.Token).WaitAsync(cleanup.Token);
            await session.DisposeAsync(); session = null;
        }
        catch
        {
            // Retain ownership and recovery quarantine when quiescence is unknown.
            // No restore, activation grant or restart is issued from this path.
        }
    }
    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync();
        try
        {
            if (disposed) return;
            disposed = true;
            if (session is not null)
            {
                await FailAndCleanAsync();
                if (session is not null) throw new IOException("LIFECYCLE_QUIESCENCE_UNCONFIRMED");
            }
        }
        finally { serial.Release(); }
    }
}
