using Yahad.Install;
using Yahad.Service;

internal static class ServiceStartupDiagnosticsTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var mode in new[]{"admission","launch","launch_cancel","readiness","timeout","early_exit","healthy","late_completion"})await check("startup observation classifies "+mode+" without changing ownership",async()=>
        {
            var resource=new Resource();var launcher=new Launcher(mode);var authorizer=new Authorizer(mode,launcher,resource);
            var controller=new ServiceController(authorizer,mode is "late_completion" or "timeout" or "early_exit"?TimeSpan.FromMilliseconds(50):TimeSpan.FromSeconds(2),mode=="late_completion"?TimeSpan.FromMilliseconds(200):TimeSpan.FromMilliseconds(100));
            var reports=new List<ServiceReport>();
            var result=await controller.RunAsync(report=>{reports.Add(report);if(report.State==ServiceState.Running)controller.Control(ServiceDefinition.Stop);});
            var observation=controller.StartupObservation;
            var expected=mode switch{"admission"=>"admission_denied","launch"=>"launch_failed","launch_cancel"=>"launch_cancelled","readiness"=>"readiness_failed","timeout" or "early_exit"=>"readiness_timeout",_=>null};
            if(mode=="late_completion")
            {
                if(observation.Failure is not("startup_timeout" or "readiness_cancelled"))throw new Exception("STARTUP_LATE_FIRST_CAUSE");
                launcher.LateReady.TrySetResult();await Task.Delay(30);
                if(controller.StartupObservation!=observation)throw new Exception("STARTUP_LATE_COMPLETION_OVERWROTE_FAILURE");
            }
            else if(mode is "timeout" or "early_exit")
            {if(observation.Failure is not("readiness_timeout" or "readiness_cancelled" or "startup_timeout"))throw new Exception("STARTUP_OBSERVATION_REASON "+observation.Failure);}
            else if(observation.Failure!=expected)throw new Exception("STARTUP_OBSERVATION_REASON "+observation.Failure);
            if(observation.ElapsedMilliseconds<0)throw new Exception("STARTUP_OBSERVATION_TIME");
            var stage=mode switch{"admission"=>ServiceStartupStage.Authorizing,"launch" or "launch_cancel"=>ServiceStartupStage.Launching,"healthy"=>ServiceStartupStage.Running,_=>ServiceStartupStage.WaitingForHealth};
            if(observation.Stage!=stage||!result.QuiescenceConfirmed||controller.CleanupReconciliationRequired)throw new Exception("STARTUP_OBSERVATION_STATE");
            if(mode=="healthy")
            {if(result.Failure!=ServiceFailure.None||!launcher.Session.Graceful||launcher.Session.Forced||!launcher.Session.Disposed)throw new Exception("STARTUP_OBSERVATION_HEALTH_OWNERSHIP");}
            else if(reports.Any(x=>x.State==ServiceState.Running)||result.Failure==ServiceFailure.None)throw new Exception("STARTUP_OBSERVATION_FALSE_HEALTH");
            if(mode!="admission"&&!resource.Disposed)throw new Exception("STARTUP_OBSERVATION_GRANT_LEAK");
            if(mode is "readiness" or "timeout" or "early_exit" or "late_completion"&&(!launcher.Session.Forced||!launcher.Session.Disposed))throw new Exception("STARTUP_OBSERVATION_CLEANUP");
            if(mode=="early_exit"&&controller.RootExitObservation is not{Code:42,ReadyObserved:false,ForcedCleanupRequested:false})throw new Exception("STARTUP_EARLY_EXIT_UNOBSERVED");
            if(mode=="healthy")
            {
                using var observationDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));
                while(controller.RootExitObservation is null)await Task.Delay(1,observationDeadline.Token);
                if(controller.RootExitObservation is not{Code:0,ReadyObserved:true,ForcedCleanupRequested:false})throw new Exception("STARTUP_HEALTHY_EXIT_UNOBSERVED");
            }
        });
    }
    private sealed class Resource:IDisposable{internal bool Disposed;public void Dispose()=>Disposed=true;}
    private sealed class Authorizer(string mode,Launcher launcher,Resource resource):IServiceStartAuthorizer,IObservedAdmission
    {
        public TimeSpan AdmissionBudget=>TimeSpan.FromSeconds(2);
        public event Action? ObservedWork{add{}remove{}}
        public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
        {cancellation.ThrowIfCancellationRequested();if(mode=="admission")throw new ServiceStartDenied(ServiceFailure.ActivationUnavailable);return Task.FromResult(new AuthorizedServiceStart(new("synthetic"),launcher,new Barrier(),resource));}
    }
    private sealed class Barrier:IAppRecoveryBarrier{public Task RequireStartAllowedAsync(AppRelease release,CancellationToken c){c.ThrowIfCancellationRequested();return Task.CompletedTask;}}
    private sealed class Launcher(string mode):IPrivateAppLauncher
    {
        internal Session Session=new();
        internal TaskCompletionSource LateReady=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken c)
        {c.ThrowIfCancellationRequested();if(mode=="launch")throw new IOException("SYNTHETIC_LAUNCH_REFUSAL");if(mode=="launch_cancel")throw new OperationCanceledException();if(mode=="early_exit")Session.NaturalExit(42);return Task.FromResult<IContainedAppSession>(Session);}
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken c)
        {if(mode=="readiness")throw new IOException("SYNTHETIC_READINESS_REFUSAL");if(mode=="late_completion")return LateReady.Task;return mode is "timeout" or "early_exit"?Task.Delay(Timeout.Infinite,c):Task.CompletedTask;}
    }
    private sealed class Session:IContainedAppSession
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Graceful,Forced,Disposed;
        internal void NaturalExit(int code)=>exit.TrySetResult(code);
        public Task RequestGracefulStopAsync(CancellationToken c){c.ThrowIfCancellationRequested();Graceful=true;exit.TrySetResult(0);return Task.CompletedTask;}
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>exit.Task.WaitAsync(c);
        public Task WaitForTreeEmptyAsync(CancellationToken c){c.ThrowIfCancellationRequested();if(!exit.Task.IsCompleted)throw new Exception("SYNTHETIC_TREE_LIVE");return Task.CompletedTask;}
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c){c.ThrowIfCancellationRequested();Forced=true;exit.TrySetResult(1);return Task.CompletedTask;}
        public ValueTask DisposeAsync(){if(!exit.Task.IsCompleted)throw new Exception("SYNTHETIC_DISPOSE_BEFORE_EXIT");Disposed=true;return ValueTask.CompletedTask;}
    }
}
