using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

/// <summary>Native process containment spike. Not a registered Windows service.</summary>
public sealed class JobProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    public int ProcessId { get; }
    private JobProcess(SafeFileHandle job, SafeProcessHandle process, int id)
    { this.job = job; this.process = process; ProcessId = id; }

    public static JobProcess Start(string executable, IEnumerable<string> arguments, string directory, IReadOnlyDictionary<string,string>? environment = null)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("EXECUTABLE_REQUIRED");
        var environmentBlock = LaunchEnvironment.Block(environment ?? LaunchEnvironment.Clean(directory));
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { Marshal.FreeHGlobal(environmentPointer); throw new Win32Exception(); }
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        ProcessInfo process = default;
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!Native.SetInformationJobObject(job, 9, buffer, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            // No shell. Suspended creation closes the race where a child could spawn before containment.
            if (!Native.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x00000004 | 0x08000000 | 0x00000400, environmentPointer, directory, ref start, out process)) throw new Win32Exception();
            if (!Native.AssignProcessToJobObject(job, process.Process)) throw new Win32Exception();
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            var result = new JobProcess(job, new SafeProcessHandle(process.Process, true), (int)process.Id);
            process.Process = IntPtr.Zero; // Result owns the exact process handle, avoiding PID reuse.
            return result;
        }
        catch
        {
            // If assignment failed the suspended child is not yet owned by the job.
            if (process.Process != IntPtr.Zero) Native.TerminateProcess(process.Process, 1);
            job.Dispose();
            throw;
        }
        finally
        {
            if (process.Thread != IntPtr.Zero) Native.CloseHandle(process.Thread);
            if (process.Process != IntPtr.Zero) Native.CloseHandle(process.Process);
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(environmentPointer);
        }
    }

    public async Task<int> WaitAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            var status = Native.WaitForSingleObject(process, 0);
            if (status == 0)
            {
                if (!Native.GetExitCodeProcess(process, out var code)) throw new Win32Exception();
                return unchecked((int)code);
            }
            if (status != 258) throw new Win32Exception(); // WAIT_TIMEOUT is the only continuing state.
            await Task.Delay(20, cancellation);
        }
    }
    // A root process exit is insufficient: a descendant may still hold the DB.
    // Keep the job handle open until the complete contained tree is empty.
    public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Native.QueryInformationJobObject(job, 1, out var accounting,
                    (uint)Marshal.SizeOf<BasicAccounting>(), IntPtr.Zero)) throw new Win32Exception();
            if (accounting.ActiveProcesses == 0) return;
            await Task.Delay(20, cancellation);
        }
    }
    public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
    {
        // Keep the job handle until the OS reports every contained process gone.
        if (!Native.TerminateJobObject(job,1)) throw new Win32Exception();
        await WaitForTreeEmptyAsync(cancellation);
    }
    public void Dispose() { job.Dispose(); process.Dispose(); }

    // Microsoft C runtime quoting, including trailing slashes before the closing quote.
    internal static string Quote(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("NUL_ARGUMENT");
        var b = new StringBuilder("\""); var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            b.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            b.Append(c); slashes = 0;
        }
        return b.Append('\\', slashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential)] private struct BasicAccounting
    {
        public long TotalUser, TotalKernel, PeriodUser, PeriodKernel;
        public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimit
    {
        public long ProcessTime, JobTime;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong Read, Write, Other, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimit
    {
        public BasicLimit Basic; public IoCounters Io;
        public UIntPtr ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInfo
    {
        public int Size; public string? Reserved, Desktop, Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short Show, ReservedSize; public IntPtr Reserved2, In, Out, Error;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ProcessInfo { public IntPtr Process, Thread; public uint Id, ThreadId; }
    private static class Native
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, IntPtr information, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TerminateJobObject(SafeFileHandle job,uint code);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcessW(string app, StringBuilder command, IntPtr processAttributes,
            IntPtr threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment,
            string directory, ref StartupInfo startup, out ProcessInfo process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool QueryInformationJobObject(SafeFileHandle job, int kind,
            out BasicAccounting information, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
