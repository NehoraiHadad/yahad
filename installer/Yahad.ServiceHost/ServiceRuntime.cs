using Yahad.Install;

namespace Yahad.Service;

internal static class ServiceDefinition
{
    internal const string Name="Yahad.Acceptance";
    internal const string Account="NT SERVICE\\Yahad.Acceptance";
    internal const uint OwnProcess=0x10,Stop=1,Interrogate=4,Shutdown=5;
}
internal enum ServiceState:uint {Stopped=1,StartPending=2,StopPending=3,Running=4}
internal enum ServiceFailure:uint {None=0,IdentityDenied=1001,BootstrapUnavailable=1002,ActivationUnavailable=1003,
    StartFailed=1004,UnexpectedExit=1005,StopFailed=1006,QuiescenceUnknown=1007,ArgumentsRejected=1008,StatusTransport=1009}
internal sealed class ServiceStartDenied(ServiceFailure failure):Exception
{internal ServiceFailure Failure=>failure;}
internal readonly record struct ServiceReport(ServiceState State,uint AcceptedControls,uint Checkpoint,uint WaitHint,
    uint Win32ExitCode,uint ServiceSpecificExitCode);
internal interface IScmApi
{
    void Dispatch(string name,Action<string[]> main);
    void Register(string name,Func<uint,uint> handler);
    void Publish(ServiceReport report);
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
internal sealed class NoHostUpdateAuthority:IAppUpdateAuthority
{
    public Task<AppRelease> ActivateAsync(AppRelease requested,QuiescentApp grant,CancellationToken cancellation)
        =>throw new UnauthorizedAccessException("HOST_HAS_NO_UPDATE_AUTHORITY");
}
internal readonly record struct ServiceRunResult(ServiceFailure Failure,bool QuiescenceConfirmed);

internal sealed class ServiceController
{
    private readonly IServiceStartAuthorizer authorizer;
    private readonly TimeSpan startDeadline,lifecycleDeadline;
    private readonly TaskCompletionSource stop=new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int terminal;
    internal ServiceController(IServiceStartAuthorizer authorizer,TimeSpan? startDeadline=null,TimeSpan? lifecycleDeadline=null)
    {
        this.authorizer=authorizer;this.startDeadline=startDeadline??TimeSpan.FromSeconds(25);
        this.lifecycleDeadline=lifecycleDeadline??TimeSpan.FromSeconds(20);
        if(this.startDeadline<=TimeSpan.Zero||this.startDeadline>TimeSpan.FromSeconds(30)||this.lifecycleDeadline<=TimeSpan.Zero||this.lifecycleDeadline>TimeSpan.FromSeconds(30))throw new ArgumentOutOfRangeException();
    }
    internal uint Control(uint control)
    {
        // Handler never runs cancellation callbacks, waits for a process, does
        // file I/O or performs update/recovery on the SCM dispatcher thread.
        if(control==ServiceDefinition.Interrogate)return 0;
        if(control is not(ServiceDefinition.Stop or ServiceDefinition.Shutdown))return 120;
        if(Volatile.Read(ref terminal)!=0)return 1062;
        stop.TrySetResult();return 0;
    }
    private sealed class TrackedLauncher(IPrivateAppLauncher inner):IPrivateAppLauncher
    {
        private readonly TaskCompletionSource<int> exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task<int> Exit=>exit.Task;
        public async Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken cancellation)
        {
            var session=await inner.StartAsync(release,cancellation);_ = Watch(session);return session;
        }
        private async Task Watch(IContainedAppSession session)
        {try{exit.TrySetResult(await session.WaitForRootExitAsync(default));}catch(Exception e){exit.TrySetException(e);}}
        public Task WaitForHealthyAsync(IContainedAppSession session,CancellationToken cancellation)=>inner.WaitForHealthyAsync(session,cancellation);
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
        var failure=ServiceFailure.None;var quiescent=true;var checkpoint=0u;
        var pending=ServiceState.StartPending;
        void Pending(ServiceState state)
        {
            if(state!=pending){pending=state;checkpoint=0;}
            publish(new(state,0,++checkpoint,60000,0,0));
        }
        try
        {
            Pending(ServiceState.StartPending);
            using var start=new CancellationTokenSource(startDeadline);
            // A stop request cancels startup on a pool thread, never inside HandlerEx.
            _=stop.Task.ContinueWith(_=>{try{start.Cancel();}catch(Exception e)when(e is ObjectDisposedException or AggregateException){}},CancellationToken.None,TaskContinuationOptions.None,TaskScheduler.Default);
            var authorization=authorizer.AuthorizeAsync(start.Token);
            try{grant=await authorization.WaitAsync(start.Token);}
            catch
            {
                // Revoke late results; an authorizer may pin files, never start processes.
                _=authorization.ContinueWith(t=>{if(t.Status==TaskStatus.RanToCompletion)t.Result.Dispose();else _=t.Exception;},TaskScheduler.Default);
                throw;
            }
            start.Token.ThrowIfCancellationRequested();Pending(ServiceState.StartPending);
            var tracked=new TrackedLauncher(grant.Launcher);
            lifecycle=new(grant.Release,tracked,new BoundedStartBarrier(grant.Recovery),new NoHostUpdateAuthority(),lifecycleDeadline);
            await lifecycle.StartAsync(start.Token);
            start.Token.ThrowIfCancellationRequested();if(stop.Task.IsCompleted)throw new OperationCanceledException();
            start.CancelAfter(Timeout.InfiniteTimeSpan);
            publish(new(ServiceState.Running,0x1|0x4,0,0,0,0));
            var completed=await Task.WhenAny(stop.Task,tracked.Exit);
            Pending(ServiceState.StopPending);
            if(completed==tracked.Exit&&!stop.Task.IsCompleted)
            {failure=ServiceFailure.UnexpectedExit;try{await tracked.Exit;}catch{}}
            else await lifecycle.StopAsync();
        }
        catch(ServiceStartDenied denied){failure=denied.Failure;}
        catch(OperationCanceledException)
        {
            // Expected cancellation before RUNNING differs from a failed stop
            // after RUNNING. Forced cleanup must never turn that timeout into
            // a successful STOPPED status merely because STOP was requested.
            failure=pending==ServiceState.StopPending?ServiceFailure.StopFailed:
                stop.Task.IsCompleted?ServiceFailure.None:ServiceFailure.StartFailed;
        }
        catch{failure=pending==ServiceState.StopPending?ServiceFailure.StopFailed:ServiceFailure.StartFailed;}
        finally
        {
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
            if(quiescent)grant?.Dispose();
            Volatile.Write(ref terminal,1);
        }
        if(quiescent)
        {
            try{publish(new(ServiceState.Stopped,0,0,0,failure==ServiceFailure.None?0u:1066u,(uint)failure));}
            catch{return new(ServiceFailure.StatusTransport,true);}
        }
        // Unknown quiescence NEVER emits STOPPED success (or any STOPPED claim).
        // Native entry exits unsuccessfully; OS job-handle teardown is a last
        // containment mechanism, not a verified recovery observation.
        return new(failure,quiescent);
    }
}

internal sealed class ServiceApplication(IScmApi api,IServiceStartAuthorizer authorizer,Action<int> unsafeExit)
{
    internal int Run()
    {
        var result=new ServiceRunResult(ServiceFailure.StatusTransport,false);
        api.Dispatch(ServiceDefinition.Name,arguments=>
        {
            var controller=new ServiceController(authorizer);
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
