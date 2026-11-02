using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Install;

// Numeric handles are a protected, process-generation-bound transport only.
// Adoption always re-observes the actual kernel objects. These records are not
// policy capabilities and are never accepted from app/Owner protocol frames.
internal sealed record NativeRuntimeTransfer(int TargetPid,long TargetCreated,int RootPid,long RootCreated,ulong Host,ulong Job);
internal sealed record NativeDataLeaseTransfer(int TargetPid,long TargetCreated,ulong[] Ancestors,ulong Gate);
internal static class NativeCustodyTransfer
{
    internal static (int Pid,long Created) RequireTarget(SafeProcessHandle process)
    {
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));
        using(var current=WindowsIdentity.GetCurrent())
        {
            if(current.User is null)throw new UnauthorizedAccessException("CUSTODY_AUTHORITY_REQUIRED");
            var actor=UpdaterServiceIdentity.FromUser(current.User,authority);
            if(!string.Equals(Environment.ProcessPath,UpdaterServiceIdentity.Image(actor),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("CUSTODY_ACTOR_FIXED_IMAGE");
            UpdaterServiceIdentity.Require(actor,Environment.ProcessId);
        }
        return ObserveService(process);
    }
    // Query-only observation used by a nonce-bound probe of its SCM parent.
    // No transfer/adoption API accepts a non-SCM child as a custody receiver.
    internal static (int Pid,long Created) ObserveService(SafeProcessHandle process,SecurityIdentifier? expectedUser=null)
    {
        var (pid,created,user,observed)=Observe(process);
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));
        if(expectedUser is not null&&user!=expectedUser)throw new UnauthorizedAccessException("HELPER_PROBE_PARENT_ACCOUNT");
        var target=UpdaterServiceIdentity.FromUser(user,authority);
        if(!observed.Equals(UpdaterServiceIdentity.Image(target),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("CUSTODY_TARGET_FIXED_IMAGE");
        UpdaterServiceIdentity.Require(target,pid);return(pid,created);
    }
    internal static (int Pid,long Created) ObserveProbe(SafeProcessHandle process,RecoveryHelperSlot slot,SecurityIdentifier parentUser)
    {
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledUpdaterLayout.AuthorityServiceName).Translate(typeof(SecurityIdentifier));
        _=UpdaterServiceIdentity.FromUser(parentUser,authority);
        var (pid,created,user,image)=Observe(process);
        if(user!=parentUser||!image.Equals(BootstrapRecoveryLayout.Image(slot),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("HELPER_PROBE_NATIVE_IDENTITY");
        return(pid,created);
    }
    private static (int Pid,long Created,SecurityIdentifier User,string Image) Observe(SafeProcessHandle process)
    {
        if(process.IsClosed||process.IsInvalid||!GetProcessTimes(process,out var created,out _,out _,out _))throw new Win32Exception();
        var pid=GetProcessId(process);if(pid==0||pid>int.MaxValue||WaitForSingleObject(process,0)!=258)throw new IOException("CUSTODY_TARGET_GENERATION");
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
        SecurityIdentifier user;
        using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))user=identity.User??throw new UnauthorizedAccessException("CUSTODY_TARGET_ACCOUNT");
        var path=new StringBuilder(32768);var length=path.Capacity;if(!QueryFullProcessImageNameW(process,0,path,ref length))throw new Win32Exception();
        return((int)pid,created,user,Path.GetFullPath(path.ToString()));
    }
    internal static void RequireReceiver(int pid,long created)
    {
        using var self=OpenProcess(0x00101000,false,Environment.ProcessId);var actual=RequireTarget(self);
        if(actual.Pid!=pid||actual.Created!=created)throw new UnauthorizedAccessException("CUSTODY_WRONG_RECEIVER");
    }
    internal static long Created(SafeProcessHandle process)
    {if(!GetProcessTimes(process,out var created,out _,out _,out _))throw new Win32Exception();return created;}
    internal static (int Pid,long Created) CurrentIdentity()
    {using var self=OpenProcess(0x00101000,false,Environment.ProcessId);return RequireTarget(self);}
    // Busy is observed by duplicating the ACTUAL peer-owned exclusive gate,
    // validating its process generation/file IDs/role through normal adoption,
    // and immediately disposing the observation. A sharing error alone is not
    // treated as an authenticated owner or permission to proceed.
    internal static WindowsFileBoundary.DataLease ObservePeerSerialOwner(NativeDataLeaseTransfer witness)
    {
        using var peer=OpenProcess(0x00101040,false,witness.TargetPid);if(peer.IsInvalid)throw new Win32Exception();
        if(RequireTarget(peer)!=(witness.TargetPid,witness.TargetCreated))throw new IOException("CUSTODY_PEER_GENERATION");
        if(witness.Ancestors is null||witness.Ancestors.Length is <1 or >128)throw new IOException("CUSTODY_PEER_HANDLES");
        var copied=new List<ulong>();
        try
        {
            foreach(var value in witness.Ancestors.Append(witness.Gate))
            {if(!DuplicateHandle(peer.DangerousGetHandle(),Pointer(value),GetCurrentProcess(),out var local,0,false,2))throw new Win32Exception();copied.Add(unchecked((ulong)local.ToInt64()));}
            if(RequireTarget(peer)!=(witness.TargetPid,witness.TargetCreated))throw new IOException("CUSTODY_PEER_CHANGED");
            var self=CurrentIdentity();var transfer=new NativeDataLeaseTransfer(self.Pid,self.Created,copied.Take(copied.Count-1).ToArray(),copied[^1]);
            // Normal adoption consumes these local duplicates, including its
            // failure cleanup. Never close their numeric values a second time.
            copied.Clear();return WindowsFileBoundary.DataLease.AdoptTransferred(Path.Combine(InstalledUpdaterLayout.StateRoot,".authority","update-serial"),transfer);
        }
        finally{foreach(var handle in copied)CloseHandle(Pointer(handle));}
    }
    internal static ulong[] DuplicateTo(SafeProcessHandle process,IReadOnlyList<SafeHandle> originals)
    {
        var expected=RequireTarget(process);var result=new List<ulong>();
        try
        {
            foreach(var original in originals)
            {
                var added=false;
                try{original.DangerousAddRef(ref added);if(!DuplicateHandle(GetCurrentProcess(),original.DangerousGetHandle(),process.DangerousGetHandle(),out var copied,0,false,2))throw new Win32Exception();result.Add(unchecked((ulong)copied.ToInt64()));}
                finally{if(added)original.DangerousRelease();}
            }
            if(RequireTarget(process)!=expected)throw new IOException("CUSTODY_TARGET_CHANGED");return result.ToArray();
        }
        catch
        {
            foreach(var remote in result)
                if(DuplicateHandle(process.DangerousGetHandle(),Pointer(remote),GetCurrentProcess(),out var local,0,false,3))CloseHandle(local);
            throw;
        }
    }
    internal static IntPtr Pointer(ulong value)=>value is >0 and <=long.MaxValue?new((long)value):throw new IOException("CUSTODY_HANDLE_BOUND");
    [DllImport("kernel32.dll")]private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,int pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool DuplicateHandle(IntPtr source,IntPtr original,IntPtr target,out IntPtr copy,uint access,bool inherit,uint options);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder image,ref int length);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
}
