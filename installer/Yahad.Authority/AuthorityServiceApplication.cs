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
        IAuthorityRuntimeLifecycle Next()
        {
            var previous=current;var next=MakeRuntime();
            return new TrackedRuntime(next,()=>{current=next;previous.ReleaseStoppedResources();});
        }
        var currentPreparation=new CurrentPreparation(()=>current);
        composition=InstalledAuthorityComposition.Installed(()=>current.RetainedGate,c=>current.StopAsync(c),new LocalChildControlFactory(),Next,
            currentPreparation,currentPreparation,clock);
        var files=composition.Files;var anchor=composition.Anchor;
        var processRoles=files.Roles??throw new IOException("AUTHORITY_NATIVE_ROLES_REQUIRED");
        InstalledProcessObjectAccess.PublishAuthorityPeerAccess(processRoles.Authority.Value,processRoles.Runtime.Value,anchor.OwnerSid);
        InstalledProcessObjectAccess.PublishAuthorityCustodyAccess(processRoles);
        void TrackFullRuntime(WindowsScmRuntimeLifecycle next)
        {
            var previous=current;current=next;
            if(!ReferenceEquals(previous,next)&&previous.OwnsRuntimeResources&&previous.Phase is "stopped" or "offline_recovery_ready")previous.ReleaseStoppedResources();
        }
        using var full=anchor.FullSystemPolicyDigest is null?null:FullSystemAuthorityApplication.Create(composition,()=>current,MakeRuntime,TrackFullRuntime,clock);
        async Task CompleteFullRequest(string operation,string outcome,CancellationToken token)
        {
            await composition.Router.CompleteFullSystemRequestAsync(operation,outcome,token);
            if(outcome=="healthy")_=await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(files,anchor,operation,token);
        }
        async Task<RetainedRuntimeQuiescence> Offline(InstallationBinding recoveryBinding,AppRelease release,CancellationToken token)
        {
            // An existing healthy runtime still needs its actual app idle fence.
            // A new waiting host is never used to excuse an occupied old runtime.
            if(current.Phase=="healthy")
            {
                var id=Guid.NewGuid().ToString("N");var stopped=false;
                try
                {
                    if(!await current.PrepareUpdateAsync(id,token))throw new IOException("OWNER_RECOVERY_RUNTIME_BUSY");
                    await current.StopAsync(token);stopped=true;
                }
                finally
                {
                    if(!stopped&&current.Phase=="healthy")
                    {using var undo=new CancellationTokenSource(TimeSpan.FromSeconds(5));await current.CancelUpdateAsync(id,undo.Token);}
                }
            }
            else if(current.CleanupReconciliationRequired&&current.OwnsRuntimeResources)
                throw new IOException("OWNER_RECOVERY_OLD_OWNERSHIP_UNCONFIRMED");
            var previous=current;var offline=MakeRuntime();await offline.PrepareOfflineRecoveryAsync(recoveryBinding,release,token);current=offline;
            var observation=offline.AcquireRecoveryQuiescence();
            try{if(previous.OwnsRuntimeResources)previous.ReleaseStoppedResources();return observation;}
            catch{observation.Dispose();throw;}
        }
        if(full is null)composition.Router.ConfigureRecovery(new OwnerRecoveryEndpoint(files,anchor,composition.Ledger,
            Offline,()=>current.RetainedGate,Next,new LocalChildControlFactory(),clock));
        else composition.Router.ConfigureRecovery(new FullSystemOwnerRecoveryEndpoint(files,anchor,composition.Ledger,
            full.Kernel.PendingOperation,full.Coordinator.ResumeAsync,()=>full.Coordinator.Phase,CompleteFullRequest,clock));
        Task[] workers=[];var running=false;
        using var workStop=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        using var intake=new NativeCandidateIntake(files,anchor,clock);
        try
        {
            // A bounded wait hint is not invented progress. Do not advance SCM
            // checkpoints merely because time passes while admission is pending.
            api.Publish(new(ServiceState.StartPending,0,1,300000,0,0));
            var boot=FullSystemBootDisposition.NoPending;
            if(full is not null)boot=await full.BootBeforeRuntimeAsync(cancellation);
            else try{InstalledUpdateTransaction.RequireNoUnfinished(files,anchor);}catch(IOException){composition.Router.MarkBootRecoveryRequired();}
            if(boot==FullSystemBootDisposition.Healthy&&full?.Coordinator.CurrentOperationId is {} recoveredOperation)
                _=await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(files,anchor,recoveredOperation,cancellation);
            if(boot==FullSystemBootDisposition.NoPending&&composition.Router.Phase!="recovery_required")
            {
                // Recovery runs before ordinary admission: an interrupted
                // five-record publication may temporarily have mixed records.
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
                Func<CancellationToken,Task<ScheduledUpdateCandidate?>> checkSource=full is not null?full.CheckSourceAsync:async c=>
                {_=NativeUpdateEnrollmentReader.Require(files,anchor);var candidate=await intake.CheckAndPublishAsync(c);return candidate is null?null:new(candidate.Target);};
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
            catch{composition.Router.MarkBootRecoveryRequired();} // Keep native owner/status available; never erase partial state.
            InstallerReadiness.Publish(files,anchor,current);
            var listeners=new List<Task>{ServeAsync(composition,AuthorityEndpointRole.Runtime,workStop.Token),ServeAsync(composition,AuthorityEndpointRole.Owner,workStop.Token)};
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
    private static async Task ServeAsync(InstalledAuthorityComposition composition,AuthorityEndpointRole role,CancellationToken cancellation)
    {
        while(!cancellation.IsCancellationRequested)
        {
            try{await WindowsAuthorityEndpoint.ServeOnceAsync(composition,role,cancellation);}
            catch(Exception error)when(error is IOException or UnauthorizedAccessException)
            {await Task.Delay(100,cancellation);} // Bound invalid-client retries; native listener collisions fail the worker.
        }
    }
    private sealed class CurrentPreparation(Func<WindowsScmRuntimeLifecycle> current):IAuthorityRuntimeUpdatePreparation,IAuthorityRuntimeRecoveryObservation
    {
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken c)=>current().PrepareUpdateAsync(id,c);
        public Task CancelUpdateAsync(string id,CancellationToken c)=>current().CancelUpdateAsync(id,c);
        public RetainedRuntimeQuiescence AcquireRecoveryQuiescence()
            =>current().AcquireRecoveryQuiescence();
    }
    private sealed class TrackedRuntime(WindowsScmRuntimeLifecycle runtime,Action started):IAuthorityRuntimeLifecycle,IAuthorityRuntimeUpdatePreparation
    {
        public string Phase=>runtime.Phase;public bool CleanupReconciliationRequired=>runtime.CleanupReconciliationRequired;
        public async Task StartCommittedAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default)
        {await runtime.StartCommittedAsync(binding,expected,cancellation);started();}
        public Task StopAsync(CancellationToken c=default)=>runtime.StopAsync(c);
        public Task RetryCleanupAsync(CancellationToken c=default)=>runtime.RetryCleanupAsync(c);
        public Task<bool> PrepareUpdateAsync(string id,CancellationToken c)=>runtime.PrepareUpdateAsync(id,c);
        public Task CancelUpdateAsync(string id,CancellationToken c)=>runtime.CancelUpdateAsync(id,c);
    }
}
