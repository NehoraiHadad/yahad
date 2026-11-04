using Yahad.Install;

internal static class AppLifecycleTests
{
    private sealed class Fixture : IPrivateAppLauncher
    {
        public readonly List<string> Calls = [];
        public int RootExit;
        public bool StuckTree, FailHealth, FailCleanup;
        private sealed class Session(Fixture owner) : IContainedAppSession
        {
            public Task RequestGracefulStopAsync(CancellationToken cancellation) { owner.Calls.Add("stop"); return Task.CompletedTask; }
            public Task<int> WaitForRootExitAsync(CancellationToken cancellation) { owner.Calls.Add("root-exit"); return Task.FromResult(owner.RootExit); }
            public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
            { owner.Calls.Add("tree-empty"); if (owner.StuckTree) await Task.Delay(Timeout.Infinite, cancellation); }
            public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation) { owner.Calls.Add("abort-tree-empty"); if(owner.FailCleanup) throw new IOException("QUIESCENCE_UNKNOWN"); return Task.CompletedTask; }
            public ValueTask DisposeAsync() { owner.Calls.Add("dispose-session"); return ValueTask.CompletedTask; }
        }
        public Task<IContainedAppSession> StartAsync(CancellationToken cancellation)
        { Calls.Add("start:current"); return Task.FromResult<IContainedAppSession>(new Session(this)); }
        public Task WaitForHealthyAsync(IContainedAppSession session, CancellationToken cancellation)
        { Calls.Add("health"); if (FailHealth) throw new IOException("UNHEALTHY"); return Task.CompletedTask; }
        public AppLifecycle Host() => new(this, TimeSpan.FromMilliseconds(250));
    }
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        async Task Reject(Func<Task> action) { try { await action(); } catch (Exception e) when (e is InvalidOperationException or IOException or OperationCanceledException) { return; } throw new Exception("expected refusal"); }
        await check("native host orders barrier start health then shutdown and tree-empty", async () =>
        {
            var f = new Fixture(); await using var host = f.Host(); await host.StartAsync();
            Assert(host.State == AppLifecycleState.Running);
            await host.StopAsync(); Assert(host.State == AppLifecycleState.Stopped);
            Assert(f.Calls.SequenceEqual(new[] { "start:current", "health", "stop", "root-exit", "tree-empty", "dispose-session" }));
        });
        await check("unclean root exit is not a confirmed stop", async () =>
        {
            var f = new Fixture { RootExit = 17 }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.StopAsync());
            Assert(host.State == AppLifecycleState.Failed && f.Calls.Contains("abort-tree-empty"));
        });
        await check("living descendant after root exit times out and refuses a later start", async () =>
        {
            var f = new Fixture { StuckTree = true }; await using var host = f.Host(); await host.StartAsync(); await Reject(() => host.StopAsync());
            Assert(host.State == AppLifecycleState.Failed);
            await Reject(() => host.StartAsync());
        });
        await check("failed health terminates the tree and stays down", async () =>
        {
            var f = new Fixture { FailHealth = true }; await using var host = f.Host(); await Reject(() => host.StartAsync());
            Assert(host.State == AppLifecycleState.Failed && f.Calls.Count(c => c.StartsWith("start:")) == 1 && f.Calls.Contains("abort-tree-empty") && f.Calls.Contains("dispose-session"));
        });
        await check("repeated disposal retries unresolved cleanup and never reports false success", async () =>
        {
            var f=new Fixture { FailCleanup=true };var host=f.Host();await host.StartAsync();
            await Reject(async()=>await host.DisposeAsync());
            await Reject(async()=>await host.DisposeAsync());
            Assert(f.Calls.Count(c=>c=="abort-tree-empty")==2&&!f.Calls.Contains("dispose-session"));
            await Reject(()=>host.StartAsync());
            f.FailCleanup=false;await host.DisposeAsync();
            Assert(f.Calls.Count(c=>c=="abort-tree-empty")==3&&f.Calls.Count(c=>c=="dispose-session")==1);
            await host.DisposeAsync();Assert(f.Calls.Count(c=>c=="abort-tree-empty")==3);
        });
        await check("cancelled request cannot stop a running app", async () =>
        {
            var f = new Fixture(); await using var host = f.Host(); await host.StartAsync(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
            await Reject(() => host.StopAsync(cancel.Token)); Assert(host.State == AppLifecycleState.Running && !f.Calls.Contains("stop"));
        });
    }
}
