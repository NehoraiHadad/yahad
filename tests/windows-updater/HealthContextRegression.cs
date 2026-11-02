using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

// The native prefix uses compound success assertions. Preserve enough independent
// façade observations to distinguish startup refusal, early exit and failed stop.
// These are in-memory negative cases; they do not launch Node or touch a Store.
internal static class HealthContextRegression
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var mode in new[]{"readiness_refused","natural_exit","stop_refused"})
        await check("health context preserves independent verdicts: "+mode,async()=>
        {
            var resource=new Resource();var launcher=new Launcher(mode);var release=new AppRelease(new string('a',64));
            var health=new RuntimeHealthCoordinator(_=>new Authorizer(release,launcher,resource));
            var binding=new InstallationBinding(Guid.NewGuid().ToString("N"),new string('b',64));
            var runningObserved=false;
            try{await health.StartCommittedAsync(binding,release);runningObserved=true;}
            catch(IOException error)when(mode!="stop_refused"&&error.Message=="RUNTIME_DID_NOT_REMAIN_HEALTHY"){}
            if(mode=="stop_refused")await health.StopAsync();
            var result=health.LastResult??throw new Exception("HEALTH_CONTEXT_RESULT_MISSING");
            var startup=health.LastStartupObservation??throw new Exception("HEALTH_CONTEXT_STARTUP_MISSING");
            using var observationDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
            while(health.LastRootExitObservation is null)await Task.Delay(1,observationDeadline.Token);
            var exit=health.LastRootExitObservation!;
            if(mode=="stop_refused")
            {
                if(!runningObserved||result.Failure!=ServiceFailure.StopFailed||startup.Stage!=ServiceStartupStage.Running||startup.Failure is not null||
                    exit.Code!=1||!exit.ReadyObserved||!exit.ForcedCleanupRequested)throw new Exception("HEALTH_CONTEXT_STOP_MISCLASSIFIED");
            }
            else
            {
                if(runningObserved||result.Failure!=ServiceFailure.StartFailed||startup.Stage!=ServiceStartupStage.WaitingForHealth||startup.Failure!="readiness_failed"||exit.ReadyObserved)
                    throw new Exception("HEALTH_CONTEXT_START_MISCLASSIFIED");
                if(mode=="natural_exit"?(exit.Code!=42||exit.ForcedCleanupRequested):(exit.Code!=1||!exit.ForcedCleanupRequested))
                    throw new Exception("HEALTH_CONTEXT_EXIT_CAUSE_LOST");
            }
            if(health.Phase!="failed"||!result.QuiescenceConfirmed||health.CleanupReconciliationRequired||health.OwnsRuntimeResources||
                resource.Disposals!=1||launcher.Session.Disposals!=1||!launcher.Session.Forced)
                throw new Exception("HEALTH_CONTEXT_OWNERSHIP_NOT_RELEASED");
        });
    }
    private sealed class Resource:IDisposable{internal int Disposals;public void Dispose()=>Disposals++;}
    private sealed class Authorizer(AppRelease release,Launcher launcher,Resource resource):IServiceStartAuthorizer
    {
        public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();return Task.FromResult(new AuthorizedServiceStart(release,launcher,new Barrier(),resource));}
    }
    private sealed class Barrier:IAppRecoveryBarrier
    {public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;}}
    private sealed class Launcher(string mode):IPrivateAppLauncher
    {
        internal Session Session=new(mode);
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();if(mode=="natural_exit")Session.ExitNaturally();return Task.FromResult<IContainedAppSession>(Session);}
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();return mode=="stop_refused"?Task.CompletedTask:Task.FromException(new IOException("SYNTHETIC_READINESS_REFUSAL"));}
    }
    private sealed class Session(string mode):IContainedAppSession
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Forced;internal int Disposals;
        internal void ExitNaturally()=>exit.TrySetResult(42);
        public Task RequestGracefulStopAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();return mode=="stop_refused"?Task.FromException(new IOException("SYNTHETIC_STOP_REFUSAL")):Task.CompletedTask;}
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation)=>exit.Task.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();if(!exit.Task.IsCompleted)throw new Exception("SYNTHETIC_TREE_LIVE");return Task.CompletedTask;}
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();Forced=true;exit.TrySetResult(1);return Task.CompletedTask;}
        public ValueTask DisposeAsync()
        {if(!exit.Task.IsCompleted)throw new Exception("SYNTHETIC_DISPOSE_BEFORE_EXIT");Disposals++;return ValueTask.CompletedTask;}
    }
}
