using Yahad.Install;

namespace Yahad.Service;

internal enum ServiceFailure:uint {None=0,IdentityDenied=1001,BootstrapUnavailable=1002,ActivationUnavailable=1003,
    StartFailed=1004,UnexpectedExit=1005,StopFailed=1006,QuiescenceUnknown=1007,ArgumentsRejected=1008,StatusTransport=1009}
internal sealed class ServiceStartDenied(ServiceFailure failure):Exception
{internal ServiceFailure Failure=>failure;}
internal interface IObservedAdmission
{
    TimeSpan AdmissionBudget {get;}
    event Action? ObservedWork;
}
internal interface IServiceStartAuthorizer
{Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation);}

// Only trusted in-process adapters can construct this grant. No CLI, JSON or
// UI Boolean becomes a launch grant. Default installed activation is denied.
internal sealed class AuthorizedServiceStart(AppRelease release,IPrivateAppLauncher launcher,
    IAppRecoveryBarrier recovery,IDisposable? resources=null):IDisposable
{
    internal AppRelease Release=>release;
    internal IPrivateAppLauncher Launcher=>launcher;
    internal IAppRecoveryBarrier Recovery=>recovery;
    public void Dispose()=>resources?.Dispose();
}
internal readonly record struct ServiceRunResult(ServiceFailure Failure,bool QuiescenceConfirmed);
internal enum ServiceStartupStage {NotStarted,Authorizing,ValidatingStart,Launching,WaitingForHealth,Ready,Running}
// Operational observations only: no paths, credentials, payloads or exception text.
internal sealed record ServiceStartupObservation(ServiceStartupStage Stage,long ElapsedMilliseconds,string? Failure);
internal sealed record ServiceRootExitObservation(int Code,long ElapsedMilliseconds,bool ReadyObserved,bool ForcedCleanupRequested);

internal sealed class ServiceController
{
    private readonly IServiceStartAuthorizer authorizer;
    private readonly Action<Exception>? reportFailure;
    private readonly TimeSpan startDeadline,lifecycleDeadline;
    private readonly TaskCompletionSource stop=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int terminal;
    // Resources whose cleanup could not be observed stay referenced, so their
    // handles are never released as if the tree were known to be empty.
    private Task<AuthorizedServiceStart>? unresolvedAuthorization;
    private AppLifecycle? unresolvedLifecycle;private AuthorizedServiceStart? unresolvedGrant;
    private ServiceStartupObservation startupObservation=new(ServiceStartupStage.NotStarted,0,null);
    internal ServiceStartupObservation StartupObservation=>Volatile.Read(ref startupObservation);
    // Every accepted observation, in order, so a failure report shows where the
    // admission budget went rather than only the stage it ended in.
    private readonly List<StartupStep> startupSteps=[];
    internal StartupStep[] StartupSteps{get{lock(startupSteps)return [..startupSteps];}}
    private ServiceRootExitObservation? rootExitObservation;
    internal ServiceRootExitObservation? RootExitObservation=>Volatile.Read(ref rootExitObservation);
    internal ServiceController(IServiceStartAuthorizer authorizer,TimeSpan? startDeadline=null,TimeSpan? lifecycleDeadline=null,Action<Exception>? reportFailure=null)
    {
        this.authorizer=authorizer;this.reportFailure=reportFailure;this.startDeadline=startDeadline??(authorizer is IObservedAdmission observed?observed.AdmissionBudget:TimeSpan.FromSeconds(25));
        this.lifecycleDeadline=lifecycleDeadline??TimeSpan.FromSeconds(20);
        if(this.startDeadline<=TimeSpan.Zero||this.startDeadline>(authorizer is IObservedAdmission?TimeSpan.FromMinutes(5):TimeSpan.FromSeconds(30))||this.lifecycleDeadline<=TimeSpan.Zero||this.lifecycleDeadline>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException();
    }
    private void ReportFailure(Exception error){try{reportFailure?.Invoke(error);}catch{}}
    internal uint Control(uint control)
    {
        // Handler never runs cancellation callbacks, waits for a process, does
        // file I/O or performs update/recovery on the SCM dispatcher thread.
        if(control==ServiceDefinition.Interrogate)return 0;
        if(control is not(ServiceDefinition.Stop or ServiceDefinition.Shutdown))return 120;
        if(Volatile.Read(ref terminal)!=0)return 1062;
        stop.TrySetResult();return 0;
    }
    private sealed class TrackedLauncher(IPrivateAppLauncher inner,TimeSpan healthDeadline,Action<ServiceStartupStage,string?> observe,Action<int,bool,bool> observedExit):IPrivateAppLauncher
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int readyObserved,forcedCleanup;
        internal Task<int> Exit=>exit.Task;
        public async Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {
            observe(ServiceStartupStage.Launching,null);
            try{var session=await inner.StartAsync(release,cancellation);_ = Watch(session);return new ObservedSession(session,()=>Interlocked.Exchange(ref forcedCleanup,1));}
            catch(OperationCanceledException){observe(ServiceStartupStage.Launching,"launch_cancelled");throw;}
            catch{observe(ServiceStartupStage.Launching,"launch_failed");throw;}
        }
        private async Task Watch(IContainedAppSession session)
        {try{var code=await session.WaitForRootExitAsync(default);observedExit(code,Volatile.Read(ref readyObserved)!=0,Volatile.Read(ref forcedCleanup)!=0);exit.TrySetResult(code);}catch(Exception e){exit.TrySetException(e);}}
        public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        {
            observe(ServiceStartupStage.WaitingForHealth,null);
            using var health=CancellationTokenSource.CreateLinkedTokenSource(cancellation);health.CancelAfter(healthDeadline);
            try{await inner.WaitForHealthyAsync(((ObservedSession)session).Inner,health.Token).WaitAsync(health.Token);Interlocked.Exchange(ref readyObserved,1);observe(ServiceStartupStage.Ready,null);}
            catch(OperationCanceledException){observe(ServiceStartupStage.WaitingForHealth,!cancellation.IsCancellationRequested&&health.IsCancellationRequested?"readiness_timeout":"readiness_cancelled");throw;}
            catch{observe(ServiceStartupStage.WaitingForHealth,"readiness_failed");throw;}
        }
        private sealed class ObservedSession(IContainedAppSession inner,Action forced):IContainedAppSession
        {
            internal IContainedAppSession Inner=>inner;
            public Task RequestGracefulStopAsync(CancellationToken c)=>inner.RequestGracefulStopAsync(c);
            public Task<int> WaitForRootExitAsync(CancellationToken c)=>inner.WaitForRootExitAsync(c);
            public Task WaitForTreeEmptyAsync(CancellationToken c)=>inner.WaitForTreeEmptyAsync(c);
            public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c){forced();return inner.TerminateAndWaitForTreeEmptyAsync(c);}
            public ValueTask DisposeAsync()=>inner.DisposeAsync();
        }
    }
    private sealed class BoundedStartBarrier(IAppRecoveryBarrier inner):IAppRecoveryBarrier
    {
        // A start barrier is a read-only admission verdict, never a restore or
        // activation executor. Ignored cancellation cannot hold SCM startup open.
        public Task RequireStartAllowedAsync(AppRelease release,CancellationToken cancellation)
            =>inner.RequireStartAllowedAsync(release,cancellation).WaitAsync(cancellation);
    }
    internal async Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish)
    {
        AuthorizedServiceStart? grant=null;AppLifecycle? lifecycle=null;
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        var observationGate=new object();var observationsComplete=false;
        Volatile.Write(ref startupObservation,new(ServiceStartupStage.NotStarted,0,null));Volatile.Write(ref rootExitObservation,null);
        lock(startupSteps)startupSteps.Clear();
        void Observe(ServiceStartupStage stage,string? error)
        {
            // First cause is latched. A late readiness task cannot clear it or
            // publish a false Ready after the controller has already returned.
            lock(observationGate)
            {
                if(observationsComplete||StartupObservation.Failure is not null)return;var observation=new ServiceStartupObservation(stage,elapsed.ElapsedMilliseconds,error);
                Volatile.Write(ref startupObservation,observation);lock(startupSteps)startupSteps.Add(new(stage.ToString(),observation.ElapsedMilliseconds,error));
            }
        }
        // Exit can be observed after unresolved cleanup returns. Record it once,
        // separately from the frozen startup verdict; it is not proof of tree exit.
        void ObservedExit(int code,bool ready,bool forced)=>Interlocked.CompareExchange(ref rootExitObservation,new(code,elapsed.ElapsedMilliseconds,ready,forced),null);
        void Refused(string error){var current=StartupObservation;if(current.Failure is null)Observe(current.Stage,error);}
        var failure=ServiceFailure.None;var quiescent=true;var checkpoint=0u;
        var pending=ServiceState.StartPending;var statusGate=new object();var acceptingProgress=true;var progressStatusFailed=false;
        var observed=authorizer as IObservedAdmission;
        // Startup accepts STOP/SHUTDOWN: the handler cancels it (below), so a
        // long admission never makes the SCM refuse a stop with 1052.
        void Pending(ServiceState state)
        {
            lock(statusGate)
            {
                if(state!=pending){pending=state;checkpoint=0;}
                publish(new(state,state==ServiceState.StartPending?0x1u|0x4u:0,++checkpoint,60000,0,0));
            }
        }
        void Observed()
        {
            lock(statusGate)
            {
                if(!acceptingProgress||pending!=ServiceState.StartPending)return;
                try{Pending(ServiceState.StartPending);}catch{progressStatusFailed=true;throw;}
            }
        }
        try
        {
            Pending(ServiceState.StartPending);if(observed is not null)observed.ObservedWork+=Observed;
            using var start=new CancellationTokenSource(startDeadline);
            // A stop request cancels startup on a pool thread, never inside HandlerEx.
            _=stop.Task.ContinueWith(_=>{try{start.Cancel();}catch(Exception e)when(e is ObjectDisposedException or AggregateException){}},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
            Observe(ServiceStartupStage.Authorizing,null);var authorization=authorizer.AuthorizeAsync(start.Token);
            try{grant=await authorization.WaitAsync(start.Token);}
            catch
            {
                // Revoke late results; an authorizer may pin files, never start processes.
                // Let cooperative cancellation settle briefly. If it has not
                // settled, retain the task: it may still own a probe/reservation.
                if(!authorization.IsCompleted)await Task.WhenAny(authorization,Task.Delay(250));
                if(authorization.IsCompletedSuccessfully)
                {unresolvedAuthorization=authorization;authorization.Result.Dispose();unresolvedAuthorization=null;}
                else if(authorization.IsCompleted)
                {
                    try{authorization.GetAwaiter().GetResult();}
                    catch{_ =authorization.Exception;}
                }
                else{unresolvedAuthorization=authorization;quiescent=false;}
                throw;
            }
            start.Token.ThrowIfCancellationRequested();Pending(ServiceState.StartPending);
            // Readiness belongs to startup. The linked start token still bounds
            // total admission + launch; stop/cleanup retain lifecycleDeadline.
            var tracked=new TrackedLauncher(grant.Launcher,startDeadline,Observe,ObservedExit);
            lifecycle=new(grant.Release,tracked,new BoundedStartBarrier(grant.Recovery),lifecycleDeadline,authorizer is IObservedAdmission?startDeadline:null);
            Observe(ServiceStartupStage.ValidatingStart,null);
            await lifecycle.StartAsync(start.Token);
            start.Token.ThrowIfCancellationRequested();if(stop.Task.IsCompleted)throw new OperationCanceledException();
            start.CancelAfter(Timeout.InfiniteTimeSpan);
            lock(statusGate){acceptingProgress=false;publish(new(ServiceState.Running,0x1|0x4,0,0,0,0));}Observe(ServiceStartupStage.Running,null);
            var completed=await Task.WhenAny(stop.Task,tracked.Exit);
            Pending(ServiceState.StopPending);
            if(completed==tracked.Exit&&!stop.Task.IsCompleted)
            {
                failure=ServiceFailure.UnexpectedExit;try{await tracked.Exit;}catch{}
            }
            else await lifecycle.StopAsync();
        }
        // Each handler latches its fixed failure token before reporting, so the
        // report carries the complete stage history including the verdict.
        catch(ServiceStartDenied denied){if(pending==ServiceState.StartPending)Refused("admission_denied");ReportFailure(denied);failure=denied.Failure;}
        catch(OperationCanceledException error)
        {
            if(pending==ServiceState.StartPending)Refused(stop.Task.IsCompleted?"startup_cancelled":"startup_timeout");
            ReportFailure(error);
            // Expected cancellation before RUNNING differs from a failed stop
            // after RUNNING. Forced cleanup must never turn that timeout into
            // a successful STOPPED status merely because STOP was requested.
            failure=pending==ServiceState.StopPending?ServiceFailure.StopFailed:
                stop.Task.IsCompleted?ServiceFailure.None:ServiceFailure.StartFailed;
        }
        catch(Exception error){if(pending==ServiceState.StartPending)Refused("startup_failed");ReportFailure(error);failure=pending==ServiceState.StopPending?ServiceFailure.StopFailed:ServiceFailure.StartFailed;}
        finally
        {
            lock(statusGate)acceptingProgress=false;
            if(observed is not null)observed.ObservedWork-=Observed;
            if(progressStatusFailed)failure=ServiceFailure.StatusTransport;
            if(lifecycle is not null)
            {
                try{if(pending!=ServiceState.StopPending)Pending(ServiceState.StopPending);}catch{failure=ServiceFailure.StatusTransport;}
                try{await lifecycle.DisposeAsync();}
                catch
                {
                    // Same explicit retry contract as AppLifecycle. Retain all
                    // owned resources if cleanup cannot observe an empty tree.
                    try{await lifecycle.DisposeAsync();}catch{quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}
                }
            }
            if(unresolvedAuthorization is not null){quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}
            if(!quiescent){unresolvedLifecycle=lifecycle;unresolvedGrant=grant;}
            else if(grant is not null)
            {try{grant.Dispose();}catch{unresolvedGrant=grant;quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}}
            Volatile.Write(ref terminal,1);
            lock(observationGate)observationsComplete=true;
        }
        if(quiescent)
        {
            try{publish(new(ServiceState.Stopped,0,0,0,failure==ServiceFailure.None?0u:1066u,(uint)failure));}
            catch(Exception error){ReportFailure(error);return new(ServiceFailure.StatusTransport,true);}
        }
        // Unknown quiescence NEVER emits STOPPED success (or any STOPPED claim).
        // Native entry exits unsuccessfully; OS job-handle teardown is a last
        // containment mechanism, not a verified recovery observation.
        if(!quiescent)ReportFailure(new IOException("RUNTIME_QUIESCENCE_UNCONFIRMED"));
        return new(failure,quiescent);
    }
}

internal sealed class ServiceApplication(IScmApi api,IServiceStartAuthorizer authorizer,Action<int> unsafeExit,Action<Exception,StartupStep[]>? reportFailure=null)
{
    internal int Run()
    {
        var result=new ServiceRunResult(ServiceFailure.StatusTransport,false);
        api.Dispatch(ServiceDefinition.Name,arguments=>
        {
            ServiceController? controller=null;
            controller=new ServiceController(authorizer,reportFailure:reportFailure is null?null:error=>reportFailure(error,controller!.StartupSteps));
            api.Register(ServiceDefinition.Name,controller.Control);
            if(arguments.Length!=1||arguments[0]!=ServiceDefinition.Name)
            {
                api.Publish(new(ServiceState.Stopped,0,0,0,1066,(uint)ServiceFailure.ArgumentsRejected));
                result=new(ServiceFailure.ArgumentsRejected,true);return;
            }
            result=controller.RunAsync(api.Publish).GetAwaiter().GetResult();
            if(!result.QuiescenceConfirmed||result.Failure==ServiceFailure.StatusTransport)unsafeExit(1);
        });
        return result.Failure==ServiceFailure.None&&result.QuiescenceConfirmed?0:1;
    }
}
