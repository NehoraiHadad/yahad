using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Concrete installed SCM composition, deliberately not called by Program. This
// requires the combined installer-owned dual-service layout and enrollment;
// missing enrollment never falls back to an application-controlled source.
internal static class AuthorityServiceApplication
{
    internal static int RunInstalled()=>Run(new NativeScmApi(),Environment.Exit,error=>ServiceStartupDiagnostic.TryRecord(StartupDiagnosticRole.Authority,error));
    internal static int Run(IScmApi api,Action<int> unsafeExit,Action<Exception>? reportFailure=null)
    {
        var success=false;
        api.Dispatch(InstalledAuthorityReader.AuthorityService,arguments=>
        {
            using var stop=new CancellationTokenSource();
            api.Register(InstalledAuthorityReader.AuthorityService,control=>
            {
                if(control is ServiceDefinition.Stop or ServiceDefinition.Shutdown)
                {_ =Task.Run(()=>{try{stop.Cancel();}catch(ObjectDisposedException){}});return 0;}return control==ServiceDefinition.Interrogate?0u:120u;
            });
            if(arguments.Length!=1||arguments[0]!=InstalledAuthorityReader.AuthorityService)
            {api.Publish(new(ServiceState.Stopped,0,0,0,1066,(uint)ServiceFailure.ArgumentsRejected));return;}
            try
            {
                api.Publish(new(ServiceState.StartPending,0,1,60000,0,0));
                success=RunHostedAsync(api,stop.Token).GetAwaiter().GetResult();
                if(success)api.Publish(new(ServiceState.Stopped,0,0,0,0,0));else{Report(new IOException("AUTHORITY_HOST_NOT_HEALTHY"));unsafeExit(1);}
            }
            catch(Exception error){Report(error);unsafeExit(1);}
        });
        return success?0:1;
        void Report(Exception error){try{reportFailure?.Invoke(error);}catch{}}
    }
    private static async Task<bool> RunHostedAsync(IScmApi api,CancellationToken cancellation)
    {
        var runtimes=new List<WindowsScmRuntimeLifecycle>();
        WindowsScmRuntimeLifecycle MakeRuntime(){var value=new WindowsScmRuntimeLifecycle(()=>new NativeScmRuntimeSession());runtimes.Add(value);return value;}
        var current=MakeRuntime();InstalledAuthorityComposition? composition=null;
        var clock=()=>DateTimeOffset.UtcNow;
        composition=InstalledAuthorityComposition.Installed();
        var files=composition.Files;var anchor=composition.Anchor;
        var processRoles=files.Roles??throw new IOException("AUTHORITY_NATIVE_ROLES_REQUIRED");
        InstalledProcessObjectAccess.PublishAuthorityPeerAccess(processRoles.Authority.Value,processRoles.Runtime.Value);
        InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(processRoles);
        void TrackFullRuntime(WindowsScmRuntimeLifecycle next)
        {
            var previous=current;current=next;
            if(!ReferenceEquals(previous,next)&&previous.OwnsRuntimeResources&&previous.Phase is "stopped" or "offline_recovery_ready")previous.ReleaseStoppedResources();
        }
        using var full=FullSystemAuthorityApplication.Create(composition,()=>current,MakeRuntime,TrackFullRuntime,clock);
        Task[] workers=[];var running=false;
        using var workStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        try
        {
            // A bounded wait hint is not invented progress. Do not advance SCM
            // checkpoints merely because time passes while admission is pending.
            api.Publish(new(ServiceState.StartPending,0,1,300000,0,0));
            var boot=await full.BootBeforeRuntimeAsync(cancellation);
            if(boot==FullSystemBootDisposition.Healthy&&full.Coordinator.CurrentOperationId is {} recoveredOperation)
                _=await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(files,anchor,recoveredOperation,cancellation);
            if(boot==FullSystemBootDisposition.NoPending&&composition.Router.Phase!="recovery_required")
            {
                // Recovery runs before ordinary admission: an interrupted
                // four-record publication may temporarily have mixed records.
                var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
                var bytes=files.Read(files.PathOf("activation.json"),16384);var activation=NativeJson.Read<InstalledActivation>(bytes);
                if(binding.InstallationId!=anchor.InstallationId||activation.InstallationId!=anchor.InstallationId||InstalledActivationResolver.Hash(bytes)!=binding.ActivationReceiptDigest||activation.Phase!="committed")
                    throw new IOException("AUTHORITY_START_COMMITTED_BINDING");
                try{await current.StartCommittedAsync(binding,new(activation.ReleaseDigest),cancellation);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){throw;}
                catch{composition.Router.MarkBootRecoveryRequired();}
            }
            cancellation.ThrowIfCancellationRequested();
            // Recovery/status stays available when the candidate cannot start.
            // Every download checks protected enrollment again, including retry.
            InstalledUpdateScheduler? scheduler=null;
            try
            {
                Func<CancellationToken,Task<ScheduledUpdateCandidate?>> checkSource=full.CheckSourceAsync;
                scheduler=new InstalledUpdateScheduler(files,checkSource,
                    composition.Router.CheckScheduledAsync,()=>composition.Router.Execution,()=>composition.Router.ActivationPhase,clock,()=>composition.Router.CandidateStartIssued);
                composition.Router.ConfigureDeferral(scheduler.DeferUntil);
                composition.Router.ConfigureSourceCheck(async(id,cancellation)=>
                {
                    var requested=new InstalledUpdateScheduler(files,checkSource,
                        c=>composition.Router.CheckScheduledAsync(c,id),()=>composition.Router.Execution,()=>composition.Router.ActivationPhase,clock,()=>composition.Router.CandidateStartIssued);
                    await requested.RunOnceAsync(true,cancellation);
                    await composition.Router.CompleteSourceCheckAsync(id,requested.Status.Error);
                });
            }
            catch{composition.Router.MarkBootRecoveryRequired();} // Keep native status available; never erase partial state.
            InstallerReadiness.Publish(files,anchor,current);
            var listeners=new List<Task>{ServeAsync(composition,workStop.Token)};
            if(scheduler is not null)listeners.Add(scheduler.RunAsync(workStop.Token));workers=listeners.ToArray();
            api.Publish(new(ServiceState.Running,0x1|0x4,0,0,0,0));running=true;
            var cancelled=Task.Delay(Timeout.Infinite,cancellation);var ended=await Task.WhenAny(workers.Append(cancelled));
            if(ended!=cancelled){running=false;await ended;} // A stopped/faulted worker is not a healthy Authority.
        }
        catch(OperationCanceledException)when(cancellation.IsCancellationRequested){}
        finally
        {
            workStop.Cancel();api.Publish(new(ServiceState.StopPending,0,1,30000,0,0));
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try{await Task.WhenAll(workers).WaitAsync(cleanup.Token);}catch(OperationCanceledException)when(workStop.IsCancellationRequested&&!cleanup.IsCancellationRequested){}catch{running=false;}
            try{await composition.Router.StopAsync(cleanup.Token);}catch{running=false;}
            foreach(var runtime in runtimes)
            {
                try
                {
                    if(runtime.CleanupReconciliationRequired)await runtime.RetryCleanupAsync(cleanup.Token);
                    else if(runtime.Phase is "healthy" or "starting_runtime")await runtime.StopAsync(cleanup.Token);
                    if(runtime.OwnsRuntimeResources)runtime.ReleaseStoppedResources();
                }
                catch{running=false;}
            }
        }
        return running&&runtimes.All(r=>!r.OwnsRuntimeResources);
    }
    private static async Task ServeAsync(InstalledAuthorityComposition composition,CancellationToken cancellation)
    {
        while(!cancellation.IsCancellationRequested)
        {
            try{await WindowsAuthorityEndpoint.ServeOnceAsync(composition,cancellation);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {await Task.Delay(100,cancellation);} // Bound invalid-client retries; native listener collisions fail the worker.
        }
    }
}
