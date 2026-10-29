using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Yahad.Service;

// Transport only: no CreateService/OpenSCManager/StartService/DeleteService or
// account/ACL installation APIs exist in this executable.
internal sealed class NativeScmApi:IScmApi
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate void MainCallback(uint count,IntPtr arguments);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]private delegate uint ControlCallback(uint control,uint eventType,IntPtr eventData,IntPtr context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct TableEntry
    {[MarshalAs(UnmanagedType.LPWStr)]public string? Name;public MainCallback? Main;}
    [StructLayout(LayoutKind.Sequential)]internal struct NativeStatus
    {public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint;}
    private MainCallback? mainRoot;private ControlCallback? handlerRoot;private IntPtr handle;
    public void Dispatch(string name,Action<string[]> main)
    {
        mainRoot=(count,pointers)=>
        {
            try
            {
                if(count is <1 or >8)throw new IOException("SCM_ARGUMENT_BOUND");
                var args=new string[count];
                for(var i=0;i<count;i++)
                {
                    args[i]=Marshal.PtrToStringUni(Marshal.ReadIntPtr(pointers,i*IntPtr.Size))??throw new IOException("SCM_ARGUMENT_NULL");
                    if(args[i].Length>256)throw new IOException("SCM_ARGUMENT_BOUND");
                }
                main(args);
            }
            catch
            {
                // Never let a managed exception cross a native callback or let
                // dispatcher wait forever after unsafe startup/status failure.
                Environment.Exit(1);
            }
        };
        var table=new[]{new TableEntry{Name=name,Main=mainRoot},new TableEntry()};
        if(!StartServiceCtrlDispatcherW(table))throw new Win32Exception();
        GC.KeepAlive(mainRoot);GC.KeepAlive(handlerRoot);
    }
    public void Register(string name,Func<uint,uint> handler)
    {
        if(handle!=IntPtr.Zero)throw new InvalidOperationException("SCM_ALREADY_REGISTERED");
        handlerRoot=(control,_,_,_)=>{try{return handler(control);}catch{return 120;}};
        handle=RegisterServiceCtrlHandlerExW(name,handlerRoot,IntPtr.Zero);
        if(handle==IntPtr.Zero)throw new Win32Exception();
    }
    public void Publish(ServiceReport report)
    {
        if(handle==IntPtr.Zero)throw new InvalidOperationException("SCM_HANDLER_REQUIRED");
        var status=new NativeStatus{Type=ServiceDefinition.OwnProcess,State=(uint)report.State,Accepted=report.AcceptedControls,
            Win32=report.Win32ExitCode,Specific=report.ServiceSpecificExitCode,Checkpoint=report.Checkpoint,WaitHint=report.WaitHint};
        if(!SetServiceStatus(handle,ref status))throw new Win32Exception();
    }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool StartServiceCtrlDispatcherW([In] TableEntry[] table);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern IntPtr RegisterServiceCtrlHandlerExW(string name,ControlCallback handler,IntPtr context);
    [DllImport("advapi32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetServiceStatus(IntPtr handle,ref NativeStatus status);
}
