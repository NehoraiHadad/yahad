using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Authority;
using Yahad.Install;
using Yahad.Service;

// Protected private-file evidence fixtures, not native service/health acceptance.
internal static class RecoveredUpdateScheduleTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("recovered full healthy handoff records one actual update time and preserves cadence/defer",async()=>
        {
            var f=new Fixture();f.Prepare();var before=f.Scheduler();before.DeferUntil(DateTimeOffset.UtcNow.AddHours(1));var defer=before.Status.DeferredUntilUnix;
            var nativeTime=new DateTimeOffset(File.GetLastWriteTimeUtc(f.NativeDone)).ToUnixTimeSeconds();
            Assert(await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(f.App.Files,f.Anchor,f.Operation));
            var restored=f.Scheduler();Assert(restored.Status.LastUpdateUnix==nativeTime&&restored.Status.LastUpdateOperationId==f.Operation&&restored.Status.DeferredUntilUnix==defer);
            var original=File.ReadAllBytes(f.App.Files.PathOf(".authority/scheduler/state.json"));File.SetLastWriteTimeUtc(f.NativeDone,DateTime.UtcNow.AddMinutes(1));
            Assert(await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(f.App.Files,f.Anchor,f.Operation));Assert(File.ReadAllBytes(f.App.Files.PathOf(".authority/scheduler/state.json")).SequenceEqual(original));
            await restored.RunOnceAsync(true,default);Assert(restored.Status.LastUpdateUnix==nativeTime);
        });
        foreach(var kind in new[]{"notify_check","previous_restored","incomplete","rpc_failed","old_current","fake_done"})
            await check("recovered timestamp never fabricates update success from "+kind,async()=>
            {
                var f=new Fixture();f.Prepare(admitted:kind is not("notify_check" or "fake_done"),healthy:kind!="incomplete",nativeResult:kind=="previous_restored"?"previous_restored":"healthy",rpcPhase:kind=="rpc_failed"?"failed":"healthy",current:kind!="old_current");
                Assert(!await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(f.App.Files,f.Anchor,f.Operation));Assert(f.Scheduler().Status.LastUpdateUnix is null);
            });
        foreach(var kind in new[]{"native_binding","rpc_binding","data_chain"})
            await check("recovered timestamp rejects forged protected "+kind+" bindings",async()=>
            {
                var f=new Fixture();f.Prepare();
                if(kind=="native_binding")f.Write(".authority/system-native/"+f.Operation+".done.json",new{Format=1,InstallationId=f.Anchor.InstallationId,OperationId=Guid.NewGuid().ToString("N"),PreviousActivationDigest=f.PreviousDigest,Result="healthy"});
                if(kind=="rpc_binding")f.Write(".authority/rpc-"+f.Operation+".json",new{Id=f.Operation,Action="check",Profile=FullSystemUpdatePolicy.Profile,Target=new string('9',64)});
                if(kind=="data_chain"){var path=f.App.Files.PathOf(".authority/updates/"+f.Operation+"/00001.json");var first=NativeJson.Read<InstalledUpdateRecord>(File.ReadAllBytes(path));File.WriteAllBytes(path,JsonSerializer.SerializeToUtf8Bytes(first with{Checkpoint=UpdateCheckpoint.Healthy}));}
                await Rejected(()=>InstalledUpdateScheduler.RecordRecoveredUpdateAsync(f.App.Files,f.Anchor,f.Operation));Assert(f.Scheduler().Status.LastUpdateUnix is null);
            });
        await check("recovered metadata accepts old scheduler state and never regresses a later actual timestamp",async()=>
        {
            var f=new Fixture();f.Prepare();_=f.Scheduler();var later=DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
            f.Write(".authority/scheduler/state.json",new UpdateScheduleState(1,123,456,later,0,null,null,0,null,789));
            Assert(await InstalledUpdateScheduler.RecordRecoveredUpdateAsync(f.App.Files,f.Anchor,f.Operation));var state=f.Scheduler().Status;Assert(state.LastUpdateUnix==later&&state.NextCheckUnix==123&&state.LastCheckUnix==456&&state.DeferredUntilUnix==789);
        });
    }
    private static void Assert(bool condition){if(!condition)throw new IOException("RECOVERED_SCHEDULE_ASSERTION");}
    private static async Task Rejected(Func<Task> work){try{await work();}catch(IOException){return;}throw new IOException("RECOVERED_SCHEDULE_EXPECTED_REJECTION");}
    private sealed class Fixture
    {
        internal readonly NativeCandidateIntakeTests.Fixture App=new();internal readonly string Operation=Guid.NewGuid().ToString("N");
        internal readonly string Candidate=new('c',64),Policy=new('f',64),NewData=Guid.NewGuid().ToString("N");
        internal InstalledAuthorityAnchor Anchor=>App.Anchor with{FullSystemPolicyDigest=Policy};
        internal string PreviousDigest=>Hash(App.Before);
        internal string NativeDone=>App.Files.PathOf(".authority/system-native/"+Operation+".done.json");
        internal void Prepare(bool admitted=true,bool healthy=true,string nativeResult="healthy",string rpcPhase="healthy",bool current=true)
        {
            var source=NativeJson.Read<InstalledActivation>(App.Before);
            if(admitted)
            {
                FullSystemOperationJournal.Publish(App.Files,new(1,Anchor.InstallationId,Operation,new('b',64),Policy,Guid.NewGuid().ToString("N"),new('d',64),new("0.6.0",1,PreviousDigest,6),new('e',64),Candidate,"0.6.1",new('a',40),6));
                using var journal=new InstalledUpdateJournal(App.Files,Anchor.InstallationId);
                journal.Begin(new(1,Anchor.InstallationId,Operation,source.DataId,PreviousDigest,source.ReleaseDigest,Candidate,6,6,6,6,6,Anchor.IntakePolicyDigest,Anchor.IntakePinDigest,FullSystemPolicyDigest:Policy));
                journal.Append(UpdateCheckpoint.Quiesced);journal.Append(UpdateCheckpoint.BackupVerified,new(Guid.NewGuid().ToString("N"),new('a',64),6));
                journal.Append(UpdateCheckpoint.CloneIntent,migrationAttempt:new(1,Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),NewData));journal.Append(UpdateCheckpoint.CloneVerified);journal.Append(UpdateCheckpoint.ActivationIntent);journal.Append(UpdateCheckpoint.CandidateStartIntent);
                if(healthy){journal.Append(UpdateCheckpoint.ProbationHealthy);journal.Append(UpdateCheckpoint.EffectsReleaseIntent);journal.Append(UpdateCheckpoint.Healthy);}
            }
            if(current)
            {
                var head=source with{Epoch=2,ReleaseDigest=Candidate,DataId=NewData};var digest=Write("activation.json",head);
                Write("epoch.json",new InstalledEpoch(1,Anchor.InstallationId,2,digest));Write("bootstrap.json",new{schema=1,service=ServiceDefinition.Name,installationId=Anchor.InstallationId,activationReceiptDigest=digest});
            }
            Write(".authority/system-native/"+Operation+".done.json",new{Format=1,InstallationId=Anchor.InstallationId,OperationId=Operation,PreviousActivationDigest=PreviousDigest,Result=nativeResult});
            Write(".authority/rpc-"+Operation+".json",new{Id=Operation,Action="check",Profile=FullSystemUpdatePolicy.Profile,Target=new string('b',64)});
            Write(".authority/done-"+Operation+".json",new{Id=Operation,Phase=rpcPhase});
        }
        internal InstalledUpdateScheduler Scheduler()=>new(App.Files,_=>Task.FromResult<ScheduledUpdateCandidate?>(null),_=>throw new IOException("NO_DISPATCH"),()=>null,()=>"idle",()=>DateTimeOffset.UtcNow);
        internal string Write<T>(string relative,T value){var path=App.Files.PathOf(relative);Directory.CreateDirectory(Path.GetDirectoryName(path)!);var bytes=JsonSerializer.SerializeToUtf8Bytes(value);File.WriteAllBytes(path,bytes);return Hash(bytes);}
        private static string Hash(byte[] bytes)=>Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
}
