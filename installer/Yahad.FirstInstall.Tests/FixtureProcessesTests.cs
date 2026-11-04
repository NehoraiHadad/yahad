using System.IO;
using Yahad.Install;

internal static class FixtureProcessesTests
{
    private static void Require(bool value){if(!value)throw new Exception("FIXTURE_PROCESS_ASSERTION");}
    internal static async Task NativeLaunchRace(string node)
    {
        var directory=OwnedFixtureDirectory.Create(Path.GetTempPath());var owner=new FixtureProcesses();
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var start=Task.Run(()=>owner.StartJob(node,["-e",""],directory.Root,LaunchEnvironment.Clean(directory.Root),(_,_)=>
        {entered.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new IOException("TEST_START_GATE_TIMEOUT");}));
        try
        {
            if(!entered.Wait(TimeSpan.FromSeconds(10)))throw new IOException("TEST_START_NOT_OBSERVED");
            var cleanup=owner.CleanupAsync();await Task.Delay(100);
            Require(!cleanup.IsCompleted&&!owner.Confirmed);release.Set();using var job=await start;await cleanup;
            Require(owner.Confirmed);
        }
        finally{release.Set();try{using var job=await start;}finally{await owner.CleanupAsync();directory.Cleanup(()=>owner.Confirmed);}}
    }
    internal static async Task RunAsync(Func<string,Func<Task>,Task> check)
    {
        await check("fixture teardown terminates only owned session before disposal",async()=>
        {var owner=new FixtureProcesses();var fake=new Fake();var launcher=owner.Launcher(fake);_=await launcher.StartAsync(default);await owner.CleanupAsync();Require(owner.Confirmed&&fake.Terminated==1&&fake.Disposed==1);await owner.CleanupAsync();Require(fake.Terminated==1);});
        await check("unconfirmed owned session refuses cleanup and blocks another launch",async()=>
        {var owner=new FixtureProcesses();var fake=new Fake{Deny=true};var launcher=owner.Launcher(fake);_=await launcher.StartAsync(default);try{await owner.CleanupAsync();throw new Exception("CLEANUP_ADMITTED");}catch(IOException){}Require(!owner.Confirmed&&fake.Disposed==0);try{_=await launcher.StartAsync(default);throw new Exception("NEW_LAUNCH_ADMITTED");}catch(IOException){}Require(fake.Started==1);fake.Deny=false;await owner.CleanupAsync();});
        await check("premature session disposal retains handle until real empty-tree observation",async()=>
        {var owner=new FixtureProcesses();var fake=new Fake();var session=await owner.Launcher(fake).StartAsync(default);try{await session.DisposeAsync();throw new Exception("UNPROVEN_DISPOSE");}catch(IOException){}Require(fake.Disposed==0);await owner.CleanupAsync();Require(owner.Confirmed&&fake.Disposed==1);});
        await check("normally disposed owned session is not terminated twice",async()=>
        {var owner=new FixtureProcesses();var fake=new Fake();var session=await owner.Launcher(fake).StartAsync(default);await session.WaitForTreeEmptyAsync(default);await session.DisposeAsync();await owner.CleanupAsync();Require(owner.Confirmed&&fake.Terminated==0&&fake.Disposed==1);});
    }
    private sealed class Fake:IPrivateAppLauncher,IContainedAppSession
    {
        internal int Started,Terminated,Disposed;internal bool Deny;
        public Task<IContainedAppSession> StartAsync(CancellationToken c){Started++;return Task.FromResult<IContainedAppSession>(this);}
        public Task WaitForHealthyAsync(IContainedAppSession s,CancellationToken c)=>Task.CompletedTask;
        public Task RequestGracefulStopAsync(CancellationToken c)=>Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>Task.FromResult(0);
        public Task WaitForTreeEmptyAsync(CancellationToken c)=>Deny?Task.FromException(new IOException("TEST_EMPTY_UNCONFIRMED")):Task.CompletedTask;
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c){Terminated++;return WaitForTreeEmptyAsync(c);}
        public ValueTask DisposeAsync(){Disposed++;return ValueTask.CompletedTask;}
    }
}
