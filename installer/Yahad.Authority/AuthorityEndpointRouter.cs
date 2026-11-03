using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record AuthorityFrame(int Format,string Id,string Method,JsonElement Body);
internal sealed record AuthorityTarget(string Version,string Fingerprint);
internal sealed record EndpointReply(int Format,string Id,string State,string Phase,string Mode,AuthorityTarget? Target,NativeUpdateView? View=null,JsonElement? LastRequest=null,JsonElement? LastModeRequest=null);

// The native endpoint and in-memory fixtures call this same concrete router.
// Peer identity is supplied by the OS adapter; the bounded frame cannot carry it.
// The authenticated Runtime is the only caller: its admin request is the approval.
internal sealed partial class AuthorityEndpointRouter
{
    private readonly InstalledStateFiles files;private readonly InstalledAuthorityAnchor anchor;private readonly string runtimeSid;
    private readonly InstalledIntakeSelection intake;private readonly SemaphoreSlim serial=new(1,1);
    private readonly Func<Func<Task>,Task> schedule;
    private Action<DateTimeOffset>? defer;
    private Func<string,CancellationToken,Task>? sourceCheck;
    private Task? sourceExecution;private CancellationTokenSource? sourceCancellation;
    internal Task? SourceExecution=>sourceExecution;
    internal void ConfigureSourceCheck(Func<string,CancellationToken,Task> check)
    {if(sourceCheck is not null)throw new IOException("AUTHORITY_CHECK_CONFIGURED");sourceCheck=check;}
    internal void ConfigureDeferral(Action<DateTimeOffset> deferral)
    {if(defer is not null)throw new IOException("AUTHORITY_DEFERRAL_CONFIGURED");defer=deferral;}
    private string? requestId;private string phase="idle";
    internal bool CandidateStartIssued=>systemExecution is not null&&systemCandidateStartIssued?.Invoke()==true;
    internal string ActivationPhase=>SystemActivationPhase??phase;
    internal string Phase=>ActivationPhase!="recovery_required"&&sourceExecution is {IsCompleted:false}&&Execution is not {IsCompleted:false}?"checking":ActivationPhase;
    internal Task? Execution=>systemExecution;
    internal void MarkBootRecoveryRequired()=>phase="recovery_required";
    internal AuthorityEndpointRouter(InstalledStateFiles files,InstalledAuthorityAnchor anchor,string runtimeSid,InstalledIntakeSelection intake,Func<Func<Task>,Task>? schedule=null)
    {this.files=files;this.anchor=anchor;this.runtimeSid=runtimeSid;this.intake=intake;this.schedule=schedule??(work=>Task.Run(work));files.EnsureDirectory(files.PathOf(".authority"));if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Any(p=>!File.Exists(files.PathOf(".authority/done-"+Path.GetFileNameWithoutExtension(p)[4..]+".json"))))phase="recovery_required";}
    // Called only by the protected Authority scheduler. No fabricated runtime
    // peer and no IPC method can enter this path.
    internal Task<EndpointReply> CheckScheduledAsync(CancellationToken cancellation)=>CheckScheduledAsync(cancellation,null);
    internal async Task<EndpointReply> CheckScheduledAsync(CancellationToken cancellation,string? explicitId)
    {
        await serial.WaitAsync(cancellation);
        try{var id=explicitId??Guid.NewGuid().ToString("N");InstalledActivationResolver.Id(id);return CheckSystemScheduledLocked(id,cancellation);}
        finally{serial.Release();}
    }
    internal async Task<EndpointReply> DispatchAsync(AuthorityPeer peer,byte[] frame,CancellationToken cancellation=default)
    {
        if(frame.Length is <2 or >32768)throw new IOException("AUTHORITY_FRAME_BOUND");var f=NativeJson.Read<AuthorityFrame>(frame);InstalledActivationResolver.Id(f.Id);
        if(f.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");RequirePeer(peer);
        await serial.WaitAsync(cancellation);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            if(f.Method=="status")
            {if(f.Body.ValueKind!=JsonValueKind.Object||f.Body.EnumerateObject().Any())throw new IOException("AUTHORITY_EMPTY_BODY_REQUIRED");return Reply(f.Id,"observed");}
            if(f.Method=="defer")
            {
                var requested=NativeJson.Read<NativeDeferral>(JsonSerializer.SerializeToUtf8Bytes(f.Body));
                (defer??throw new IOException("AUTHORITY_DEFERRAL_UNCONFIGURED"))(DateTimeOffset.FromUnixTimeSeconds(requested.UntilUnix));
                return Reply(f.Id,"queued");
            }
            if(f.Method=="cancel")
            {
                var id=NativeJson.Read<CancelRequest>(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(id.RequestId!=requestId)throw new IOException("AUTHORITY_CANCEL_ID");
                systemCancellation?.Cancel();WriteOnce("cancel-"+id.RequestId,new{Id=id.RequestId});
                if(Execution is null||Execution.IsCompleted){phase="cancelled";Done();}return Reply(f.Id,"queued");
            }
            if(f.Method!="request")throw new IOException("AUTHORITY_METHOD");
            var request=AuthorityProtocol.Request(JsonSerializer.SerializeToUtf8Bytes(f.Body));if(request.Id!=f.Id)throw new IOException("AUTHORITY_REQUEST_ID");
            if(File.Exists(files.PathOf(".authority/check-request-"+request.Id+".json")))
            {if(request.Action!="check")throw new IOException("AUTHORITY_REPLAY_BINDING");return Reply(f.Id,sourceExecution is {IsCompleted:false}?"pending":"recorded");}
            if(File.Exists(files.PathOf(".authority/rpc-"+request.Id+".json")))
            {
                var prior=NativeJson.Read<JsonElement>(files.Read(files.PathOf(".authority/rpc-"+request.Id+".json"),32768));
                if(prior.GetProperty("Action").GetString()!=request.Action||request.Action=="set_mode"&&prior.GetProperty("Mode").GetString()!=request.Mode||prior.TryGetProperty("Target",out var target)&&request.Action=="install"&&target.GetString()!=request.TargetDigest)throw new IOException("AUTHORITY_REPLAY_BINDING");
                return Reply(f.Id,request.Id==requestId?"pending":"recorded");
            }
            if(Directory.EnumerateFiles(files.PathOf(".authority"),"rpc-*.json").Take(1001).Count()>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
            if(request.Action=="set_mode"&&request.Mode=="notify")return NotifySystemLocked(request);
            if(Execution is {IsCompleted:false}||sourceExecution is {IsCompleted:false}||ActivationPhase=="recovery_required")return Reply(f.Id,"pending");
            if(request.Action!="check"||sourceCheck is null)return DispatchSystemLocked(request);
            if(Directory.EnumerateFiles(files.PathOf(".authority"),"check-request-*.json").Take(1001).Count()>1000)throw new IOException("AUTHORITY_REQUEST_BOUND");
            WriteOnce("check-request-"+request.Id,new{request.Id,request.Action});sourceCancellation=new();
            sourceExecution=Task.Run(()=>RunSourceCheckAsync(request.Id,sourceCancellation.Token));return Reply(f.Id,"queued");
        }
        finally{serial.Release();}
    }
    private async Task RunSourceCheckAsync(string id,CancellationToken cancellation)
    {
        try{await sourceCheck!(id,cancellation);}
        catch(OperationCanceledException){await CompleteSourceCheckAsync(id,"cancelled");}
        catch{await CompleteSourceCheckAsync(id,"network");}
    }
    internal async Task CompleteSourceCheckAsync(string id,string? error)
    {
        InstalledActivationResolver.Id(id);await serial.WaitAsync();
        try
        {
            // A dispatched activation owns its own durable completion/recovery.
            if(File.Exists(files.PathOf(".authority/rpc-"+id+".json")))return;
            WriteOnce("rpc-"+id,new{Id=id,Action="check"});
            if(phase!="recovery_required")
            {
                if(error is null){try{phase=RequireSystemControl().Target() is null?"idle":"available";}catch{phase="idle";}}
                else phase=error=="cancelled"?"cancelled":"failed";
            }
            WriteOnce("done-"+id,new{Id=id,Phase=phase,Error=error is "network"?"network":error is null?null:"internal"});
        }
        finally{serial.Release();}
    }
    internal async Task StopAsync(CancellationToken cancellation=default)
    {
        sourceCancellation?.Cancel();if(sourceExecution is{} source)await source.WaitAsync(cancellation);
        systemCancellation?.Cancel();if(systemExecution is { } system)await system.WaitAsync(cancellation);
        if(observedSystemPhase?.Invoke()=="recovery_required")phase="recovery_required";
    }
    private void RequirePeer(AuthorityPeer peer)
    {if(peer.ProcessId<1||peer.Created<=0||peer.Sid!=runtimeSid||peer.Interactive||peer.Session!=0)throw new UnauthorizedAccessException("AUTHORITY_ENDPOINT_PEER");}
    private EndpointReply Reply(string id,string state)
    {
        AuthorityTarget? target=null;try{target=RequireSystemControl().Target();}catch{}
        NativeUpdateView? view=null;try{view=NativeUpdateEnrollmentReader.View(files,anchor,intake,()=>DateTimeOffset.UtcNow);}catch{}
        return new(1,id,state,Phase,intake.Mode().Mode,target,view,NativeRequestProjection.Read(files,false),NativeRequestProjection.Read(files,true));
    }
    private void WriteOnce<T>(string name,T record)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(record);if(bytes.Length>32768)throw new IOException("AUTHORITY_RECORD_BOUND");using var file=files.Create(files.PathOf(".authority/"+name+".json"));file.Write(bytes);file.Flush(true);}
    private void Done(){if(requestId is not null&&!File.Exists(files.PathOf(".authority/done-"+requestId+".json")))WriteOnce("done-"+requestId,new{Id=requestId,Phase=phase});}
    private sealed record CancelRequest(string RequestId);
    private sealed record NativeDeferral(long UntilUnix);
}
