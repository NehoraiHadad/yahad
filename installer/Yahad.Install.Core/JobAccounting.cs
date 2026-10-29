using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Install;

// JOBOBJECT_BASIC_ACCOUNTING_INFORMATION: four LARGE_INTEGERs, then four DWORDs.
// TotalProcesses is lifetime accounting; only ActiveProcesses observes emptiness.
[StructLayout(LayoutKind.Sequential)]
internal struct JobAccounting
{
    public long TotalUser,TotalKernel,PeriodUser,PeriodKernel;
    public uint PageFaults,TotalProcesses,ActiveProcesses,TerminatedProcesses;
    internal bool TreeEmpty=>ActiveProcesses==0;
    internal static JobAccounting Read(SafeFileHandle retainedJob)
    {
        if(!QueryInformationJobObject(retainedJob,1,out var accounting,(uint)Marshal.SizeOf<JobAccounting>(),IntPtr.Zero))throw new Win32Exception();
        return accounting;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(SafeFileHandle job,int kind,out JobAccounting accounting,uint length,IntPtr returned);
}
