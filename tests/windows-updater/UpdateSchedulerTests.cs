using Yahad.Authority;
using Yahad.Install;

internal static class UpdateSchedulerTests
{
    internal static async Task Run(Func<string,Func<Task>,Task> check)
    {
        await check("scheduler persists offline backoff and does not spin on a second tick",async()=>
        {
            var f=new Fixture();var calls=0;
            var scheduler=f.Make(_=>{calls++;throw new IOException("offline");});
            await scheduler.RunOnceAsync(false,default);Assert(calls==1&&scheduler.Status.Failures==1&&scheduler.Status.NextCheckUnix==f.Now.ToUnixTimeSeconds()+60);
            await f.Make(_=>{calls++;throw new IOException();}).RunOnceAsync(false,default);Assert(calls==1);
            f.Now=f.Now.AddSeconds(60);await scheduler.RunOnceAsync(false,default);Assert(calls==2&&scheduler.Status.Failures==2&&scheduler.Status.NextCheckUnix==f.Now.ToUnixTimeSeconds()+120);
        });
        await check("scheduler full system disk reserve failures expose actionable storage and retain offline backoff",async()=>
        {
            foreach(var code in new[]{"NATIVE_INTAKE_DISK_SPACE","SYSTEM_SLOTS_DISK_SPACE","SYSTEM_BOOTSTRAP_DISK_SPACE"})
            {
                var f=new Fixture();var calls=0;var scheduler=f.Make(_=>{calls++;throw new IOException(code);});
                await scheduler.RunOnceAsync(false,default);Assert(calls==1&&scheduler.Status.Error=="storage"&&scheduler.Status.Failures==1);
                Assert(scheduler.Status.NextCheckUnix==f.Now.ToUnixTimeSeconds()+60);
                await scheduler.RunOnceAsync(false,default);Assert(calls==1);
            }
        });
        await check("scheduler Notify publishes availability without applying or inferring update success",async()=>
        {
            var f=new Fixture();var calls=0;var scheduler=f.Make(_=>Task.FromResult<ScheduledUpdateCandidate?>(f.Candidate),_=>{calls++;return Task.FromResult(Reply("notify","observed"));},()=>"healthy");
            await scheduler.RunOnceAsync(false,default);Assert(calls==1&&scheduler.Status.LastUpdateUnix is null&&scheduler.Status.Error is null);
        });
        await check("scheduler explicit check preserves a protected defer across restart",async()=>
        {
            var f=new Fixture();var applies=0;var scheduler=f.Make(_=>Task.FromResult<ScheduledUpdateCandidate?>(f.Candidate),_=>{applies++;return Task.FromResult(Reply("auto","queued"));});
            scheduler.DeferUntil(f.Now.AddHours(1));await scheduler.RunOnceAsync(true,default);Assert(applies==0&&scheduler.Status.LastCheckUnix is not null);
            var restored=f.Make(_=>throw new Exception("DEFER_IGNORED"));await restored.RunOnceAsync(false,default);Assert(restored.Status.DeferredUntilUnix==f.Now.AddHours(1).ToUnixTimeSeconds());
        });
        await check("scheduler quarantines a repeated unhealthy candidate but permits a different release",async()=>
        {
            var f=new Fixture();var applies=0;var scheduler=f.Make(_=>Task.FromResult<ScheduledUpdateCandidate?>(f.Candidate),_=>{applies++;return Task.FromResult(Reply("auto","queued"));},()=>"recovery_required");
            await scheduler.RunOnceAsync(true,default);await scheduler.RunOnceAsync(true,default);await scheduler.RunOnceAsync(true,default);
            Assert(applies==2&&scheduler.Status.QuarantinedTarget==f.Candidate.Target&&scheduler.Status.Error=="quarantined");
        });
        await check("scheduler busy or unrelated running work does not report a healthy update",async()=>
        {
            var f=new Fixture();var scheduler=f.Make(_=>Task.FromResult<ScheduledUpdateCandidate?>(f.Candidate),_=>Task.FromResult(Reply("auto","pending")),()=>"healthy");
            await scheduler.RunOnceAsync(false,default);Assert(scheduler.Status.LastUpdateUnix is null&&scheduler.Status.NextCheckUnix==f.Now.ToUnixTimeSeconds()+60);
        });
        await check("scheduler no new candidate cannot reapply a consumed available pointer",async()=>
        {
            var f=new Fixture();var scheduler=f.Make(_=>Task.FromResult<ScheduledUpdateCandidate?>(null),_=>throw new Exception("STALE_POINTER_REAPPLIED"));
            await scheduler.RunOnceAsync(true,default);Assert(scheduler.Status.LastCheckUnix is not null&&scheduler.Status.LastUpdateUnix is null);
        });
    }
    private static EndpointReply Reply(string mode,string state)=>new(1,new string('a',32),state,"available",mode,null,null);
    private static void Assert(bool value){if(!value)throw new IOException("SCHEDULER_ASSERTION");}
    private sealed class Fixture
    {
        internal DateTimeOffset Now=DateTimeOffset.FromUnixTimeSeconds(1800000000);
        internal readonly InstalledStateFiles Files=InstalledStateFiles.Private(SafeDirectory.CreatePrivateSession(Path.Combine(Path.GetTempPath(),"yahad-schedule")));
        internal readonly ScheduledUpdateCandidate Candidate=new(new string('b',64));
        internal InstalledUpdateScheduler Make(Func<CancellationToken,Task<ScheduledUpdateCandidate?>> check,Func<CancellationToken,Task<EndpointReply>>? apply=null,Func<string>? phase=null)
            =>new(Files,check,apply??(_=>Task.FromResult(Reply("notify","observed"))),()=>null,phase??(()=>"available"),()=>Now);
    }
}
