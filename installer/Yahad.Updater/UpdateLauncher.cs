using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace Yahad.Updater;

// Starts a verified release's own Setup outside this process's job and console, so
// stopping and replacing this service cannot touch it. No handle is inherited.
internal static class UpdateLauncher
{
    private const uint Detached=0x8,NewGroup=0x200,Breakaway=0x1000000,Unicode=0x400;
    internal static string CommandLine(string setup,string directory,string result)=>Quote(setup)+" --quiet-upgrade "+Quote(directory)+" --result "+Quote(result);
    private static string Quote(string value)
    {if(value.Contains('"')||value.EndsWith('\\'))throw new IOException("UPDATER_LAUNCH_PATH");return "\""+value+"\"";}
    internal static void Launch(StagedRelease release,string result)
    {
        var startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfo>()};var line=new StringBuilder(CommandLine(release.Setup,release.Directory,result));
        if(!CreateProcessW(release.Setup,line,IntPtr.Zero,IntPtr.Zero,false,Detached|NewGroup|Breakaway|Unicode,IntPtr.Zero,release.Directory,ref startup,out var info))throw new Win32Exception();
        CloseHandle(info.Process);CloseHandle(info.Thread);
    }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo
    {public int Size;public string? Reserved,Desktop,Title;public int X,Y,XSize,YSize,XChars,YChars,Attribute,Flags;public short Show,Reserved2;public IntPtr Reserved3,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInfo{public IntPtr Process,Thread;public int ProcessId,ThreadId;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string image,StringBuilder line,IntPtr process,IntPtr thread,[MarshalAs(UnmanagedType.Bool)]bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfo startup,out ProcessInfo info);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
