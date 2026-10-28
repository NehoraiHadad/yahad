using System.Diagnostics;
using Yahad.Install;

internal static class LifecycleCleanupTests
{
    private sealed class NativeSession(JobProcess job) : IContainedAppSession
    {
        public Task RequestGracefulStopAsync(CancellationToken cancellation) => Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation) => job.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation) => job.WaitForTreeEmptyAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation) => job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public ValueTask DisposeAsync() { job.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class NativeFixture(string root,bool timeout) : IPrivateAppLauncher,IAppRecoveryBarrier,IAppUpdateAuthority
    {
        public readonly string PidFile=Path.Combine(root,"grandchild.pid");
        public int RootPid;
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)=>Task.CompletedTask;
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {
            var environment=LaunchEnvironment.Clean(root);
            environment["DOTNET_ROOT"]=Environment.GetEnvironmentVariable("DOTNET_ROOT")??throw new IOException("FIXTURE_DOTNET_ROOT");
            var job=JobProcess.Start(Environment.ProcessPath!,["--tree",PidFile],root,environment);RootPid=job.ProcessId;
            return Task.FromResult<IContainedAppSession>(new NativeSession(job));
        }
        public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        {
            while(!File.Exists(PidFile)||new FileInfo(PidFile).Length==0)await Task.Delay(20,cancellation);
            if(timeout)await Task.Delay(Timeout.Infinite); // deliberately ignores cancellation
            throw new IOException("FIXTURE_UNHEALTHY");
        }
        public Task<AppRelease> ActivateAsync(AppRelease release,QuiescentApp grant,CancellationToken cancellation)=>throw new Exception("must not activate");
    }
    private static bool Alive(int id) {try{using var p=Process.GetProcessById(id);return !p.HasExited;}catch(ArgumentException){return false;}}
    public static async Task Run(string root,Func<string,Func<Task>,Task> check)
    {
        foreach(var timeout in new[]{false,true})
        await check(timeout?"native health timeout cleans complete job before returning failure":"native failed health cleans root and grandchild before returning failure",async()=>
        {
            var directory=SafeDirectory.CreatePrivateSession(root);var fixture=new NativeFixture(directory,timeout);
            await using var host=new AppLifecycle(new("fixture"),fixture,fixture,fixture,TimeSpan.FromSeconds(3));
            try{await host.StartAsync();throw new Exception("expected startup failure");}
            catch(Exception e)when(e is IOException or OperationCanceledException){}
            if(host.State!=AppLifecycleState.RecoveryRequired||host.QuiescenceReconciliationRequired||Alive(fixture.RootPid))throw new Exception("root cleanup unconfirmed");
            if(File.Exists(fixture.PidFile)&&Alive(int.Parse(File.ReadAllText(fixture.PidFile))))throw new Exception("grandchild survived failed health");
        });
    }
}
