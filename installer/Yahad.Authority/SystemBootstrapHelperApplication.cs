using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record SystemBootstrapHelperCapsule(int Format,string OperationId,string ManifestDigest,string Nonce,int HelperPid,long HelperCreated,
    string HelperImageDigest,string DataDirectory,ulong HelperJob,ulong OldAuthority,int OldAuthorityPid,long OldAuthorityCreated,
    NativeRuntimeTransfer Runtime,NativeDataLeaseTransfer Data,NativeDataLeaseTransfer Serial,bool RecoveryOnly=false,SystemBootstrapHelperMarker? RecoveryOrigin=null);
internal sealed record SystemBootstrapHelperMarker(int Format,string OperationId,string ManifestDigest,string Nonce,int HelperPid,long HelperCreated,string Phase);
internal sealed record SystemBootstrapAuthorityTransfer(string OperationId,string ManifestDigest,string Nonce,int TargetPid,long TargetCreated,
    ulong Helper,long HelperCreated,ulong HelperJob,ulong OldAuthority,long OldAuthorityCreated,NativeRuntimeTransfer Runtime,NativeDataLeaseTransfer Data,NativeDataLeaseTransfer Serial);
internal sealed record SystemBootstrapRecoveredCustody(SystemBootstrapHelperCustody Custody,string DataDirectory);
internal enum HelperHandoffObservation {Active,QuiescedWithoutPublishedGo,QuiescedWithPublishedGo}
internal static class HelperHandoffObservationRules
{
    // Observation-only, internal fake seam. Production supplies actual retained
    // kernel observations, never a caller frame or ownership Boolean.
    internal static HelperHandoffObservation Select(bool rootExited,bool treeEmpty,Func<bool> publishedGo)
        =>!rootExited||!treeEmpty?HelperHandoffObservation.Active:publishedGo()?HelperHandoffObservation.QuiescedWithPublishedGo:HelperHandoffObservation.QuiescedWithoutPublishedGo;
}

// Trusted composition callbacks belong to the full-system coordinator. They
// cannot be selected by CLI, IPC request, environment or candidate metadata.
// In particular, Ready must transfer actual retained custody into the NEW
// authenticated Authority and wait for its ownership acknowledgement.
internal sealed record SystemBootstrapHelperHooks(
    FullSystemUpdatePolicy Policy,
    Func<VerifiedSystemOperation,bool,CancellationToken,Task> AuthorityHealth,
    Func<VerifiedSystemOperation,CancellationToken,Task<RetainedRuntimeQuiescence>> RuntimeBootstrap,
    Func<VerifiedSystemOperation,CancellationToken,Task> DataUnchanged,
    Func<VerifiedSystemOperation,CancellationToken,Task> HelperHealth,
    Func<SystemBootstrapHelperCustody,string,VerifiedSystemOperation,CancellationToken,Task> CustodyAcquired,
    Func<VerifiedSystemOperation,CancellationToken,Task> VerifyBeforeDisruption,
    Func<SystemBootstrapHelperCustody,VerifiedSystemOperation,CancellationToken,Task> TransferToNewAuthority,
    Func<VerifiedSystemOperation,CancellationToken,Task<SystemBootstrapRecoveredCustody>> RecoverActualCustody);

// Owns the exact native resources received by this helper. Exporting duplicates
// never releases the original owners; the receiver must acknowledge adoption.
internal sealed class SystemBootstrapHelperCustody:IDisposable
{
    internal RetainedRuntimeQuiescence Runtime{get;}
    internal WindowsFileBoundary.DataLease Data{get;}
    internal WindowsFileBoundary.DataLease Serial{get;}
    internal SafeProcessHandle? OldAuthority{get;}
    internal SafeProcessHandle Helper{get;}
    private readonly SafeFileHandle job;
    private SystemBootstrapHelperCustody(RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,
        SafeProcessHandle? oldAuthority,SafeProcessHandle helper,SafeFileHandle job)
    {Runtime=runtime;Data=data;Serial=serial;OldAuthority=oldAuthority;Helper=helper;this.job=job;}
    internal static SystemBootstrapHelperCustody Adopt(SystemBootstrapHelperCapsule capsule)
    {
        NativeCustodyTransfer.RequireReceiver(capsule.HelperPid,capsule.HelperCreated);
        if(capsule.HelperJob==capsule.OldAuthority)throw new IOException("SYSTEM_HELPER_HANDLE_ALIAS");
        RetainedRuntimeQuiescence? runtime=null;WindowsFileBoundary.DataLease? data=null,serial=null;
        SafeProcessHandle? old=null,helper=null;SafeFileHandle? job=null;
        try
        {
            old=new(NativeCustodyTransfer.Pointer(capsule.OldAuthority),true);
            if(NativeCustodyTransfer.Created(old)!=capsule.OldAuthorityCreated||SystemBootstrapHelperNative.ProcessId(old)!=capsule.OldAuthorityPid)throw new IOException("SYSTEM_HELPER_OLD_AUTHORITY_GENERATION");
            helper=SystemBootstrapHelperNative.OpenSelf();job=new(NativeCustodyTransfer.Pointer(capsule.HelperJob),true);
            SystemBootstrapHelperNative.RequireJob(helper,job);
            runtime=RetainedRuntimeQuiescence.AdoptTransferred(capsule.Runtime);
            data=WindowsFileBoundary.DataLease.AdoptTransferred(capsule.DataDirectory,capsule.Data);
            serial=WindowsFileBoundary.DataLease.AdoptTransferred(Path.Combine(InstalledUpdaterLayout.StateRoot,".authority","update-serial"),capsule.Serial);
            return new(runtime,data,serial,old,helper,job);
        }
        catch{serial?.Dispose();data?.Dispose();runtime?.Dispose();old?.Dispose();helper?.Dispose();job?.Dispose();throw;}
    }
    internal static SystemBootstrapHelperCustody FromRecoveredNativeOwners(RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,SafeProcessHandle? currentStoppedAuthority=null)
    {
        var helper=SystemBootstrapHelperNative.OpenSelfForAssignment();SystemBootstrapHelperNative.RequireHelperImage(helper);
        try{return new(runtime,data,serial,currentStoppedAuthority,helper,SystemBootstrapHelperNative.CreateSelfJob(helper));}catch{helper.Dispose();throw;}
    }
    internal void PrepareCleanExit()=>SystemBootstrapHelperNative.PrepareCleanExit(job,Helper);
    internal RetainedRuntimeQuiescence AcquireHelperQuiescence()=>RetainedRuntimeQuiescence.FromRetainedHostAndJob(Helper,job,SystemBootstrapHelperNative.ProcessId(Helper));
    internal SystemBootstrapAuthorityTransfer DuplicateToAuthority(VerifiedSystemOperation operation,SafeProcessHandle actualAuthority,string nonce)
    {
        InstalledActivationResolver.Id(nonce);var target=NativeCustodyTransfer.RequireTarget(actualAuthority);
        SystemBootstrapHelperNative.RequireAuthorityImage(actualAuthority);
        var handles=NativeCustodyTransfer.DuplicateTo(actualAuthority,OldAuthority is null?[Helper,job]:[Helper,job,OldAuthority]);
        return new(operation.OperationId,operation.ManifestDigest,nonce,target.Pid,target.Created,handles[0],NativeCustodyTransfer.Created(Helper),handles[1],OldAuthority is null?0:handles[2],OldAuthority is null?0:NativeCustodyTransfer.Created(OldAuthority),
            Runtime.DuplicateNativeHandlesForProcess(actualAuthority),Data.DuplicateNativeHandlesForProcess(actualAuthority),Serial.DuplicateNativeHandlesForProcess(actualAuthority));
    }
    public void Dispose(){Serial.Dispose();Data.Dispose();Runtime.Dispose();OldAuthority?.Dispose();Helper.Dispose();job.Dispose();}
}

// Initial installer SCM failure command is exactly the fixed handoff image plus
// --system-handoff. No caller-selected operation, path or account is accepted.
// Program activation remains a separate root/installer acceptance decision.
internal static class SystemBootstrapHelperApplication
{
    internal static async Task<int> RunInstalledAsync(string[] arguments,SystemBootstrapHelperHooks hooks,CancellationToken cancellation=default)
    {
        if(arguments is not["--system-handoff"])throw new IOException("SYSTEM_HELPER_ARGUMENTS");
        var anchor=InstalledAuthorityReader.Read();var roles=SystemBootstrapHelperNative.Roles();
        var files=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,roles,"","");
        var binaries=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,roles,"","");
        using var self=SystemBootstrapHelperNative.OpenSelf();SystemBootstrapHelperNative.RequireHelperImage(self);
        var capsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(files.PathOf(".authority/bootstrap-helper-active.json"),65536));
        RequireCapsule(capsule);
        var operation=hooks.Policy.RemintBootstrapRecovery(capsule.OperationId);
        if(operation.ManifestDigest!=capsule.ManifestDigest)throw new IOException("SYSTEM_HELPER_OPERATION_BINDING");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(5));
        var receiver=NativeCustodyTransfer.RequireTarget(self);var originalReceiver=receiver==(capsule.HelperPid,capsule.HelperCreated);
        SystemBootstrapRecoveredCustody owned;
        if(originalReceiver)
        {
            using var image=SystemBootstrapHelperNative.PinImage(SystemBootstrapHelperNative.HelperImage,capsule.HelperImageDigest);
            owned=new(SystemBootstrapHelperCustody.Adopt(capsule),capsule.DataDirectory);
        }
        else
        {
            // SCM failure/reboot starts a new process generation. Stale numeric
            // handles are never touched. Only a durable previous Go authorizes
            // recovery; fresh waiting-host STOP/data/serial owners are required.
            _=RequireGoEvidence(files,capsule);
            owned=await hooks.RecoverActualCustody(operation,deadline.Token);
        }
        using var custody=owned.Custody;
        await custody.Runtime.RequireAsync(deadline.Token);
        using(var data=custody.Data.DuplicateForDirectory(owned.DataDirectory)){}
        await hooks.CustodyAcquired(custody,owned.DataDirectory,operation,deadline.Token);
        if(originalReceiver){WriteMarker(files,capsule,"ready");await WaitMarkerAsync(files,capsule,"go",deadline.Token);}
        var kernel=new SystemBootstrapNative(files,binaries,anchor,custody.Runtime,custody.OldAuthority,custody.Helper,custody.Data,owned.DataDirectory,
            hooks.AuthorityHealth,hooks.RuntimeBootstrap,hooks.DataUnchanged,hooks.HelperHealth,hooks.Policy.RequireCurrent);
        kernel.ConfigureHelperPublication(new SystemBootstrapRecoverySlots(files,new WindowsRecoveryHelperSlotKernel(files,binaries,kernel),hooks.Policy.RequireCurrent,custody.Serial));
        var handoff=new SystemBootstrapHandoff(kernel,(op,c)=>{c.ThrowIfCancellationRequested();hooks.Policy.RequireCurrent(op);return Task.CompletedTask;});
        using(var journal=new SystemBootstrapJournal(files,operation,custody.Serial))
        {
            if(journal.Current is null)await handoff.PrepareAsync(operation,journal,deadline.Token);
            var state=journal.Current??throw new IOException("SYSTEM_HELPER_MISSING_PREPARATION");
            var forward=!capsule.RecoveryOnly;
            try{hooks.Policy.RequireCurrent(operation);}catch(UnauthorizedAccessException){forward=false;}
            if(state.Phase==SystemBootstrapPhase.Prepared&&forward)await handoff.ContinuePreparedAsync(operation,journal,deadline.Token,hooks.VerifyBeforeDisruption);
            else if(!forward)await handoff.ReconcileRollbackOnlyAsync(operation,journal,deadline.Token);
            else if(state.Phase is not(SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished)&&!(state.Phase==SystemBootstrapPhase.RecoveryRequired&&journal.HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!journal.HasPhase(SystemBootstrapPhase.RollbackIntent)))await handoff.ReconcileAsync(operation,journal,deadline.Token);
            // The still-running helper can never finalize its own replacement.
            if(journal.Current?.Phase is SystemBootstrapPhase.AwaitingHelperExit or SystemBootstrapPhase.HelperIntent or SystemBootstrapPhase.HelperPublished||journal.Current?.Phase==SystemBootstrapPhase.RecoveryRequired&&journal.HasPhase(SystemBootstrapPhase.AwaitingHelperExit)&&!journal.HasPhase(SystemBootstrapPhase.RollbackIntent))
                await hooks.TransferToNewAuthority(custody,operation,deadline.Token);
        }
        custody.PrepareCleanExit();
        return 0;
    }
    internal static void RequireCapsule(SystemBootstrapHelperCapsule value)
    {
        InstalledActivationResolver.Id(value.OperationId);InstalledActivationResolver.Id(value.Nonce);InstalledActivationResolver.Digest(value.ManifestDigest);InstalledActivationResolver.Digest(value.HelperImageDigest);
        if(value.Format!=1||value.HelperPid<1||value.HelperCreated<=0||value.OldAuthorityPid<1||value.OldAuthorityCreated<=0||value.Runtime.TargetPid!=value.HelperPid||value.Runtime.TargetCreated!=value.HelperCreated||
            value.Data.TargetPid!=value.HelperPid||value.Serial.TargetPid!=value.HelperPid||value.Data.TargetCreated!=value.HelperCreated||value.Serial.TargetCreated!=value.HelperCreated)throw new IOException("SYSTEM_HELPER_CAPSULE_BINDING");
        var root=Path.GetFullPath(Path.Combine(InstalledUpdaterLayout.StateRoot,"datasets"))+Path.DirectorySeparatorChar;
        var data=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(value.DataDirectory));
        if(!data.StartsWith(root,StringComparison.OrdinalIgnoreCase)||Path.GetFileName(data)!="data")throw new IOException("SYSTEM_HELPER_DATA_SCOPE");
        var handles=value.Data.Ancestors.Concat(value.Serial.Ancestors).Concat([value.Data.Gate,value.Serial.Gate,value.Runtime.Host,value.Runtime.Job,value.HelperJob,value.OldAuthority]).ToArray();
        if(handles.Length!=handles.Distinct().Count()||handles.Any(x=>x==0||x>long.MaxValue))throw new IOException("SYSTEM_HELPER_CAPSULE_HANDLES");
        if(value.RecoveryOnly!=(value.RecoveryOrigin is not null))throw new IOException("SYSTEM_HELPER_RECOVERY_ORIGIN");
        if(value.RecoveryOrigin is{} origin)
        {InstalledActivationResolver.Id(origin.Nonce);if(origin.Format!=1||origin.OperationId!=value.OperationId||origin.ManifestDigest!=value.ManifestDigest||origin.Phase!="go"||origin.HelperPid<1||origin.HelperCreated<=0)throw new IOException("SYSTEM_HELPER_RECOVERY_ORIGIN");}
    }
    internal static string MarkerPath(InstalledStateFiles files,string id,string phase,string nonce)
    {InstalledActivationResolver.Id(id);InstalledActivationResolver.Id(nonce);if(phase is not("ready" or "go"))throw new IOException("SYSTEM_HELPER_MARKER_PHASE");return files.PathOf(".authority/bootstrap-helper-"+id+"-"+nonce+"-"+phase+".json");}
    internal static SystemBootstrapHelperMarker RequireGoEvidence(InstalledStateFiles files,SystemBootstrapHelperCapsule capsule)
    {
        var expected=new SystemBootstrapHelperMarker(1,capsule.OperationId,capsule.ManifestDigest,capsule.Nonce,capsule.HelperPid,capsule.HelperCreated,"go");
        try
        {
            var observed=NativeJson.Read<SystemBootstrapHelperMarker>(files.Read(MarkerPath(files,capsule.OperationId,"go",capsule.Nonce),4096));
            if(observed!=expected)throw new IOException("SYSTEM_HELPER_GO_BINDING");return observed;
        }
        catch(Exception missing)when(missing is FileNotFoundException||missing is Win32Exception native&&native.NativeErrorCode is 2 or 3)
        {
            var origin=capsule.RecoveryOrigin??throw new IOException("SYSTEM_HELPER_GO_REQUIRED",missing);
            var observed=NativeJson.Read<SystemBootstrapHelperMarker>(files.Read(MarkerPath(files,origin.OperationId,"go",origin.Nonce),4096));
            if(observed!=origin)throw new IOException("SYSTEM_HELPER_ORIGIN_GO_BINDING");return observed;
        }
    }
    internal static void WriteMarker(InstalledStateFiles files,SystemBootstrapHelperCapsule capsule,string phase)
    {
        var path=MarkerPath(files,capsule.OperationId,phase,capsule.Nonce);var bytes=JsonSerializer.SerializeToUtf8Bytes(new SystemBootstrapHelperMarker(1,capsule.OperationId,capsule.ManifestDigest,capsule.Nonce,capsule.HelperPid,capsule.HelperCreated,phase));
        var temporary=path+".partial-"+Guid.NewGuid().ToString("N");using(var output=files.Create(temporary)){output.Write(bytes);output.Flush(true);}
        SystemBootstrapHelperNative.Publish(temporary,path);
    }
    internal static async Task WaitMarkerAsync(InstalledStateFiles files,SystemBootstrapHelperCapsule capsule,string phase,CancellationToken cancellation)
    {
        var path=MarkerPath(files,capsule.OperationId,phase,capsule.Nonce);
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                var value=NativeJson.Read<SystemBootstrapHelperMarker>(files.Read(path,4096));
                if(value!=new SystemBootstrapHelperMarker(1,capsule.OperationId,capsule.ManifestDigest,capsule.Nonce,capsule.HelperPid,capsule.HelperCreated,phase))throw new IOException("SYSTEM_HELPER_MARKER_BINDING");return;
            }
            catch(FileNotFoundException){}catch(Win32Exception error)when(error.NativeErrorCode is 2 or 3){}
            await Task.Delay(20,cancellation);
        }
    }
}

// No IPC listener or externally selected target. The old Authority receives
// ReadyAsync only after the helper has adopted actual retained kernel owners.
internal sealed class SystemBootstrapHelperSession:IDisposable
{
    private readonly InstalledStateFiles files;private readonly SystemBootstrapHelperCapsule capsule;private readonly SafeProcessHandle process;private readonly SafeFileHandle job;
    private bool committed,readyObserved,handoffMayHaveBegun;private readonly FullSystemUpdatePolicy policy;private readonly VerifiedSystemOperation operation;
    private readonly RecoveryHelperSlot? scmSlot;
    private readonly Action? requireAdmission;
    internal bool HandoffMayHaveBegun=>handoffMayHaveBegun;
    internal SafeProcessHandle Process=>process;
    internal SystemBootstrapHelperSession(InstalledStateFiles files,SystemBootstrapHelperCapsule capsule,SafeProcessHandle process,SafeFileHandle job,FullSystemUpdatePolicy policy,VerifiedSystemOperation operation,RecoveryHelperSlot? scmSlot=null,Action? requireAdmission=null)
    {this.files=files;this.capsule=capsule;this.process=process;this.job=job;this.policy=policy;this.operation=operation;this.scmSlot=scmSlot;this.requireAdmission=requireAdmission;}
    private void RequireCurrent(){SystemBootstrapHelperNative.RequireCurrent(process,capsule.HelperPid,capsule.HelperCreated);if(scmSlot is{} slot)RecoveryHelperScmIdentity.Require(slot,capsule.HelperPid);}
    internal Task<HelperHandoffObservation> ObserveHandoffAsync(VerifiedSystemOperation expected,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        if(AuthorityProtocol.Digest(expected)!=AuthorityProtocol.Digest(operation)||SystemBootstrapHelperNative.ProcessId(process)!=capsule.HelperPid||NativeCustodyTransfer.Created(process)!=capsule.HelperCreated)throw new IOException("SYSTEM_HELPER_OBSERVATION_BINDING");
        var rootExited=SystemBootstrapHelperNative.Exited(process);var treeEmpty=JobAccounting.Read(job).TreeEmpty;
        return Task.FromResult(HelperHandoffObservationRules.Select(rootExited,treeEmpty,()=>
        {
            try{_=SystemBootstrapHelperApplication.RequireGoEvidence(files,capsule);return true;}
            catch(IOException missing)when(missing.Message=="SYSTEM_HELPER_GO_REQUIRED")
            {
                var final=SystemBootstrapHelperApplication.MarkerPath(files,capsule.OperationId,"go",capsule.Nonce);var prefix=Path.GetFileName(final)+".partial-";
                var partials=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(final)!).Where(p=>Path.GetFileName(p).StartsWith(prefix,StringComparison.Ordinal)).Take(2).ToArray();
                if(partials.Length>1)throw new IOException("SYSTEM_HELPER_GO_PARTIAL_UNCERTAIN");
                foreach(var partial in partials)
                {
                    var suffix=Path.GetFileName(partial)[prefix.Length..];if(!Guid.TryParseExact(suffix,"N",out var id)||id.ToString("N")!=suffix)throw new IOException("SYSTEM_HELPER_GO_PARTIAL_BINDING");
                    using var pinned=files.Open(partial);if(pinned.Length>4096)throw new IOException("SYSTEM_HELPER_GO_PARTIAL_BOUND");
                }
                return false;
            }
        }));
    }
    internal async Task ReadyAsync(CancellationToken cancellation)
    {
        using var ready=CancellationTokenSource.CreateLinkedTokenSource(cancellation);ready.CancelAfter(TimeSpan.FromSeconds(30));
        var marker=SystemBootstrapHelperApplication.WaitMarkerAsync(files,capsule,"ready",ready.Token);
        while(!marker.IsCompleted){RequireCurrent();await Task.WhenAny(marker,Task.Delay(20,ready.Token));}
        await marker;RequireCurrent();readyObserved=true;
    }
    internal void RequireReadyCustody(VerifiedSystemOperation expected)
    {
        if(!readyObserved||committed||handoffMayHaveBegun||expected.OperationId!=operation.OperationId||expected.ManifestDigest!=operation.ManifestDigest||expected.GrantId!=operation.GrantId||expected.GrantBindingDigest!=operation.GrantBindingDigest)throw new IOException("SYSTEM_HELPER_CUSTODY_NOT_READY");
        if(requireAdmission is not null)requireAdmission();else if(capsule.RecoveryOnly)policy.RequireRecoveryEvidence(operation);else policy.RequireCurrent(operation);RequireCurrent();
    }
    internal void CommitHandoff(){RequireReadyCustody(operation);handoffMayHaveBegun=true;SystemBootstrapHelperApplication.WriteMarker(files,capsule,"go");committed=true;}
    public void Dispose()
    {
        // Before commit, retained parent ownership ends the exact bounded job.
        // After commit the helper owns its duplicate, surviving Authority exit.
        if(!handoffMayHaveBegun)SystemBootstrapHelperNative.TerminateAndObserve(job,process);process.Dispose();job.Dispose();
    }
}

internal static class SystemBootstrapHelperNative
{
    internal static string HelperImage
    {
        get
        {
            if(Environment.ProcessPath is{} current&&BootstrapRecoveryLayout.IsHelperImage(current))return Path.GetFullPath(current);
            var anchor=InstalledAuthorityReader.Read();var files=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,Roles(),"","");
            var path=files.PathOf("bootstrap-helper-selection.json");
            try{_=File.GetAttributes(path);}catch(FileNotFoundException){return Path.Combine(InstalledUpdaterLayout.BinaryRoot,"bootstrap","handoff","Yahad.Authority.exe");}
            var value=NativeJson.Read<RecoveryHelperSlotsSelection>(files.Read(path,8*1024*1024));
            return BootstrapRecoveryLayout.Image(SystemBootstrapRecoverySlots.Read(files,anchor.InstallationId,value.PolicyDigest).Active);
        }
    }
    internal static InstalledFileRoles Roles()=>new InstalledFileRoles((System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(System.Security.Principal.SecurityIdentifier)),
        (System.Security.Principal.SecurityIdentifier)new System.Security.Principal.NTAccount("NT SERVICE",InstalledUpdaterLayout.RuntimeServiceName).Translate(typeof(System.Security.Principal.SecurityIdentifier))).WithRecoveryHelpers();
    internal const uint QueryDuplicateAccess=0x00101040,SelfAssignmentAccess=0x00101141;
    internal static SafeProcessHandle OpenSelf()=>OpenProcess(QueryDuplicateAccess,false,Environment.ProcessId);
    internal static SafeProcessHandle OpenSelfForAssignment()=>OpenProcess(SelfAssignmentAccess,false,Environment.ProcessId);
    internal static int ProcessId(SafeProcessHandle process)
    {var pid=GetProcessId(process);if(pid==0)throw new Win32Exception();return checked((int)pid);}
    internal static void RequireJob(SafeProcessHandle process,SafeFileHandle job)
    {if(!IsProcessInJob(process,job,out var inside)||!inside)throw new IOException("SYSTEM_HELPER_JOB_MEMBERSHIP");}
    internal static void RequireOutsideJob(SafeFileHandle job)
    {using var self=OpenSelf();if(!IsProcessInJob(self,job,out var inside)||inside)throw new IOException("SYSTEM_AUTHORITY_INSIDE_HELPER_JOB");}
    internal static void RequireCurrent(SafeProcessHandle process,int pid,long created)
    {if(NativeCustodyTransfer.RequireTarget(process)!=(pid,created))throw new IOException("SYSTEM_HELPER_PROCESS_GENERATION");RequireHelperImage(process);}
    internal static string Image(SafeProcessHandle process)
    {var text=new StringBuilder(32768);var length=text.Capacity;if(!QueryFullProcessImageNameW(process,0,text,ref length))throw new Win32Exception();return Path.GetFullPath(text.ToString());}
    internal static void RequireHelperImage(SafeProcessHandle process){if(!BootstrapRecoveryLayout.IsHelperImage(Image(process)))throw new UnauthorizedAccessException("SYSTEM_HELPER_FIXED_IMAGE");}
    internal static void RequireAuthorityImage(SafeProcessHandle process){if(!Image(process).Equals(InstalledUpdaterLayout.AuthorityExecutable,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("SYSTEM_NEW_AUTHORITY_FIXED_IMAGE");}
    internal static FileStream PinImage(string path,string digest)
    {SafeDirectory.NoReparseAncestors(path);var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);try{if(Convert.ToHexStringLower(SHA256.HashData(input))!=digest)throw new IOException("SYSTEM_HELPER_IMAGE_DIGEST");return input;}catch{input.Dispose();throw;}}
    internal static void Terminate(SafeFileHandle job){if(!TerminateJobObject(job,1))throw new Win32Exception();}
    internal static async Task WaitExitAsync(SafeProcessHandle process,CancellationToken cancellation)
    {while(true){cancellation.ThrowIfCancellationRequested();var state=WaitForSingleObject(process,0);if(state==0)return;if(state!=258)throw new Win32Exception();await Task.Delay(20,cancellation);}}
    internal static bool Exited(SafeProcessHandle process)
    {var result=WaitForSingleObject(process,0);return result switch{0=>true,258=>false,_=>throw new Win32Exception()};}
    internal static void TerminateAndObserve(SafeFileHandle job,SafeProcessHandle process)
    {
        Terminate(job);var until=Environment.TickCount64+5000;
        while(Environment.TickCount64<until){if(WaitForSingleObject(process,0)==0&&JobAccounting.Read(job).TreeEmpty)return;Thread.Sleep(20);}
        throw new IOException("SYSTEM_HELPER_CLEANUP_UNCONFIRMED"); // Caller retains both handles on this path.
    }
    internal static SafeFileHandle CreateSelfJob(SafeProcessHandle self)
    {
        var job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception();
        try
        {
            var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=0x2000|8,ActiveProcesses=4}};var memory=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
            try{Marshal.StructureToPtr(limits,memory,false);if(!SetInformationJobObject(job,9,memory,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}
            if(!AssignProcessToJobObject(job,self))throw new Win32Exception();RequireJob(self,job);return job;
        }
        catch{job.Dispose();throw;}
    }
    internal static void Publish(string source,string target){if(!MoveFileExW(@"\\?\"+source,@"\\?\"+target,9))throw new Win32Exception();}
    internal static void PrepareCleanExit(SafeFileHandle job,SafeProcessHandle self)
    {
        RequireJob(self,job);if(JobAccounting.Read(job).ActiveProcesses!=1)throw new IOException("SYSTEM_HELPER_DESCENDANTS_UNCONFIRMED");
        var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=8,ActiveProcesses=4}};var memory=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        try{Marshal.StructureToPtr(limits,memory,false);if(!SetInformationJobObject(job,9,memory,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new Win32Exception();}
        finally{Marshal.FreeHGlobal(memory);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimit{public long ProcessTime,JobTime;public uint Flags;public UIntPtr Minimum,Maximum;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit{public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,out bool inside);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder image,ref int length);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,IntPtr value,uint length);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string source,string target,uint flags);
}
