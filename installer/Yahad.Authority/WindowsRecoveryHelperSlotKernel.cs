using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using System.Security.Principal;
using Yahad.Install;
namespace Yahad.Authority;

internal sealed record RecoveryHelperProbeFrame(int Protocol,string OperationId,string ManifestDigest,string Nonce,RecoveryHelperSlot Slot,
    string ClosureManifestDigest,string Version,string EntrySha256,int ProcessId,long Created,int ParentPid,long ParentCreated,string Phase);

internal sealed class WindowsRecoveryHelperSlotKernel(InstalledStateFiles files,InstalledStateFiles binaries,SystemBootstrapNative publisher):IRecoveryHelperSlotKernel
{
    private SystemCandidateProbeOwnershipException? pending;private readonly List<FileStream> retainedPins=[];
    public bool IsExecuting(RecoveryHelperSlot slot)=>Environment.ProcessPath is{} current&&Path.GetFullPath(current).Equals(BootstrapRecoveryLayout.Image(slot),StringComparison.OrdinalIgnoreCase);
    public void RequireProvisioned()
    {
        NativeCleanupOwnershipGate.RequireClear();
        if(binaries.Roles is null||files.Roles is null||binaries.Root!=InstalledUpdaterLayout.BinaryRoot||files.Root!=InstalledUpdaterLayout.StateRoot)throw new IOException("HELPER_SLOT_INSTALLED_ROOTS");
        binaries.ValidateRoot();files.ValidateRoot();foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())RecoveryHelperScmIdentity.Require(slot,null);
    }
    public Task RequireClosureAsync(RecoveryHelperSlot slot,RecoveryHelperClosure closure,CancellationToken cancellation)=>publisher.RequireRecoveryHelperClosureAsync(slot,closure,cancellation);
    public async Task PublishAsync(VerifiedSystemOperation operation,RecoveryHelperSlot slot,RecoveryHelperClosure wanted,IReadOnlyList<RecoveryHelperClosure> prior,bool rollback,CancellationToken cancellation)
    {
        NativeCleanupOwnershipGate.RequireClear();if(pending is not null)throw new IOException("HELPER_PROBE_OWNER_RETAINED");
        if(IsExecuting(slot))throw new IOException("HELPER_SLOT_STILL_EXECUTING");RecoveryHelperScmIdentity.Require(slot,null,true);
        await publisher.PublishRecoveryHelperSlotAsync(operation,slot,wanted,prior,rollback,cancellation);
    }
    public async Task<RecoveryHelperHealth> ObserveHealthyAsync(VerifiedSystemOperation operation,RecoveryHelperSlot slot,RecoveryHelperClosure closure,string nonce,CancellationToken cancellation)
    {
        NativeCleanupOwnershipGate.RequireClear();if(pending is not null)throw new IOException("HELPER_PROBE_OWNER_RETAINED");await RequireClosureAsync(slot,closure,cancellation);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var root=files.PathOf(".authority/bootstrap-helper-probes");if(!Directory.Exists(root))files.CreateDirectory(root);
        var temporary=files.PathOf(".authority/bootstrap-helper-probe-temp");if(!Directory.Exists(temporary))files.CreateDirectory(temporary);
        JobProcess? owned=null;SafeProcessHandle? process=null;RecoveryHelperProbeFrame? request=null;
        try
        {
            foreach(var file in closure.Files)retainedPins.Add(SystemBootstrapHelperNative.PinImage(Path.Combine(BootstrapRecoveryLayout.Directory(slot),file.RelativePath.Replace('/',Path.DirectorySeparatorChar)),file.Sha256));
            using var parent=SystemBootstrapHelperNative.OpenSelf();var parentIdentity=NativeCustodyTransfer.RequireTarget(parent);
            var parentUser=WindowsIdentity.GetCurrent().User??throw new IOException("HELPER_PROBE_PARENT_ACCOUNT");
            owned=JobProcess.Start(BootstrapRecoveryLayout.Image(slot),["--recovery-slot-probe"],BootstrapRecoveryLayout.Directory(slot),LaunchEnvironment.Clean(temporary),(handle,pid)=>
            {
                var identity=NativeCustodyTransfer.ObserveProbe(handle,slot,parentUser);if(identity.Pid!=pid)throw new IOException("HELPER_PROBE_NATIVE_IDENTITY");
                process=OpenProcess(0x00101000,false,pid);if(process.IsInvalid)throw new Win32Exception();
                request=new(1,operation.OperationId,operation.ManifestDigest,nonce,slot,closure.ManifestDigest,closure.Version,closure.EntrySha256,pid,identity.Created,parentIdentity.Pid,parentIdentity.Created,"request");
                SystemBootstrapAuthorityChannel.Write(files,Path.Combine(root,nonce+"-request.json"),request);
                SystemBootstrapAuthorityChannel.Write(files,files.PathOf(".authority/bootstrap-helper-probe-active.json"),request);
            });
            var bound=request??throw new IOException("HELPER_PROBE_REQUEST_UNOBSERVED");
            void Peer(){if(process is null||NativeCustodyTransfer.ObserveProbe(process,slot,parentUser)!=(bound.ProcessId,bound.Created))throw new IOException("HELPER_PROBE_GENERATION_CHANGED");}
            var ready=await SystemBootstrapAuthorityChannel.ReadAsync<RecoveryHelperProbeFrame>(files,Path.Combine(root,nonce+"-ready.json"),deadline.Token,Peer);
            if(ready!=(bound with{Phase="ready"}))throw new UnauthorizedAccessException("HELPER_PROBE_RESPONSE_BINDING");Peer();
            SystemBootstrapAuthorityChannel.Write(files,Path.Combine(root,nonce+"-ack.json"),bound with{Phase="accepted"});
            if(await owned.WaitAsync(deadline.Token)!=0)throw new IOException("HELPER_PROBE_EXIT");await owned.WaitForTreeEmptyAsync(deadline.Token);
            owned.Dispose();owned=null;return new(slot,bound.ProcessId,bound.Created,bound.EntrySha256,1,nonce);
        }
        catch(Exception error)
        {
            if(error is JobStartQuiescenceUnconfirmedException failed)owned=failed.OwnedProcess;
            if(owned is not null)
            {
                try{using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));await owned.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);owned.Dispose();owned=null;}
                catch(Exception cleanup)
                {
                    var uncertain=owned??throw new IOException("HELPER_PROBE_OWNER_LOST",cleanup);var resources=retainedPins.Cast<IDisposable>().ToArray();retainedPins.Clear();
                    pending=new SystemCandidateProbeOwnershipException(uncertain,resources,new AggregateException(error,cleanup),()=>pending=null);throw pending;
                }
            }
            throw;
        }
        finally{process?.Dispose();if(pending is null){foreach(var pin in retainedPins)pin.Dispose();retainedPins.Clear();}}
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
}

// Candidate probe accepts identifiers only, writes one protected nonce-bound
// response, and waits for the actual pinned parent's acknowledgement. It has no
// update/data/normal-app entry and never broadens a revoked grant.
internal static class SystemBootstrapHelperProbeApplication
{
    internal static void RequireCurrentProbe(InstalledFileRoles roles)
    {
        var files=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,roles,"","");
        var request=NativeJson.Read<RecoveryHelperProbeFrame>(files.Read(files.PathOf(".authority/bootstrap-helper-probe-active.json"),8192));
        Yahad.Service.InstalledActivationResolver.Id(request.OperationId);Yahad.Service.InstalledActivationResolver.Id(request.Nonce);
        if(!Enum.IsDefined(request.Slot))throw new IOException("HELPER_PROBE_REQUEST_BINDING");
        using var self=SystemBootstrapHelperNative.OpenSelf();var user=WindowsIdentity.GetCurrent().User??throw new IOException("HELPER_PROBE_PARENT_ACCOUNT");
        var actual=NativeCustodyTransfer.ObserveProbe(self,request.Slot,user);
        if(request.Protocol!=1||request.Phase!="request"||request.ProcessId!=actual.Pid||request.Created!=actual.Created||
            NativeJson.Read<RecoveryHelperProbeFrame>(files.Read(files.PathOf(".authority/bootstrap-helper-probes/"+request.Nonce+"-request.json"),8192))!=request)throw new IOException("HELPER_PROBE_REQUEST_BINDING");
        using var parent=OpenProcess(0x00101000,false,request.ParentPid);if(parent.IsInvalid)throw new Win32Exception();
        if(NativeCustodyTransfer.ObserveService(parent,user)!=(request.ParentPid,request.ParentCreated))throw new IOException("HELPER_PROBE_PARENT_GENERATION");
    }
    internal static async Task<int> RunInstalledAsync(string[] args,FullSystemUpdatePolicy policy,CancellationToken cancellation=default)
    {
        if(args is not["--recovery-slot-probe"])throw new IOException("HELPER_PROBE_ARGUMENTS");
        _=InstalledAuthorityReader.Read(true);
        var files=InstalledStateFiles.Installed(InstalledUpdaterLayout.StateRoot,SystemBootstrapHelperNative.Roles(),"","");
        var request=NativeJson.Read<RecoveryHelperProbeFrame>(files.Read(files.PathOf(".authority/bootstrap-helper-probe-active.json"),8192));
        var id=request.OperationId;var nonce=request.Nonce;Yahad.Service.InstalledActivationResolver.Id(id);Yahad.Service.InstalledActivationResolver.Id(nonce);
        var root=files.PathOf(".authority/bootstrap-helper-probes");
        if(NativeJson.Read<RecoveryHelperProbeFrame>(files.Read(Path.Combine(root,nonce+"-request.json"),8192))!=request)throw new IOException("HELPER_PROBE_ACTIVE_BINDING");
        var op=policy.RemintBootstrapRecovery(id);using var self=SystemBootstrapHelperNative.OpenSelf();var user=WindowsIdentity.GetCurrent().User!;
        var actual=NativeCustodyTransfer.ObserveProbe(self,request.Slot,user);
        if(request.Protocol!=1||request.OperationId!=id||request.ManifestDigest!=op.ManifestDigest||request.Nonce!=nonce||request.Phase!="request"||request.ProcessId!=actual.Pid||request.Created!=actual.Created||
            !Enum.IsDefined(request.Slot)||!SystemBootstrapHelperNative.Image(self).Equals(BootstrapRecoveryLayout.Image(request.Slot),StringComparison.OrdinalIgnoreCase))throw new IOException("HELPER_PROBE_REQUEST_BINDING");
        var selected=SystemBootstrapRecoverySlots.Read(files,op.InstallationId,op.PolicyDigest);
        var closure=selected.Slots.SelectMany(s=>s.Prior is{} prior?new[]{s.Closure,prior}:new[]{s.Closure}).FirstOrDefault(c=>c.ManifestDigest==request.ClosureManifestDigest&&c.Version==request.Version&&c.EntrySha256==request.EntrySha256)??throw new IOException("HELPER_PROBE_SOURCE_NOT_SELECTED");
        var binaries=InstalledStateFiles.Installed(InstalledUpdaterLayout.BinaryRoot,SystemBootstrapHelperNative.Roles(),"","");
        foreach(var member in closure.Files)
        {
            using var protectedMember=binaries.Open(Path.Combine(BootstrapRecoveryLayout.Directory(request.Slot),member.RelativePath.Replace('/',Path.DirectorySeparatorChar)));
            if(protectedMember.Length!=member.Size)throw new IOException("HELPER_PROBE_SOURCE_SIZE");
            using var pinned=SystemBootstrapHelperNative.PinImage(Path.Combine(BootstrapRecoveryLayout.Directory(request.Slot),member.RelativePath.Replace('/',Path.DirectorySeparatorChar)),member.Sha256);
        }
        using var image=SystemBootstrapHelperNative.PinImage(BootstrapRecoveryLayout.Image(request.Slot),request.EntrySha256);
        using var parent=OpenProcess(0x00101000,false,request.ParentPid);if(parent.IsInvalid)throw new Win32Exception();
        void Peer(){if(NativeCustodyTransfer.ObserveService(parent,user)!=(request.ParentPid,request.ParentCreated))throw new IOException("HELPER_PROBE_PARENT_GENERATION");}
        Peer();SystemBootstrapAuthorityChannel.Write(files,Path.Combine(root,nonce+"-ready.json"),request with{Phase="ready"});
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        var ack=await SystemBootstrapAuthorityChannel.ReadAsync<RecoveryHelperProbeFrame>(files,Path.Combine(root,nonce+"-ack.json"),deadline.Token,Peer);
        if(ack!=(request with{Phase="accepted"}))throw new IOException("HELPER_PROBE_ACK_BINDING");return 0;
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
}

// Read/query only. The two fixed automatic boot services are installer contract,
// not runtime-created services. No SERVICE_CHANGE_CONFIG or account fallback.
internal static class RecoveryHelperScmIdentity
{
    internal static SafeProcessHandle? PinRunningHelper(InstalledStateFiles files,InstalledStateFiles binaries,VerifiedSystemOperation operation,CancellationToken cancellation=default)
    {
        var selection=SystemBootstrapRecoverySlots.Read(files,operation.InstallationId,operation.PolicyDigest);
        foreach(var slot in Enum.GetValues<RecoveryHelperSlot>())
        {
            Require(slot,null);using var manager=new Handle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
            using var service=new Handle(OpenServiceW(manager,BootstrapRecoveryLayout.ServiceName(slot),5));if(service.IsInvalid)throw new Win32Exception();
            if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<Status>(),out _))throw new Win32Exception();
            if(status.State is not(2 or 4)||status.Pid==0)continue;
            var process=OpenProcess(0x00101000,false,status.Pid);if(process.IsInvalid)throw new Win32Exception();
            try
            {
                var actual=NativeCustodyTransfer.RequireTarget(process);if(actual.Pid!=(int)status.Pid||!SystemBootstrapHelperNative.Image(process).Equals(BootstrapRecoveryLayout.Image(slot),StringComparison.OrdinalIgnoreCase))throw new IOException("HELPER_BOOT_OBSERVER_IDENTITY");
                Require(slot,actual.Pid);var known=selection.Slots.Single(s=>s.Slot==slot);
                try{SystemBootstrapRecoveryBootApplication.RequireClosure(binaries,slot,known.Closure,cancellation);}
                catch(IOException)when(known.Prior is not null){SystemBootstrapRecoveryBootApplication.RequireClosure(binaries,slot,known.Prior,cancellation);}
                Require(slot,actual.Pid);return process;
            }
            catch{process.Dispose();throw;}
        }
        return null;
    }
    internal static void Require(RecoveryHelperSlot slot,int? expectedPid,bool stopped=false)
        =>UpdaterServiceIdentity.Require(UpdaterServiceIdentity.ForSlot(slot),expectedPid,stopped);
    [StructLayout(LayoutKind.Sequential)]private struct Status{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    private sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid{internal Handle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(Handle manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(Handle service,int kind,out Status status,uint size,out uint needed);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
}
