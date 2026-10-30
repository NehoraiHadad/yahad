using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

// Internal failure-only test injection. No production configuration/CLI surface;
// it can deny observations, never manufacture successful native observations.
internal sealed class JobProcessFaultInjection
{
    internal bool FailBeforeAssignment,FailAfterHandoff,FailResumeThread,RootObservationUnavailable,TreeObservationUnavailable;
    internal JobProcess? CreatedProcess;
    internal IContainedAppSession? FailedStartSession;
    internal bool RootExitObserved,TreeEmptyObserved;
}

/// <summary>Retains the exact owned process/job after an unconfirmed failed start.
/// Do not release data reservations until a later observed cleanup succeeds.</summary>
public sealed class JobStartQuiescenceUnconfirmedException:IOException
{
    public JobProcess OwnedProcess {get;}
    internal JobStartQuiescenceUnconfirmedException(JobProcess owned,Exception start,Exception cleanup)
        :base("JOB_START_QUIESCENCE_UNCONFIRMED",new AggregateException(start,cleanup)){OwnedProcess=owned;}
}

/// <summary>Native owned process tree. No service registration APIs.</summary>
public sealed class JobProcess : IDisposable
{
    private readonly SafeFileHandle job;
    private readonly SafeProcessHandle process;
    private readonly JobProcessFaultInjection? fault;
    public int ProcessId { get; }
    internal JobAccounting ObserveAccounting()=>JobAccounting.Read(job);
    private JobProcess(SafeFileHandle job, SafeProcessHandle process, int id,JobProcessFaultInjection? fault)
    { this.job = job; this.process = process; ProcessId = id;this.fault=fault; }

    public static JobProcess Start(string executable, IEnumerable<string> arguments, string directory, IReadOnlyDictionary<string,string>? environment = null, Action<SafeProcessHandle,int>? beforeResume = null)
        =>StartCore(executable,arguments,directory,environment,beforeResume,null);
    internal static JobProcess StartWithFault(string executable,IEnumerable<string> arguments,string directory,
        IReadOnlyDictionary<string,string>? environment,Action<SafeProcessHandle,int>? beforeResume,JobProcessFaultInjection fault)
        =>StartCore(executable,arguments,directory,environment,beforeResume,fault);
    private static JobProcess StartCore(string executable,IEnumerable<string> arguments,string directory,
        IReadOnlyDictionary<string,string>? environment,Action<SafeProcessHandle,int>? beforeResume,JobProcessFaultInjection? fault)
    {
        if (!Path.IsPathFullyQualified(executable) || !File.Exists(executable)) throw new IOException("EXECUTABLE_REQUIRED");
        var environmentBlock = LaunchEnvironment.Block(environment ?? LaunchEnvironment.Clean(directory));
        var environmentPointer = Marshal.StringToHGlobalUni(environmentBlock);
        var job = Native.CreateJobObjectW(IntPtr.Zero, null);
        if (job.IsInvalid) { Marshal.FreeHGlobal(environmentPointer); throw new Win32Exception(); }
        var info = new ExtendedLimit { Basic = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        ProcessInfo process = default;JobProcess? child=null;
        try
        {
            Marshal.StructureToPtr(info, buffer, false);
            if (!Native.SetInformationJobObject(job, 9, buffer, (uint)Marshal.SizeOf<ExtendedLimit>())) throw new Win32Exception();
            var start = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>() };
            var command = new StringBuilder(Quote(executable) + " " + string.Join(" ", arguments.Select(Quote)));
            // No shell. Suspended creation closes the race where a child could spawn before containment.
            if (!Native.CreateProcessW(executable, command, IntPtr.Zero, IntPtr.Zero, false,
                    0x00000004 | 0x08000000 | 0x00000400, environmentPointer, directory, ref start, out process)) throw new Win32Exception();
            child=new JobProcess(job,new SafeProcessHandle(process.Process,true),(int)process.Id,fault);
            process.Process=IntPtr.Zero; // Ownership transfers immediately after successful creation.
            if(fault is not null)fault.CreatedProcess=child;
            if(fault?.FailBeforeAssignment==true)throw new IOException("INJECTED_BEFORE_ASSIGNMENT_FAILURE");
            if (!Native.AssignProcessToJobObject(job, child.process.DangerousGetHandle())) throw new Win32Exception();
            using(var borrowed=new SafeProcessHandle(child.process.DangerousGetHandle(),false))beforeResume?.Invoke(borrowed,child.ProcessId);
            if(fault?.FailAfterHandoff==true)throw new IOException("INJECTED_HANDOFF_FAILURE");
            if(fault?.FailResumeThread==true)throw new Win32Exception(6); // failure-only synthetic injection
            if (Native.ResumeThread(process.Thread) == uint.MaxValue) throw new Win32Exception();
            return child;
        }
        catch(Exception startFailure)
        {
            if(child is null)job.Dispose();
            else
            {
                try
                {
                    using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    child.TerminateAndWaitForTreeEmptyAsync(cleanup.Token).GetAwaiter().GetResult();
                }
                catch(Exception cleanupFailure)
                {
                    // Keep the exact root and job handles owned. The service
                    // launcher must transfer this owner into its failed session.
                    throw new JobStartQuiescenceUnconfirmedException(child,startFailure,cleanupFailure);
                }
                child.Dispose();
            }
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
            if(fault?.RootObservationUnavailable==true)throw new IOException("INJECTED_ROOT_OBSERVATION_UNAVAILABLE");
            var status = Native.WaitForSingleObject(process, 0);
            if (status == 0)
            {
                if (!Native.GetExitCodeProcess(process, out var code)) throw new Win32Exception();
                if(fault is not null)fault.RootExitObserved=true;
                return unchecked((int)code);
            }
            if (status != 258) throw new Win32Exception(); // WAIT_TIMEOUT is the only continuing state.
            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }
    // A root process exit is insufficient: a descendant may still hold the DB.
    // Keep the job handle open until the complete contained tree is empty.
    public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if(fault?.TreeObservationUnavailable==true)throw new IOException("INJECTED_TREE_OBSERVATION_UNAVAILABLE");
            var accounting=JobAccounting.Read(job);
            if (accounting.TreeEmpty){if(fault is not null)fault.TreeEmptyObserved=true;return;}
            await Task.Delay(20, cancellation).ConfigureAwait(false);
        }
    }
    public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
    {
        // Requests are not observations. Also terminate/wait the exact root:
        // it may have been created successfully but not assigned to the job.
        _=Native.TerminateJobObject(job,1);
        _=Native.TerminateProcess(process.DangerousGetHandle(),1);
        await WaitAsync(cancellation).ConfigureAwait(false);
        await WaitForTreeEmptyAsync(cancellation).ConfigureAwait(false);
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
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(IntPtr process, uint code);
        [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CloseHandle(IntPtr handle);
    }
}
