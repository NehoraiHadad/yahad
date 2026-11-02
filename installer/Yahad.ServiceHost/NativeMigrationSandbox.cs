using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Service;

internal interface IMigrationSuspendedChild:IMigrationChildSession
{void Resume();}
internal interface IMigrationSandboxKernel
{IMigrationSuspendedChild CreateSuspended(VerifiedMigrationSpec spec);}

// This factory runs only inside the admitted Runtime service. Kernel adapters
// can be replaced by trusted tests, never by environment, CLI or protocol input.
internal sealed class NativeMigrationSandbox(IMigrationSandboxKernel kernel):IMigrationProcessFactory
{
    internal static NativeMigrationSandbox Installed()=>new(new WindowsMigrationSandboxKernel());
    internal static string ProfileName(string installation)
    {InstalledActivationResolver.Id(installation);return "Yahad.Migrate."+installation;}
    internal static string DeriveProfileSid(string installation)=>WindowsMigrationSandboxKernel.DeriveProfileSid(installation);
    public async Task<IMigrationChildSession> StartAsync(VerifiedMigrationSpec spec,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();ValidateSpec(spec);
        var owned=kernel.CreateSuspended(spec);
        try
        {
            RequireObservation(spec,owned.Observe());cancellation.ThrowIfCancellationRequested();owned.Resume();
            RequireObservation(spec,owned.Observe());return owned;
        }
        catch(Exception failure)
        {
            try
            {
                using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await owned.TerminateAndWaitForTreeEmptyAsync(cleanup.Token);await owned.DisposeAsync();
            }
            catch
            {
                // Transfer the exact native owner. Core stores this session
                // before Observe throws and retains all data reservations.
                return new FailedMigrationChild(owned,failure);
            }
            throw;
        }
    }
    internal static void ValidateSpec(VerifiedMigrationSpec spec)
    {
        spec.Validate();
        InstalledActivationResolver.Id(spec.Plan.InstallationId);InstalledActivationResolver.Id(spec.Plan.OperationId);InstalledActivationResolver.Id(spec.Nonce);
        InstalledActivationResolver.Digest(spec.NodeDigest);InstalledActivationResolver.Digest(spec.Plan.EntryDigest);InstalledActivationResolver.Digest(spec.Plan.PayloadDigest);
        var payload=Canonical(spec.PayloadRoot);var clone=Canonical(spec.CloneRoot);
        if(!Same(spec.NodePath,Path.Combine(payload,"runtime","node","node.exe"))||!Same(spec.EntryPath,Path.Combine(payload,"app","src","native-migrate-v1.ts"))||
            !Same(spec.DataDirectory,Path.Combine(clone,"data"))||Same(payload,clone)||Within(clone,payload)||Within(payload,clone)||
            !spec.ExpectedRuntimeSid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||!spec.ExpectedAuthoritySid.StartsWith("S-1-5-80-",StringComparison.Ordinal)||spec.ExpectedRuntimeSid==spec.ExpectedAuthoritySid||
            !spec.ExpectedAppContainerSid.StartsWith("S-1-15-2-",StringComparison.Ordinal)||spec.ExpectedAppContainerSid=="S-1-15-2-1")throw new IOException("MIGRATION_PROCESS_SPEC");
        foreach(var path in new[]{spec.NodePath,spec.EntryPath,spec.PayloadRoot,spec.CloneRoot,spec.DataDirectory,spec.RequestFile,spec.ResultFile,spec.TemporaryDirectory,spec.SourceDataDirectory,spec.ControlRoot})_ =Canonical(path);
        if(Same(spec.SourceDataDirectory,clone)||Same(spec.SourceDataDirectory,spec.DataDirectory)||Within(spec.SourceDataDirectory,clone)||Within(clone,spec.SourceDataDirectory)||Same(spec.ControlRoot,clone)||
            Same(spec.RequestFile,spec.ResultFile)||Within(spec.SourceDataDirectory,spec.ResultFile))throw new IOException("MIGRATION_PROCESS_SCOPE");
        _ =new SecurityIdentifier(spec.ExpectedRuntimeSid);_ =new SecurityIdentifier(spec.ExpectedAuthoritySid);_ =new SecurityIdentifier(spec.ExpectedAppContainerSid);
    }
    internal static void RequireObservation(VerifiedMigrationSpec spec,MigrationSandboxObservation observed)
    {
        if(observed.ProcessId<1||observed.Created<=0||observed.RuntimeSid!=spec.ExpectedRuntimeSid||observed.AppContainerSid!=spec.ExpectedAppContainerSid||
            observed.CapabilitySids is null||observed.CapabilitySids.Length!=0||observed.LoopbackExempt||observed.EntryDigest!=spec.Plan.EntryDigest||observed.NodeDigest!=spec.NodeDigest)
            throw new UnauthorizedAccessException("MIGRATION_SANDBOX_OBSERVATION");
    }
    private static string Canonical(string path)=>WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(path));
    private static bool Same(string left,string right)=>Canonical(left).Equals(Canonical(right),StringComparison.OrdinalIgnoreCase);
    private static bool Within(string parent,string path)=>Canonical(path).StartsWith(Canonical(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase);
    private sealed class FailedMigrationChild(IMigrationChildSession inner,Exception failure):IMigrationChildSession
    {
        public MigrationSandboxObservation Observe()=>throw new IOException("MIGRATION_START_CLEANUP_UNCONFIRMED",failure);
        public Task RequestGracefulStopAsync(CancellationToken c)=>inner.RequestGracefulStopAsync(c);
        public Task<int> WaitForRootExitAsync(CancellationToken c)=>inner.WaitForRootExitAsync(c);
        public Task WaitForTreeEmptyAsync(CancellationToken c)=>inner.WaitForTreeEmptyAsync(c);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken c)=>inner.TerminateAndWaitForTreeEmptyAsync(c);
        public ValueTask DisposeAsync()=>inner.DisposeAsync();
    }
}

// New AppContainer profiles/ACLs are provisioned by the initial full-consent
// installation. This implementation never repairs either boundary. Creating a
// job alone does not deny network: SECURITY_CAPABILITIES and observed token do.
internal sealed class WindowsMigrationSandboxKernel:IMigrationSandboxKernel
{
    internal static string DeriveProfileSid(string installation)
    {
        var error=DeriveAppContainerSidFromAppContainerName(NativeMigrationSandbox.ProfileName(installation),out var sid);
        try{if(error!=0)Marshal.ThrowExceptionForHR(error);if(sid==IntPtr.Zero)throw new IOException("MIGRATION_PROFILE_SID");return new SecurityIdentifier(sid).Value;}
        finally{if(sid!=IntPtr.Zero)FreeSid(sid);}
    }
    public IMigrationSuspendedChild CreateSuspended(VerifiedMigrationSpec spec)
    {
        NativeMigrationSandbox.ValidateSpec(spec);var tokens=new WindowsServiceTokenReader();ServiceIdentityPolicy.Require(tokens.Read(),spec.ExpectedRuntimeSid);
        foreach(var path in new[]{spec.PayloadRoot,spec.CloneRoot,spec.SourceDataDirectory,spec.ControlRoot,spec.TemporaryDirectory})SafeDirectory.NoReparseAncestors(path);
        var node=Pin(spec.NodePath,spec.NodeDigest);FileStream? entry=null;SafeFileHandle? job=null;WindowsMigrationChild? child=null;
        IntPtr sid=IntPtr.Zero,attributes=IntPtr.Zero,capabilities=IntPtr.Zero,environment=IntPtr.Zero;var attributesInitialized=false;ProcessInformation created=default;
        try
        {
            entry=Pin(spec.EntryPath,spec.Plan.EntryDigest);
            var hr=DeriveAppContainerSidFromAppContainerName(NativeMigrationSandbox.ProfileName(spec.Plan.InstallationId),out sid);if(hr!=0)Marshal.ThrowExceptionForHR(hr);
            if(new SecurityIdentifier(sid).Value!=spec.ExpectedAppContainerSid||LoopbackExempt(spec.ExpectedAppContainerSid))throw new UnauthorizedAccessException("MIGRATION_CONTAINER_PROFILE");
            UIntPtr bytes=UIntPtr.Zero;_ =InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref bytes);
            if(bytes.ToUInt64() is <1 or >65536)throw new IOException("MIGRATION_ATTRIBUTE_BOUND");attributes=Marshal.AllocHGlobal(checked((int)bytes.ToUInt64()));
            if(!InitializeProcThreadAttributeList(attributes,1,0,ref bytes))throw new Win32Exception();
            attributesInitialized=true;
            capabilities=Marshal.AllocHGlobal(Marshal.SizeOf<SecurityCapabilities>());Marshal.StructureToPtr(new SecurityCapabilities{AppContainerSid=sid},capabilities,false);
            if(!UpdateProcThreadAttribute(attributes,0,(UIntPtr)0x00020009,capabilities,(UIntPtr)Marshal.SizeOf<SecurityCapabilities>(),IntPtr.Zero,IntPtr.Zero))throw new Win32Exception();
            job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception();
            var limits=new ExtendedLimit{Basic=new BasicLimit{Flags=0x2000}};var limit=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
            try{Marshal.StructureToPtr(limits,limit,false);if(!SetInformationJobObject(job,9,limit,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new Win32Exception();}finally{Marshal.FreeHGlobal(limit);}
            environment=Marshal.StringToHGlobalUni(LaunchEnvironment.Block(LaunchEnvironment.Clean(spec.TemporaryDirectory)));
            var start=new StartupInfoEx{Startup=new StartupInfo{Size=Marshal.SizeOf<StartupInfoEx>()},Attributes=attributes};
            var command=new StringBuilder(JobProcess.Quote(spec.NodePath)+" --import tsx "+JobProcess.Quote(spec.EntryPath)+" --request "+JobProcess.Quote(spec.RequestFile));
            // No handle list, inheritance or source data lease. Suspended start
            // lets the host verify actual token/job/access before any Node code.
            if(!CreateProcessW(spec.NodePath,command,IntPtr.Zero,IntPtr.Zero,false,0x00000004|0x00080000|0x08000000|0x00000400,
                environment,Path.Combine(spec.PayloadRoot,"app"),ref start,out created))throw new Win32Exception();
            child=new(spec,job,new SafeProcessHandle(created.Process,true),new SafeWaitHandle(created.Thread,true),checked((int)created.Id),node,entry);
            job=null;node=null!;entry=null;created=default;
            try{child.Assign();child.RequireFilesystemBoundary();}catch(Exception error){child.StartFailure=error;}
            return child;
        }
        catch
        {
            if(child is not null)return child;
            job?.Dispose();entry?.Dispose();node?.Dispose();throw;
        }
        finally
        {
            if(created.Thread!=IntPtr.Zero)CloseHandle(created.Thread);if(created.Process!=IntPtr.Zero)CloseHandle(created.Process);
            if(environment!=IntPtr.Zero)Marshal.FreeHGlobal(environment);if(capabilities!=IntPtr.Zero)Marshal.FreeHGlobal(capabilities);
            if(attributes!=IntPtr.Zero){if(attributesInitialized)DeleteProcThreadAttributeList(attributes);Marshal.FreeHGlobal(attributes);}if(sid!=IntPtr.Zero)FreeSid(sid);
        }
    }
    private static FileStream Pin(string path,string expected)
    {
        SafeDirectory.NoReparseAncestors(path);var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(!GetFileInformationByHandle(file.SafeFileHandle,out var info)||info.Links!=1||(info.Attributes&0x410)!=0||file.Length is <1 or >536870912||
                Convert.ToHexStringLower(SHA256.HashData(file))!=expected)throw new IOException("MIGRATION_IMAGE_PIN");return file;
        }
        catch{file.Dispose();throw;}
    }
    private sealed class WindowsMigrationChild(VerifiedMigrationSpec spec,SafeFileHandle job,SafeProcessHandle process,SafeWaitHandle thread,int pid,FileStream node,FileStream entry):IMigrationSuspendedChild
    {
        internal Exception? StartFailure;private bool resumed,rootExited,treeEmpty,disposed;private MigrationSandboxObservation? observed;
        internal void Assign(){if(!AssignProcessToJobObject(job,process))throw new Win32Exception();}
        public MigrationSandboxObservation Observe()
        {
            ObjectDisposedException.ThrowIf(disposed,this);if(StartFailure is{} failure)throw new IOException("MIGRATION_CREATED_START_FAILED",failure);
            if(!IsProcessInJob(process,job,out var member)||!member)throw new UnauthorizedAccessException("MIGRATION_JOB_BINDING");
            if(!GetProcessTimes(process,out var created,out _,out _,out _))throw new Win32Exception();
            if(rootExited&&observed is not null)return observed;
            using var token=Token(process);var app=ReadToken(token,29);try{if(Marshal.ReadInt32(app)!=1)throw new UnauthorizedAccessException("MIGRATION_NOT_APPCONTAINER");}finally{Marshal.FreeHGlobal(app);}
            var user=TokenSid(token,1);var package=TokenSid(token,31);var caps=Capabilities(token);var image=new StringBuilder(32768);var size=(uint)image.Capacity;
            if(!QueryFullProcessImageNameW(process,0,image,ref size)||!Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(spec.NodePath),StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("MIGRATION_PROCESS_IMAGE");
            observed=new(pid,created,user,package,caps,LoopbackExempt(package),spec.Plan.EntryDigest,spec.NodeDigest);
            NativeMigrationSandbox.RequireObservation(spec,observed);return observed;
        }
        internal void RequireFilesystemBoundary()
        {
            _=Observe();using var token=Token(process);if(!ImpersonateLoggedOnUser(token))throw new Win32Exception();
            try
            {
                Access(spec.NodePath,0xa0000000,false,true);Access(spec.EntryPath,0x80000000,false,true);
                Access(spec.PayloadRoot,1,true,true);Access(spec.PayloadRoot,2,true,false);Access(spec.CloneRoot,2,true,false);
                Access(spec.DataDirectory,7,true,true);Access(spec.RequestFile,0x80000000,false,true);
                Access(Path.GetDirectoryName(spec.ResultFile)!,7,true,true);Access(spec.TemporaryDirectory,7,true,true);
                // Deny each right independently: a combined request could fail
                // because read is denied while an accidental write grant exists.
                foreach(var right in new uint[]{1,2,4}){Access(spec.SourceDataDirectory,right,true,false);Access(spec.ControlRoot,right,true,false);}
                foreach(var right in new uint[]{0x80000000,0x40000000})Access(Path.Combine(spec.SourceDataDirectory,"app.sqlite"),right,false,false);
                Access(Path.Combine(spec.ControlRoot,"activation.json"),0x40000000,false,false);
                Access(Path.Combine(spec.ControlRoot,"update-authority.json"),0x40000000,false,false);
            }
            finally{if(!RevertToSelf())Environment.FailFast("MIGRATION_IDENTITY_REVERSION_FAILED");}
        }
        public void Resume()
        {ObjectDisposedException.ThrowIf(disposed,this);if(resumed||StartFailure is not null)throw new IOException("MIGRATION_RESUME_STATE");_ =Observe();if(ResumeThread(thread)==uint.MaxValue)throw new Win32Exception();resumed=true;thread.Dispose();}
        public Task RequestGracefulStopAsync(CancellationToken cancellation){cancellation.ThrowIfCancellationRequested();return Task.CompletedTask;}
        public async Task<int> WaitForRootExitAsync(CancellationToken cancellation)
        {
            while(true){cancellation.ThrowIfCancellationRequested();var state=WaitForSingleObject(process,0);if(state==0){if(!GetExitCodeProcess(process,out var code))throw new Win32Exception();rootExited=true;return unchecked((int)code);}if(state!=258)throw new Win32Exception();await Task.Delay(20,cancellation);}
        }
        public async Task WaitForTreeEmptyAsync(CancellationToken cancellation)
        {while(true){cancellation.ThrowIfCancellationRequested();if(JobAccounting.Read(job).TreeEmpty){treeEmpty=true;return;}await Task.Delay(20,cancellation);}}
        public async Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation)
        {_=TerminateJobObject(job,1);_=TerminateProcess(process,1);await WaitForRootExitAsync(cancellation);await WaitForTreeEmptyAsync(cancellation);}
        public ValueTask DisposeAsync()
        {
            if(disposed)return ValueTask.CompletedTask;if(!rootExited||!treeEmpty)throw new IOException("MIGRATION_DISPOSE_BEFORE_QUIESCENCE");
            thread.Dispose();process.Dispose();job.Dispose();entry.Dispose();node.Dispose();disposed=true;return ValueTask.CompletedTask;
        }
    }
    private static SafeAccessTokenHandle Token(SafeProcessHandle process)
    {if(!OpenProcessToken(process,0x000e,out var token))throw new Win32Exception();return token;}
    private static IntPtr ReadToken(SafeAccessTokenHandle token,int kind)
    {
        _=GetTokenInformation(token,kind,IntPtr.Zero,0,out var size);if(size is <4 or >65536)throw new IOException("MIGRATION_TOKEN_BOUND");var memory=Marshal.AllocHGlobal((int)size);
        try{if(!GetTokenInformation(token,kind,memory,size,out _))throw new Win32Exception();return memory;}catch{Marshal.FreeHGlobal(memory);throw;}
    }
    private static string TokenSid(SafeAccessTokenHandle token,int kind)
    {var memory=ReadToken(token,kind);try{var sid=Marshal.ReadIntPtr(memory);if(sid==IntPtr.Zero)throw new IOException("MIGRATION_TOKEN_SID");return new SecurityIdentifier(sid).Value;}finally{Marshal.FreeHGlobal(memory);}}
    private static string[] Capabilities(SafeAccessTokenHandle token)
    {
        var memory=ReadToken(token,30);try
        {var count=Marshal.ReadInt32(memory);if(count is <0 or >128)throw new IOException("MIGRATION_CAPABILITY_BOUND");var result=new string[count];var offset=IntPtr.Size==8?8:4;var stride=Marshal.SizeOf<SidAttributes>();for(var i=0;i<count;i++)result[i]=new SecurityIdentifier(Marshal.PtrToStructure<SidAttributes>(memory+offset+i*stride).Sid).Value;return result;}
        finally{Marshal.FreeHGlobal(memory);}
    }
    private static bool LoopbackExempt(string package)
    {
        var error=NetworkIsolationGetAppContainerConfig(out var count,out var entries);if(error!=0)throw new Win32Exception((int)error);
        try
        {if(count>65536||count>0&&entries==IntPtr.Zero)throw new IOException("MIGRATION_LOOPBACK_BOUND");var stride=Marshal.SizeOf<SidAttributes>();for(var i=0u;i<count;i++)if(new SecurityIdentifier(Marshal.PtrToStructure<SidAttributes>(entries+checked((int)i*stride)).Sid).Value==package)return true;return false;}
        finally
        {if(entries!=IntPtr.Zero){var heap=GetProcessHeap();if(count<=65536){var stride=Marshal.SizeOf<SidAttributes>();for(var i=0u;i<count;i++){var sid=Marshal.PtrToStructure<SidAttributes>(entries+checked((int)i*stride)).Sid;if(sid!=IntPtr.Zero)_=HeapFree(heap,0,sid);}}_=HeapFree(heap,0,entries);}}
    }
    private static void Access(string path,uint access,bool directory,bool expected)
    {
        using var opened=CreateFileW(@"\\?\"+Path.GetFullPath(path),access,3,IntPtr.Zero,3,0x00200000u|(directory?0x02000000u:0),IntPtr.Zero);
        if(!opened.IsInvalid){if(!expected)throw new UnauthorizedAccessException("MIGRATION_SCOPE_ACCESS");return;}
        var error=Marshal.GetLastWin32Error();if(expected||error!=5)throw new Win32Exception(error);
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityCapabilities{public IntPtr AppContainerSid,Capabilities;public uint Count,Reserved;}
    [StructLayout(LayoutKind.Sequential)]private struct SidAttributes{public IntPtr Sid;public uint Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimit{public long ProcessTime,JobTime;public uint Flags;public UIntPtr Min,Max;public uint Count;public UIntPtr Affinity;public uint Priority,Scheduling;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit{public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct StartupInfo{public int Size;public string? Reserved,Desktop,Title;public int X,Y,XSize,YSize,XChars,YChars,Fill,Flags;public short Show,ReservedSize;public IntPtr Reserved2,In,Out,Error;}
    [StructLayout(LayoutKind.Sequential)]private struct StartupInfoEx{public StartupInfo Startup;public IntPtr Attributes;}
    [StructLayout(LayoutKind.Sequential)]private struct ProcessInformation{public IntPtr Process,Thread;public uint Id,ThreadId;}
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("userenv.dll",CharSet=CharSet.Unicode)]private static extern int DeriveAppContainerSidFromAppContainerName(string name,out IntPtr sid);
    [DllImport("advapi32.dll")]private static extern IntPtr FreeSid(IntPtr sid);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref UIntPtr size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,UIntPtr attribute,IntPtr value,UIntPtr size,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")]private static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,IntPtr info,uint bytes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CreateProcessW(string image,StringBuilder command,IntPtr process,IntPtr thread,bool inherit,uint flags,IntPtr environment,string directory,ref StartupInfoEx startup,out ProcessInformation info);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool AssignProcessToJobObject(SafeFileHandle job,SafeProcessHandle process);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool IsProcessInJob(SafeProcessHandle process,SafeFileHandle job,out bool result);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateJobObject(SafeFileHandle job,uint code);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool TerminateProcess(SafeProcessHandle process,uint code);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder name,ref uint size);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetTokenInformation(SafeAccessTokenHandle token,int kind,IntPtr memory,uint bytes,out uint returned);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool ImpersonateLoggedOnUser(SafeAccessTokenHandle token);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool RevertToSelf();
    [DllImport("firewallapi.dll")]private static extern uint NetworkIsolationGetAppContainerConfig(out uint count,out IntPtr entries);
    [DllImport("kernel32.dll")]private static extern IntPtr GetProcessHeap();
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool HeapFree(IntPtr heap,uint flags,IntPtr memory);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr attributes,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle file,out FileInformation info);
    [DllImport("kernel32.dll")][return:MarshalAs(UnmanagedType.Bool)]private static extern bool CloseHandle(IntPtr handle);
}
