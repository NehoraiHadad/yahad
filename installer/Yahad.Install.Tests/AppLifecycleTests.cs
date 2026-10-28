using Yahad.Install;

internal static class AppLifecycleTests
{
    private sealed class Fixture : IPrivateAppLauncher, IAppRecoveryBarrier, IAppUpdateAuthority
    {
        public readonly List<string> Calls = [];
        public int RootExit;
        public bool StuckTree, DenyRecovery, FailActivation, FailNewHealth;
        private sealed class Session(Fixture owner) : IContainedAppSession
        {
            public Task RequestGracefulStopAsync(CancellationToken cancellation) { owner.Calls.Add("stop"); return Task.CompletedTask; }
            public Task<int> WaitForRootExitAsync(CancellationToken cancellation) { owner.Calls.Add("root-exit"); return Task.FromResult(owner.RootExit); }
            public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
            { owner.Calls.Add("tree-empty"); if (owner.StuckTree) await Task.Delay(Timeout.Infinite, cancellation); }
            public ValueTask DisposeAsync() { owner.Calls.Add("dispose-session"); return ValueTask.CompletedTask; }
        }
        public Task RequireStartAllowedAsync(AppRelease release, CancellationToken cancellation)
        { Calls.Add("barrier:" + release.Id); if (DenyRecovery) throw new InvalidOperationException("RECOVERY_REQUIRED"); return Task.CompletedTask; }
        public Task<IContainedAppSession> StartAsync(AppRelease release, CancellationToken cancellation)
        { Calls.Add("start:" + release.Id); return Task.FromResult<IContainedAppSession>(new Session(this)); }
        public Task WaitForHealthyAsync(IContainedAppSession session, CancellationToken cancellation)
        { Calls.Add("health"); if (FailNewHealth && Calls.Contains("activate:new:old")) throw new IOException("UNHEALTHY"); return Task.CompletedTask; }
        public Task<AppRelease> ActivateAsync(AppRelease release, QuiescentApp quiescence, CancellationToken cancellation)
        { Calls.Add("activate:" + release.Id + ":" + quiescence.PreviousRelease.Id); if (FailActivation) throw new IOException("ACTIVATION_UNCERTAIN"); return Task.FromResult(release); }
        public AppLifecycle Host() => new(new("old"), this, this, this, TimeSpan.FromMilliseconds(250));
    }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException) { return; } throw new Exception("expected refusal"); }
        await check("native host update orders barrier shutdown tree-empty authority then health", async () =>
        {
            var f = new Fixture(); await using var host = f.Host(); await host.StartAsync(); await host.UpdateAsync(new("new"));
            Assert(host.State == AppLifecycleState.Running);
            Assert(f.Calls.SequenceEqual(new[] { "barrier:old", "start:old", "health", "stop", "root-exit", "tree-empty", "dispose-session", "activate:new:old", "barrier:new", "start:new", "health" }));
            await host.StopAsync(); Assert(host.State == AppLifecycleState.Stopped);
        });
        await check("recovery barrier refuses app start without touching update authority", async () =>
        {
            var f = new Fixture { DenyRecovery = true }; await using var host = f.Host(); await Reject(() => host.StartAsync());
            Assert(host.State == AppLifecycleState.RecoveryRequired && f.Calls.SequenceEqual(new[] { "barrier:old" }));
        });
        await check("unclean root exit cannot grant activation", async () =>
        {
            var f = new Fixture { RootExit = 17 }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.UpdateAsync(new("new")));
            Assert(host.State == AppLifecycleState.RecoveryRequired && !f.Calls.Any(c => c.StartsWith("activate:")));
        });
        await check("living descendant after root exit times out and prevents activation", async () =>
        {
            var f = new Fixture { StuckTree = true }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.UpdateAsync(new("new")));
            Assert(host.State == AppLifecycleState.RecoveryRequired && !f.Calls.Any(c => c.StartsWith("activate:")));
            await Reject(() => host.StartAsync());
        });
        await check("uncertain activation never restarts old payload or restores data", async () =>
        {
            var f = new Fixture { FailActivation = true }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.UpdateAsync(new("new")));
            Assert(host.State == AppLifecycleState.RecoveryRequired && f.Calls.Count(c => c == "start:old") == 1 && !f.Calls.Contains("start:new"));
        });
        await check("failed new health stays behind recovery barrier without downgrade", async () =>
        {
            var f = new Fixture { FailNewHealth = true }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.UpdateAsync(new("new")));
            Assert(host.State == AppLifecycleState.RecoveryRequired && f.Calls.Count(c => c == "start:old") == 1);
        });
        await check("cancelled request cannot stop or activate running app", async () =>
        {
            var f = new Fixture(); await using var host = f.Host(); await host.StartAsync(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Reject(() => host.UpdateAsync(new("new"), cancel.Token)); Assert(host.State == AppLifecycleState.Running && !f.Calls.Contains("stop"));
        });
    }
}
