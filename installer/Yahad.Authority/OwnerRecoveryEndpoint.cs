using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal interface IOwnerRecoveryEndpoint
{
    event Action? Completed;
    Task<EndpointReply> DispatchAsync(AuthorityPeer owner,AuthorityFrame frame,CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}
internal sealed record OwnerRecoveryItem(string OperationId,string Stage);
internal sealed record OwnerRecoverySummary(OwnerRecoveryItem[] Operations,string? SelectedOperationId,string State,string? Error);

// Owner-only composition over the existing ledger/writer/recovery engine. Wire
// input selects only a protected journal ID, never a path, release or data set.
internal sealed class OwnerRecoveryEndpoint(InstalledStateFiles files,InstalledAuthorityAnchor anchor,OwnerApprovalLedger ledger,
    Func<InstallationBinding,AppRelease,CancellationToken,Task<RetainedRuntimeQuiescence>> offlineFactory,
    Func<RuntimeAuthorityGate> currentGate,Func<IAuthorityRuntimeLifecycle> newRuntimeFactory,IChildControlFactory channels,Func<DateTimeOffset> clock):IOwnerRecoveryEndpoint
{
    public event Action? Completed;
    private readonly SemaphoreSlim serial=new(1,1);private InstalledUpdateTransaction? transaction;private CatalogOperation? selected;
    private OperationConfirmation? confirmation;private string? operationId,error;private string phase="recovery_required";
    private Task? execution;private CancellationTokenSource? stop;private bool previousRestart;
    public async Task<EndpointReply> DispatchAsync(AuthorityPeer owner,AuthorityFrame frame,CancellationToken cancellation)
    {
        if(owner.Sid!=anchor.OwnerSid||owner.ProcessId<1||owner.Created<=0||owner.Session<1||!owner.Interactive)throw new UnauthorizedAccessException("OWNER_RECOVERY_PEER");
        InstalledActivationResolver.Id(frame.Id);if(frame.Format!=1)throw new IOException("AUTHORITY_FRAME_VERSION");
        await serial.WaitAsync(cancellation);
        try
        {
            if(frame.Method=="recovery")
            {
                if(frame.Body.ValueKind!=JsonValueKind.Object||frame.Body.EnumerateObject().Any())throw new IOException("AUTHORITY_EMPTY_BODY_REQUIRED");
                return Reply(frame.Id,"observed");
            }
            if(frame.Method=="recover")
            {
                if(execution is {IsCompleted:false}||confirmation is not null)return Reply(frame.Id,"pending");
                var request=NativeJson.Read<BeginRecovery>(JsonSerializer.SerializeToUtf8Bytes(frame.Body));InstalledActivationResolver.Id(request.OperationId);
                // Require that the opaque operation was actually published by this
                // installation before attempting even a waiting-host start.
                if(!Scan().Any(x=>x.OperationId==request.OperationId))throw new IOException("OWNER_RECOVERY_OPERATION_UNAVAILABLE");
                var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
                var bytes=files.Read(files.PathOf("activation.json"),16384);var head=NativeJson.Read<InstalledActivation>(bytes);
                if(binding.InstallationId!=anchor.InstallationId||head.InstallationId!=anchor.InstallationId||InstalledActivationResolver.Hash(bytes)!=binding.ActivationReceiptDigest)throw new IOException("OWNER_RECOVERY_HEAD_UNCERTAIN");
                var retained=await offlineFactory(binding,new(head.ReleaseDigest),cancellation);
                transaction=await InstalledUpdateTransaction.ResumeAsync(files,anchor,request.OperationId,retained,clock,cancellation);
                operationId=request.OperationId;previousRestart=transaction.CanRestartPreviousUnchanged;
                selected=previousRestart?transaction.PreparePreviousRestart(clock):transaction.PrepareProtectedRecover(clock);
                var challenge=ledger.Challenge(selected.Operation);
                confirmation=new(1,anchor.InstallationId,operationId,challenge.Id,challenge.Digest,challenge.Scope,selected.Facts.CurrentVersion,selected.Facts.TargetVersion,
                    selected.Facts.Origin,selected.Operation.DataId,previousRestart?"previous_unchanged":"new_copy_preserve_current",challenge.ExpiresUnix,null);
                phase="awaiting_local_approval";error=null;return Reply(frame.Id,"queued");
            }
            if(frame.Method!="recovery-answer")throw new IOException("OWNER_RECOVERY_METHOD");
            if(confirmation is null||selected is null||transaction is null||execution is {IsCompleted:false})throw new IOException("OWNER_RECOVERY_NO_CONFIRMATION");
            var answer=AuthorityProtocol.Answer(JsonSerializer.SerializeToUtf8Bytes(frame.Body));
            if(answer.Challenge!=confirmation.Challenge||answer.OperationDigest!=confirmation.Digest)throw new UnauthorizedAccessException("OWNER_RECOVERY_CONFIRMATION_CHANGED");
            ledger.Approve(owner,answer);confirmation=null;
            if(!answer.Approve){phase="recovery_required";transaction.Failure();return Reply(frame.Id,"denied");}
            // Accept once and answer promptly. A lost pipe response must not
            // cancel or repeat an approved restore; service stop owns cancellation.
            stop=new();phase="recovering";var approved=selected;var update=transaction;var original=operationId!;var restart=previousRestart;
            execution=Task.Run(()=>ExecuteAsync(update,approved,original,restart,frame.Id,stop.Token));return Reply(frame.Id,"queued");
        }
        catch
        {
            if(execution is not {IsCompleted:false}){phase="recovery_required";error="recovery_unavailable";}
            throw;
        }
        finally{serial.Release();}
    }
    private async Task ExecuteAsync(InstalledUpdateTransaction update,CatalogOperation approved,string original,bool restart,string answerId,CancellationToken cancellation)
    {
        try
        {
            InstallationBinding binding;
            if(restart)
            {
                await update.PreviousStartIntentAsync(ledger,approved,cancellation);
                binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
            }
            else
            {
                await update.RestoreNewAsync(ledger,approved,cancellation);var gate=currentGate();
                var writer=new ProtectedStateTransactionWriter(files,anchor.InstallationId,c=>{c.ThrowIfCancellationRequested();gate.RequireQuiescence();return Task.CompletedTask;},ledger,channels,clock);
                writer.UseCommitDataLease(update.DuplicateLease);binding=await writer.CommitAsync(approved.Operation,cancellation:cancellation);
                update.ReleaseForRollbackStart(binding);
            }
            var runtime=newRuntimeFactory();await runtime.StartCommittedAsync(binding,new(approved.Operation.ReleaseDigest),cancellation);
            if(runtime.Phase!="healthy")throw new IOException("OWNER_RECOVERY_RUNTIME_UNHEALTHY");
            CompleteObservedRecovery(()=>PublishDone(original,answerId),
                ()=>{if(restart)update.MarkPreviousHealthy();else update.MarkRollbackComplete(binding);},
                ()=>{phase="healthy";error=null;Completed?.Invoke();});
        }
        catch
        {
            phase="recovery_required";error="recovery_failed";
            try{update.Failure();}catch{/* Preserve the original durable evidence and retained owner. */}
        }
    }
    // Called only after the retained runtime reports actual health. The original
    // RPC must be durable before the journal becomes terminal: a crash otherwise
    // hides the only operation that can repair an unfinished RPC after reboot.
    internal static void CompleteObservedRecovery(Action publishDone,Action markTerminal,Action completed,Action<string>? fault=null)
        =>UpdateCompletionProtocol.Complete(publishDone,markTerminal,completed,fault);
    private void PublishDone(string original,string answerId)
    {
        var destination=files.PathOf(".authority/done-"+original+".json");var temporary=destination+".partial-"+answerId;
        using(var output=files.Create(temporary)){output.Write(JsonSerializer.SerializeToUtf8Bytes(new{Id=original,Phase="healthy",RecoveryRequestId=answerId}));output.Flush(true);}
        if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+destination,9))throw new IOException("OWNER_RECOVERY_DONE_PUBLICATION");
    }
    private OwnerRecoveryItem[] Scan()
    {
        var root=files.PathOf(".authority/updates");if(!Directory.Exists(root))return [];
        using var pin=files.PinDirectory(root);var directories=Directory.EnumerateDirectories(root).Take(1001).ToArray();
        if(directories.Length>1000)throw new IOException("OWNER_RECOVERY_CATALOG_BOUND");var result=new List<OwnerRecoveryItem>();
        foreach(var directory in directories)
        {
            var id=Path.GetFileName(directory);InstalledActivationResolver.Id(id);using var item=files.PinDirectory(directory);
            var records=Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Take(129).ToArray();
            if(records.Length is <1 or >128||records.Any(p=>Path.GetFileName(p).Contains(".partial",StringComparison.Ordinal)))throw new IOException("OWNER_RECOVERY_EVIDENCE_UNAVAILABLE");
            var last=NativeJson.Read<InstalledUpdateRecord>(files.Read(records[^1],32768));
            if(last.Format!=1||last.Plan.InstallationId!=anchor.InstallationId||last.Plan.OperationId!=id)throw new IOException("OWNER_RECOVERY_EVIDENCE_BINDING");
            if(last.Checkpoint is UpdateCheckpoint.Healthy or UpdateCheckpoint.RollbackComplete or UpdateCheckpoint.Aborted)continue;
            // Display metadata only. ResumeAsync validates the complete protected
            // chain and backup before offering an executable recovery challenge.
            result.Add(new(id,"interrupted"));
        }
        return result.ToArray();
    }
    private EndpointReply Reply(string id,string state)
    {
        OwnerRecoveryItem[] items;try{items=Scan();}catch{items=[];error="recovery_evidence_unavailable";}
        var summary=new OwnerRecoverySummary(items,operationId,Volatile.Read(ref phase),error);
        return new(1,id,state,summary.State,"unavailable",null,confirmation,LastRequest:JsonSerializer.SerializeToElement(summary));
    }
    public async Task StopAsync(CancellationToken cancellation)
    {
        stop?.Cancel();if(execution is { } work)await work.WaitAsync(cancellation);
        if(phase=="recovery_required"&&transaction?.HasRetainedResources==true)throw new IOException("OWNER_RECOVERY_OWNERSHIP_RETAINED");
    }
    private sealed record BeginRecovery(string OperationId);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string from,string to,uint flags);
}
