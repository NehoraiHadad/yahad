using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Setup;

// Installer-only SCM writer for the one Runtime service. The name, account and
// image come from compiled constants and the verified package version; no CLI,
// file or runtime peer can choose them.
internal sealed class NativeRuntimeService:IDisposable
{
    internal const uint DemandStart=3,AutoStart=2,Disabled=4;
    private string command;private readonly Handle manager,service;private bool stopRequested;
    private NativeRuntimeService(string command,Handle manager,Handle service){this.command=command;this.manager=manager;this.service=service;}
    internal static bool Exists()
    {
        using var manager=new Handle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
        using var service=new Handle(OpenServiceW(manager,InstallLocations.ServiceName,4));if(!service.IsInvalid)return true;
        var error=Marshal.GetLastPInvokeError();if(error==1060)return false;if(error==1072)return true;throw new Win32Exception(error);
    }
    internal static NativeRuntimeService Create(string version,string owner)
    {
        NativeInstallFiles.RequireAdministrator();var command=InstallLocations.ServiceCommand(version);
        var manager=new Handle(OpenSCManagerW(null,null,3));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        var service=new Handle(CreateServiceW(manager,InstallLocations.ServiceName,InstallLocations.ServiceName,0xf01ff,16,DemandStart,1,command,null,IntPtr.Zero,null,InstallLocations.ServiceAccount,null));
        if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        var value=new NativeRuntimeService(command,manager,service);
        try
        {
            // This handle is the result of a successful CREATE, never adoption
            // of an existing name. Give only the administrative group ownership
            // before the narrow service ACL is published to other principals.
            var ownerOnly=new RawSecurityDescriptor("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)");var descriptor=new byte[ownerOnly.BinaryLength];ownerOnly.GetBinaryForm(descriptor,0);
            if(!SetServiceObjectSecurity(service,5,descriptor))throw new Win32Exception();
            value.Provision(owner);var status=value.Status();RequireProvisioningState(value.RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);return value;
        }
        catch{value.Dispose();throw;} // The durable preparing record owns a created service; no guessed rollback.
    }
    // Only a same-package preparing record enters this path. An interruption
    // between CreateService and its configuration resumes only before the
    // service has EVER started; the strict Open stays unchanged.
    internal static NativeRuntimeService OpenForProvisioning(string version,string owner)
    {
        var value=OpenHandle(version);
        try
        {
            var status=value.Status();RequireProvisioningState(value.RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);
            var acl=value.Security();try{RequireProtection(acl,owner);}catch(IOException){RequireAdministrativeWriters(acl);}
            value.Provision(owner);value.SetFailureActions(false);value.Change(4,0u);value.RequireOwned();return value;
        }
        catch{value.Dispose();throw;}
    }
    internal static NativeRuntimeService Open(string version,string owner)
    {
        var value=OpenHandle(version);
        try{value.RequireOwned();RequireProtection(value.Security(),owner);return value;}catch{value.Dispose();throw;}
    }
    // During an upgrade the image is one of exactly two recorded versions.
    internal static NativeRuntimeService OpenAt(string version,string other,string owner)
    {
        try{return Open(version,owner);}catch(IOException error)when(error.Message=="SERVICE_NOT_OWNED"){return Open(other,owner);}
    }
    // Repair: an image pointing anywhere else is set back to the installed
    // release. Type and account are still required to be the Runtime's own.
    internal static NativeRuntimeService OpenRepointed(string version,string owner,ref bool repointed)
    {
        var value=OpenHandle(version);
        try
        {
            RequireProtection(value.Security(),owner);
            try{value.RequireOwned();}
            catch(IOException error)when(error.Message=="SERVICE_NOT_OWNED"){value.SetImage();value.RequireOwned();repointed=true;}
            return value;
        }
        catch{value.Dispose();throw;}
    }
    internal static async Task<NativeRuntimeService?> OpenForRemovalAsync(string version,string owner,CancellationToken cancellation)
    {
        var until=Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();if(!Exists())return null;
            try{return Open(version,owner);}
            catch(Win32Exception error)when(error.NativeErrorCode==1072)
            {if(until.Elapsed>=TimeSpan.FromSeconds(15))throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING",error);await Task.Delay(100,cancellation);}
            catch(Win32Exception error)when(error.NativeErrorCode==1060){return null;}
        }
    }
    private static NativeRuntimeService OpenHandle(string version)
    {
        NativeInstallFiles.RequireAdministrator();var command=InstallLocations.ServiceCommand(version);var manager=new Handle(OpenSCManagerW(null,null,1));
        if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}var handle=new Handle(OpenServiceW(manager,InstallLocations.ServiceName,0xf01ff));
        if(handle.IsInvalid){handle.Dispose();manager.Dispose();throw new Win32Exception();}return new(command,manager,handle);
    }
    private void Provision(string owner)
    {
        Change(5,1u);var names=Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
        try{Change(6,new Privileges{Names=names});}finally{Marshal.FreeHGlobal(names);}
        RequireOwned();var acl=new RawSecurityDescriptor(ServiceDescriptor(owner,Sid()));var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);
        if(!SetServiceObjectSecurity(service,4,bytes))throw new Win32Exception();
        RequireProtection(Security(),owner);
    }
    // The account the SCM resolves for the service must be the SID the
    // installer granted its files to before the service existed.
    internal static string Sid()
    {
        var resolved=((SecurityIdentifier)new NTAccount(InstallLocations.ServiceAccount).Translate(typeof(SecurityIdentifier))).Value;
        if(resolved!=InstallLocations.ServiceSid)throw new IOException("INSTALL_SERVICE_IDENTITY");return resolved;
    }
    internal void RequireOwned(){RequireConfigurationIdentity();RequireRestrictedIdentity();}
    private uint RequireConfigurationIdentity()
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var count);if(count<(uint)Marshal.SizeOf<Configuration>()||count>16384)throw new IOException("INSTALL_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)count);
        try
        {
            if(!QueryServiceConfigW(service,memory,count,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<Configuration>(memory);
            if(config.Type!=16||Marshal.PtrToStringUni(config.Image)!=command||!string.Equals(Marshal.PtrToStringUni(config.Account),InstallLocations.ServiceAccount,StringComparison.OrdinalIgnoreCase))throw new IOException("SERVICE_NOT_OWNED");
            return config.Start;
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    private void RequireRestrictedIdentity()
    {
        if(ReadConfig2(5,(p,_)=>Marshal.ReadInt32(p))!=1)throw new IOException("INSTALL_SERVICE_SID_TYPE");
        _=ReadConfig2(6,(p,n)=>
        {
            if(n<IntPtr.Size)throw new IOException("INSTALL_SERVICE_PRIVILEGES");
            var text=Marshal.ReadIntPtr(p);var privileges=ReadString(text,p,n);
            if(privileges!="SeChangeNotifyPrivilege")throw new IOException("INSTALL_SERVICE_PRIVILEGES");
            var terminator=text+checked((privileges.Length+1)*2);
            if(terminator.ToInt64()<p.ToInt64()||terminator.ToInt64()+2>p.ToInt64()+n||Marshal.ReadInt16(terminator)!=0)throw new IOException("INSTALL_SERVICE_PRIVILEGES");return 0;
        });
    }
    internal static void RequireProvisioningState(uint start,uint state,uint pid,uint win32,uint specific)
    {if(start!=DemandStart||state!=1||pid!=0||win32!=1077||specific!=0)throw new IOException("INSTALL_SERVICE_PROVISIONING_UNCONFIRMED");}
    private RawSecurityDescriptor Security()
    {
        QueryServiceObjectSecurity(service,5,IntPtr.Zero,0,out var size);if(size is <20 or >65536)throw new IOException("INSTALL_SERVICE_DACL_BOUND");var memory=Marshal.AllocHGlobal((int)size);
        try{if(!QueryServiceObjectSecurity(service,5,memory,size,out _))throw new Win32Exception();var bytes=new byte[size];Marshal.Copy(memory,bytes,0,bytes.Length);return new(bytes,0);}
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static void RequireAdministrativeWriters(RawSecurityDescriptor acl)
    {
        RequireAdministrativeOwner(acl);if(acl.DiscretionaryAcl is null)throw new IOException("INSTALL_SERVICE_DACL");
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("INSTALL_SERVICE_DACL");
            if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("INSTALL_SERVICE_DACL");
            if(ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544")continue;
            // Generic/service read only: query config/status/dependents,
            // interrogate and READ_CONTROL. No start/stop, config or ownership.
            if((ace.AccessMask&~unchecked((int)0x8002008d))!=0)throw new IOException("INSTALL_SERVICE_DACL");
        }
    }
    internal static void RequireAdministrativeOwner(RawSecurityDescriptor acl)
    {if(acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("INSTALL_SERVICE_OWNER");}
    // 0x5 = query-config + query-status for the administrator who installed
    // and for the service itself. No start/stop, change-config, delete or
    // write-DACL/owner outside the machine administrators.
    internal static string ServiceDescriptor(string originalOwner,string runtimeSid)
    {
        if(!originalOwner.StartsWith("S-1-5-21-",StringComparison.Ordinal)||!new SecurityIdentifier(originalOwner).IsAccountSid()||!runtimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal))throw new IOException("INSTALL_SERVICE_GRANT");
        return $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00000005;;;{originalOwner})(A;;0x00000005;;;{runtimeSid})";
    }
    internal static void RequireProtection(RawSecurityDescriptor acl,string originalOwner)
    {
        RequireAdministrativeOwner(acl);if(acl.DiscretionaryAcl is null||acl.DiscretionaryAcl.Count!=4)throw new IOException("INSTALL_SERVICE_DACL");
        var wanted=new Dictionary<string,int>(StringComparer.Ordinal){{"S-1-5-18",0xf01ff},{"S-1-5-32-544",0xf01ff},{originalOwner,5},{InstallLocations.ServiceSid,5}};
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||ace.AceFlags!=AceFlags.None||!wanted.Remove(ace.SecurityIdentifier.Value,out var expected))throw new IOException("INSTALL_SERVICE_DACL");
            var observed=ace.AccessMask==0x10000000?0xf01ff:ace.AccessMask;if(observed!=expected)throw new IOException("INSTALL_SERVICE_DACL");
        }
    }
    private T ReadConfig2<T>(uint kind,Func<IntPtr,uint,T> read)
    {
        QueryServiceConfig2W(service,kind,IntPtr.Zero,0,out var bytes);if(bytes is <4 or >65536)throw new IOException("INSTALL_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try{if(!QueryServiceConfig2W(service,kind,memory,bytes,out _))throw new Win32Exception();return read(memory,bytes);}finally{Marshal.FreeHGlobal(memory);}
    }
    private static string ReadString(IntPtr text,IntPtr memory,uint bytes)
    {
        if(text==IntPtr.Zero)return "";var start=text.ToInt64();var lower=memory.ToInt64();var end=lower+bytes;
        if(start<lower||start>=end||(start-lower)%2!=0)throw new IOException("INSTALL_SERVICE_CONFIG_POINTER");var result=new System.Text.StringBuilder();
        for(var offset=0;start+offset+2<=end&&offset<65536;offset+=2){var c=(char)Marshal.ReadInt16(text,offset);if(c=='\0')return result.ToString();result.Append(c);}throw new IOException("INSTALL_SERVICE_CONFIG_STRING");
    }
    private void Change<T>(uint kind,T value)where T:struct
    {var memory=Marshal.AllocHGlobal(Marshal.SizeOf<T>());try{Marshal.StructureToPtr(value,memory,false);if(!ChangeServiceConfig2W(service,kind,memory))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}}
    // Automatic start with SCM restarts after 5 s, 30 s and 2 min, counted
    // over one day. A reported (non-crash) failure also restarts the service.
    internal void EnableStartup()
    {
        RequireOwned();if(!ChangeServiceConfigW(service,0xffffffff,AutoStart,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        SetFailureActions(true);Change(4,1u);
        if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=1)throw new IOException("INSTALL_SERVICE_FAILURE_FLAGS");
        RequireStartType(AutoStart);
    }
    // While an upgrade moves the service nothing may restart it behind Setup's back.
    internal void ClearFailureActions(){RequireOwned();SetFailureActions(false);Change(4,0u);}
    internal void SwitchImage(string version)
    {
        RequireStoppedForSwitch();command=InstallLocations.ServiceCommand(version);SetImage();RequireOwned();
    }
    private void RequireStoppedForSwitch(){var observed=Status();RequireStopped(observed.State,observed.Pid);}
    private void SetImage()
    {if(!ChangeServiceConfigW(service,0xffffffff,0xffffffff,0xffffffff,command,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();}
    internal bool IsStopped()=>Status().State==1;
    internal bool IsRunning()=>Status().State==4;
    internal bool StartupConfigured()
    {
        try
        {
            RequireOwned();RequireStartType(AutoStart);VerifyFailureActions(true);
            return ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))==1;
        }
        catch(IOException){return false;}
    }
    internal void FenceForRemoval()
    {
        RequireOwned();if(!ChangeServiceConfigW(service,0xffffffff,Disabled,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        SetFailureActions(false);Change(4,0u);
        RequireStartType(Disabled);if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=0)throw new IOException("INSTALL_SERVICE_FAILURE_FLAGS");
    }
    private void RequireStartType(uint expected)
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var bytes);if(bytes<(uint)Marshal.SizeOf<Configuration>()||bytes>16384)throw new IOException("INSTALL_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try{if(!QueryServiceConfigW(service,memory,bytes,out _))throw new Win32Exception();if(Marshal.PtrToStructure<Configuration>(memory).Start!=expected)throw new IOException("INSTALL_SERVICE_START_TYPE");}finally{Marshal.FreeHGlobal(memory);}
    }
    internal const uint FailureReset=86400;
    internal static FailureAction[] FailurePlan(bool configured)
        =>!configured?[]:[new(){Kind=1,Delay=5000},new(){Kind=1,Delay=30000},new(){Kind=1,Delay=120000}];
    internal static void RequireFailurePlan(bool configured,uint reset,IReadOnlyList<FailureAction> actual)
    {
        var expected=FailurePlan(configured);
        if(reset!=(configured?FailureReset:0u)||actual.Count!=expected.Length||actual.Where((value,index)=>value.Kind!=expected[index].Kind||value.Delay!=expected[index].Delay).Any())throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
    }
    private void SetFailureActions(bool configured)
    {
        // NULL means "leave unchanged" in SERVICE_FAILURE_ACTIONS. An empty
        // command and a non-NULL zero-count array REMOVE the old actions.
        var plan=FailurePlan(configured);var stride=Marshal.SizeOf<FailureAction>();
        var text=Marshal.StringToHGlobalUni("");var action=Marshal.AllocHGlobal(stride*Math.Max(1,plan.Length));
        try
        {
            if(plan.Length==0)Marshal.StructureToPtr(new FailureAction(),action,false);
            for(var i=0;i<plan.Length;i++)Marshal.StructureToPtr(plan[i],action+i*stride,false);
            Change(2,new FailureActions{Reset=configured?FailureReset:0u,Command=text,Count=(uint)plan.Length,Actions=action});
            VerifyFailureActions(configured);
        }
        finally{Marshal.FreeHGlobal(text);Marshal.FreeHGlobal(action);}
    }
    private void VerifyFailureActions(bool configured)
    {
        var plan=FailurePlan(configured);var stride=Marshal.SizeOf<FailureAction>();
        _=ReadConfig2(2,(p,n)=>
        {
            if(n<(uint)Marshal.SizeOf<FailureActions>())throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
            var observed=Marshal.PtrToStructure<FailureActions>(p);if(ReadString(observed.Command,p,n)!=""||observed.Count!=plan.Length)throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
            var actual=new List<FailureAction>();
            if(plan.Length>0)
            {
                var offset=observed.Actions.ToInt64()-p.ToInt64();if(offset<0||offset>n-stride*plan.Length)throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
                for(var i=0;i<plan.Length;i++)actual.Add(Marshal.PtrToStructure<FailureAction>(observed.Actions+i*stride));
            }
            RequireFailurePlan(configured,observed.Reset,actual);return 0;
        });
    }
    internal async Task StartAsync(CancellationToken cancellation)
    {
        RequireOwned();if(!StartServiceW(service,0,IntPtr.Zero)&&Marshal.GetLastPInvokeError()!=1056)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(6));
        while(true){deadline.Token.ThrowIfCancellationRequested();var state=Status();if(state.State==4&&state.Pid!=0)return;if(state.State==1)throw new IOException("SERVICE_START_FAILED");await Task.Delay(250,deadline.Token);}
    }
    internal async Task StopAsync(CancellationToken cancellation)
    {
        RequireOwned();using var process=await StopSettledAsync(cancellation);await WaitForExitAsync(process,cancellation);
        var stopped=Status();RequireStopped(stopped.State,stopped.Pid);
    }
    // The SCM refuses STOP (1052/1061) unless the current state accepts it, as
    // with a START_PENDING service that does not. STOP is sent only when
    // accepted; otherwise the service is waited on until it settles, then sent.
    internal static bool SendsStop(uint state,uint accepted,bool requested)=>!requested&&state is not(1 or 3)&&(accepted&1)!=0;
    // SCM pending protocol: a state is live while its checkpoint advances within
    // its own wait hint. A settled state gets the fixed stop bound.
    internal static bool Stalled(uint waitHint,TimeSpan unchanged)=>unchanged>TimeSpan.FromMilliseconds(Math.Max(waitHint,60000));
    // A process opened while RUNNING is the service's own while the SCM still
    // reports its PID; a requested STOP moving it to STOP_PENDING is expected.
    internal static bool SameGeneration(uint pid,uint state,uint observedPid)=>state is 3 or 4&&observedPid==pid;
    internal void RequestStop()
    {
        RequireOwned();var state=Status();if(!SendsStop(state.State,state.Accepted,stopRequested))return;
        if(ControlService(service,1,out _)){stopRequested=true;return;}
        // The state moved between query and control. The settle loop re-reads it.
        var error=Marshal.GetLastPInvokeError();if(error is not(1052 or 1061 or 1062))throw new Win32Exception(error);
    }
    private async Task<Process?> StopSettledAsync(CancellationToken cancellation)
    {
        Process? process=null;
        try
        {
            var observed=Status();var unchanged=Stopwatch.StartNew();
            while(true)
            {
                var state=Status();
                if(state.State==1){stopRequested=false;return process;}
                // Windows documents a valid process ID only for RUNNING.
                if(state.State==4&&state.Pid!=0)
                {
                    if(process is null)
                    {
                        process=Process.GetProcessById((int)state.Pid);_=process.SafeHandle;
                        var again=Status();if(again.State==1){process.Dispose();process=null;continue;}
                        if(!SameGeneration(state.Pid,again.State,again.Pid))throw new IOException("INSTALL_SERVICE_GENERATION_CHANGED");
                    }
                    else if(process.Id!=state.Pid)throw new IOException("INSTALL_SERVICE_GENERATION_CHANGED");
                }
                RequestStop();
                if(state.State!=observed.State||state.Checkpoint!=observed.Checkpoint){observed=state;unchanged.Restart();}
                else if(Stalled(state.WaitHint,unchanged.Elapsed))throw new IOException("INSTALL_SERVICE_STOP_STALLED");
                await Task.Delay(100,cancellation);
            }
        }
        catch{process?.Dispose();throw;}
    }
    private static async Task WaitForExitAsync(Process? process,CancellationToken cancellation)
    {
        if(process is null)return;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(60));
        await process.WaitForExitAsync(deadline.Token);
    }
    // The SCM's exit code says how the last run ended (a failed start leaves 1066),
    // not whether it is over, and it stays until the next start, so repair must not
    // read it. Stopped with no process means the host and its kill-on-close job are
    // gone; a survivor would still hold the data lease that any next user must take.
    internal static void RequireStopped(uint state,uint pid)
    {if(state!=1||pid!=0)throw new IOException("UNINSTALL_QUIESCENCE_UNCONFIRMED");}
    internal void RequireFencedStoppedForRemoval()
    {RequireRemovalFence();var observed=Status();RequireStopped(observed.State,observed.Pid);}
    private void RequireRemovalFence()
    {
        RequireOwned();RequireStartType(Disabled);
        if(ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))!=0)throw new IOException("INSTALL_SERVICE_FAILURE_FLAGS");
        VerifyFailureActions(false);
    }
    // Observation only: the uninstaller still holds the data lease before any
    // destructive step, so STOPPED alone is never taken as writer quiescence.
    internal async Task StopForRemovalAsync(CancellationToken cancellation)
    {
        RequireRemovalFence();using var process=await StopSettledAsync(cancellation);await WaitForExitAsync(process,cancellation);
        RequireFencedStoppedForRemoval();
    }
    internal void Delete()
    {RequireOwned();var state=Status();if(state.State!=1||state.Pid!=0)throw new IOException("UNINSTALL_QUIESCENCE_UNCONFIRMED");if(!DeleteService(service))throw new Win32Exception();}
    private StatusProcess Status(){if(!QueryServiceStatusEx(service,0,out var value,(uint)Marshal.SizeOf<StatusProcess>(),out _))throw new Win32Exception();return value;}
    public void Dispose(){service.Dispose();manager.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct Privileges{public IntPtr Names;}
    [StructLayout(LayoutKind.Sequential)]private struct Configuration{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusProcess{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusBasic{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint;}
    [StructLayout(LayoutKind.Sequential)]private struct FailureActions{public uint Reset;public IntPtr RebootMessage,Command;public uint Count;public IntPtr Actions;}
    [StructLayout(LayoutKind.Sequential)]internal struct FailureAction{public uint Kind,Delay;}
    private sealed class Handle:SafeHandleZeroOrMinusOneIsInvalid{internal Handle(IntPtr value):base(true){SetHandle(value);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(Handle manager,string name,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateServiceW(Handle manager,string name,string display,uint rights,uint type,uint start,uint error,string image,string? group,IntPtr tag,string? dependencies,string account,string? password);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(Handle service,IntPtr memory,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfig2W(Handle service,uint kind,IntPtr memory,uint bytes,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfigW(Handle service,uint type,uint start,uint error,string? image,string? group,IntPtr tag,string? dependencies,string? account,string? password,string? display);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfig2W(Handle service,uint kind,IntPtr value);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool SetServiceObjectSecurity(Handle service,uint information,byte[] descriptor);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceObjectSecurity(Handle service,uint information,IntPtr descriptor,uint bytes,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(Handle service,uint level,out StatusProcess status,uint bytes,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool StartServiceW(Handle service,uint argc,IntPtr argv);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(Handle service,uint control,out StatusBasic status);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DeleteService(Handle service);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
