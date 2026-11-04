using Yahad.Install;

namespace Yahad.Service;

internal enum ServiceFailure:uint {None=0,ActivationUnavailable=1003,
    StartFailed=1004,UnexpectedExit=1005,StopFailed=1006,QuiescenceUnknown=1007,ArgumentsRejected=1008,StatusTransport=1009}
internal interface IServiceStartAuthorizer
{Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation);}

// What one admitted start owns: its launcher and the resources held until it stops.
internal sealed class AuthorizedServiceStart(IPrivateAppLauncher launcher,IDisposable? resources=null):IDisposable
{
    internal IPrivateAppLauncher Launcher=>launcher;
    public void Dispose()=>resources?.Dispose();
}
internal readonly record struct ServiceRunResult(ServiceFailure Failure,bool QuiescenceConfirmed);

// One SCM service: the control handler and the dispatch both services share.
internal abstract class ScmController
{
    protected readonly TaskCompletionSource stop=new(TaskCreationOptions.RunContinuationsAsynchronously);
    protected int terminal;
    internal uint Control(uint control)
    {
        // Handler never runs cancellation callbacks, waits for a process, does
        // file I/O or performs an update on the SCM dispatcher thread.
        if(control==ServiceDefinition.Interrogate)return 0;
        if(control is not(ServiceDefinition.Stop or ServiceDefinition.Shutdown))return 120;
        if(Volatile.Read(ref terminal)!=0)return 1062;
        stop.TrySetResult();return 0;
    }
    internal abstract Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish);
    internal static int Dispatch(IScmApi api,string name,Action<int> unsafeExit,Func<ScmController> create)
    {
        var result=new ServiceRunResult(ServiceFailure.StatusTransport,false);
        api.Dispatch(name,arguments=>
        {
            var controller=create();
            api.Register(name,controller.Control);
            if(arguments.Length!=1||arguments[0]!=name)
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

internal sealed class ServiceController(IServiceStartAuthorizer authorizer,TimeSpan? startDeadline=null,TimeSpan? lifecycleDeadline=null,Action<Exception>? reportFailure=null):ScmController
{
    private readonly TimeSpan startBudget=startDeadline??TimeSpan.FromSeconds(25),stopBudget=lifecycleDeadline??TimeSpan.FromSeconds(20);
    // Every accepted stage in order, so a failure report shows where the
    // admission budget went rather than only the stage it ended in.
    private readonly List<StartupStep> startupSteps=[];
    private bool stepsComplete;
    internal StartupStep[] StartupSteps{get{lock(startupSteps)return [..startupSteps];}}
    private void ReportFailure(Exception error){try{reportFailure?.Invoke(error);}catch{}}
    private sealed class TrackedLauncher(IPrivateAppLauncher inner,Action<string,string?> observe):IPrivateAppLauncher
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<int> Exit=>exit.Task;
        public async Task<IContainedAppSession> StartAsync(CancellationToken cancellation)
        {
            observe("Launching",null);
            try{var session=await inner.StartAsync(cancellation);_ = Watch(session);return session;}
            catch(OperationCanceledException){observe("Launching","launch_cancelled");throw;}
            catch{observe("Launching","launch_failed");throw;}
        }
        private async Task Watch(IContainedAppSession session)
        {try{exit.TrySetResult(await session.WaitForRootExitAsync(default));}catch(Exception e){exit.TrySetException(e);}}
        public async Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)
        {
            observe("WaitingForHealth",null);
            try{await inner.WaitForHealthyAsync(session,cancellation).WaitAsync(cancellation);observe("Ready",null);}
            catch(OperationCanceledException){observe("WaitingForHealth","readiness_cancelled");throw;}
            catch{observe("WaitingForHealth","readiness_failed");throw;}
        }
    }
    internal override async Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish)
    {
        AuthorizedServiceStart? grant=null;AppLifecycle? lifecycle=null;
        var elapsed=System.Diagnostics.Stopwatch.StartNew();
        void Observe(string stage,string? error)
        {
            // First cause is latched. A late readiness task cannot clear it or
            // publish a false Ready after the controller has already returned.
            lock(startupSteps)
            {
                if(stepsComplete||startupSteps.LastOrDefault()?.Failure is not null)return;
                startupSteps.Add(new(stage,elapsed.ElapsedMilliseconds,error));
            }
        }
        void Refused(string error)=>Observe(StartupSteps.LastOrDefault()?.Stage??"NotStarted",error);
        var failure=ServiceFailure.None;var quiescent=true;var authorizationUnresolved=false;var checkpoint=0u;
        var pending=ServiceState.StartPending;
        // Startup accepts STOP/SHUTDOWN: the handler cancels it (below), so a
        // long admission never makes the SCM refuse a stop with 1052.
        void Pending(ServiceState state)
        {
            if(state!=pending){pending=state;checkpoint=0;}
            publish(new(state,state==ServiceState.StartPending?0x1u|0x4u:0,++checkpoint,60000,0,0));
        }
        try
        {
            Pending(ServiceState.StartPending);
            using var start=new CancellationTokenSource(startBudget);
            // A stop request cancels startup on a pool thread, never inside HandlerEx.
            _=stop.Task.ContinueWith(_=>{try{start.Cancel();}catch(Exception e)when(e is ObjectDisposedException or AggregateException){}},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
            Observe("Authorizing",null);var authorization=authorizer.AuthorizeAsync(start.Token);
            try{grant=await authorization.WaitAsync(start.Token);}
            catch
            {
                // Revoke late results; an authorizer may pin files, never start processes.
                // Let cooperative cancellation settle briefly. If it has not
                // settled, it may still own the data lease.
                if(!authorization.IsCompleted)await Task.WhenAny(authorization,Task.Delay(250));
                if(authorization.IsCompletedSuccessfully)
                {authorizationUnresolved=true;authorization.Result.Dispose();authorizationUnresolved=false;}
                else if(authorization.IsCompleted)
                {
                    try{authorization.GetAwaiter().GetResult();}
                    catch{_ =authorization.Exception;}
                }
                else authorizationUnresolved=true;
                throw;
            }
            start.Token.ThrowIfCancellationRequested();Pending(ServiceState.StartPending);
            // Readiness belongs to startup. The linked start token still bounds
            // total admission + launch; stop/cleanup retain their own budget.
            var tracked=new TrackedLauncher(grant.Launcher,Observe);
            lifecycle=new(tracked,stopBudget,startBudget);
            await lifecycle.StartAsync(start.Token);
            start.Token.ThrowIfCancellationRequested();if(stop.Task.IsCompleted)throw new OperationCanceledException();
            start.CancelAfter(Timeout.InfiniteTimeSpan);
            publish(new(ServiceState.Running,0x1|0x4,0,0,0,0));Observe("Running",null);
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
            if(lifecycle is not null)
            {
                try{if(pending!=ServiceState.StopPending)Pending(ServiceState.StopPending);}catch{failure=ServiceFailure.StatusTransport;}
                try{await lifecycle.DisposeAsync();}
                catch
                {
                    // Same explicit retry contract as AppLifecycle. Unknown cleanup
                    // keeps the grant: the host exits with the lease still held.
                    try{await lifecycle.DisposeAsync();}catch{quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}
                }
            }
            if(authorizationUnresolved){quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}
            if(quiescent&&grant is not null)
            {try{grant.Dispose();}catch{quiescent=false;failure=ServiceFailure.QuiescenceUnknown;}}
            Volatile.Write(ref terminal,1);
            lock(startupSteps)stepsComplete=true;
        }
        if(quiescent)
        {
            try{publish(new(ServiceState.Stopped,0,0,0,failure==ServiceFailure.None?0u:1066u,(uint)failure));}
            catch(Exception error){ReportFailure(error);return new(ServiceFailure.StatusTransport,true);}
        }
        // Unknown quiescence NEVER emits STOPPED success (or any STOPPED claim).
        // Native entry exits unsuccessfully; OS job-handle teardown is a last
        // containment mechanism, not a verified stop.
        if(!quiescent)ReportFailure(new IOException("RUNTIME_QUIESCENCE_UNCONFIRMED"));
        return new(failure,quiescent);
    }
}
