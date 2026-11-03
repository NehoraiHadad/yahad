using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

// The waiting host publishes its OWN peer-access DACLs before sending HELLO.
// Keep the first SCM PID fixed while waiting; a pipe/frame can never select a
// different process or authorize adoption of a replacement service generation.
internal static class RuntimeWaitingHostPin
{
    internal static async Task<(RuntimeAdmissionMessage Hello,T Pin)> AfterHelloAsync<T>(InstallationBinding binding,uint expectedPid,
        Func<CancellationToken,Task<RuntimeAdmissionMessage>> read,Func<(uint State,uint Pid)> service,Func<uint> client,
        Func<RuntimeAdmissionMessage,T> pin,CancellationToken cancellation)
    {
        void Require()
        {
            cancellation.ThrowIfCancellationRequested();var observed=service();
            if(expectedPid==0||observed.Pid!=expectedPid||observed.State is not(2 or 4)||client()!=expectedPid)throw new UnauthorizedAccessException("RUNTIME_WAITING_FIXED_PID");
        }
        Require();var hello=await read(cancellation);Require();
        if(hello.Format!=1||hello.Kind!="hello"||hello.Admission is not null||hello.Binding!=binding||hello.Host is null||hello.Host.ProcessId!=expectedPid||hello.Host.Created<=0)
            throw new UnauthorizedAccessException("RUNTIME_WAITING_HELLO_BINDING");
        InstalledActivationResolver.Id(hello.Id);
        return(hello,pin(hello));
    }
}

// Actual SCM/pipe/job operations, compiled but never selected by Program. This
// adapter does not install services, repair ACLs or create credentials. Its fixed
// dual-service layout must be provisioned separately by the sole installer.
internal sealed class NativeScmRuntimeSession:IRetainedScmRuntimeSession,IRetainedScmUpdatePreparation,IRetainedScmRecoveryObservation,IProbationEffectsRuntime
{
    private WindowsFixedRuntimeService? service;private WindowsFixedRuntimeService.WindowsPinnedServiceProcess? host;
    private SafeFileHandle? job;private NamedPipeServerStream? pipe;private RuntimeAdmissionBinding? admission;
    private string? helloId;private bool startRequested,launchReleased,disposed;
    private uint waitingPid;private long waitingCreated;
    private RetainedRuntimeQuiescence? recoveryObservation;private bool recoveryObservationRetained;
    private readonly SemaphoreSlim commands=new(1,1);
    private string purpose="admit";private bool effectsIssued;
    public IRuntimeGateKernel Kernel=>new NativeRuntimeGateKernel(job??throw new IOException("RUNTIME_JOB_UNAVAILABLE"),host?.Handle??throw new IOException("RUNTIME_HOST_UNAVAILABLE"),Observe);
    public void RetainRecoveryObservation()
    {
        if(recoveryObservationRetained||launchReleased)throw new IOException("RUNTIME_RECOVERY_OBSERVATION_REPLAY");
        RequireAuthenticatedCurrent(admission??throw new IOException("RUNTIME_ADMISSION_UNAVAILABLE"));
        recoveryObservation=DuplicateRecoveryObservation();recoveryObservationRetained=true;
    }
    public RetainedRuntimeQuiescence AcquireRecoveryQuiescence()
    {
        if(recoveryObservation is { } retained){recoveryObservation=null;return retained;}
        if(recoveryObservationRetained)throw new IOException("RUNTIME_RECOVERY_OBSERVATION_TRANSFERRED");
        return DuplicateRecoveryObservation();
    }
    private RetainedRuntimeQuiescence DuplicateRecoveryObservation()
        =>RetainedRuntimeQuiescence.FromRetainedHostAndJob(host?.Handle??throw new IOException("RUNTIME_HOST_UNAVAILABLE"),job??throw new IOException("RUNTIME_JOB_UNAVAILABLE"),checked((int)host.Pid));
    private RuntimeGeneration Observe()
    {RequireAuthenticatedCurrent(admission??throw new IOException("RUNTIME_ADMISSION_UNAVAILABLE"));return new(admission.Binding.InstallationId,admission.ReleaseDigest,admission.Nonce,admission.ProcessId,admission.Created);}
    public async Task<RuntimeAdmissionBinding> StartWaitingAsync(RuntimeStartIntent intent,CancellationToken cancellation)
    {
        if(job is not null||disposed)throw new IOException("RUNTIME_NATIVE_SESSION_REPLAY");
        new RuntimeAdmissionBinding(intent.Binding,intent.ReleaseDigest,intent.Nonce,1,1).Validate();
        if(intent.Purpose is not("admit" or "probation"))throw new IOException("RUNTIME_PURPOSE_UNKNOWN");purpose=intent.Purpose;
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",WindowsFixedRuntimeService.AuthorityName).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        var actor=new SecurityIdentifier(InstalledAuthorityReader.RequireUpdater());
        job=CreateJobObjectW(IntPtr.Zero,null);if(job.IsInvalid)throw new Win32Exception();
        var limits=new ExtendedLimit{Basic=new(){LimitFlags=0x2000}}; // kill-on-close, no breakaway
        var memory=Marshal.AllocHGlobal(Marshal.SizeOf<ExtendedLimit>());
        try{Marshal.StructureToPtr(limits,memory,false);if(!SetInformationJobObject(job,9,memory,(uint)Marshal.SizeOf<ExtendedLimit>()))throw new Win32Exception();}
        finally{Marshal.FreeHGlobal(memory);}
        pipe=CreatePipe(intent.Binding.InstallationId,actor,runtime);
        service=WindowsFixedRuntimeService.RuntimeControl();
        // A collision is never adopted, stopped or assigned to this job.
        if(service.Status().State!=1)throw new IOException("RUNTIME_SERVICE_ALREADY_ACTIVE");
        // StartService failure may have an uncertain process outcome. Retain the
        // attempted owner before the call; never infer quiescence from its error.
        startRequested=true;service.Start();
        while(waitingPid==0)
        {
            cancellation.ThrowIfCancellationRequested();var status=service.Status();
            if(status.State==1)throw new IOException("RUNTIME_WAITING_HOST_EXITED");
            if(status.Pid!=0)waitingPid=status.Pid;
            else await Task.Delay(20,cancellation);
        }
        await pipe.WaitForConnectionAsync(cancellation);
        var waiting=await RuntimeWaitingHostPin.AfterHelloAsync(intent.Binding,waitingPid,c=>RuntimeAdmissionWire.ReadAsync(pipe,c),()=>service.Status(),
            ()=>{if(!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var client))throw new Win32Exception();return client;},
            frame=>
            {
                waitingCreated=frame.Host!.Created;
                var pinned=service.Pin(waitingPid,0x00101101); // ONLY after Runtime's published-access HELLO.
                try{if(pinned.Created!=waitingCreated)throw new UnauthorizedAccessException("RUNTIME_WAITING_CREATION_CHANGED");return pinned;}
                catch{pinned.Dispose();throw;}
            },cancellation);
        host=waiting.Pin;var hello=waiting.Hello;
        var peer=WindowsAuthorityPeer.Native().Read(pipe.SafePipeHandle);
        if(hello.Kind!="hello"||hello.Admission is not null||hello.Binding!=intent.Binding||hello.Host!=new RuntimeHostIdentity((int)host.Pid,host.Created)||
            peer.Sid!=runtime.Value||peer.ProcessId!=(int)host.Pid||peer.Created!=host.Created||peer.Session!=0||peer.Interactive)
            throw new UnauthorizedAccessException("RUNTIME_WAITING_PEER");
        helloId=hello.Id;admission=new(intent.Binding,intent.ReleaseDigest,intent.Nonce,(int)host.Pid,host.Created);
        RequireAuthenticatedCurrent(admission);return admission;
    }
    public void RequireAuthenticatedCurrent(RuntimeAdmissionBinding expected)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(expected!=admission||host is null||pipe is null||!GetNamedPipeClientProcessId(pipe.SafePipeHandle,out var pid)||pid!=host.Pid)throw new UnauthorizedAccessException("RUNTIME_NATIVE_PEER_CHANGED");
        host.RequireCurrent();if(expected.ProcessId!=(int)host.Pid||expected.Created!=host.Created)throw new UnauthorizedAccessException("RUNTIME_NATIVE_GENERATION_CHANGED");
    }
    public async Task ReleaseLaunchAsync(RuntimeAdmissionBinding expected,CancellationToken cancellation)
    {
        RequireAuthenticatedCurrent(expected);if(launchReleased)throw new IOException("RUNTIME_NATIVE_LAUNCH_REPLAY");
        // Independently check the actual assignment before emitting the launch
        // admission. Admit was performed by the lifecycle using this same kernel.
        var kernel=Kernel;var generation=Observe();
        if(!kernel.InOwnedJob(generation))throw new IOException("RUNTIME_JOB_UNCONFIRMED");
        launchReleased=true;await RuntimeAdmissionWire.WriteAsync(pipe!,new(1,purpose,helloId!,expected),cancellation);
    }
    public async Task<RuntimeAdmissionBinding> WaitReadyAsync(CancellationToken cancellation)
    {
        if(!launchReleased)throw new IOException("RUNTIME_LAUNCH_NOT_RELEASED");
        var ready=await RuntimeAdmissionWire.ReadAsync(pipe!,cancellation);RequireAuthenticatedCurrent(admission!);
        if(ready.Kind!="ready"||ready.Id!=admission!.Nonce||ready.Admission!=admission||ready.Binding is not null||ready.Host is not null)throw new UnauthorizedAccessException("RUNTIME_NATIVE_READY_BINDING");
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();RequireAuthenticatedCurrent(admission);
            if(service!.Status().State==4)break;await Task.Delay(20,cancellation);
        }
        await RuntimeAdmissionWire.WriteAsync(pipe!,new(1,"ready-accepted",ready.Id,admission),cancellation);return admission;
    }
    public async Task ReleaseEffectsAsync(ProbationRuntimeBinding binding,string requestId,CancellationToken cancellation)
    {
        binding.Validate();if(purpose!="probation"||effectsIssued||admission is null||binding.Binding!=admission.Binding||binding.ReleaseDigest!=admission.ReleaseDigest||binding.Nonce!=admission.Nonce)
            throw new UnauthorizedAccessException("PROBATION_RUNTIME_BINDING");effectsIssued=true;
        if((await CommandAsync(admission,"release-effects",requestId,cancellation)).Kind!="effects-released")throw new IOException("PROBATION_RELEASE_REPLY");
    }
    public async Task<bool> PrepareUpdateAsync(RuntimeAdmissionBinding expected,string id,CancellationToken cancellation)
    {
        var reply=await CommandAsync(expected,"prepare-update",id,cancellation);
        return reply.Kind switch{"update-idle"=>true,"update-busy"=>false,_=>throw new IOException("RUNTIME_IDLE_REPLY")};
    }
    public async Task CancelUpdateAsync(RuntimeAdmissionBinding expected,string id,CancellationToken cancellation)
    {if((await CommandAsync(expected,"cancel-update",id,cancellation)).Kind!="update-cancelled")throw new IOException("RUNTIME_CANCEL_REPLY");}
    private async Task<RuntimeAdmissionMessage> CommandAsync(RuntimeAdmissionBinding expected,string kind,string id,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(id);await commands.WaitAsync(cancellation);
        try
        {
            RequireAuthenticatedCurrent(expected);if(!launchReleased)throw new IOException("RUNTIME_NOT_RELEASED");
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            await RuntimeAdmissionWire.WriteAsync(pipe!,new(1,kind,id,expected),deadline.Token);
            return await RuntimeAdmissionWire.ReadControlReplyAsync(pipe!,expected,id,kind=="cancel-update",()=>RequireAuthenticatedCurrent(expected),deadline.Token);
        }
        finally{commands.Release();}
    }
    public async Task StopAsync(CancellationToken cancellation)
    {
        if(!startRequested)return;
        if(host is null)throw new IOException("RUNTIME_STOP_HOST_UNOBSERVED");
        // A Runtime already STOP_PENDING (a concurrent Setup STOP or SCM
        // SHUTDOWN) needs no signal; only a STOP actually sent is bound to the
        // pinned PID. Either way the owned host and job prove the exit below.
        var status=service!.Status();if(!WindowsFixedRuntimeService.StopUnderway(status.State))
        {
            if(status.Pid!=host.Pid)throw new UnauthorizedAccessException("RUNTIME_STOP_GENERATION_CHANGED");service.Stop();
        }
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var result=WaitForSingleObject(host.Handle,0);
            if(result==0&&job is not null&&JobAccounting.Read(job).TreeEmpty)return;
            if(result is not(0 or 258))throw new Win32Exception();await Task.Delay(20,cancellation);
        }
    }
    public async Task WaitForExitAsync(CancellationToken cancellation)
    {
        if(host is null)throw new IOException("RUNTIME_HOST_UNAVAILABLE");
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();var result=WaitForSingleObject(host.Handle,0);
            if(result==0)return;if(result!=258)throw new Win32Exception();host.RequireCurrent();await Task.Delay(100,cancellation);
        }
    }
    public void RequireQuiescence()
    {
        if(startRequested&&(host is null||WaitForSingleObject(host.Handle,0)!=0))throw new IOException("RUNTIME_NATIVE_ROOT_UNCONFIRMED");
        if(job is not null&&!job.IsInvalid&&!JobAccounting.Read(job).TreeEmpty)throw new IOException("RUNTIME_NATIVE_TREE_UNCONFIRMED");
    }
    public void Dispose()
    {
        if(disposed)return;RequireQuiescence();disposed=true;recoveryObservation?.Dispose();recoveryObservation=null;pipe?.Dispose();host?.Dispose();job?.Dispose();service?.Dispose();
    }
    private static NamedPipeServerStream CreatePipe(string installation,SecurityIdentifier authority,SecurityIdentifier runtime)
    {
        var descriptor=new RawSecurityDescriptor($"O:{authority.Value}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{authority.Value})(A;;0x00100083;;;{runtime.Value})");
        var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=pinned.AddrOfPinnedObject()};var memory=Marshal.AllocHGlobal(attributes.Length);
        try
        {
            Marshal.StructureToPtr(attributes,memory,false);var handle=CreateNamedPipeW(@"\\.\pipe\"+RuntimeAdmissionWire.Name(installation),0x40080003,0x8,1,32772,32772,15000,memory);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}
            try{return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);}catch{handle.Dispose();throw;}
        }
        finally{Marshal.FreeHGlobal(memory);pinned.Free();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [StructLayout(LayoutKind.Sequential)]private struct BasicLimit{public long ProcessTime,JobTime;public uint LimitFlags;public UIntPtr MinimumWorkingSet,MaximumWorkingSet;public uint ActiveProcessLimit;public UIntPtr Affinity;public uint PriorityClass,SchedulingClass;}
    [StructLayout(LayoutKind.Sequential)]private struct IoCounters{public ulong Read,Write,Other,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)]private struct ExtendedLimit{public BasicLimit Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcessMemory,PeakJobMemory;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateJobObjectW(IntPtr security,string? name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetInformationJobObject(SafeFileHandle job,int kind,IntPtr value,uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateNamedPipeW(string name,uint open,uint mode,uint instances,uint output,uint input,uint timeout,IntPtr security);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
}
