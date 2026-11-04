namespace Yahad.Install;

public enum AppLifecycleState { Stopped, Starting, Running, Quiescing, Failed }

/// <summary>The launcher contains all of the session's descendants.</summary>
public interface IContainedAppSession : IAsyncDisposable
{
    Task RequestGracefulStopAsync(CancellationToken cancellation);
    Task<int> WaitForRootExitAsync(CancellationToken cancellation);
    Task WaitForTreeEmptyAsync(CancellationToken cancellation);
    Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation);
}
public interface IPrivateAppLauncher
{
    Task<IContainedAppSession> StartAsync(CancellationToken cancellation);
    Task WaitForHealthyAsync(IContainedAppSession session, CancellationToken cancellation);
}

/// <summary>Start and stop of one contained app session; no SCM or database implementation.</summary>
public sealed class AppLifecycle : IAsyncDisposable
{
    private readonly IPrivateAppLauncher launcher;
    private readonly TimeSpan deadline,startupDeadline;
    private readonly SemaphoreSlim serial = new(1, 1);
    private IContainedAppSession? session;
    private bool disposed;
    public AppLifecycleState State { get; private set; } = AppLifecycleState.Stopped;

    public AppLifecycle(IPrivateAppLauncher launcher,TimeSpan deadline,TimeSpan? startupDeadline=null)
    {
        if (deadline <= TimeSpan.Zero || deadline > TimeSpan.FromMinutes(5)) throw new ArgumentOutOfRangeException(nameof(deadline));
        this.launcher = launcher; this.deadline = deadline;
        this.startupDeadline=startupDeadline??deadline;
        if(this.startupDeadline<=TimeSpan.Zero||this.startupDeadline>TimeSpan.FromMinutes(5))throw new ArgumentOutOfRangeException(nameof(startupDeadline));
    }
    private CancellationTokenSource Bound(CancellationToken cancellation,TimeSpan? timeout=null)
    { var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation); bounded.CancelAfter(timeout??deadline); return bounded; }
    private void Require(AppLifecycleState state)
    { ObjectDisposedException.ThrowIf(disposed, this); if (State != state) throw new InvalidOperationException("LIFECYCLE_STATE"); }

    public async Task StartAsync(CancellationToken cancellation = default)
    {
        await serial.WaitAsync(cancellation);
        try
        {
            Require(AppLifecycleState.Stopped);
            using var bounded = Bound(cancellation,startupDeadline);
            try { await StartCoreAsync(bounded.Token); }
            catch { await FailAndCleanAsync(); throw; }
        }
        finally { serial.Release(); }
    }
    private async Task StartCoreAsync(CancellationToken cancellation)
    {
        State = AppLifecycleState.Starting;
        session = await launcher.StartAsync(cancellation);
        await launcher.WaitForHealthyAsync(session, cancellation).WaitAsync(cancellation);
        State = AppLifecycleState.Running;
    }
    private async Task QuiesceAsync(CancellationToken cancellation)
    {
        State = AppLifecycleState.Quiescing;
        var running = session ?? throw new InvalidOperationException("SESSION_REQUIRED");
        await running.RequestGracefulStopAsync(cancellation).WaitAsync(cancellation);
        if (await running.WaitForRootExitAsync(cancellation).WaitAsync(cancellation) != 0) throw new InvalidOperationException("UNCLEAN_APP_EXIT");
        await running.WaitForTreeEmptyAsync(cancellation).WaitAsync(cancellation);
        cancellation.ThrowIfCancellationRequested();
        await running.DisposeAsync(); session = null;
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
    private async Task FailAndCleanAsync()
    {
        State = AppLifecycleState.Failed;
        if (session is null) return;
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await session.TerminateAndWaitForTreeEmptyAsync(cleanup.Token).WaitAsync(cleanup.Token);
            await session.DisposeAsync(); session = null;
        }
        catch
        {
            // Retain ownership when quiescence is unknown; nothing restarts from this path.
        }
    }
    public async ValueTask DisposeAsync()
    {
        await serial.WaitAsync();
        try
        {
            // Repeated disposal retries an unresolved owned session. A previous
            // failure never turns the next call into a false success.
            if (disposed && session is null) return;
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
