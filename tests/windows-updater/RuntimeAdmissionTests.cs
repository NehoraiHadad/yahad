using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

internal static class RuntimeAdmissionTests
{
    private static readonly InstallationBinding Binding=new(new string('a',32),new string('b',64));
    private static readonly AppRelease Release=new(new string('c',64));
    private static readonly string Nonce=new('d',32);
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("SCM admission assigns exact waiting generation before launch and readiness",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);
            Assert(life.Phase=="healthy"&&session.Order.SequenceEqual(new[]{"start","authenticate","assign","authenticate","launch","ready","authenticate"}));
            await Denied(()=>life.StartCommittedAsync(Binding,Release));await life.StopAsync();life.RetainedGate.RequireQuiescence();Assert(!session.Disposed);
            life.ReleaseStoppedResources();Assert(session.Disposed&&!life.OwnsRuntimeResources);
        });
        await check("offline recovery authenticates and assigns a real waiting-generation contract without launch",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.PrepareOfflineRecoveryAsync(Binding,Release);
            Assert(life.Phase=="offline_recovery_ready"&&session.RecoveryRetains==1&&session.Exited&&!session.Released&&!session.Disposed);
            Assert(session.Order.SequenceEqual(new[]{"start","authenticate","assign","authenticate","retain-recovery","stop"}));
            life.RetainedGate.RequireQuiescence();await Denied(()=>life.StartCommittedAsync(Binding,Release));
            await Denied(()=>life.PrepareOfflineRecoveryAsync(Binding,Release));life.ReleaseStoppedResources();Assert(session.Disposed);
        });
        foreach(var mode in new[]{"binding","assign","retain_recovery"})await check("offline recovery refuses "+mode+" and never releases payload launch",async()=>
        {
            var session=new Session{Fault=mode};var life=Lifecycle(session);await Denied(()=>life.PrepareOfflineRecoveryAsync(Binding,Release));
            Assert(!session.Released&&session.Disposed&&life.Phase=="failed");
        });
        await check("offline recovery retains exact waiting owner when descendant quiescence is uncertain",async()=>
        {
            var session=new Session{CleanupUnavailable=true};var life=Lifecycle(session);await Denied(()=>life.PrepareOfflineRecoveryAsync(Binding,Release));
            Assert(life.CleanupReconciliationRequired&&session.RecoveryRetains==1&&!session.Released&&!session.Disposed);
            session.CleanupUnavailable=false;await life.RetryCleanupAsync();life.ReleaseStoppedResources();Assert(session.Disposed);
        });
        foreach(var mode in new[]{"nonce","binding","release","pid","created","assign","changed_after_assign","ready","identity"})await check("SCM admission rejects "+mode+" without accepting health",async()=>
        {
            var session=new Session{Fault=mode};var life=Lifecycle(session);await Denied(()=>life.StartCommittedAsync(Binding,Release));
            Assert(life.Phase=="failed"&&session.Disposed&&!life.OwnsRuntimeResources);
            if(mode!="ready")Assert(!session.Released);
        });
        await check("SCM startup failure retains exact owner until observed cleanup",async()=>
        {
            var session=new Session{Fault="start",CleanupUnavailable=true};var life=Lifecycle(session);
            await Denied(()=>life.StartCommittedAsync(Binding,Release));Assert(life.CleanupReconciliationRequired&&life.OwnsRuntimeResources&&!session.Disposed);
            await Denied(()=>life.RetryCleanupAsync());Assert(life.OwnsRuntimeResources&&!session.Disposed);
            session.CleanupUnavailable=false;await life.RetryCleanupAsync();Assert(!life.OwnsRuntimeResources&&session.Disposed&&life.Phase=="failed");
        });
        await check("SCM descendant occupancy prevents success even after root exit",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);
            session.CleanupUnavailable=true;await Denied(()=>life.StopAsync());Assert(life.CleanupReconciliationRequired&&!session.Disposed);
            session.CleanupUnavailable=false;await life.RetryCleanupAsync();life.ReleaseStoppedResources();Assert(session.Disposed);
        });
        await check("SCM stop of a Runtime already STOP_PENDING still stops and proves quiescence",async()=>
        {
            // Setup's removal STOP (or SCM SHUTDOWN) reached the Runtime first:
            // its generation is no longer RUNNING, so it is not current.
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);session.NotCurrent=true;
            await life.StopAsync();Assert(life.Phase=="stopped"&&session.Order[^1]=="stop"&&session.Exited);
            life.RetainedGate.RequireQuiescence();life.ReleaseStoppedResources();Assert(session.Disposed&&!life.OwnsRuntimeResources);
        });
        await check("SCM cancelled readiness cannot become healthy or release cleanup ownership",async()=>
        {
            var session=new Session{Fault="wait_ready"};var life=Lifecycle(session);using var cancellation=new CancellationTokenSource();
            var start=life.StartCommittedAsync(Binding,Release,cancellation.Token);cancellation.Cancel();
            try{await start;throw new Exception("CANCELLED_READY_ACCEPTED");}catch(OperationCanceledException){}
            Assert(life.Phase=="cancelled"&&session.Disposed&&!life.OwnsRuntimeResources);
        });
        await check("SCM spontaneous exit revokes healthy status and retains owner for cleanup",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);session.NaturalExit();
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));while(life.Phase=="healthy")await Task.Delay(1,deadline.Token);
            Assert(life.CleanupReconciliationRequired&&!session.Disposed);await life.RetryCleanupAsync();Assert(session.Disposed);
        });
        await check("SCM preparation forwards exact admitted generation and busy never stops runtime",async()=>
        {
            var session=new Session();var life=Lifecycle(session);await life.StartCommittedAsync(Binding,Release);
            Assert(!await life.PrepareUpdateAsync(Nonce,default)&&session.IdleRequests==1&&!session.Exited);
            session.Idle=true;Assert(await life.PrepareUpdateAsync(Nonce,default));await life.CancelUpdateAsync(Nonce,default);
            Assert(session.IdleCancelled&&!session.Exited);await life.StopAsync();life.ReleaseStoppedResources();
        });
        await check("native admission frame roundtrip is bounded and rejects duplicate fields",async()=>
        {
            using var stream=new MemoryStream();var message=new RuntimeAdmissionMessage(1,"hello",Nonce,Binding:Binding,Host:new(55,66));
            await RuntimeAdmissionWire.WriteAsync(stream,message,default);stream.Position=0;Assert(await RuntimeAdmissionWire.ReadAsync(stream,default)==message);
            using var oversized=new MemoryStream(BitConverter.GetBytes(32769));await Denied(()=>RuntimeAdmissionWire.ReadAsync(oversized,default));
            var bytes=System.Text.Encoding.UTF8.GetBytes("{\"Format\":1,\"Format\":1,\"Kind\":\"hello\",\"Id\":\""+Nonce+"\"}");
            using var duplicate=new MemoryStream();duplicate.Write(BitConverter.GetBytes(bytes.Length));duplicate.Write(bytes);duplicate.Position=0;
            await Denied(()=>RuntimeAdmissionWire.ReadAsync(duplicate,default));
        });
        foreach(var delayed in new[]{"update-idle","update-busy"})await check("native cancel drains one exact delayed "+delayed+" before cancel acknowledgement",async()=>
        {
            using var stream=new MemoryStream();var admission=new RuntimeAdmissionBinding(Binding,Release.Id,Nonce,55,66);
            await RuntimeAdmissionWire.WriteAsync(stream,new(1,delayed,Nonce,admission),default);
            await RuntimeAdmissionWire.WriteAsync(stream,new(1,"update-cancelled",Nonce,admission),default);stream.Position=0;var observations=0;
            var reply=await RuntimeAdmissionWire.ReadControlReplyAsync(stream,admission,Nonce,true,()=>observations++,default);
            Assert(reply.Kind=="update-cancelled"&&observations==2);
        });
        foreach(var changed in new[]{"id","generation","duplicate"})await check("native cancel refuses to drain "+changed+" mismatch",async()=>
        {
            using var stream=new MemoryStream();var admission=new RuntimeAdmissionBinding(Binding,Release.Id,Nonce,55,66);
            await RuntimeAdmissionWire.WriteAsync(stream,new(1,"update-idle",changed=="id"?new string('e',32):Nonce,changed=="generation"?admission with{Created=67}:admission),default);
            await RuntimeAdmissionWire.WriteAsync(stream,new(1,changed=="duplicate"?"update-idle":"update-cancelled",Nonce,admission),default);stream.Position=0;
            await Denied(()=>RuntimeAdmissionWire.ReadControlReplyAsync(stream,admission,Nonce,true,()=>{},default));
        });
        await check("waiting authorizer cannot resolve or launch before authenticated admission",async()=>
        {
            var channel=new Channel();var installed=new Authorizer();var reader=new Reader();var host=new RuntimeHostIdentity(55,66);
            var waiter=new WaitingRuntimeAuthorizer(installed,reader,channel,()=>host);var pending=waiter.AuthorizeAsync(default);
            Assert(installed.Calls==0&&!pending.IsCompleted);channel.Admit(new(Binding,Release.Id,Nonce,55,66));
            using var grant=await pending;Assert(installed.Calls==1);var child=await grant.Launcher.StartAsync(Release,default);
            Assert(!channel.Ready);await grant.Launcher.WaitForHealthyAsync(child,default);Assert(channel.Ready);
            await Denied(()=>waiter.AuthorizeAsync(default));
        });
        foreach(var mode in new[]{"activation","pid","created","release","revoked_before_launch"})await check("waiting authorizer rejects "+mode+" binding",async()=>
        {
            var channel=new Channel();var installed=new Authorizer();var admission=new RuntimeAdmissionBinding(Binding,Release.Id,Nonce,55,66);
            channel.Admit(mode switch{"activation"=>admission with{Binding=Binding with{ActivationReceiptDigest=new string('e',64)}},"pid"=>admission with{ProcessId=56},"created"=>admission with{Created=67},"release"=>admission with{ReleaseDigest=new string('e',64)},_=>admission});
            var waiter=new WaitingRuntimeAuthorizer(installed,new Reader(),channel,()=>new(55,66));
            if(mode=="revoked_before_launch")
            {using var grant=await waiter.AuthorizeAsync(default);channel.Current=false;await Denied(()=>grant.Launcher.StartAsync(Release,default));Assert(installed.Launches==0);}
            else await Denied(()=>waiter.AuthorizeAsync(default));
            Assert(!channel.Ready);
        });
        await check("waiting authorizer refuses changed protected head immediately before launch",async()=>
        {
            var channel=new Channel();var installed=new Authorizer();var reader=new Reader();channel.Admit(new(Binding,Release.Id,Nonce,55,66));
            var waiter=new WaitingRuntimeAuthorizer(installed,reader,channel,()=>new(55,66));using var grant=await waiter.AuthorizeAsync(default);
            reader.Current=Binding with{ActivationReceiptDigest=new string('e',64)};await Denied(()=>grant.Launcher.StartAsync(Release,default));Assert(installed.Launches==0);
        });
    }
    private static WindowsScmRuntimeLifecycle Lifecycle(Session session)=>new(()=>session,TimeSpan.FromSeconds(1),TimeSpan.FromMilliseconds(100),()=>Nonce);
    private static void Assert(bool value){if(!value)throw new Exception("RUNTIME_ADMISSION_ASSERTION");}
    private static async Task Denied(Func<Task> work)
    {try{await work();}catch(Exception e)when(e is IOException or UnauthorizedAccessException){return;}throw new Exception("RUNTIME_ADMISSION_UNEXPECTED_SUCCESS");}
    private sealed class Session:IRetainedScmRuntimeSession,IRuntimeGateKernel,IRetainedScmUpdatePreparation,IRetainedScmRecoveryObservation
    {
        internal string? Fault;internal bool CleanupUnavailable,Disposed,Released,NotCurrent;private bool assigned,exited;
        internal bool Idle,IdleCancelled;internal int IdleRequests;internal bool Exited=>exited;
        internal int RecoveryRetains;
        private RuntimeAdmissionBinding value=null!;internal readonly List<string> Order=[];
        private readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IRuntimeGateKernel Kernel=>this;
        public Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken c)
        {
            Order.Add("start");if(Fault=="start")throw new IOException("START_FAILED_AFTER_OWNERSHIP");
            value=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,55,66);
            return Task.FromResult(Fault switch{"nonce"=>value with{Nonce=new string('e',32)},"binding"=>value with{Binding=Binding with{ActivationReceiptDigest=new string('e',64)}},"release"=>value with{ReleaseDigest=new string('e',64)},"pid"=>value with{ProcessId=0},"created"=>value with{Created=0},_=>value});
        }
        public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding admission){Order.Add("authenticate");if(Fault=="identity"||NotCurrent||admission!=value)throw new UnauthorizedAccessException("IDENTITY_CHANGED");}
        public Task ReleaseLaunchAsync(RuntimeAdmissionBinding admission,CancellationToken c){Order.Add("launch");Assert(assigned);Released=true;return Task.CompletedTask;}
        public async Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken c){Order.Add("ready");if(Fault=="wait_ready")await Task.Delay(Timeout.Infinite,c);return Fault=="ready"?value with{Created=67}:value;}
        public Task StopAsync(CancellationToken c){Order.Add("stop");exited=true;exit.TrySetResult();return Task.CompletedTask;}
        public void RetainRecoveryObservation()
        {Order.Add("retain-recovery");Assert(assigned&&!exited&&!Released);if(Fault=="retain_recovery")throw new IOException("RECOVERY_HANDLE_DUPLICATION_FAILED");RecoveryRetains++;}
        public RetainedRuntimeQuiescence AcquireRecoveryQuiescence()=>throw new IOException("SYNTHETIC_SESSION_CANNOT_ISSUE_NATIVE_PROOF");
        internal void NaturalExit(){exited=true;exit.TrySetResult();}
        public Task WaitForExitAsync(CancellationToken c)=>exit.Task.WaitAsync(c);
        public Task<bool> PrepareUpdateAsync(RuntimeAdmissionBinding admission,string id,CancellationToken c)
        {Assert(admission==value&&id==Nonce);IdleRequests++;return Task.FromResult(Idle);}
        public Task CancelUpdateAsync(RuntimeAdmissionBinding admission,string id,CancellationToken c)
        {Assert(admission==value&&id==Nonce);IdleCancelled=true;return Task.CompletedTask;}
        public void RequireQuiescence(){if(!exited||CleanupUnavailable)throw new IOException("UNOBSERVED_TREE");}
        public void Dispose(){RequireQuiescence();Disposed=true;}
        public RuntimeGeneration ObserveHost()=>new(value.Binding.InstallationId,value.ReleaseDigest,value.Nonce,value.ProcessId,Fault=="changed_after_assign"&&assigned?value.Created+1:value.Created);
        public void AssignWaitingHost(RuntimeGeneration generation){Order.Add("assign");if(Fault=="assign")throw new IOException("ASSIGN_FAILED");assigned=true;}
        public bool InOwnedJob(RuntimeGeneration generation)=>assigned;
        public bool HostExited(RuntimeGeneration generation)=>exited;
        public bool OwnedTreeEmpty()=>exited&&!CleanupUnavailable;
    }
    private sealed class Reader:IInstallationReader{internal InstallationBinding Current=Binding;public InstallationBinding Read()=>Current;}
    private sealed class Channel:IRuntimeAdmissionChannel
    {
        private readonly TaskCompletionSource<RuntimeAdmissionBinding> admission=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Current=true,Ready;internal void Admit(RuntimeAdmissionBinding value)=>admission.SetResult(value);
        public Task<RuntimeAdmissionBinding> WaitForAdmissionAsync(InstallationBinding binding,RuntimeHostIdentity host,CancellationToken c)=>admission.Task.WaitAsync(c);
        public void RequireLaunch(RuntimeAdmissionBinding value){if(!Current)throw new UnauthorizedAccessException("AUTHORITY_LOST");}
        public Task ReportReadyAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireLaunch(value);Ready=true;return Task.CompletedTask;}
    }
    private sealed class Authorizer:IServiceStartAuthorizer,IPrivateAppLauncher,IAppRecoveryBarrier
    {
        internal int Calls,Launches;
        public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken c){Calls++;return Task.FromResult(new AuthorizedServiceStart(Release,this,this));}
        public Task<IContainedAppSession> StartAsync(AppRelease release,CancellationToken c){Launches++;return Task.FromResult<IContainedAppSession>(new Child());}
        public Task WaitForHealthyAsync(IContainedAppSession s,CancellationToken c)=>Task.CompletedTask;
        public Task RequireStartAllowedAsync(AppRelease r,CancellationToken c)=>Task.CompletedTask;
    }
    private sealed class Child:IContainedAppSession
    {
        public Task RequestGracefulStopAsync(CancellationToken c)=>Task.CompletedTask;
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>Task.FromResult(0);
        public Task WaitForTreeEmptyAsync(CancellationToken c)=>Task.CompletedTask;
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
