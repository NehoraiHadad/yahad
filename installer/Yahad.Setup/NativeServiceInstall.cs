using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Setup;

internal sealed class NativeServiceInstall:IDisposable
{
    private readonly ServiceHandle manager,service;
    private NativeServiceInstall(ServiceHandle manager,ServiceHandle service){this.manager=manager;this.service=service;}
    internal static bool Exists()
    {
        using var manager=new ServiceHandle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
        using var service=new ServiceHandle(OpenServiceW(manager,InstallLocations.ServiceName,4));if(!service.IsInvalid)return true;
        if(Marshal.GetLastPInvokeError()==1060)return false;throw new Win32Exception();
    }
    internal static NativeServiceInstall Create(string ownerSid)
    {
        var manager=new ServiceHandle(OpenSCManagerW(null,null,3));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        var image="\""+InstallLocations.ServiceImage+"\" --service";
        var service=new ServiceHandle(CreateServiceW(manager,InstallLocations.ServiceName,"יחד — שרת משפחתי",0xf01ff,16,3,1,image,null,IntPtr.Zero,null,"NT SERVICE\\"+InstallLocations.ServiceName,null));
        if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        var result=new NativeServiceInstall(manager,service);
        try
        {
            var sid=1u;Change(5,ref sid);
            var names=Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
            try{var info=new Privileges{Names=names};Change(6,ref info);}finally{Marshal.FreeHGlobal(names);}
            var acl=new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00000005;;;{ownerSid})");var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);
            if(!SetServiceObjectSecurity(service,4,bytes))throw new Win32Exception();return result;
        }
        catch{try{if(result.Status().State==1)DeleteService(service);}catch{}result.Dispose();throw;}
        void Change<T>(uint kind,ref T value)where T:struct{var memory=Marshal.AllocHGlobal(Marshal.SizeOf<T>());try{Marshal.StructureToPtr(value,memory,false);if(!ChangeServiceConfig2W(service,kind,memory))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}}
    }
    internal static NativeServiceInstall OpenOwned()
    {
        var manager=new ServiceHandle(OpenSCManagerW(null,null,1));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        var service=new ServiceHandle(OpenServiceW(manager,InstallLocations.ServiceName,0x10037));if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        var result=new NativeServiceInstall(manager,service);try{result.RequireOwned();return result;}catch{result.Dispose();throw;}
    }
    internal void RequireOwned()
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var size);if(size is <44 or >16384)throw new IOException("SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)size);
        try{if(!QueryServiceConfigW(service,memory,size,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<Config>(memory);
            RequireOwnedConfiguration(config.Type,Marshal.PtrToStringUni(config.Account),Marshal.PtrToStringUni(config.Image));}
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static void RequireOwnedConfiguration(uint type,string? account,string? image)
    {
        if(type!=16||!string.Equals(account,"NT SERVICE\\"+InstallLocations.ServiceName,StringComparison.OrdinalIgnoreCase)||
           image!="\""+InstallLocations.ServiceImage+"\" --service")throw new IOException("SERVICE_NOT_OWNED");
    }
    internal void RequireStopped(){RequireOwned();if(Status().State!=1)throw new IOException("SERVICE_NOT_QUIESCENT");}
    internal void EnableAutomaticStart()
    {
        RequireOwned();if(!ChangeServiceConfigW(service,0xffffffff,2,0xffffffff,null,null,IntPtr.Zero,null,null,null,null))throw new Win32Exception();
        var memory=Marshal.AllocHGlobal(4);try{Marshal.WriteInt32(memory,1);if(!ChangeServiceConfig2W(service,3,memory))throw new Win32Exception();}finally{Marshal.FreeHGlobal(memory);}
    }
    internal async Task StartAsync(Action<int> observed,CancellationToken cancellation)
    {
        RequireOwned();if(!StartServiceW(service,0,IntPtr.Zero)&&Marshal.GetLastPInvokeError()!=1056)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(6));
        while(true)
        {
            deadline.Token.ThrowIfCancellationRequested();var status=Status();if(status.State==4){if(status.Pid==0)throw new IOException("SERVICE_PID_MISSING");return;}
            if(status.State==1)throw new IOException("SERVICE_START_FAILED");observed((int)status.Checkpoint);await Task.Delay(250,deadline.Token);
        }
    }
    internal async Task StopAsync(CancellationToken cancellation)
    {
        RequireOwned();var before=Status();using var process=before.Pid==0?null:Process.GetProcessById((int)before.Pid);if(process is not null)_=process.SafeHandle;
        if(before.State!=1&&!ControlService(service,1,out _)&&Marshal.GetLastPInvokeError()!=1062)throw new Win32Exception();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(40));
        while(Status().State!=1)await Task.Delay(200,deadline.Token);
        if(process is not null)await process.WaitForExitAsync(deadline.Token);
    }
    internal void RequireCleanStop(){RequireOwned();var status=Status();RequireCleanStop(status.State,status.Win32,status.Specific);}
    internal static void RequireCleanStop(uint state,uint win32,uint specific)
    {if(state!=1||win32!=0||specific!=0)throw new IOException("UNINSTALL_QUIESCENCE_UNCONFIRMED");}
    internal void RequireRemovalStop(){RequireOwned();var status=Status();RequireRemovalStop(status.State,status.Win32,status.Specific,status.Pid);}
    internal static void RequireRemovalStop(uint state,uint win32,uint specific,uint pid)
    {
        // 1077 means no start attempts SINCE THE LAST BOOT, not "never ran" or
        // "previous cleanup succeeded". Removal still requires the authenticated
        // original Owner, exact service/image and protected-tree/data-lease gates.
        // Receipt repair deliberately retains the stricter RequireCleanStop.
        if(state!=1||pid!=0||specific!=0||win32 is not(0 or 1077))throw new IOException("UNINSTALL_QUIESCENCE_UNCONFIRMED");
    }
    internal void RequireConfigurationStop()
    {RequireOwned();var status=Status();RequireConfigurationStop(status.State,status.Win32,status.Specific,status.Pid);}
    internal static void RequireConfigurationStop(uint state,uint win32,uint specific,uint pid)
    {
        // ERROR_SERVICE_NEVER_STARTED is the normal stopped state of a freshly
        // created service. It is safe for configuration, not proof that an
        // application previously running completed an uninstall shutdown.
        if(state!=1||pid!=0||specific!=0||win32 is not(0 or 1077))throw new IOException("LAN_QUIESCENCE_UNCONFIRMED");
    }
    internal static async Task WaitUntilRemovedAsync(CancellationToken cancellation)
    {
        var started=System.Diagnostics.Stopwatch.StartNew();
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();try{if(!Exists())return;}catch(Win32Exception e)when(e.NativeErrorCode==1072){}
            if(started.Elapsed>TimeSpan.FromSeconds(10))throw new IOException("UNINSTALL_SERVICE_REMOVAL_PENDING");await Task.Delay(100,cancellation);
        }
    }
    internal void Delete(){RequireOwned();if(Status().State!=1)throw new IOException("SERVICE_NOT_QUIESCENT");if(!DeleteService(service))throw new Win32Exception();}
    internal void DeleteForRemoval(){RequireRemovalStop();if(!DeleteService(service))throw new Win32Exception();}
    private StatusProcess Status(){if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<StatusProcess>(),out _))throw new Win32Exception();return status;}
    public void Dispose(){service.Dispose();manager.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct Privileges{public IntPtr Names;}
    [StructLayout(LayoutKind.Sequential)]private struct Config{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusProcess{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct StatusBasic{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid{internal ServiceHandle(IntPtr h):base(true){SetHandle(h);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(ServiceHandle manager,string name,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr CreateServiceW(ServiceHandle manager,string name,string display,uint rights,uint type,uint start,uint error,string image,string? group,IntPtr tag,string? dependencies,string account,string? password);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfig2W(ServiceHandle service,uint kind,IntPtr info);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool ChangeServiceConfigW(ServiceHandle service,uint type,uint start,uint error,string? image,string? group,IntPtr tag,string? dependencies,string? account,string? password,string? display);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool SetServiceObjectSecurity(ServiceHandle service,uint info,byte[] descriptor);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(ServiceHandle service,uint kind,out StatusProcess status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(ServiceHandle service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool StartServiceW(ServiceHandle service,uint count,IntPtr arguments);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(ServiceHandle service,uint control,out StatusBasic status);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
