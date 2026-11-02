using System.Text.Json;
using System.Runtime.InteropServices;
using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// Original bytes, not a reserialization, bind all later source-data selection.
internal sealed record NativeFullSystemPlan(int Format,string InstallationId,string OperationId,byte[] PreviousActivation);
internal sealed record NativeFullSystemForwardRepair(int Format,string InstallationId,string SystemOperationId,int Attempt,byte[] Activation,ProbationRuntimeBinding Binding);
internal sealed record NativeFullSystemTerminalRestart(int Format,string InstallationId,string SystemOperationId,int Attempt,byte[] Activation,InstallationBinding Binding);
internal sealed record NativeFullSystemCurrentRestart(int Format,string InstallationId,string SystemOperationId,int Attempt,byte[] Activation,InstallationBinding Binding);
internal enum NativeFullSystemCrashBoundary {NoRequest,UnadmittedRequest,AdmissionBeforePlan,PlanBeforePreparation,Prepared,EffectsPossible}
internal static class NativeFullSystemBoundaryEvidence
{
    // Evidence classification grants no execution/ownership. Native callers must
    // still acquire and observe actual jobs, file leases, source and health.
    internal static NativeFullSystemCrashBoundary Classify(bool rpc,bool admission,bool plan,SystemBootstrapPhase? bootstrap,bool dataEffects)
    {
        if(!rpc&&!admission&&!plan&&bootstrap is null&&!dataEffects)return NativeFullSystemCrashBoundary.NoRequest;
        if(dataEffects||bootstrap is not(null or SystemBootstrapPhase.Prepared)||!admission&&(plan||bootstrap is not null))return NativeFullSystemCrashBoundary.EffectsPossible;
        if(!admission)return NativeFullSystemCrashBoundary.UnadmittedRequest;
        if(bootstrap==SystemBootstrapPhase.Prepared)return NativeFullSystemCrashBoundary.Prepared;
        return plan?NativeFullSystemCrashBoundary.PlanBeforePreparation:NativeFullSystemCrashBoundary.AdmissionBeforePlan;
    }
    internal static bool Complete(UpdateCheckpoint? data,SystemBootstrapPhase? bootstrap,bool nativeDone,bool rpcDone,bool noEffects)
    {
        if(!nativeDone||!rpcDone)return false;
        if(data is null)return noEffects&&(bootstrap is null or SystemBootstrapPhase.RolledBack);
        if(data==UpdateCheckpoint.Healthy)return bootstrap==SystemBootstrapPhase.Complete;
        return (data is UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted)&&(bootstrap is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack);
    }
}

internal sealed class InstalledFullSystemExecutionKernel:IFullSystemExecutionKernel
{
    private readonly InstalledStateFiles files,binaries;private readonly InstalledAuthorityAnchor anchor;private readonly FullSystemUpdatePolicy policy;
    private readonly Func<WindowsScmRuntimeLifecycle> current,create;private readonly Action<WindowsScmRuntimeLifecycle> track;private readonly Func<DateTimeOffset> clock;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string,Session> unresolved=new();
    internal InstalledFullSystemExecutionKernel(InstalledStateFiles files,InstalledStateFiles binaries,InstalledAuthorityAnchor anchor,FullSystemUpdatePolicy policy,
        Func<WindowsScmRuntimeLifecycle> current,Func<WindowsScmRuntimeLifecycle> create,Action<WindowsScmRuntimeLifecycle> track,Func<DateTimeOffset> clock)
    {
        this.files=files;this.binaries=binaries;this.anchor=anchor;this.policy=policy;this.current=current;this.create=create;this.track=track;this.clock=clock;
        if(files.Roles is null||binaries.Roles is null||files.Root!=InstalledUpdaterLayout.StateRoot||binaries.Root!=InstalledUpdaterLayout.BinaryRoot||anchor.FullSystemPolicyDigest!=policy.PolicyDigest)
            throw new IOException("FULL_SYSTEM_NATIVE_INSTALLED_ROOTS");
    }
    private static string PlanRelative(string id){InstalledActivationResolver.Id(id);return ".authority/system-native/"+id+".json";}
    private static string DoneRelative(string id){InstalledActivationResolver.Id(id);return ".authority/system-native/"+id+".done.json";}
    // Read-only reuse of the exact native completion parser. The installer
    // cannot authorize, resume or cancel a provider/update operation here.
    internal static void RequireInstallerQuiescence(InstalledStateFiles files,InstalledStateFiles binaries,InstalledAuthorityAnchor anchor,WindowsFileBoundary.DataLease serial)
    {
        using var retained=serial.DuplicateForDirectory(files.PathOf(".authority/update-serial"));
        var policy=FullSystemUpdatePolicy.Open(files,anchor.InstallationId,anchor.OwnerSid,anchor.FullSystemPolicyDigest,()=>DateTimeOffset.UtcNow,new InspectionCandidates());
        var inspection=new InstalledFullSystemExecutionKernel(files,binaries,anchor,policy,
            ()=>throw new IOException("INSTALLER_INSPECTION_ONLY"),()=>throw new IOException("INSTALLER_INSPECTION_ONLY"),_=>throw new IOException("INSTALLER_INSPECTION_ONLY"),()=>DateTimeOffset.UtcNow);
        if(Directory.EnumerateFileSystemEntries(files.Root).Any(p=>Path.GetFileName(p)=="pending.json"||Path.GetFileName(p).Contains(".partial-",StringComparison.Ordinal))||inspection.PendingOperation() is not null||inspection.UnadmittedRequests().Count!=0)
            throw new IOException("UPDATE_MUST_FINISH_BEFORE_INSTALLER");
        using(var journal=new InstalledUpdateJournal(files,anchor.InstallationId,serial))journal.RequireNoUnfinished();
        var catalog=files.PathOf(".authority/bootstrap-handoffs");
        if(Directory.Exists(catalog))
        {
            using var pin=files.PinDirectory(catalog);var entries=Directory.EnumerateFileSystemEntries(catalog).Take(1001).ToArray();
            if(entries.Length>1000)throw new IOException("SYSTEM_HANDOFF_CATALOG_BOUND");
            foreach(var entry in entries)
            {
                if(!Directory.Exists(entry))throw new IOException("SYSTEM_HANDOFF_OTHER_UNFINISHED");
                var record=SystemBootstrapJournal.Inspect(files,anchor.InstallationId,Path.GetFileName(entry));
                if(record is not null&&record.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack))throw new IOException("SYSTEM_HANDOFF_OTHER_UNFINISHED");
            }
        }
        if(SystemBootstrapRecoverySlots.Read(files,anchor.InstallationId,anchor.FullSystemPolicyDigest!).PendingOperationId is not null)throw new IOException("UPDATE_MUST_FINISH_BEFORE_INSTALLER");
    }
    private sealed class InspectionCandidates:IProtectedSystemCandidateResolver
    {public SystemCandidatePolicyFacts RequireVerified(string receiptDigest)=>throw new IOException("INSTALLER_INSPECTION_ONLY");}
    private bool HasAdmission(string id)=>File.Exists(files.PathOf(".authority/system-operations/"+id+".json"));
    private bool NoEffects(string id,bool allowTerminalBootstrap=false,bool ignoreForeignControlPending=false)
    {
        InstalledActivationResolver.Id(id);
        foreach(var name in new[]{".authority/bootstrap-handoffs/"+id,".authority/updates/"+id})
        {
            var path=files.PathOf(name);if(!Directory.Exists(path)||!Directory.EnumerateFileSystemEntries(path).Any())continue;
            if(allowTerminalBootstrap&&name.Contains("bootstrap-handoffs",StringComparison.Ordinal)&&SystemBootstrapJournal.Inspect(files,anchor.InstallationId,id)?.Phase==SystemBootstrapPhase.RolledBack)continue;
            return false;
        }
        foreach(var name in new[]{".authority/full-data-operation-"+id+".json",".authority/full-data-return-"+id+".json",".authority/migration-effects-"+id+".json"})if(File.Exists(files.PathOf(name)))return false;
        if(!ignoreForeignControlPending&&(File.Exists(files.PathOf("pending.json"))||Directory.EnumerateFileSystemEntries(files.Root,"pending.json.partial-*").Any()))return false;
        var capsulePath=files.PathOf(".authority/bootstrap-helper-active.json");
        if(File.Exists(capsulePath)){var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(capsulePath,65536));SystemBootstrapHelperApplication.RequireCapsule(capsule);if(capsule.OperationId==id)return false;}
        return true;
    }
    internal IReadOnlyList<string> UnadmittedRequests()
    {
        var directory=files.PathOf(".authority");if(!Directory.Exists(directory))return [];
        using var held=files.PinDirectory(directory);var pending=new List<string>();var paths=Directory.EnumerateFiles(directory,"rpc-*.json").Take(2001).ToArray();if(paths.Length>2000)throw new IOException("FULL_SYSTEM_RPC_CATALOG_BOUND");
        foreach(var path in paths)
        {
            var id=Path.GetFileName(path)[4..^5];InstalledActivationResolver.Id(id);if(File.Exists(files.PathOf(".authority/done-"+id+".json"))||HasAdmission(id))continue;
            var rpc=NativeJson.Read<JsonElement>(files.Read(path,8192));
            if(!rpc.TryGetProperty("Profile",out var profile)||profile.GetString()!=FullSystemUpdatePolicy.Profile)continue;
            if(rpc.GetProperty("Id").GetString()!=id||rpc.GetProperty("Action").GetString() is not("check" or "install"))continue;
            InstalledActivationResolver.Digest(rpc.GetProperty("Target").GetString()!);
            var planned=File.Exists(files.PathOf(PlanRelative(id)))||Directory.Exists(files.PathOf(".authority/system-native"))&&Directory.EnumerateFiles(files.PathOf(".authority/system-native"),id+".json.partial-*").Any();
            if(NativeFullSystemBoundaryEvidence.Classify(true,HasAdmission(id),planned,null,!NoEffects(id))!=NativeFullSystemCrashBoundary.UnadmittedRequest)throw new IOException("FULL_SYSTEM_RPC_UNADMITTED_EFFECTS");pending.Add(id);
        }
        return pending;
    }
    internal async Task ReconcileUnadmittedRequestsAsync(IReadOnlyList<string> requests,CancellationToken cancellation)
    {
        if(requests.Count==0)return;var expected=UnadmittedRequests();if(!requests.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal)))throw new IOException("FULL_SYSTEM_RPC_ORPHAN_SET_CHANGED");
        var original=ReadCommitted();var first=requests[0];Session session;
        if(current().Phase=="healthy")session=(Session?)(await TryAcquireIdleAsync(first,cancellation))??throw new IOException("FULL_SYSTEM_RPC_SOURCE_BUSY");
        else
        {
            var offline=create();var head=NativeJson.Read<InstalledActivation>(original);
            await offline.PrepareOfflineRecoveryAsync(new(anchor.InstallationId,InstalledActivationResolver.Hash(original)),new(head.ReleaseDigest),cancellation);track(offline);
            session=new(this,first,original,offline.AcquireRecoveryQuiescence());
        }
        try
        {
            await session.AcquireStoppedAsync(cancellation);_=await session.ObserveSourceAsync(cancellation);
            if(!requests.Order(StringComparer.Ordinal).SequenceEqual(UnadmittedRequests().Order(StringComparer.Ordinal)))throw new IOException("FULL_SYSTEM_RPC_ORPHAN_SET_CHANGED");
            if(await session.RestoreUnadmittedSourceAsync(cancellation)!=FullSystemCompletion.RestoredPrevious)throw new IOException("FULL_SYSTEM_RPC_SOURCE_NOT_HEALTHY");
            foreach(var id in requests)PublishAtomic(files.PathOf(".authority/done-"+id+".json"),new{Id=id,Phase="cancelled"});
        }
        catch{session.RetainUnresolvedOwnership();throw;}
        finally{await session.DisposeAsync();}
    }
    internal NativeFullSystemPlan ReadPlan(string id)
    {
        if(!File.Exists(files.PathOf(PlanRelative(id))))throw new FileNotFoundException("FULL_SYSTEM_NATIVE_PLAN_MISSING");
        var value=NativeJson.Read<NativeFullSystemPlan>(files.Read(files.PathOf(PlanRelative(id)),65536));
        if(value.Format!=1||value.InstallationId!=anchor.InstallationId||value.OperationId!=id||value.PreviousActivation is null||value.PreviousActivation.Length is <2 or >16384)
            throw new IOException("FULL_SYSTEM_NATIVE_PLAN");
        var head=NativeJson.Read<InstalledActivation>(value.PreviousActivation);if(head.InstallationId!=anchor.InstallationId||head.Phase!="committed")throw new IOException("FULL_SYSTEM_NATIVE_PLAN_HEAD");return value;
    }
    internal string? PendingOperation()
    {
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var relative in new[]{".authority/system-native",".authority/system-operations"})
        {
            var directory=files.PathOf(relative);if(!Directory.Exists(directory))continue;using var pin=files.PinDirectory(directory);var entries=Directory.EnumerateFileSystemEntries(directory).Take(2001).ToArray();
            if(entries.Length>2000||entries.Any(Directory.Exists))throw new IOException("FULL_SYSTEM_NATIVE_CATALOG");
            foreach(var path in entries)
            {
                var name=Path.GetFileName(path);if(name.Length<37)throw new IOException("FULL_SYSTEM_NATIVE_CATALOG");var id=name[..32];InstalledActivationResolver.Id(id);var suffix=name[32..];
                if(suffix is ".json" or ".done.json")ids.Add(id);
                else if(suffix.StartsWith(".json.partial-",StringComparison.Ordinal))
                {InstalledActivationResolver.Id(suffix[14..]);if(relative.EndsWith("system-native",StringComparison.Ordinal))ids.Add(id);}
                else if(relative.EndsWith("system-native",StringComparison.Ordinal)&&suffix.StartsWith(".done.json.partial-",StringComparison.Ordinal))
                {InstalledActivationResolver.Id(suffix[19..]);ids.Add(id);}
                else throw new IOException("FULL_SYSTEM_NATIVE_CATALOG");
            }
        }
        string? pending=null;
        foreach(var id in ids)
        {
            if(!HasAdmission(id))throw new IOException("FULL_SYSTEM_NATIVE_ADMISSION_MISSING");
            NativeFullSystemPlan? plan=null;
            try{plan=ReadPlan(id);}catch(Exception error)when(error is JsonException or IOException or FileNotFoundException or DirectoryNotFoundException){}
            if(plan is not null&&Completed(id,plan))continue;if(pending is not null)throw new IOException("FULL_SYSTEM_NATIVE_MULTIPLE_PENDING");pending=id;
        }
        return pending;
    }
    private bool Completed(string id,NativeFullSystemPlan plan,bool requireRpc=true)
    {
        var path=files.PathOf(DoneRelative(id));if(!File.Exists(path))return false;var done=NativeJson.Read<NativeFullSystemDone>(files.Read(path,8192));
        if(done.Format!=1||done.InstallationId!=anchor.InstallationId||done.OperationId!=id||done.PreviousActivationDigest!=InstalledActivationResolver.Hash(plan.PreviousActivation)||done.Result is not("healthy" or "previous_restored"))throw new IOException("FULL_SYSTEM_NATIVE_COMPLETION");
        if(Directory.EnumerateFiles(files.PathOf(".authority/system-native"),id+".json.partial-*").Any())return false;
        var admission=FullSystemOperationJournal.Read(files,anchor.InstallationId,id);
        var dataDirectory=files.PathOf(".authority/updates/"+id);
        InstalledUpdateRecord? data=Directory.Exists(dataDirectory)&&Directory.EnumerateFileSystemEntries(dataDirectory).Any()?InstalledUpdateJournal.Inspect(files,anchor.InstallationId,id):null;
        var bootstrap=SystemBootstrapJournal.Inspect(files,anchor.InstallationId,id);
        if(data is null||bootstrap is null)
        {if(done.Result!="previous_restored"||bootstrap is not null&&bootstrap.Phase!=SystemBootstrapPhase.RolledBack||!NoEffects(id,true,true))return false;}
        else if(data.Checkpoint is not(UpdateCheckpoint.Healthy or UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted)||bootstrap.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)||data.Plan.FullSystemPolicyDigest!=admission.PolicyDigest||data.Plan.PreviousActivationDigest!=admission.Observed.ActivationDigest||data.Plan.CandidateRelease!=admission.ApplicationBundleDigest||bootstrap.ManifestDigest!=admission.ManifestDigest)return false;
        if(data is not null&&done.Result!=(data.Checkpoint==UpdateCheckpoint.Healthy?"healthy":"previous_restored"))return false;
        var journalReady=NativeFullSystemBoundaryEvidence.Complete(data?.Checkpoint,bootstrap?.Phase,true,true,data is null&&NoEffects(id,true,true));if(!journalReady)return false;
        if(!requireRpc)return true;var rpcPath=files.PathOf(".authority/rpc-"+id+".json");if(!File.Exists(rpcPath))return true;
        var rpc=NativeJson.Read<JsonElement>(files.Read(rpcPath,8192));if(rpc.GetProperty("Id").GetString()!=id||rpc.GetProperty("Profile").GetString()!=FullSystemUpdatePolicy.Profile||rpc.GetProperty("Target").GetString()!=admission.SourceReceiptDigest)throw new IOException("FULL_SYSTEM_NATIVE_RPC_BINDING");
        var rpcDone=files.PathOf(".authority/done-"+id+".json");if(!File.Exists(rpcDone))return false;var response=NativeJson.Read<JsonElement>(files.Read(rpcDone,8192));
        if(response.GetProperty("Id").GetString()!=id||response.GetProperty("Phase").GetString() is not("healthy" or "previous_restored" or "recovery_required" or "cancelled" or "failed" or "available"))throw new IOException("FULL_SYSTEM_NATIVE_RPC_COMPLETION");return true;
    }
    public Task RequireNoOtherUnfinishedAsync(string operationId,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();InstalledActivationResolver.Id(operationId);var pending=PendingOperation();
        if(pending is not null&&pending!=operationId)throw new IOException("FULL_SYSTEM_NATIVE_OTHER_UNFINISHED");return Task.CompletedTask;
        // Both shared journal catalogs are independently checked again while the
        // actual serial lease is held, before backup/publication can proceed.
    }
    public async Task<IFullSystemExecutionSession?> TryAcquireIdleAsync(string operationId,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(operationId);if(unresolved.TryGetValue(operationId,out var retained))return retained;
        var runtime=current();if(runtime.Phase!="healthy")return null;var original=ReadCommitted();
        RetainedRuntimeQuiescence? proof=null;Session? owned=null;
        try
        {
            // Duplicate the actual admitted generation while still retained by
            // the healthy lifecycle; require its real exit/job-empty AFTER stop.
            proof=runtime.AcquireRecoveryQuiescence();
            if(!await runtime.PrepareUpdateAsync(operationId,cancellation)){proof.Dispose();proof=null;return null;}
            owned=new(this,operationId,original,proof);proof=null;
            try{await runtime.StopAsync(cancellation);await owned.AcquireStoppedAsync(cancellation);}
            catch(Exception error){owned.AcquisitionFailure=error;}
            return owned; // Even a failed stop has a concrete owner for recovery.
        }
        catch
        {
            proof?.Dispose();
            if(owned is not null)owned.RetainUnresolvedOwnership();
            else if(runtime.Phase=="healthy")
            {using var undo=new CancellationTokenSource(TimeSpan.FromSeconds(5));await runtime.CancelUpdateAsync(operationId,undo.Token);}
            throw;
        }
    }
    public async Task<IFullSystemExecutionSession> ReacquireForRecoveryAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
    {
        await NativeCleanupOwnershipGate.RetryRetainedAsync(cancellation);
        NativeCleanupOwnershipGate.RequireClear();
        policy.RequireRecoveryEvidence(operation);if(unresolved.TryGetValue(operation.OperationId,out var retained))return retained;
        NativeFullSystemPlan plan;var reconstruct=false;
        try{plan=ReadPlan(operation.OperationId);RequirePlan(operation,plan);}
        catch(Exception error)when(error is JsonException or IOException or FileNotFoundException or DirectoryNotFoundException)
        {
            if(!NoEffects(operation.OperationId))throw;var raw=ReadCommitted();if(InstalledActivationResolver.Hash(raw)!=operation.PreviousActivationDigest)throw new IOException("FULL_SYSTEM_NATIVE_RECONSTRUCT_HEAD");
            plan=new(1,anchor.InstallationId,operation.OperationId,raw);reconstruct=true;
        }
        var prepared=File.Exists(files.PathOf(".authority/bootstrap-handoffs/"+operation.OperationId+"/00001.json"));
        if(!prepared&&Directory.Exists(files.PathOf(".authority/system-native"))&&Directory.EnumerateFiles(files.PathOf(".authority/system-native"),operation.OperationId+".json.partial-*").Any())reconstruct=true;
        var early=NativeFullSystemBoundaryEvidence.Classify(true,true,!reconstruct,prepared?SystemBootstrapPhase.Prepared:null,!NoEffects(operation.OperationId));
        if(!prepared&&early==NativeFullSystemCrashBoundary.EffectsPossible)throw new IOException("FULL_SYSTEM_NATIVE_MISSING_PREPARATION_WITH_EFFECTS");
        var before=prepared?ReadPreparedBefore(operation):null;
        var session=new Session(this,operation.OperationId,plan.PreviousActivation,null);session.Operation=operation;
        try
        {
            var active=files.PathOf(".authority/bootstrap-helper-active.json");
            using var bootHelper=RecoveryHelperScmIdentity.PinRunningHelper(files,binaries,operation,cancellation);
            if(prepared&&File.Exists(active)&&(OriginalHelperIsRunning(operation)||bootHelper is not null))
            {
                session.Receiver=await SystemBootstrapAuthorityChannel.ReceiveInstalledAsync(files,binaries,policy,operation,before!,cancellation);
                if(session.Receiver.RepairOnly)
                {await session.Receiver.ServeRepairRuntimeAsync(cancellation);session.ControlledRepair=true;return session;}
                if(session.Receiver.Previous)await session.Receiver.WaitPreviousRollbackAsync(cancellation);
                else
                {
                    session.Custody=await session.Receiver.AdoptAsync(SourceDirectory(files,NativeJson.Read<InstalledActivation>(plan.PreviousActivation)),cancellation);
                    session.AdoptCustody();await session.OpenJournalsAsync(operation,cancellation);return session;
                }
            }
            // Cold admission is an actual SCM waiting host that is stopped
            // without resolving/launching any application or creating a fake job.
            var waiting=WaitingOnlySelection(operation);
            var offline=create();await offline.PrepareOfflineRecoveryAsync(waiting.Binding,new(waiting.Head.ReleaseDigest),cancellation);track(offline);
            session.SetProof(offline.AcquireRecoveryQuiescence());await session.AcquireStoppedAsync(cancellation);
            if(reconstruct||!prepared)
            {
                RequireObserved(operation,await session.ObserveSourceAsync(cancellation));
                if(!NoEffects(operation.OperationId))throw new IOException("FULL_SYSTEM_NATIVE_RECONSTRUCT_EFFECTS");
                if(reconstruct)RepairOriginalPlan(operation,plan.PreviousActivation);
            }
            if(prepared)await session.OpenJournalsAsync(operation,cancellation);return session;
        }
        catch{session.RetainUnresolvedOwnership();throw;}
    }
    private bool HelperChallengeTargetsThisAuthority(VerifiedSystemOperation operation)
    {
        var path=SystemBootstrapAuthorityChannel.PathFor(files,operation.OperationId,"active.json");if(!File.Exists(path))return false;
        var challenge=NativeJson.Read<SystemBootstrapChallenge>(files.Read(path,32768));SystemBootstrapAuthorityChannel.RequireChallenge(operation,challenge);
        using var self=SystemBootstrapHelperNative.OpenSelf();var actual=NativeCustodyTransfer.RequireTarget(self);
        return challenge.AuthorityPid==actual.Pid&&challenge.AuthorityCreated==actual.Created;
    }
    private bool OriginalHelperIsRunning(VerifiedSystemOperation operation)
    {
        var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(files.PathOf(".authority/bootstrap-helper-active.json"),32768));SystemBootstrapHelperApplication.RequireCapsule(capsule);
        if(capsule.OperationId!=operation.OperationId)return false;
        if(capsule.ManifestDigest!=operation.ManifestDigest)throw new IOException("FULL_SYSTEM_NATIVE_HELPER_BINDING");
        using var process=OpenProcess(0x00101000,false,(uint)capsule.HelperPid);
        if(process.IsInvalid){var error=Marshal.GetLastWin32Error();if(error==87)return false;throw new Win32Exception(error);}
        if(NativeCustodyTransfer.Created(process)!=capsule.HelperCreated)return false;
        SystemBootstrapHelperNative.RequireHelperImage(process);return WaitForSingleObject(process,0)==258;
    }
    private SystemBootstrapSnapshot ReadPreparedBefore(VerifiedSystemOperation operation)
    {
        var record=NativeJson.Read<SystemBootstrapRecord>(files.Read(files.PathOf(".authority/bootstrap-handoffs/"+operation.OperationId+"/00001.json"),8*1024*1024));
        if(record.Format!=1||record.Sequence!=1||record.PreviousDigest!=""||record.Phase!=SystemBootstrapPhase.Prepared||record.InstallationId!=operation.InstallationId||record.OperationId!=operation.OperationId||record.ManifestDigest!=operation.ManifestDigest||record.GrantId!=operation.GrantId||record.GrantBindingDigest!=operation.GrantBindingDigest||record.ExpectedEpoch!=operation.ExpectedBootstrapEpoch||record.Before.Epoch!=operation.ExpectedBootstrapEpoch||record.Before.Version!=operation.BootstrapFromVersion)
            throw new IOException("FULL_SYSTEM_NATIVE_PREPARED_BINDING");return record.Before;
    }
    private byte[] ReadCommitted()
    {
        var bytes=files.Read(files.PathOf("activation.json"),16384);var head=NativeJson.Read<InstalledActivation>(bytes);var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
        if(head.InstallationId!=anchor.InstallationId||head.Phase!="committed"||binding!=new InstallationBinding(anchor.InstallationId,InstalledActivationResolver.Hash(bytes)))throw new IOException("FULL_SYSTEM_NATIVE_COMMITTED_HEAD");return bytes;
    }
    private (InstallationBinding Binding,InstalledActivation Head) WaitingOnlySelection(VerifiedSystemOperation operation)
    {
        try{var raw=ReadCommitted();return(new(anchor.InstallationId,InstalledActivationResolver.Hash(raw)),NativeJson.Read<InstalledActivation>(raw));}
        catch(IOException error)when(error.Message=="FULL_SYSTEM_NATIVE_COMMITTED_HEAD"){}
        policy.RequireRecoveryEvidence(operation);var saved=new List<ProtectedOperation>();
        foreach(var name in new[]{".authority/full-data-operation-"+operation.OperationId+".json",".authority/full-data-return-"+operation.OperationId+".json"})
        {var path=files.PathOf(name);if(File.Exists(path)){var value=NativeJson.Read<ProtectedOperation>(files.Read(path,32768));InstalledActivationResolver.Id(value.Nonce);if(value.InstallationId!=operation.InstallationId)throw new IOException("FULL_SYSTEM_WAITING_OPERATION");saved.Add(value);}}
        var paths=Directory.EnumerateFileSystemEntries(files.Root).Where(path=>Path.GetFileName(path)=="pending.json"||Path.GetFileName(path).StartsWith("pending.json.partial-",StringComparison.Ordinal)).Take(3).ToArray();
        if(paths.Length!=1)throw new IOException("FULL_SYSTEM_WAITING_PENDING_REQUIRED");
        var pending=NativeJson.Read<ProtectedPending>(files.Read(paths[0],128*1024));var digest=AuthorityProtocol.Digest(pending.Operation);
        if(pending.Format!=1||!saved.Any(op=>AuthorityProtocol.Digest(op)==digest)||pending.Operation.InstallationId!=anchor.InstallationId||pending.Owner.InstallationId!=anchor.InstallationId||pending.Owner.Subject!=anchor.OwnerSid||pending.Owner.OperationDigest!=digest||pending.Owner.Scope!=pending.Operation.Kind||pending.Owner.ApprovedUnix<=0||pending.Owner.ValidUntilUnix<=pending.Owner.ApprovedUnix)
            throw new IOException("FULL_SYSTEM_WAITING_PENDING_BINDING");
        if(Path.GetFileName(paths[0])!="pending.json"&&Path.GetFileName(paths[0])!="pending.json.partial-"+pending.Operation.Nonce)throw new IOException("FULL_SYSTEM_WAITING_PENDING_NONCE");
        string[] records=["owner-intent.json","web.json","activation.json","epoch.json","bootstrap.json"];
        if(!pending.Before.Keys.Order(StringComparer.Ordinal).SequenceEqual(records.Order(StringComparer.Ordinal))||!pending.After.Keys.Order(StringComparer.Ordinal).SequenceEqual(records.Order(StringComparer.Ordinal)))throw new IOException("FULL_SYSTEM_WAITING_RECORD_SET");
        foreach(var name in records)
        {
            var actual=files.Read(files.PathOf(name),32768);var before=Convert.FromBase64String(pending.Before[name]);var after=Convert.FromBase64String(pending.After[name]);
            if(before.Length>32768||after.Length>32768||!actual.AsSpan().SequenceEqual(before)&&!actual.AsSpan().SequenceEqual(after))throw new IOException("FULL_SYSTEM_WAITING_VISIBLE_RECORD");
        }
        var binding=WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
        foreach(var snapshot in new[]{pending.Before,pending.After})
        {
            var raw=Convert.FromBase64String(snapshot["activation.json"]);var priorBinding=WindowsInstallationReader.Parse(Convert.FromBase64String(snapshot["bootstrap.json"]));
            if(priorBinding!=binding||InstalledActivationResolver.Hash(raw)!=binding.ActivationReceiptDigest)continue;
            var selected=NativeJson.Read<InstalledActivation>(raw);if(selected.InstallationId!=anchor.InstallationId)throw new IOException("FULL_SYSTEM_WAITING_INSTALLATION");InstalledActivationResolver.Digest(selected.ReleaseDigest);return(binding,selected);
        }
        throw new IOException("FULL_SYSTEM_WAITING_BOOTSTRAP_BINDING");
    }
    internal FullSystemInstalledState PublishedStateForProposal()
    {
        var bytes=ReadCommitted();var head=NativeJson.Read<InstalledActivation>(bytes);if(head.MinimumSchema!=head.MaximumSchema)throw new IOException("FULL_SYSTEM_SOURCE_SCHEMA_NOT_EXACT");
        return PublishedState(bytes,head.MaximumSchema);
    }
    private FullSystemInstalledState PublishedState(byte[] bytes,int actualSchema)
    {
        var head=NativeJson.Read<InstalledActivation>(bytes);var manifest=files.Read(files.PathOf("payloads/"+head.ReleaseDigest+"/bundle.json"),8*1024*1024);
        if(InstalledActivationResolver.Hash(manifest)!=head.ReleaseDigest)throw new IOException("FULL_SYSTEM_NATIVE_OLD_PAYLOAD");
        var version=NativeJson.Read<JsonElement>(manifest).GetProperty("version").GetString()!;SystemCandidateManifest.Version(version);
        var snapshot=NativeJson.Read<SystemBootstrapSnapshot>(files.Read(files.PathOf("system-bootstrap-current.json"),8*1024*1024));
        if(snapshot.Format!=1||snapshot.InstallationId!=anchor.InstallationId||snapshot.Epoch<1||snapshot.PolicyDigest!=policy.PolicyDigest)throw new IOException("FULL_SYSTEM_NATIVE_BOOTSTRAP_HEAD");SystemCandidateManifest.Version(snapshot.Version);
        return new(version,snapshot.Epoch,InstalledActivationResolver.Hash(bytes),actualSchema,snapshot.Version);
    }
    private InstalledStateFiles OperationalFiles(long minimumEpoch=0)
    {
        var selected=InstalledOperationalProbe.Require(files,binaries,anchor.InstallationId,anchor.FullSystemPolicyDigest,InstalledUpdaterLayout.ProbeExecutable,
            anchor.ProbeSha256??throw new IOException("FULL_SYSTEM_NATIVE_BASELINE_PROBE"),minimumEpoch);
        return InstalledStateFiles.Installed(files.Root,files.Roles!,selected.Executable,selected.Sha256);
    }
    private static string SourceDirectory(InstalledStateFiles files,InstalledActivation head)
    {
        InstalledActivationResolver.Id(head.DataId);
        if(head.Recovery is not {} recovery)return files.PathOf("datasets/"+head.DataId+"/data");
        InstalledActivationResolver.Id(recovery.SourceDataId);InstalledActivationResolver.Id(recovery.OperationId);
        return files.PathOf("datasets/"+recovery.SourceDataId+"/restore-"+recovery.OperationId+"/completed/data");
    }
    private void RequirePlan(VerifiedSystemOperation operation,NativeFullSystemPlan plan)
    {if(plan.InstallationId!=operation.InstallationId||plan.OperationId!=operation.OperationId||InstalledActivationResolver.Hash(plan.PreviousActivation)!=operation.PreviousActivationDigest)throw new IOException("FULL_SYSTEM_NATIVE_ORIGINAL_BYTES");}
    private void PublishPlan(VerifiedSystemOperation operation,byte[] original)
    {
        var value=new NativeFullSystemPlan(1,anchor.InstallationId,operation.OperationId,original.ToArray());RequirePlan(operation,value);
        Ensure(".authority");Ensure(".authority/system-native");var path=files.PathOf(PlanRelative(operation.OperationId));
        if(File.Exists(path)){var existing=ReadPlan(operation.OperationId);if(!existing.PreviousActivation.AsSpan().SequenceEqual(original))throw new IOException("FULL_SYSTEM_NATIVE_PLAN_REPLAY");return;}
        PublishAtomic(path,value);
    }
    private static void RequireObserved(VerifiedSystemOperation operation,FullSystemInstalledState observed)
    {if(observed.ActivationDigest!=operation.PreviousActivationDigest||observed.Version!=operation.FromVersion||observed.BootstrapEpoch!=operation.ExpectedBootstrapEpoch||(observed.BootstrapVersion??observed.Version)!=operation.BootstrapFromVersion||observed.DatabaseSchema!=operation.SourceSchema)throw new IOException("FULL_SYSTEM_NATIVE_RECONSTRUCT_SOURCE");}
    private void RepairOriginalPlan(VerifiedSystemOperation operation,byte[] original)
    {
        if(!NoEffects(operation.OperationId))throw new IOException("FULL_SYSTEM_NATIVE_PLAN_REPAIR_EFFECTS");Ensure(".authority");Ensure(".authority/system-native");Ensure(".authority/system-native-residue");
        var originalPath=files.PathOf(PlanRelative(operation.OperationId));var residues=Directory.EnumerateFiles(files.PathOf(".authority/system-native"),operation.OperationId+".json.partial-*").ToList();if(File.Exists(originalPath))residues.Add(originalPath);
        if(residues.Count>8)throw new IOException("FULL_SYSTEM_NATIVE_PLAN_RESIDUE_BOUND");
        foreach(var residue in residues)
        {using(var held=files.Open(residue)){}var target=files.PathOf(".authority/system-native-residue/"+operation.OperationId+"-"+Guid.NewGuid().ToString("N")+".json");if(!MoveFileExW(@"\\?\"+residue,@"\\?\"+target,8))throw new IOException("FULL_SYSTEM_NATIVE_PLAN_QUARANTINE");}
        PublishPlan(operation,original);
    }
    private void PublishAtomic<T>(string path,T value)
    {var temporary=path+".partial-"+Guid.NewGuid().ToString("N");WriteOnce(temporary,value);if(!MoveFileExW(@"\\?\"+temporary,@"\\?\"+path,8))throw new IOException("FULL_SYSTEM_NATIVE_ATOMIC_PUBLICATION");}
    private void Ensure(string relative){var path=files.PathOf(relative);if(!Directory.Exists(path))files.CreateDirectory(path);using var held=files.PinDirectory(path);}
    private void WriteOnce<T>(string path,T value){using var output=files.Create(path);output.Write(JsonSerializer.SerializeToUtf8Bytes(value));output.Flush(true);}
    private sealed record NativeFullSystemDone(int Format,string InstallationId,string OperationId,string PreviousActivationDigest,string Result);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string source,string target,uint flags);

    private sealed class Session:IFullSystemExecutionSession
    {
        private readonly InstalledFullSystemExecutionKernel owner;private readonly string id;private readonly byte[] original;
        private readonly InstalledActivation source;private RetainedRuntimeQuiescence? proof;private WindowsFileBoundary.DataLease? serial,data;
        private InstalledStateFiles activeFiles;private SystemBootstrapJournal? bootstrap;private InstalledDataMigrationTransaction? transaction;
        private SystemBootstrapHelperSession? helper;private WindowsScmRuntimeLifecycle? candidateRuntime;private bool retained,terminal,handed,disposed;
        private (WindowsScmRuntimeLifecycle Runtime,string Id)? pendingIdleCancellation;
        private readonly List<RetainedRuntimeQuiescence> cancellationOwners=[];
        internal Exception? AcquisitionFailure;internal VerifiedSystemOperation? Operation;internal SystemBootstrapAuthorityReceiver? Receiver;
        internal SystemBootstrapAuthorityCustody? Custody;internal bool ControlledRepair;
        internal Session(InstalledFullSystemExecutionKernel owner,string id,byte[] original,RetainedRuntimeQuiescence? proof)
        {this.owner=owner;this.id=id;this.original=original.ToArray();source=NativeJson.Read<InstalledActivation>(original);this.proof=proof;activeFiles=owner.OperationalFiles();}
        private RetainedRuntimeQuiescence Proof=>proof??throw new IOException("FULL_SYSTEM_NATIVE_QUIESCENCE_REQUIRED");
        private WindowsFileBoundary.DataLease Serial=>serial??throw new IOException("FULL_SYSTEM_NATIVE_SERIAL_REQUIRED");
        private WindowsFileBoundary.DataLease Data=>data??throw new IOException("FULL_SYSTEM_NATIVE_DATA_LEASE_REQUIRED");
        public bool CandidateStartWasRecorded=>transaction?.CandidateStartWasRecorded==true;
        public bool EffectsMayHaveBeenReleased=>transaction?.EffectsMayHaveBeenReleased==true;
        internal void SetProof(RetainedRuntimeQuiescence value){if(proof is not null)throw new IOException("FULL_SYSTEM_NATIVE_OWNER_REPLAY");proof=value;}
        internal void AdoptCustody()
        {var value=Custody??throw new IOException("FULL_SYSTEM_NATIVE_CUSTODY_REQUIRED");proof=value.Runtime.Duplicate();serial=value.Serial.DuplicateForDirectory(owner.files.PathOf(".authority/update-serial"));data=value.Data.DuplicateForDirectory(SourceDirectory(owner.files,source));}
        internal async Task AcquireStoppedAsync(CancellationToken cancellation)
        {
            await Proof.RequireAsync(cancellation);owner.Ensure(".authority");owner.Ensure(".authority/update-serial");serial??=owner.files.Lease(owner.files.PathOf(".authority/update-serial"));
            using(var journals=new InstalledUpdateJournal(owner.files,owner.anchor.InstallationId,Serial))journals.RequireNoOtherUnfinished(id);
            data??=owner.files.Lease(SourceDirectory(owner.files,source));await Proof.RequireAsync(cancellation);
        }
        private void RequireOriginal(){if(!owner.ReadCommitted().AsSpan().SequenceEqual(original))throw new IOException("FULL_SYSTEM_NATIVE_SOURCE_CHANGED");}
        private void RequireSourceIdentity()
        {
            var selected=source.Recovery is null?activeFiles.PathOf("datasets/"+source.DataId):Path.GetDirectoryName(SourceDirectory(activeFiles,source))!;
            var key=source.Recovery is null?Path.Combine(selected,"master-key"):Path.Combine(selected,"identity","master-key");
            var policy=source.Recovery is null?Path.Combine(selected,"update-policy"):Path.Combine(selected,"policy","update-policy");var pin=source.Recovery is null?Path.Combine(selected,"update-pin"):Path.Combine(selected,"policy","update-pin");
            if(InstalledActivationResolver.Hash(activeFiles.Read(key,4096))!=source.MasterKeyDigest||InstalledActivationResolver.Hash(activeFiles.Read(policy,1024*1024))!=source.PolicyDigest||InstalledActivationResolver.Hash(activeFiles.Read(pin,1024*1024))!=source.PinDigest)
                throw new IOException("FULL_SYSTEM_NATIVE_SOURCE_IDENTITY");
        }
        private async Task<int> InspectAsync(CancellationToken cancellation)
        {
            await Proof.RequireAsync(cancellation);using var inspector=new NativeSqliteInspector(activeFiles.ProbeExecutable,activeFiles.ProbeDigest);
            using var recovery=await NativeRecoveryFixture.OpenInstalledAsync(activeFiles,source.Recovery?.SourceDataId??source.DataId,Proof.Duplicate(),inspector,source.Recovery?.OperationId,cancellation,Data);
            return await recovery.InspectCurrentSchemaAsync(cancellation);
        }
        public async Task<FullSystemInstalledState> ObserveSourceAsync(CancellationToken cancellation)
        {if(AcquisitionFailure is not null)throw new IOException("FULL_SYSTEM_NATIVE_ACQUISITION_FAILED",AcquisitionFailure);RequireOriginal();RequireSourceIdentity();var schema=await InspectAsync(cancellation);RequireOriginal();RequireSourceIdentity();return owner.PublishedState(original,schema);}
        private SystemBootstrapNative Native(VerifiedSystemOperation operation)
        {
            var native=new SystemBootstrapNative(activeFiles,owner.binaries,owner.anchor,proof,Custody?.OldAuthority,Custody?.Helper,Data,SourceDirectory(owner.files,source),
                (op,previous,c)=>SystemBootstrapAuthorityChannel.ObserveLocalAuthorityAsync(owner.binaries,owner.policy,op,bootstrap?.Current?.Before??owner.ReadPreparedBefore(op),previous,c),
                (op,c)=>SystemBootstrapAuthorityChannel.ObserveLocalRuntimeAsync(activeFiles,c),RequireDataUnchangedAsync,
                (op,c)=>SystemBootstrapCandidateProbe.VerifyAsync(activeFiles,owner.binaries,op,owner.policy,c),owner.policy.RequireCurrent);
            native.ConfigureHelperPublication(new SystemBootstrapRecoverySlots(activeFiles,new WindowsRecoveryHelperSlotKernel(activeFiles,owner.binaries,native),owner.policy.RequireCurrent,Serial));
            if(Custody is not null)native.ConfigureHelperExitObservation(Custody.HelperQuiescence);return native;
        }
        private SystemBootstrapHandoff Handoff(VerifiedSystemOperation operation)=>new(Native(operation),(op,c)=>{c.ThrowIfCancellationRequested();owner.policy.RequireCurrent(op);return Task.CompletedTask;});
        private async Task RequireDataUnchangedAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            RequireOriginal();await Proof.RequireAsync(cancellation);using var evidence=new InstalledUpdateJournal(activeFiles,owner.anchor.InstallationId,Serial);
            var record=Directory.Exists(activeFiles.PathOf(".authority/updates/"+id))?evidence.Resume(id):null;
            using var inspector=new NativeSqliteInspector(activeFiles.ProbeExecutable,activeFiles.ProbeDigest);
            using var recovery=await NativeRecoveryFixture.OpenInstalledAsync(activeFiles,source.Recovery?.SourceDataId??source.DataId,Proof.Duplicate(),inspector,source.Recovery?.OperationId,cancellation,Data);
            if(record?.BackupId is {} backup&&record.BackupReceiptDigest is {} receipt)await recovery.RequireSourceMatchesBackupAsync(backup,receipt,operation.SourceSchema,cancellation);
            else if(await recovery.InspectCurrentSchemaAsync(cancellation)!=operation.SourceSchema)throw new IOException("FULL_SYSTEM_NATIVE_SOURCE_SCHEMA_CHANGED");
            RequireOriginal();
        }
        public async Task PrepareBootstrapAndBackupAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            // The Runtime idle gate holds first setup. Independently verify the
            // actual designated parent on the quiescent, exclusively held DB
            // before backup, migration or privileged bootstrap publication.
            var enrollment=NativeUpdateEnrollmentReader.Require(activeFiles,owner.anchor);
            using(var inspector=new NativeSqliteInspector(activeFiles.ProbeExecutable,activeFiles.ProbeDigest))
            using(var recovery=await NativeRecoveryFixture.OpenInstalledAsync(activeFiles,source.Recovery?.SourceDataId??source.DataId,Proof.Duplicate(),inspector,source.Recovery?.OperationId,cancellation,Data))
                await recovery.RequireCurrentOperatorAsync(enrollment.FamilyId,enrollment.UserId,cancellation);
            owner.policy.RequireCurrent(operation);RequireOriginal();Operation=operation;owner.PublishPlan(operation,original);
            bootstrap=new(activeFiles,operation,Serial);await Handoff(operation).PrepareAsync(operation,bootstrap,cancellation);
            transaction=InstalledDataMigrationTransaction.Begin(activeFiles,owner.anchor,owner.policy,operation,bootstrap,Proof,Serial,Data,owner.clock);
            await transaction.BackupAsync(cancellation);
        }
        public async Task<FullSystemCompletion> ReplaceBootstrapAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireCurrent(operation);var journal=bootstrap??throw new IOException("FULL_SYSTEM_NATIVE_PREPARE_REQUIRED");
            helper=SystemBootstrapHelperLauncher.Start(activeFiles,owner.binaries,operation,journal.Current!.Before,Proof,Data,Serial,SourceDirectory(owner.files,source),owner.policy);
            await helper.ReadyAsync(cancellation);transaction!.DetachForBootstrap(helper);transaction=null;helper.CommitHandoff();handed=true;return FullSystemCompletion.HandedToReplacementAuthority;
        }
        internal async Task OpenJournalsAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            await Proof.RequireAsync(cancellation);using(var shared=new InstalledUpdateJournal(owner.files,owner.anchor.InstallationId,Serial))shared.RequireNoOtherUnfinished(id);
            bootstrap?.Dispose();bootstrap=new(owner.files,operation,Serial);
            if(bootstrap.Current?.Phase==SystemBootstrapPhase.Prepared)return; // Verified no-disruption abort is performed by Reconcile with the live old Authority.
            if(bootstrap.Current?.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack))
            {
                try{owner.policy.RequireCurrent(operation);}
                catch(UnauthorizedAccessException){await HandRecoveryToHelperAsync(operation,cancellation);return;}
                await Handoff(operation).ReconcileAsync(operation,bootstrap,cancellation);
            }
            activeFiles=owner.OperationalFiles(bootstrap.Current?.Phase==SystemBootstrapPhase.Complete?operation.ExpectedBootstrapEpoch+1:operation.ExpectedBootstrapEpoch);transaction=await InstalledDataMigrationTransaction.ResumeForRecoveryAsync(activeFiles,owner.anchor,owner.policy,operation,bootstrap,Proof,Serial,Data,owner.clock,original,cancellation);
        }
        private async Task<FullSystemCompletion> HandRecoveryToHelperAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireRecoveryEvidence(operation);RequireOriginal();await Proof.RequireAsync(cancellation);
            if(Custody is not null)await Custody.HelperQuiescence.RequireAsync(cancellation);
            var journal=bootstrap??throw new IOException("FULL_SYSTEM_NATIVE_RECOVERY_PREPARATION_REQUIRED");
            helper=SystemBootstrapHelperLauncher.StartRecovery(activeFiles,owner.binaries,operation,journal.Current!.Before,Proof,Data,Serial,SourceDirectory(owner.files,source),owner.policy);
            await helper.ReadyAsync(cancellation);if(transaction is not null){transaction.DetachForBootstrap(helper);transaction=null;}
            helper.CommitHandoff();handed=true;return FullSystemCompletion.HandedToReplacementAuthority;
        }
        public async Task PrepareCandidateDataAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireCurrent(operation);var access=new NativeMigrationScopeAccess(activeFiles);
            await transaction!.PrepareCandidateAsync(operation.TargetSchema>operation.SourceSchema?new NativeOfflineMigrationProxy(new WindowsInstallationReader()):null,
                activeFiles.Roles!.Runtime.Value,NativeMigrationSandbox.DeriveProfileSid(operation.InstallationId),access.GrantAsync,access.NormalizeAsync,cancellation);
        }
        public async Task CommitAndStartProbationAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireCurrent(operation);var binding=await transaction!.CommitAsync(new LocalChildControlFactory(),cancellation);var selected=transaction.Candidate??throw new IOException("FULL_SYSTEM_NATIVE_CANDIDATE_REQUIRED");
            candidateRuntime=owner.create();var descriptor=new ProbationRuntimeBinding(binding,selected.OperationId,selected.ReleaseDigest,selected.DataId,Guid.NewGuid().ToString("N"));
            await transaction.StartProbationAsync(candidateRuntime,descriptor,cancellation);owner.track(candidateRuntime);
        }
        public async Task ReleaseEffectsAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {owner.policy.RequireCurrent(operation);await transaction!.ReleaseEffectsAsync(cancellation);Complete("healthy");}
        private async Task<RetainedRuntimeQuiescence> StopCurrentAsync(CancellationToken cancellation)
        {
            if(pendingIdleCancellation is {} pending)
            {
                using var undo=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if(pending.Runtime.Phase is "healthy" or "effects_outcome_unknown")await pending.Runtime.CancelUpdateAsync(pending.Id,undo.Token);
                else
                {if(pending.Runtime.CleanupReconciliationRequired)await pending.Runtime.RetryCleanupAsync(undo.Token);foreach(var observed in cancellationOwners)await observed.RequireAsync(undo.Token);}
                pendingIdleCancellation=null;foreach(var observed in cancellationOwners)observed.Dispose();cancellationOwners.Clear();
            }
            var runtime=candidateRuntime??owner.current();
            if(runtime.Phase is "healthy" or "effects_outcome_unknown")
            {
                var stopped=runtime.AcquireRecoveryQuiescence();var preparationId=Guid.NewGuid().ToString("N");var attempted=false;
                try
                {
                    attempted=true;var idle=await runtime.PrepareUpdateAsync(preparationId,cancellation);
                    if(!idle){attempted=false;throw new IOException("FULL_SYSTEM_NATIVE_RECOVERY_BUSY");}
                    await runtime.StopAsync(cancellation);await stopped.RequireAsync(cancellation);return stopped;
                }
                catch(Exception failure)
                {
                    if(attempted&&runtime.Phase is "healthy" or "effects_outcome_unknown")
                    {
                        pendingIdleCancellation=(runtime,preparationId);using var undo=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        try{await runtime.CancelUpdateAsync(preparationId,undo.Token);pendingIdleCancellation=null;}
                        catch(Exception uncertain)
                        {cancellationOwners.Add(stopped);RetainUnresolvedOwnership();throw new IOException("FULL_SYSTEM_NATIVE_IDLE_CANCELLATION_UNCONFIRMED",new AggregateException(failure,uncertain));}
                    }
                    stopped.Dispose();throw;
                }
            }
            if(runtime.Phase=="offline_recovery_ready")return runtime.AcquireRecoveryQuiescence();
            if(runtime.CleanupReconciliationRequired)await runtime.RetryCleanupAsync(cancellation);
            var waiting=owner.WaitingOnlySelection(Operation??throw new IOException("FULL_SYSTEM_NATIVE_OPERATION_REQUIRED"));var offline=owner.create();
            await offline.PrepareOfflineRecoveryAsync(waiting.Binding,new(waiting.Head.ReleaseDigest),cancellation);owner.track(offline);
            return offline.AcquireRecoveryQuiescence();
        }
        public async Task<FullSystemCompletion> ReconcileAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireRecoveryEvidence(operation);Operation=operation;if(ControlledRepair)return FullSystemCompletion.RecoveryRequired;
            if(terminal)
            {
                var currentRuntime=owner.current();if(currentRuntime.Phase!="healthy"||currentRuntime.CleanupReconciliationRequired)throw new IOException("FULL_SYSTEM_NATIVE_TERMINAL_RUNTIME_NOT_HEALTHY");
                var currentBytes=owner.ReadCommitted();var selected=NativeJson.Read<InstalledActivation>(currentBytes);var observed=currentRuntime.RetainedSession.Kernel.ObserveHost();
                if(observed.InstallationId!=owner.anchor.InstallationId||observed.ReleaseDigest!=selected.ReleaseDigest)throw new IOException("FULL_SYSTEM_NATIVE_TERMINAL_RUNTIME_BINDING");
                currentRuntime.RetainedSession.RequireAuthenticatedCurrent(new(new(owner.anchor.InstallationId,InstalledActivationResolver.Hash(currentBytes)),observed.ReleaseDigest,observed.Nonce,observed.ProcessId,observed.Created));currentRuntime.RetainedGate.RequireLaunch(observed);
                var terminalResult=FullSystemCompletion.RestoredPrevious;
                if(File.Exists(owner.files.PathOf(PlanRelative(id))))
                {
                    var plan=owner.ReadPlan(id);if(!owner.Completed(id,plan,false))throw new IOException("FULL_SYSTEM_NATIVE_TERMINAL_EVIDENCE_REQUIRED");
                    var done=NativeJson.Read<NativeFullSystemDone>(owner.files.Read(owner.files.PathOf(DoneRelative(id)),8192));terminalResult=done.Result=="healthy"?FullSystemCompletion.Healthy:FullSystemCompletion.RestoredPrevious;
                }
                else RequireOriginal();
                retained=false;unresolved.TryRemove(id,out _);return terminalResult;
            }
            if(handed)return FullSystemCompletion.HandedToReplacementAuthority;
            if((!File.Exists(owner.files.PathOf(PlanRelative(id)))||Directory.Exists(owner.files.PathOf(".authority/system-native"))&&Directory.EnumerateFiles(owner.files.PathOf(".authority/system-native"),id+".json.partial-*").Any())&&owner.NoEffects(id))
            {RequireObserved(operation,await ObserveSourceAsync(cancellation));owner.RepairOriginalPlan(operation,original);}
            if(helper?.HandoffMayHaveBegun==true)
            {
                var observation=await helper.ObserveHandoffAsync(operation,cancellation);
                if(observation==HelperHandoffObservation.Active){RetainUnresolvedOwnership();return FullSystemCompletion.RecoveryRequired;}
                var prepared=bootstrap?.Current;
                if(prepared?.Phase!=SystemBootstrapPhase.Prepared||!await PrimaryAuthorityStillRunningAsync(operation,prepared.Before,cancellation))
                {RetainUnresolvedOwnership();return FullSystemCompletion.RecoveryRequired;}
                RequireOriginal();await Proof.RequireAsync(cancellation);await RequireDataUnchangedAsync(operation,cancellation);
                // Closing these already-observed, exited native owners cannot
                // terminate a helper or descendant. Published Go still requires
                // an external recovery helper; this Authority never replaces itself.
                helper.Dispose();helper=null;
                if(observation==HelperHandoffObservation.QuiescedWithPublishedGo)return await HandRecoveryToHelperAsync(operation,cancellation);
                // No final Go exists for this exact helper generation. Its
                // complete job is empty and this same primary SCM Authority is
                // still running, so continue the verified no-disruption abort.
            }
            if(helper is not null){helper.Dispose();helper=null;}
            if(bootstrap?.Current?.Phase==SystemBootstrapPhase.Prepared)
            {
                // No Go was issued, and the exact helper (if created) has exited.
                // Verify all live previous closures and source data before an
                // abort receipt; no running Authority folder is replaced here.
                var before=bootstrap.Current.Before;await RequireDataUnchangedAsync(operation,cancellation);
                var observed=await Native(operation).PrepareAsync(operation,cancellation);
                if(JsonSerializer.Serialize(observed)!=JsonSerializer.Serialize(before))throw new IOException("FULL_SYSTEM_NATIVE_ABORT_BOOTSTRAP_CHANGED");
                bootstrap.Append(SystemBootstrapPhase.RollbackIntent,before);await RequireDataUnchangedAsync(operation,cancellation);bootstrap.Append(SystemBootstrapPhase.RolledBack,before);
            }
            else if(bootstrap is not null&&bootstrap.Current?.Phase is not(SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack))
            {
                try{owner.policy.RequireCurrent(operation);}
                catch(UnauthorizedAccessException){return await HandRecoveryToHelperAsync(operation,cancellation);}
                await Handoff(operation).ReconcileAsync(operation,bootstrap,cancellation);
            }
            if(transaction is null&&!Directory.Exists(owner.files.PathOf(".authority/updates/"+id)))
            {var restored=await RestoreUnadmittedSourceAsync(cancellation);if(restored==FullSystemCompletion.RestoredPrevious&&File.Exists(owner.files.PathOf(PlanRelative(id))))Complete("previous_restored");return restored;}
            if(transaction is null)await OpenJournalsAsync(operation,cancellation);
            if(handed)return FullSystemCompletion.HandedToReplacementAuthority;
            if(transaction!.Evidence.Checkpoint is UpdateCheckpoint.Healthy or UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted)
                return await RestartTerminalAsync(operation,cancellation);
            // A resumed transaction may continue forward only while the original
            // consent remains current; all effects are journaled by that same owner.
            if(!transaction!.CandidateStartWasRecorded&&!transaction.EffectsMayHaveBeenReleased&&bootstrap!.Current?.Phase==SystemBootstrapPhase.Complete)
            {
                try{owner.policy.RequireCurrent(operation);await PrepareCandidateDataAsync(operation,cancellation);await CommitAndStartProbationAsync(operation,cancellation);await ReleaseEffectsAsync(operation,cancellation);return FullSystemCompletion.Healthy;}
                catch(UnauthorizedAccessException){/* Recovery-only return remains available. */}
            }
            using var stopped=await StopCurrentAsync(cancellation);
            if(data is null)
            {
                data=owner.files.Lease(SourceDirectory(owner.files,source));transaction.Failure();
                transaction=await InstalledDataMigrationTransaction.ResumeForRecoveryAsync(activeFiles,owner.anchor,owner.policy,operation,bootstrap!,stopped,Serial,Data,owner.clock,original,cancellation);
            }
            var returning=owner.create();candidateRuntime=returning;
            var result=await transaction.ReconcileAsync(new ReturnLifecycle(this,returning),new LocalChildControlFactory(),stopped,cancellation);
            if(returning.Phase=="healthy"&&!ReferenceEquals(owner.current(),returning))owner.track(returning);
            if(result.Result==FullDataRecoveryResult.RestoredPrevious){Complete("previous_restored");return FullSystemCompletion.RestoredPrevious;}
            if(result.Result==FullDataRecoveryResult.HealthyCurrent){Complete("healthy");return FullSystemCompletion.Healthy;}
            if(result.Result==FullDataRecoveryResult.ForwardRepairRequired)return await CurrentAvailabilityRestartAsync(operation,returning,stopped,cancellation);
            RetainUnresolvedOwnership();return FullSystemCompletion.RecoveryRequired;
        }
        private async Task<bool> PrimaryAuthorityStillRunningAsync(VerifiedSystemOperation operation,SystemBootstrapSnapshot before,CancellationToken cancellation)
        {
            using var service=WindowsFixedRuntimeService.AuthorityRead();var status=service.Status();
            if(status.State!=4||status.Pid!=(uint)Environment.ProcessId)return false;
            using var pinned=service.Pin(status.Pid,0x00101000);using var self=SystemBootstrapHelperNative.OpenSelf();
            if(pinned.Created!=NativeCustodyTransfer.Created(self))throw new IOException("FULL_SYSTEM_NATIVE_PRIMARY_GENERATION_CHANGED");
            await SystemBootstrapAuthorityChannel.ObserveLocalAuthorityAsync(owner.binaries,owner.policy,operation,before,true,cancellation);
            pinned.RequireCurrent();return service.Status()==status;
        }
        private async Task<FullSystemCompletion> CurrentAvailabilityRestartAsync(VerifiedSystemOperation operation,WindowsScmRuntimeLifecycle runtime,RetainedRuntimeQuiescence stopped,CancellationToken cancellation)
        {
            owner.policy.RequireRecoveryEvidence(operation);await stopped.RequireAsync(cancellation);
            if(transaction?.EffectsMayHaveBeenReleased!=true)throw new IOException("FULL_SYSTEM_CURRENT_AVAILABILITY_EFFECTS_REQUIRED");
            var raw=owner.ReadCommitted();var selected=NativeJson.Read<InstalledActivation>(raw);var candidate=transaction!.Candidate??throw new IOException("FULL_SYSTEM_FORWARD_CANDIDATE_REQUIRED");
            if(selected.ReleaseDigest!=operation.ApplicationBundleDigest||selected.DataId!=candidate.DataId||selected.MinimumSchema!=operation.TargetSchema||selected.MaximumSchema!=operation.TargetSchema||
                selected.MasterKeyDigest!=source.MasterKeyDigest||selected.PolicyDigest!=source.PolicyDigest||selected.PinDigest!=source.PinDigest||selected.Migration is null||selected.Recovery is null||
                selected.Migration.Plan.SystemOperationId!=operation.OperationId||selected.Migration.Plan.OperationId!=candidate.OperationId||selected.Migration.Plan.TargetSchema!=operation.TargetSchema)
                throw new IOException("FULL_SYSTEM_FORWARD_CURRENT_SELECTION");
            var directory=SourceDirectory(activeFiles,selected);if(directory==SourceDirectory(owner.files,source))throw new IOException("FULL_SYSTEM_FORWARD_SOURCE_OVERWRITE_REFUSED");
            WindowsFileBoundary.DataLease? currentLease=null;var pins=new List<IDisposable>();
            try
            {
                currentLease=activeFiles.Lease(directory);var scope=Path.GetDirectoryName(directory)!;
                foreach(var file in new[]{(Path.Combine(scope,"identity","master-key"),selected.MasterKeyDigest),(Path.Combine(scope,"policy","update-policy"),selected.PolicyDigest),(Path.Combine(scope,"policy","update-pin"),selected.PinDigest)})
                {var pinned=activeFiles.Open(file.Item1);pins.Add(pinned);if(InstalledActivationResolver.Hash(pinned.Small(1024*1024))!=file.Item2)throw new IOException("FULL_SYSTEM_FORWARD_IDENTITY_CHANGED");}
                var spec=NativeMigrationAdmission.SpecForInstalled(activeFiles,selected.Migration);
                _=NativeMigrationAdmission.VerifyCommittedEvidence(activeFiles,spec,selected.Migration.ReceiptDigest);
                using(var inspector=new NativeSqliteInspector(activeFiles.ProbeExecutable,activeFiles.ProbeDigest))
                using(var recovery=await NativeRecoveryFixture.OpenInstalledAsync(activeFiles,selected.Recovery.SourceDataId,stopped.Duplicate(),inspector,selected.Recovery.OperationId,cancellation,currentLease))
                {if(await recovery.InspectCurrentSchemaAsync(cancellation)!=operation.TargetSchema)throw new IOException("FULL_SYSTEM_FORWARD_SCHEMA_CHANGED");}
                if(!owner.ReadCommitted().AsSpan().SequenceEqual(raw))throw new IOException("FULL_SYSTEM_FORWARD_HEAD_CHANGED");
                var binding=new InstallationBinding(owner.anchor.InstallationId,InstalledActivationResolver.Hash(raw));
                var attempts=new List<NativeFullSystemCurrentRestart>();
                var markers=Directory.EnumerateFiles(activeFiles.PathOf(".authority"),"full-current-restart-"+id+"-*").Take(4).Order(StringComparer.Ordinal).ToArray();
                if(markers.Length>3)throw new IOException("FULL_SYSTEM_FORWARD_ATTEMPT_CATALOG");
                for(var number=1;number<=markers.Length;number++)
                {
                    var path=activeFiles.PathOf(".authority/full-current-restart-"+id+"-"+number.ToString("D2",System.Globalization.CultureInfo.InvariantCulture)+".json");
                    if(markers[number-1]!=path)throw new IOException("FULL_SYSTEM_FORWARD_ATTEMPT_SEQUENCE");var previous=NativeJson.Read<NativeFullSystemCurrentRestart>(activeFiles.Read(path,65536));
                    if(previous.Format!=1||previous.InstallationId!=owner.anchor.InstallationId||previous.SystemOperationId!=id||previous.Attempt!=number||!previous.Activation.AsSpan().SequenceEqual(raw)||
                        previous.Binding!=binding)throw new IOException("FULL_SYSTEM_FORWARD_ATTEMPT_BINDING");attempts.Add(previous);
                }
                if(attempts.Count>=3)throw new IOException("FULL_SYSTEM_FORWARD_ATTEMPTS_QUARANTINED");
                var attempt=new NativeFullSystemCurrentRestart(1,owner.anchor.InstallationId,id,attempts.Count+1,raw,binding);
                owner.WriteOnce(activeFiles.PathOf(".authority/full-current-restart-"+id+"-"+attempt.Attempt.ToString("D2",System.Globalization.CultureInfo.InvariantCulture)+".json"),attempt);
                await transaction.PrepareCurrentAvailabilityRestartAsync(binding,stopped,currentLease,cancellation);await stopped.RequireAsync(cancellation);
                // Only the CURRENT new-data lease is released. Original source,
                // backup evidence and global serial ownership remain retained.
                currentLease.Dispose();currentLease=null;foreach(var pin in pins)pin.Dispose();pins.Clear();
                candidateRuntime=runtime;await runtime.StartCommittedAsync(binding,new(selected.ReleaseDigest),cancellation);owner.track(runtime);
                transaction.CompleteCurrentAvailabilityRestart(runtime,binding);Complete("healthy");return FullSystemCompletion.Healthy;
            }
            finally{foreach(var pin in pins)pin.Dispose();currentLease?.Dispose();}
        }
        private async Task<FullSystemCompletion> RestartTerminalAsync(VerifiedSystemOperation operation,CancellationToken cancellation)
        {
            owner.policy.RequireRecoveryEvidence(operation);using var stopped=await StopCurrentAsync(cancellation);await stopped.RequireAsync(cancellation);
            data??=owner.files.Lease(SourceDirectory(owner.files,source));
            transaction=await InstalledDataMigrationTransaction.ResumeForRecoveryAsync(activeFiles,owner.anchor,owner.policy,operation,bootstrap!,stopped,Serial,Data,owner.clock,original,cancellation);
            var checkpoint=transaction.Evidence.Checkpoint;
            if(checkpoint is not(UpdateCheckpoint.Healthy or UpdateCheckpoint.FullRollbackComplete or UpdateCheckpoint.Aborted))throw new IOException("FULL_SYSTEM_TERMINAL_JOURNAL_CHANGED");
            var raw=owner.ReadCommitted();var selected=NativeJson.Read<InstalledActivation>(raw);var directory=SourceDirectory(activeFiles,selected);
            WindowsFileBoundary.DataLease? selectedLease=directory==SourceDirectory(owner.files,source)?Data.DuplicateForDirectory(directory):activeFiles.Lease(directory);
            try
            {
                var binding=new InstallationBinding(owner.anchor.InstallationId,InstalledActivationResolver.Hash(raw));
                var records=Directory.EnumerateFiles(activeFiles.PathOf(".authority"),"full-terminal-restart-"+id+"-*").Take(4).Order(StringComparer.Ordinal).ToArray();if(records.Length>=3)throw new IOException("FULL_SYSTEM_TERMINAL_RESTART_QUARANTINED");
                for(var i=0;i<records.Length;i++)
                {
                    if(Path.GetFileName(records[i])!=$"full-terminal-restart-{id}-{i+1:D2}.json")throw new IOException("FULL_SYSTEM_TERMINAL_RESTART_SEQUENCE");
                    var previous=NativeJson.Read<NativeFullSystemTerminalRestart>(activeFiles.Read(records[i],65536));
                    if(previous.Format!=1||previous.InstallationId!=operation.InstallationId||previous.SystemOperationId!=id||previous.Attempt!=i+1||previous.Binding!=binding||!previous.Activation.AsSpan().SequenceEqual(raw))throw new IOException("FULL_SYSTEM_TERMINAL_RESTART_BINDING");
                }
                var attempt=new NativeFullSystemTerminalRestart(1,operation.InstallationId,id,records.Length+1,raw,binding);
                owner.WriteOnce(activeFiles.PathOf($".authority/full-terminal-restart-{id}-{attempt.Attempt:D2}.json"),attempt);
                await transaction.PrepareTerminalRestartAsync(binding,stopped,selectedLease,cancellation);
                if(!owner.ReadCommitted().AsSpan().SequenceEqual(raw))throw new IOException("FULL_SYSTEM_TERMINAL_HEAD_CHANGED");
                if(directory==SourceDirectory(owner.files,source)){data?.Dispose();data=null;Custody?.Data.Dispose();}
                selectedLease.Dispose();selectedLease=null;
                var runtime=owner.create();candidateRuntime=runtime;await runtime.StartCommittedAsync(binding,new(selected.ReleaseDigest),cancellation);owner.track(runtime);
                transaction.CompleteTerminalRestart(runtime,binding);var result=checkpoint==UpdateCheckpoint.Healthy?"healthy":"previous_restored";Complete(result);
                return result=="healthy"?FullSystemCompletion.Healthy:FullSystemCompletion.RestoredPrevious;
            }
            finally{selectedLease?.Dispose();}
        }
        private void PublishProbation(ProbationRuntimeBinding binding)
        {
            binding.Validate();var parent=activeFiles.PathOf("runtime-staging");if(!Directory.Exists(parent))activeFiles.CreateDirectory(parent);
            parent=Path.Combine(parent,binding.Nonce);activeFiles.CreateDirectory(parent);owner.WriteOnce(activeFiles.PathOf(InstalledProbationDescriptor.Relative(binding.Nonce)),binding);
        }
        public async Task<FullSystemCompletion> RestoreUnadmittedSourceAsync(CancellationToken cancellation)
        {
            if(serial is null||data is null||AcquisitionFailure is not null)
            {
                var cleanupRuntime=owner.current();if(cleanupRuntime.CleanupReconciliationRequired)await cleanupRuntime.RetryCleanupAsync(cancellation);
                await AcquireStoppedAsync(cancellation);AcquisitionFailure=null;
            }
            RequireOriginal();RequireSourceIdentity();var schema=await InspectAsync(cancellation);
            if(schema<source.MinimumSchema||schema>source.MaximumSchema)return FullSystemCompletion.RecoveryRequired;
            Data.Dispose();data=null;var restarted=owner.create();await restarted.StartCommittedAsync(new(owner.anchor.InstallationId,InstalledActivationResolver.Hash(original)),new(source.ReleaseDigest),cancellation);owner.track(restarted);terminal=true;retained=false;unresolved.TryRemove(id,out _);return FullSystemCompletion.RestoredPrevious;
        }
        private void Complete(string result)
        {
            var value=new NativeFullSystemDone(1,owner.anchor.InstallationId,id,InstalledActivationResolver.Hash(original),result);var path=owner.files.PathOf(DoneRelative(id));
            if(File.Exists(path)){if(!owner.files.Read(path,8192).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(value)))throw new IOException("FULL_SYSTEM_NATIVE_COMPLETION_REPLAY");}
            else owner.PublishAtomic(path,value);terminal=true;retained=false;unresolved.TryRemove(id,out _);
        }
        public void RetainUnresolvedOwnership(){retained=true;unresolved.TryAdd(id,this);}
        private sealed class ReturnLifecycle(Session session,WindowsScmRuntimeLifecycle runtime):IAuthorityProbationLifecycle
        {
            public string Phase=>runtime.Phase;public bool CleanupReconciliationRequired=>runtime.CleanupReconciliationRequired;
            public Task StartCommittedAsync(InstallationBinding binding,AppRelease expected,CancellationToken cancellation=default)=>runtime.StartCommittedAsync(binding,expected,cancellation);
            public async Task StartProbationAsync(ProbationRuntimeBinding binding,CancellationToken cancellation)
            {
                binding.Validate();var raw=session.owner.ReadCommitted();var selected=NativeJson.Read<InstalledActivation>(raw);
                if(binding.Binding!=new InstallationBinding(session.owner.anchor.InstallationId,InstalledActivationResolver.Hash(raw))||binding.DataId!=selected.DataId||binding.ReleaseDigest!=selected.ReleaseDigest)throw new IOException("FULL_SYSTEM_RETURN_RUNTIME_BINDING");
                if(SourceDirectory(session.owner.files,selected)==SourceDirectory(session.owner.files,session.source))
                {session.data?.Dispose();session.data=null;session.Custody?.Data.Dispose();}
                await runtime.StartProbationAsync(binding,cancellation);session.owner.track(runtime);
            }
            public Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation)=>runtime.ReleaseEffectsAsync(binding,requestId,cancellation);
            public Task StopAsync(CancellationToken cancellation=default)=>runtime.StopAsync(cancellation);
            public Task RetryCleanupAsync(CancellationToken cancellation=default)=>runtime.RetryCleanupAsync(cancellation);
        }
        public ValueTask DisposeAsync()
        {
            if(disposed)return ValueTask.CompletedTask;if(retained||!terminal&&!handed){RetainUnresolvedOwnership();return ValueTask.CompletedTask;}
            helper?.Dispose();transaction?.Dispose();bootstrap?.Dispose();data?.Dispose();serial?.Dispose();proof?.Dispose();Custody?.Dispose();Receiver?.Dispose();foreach(var observed in cancellationOwners)observed.Dispose();cancellationOwners.Clear();disposed=true;return ValueTask.CompletedTask;
        }
    }
}
