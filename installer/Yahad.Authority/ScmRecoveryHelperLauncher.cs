using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Authority;

internal sealed record RecoveryHelperLaunchIntent(int Format,string OperationId,string ManifestDigest,string Nonce,RecoveryHelperSlot Slot,int ParentPid,long ParentCreated,NativeDataLeaseTransfer? SerialWitness=null,bool RecoveryOnly=false);
internal sealed class RecoveryHelperStartOwnershipException:IOException
{
    internal RecoveryHelperStartOwnershipException(SafeFileHandle job,SafeProcessHandle? process,RecoveryHelperScmControl service,IReadOnlyList<IDisposable> resources,Exception failure):base("SCM_HELPER_START_CLEANUP_UNCONFIRMED",failure)
    {NativeCleanupOwnershipGate.Retain(this,new RetainedStart(job,process,service,resources).CleanupAsync);}
    // Internal failure-injection seam: a delegate is not an ownership grant.
    // Production always retains the actual service/process/job and image pins.
    internal RecoveryHelperStartOwnershipException(Func<CancellationToken,Task> observedCleanup,Exception failure):base("SCM_HELPER_START_CLEANUP_UNCONFIRMED",failure)
    {NativeCleanupOwnershipGate.Retain(this,observedCleanup);}
    internal Task RetryCleanupAsync(CancellationToken cancellation)=>NativeCleanupOwnershipGate.RetryAsync(this,cancellation);
    private sealed class RetainedStart(SafeFileHandle job,SafeProcessHandle? process,RecoveryHelperScmControl service,IReadOnlyList<IDisposable> resources)
    {
        internal async Task CleanupAsync(CancellationToken cancellation)
        {
            service.Stop();if(process is not null){SystemBootstrapHelperNative.Terminate(job);await SystemBootstrapHelperNative.WaitExitAsync(process,cancellation);}
            while(!JobAccounting.Read(job).TreeEmpty||service.Status().State!=1){cancellation.ThrowIfCancellationRequested();await Task.Delay(20,cancellation);}
            process?.Dispose();job.Dispose();service.Dispose();foreach(var resource in resources)resource.Dispose();
        }
    }
}

// Same A/B SCM one-shot host for normal handoff AND crash/boot recovery. Trusted
// ServiceMain waits for this protected intent and native capsule; it releases no
// child or data operation before actual owned-job/custody adoption.
internal static class ScmRecoveryHelperLauncher
{
    internal static SystemBootstrapHelperSession Start(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        SystemBootstrapSnapshot before,RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,string dataDirectory,FullSystemUpdatePolicy policy)
        =>StartCore(files,binaries,operation,before,runtime,data,serial,dataDirectory,policy,false);
    internal static SystemBootstrapHelperSession StartRecovery(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        SystemBootstrapSnapshot before,RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,string dataDirectory,FullSystemUpdatePolicy policy)
        =>StartCore(files,binaries,operation,before,runtime,data,serial,dataDirectory,policy,true);
    private static SystemBootstrapHelperSession StartCore(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,
        SystemBootstrapSnapshot before,RetainedRuntimeQuiescence runtime,WindowsFileBoundary.DataLease data,WindowsFileBoundary.DataLease serial,string dataDirectory,FullSystemUpdatePolicy policy,bool recoveryOnly)
    {
        NativeCleanupOwnershipGate.RequireClear();_=InstalledAuthorityReader.Read();
        SystemBootstrapHelperCapsule? recoveryCapsule=null;SystemBootstrapHelperMarker? origin=null;
        if(recoveryOnly)
        {
            recoveryCapsule=NativeJson.Read<SystemBootstrapHelperCapsule>(files.Read(files.PathOf(".authority/bootstrap-helper-active.json"),65536));SystemBootstrapHelperApplication.RequireCapsule(recoveryCapsule);
            if(recoveryCapsule.OperationId!=operation.OperationId||recoveryCapsule.ManifestDigest!=operation.ManifestDigest||recoveryCapsule.DataDirectory!=dataDirectory)throw new IOException("SCM_HELPER_RECOVERY_SCOPE");
            origin=SystemBootstrapHelperApplication.RequireGoEvidence(files,recoveryCapsule);
        }
        void Admission()
        {
            if(!recoveryOnly){policy.RequireCurrent(operation);return;}
            policy.RequireRecoveryEvidence(operation);_=SystemBootstrapHelperApplication.RequireGoEvidence(files,recoveryCapsule!);
            using var held=data.DuplicateForDirectory(dataDirectory);using var gate=serial.DuplicateForDirectory(files.PathOf(".authority/update-serial"));
            var binding=Yahad.Service.WindowsInstallationReader.Parse(files.Read(files.PathOf("bootstrap.json"),1024));
            if(binding!=new Yahad.Service.InstallationBinding(operation.InstallationId,operation.PreviousActivationDigest)||Yahad.Service.InstalledActivationResolver.Hash(files.Read(files.PathOf("activation.json"),16384))!=operation.PreviousActivationDigest)throw new IOException("SCM_HELPER_RECOVERY_DATA_HEAD");
            var record=SystemBootstrapJournal.Inspect(files,operation.InstallationId,operation.OperationId);
            if(record is null||record.Phase is SystemBootstrapPhase.Complete or SystemBootstrapPhase.RolledBack)throw new IOException("SCM_HELPER_RECOVERY_NONTERMINAL_REQUIRED");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));runtime.RequireAsync(deadline.Token).GetAwaiter().GetResult();
        }
        Admission();
        if(files.Roles is null||binaries.Roles is null||files.Root!=InstalledUpdaterLayout.StateRoot||binaries.Root!=InstalledUpdaterLayout.BinaryRoot||before.InstallationId!=operation.InstallationId||before.Epoch!=operation.ExpectedBootstrapEpoch||before.Version!=operation.BootstrapFromVersion)throw new IOException("SCM_HELPER_LAUNCH_SCOPE");
        var selection=SystemBootstrapRecoverySlots.Read(files,operation.InstallationId,operation.PolicyDigest);var slot=selection.Active;
        var helper=selection.Slots.Single(s=>s.Slot==slot);if(helper.Phase!=RecoveryHelperSlotPhase.Verified)throw new IOException("SCM_HELPER_SLOT_UNVERIFIED");
        SystemBootstrapRecoveryBootApplication.RequireClosure(binaries,slot,helper.Closure,default);
        var pins=new List<FileStream>();SafeFileHandle? job=null;SafeProcessHandle? process=null;RecoveryHelperScmControl? service=null;bool attempted=false,uncertain=false;
        try
        {
            foreach(var file in helper.Closure.Files)pins.Add(SystemBootstrapHelperNative.PinImage(Path.Combine(BootstrapRecoveryLayout.Directory(slot),file.RelativePath.Replace('/',Path.DirectorySeparatorChar)),file.Sha256));
            service=new(slot);if(service.Status()!=((uint)1,(uint)0))throw new IOException("SCM_HELPER_ALREADY_ACTIVE");
            using var parent=SystemBootstrapHelperNative.OpenSelf();var parentId=NativeCustodyTransfer.RequireTarget(parent);SystemBootstrapHelperNative.RequireAuthorityImage(parent);
            if(!WindowsAuthorityServiceIdentity.IsCurrentServer((uint)parentId.Pid))throw new IOException("SCM_HELPER_PARENT_NOT_AUTHORITY");
            var nonce=Guid.NewGuid().ToString("N");var intent=new RecoveryHelperLaunchIntent(1,operation.OperationId,operation.ManifestDigest,nonce,slot,parentId.Pid,parentId.Created,serial.BorrowNativeWitness(),recoveryOnly);
            SystemBootstrapAuthorityChannel.Write(files,files.PathOf(".authority/bootstrap-helper-launch.json"),intent);
            job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception();SetLimits(job);
            Admission();attempted=true;service.Start();var until=Environment.TickCount64+30000;
            while(process is null)
            {
                var status=service.Status();if(status.State==1)throw new IOException("SCM_HELPER_EXITED_BEFORE_CAPSULE");
                if(status.Pid!=0)
                {
                    process=OpenProcess(0x00101141,false,status.Pid);if(process.IsInvalid){var error=Marshal.GetLastWin32Error();process.Dispose();process=null;throw new Win32Exception(error);}
                    var actual=NativeCustodyTransfer.RequireTarget(process);RecoveryHelperScmIdentity.Require(slot,actual.Pid);
                    if(!SystemBootstrapHelperNative.Image(process).Equals(BootstrapRecoveryLayout.Image(slot),StringComparison.OrdinalIgnoreCase))throw new IOException("SCM_HELPER_FIXED_IMAGE");
                }
                else{if(Environment.TickCount64>=until)throw new IOException("SCM_HELPER_PID_DEADLINE");Thread.Sleep(20);}
            }
            if(!IsProcessInJob(process,IntPtr.Zero,out var nested)||nested)throw new IOException("SCM_HELPER_UNEXPECTED_JOB");
            if(!AssignProcessToJobObject(job,process))throw new Win32Exception();SystemBootstrapHelperNative.RequireJob(process,job);
            var bound=NativeCustodyTransfer.RequireTarget(process);var handles=NativeCustodyTransfer.DuplicateTo(process,[job,parent]);
            var capsule=new SystemBootstrapHelperCapsule(1,operation.OperationId,operation.ManifestDigest,nonce,bound.Pid,bound.Created,helper.Closure.EntrySha256,dataDirectory,handles[0],handles[1],parentId.Pid,parentId.Created,
                runtime.DuplicateNativeHandlesForProcess(process),data.DuplicateNativeHandlesForProcess(process),serial.DuplicateNativeHandlesForProcess(process),recoveryOnly,origin);
            SystemBootstrapHelperApplication.RequireCapsule(capsule);Admission();
            SystemBootstrapAuthorityChannel.Write(files,files.PathOf(".authority/bootstrap-helper-active.json"),capsule);
            var result=new SystemBootstrapHelperSession(files,capsule,process,job,policy,operation,slot,Admission);process=null;job=null;return result;
        }
        catch(Exception failure)
        {
            if(attempted&&job is not null&&service is not null)
            {
                try
                {
                    service.Stop();if(process is not null)SystemBootstrapHelperNative.TerminateAndObserve(job,process);
                    else if(service.Status().State!=1||!JobAccounting.Read(job).TreeEmpty)throw new IOException("SCM_HELPER_UNOBSERVED_START");
                }
                catch(Exception cleanup){uncertain=true;throw new RecoveryHelperStartOwnershipException(job,process,service,pins.ToArray(),new AggregateException(failure,cleanup));}
            }
            throw;
        }
        finally{if(!uncertain){foreach(var pin in pins)pin.Dispose();process?.Dispose();job?.Dispose();service?.Dispose();}}
    }
    private static void SetLimits(SafeFileHandle job)
    {
        var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=0x2000|8,ActiveProcesses=4}};var memory=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        try{Marshal.StructureToPtr(limits,memory,false);if(!SetInformationJobObject(job,9,memory,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimit{public long ProcessTime,JobTime;public uint Flags;public UIntPtr Minimum,Maximum;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong ReadOperations,WriteOperations,OtherOperations,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit{public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,IntPtr value,uint length);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool IsProcessInJob(SafeProcessHandle process,IntPtr job,out bool inside);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
}

// Fixed two-name SCM control only; rights QUERY_CONFIG/QUERY_STATUS/START/STOP.
internal sealed class RecoveryHelperScmControl:IDisposable
{
    private readonly RecoveryHelperSlot slot;private readonly Handle manager,service;
    internal RecoveryHelperScmControl(RecoveryHelperSlot slot)
    {this.slot=slot;RecoveryHelperScmIdentity.Require(slot,null);manager=new(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();service=new(OpenServiceW(manager,BootstrapRecoveryLayout.ServiceName(slot),0x35));if(service.IsInvalid){manager.Dispose();throw new Win32Exception();}}
    internal (uint State,uint Pid) Status()
    {RecoveryHelperScmIdentity.Require(slot,null);if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<StatusRecord>(),out _))throw new Win32Exception();return(status.State,status.Pid);}
    internal void Start(){if(Status().State!=1)throw new IOException("SCM_HELPER_COLLISION");if(!StartServiceW(service,0,IntPtr.Zero))throw new Win32Exception();}
    internal void Stop(){var state=Status().State;if(state is 1 or 3)return;if(!ControlService(service,1,out _)){var error=Marshal.GetLastWin32Error();if(error!=1062)throw new Win32Exception(error);}}
    public void Dispose(){service.Dispose();manager.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct StatusRecord{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    private sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid{internal Handle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(Handle manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(Handle service,int kind,out StatusRecord status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool StartServiceW(Handle service,int argc,IntPtr argv);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(Handle service,uint control,out StatusRecord status);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
