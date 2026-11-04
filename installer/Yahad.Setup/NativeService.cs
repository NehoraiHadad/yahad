using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

// All that differs between the services. The Runtime holds one privilege and
// restarts quickly; the Updater is LocalSystem, starts after everything else and
// has no reason to hurry back; the home-network proxy holds one privilege, starts
// late and is never restarted by the SCM. The installer's own account and the
// Runtime may read the Runtime's and the Updater's configuration.
internal sealed record ServiceSpec(string Name,string Account,Func<string,string> Command,uint[] FailureDelays,bool Restricted,bool Delayed,string Display,string? QueryGrant)
{
    internal static readonly ServiceSpec Runtime=new(ServiceDefinition.Name,ServiceDefinition.Account,InstallLocations.ServiceCommand,[5000,30000,120000],true,false,ServiceDefinition.Name,InstallLocations.ServiceSid);
    internal static readonly ServiceSpec Updater=new(ServiceDefinition.UpdaterName,ServiceDefinition.UpdaterAccount,
        version=>{InstallLocations.RequireVersion(version);return InstalledLayout.UpdaterCommand(version);},[60000,300000,600000],false,true,ServiceDefinition.UpdaterName,InstallLocations.ServiceSid);
    internal static readonly ServiceSpec Lan=new(LanProxyPaths.ServiceName,"NT SERVICE\\"+LanProxyPaths.ServiceName,_=>"\""+LanProxyPaths.ServiceImage+"\" --lan-service",[],true,true,"Yahad home network HTTPS",null);
}
// Installer-only SCM writer. The names, accounts and images come from compiled
// constants and the verified package version; no CLI, file or runtime peer can
// choose them.
internal sealed class NativeService:IDisposable
{
    internal const uint DemandStart=3,AutoStart=2,Disabled=4;
    private readonly ServiceSpec spec;private string command;private readonly Handle manager,service;private bool stopRequested;
    private NativeService(ServiceSpec spec,string command,Handle manager,Handle service){this.spec=spec;this.command=command;this.manager=manager;this.service=service;}
    internal static bool Exists(ServiceSpec? spec=null)
    {
        using var manager=new Handle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
        using var service=new Handle(OpenServiceW(manager,(spec??ServiceSpec.Runtime).Name,4));if(!service.IsInvalid)return true;
        var error=Marshal.GetLastPInvokeError();if(error==1060)return false;if(error==1072)return true;throw new Win32Exception(error);
    }
    internal static NativeService Create(string version,string owner,ServiceSpec? kind=null)
    {
        var spec=kind??ServiceSpec.Runtime;var command=spec.Command(version);
        var manager=new Handle(OpenSCManagerW(null,null,3));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        var service=new Handle(CreateServiceW(manager,spec.Name,spec.Display,0xf01ff,16,DemandStart,1,command,null,IntPtr.Zero,null,spec.Account,null));
        if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        var value=new NativeService(spec,command,manager,service);
        try{value.Provision(owner);return value;}
        catch{value.Dispose();throw;} // The durable preparing record owns a created service; no guessed rollback.
    }
    // Only a same-package preparing record enters this path. An interruption
    // between CreateService and its configuration resumes only before the
    // service has EVER started; the strict Open stays unchanged.
    internal static NativeService OpenForProvisioning(string version,string owner,ServiceSpec? kind=null)
    {
        var value=OpenHandle(version,kind);
        try
        {
            var status=value.Status();RequireProvisioningState(value.RequireConfigurationIdentity(),status.State,status.Pid,status.Win32,status.Specific);
            value.Provision(owner);return value;
        }
        catch{value.Dispose();throw;}
    }
    internal static NativeService Open(string version,ServiceSpec? kind=null)
    {
        var value=OpenHandle(version,kind);
        try{value.RequireConfigurationIdentity();return value;}catch{value.Dispose();throw;}
    }
    // During an upgrade the image is one of exactly two recorded versions.
    internal static NativeService OpenAt(string version,string other,ServiceSpec? kind=null)
    {
        try{return Open(version,kind);}catch(IOException error)when(error.Message=="SERVICE_NOT_OWNED"){return Open(other,kind);}
    }
    // Repair: an image pointing anywhere else is set back to the installed
    // release. Type and account are still required to be the service's own.
    internal static NativeService OpenRepointed(string version,ref bool repointed,ServiceSpec? kind=null)
    {
        var value=OpenHandle(version,kind);
        try
        {
            try{value.RequireConfigurationIdentity();}
            catch(IOException error)when(error.Message=="SERVICE_NOT_OWNED"){value.SetImage();value.RequireConfigurationIdentity();repointed=true;}
            return value;
        }
        catch{value.Dispose();throw;}
    }
    internal static async Task<NativeService?> OpenForRemovalAsync(string version,CancellationToken cancellation,ServiceSpec? kind=null)
    {
        var until=Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();if(!Exists(kind))return null;
            try{return Open(version,kind);}
            catch(Win32Exception error)when(error.NativeErrorCode==1072)
            {if(until.Elapsed>=TimeSpan.FromSeconds(15))throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING",error);await Task.Delay(100,cancellation);}
            catch(Win32Exception error)when(error.NativeErrorCode==1060){return null;}
        }
    }
    private static NativeService OpenHandle(string version,ServiceSpec? kind)
    {
        var spec=kind??ServiceSpec.Runtime;var command=spec.Command(version);var manager=new Handle(OpenSCManagerW(null,null,1));
        if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}var handle=new Handle(OpenServiceW(manager,spec.Name,0xf01ff));
        if(handle.IsInvalid){handle.Dispose();manager.Dispose();throw new Win32Exception();}return new(spec,command,manager,handle);
    }
    private void Provision(string owner)
    {
        Change(5,1u);
        if(spec.Restricted)
        {
            var names=Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
            try{Change(6,new Privileges{Names=names});}finally{Marshal.FreeHGlobal(names);}
        }
        if(!SetServiceObjectSecurity(service,4,NativeInstallFiles.Bytes(ServiceDescriptor(owner,spec.QueryGrant))))throw new Win32Exception();
    }
    // A proxy created by an interrupted Setup may never have got its identity.
    internal void ProvisionIfNeverStarted(string owner){if(Status().Win32==1077)Provision(owner);}
    private uint RequireConfigurationIdentity()
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var count);if(count<(uint)Marshal.SizeOf<Configuration>()||count>16384)throw new IOException("INSTALL_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)count);
        try
        {
            if(!QueryServiceConfigW(service,memory,count,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<Configuration>(memory);
            if(config.Type!=16||Marshal.PtrToStringUni(config.Image)!=command||!string.Equals(Marshal.PtrToStringUni(config.Account),spec.Account,StringComparison.OrdinalIgnoreCase))throw new IOException("SERVICE_NOT_OWNED");
            return config.Start;
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static void RequireProvisioningState(uint start,uint state,uint pid,uint win32,uint specific)
    {if(start!=DemandStart||state!=1||pid!=0||win32!=1077||specific!=0)throw new IOException("INSTALL_SERVICE_PROVISIONING_UNCONFIRMED");}
    // 0x5 = query-config + query-status for the administrator who installed
    // and for the Runtime. No start/stop, change-config, delete or
    // write-DACL/owner outside the machine administrators.
    internal static string ServiceDescriptor(string originalOwner,string? grantSid)
        =>$"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00000005;;;{originalOwner})"+(grantSid is null?"":$"(A;;0x00000005;;;{grantSid})");
    private T ReadConfig2<T>(uint kind,Func<IntPtr,uint,T> read)
    {
        QueryServiceConfig2W(service,kind,IntPtr.Zero,0,out var bytes);if(bytes is <4 or >65536)throw new IOException("INSTALL_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)bytes);
        try{if(!QueryServiceConfig2W(service,kind,memory,bytes,out _))throw new Win32Exception();return read(memory,bytes);}finally{Marshal.FreeHGlobal(memory);}
    }
    private void Change<T>(uint kind,T value)where T:struct
    {var memory=Marshal.AllocHGlobal(Marshal.SizeOf<T>());try{Marshal.StructureToPtr(value,memory,false);if(!ChangeServiceConfig2W(service,kind,memory))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}}
    // Automatic start with the service's SCM restarts, counted over one day.
    // A reported (non-crash) failure also restarts the service.
    internal void EnableStartup()
    {
        if(!ChangeServiceConfigW(service,0xffffffff,AutoStart,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        if(spec.Delayed)Change(3,1u);
        SetFailureActions(true);Change(4,1u);
    }
    internal void DisableStartup()
    {if(!ChangeServiceConfigW(service,0xffffffff,DemandStart,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();}
    // While an upgrade moves the service nothing may restart it behind Setup's back.
    internal void ClearFailureActions(){SetFailureActions(false);Change(4,0u);}
    internal void SwitchImage(string version)
    {
        ConfirmStopped();command=spec.Command(version);SetImage();
    }
    private void SetImage()
    {if(!ChangeServiceConfigW(service,0xffffffff,0xffffffff,0xffffffff,command,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();}
    internal bool IsStopped()=>Status().State==1;
    internal bool IsRunning()=>Status().State==4;
    internal bool StartupConfigured()
    {
        try
        {
            if(RequireConfigurationIdentity()!=AutoStart)return false;
            VerifyFailureActions();
            if(spec.Delayed&&ReadConfig2(3,(p,_)=>Marshal.ReadInt32(p))!=1)return false;
            return ReadConfig2(4,(p,_)=>Marshal.ReadInt32(p))==1;
        }
        catch(IOException){return false;}
    }
    internal void FenceForRemoval()
    {
        if(spec.Delayed)Change(3,0u);
        if(!ChangeServiceConfigW(service,0xffffffff,Disabled,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        SetFailureActions(false);Change(4,0u);
    }
    internal const uint FailureReset=86400;
    internal static FailureAction[] FailurePlan(bool configured,ServiceSpec? kind=null)
        =>!configured?[]:(kind??ServiceSpec.Runtime).FailureDelays.Select(delay=>new FailureAction{Kind=1,Delay=delay}).ToArray();
    internal static void RequireFailurePlan(bool configured,uint reset,IReadOnlyList<FailureAction> actual,ServiceSpec? kind=null)
    {
        var expected=FailurePlan(configured,kind);
        if(reset!=(configured?FailureReset:0u)||actual.Count!=expected.Length||actual.Where((value,index)=>value.Kind!=expected[index].Kind||value.Delay!=expected[index].Delay).Any())throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
    }
    private void SetFailureActions(bool configured)
    {
        // NULL means "leave unchanged" in SERVICE_FAILURE_ACTIONS. An empty
        // command and a non-NULL zero-count array REMOVE the old actions.
        var plan=FailurePlan(configured,spec);var stride=Marshal.SizeOf<FailureAction>();
        var text=Marshal.StringToHGlobalUni("");var action=Marshal.AllocHGlobal(stride*Math.Max(1,plan.Length));
        try
        {
            if(plan.Length==0)Marshal.StructureToPtr(new FailureAction(),action,false);
            for(var i=0;i<plan.Length;i++)Marshal.StructureToPtr(plan[i],action+i*stride,false);
            Change(2,new FailureActions{Reset=configured?FailureReset:0u,Command=text,Count=(uint)plan.Length,Actions=action});
        }
        finally{Marshal.FreeHGlobal(text);Marshal.FreeHGlobal(action);}
    }
    private void VerifyFailureActions()
    {
        var plan=FailurePlan(true,spec);var stride=Marshal.SizeOf<FailureAction>();
        _=ReadConfig2(2,(p,n)=>
        {
            if(n<(uint)Marshal.SizeOf<FailureActions>())throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
            var observed=Marshal.PtrToStructure<FailureActions>(p);if(observed.Count!=plan.Length)throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
            var actual=new List<FailureAction>();
            if(plan.Length>0)
            {
                var offset=observed.Actions.ToInt64()-p.ToInt64();if(offset<0||offset>n-stride*plan.Length)throw new IOException("INSTALL_SERVICE_FAILURE_ACTIONS");
                for(var i=0;i<plan.Length;i++)actual.Add(Marshal.PtrToStructure<FailureAction>(observed.Actions+i*stride));
            }
            RequireFailurePlan(true,observed.Reset,actual,spec);return 0;
        });
    }
    internal async Task StartAsync(CancellationToken cancellation)
    {
        if(!StartServiceW(service,0,IntPtr.Zero)&&Marshal.GetLastPInvokeError()!=1056)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(6));
        while(true){deadline.Token.ThrowIfCancellationRequested();var state=Status();if(state.State==4&&state.Pid!=0)return;if(state.State==1)throw new IOException("SERVICE_START_FAILED");await Task.Delay(250,deadline.Token);}
    }
    internal async Task StopAsync(CancellationToken cancellation)
    {
        using var process=await StopSettledAsync(cancellation);await WaitForExitAsync(process,cancellation);
        ConfirmStopped();
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
    private void RequestStop()
    {
        var state=Status();if(!SendsStop(state.State,state.Accepted,stopRequested))return;
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
    internal void ConfirmStopped(){var observed=Status();RequireStopped(observed.State,observed.Pid);}
    internal (uint State,uint Pid,uint Win32,uint Specific) Observed(){var status=Status();return(status.State,status.Pid,status.Win32,status.Specific);}
    internal void Delete(){if(!DeleteService(service))throw new Win32Exception();}
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
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(Handle service,uint level,out StatusProcess status,uint bytes,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool StartServiceW(Handle service,uint argc,IntPtr argv);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(Handle service,uint control,out StatusBasic status);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DeleteService(Handle service);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
