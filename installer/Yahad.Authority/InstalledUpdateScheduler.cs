using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record UpdateScheduleState(int Format,long NextCheckUnix,long? LastCheckUnix,long? LastUpdateUnix,int Failures,string? Error,string? FailedTarget,int TargetFailures,string? QuarantinedTarget,long DeferredUntilUnix,[property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? LastUpdateOperationId=null);
internal sealed record ScheduledUpdateCandidate(string Target);
// One background loop in the existing Authority. All policy and application
// decisions still pass through the existing protected router and ledger.
internal sealed class InstalledUpdateScheduler
{
    private readonly InstalledStateFiles files;
    private readonly Func<CancellationToken,Task<ScheduledUpdateCandidate?>> check;
    private readonly Func<CancellationToken,Task<EndpointReply>> apply;
    private readonly Func<Task?> execution;
    private readonly Func<string> phase;
    private readonly Func<DateTimeOffset> clock;
    private readonly Func<bool>? candidateFailed;
    private UpdateScheduleState state=new(1,0,null,null,0,null,null,0,null,0);
    internal UpdateScheduleState Status=>state;
    internal InstalledUpdateScheduler(InstalledStateFiles files,Func<CancellationToken,Task<ScheduledUpdateCandidate?>> check,
        Func<CancellationToken,Task<EndpointReply>> apply,Func<Task?> execution,Func<string> phase,Func<DateTimeOffset> clock,Func<bool>? candidateFailed=null)
    {
        this.files=files;this.check=check;this.apply=apply;this.execution=execution;this.phase=phase;this.clock=clock;this.candidateFailed=candidateFailed;
        files.EnsureDirectory(files.PathOf(".authority"));files.EnsureDirectory(files.PathOf(".authority/scheduler"));
        Load();
    }
    private static string StatePath(InstalledStateFiles files)=>files.PathOf(".authority/scheduler/state.json");
    private void Load()=>state=ReadState(files);
    private static UpdateScheduleState ReadState(InstalledStateFiles files)
    {
        if(Directory.EnumerateFiles(files.PathOf(".authority/scheduler"),"state.json.partial-*").Any())throw new IOException("SCHEDULER_RECONCILIATION_REQUIRED");
        var path=StatePath(files);if(!File.Exists(path))return new(1,0,null,null,0,null,null,0,null,0);
        var state=NativeJson.Read<UpdateScheduleState>(files.Read(path,8192));
        if(state.Format!=1||state.NextCheckUnix<0||state.DeferredUntilUnix<0||state.Failures is <0 or >30||state.TargetFailures is <0 or >30||
            state.LastUpdateUnix is <0||state.LastCheckUnix is <0||state.LastUpdateOperationId is not null&&state.LastUpdateUnix is null||
            state.Error is not(null or "check_failed" or "update_failed" or "quarantined" or "storage" or "maintenance"))throw new IOException("SCHEDULER_STATE");
        if(state.FailedTarget is {} f)InstalledActivationResolver.Digest(f);if(state.QuarantinedTarget is {} q)InstalledActivationResolver.Digest(q);
        if(state.LastUpdateOperationId is {} operation)InstalledActivationResolver.Id(operation);return state;
    }
    // The replacement Authority calls after actual native boot recovery is
    // healthy, before starting the scheduler. A phase string alone cannot set
    // success: every protected operation/data/native/RPC/current-head fact binds.
    internal static Task<bool> RecordRecoveredUpdateAsync(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string operationId,CancellationToken cancellation=default)
    {
        cancellation.ThrowIfCancellationRequested();InstalledActivationResolver.Id(operationId);files.ValidateRoot();
        if(anchor.FullSystemPolicyDigest is null)return Task.FromResult(false);
        var required=new[]{".authority/system-operations/"+operationId+".json",".authority/system-native/"+operationId+".done.json",".authority/rpc-"+operationId+".json",".authority/done-"+operationId+".json"};
        if(required.Any(relative=>!File.Exists(files.PathOf(relative)))||!Directory.Exists(files.PathOf(".authority/updates/"+operationId)))return Task.FromResult(false);
        var admission=FullSystemOperationJournal.Read(files,anchor.InstallationId,operationId);
        if(admission.PolicyDigest!=anchor.FullSystemPolicyDigest)throw new IOException("SCHEDULER_RECOVERED_POLICY");
        var data=InstalledUpdateJournal.Inspect(files,anchor.InstallationId,operationId);if(data.Checkpoint!=UpdateCheckpoint.Healthy)return Task.FromResult(false);
        var plan=data.Plan;
        if(plan.FullSystemPolicyDigest!=admission.PolicyDigest||plan.PreviousActivationDigest!=admission.Observed.ActivationDigest||plan.CandidateRelease!=admission.ApplicationBundleDigest||plan.SchemaBefore!=admission.Observed.DatabaseSchema||plan.PolicyDigest!=anchor.IntakePolicyDigest||plan.PinDigest!=anchor.IntakePinDigest||plan.CandidateMinimumSchema!=admission.TargetSchema||plan.CandidateMaximumSchema!=admission.TargetSchema)throw new IOException("SCHEDULER_RECOVERED_DATA_BINDING");
        var nativePath=files.PathOf(required[1]);using var native=files.Open(nativePath);var done=NativeJson.Read<JsonElement>(native.Small(8192));
        if(done.GetProperty("Format").GetInt32()!=1||done.GetProperty("InstallationId").GetString()!=anchor.InstallationId||done.GetProperty("OperationId").GetString()!=operationId||done.GetProperty("PreviousActivationDigest").GetString()!=admission.Observed.ActivationDigest)throw new IOException("SCHEDULER_RECOVERED_NATIVE_BINDING");
        if(done.GetProperty("Result").GetString()!="healthy")return Task.FromResult(false);
        var rpc=NativeJson.Read<JsonElement>(files.Read(files.PathOf(required[2]),32768));var rpcDone=NativeJson.Read<JsonElement>(files.Read(files.PathOf(required[3]),8192));
        if(rpc.GetProperty("Id").GetString()!=operationId||rpc.GetProperty("Profile").GetString()!=FullSystemUpdatePolicy.Profile||rpc.GetProperty("Action").GetString() is not("check" or "install")||rpc.GetProperty("Target").GetString()!=admission.SourceReceiptDigest||rpcDone.GetProperty("Id").GetString()!=operationId)throw new IOException("SCHEDULER_RECOVERED_RPC_BINDING");
        if(rpcDone.GetProperty("Phase").GetString()!="healthy"||rpcDone.TryGetProperty("Error",out var error)&&error.ValueKind!=JsonValueKind.Null)return Task.FromResult(false);
        var intake=new InstalledIntakeSelection(files,anchor);var head=intake.Head();
        if(head.ReleaseDigest!=admission.ApplicationBundleDigest||head.DataId!=(data.MigrationAttempt?.NewDataId??plan.NewDataId??plan.DataId)||head.MinimumSchema!=admission.TargetSchema||head.MaximumSchema!=admission.TargetSchema)return Task.FromResult(false);
        // The native done handle pins this exact file while its timestamp is read.
        var completedUnix=new DateTimeOffset(File.GetLastWriteTimeUtc(nativePath)).ToUnixTimeSeconds();if(completedUnix<=0)throw new IOException("SCHEDULER_RECOVERED_TIME");
        files.EnsureDirectory(files.PathOf(".authority/scheduler"));using var owned=files.Lease(files.PathOf(".authority/scheduler"));var state=ReadState(files);
        cancellation.ThrowIfCancellationRequested();if(intake.Head()!=head)throw new IOException("SCHEDULER_RECOVERED_HEAD_CHANGED");
        if(state.LastUpdateOperationId==operationId)return Task.FromResult(true);
        var timestamp=Math.Max(state.LastUpdateUnix??0,completedUnix);
        SaveState(files,state with{LastUpdateUnix=timestamp,LastUpdateOperationId=operationId,FailedTarget=null,TargetFailures=0});return Task.FromResult(true);
    }
    internal void DeferUntil(DateTimeOffset until)
    {
        using var owned=files.Lease(files.PathOf(".authority/scheduler"));Load();var now=clock();
        if(until<now||until>now.AddDays(7))throw new IOException("SCHEDULER_DEFER_BOUND");
        state=state with{DeferredUntilUnix=until.ToUnixTimeSeconds()};Save();
    }
    internal async Task RunAsync(CancellationToken cancellation)
    {
        while(!cancellation.IsCancellationRequested)
        {
            try{await RunOnceAsync(false,cancellation);}catch(OperationCanceledException)when(cancellation.IsCancellationRequested){break;}catch{/* Protected partial/lease failures remain unavailable; retry without clearing evidence. */}
            // Clock changes cannot spin the loop. Work itself has its own bounded
            // transport/admission deadlines and never runs on the SCM dispatcher.
            await Task.Delay(TimeSpan.FromSeconds(30),cancellation);
        }
    }
    internal async Task RunOnceAsync(bool explicitCheck,CancellationToken cancellation)
    {
        using var owned=files.Lease(files.PathOf(".authority/scheduler"));Load();var now=clock().ToUnixTimeSeconds();
        if(!explicitCheck&&(now<state.NextCheckUnix||now<state.DeferredUntilUnix))return;
        cancellation.ThrowIfCancellationRequested();
        try
        {
            var candidate=await check(cancellation);state=state with{LastCheckUnix=now,NextCheckUnix=now+21600,Failures=0,Error=null};Save();
            if(candidate is null)return;
            InstalledActivationResolver.Digest(candidate.Target);
            if(candidate.Target==state.QuarantinedTarget){state=state with{Error="quarantined"};Save();return;}
            // Explicit check bypasses the check cadence, never a parent's defer.
            if(now<state.DeferredUntilUnix)return;
            cancellation.ThrowIfCancellationRequested();var reply=await apply(cancellation);
            if(reply.Mode=="notify"||reply.State!="queued")
            {if(reply.Mode=="auto"){state=state with{NextCheckUnix=clock().ToUnixTimeSeconds()+60};Save();}return;}
            var work=execution();if(work is not null)await work.WaitAsync(cancellation);
            if(phase()=="healthy")state=state with{LastUpdateUnix=clock().ToUnixTimeSeconds(),LastUpdateOperationId=null,FailedTarget=null,TargetFailures=0};
            else if((phase() is "recovery_required" or "previous_restored")&&candidateFailed?.Invoke()!=false)
            {
                var failures=state.FailedTarget==candidate.Target?Math.Min(30,state.TargetFailures+1):1;
                state=state with{Error="update_failed",FailedTarget=candidate.Target,TargetFailures=failures,QuarantinedTarget=failures>=2?candidate.Target:state.QuarantinedTarget};
            }
            else if(phase()=="recovery_required")state=state with{Error="update_failed"};
            else if(reply.Mode=="auto")state=state with{NextCheckUnix=clock().ToUnixTimeSeconds()+60}; // Busy/idle refusal, not a failed candidate.
            Save();
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)
        {
            var failures=Math.Min(30,state.Failures+1);var seconds=Math.Min(21600,60L<<Math.Min(9,failures-1));
            var code=error.Message switch{"NATIVE_INTAKE_DISK_SPACE" or "SYSTEM_SLOTS_DISK_SPACE" or "SYSTEM_BOOTSTRAP_DISK_SPACE"=>"storage","NATIVE_INTAKE_MAINTENANCE_REQUIRED"=>"maintenance",_=>"check_failed"};
            state=state with{LastCheckUnix=now,NextCheckUnix=now+seconds,Failures=failures,Error=code};Save();
        }
    }
    private void Save()=>SaveState(files,state);
    private static void SaveState(InstalledStateFiles files,UpdateScheduleState state)
    {
        var path=StatePath(files);var temporary=path+".partial-"+Guid.NewGuid().ToString("N");using(var output=files.Create(temporary)){output.Write(JsonSerializer.SerializeToUtf8Bytes(state));output.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+path,9))throw new IOException("SCHEDULER_PUBLICATION");
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
}
