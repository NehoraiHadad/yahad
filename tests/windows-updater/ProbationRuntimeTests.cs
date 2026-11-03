using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
using Yahad.Authority;

// In-memory adapters only: probation, effects release and lost-acknowledgement
// behaviour of the retained SCM lifecycle. No token, ACL, pipe or installed SCM
// operation is created by these cases.
internal static class ProbationRuntimeTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        foreach(var when in new[]{"during-release","after-unknown"})
            await check("original host exit "+when+" retains cleanup then permits fresh recovery proof without release retry",async()=>
            {
                var binding=Binding();var session=new ProbationRecoveryRuntime(binding){ExitDuringRelease=when=="during-release"};var life=new WindowsScmRuntimeLifecycle(()=>session);
                await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));if(when=="after-unknown")session.NaturalExit();
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(1));while(life.Phase!="recovery_required")await Task.Delay(1,deadline.Token);
                Assert(life.OwnsRuntimeResources&&!session.Disposed&&session.ReleaseCalls==1);await Reject(()=>life.ReleaseEffectsAsync(binding,new('2',32),default));
                await life.RetryCleanupAsync();Assert(session.Disposed&&!life.OwnsRuntimeResources&&life.Phase=="failed"&&session.ReleaseCalls==1);
                var fresh=new ProbationRecoveryRuntime(binding){AllowOffline=true};var offline=new WindowsScmRuntimeLifecycle(()=>fresh);
                await offline.PrepareOfflineRecoveryAsync(binding.Binding,new(binding.ReleaseDigest));offline.RetainedGate.RequireQuiescence();RequireFakeCapture(offline,fresh);
                Assert(fresh.RecoveryRetains==1&&fresh.ReleaseCalls==0&&fresh.Exited&&fresh.Empty&&offline.Phase=="offline_recovery_ready");offline.ReleaseStoppedResources();Assert(fresh.Disposed);
            });
        await check("lost effects ACK busy reply never permits a forced stop or release retry",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding);var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(life.Phase=="effects_outcome_unknown"&&session.ReleaseCalls==1);RequireFakeCapture(life,session);
            Assert(!await life.PrepareUpdateAsync(new('2',32),default));await Reject(()=>life.StopAsync());
            Assert(life.Phase=="effects_outcome_unknown"&&session.Stops==0&&!session.Exited);
            await Reject(()=>life.ReleaseEffectsAsync(binding,new('3',32),default));Assert(session.ReleaseCalls==1);
            await FinishUnknown(life,session);
        });
        await check("lost effects ACK idle contracts only the same generation then proves actual stopped job",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));RequireFakeCapture(life,session);
            Assert(await life.PrepareUpdateAsync(new('2',32),default)&&life.Phase=="effects_outcome_unknown");
            await life.StopAsync();life.RetainedGate.RequireQuiescence();Assert(session.Stops==1&&session.Exited&&session.Empty&&life.Phase=="stopped");
            Assert(session.Order.IndexOf("capture")<session.Order.IndexOf("prepare")&&session.Order.IndexOf("prepare")<session.Order.IndexOf("stop"));
            life.ReleaseStoppedResources();Assert(session.Disposed&&session.ReleaseCalls==1);
        });
        await check("lost effects ACK changed generation cannot be captured prepared or stopped",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(await life.PrepareUpdateAsync(new('2',32),default));session.Changed=true;
            await Reject(()=>{_ =life.AcquireRecoveryQuiescence();return Task.CompletedTask;});await Reject(()=>life.StopAsync());
            await Reject(()=>life.PrepareUpdateAsync(new('3',32),default));Assert(session.Captures==0&&session.Stops==0&&life.Phase=="effects_outcome_unknown");
            session.Changed=false;await FinishUnknown(life,session);
        });
        await check("unknown effects cancellation removes its idle contraction without claiming health",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            Assert(await life.PrepareUpdateAsync(new('2',32),default));await life.CancelUpdateAsync(new('2',32),default);await Reject(()=>life.StopAsync());
            Assert(session.Cancels==1&&session.Stops==0&&life.Phase=="effects_outcome_unknown");await FinishUnknown(life,session);
        });
        await check("unknown effects idle reply cannot bless a generation changed during that reply",async()=>
        {
            var binding=Binding();var session=new ProbationRecoveryRuntime(binding){Idle=true,ChangeDuringPrepare=true};var life=new WindowsScmRuntimeLifecycle(()=>session);
            await life.StartProbationAsync(binding);await Reject(()=>life.ReleaseEffectsAsync(binding,new('1',32),default));
            await Reject(()=>life.PrepareUpdateAsync(new('2',32),default));await Reject(()=>life.StopAsync());Assert(session.Stops==0&&life.Phase=="effects_outcome_unknown");
            session.Changed=false;session.ChangeDuringPrepare=false;await FinishUnknown(life,session);
        });
        await check("probation effects intent is durable before provider and HTTP release",async()=>
        {
            var order=new List<string>();var journal=new EffectsJournal(order);var runtime=new EffectsRuntime(order);var gate=new ProbationEffectsCoordinator(Binding(),journal,runtime);
            await gate.ReleaseAsync();Assert(order.SequenceEqual(new[]{"journal","release"})&&gate.Phase==ProbationEffectsPhase.Released&&gate.EffectsMayHaveBeenReleased);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==1);
        });
        await check("probation journal failure sends no release command",async()=>
        {
            var order=new List<string>();var journal=new EffectsJournal(order){Fail=true};var runtime=new EffectsRuntime(order);var gate=new ProbationEffectsCoordinator(Binding(),journal,runtime);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==0&&gate.Phase==ProbationEffectsPhase.FailedBeforeRelease&&!gate.EffectsMayHaveBeenReleased);
        });
        await check("probation lost release acknowledgement cannot authorize old-data rollback or retry",async()=>
        {
            var order=new List<string>();var runtime=new EffectsRuntime(order){LoseReply=true};var gate=new ProbationEffectsCoordinator(Binding(),new EffectsJournal(order),runtime);
            await Reject(()=>gate.ReleaseAsync());Assert(gate.EffectsMayHaveBeenReleased&&gate.Phase==ProbationEffectsPhase.OutcomeUnknown&&runtime.Calls==1);
            await Reject(()=>gate.ReleaseAsync());Assert(runtime.Calls==1);
        });
        await check("probation wire binds exact activation, operation and nonce",()=>
        {
            var b=Binding();var id=new string('7',32);
            var ready=JsonSerializer.SerializeToUtf8Bytes(new{type="probation-ready",nonce=b.Nonce,activationReceiptDigest=b.Binding.ActivationReceiptDigest,operationId=b.OperationId});
            ProbationRuntimeWire.Require(ready,b,"probation-ready");
            using var release=JsonDocument.Parse(ProbationRuntimeWire.Release(b,id));Assert(release.RootElement.GetProperty("id").GetString()==id);
            var ack=JsonSerializer.SerializeToUtf8Bytes(new{type="effects-released",id,nonce=b.Nonce,activationReceiptDigest=b.Binding.ActivationReceiptDigest,operationId=b.OperationId});
            ProbationRuntimeWire.Require(ack,b,"effects-released",id);return Task.CompletedTask;
        });
        foreach(var fault in new[]{"operation","activation","nonce","id","type","extra","duplicate"})
            await check("probation wire refuses spoofed "+fault+" acknowledgement",async()=>
            {
                var b=Binding();var id=new string('7',32);var fields=new Dictionary<string,object?>{{"type","effects-released"},{"id",id},{"nonce",b.Nonce},{"activationReceiptDigest",b.Binding.ActivationReceiptDigest},{"operationId",b.OperationId}};
                if(fault=="operation")fields["operationId"]=new string('9',32);if(fault=="activation")fields["activationReceiptDigest"]=new string('9',64);if(fault=="nonce")fields["nonce"]=new string('9',32);
                if(fault=="id")fields["id"]=new string('9',32);if(fault=="type")fields["type"]="ready";if(fault=="extra")fields["release"]=true;
                var bytes=JsonSerializer.SerializeToUtf8Bytes(fields);if(fault=="duplicate")bytes=System.Text.Encoding.UTF8.GetBytes(System.Text.Encoding.UTF8.GetString(bytes).Replace("{","{\"id\":\""+id+"\","));
                await Reject(()=>{ProbationRuntimeWire.Require(bytes,b,"effects-released",id);return Task.CompletedTask;});
            });
    }
    private static ProbationRuntimeBinding Binding()=>new(new(new('a',32),new('b',64)),new('c',32),new('d',64),new('e',32),new('f',32));
    private static async Task Reject(Func<Task> action)
    {try{await action();}catch(Exception e)when(e is IOException or UnauthorizedAccessException or OperationCanceledException or JsonException){return;}throw new Exception("PROBATION_UNEXPECTED_ADMISSION");}
    private static void Assert(bool value){if(!value)throw new Exception("PROBATION_RUNTIME_ASSERTION");}
    private static void RequireFakeCapture(WindowsScmRuntimeLifecycle lifecycle,ProbationRecoveryRuntime session)
    {
        // The fake deliberately CANNOT manufacture RetainedRuntimeQuiescence.
        // Verify that authentication reached the capture boundary; native
        // handle duplication and RequireAsync remain Windows acceptance gates.
        try{_ =lifecycle.AcquireRecoveryQuiescence();throw new Exception("FAKE_NATIVE_PROOF_CREATED");}
        catch(IOException error)when(error.Message=="FAKE_NATIVE_PROOF_NOT_CREATED"){Assert(session.Captures==1);}
    }
    private static async Task FinishUnknown(WindowsScmRuntimeLifecycle life,ProbationRecoveryRuntime session)
    {session.Idle=true;Assert(await life.PrepareUpdateAsync(new('f',32),default));await life.StopAsync();life.RetainedGate.RequireQuiescence();life.ReleaseStoppedResources();}
    private sealed class EffectsJournal(List<string> order):IProbationEffectsJournal
    {internal bool Fail;public Task RecordIntentAsync(ProbationRuntimeBinding binding,string id,CancellationToken c){order.Add("journal");if(Fail)throw new IOException("FAKE_DISK_FULL");return Task.CompletedTask;}}
    private sealed class EffectsRuntime(List<string> order):IProbationEffectsRuntime
    {internal int Calls;internal bool LoseReply;public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken c){Calls++;order.Add("release");if(LoseReply)throw new IOException("FAKE_LOST_ACK");return Task.CompletedTask;}}
    private sealed class ProbationRecoveryRuntime(ProbationRuntimeBinding expected):IRetainedScmRuntimeSession,IRetainedScmUpdatePreparation,IRetainedScmRecoveryObservation,IProbationEffectsRuntime,IRuntimeGateKernel
    {
        private RuntimeAdmissionBinding? admission;private readonly TaskCompletionSource exit=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly List<string> Order=[];internal bool Idle,Changed,ChangeDuringPrepare,Exited,Empty,Disposed,ExitDuringRelease,AllowOffline;private bool assigned;
        internal int ReleaseCalls,Stops,Cancels,Captures,RecoveryRetains;public IRuntimeGateKernel Kernel=>this;
        public Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken c)
        {
            Assert((AllowOffline?intent.Purpose=="admit":intent.Purpose=="probation"&&intent.Nonce==expected.Nonce)&&intent.Binding==expected.Binding&&intent.ReleaseDigest==expected.ReleaseDigest);
            admission=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,765,876);return Task.FromResult(admission);
        }
        public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding value)
        {if(value!=admission||Changed||Exited)throw new UnauthorizedAccessException("FAKE_CURRENT_GENERATION_CHANGED");}
        public Task ReleaseLaunchAsync(RuntimeAdmissionBinding value,CancellationToken c){RequireAuthenticatedCurrent(value);Assert(assigned&&!AllowOffline);return Task.CompletedTask;}
        public Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken c)=>Task.FromResult(admission!);
        public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string id,CancellationToken c)
        {Assert(binding==expected);ReleaseCalls++;if(ExitDuringRelease)NaturalExit();throw new IOException("FAKE_EFFECTS_ACK_LOST");}
        public Task<bool> PrepareUpdateAsync(RuntimeAdmissionBinding value,string id,CancellationToken c)
        {RequireAuthenticatedCurrent(value);Order.Add("prepare");if(ChangeDuringPrepare)Changed=true;return Task.FromResult(Idle);}
        public Task CancelUpdateAsync(RuntimeAdmissionBinding value,string id,CancellationToken c)
        {RequireAuthenticatedCurrent(value);Cancels++;return Task.CompletedTask;}
        public Task StopAsync(CancellationToken c)
        {if(Changed)throw new UnauthorizedAccessException("FAKE_STOP_CHANGED_GENERATION");Order.Add("stop");Stops++;Exited=true;Empty=true;exit.TrySetResult();return Task.CompletedTask;}
        public Task WaitForExitAsync(CancellationToken c)=>exit.Task.WaitAsync(c);
        public void RequireQuiescence(){if(!Exited||!Empty)throw new IOException("FAKE_OWNED_JOB_OCCUPIED");}
        public void RetainRecoveryObservation(){Assert(AllowOffline&&assigned&&!Exited);RecoveryRetains++;}
        public RetainedRuntimeQuiescence AcquireRecoveryQuiescence(){Order.Add("capture");Captures++;throw new IOException("FAKE_NATIVE_PROOF_NOT_CREATED");}
        public void Dispose(){RequireQuiescence();Disposed=true;}
        public RuntimeGeneration ObserveHost(){var value=admission??throw new IOException("FAKE_NOT_STARTED");return new(value.Binding.InstallationId,value.ReleaseDigest,value.Nonce,value.ProcessId,Changed?value.Created+1:value.Created);}
        public void AssignWaitingHost(RuntimeGeneration value)=>assigned=true;
        public bool InOwnedJob(RuntimeGeneration value)=>assigned;
        public bool HostExited(RuntimeGeneration value)=>Exited;
        public bool OwnedTreeEmpty()=>Empty;
        internal void NaturalExit(){Exited=true;Empty=true;exit.TrySetResult();}
    }
}
