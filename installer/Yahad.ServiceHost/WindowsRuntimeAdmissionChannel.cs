using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Service;

internal sealed record RuntimeAdmissionMessage(int Format,string Kind,string Id,RuntimeAdmissionBinding? Admission=null,InstallationBinding? Binding=null,RuntimeHostIdentity? Host=null);
internal static class RuntimeAdmissionWire
{
    internal static string Name(string installation)
    {InstalledActivationResolver.Id(installation);return "Yahad.Authority."+installation+".admission";}
    internal static async Task WriteAsync(Stream stream,RuntimeAdmissionMessage message,CancellationToken cancellation)
    {
        InstalledActivationResolver.Id(message.Id);var bytes=JsonSerializer.SerializeToUtf8Bytes(message);
        if(bytes.Length is <2 or >32768)throw new IOException("RUNTIME_FRAME_BOUND");
        var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,cancellation);await stream.WriteAsync(bytes,cancellation);await stream.FlushAsync(cancellation);
    }
    internal static async Task<RuntimeAdmissionMessage> ReadAsync(Stream stream,CancellationToken cancellation)
    {
        var header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);var length=BinaryPrimitives.ReadInt32LittleEndian(header);
        if(length is <2 or >32768)throw new IOException("RUNTIME_FRAME_BOUND");var body=new byte[length];await stream.ReadExactlyAsync(body,cancellation);
        var message=NativeJson.Read<RuntimeAdmissionMessage>(body);InstalledActivationResolver.Id(message.Id);
        if(message.Format!=1)throw new IOException("RUNTIME_FRAME_VERSION");return message;
    }
    internal static async Task<RuntimeAdmissionMessage> ReadControlReplyAsync(Stream stream,RuntimeAdmissionBinding admission,string id,
        bool cancelling,Action requireCurrent,CancellationToken cancellation)
    {
        async Task<RuntimeAdmissionMessage> ReadBound()
        {
            var value=await ReadAsync(stream,cancellation);requireCurrent();
            if(value.Id!=id||value.Admission!=admission||value.Binding is not null||value.Host is not null)throw new UnauthorizedAccessException("RUNTIME_CONTROL_REPLY_BINDING");return value;
        }
        var reply=await ReadBound();
        // A timed-out prepare may reply immediately before cancellation. Drain
        // at most that ONE exact generation/fence reply, then demand the cancel
        // acknowledgement. Never discard a different ID or arbitrary frames.
        if(cancelling&&reply.Kind is "update-idle" or "update-busy")reply=await ReadBound();
        if(cancelling&&reply.Kind!="update-cancelled")throw new IOException("RUNTIME_CANCEL_REPLY");return reply;
    }
}

// Uses only fixed, compiled SCM names/accounts/images. No request selects a
// service, account, executable, process rights or path. No provisioning APIs.
internal sealed class WindowsFixedRuntimeService:IDisposable
{
    internal const string AuthorityName=InstalledUpdaterLayout.AuthorityServiceName;
    private readonly string account,image,command;private readonly ScmHandle manager,service;
    private WindowsFixedRuntimeService(string name,string account,string image,uint rights,string? command=null)
    {
        this.account=account;this.image=image;this.command=command??"\""+image+"\" --service";
        manager=new(OpenSCManagerW(null,null,1));if(manager.IsInvalid){manager.Dispose();throw new Win32Exception();}
        service=new(OpenServiceW(manager,name,rights));if(service.IsInvalid){service.Dispose();manager.Dispose();throw new Win32Exception();}
        try{RequireConfiguration();}catch{Dispose();throw;}
    }
    internal static WindowsFixedRuntimeService AuthorityRead()=>new(AuthorityName,@"NT SERVICE\"+AuthorityName,
        InstalledUpdaterLayout.AuthorityExecutable,5);
    internal static WindowsFixedRuntimeService UpdaterRead(uint pid,bool full)
    {
        // The pipe's observed PID only selects among compiled SCM roles. Every
        // candidate is configuration-bound; Pin then checks token/image/creation.
        WindowsFixedRuntimeService? selected=null;
        try
        {
            foreach(var actor in full?Enum.GetValues<UpdaterServiceActor>():[UpdaterServiceActor.Authority])
            {
                var candidate=new WindowsFixedRuntimeService(UpdaterServiceIdentity.Name(actor),@"NT SERVICE\"+UpdaterServiceIdentity.Name(actor),
                    UpdaterServiceIdentity.Image(actor),5,UpdaterServiceIdentity.Command(actor));
                try
                {
                    var status=candidate.Status();
                    if(pid!=0&&status.Pid==pid&&status.State is 2 or 4)
                    {if(selected is not null)throw new IOException("RUNTIME_AUTHORITY_AMBIGUOUS");selected=candidate;candidate=null!;}
                }
                finally{candidate?.Dispose();}
            }
            return selected??throw new UnauthorizedAccessException("RUNTIME_AUTHORITY_CHANGED");
        }
        catch{selected?.Dispose();throw;}
    }
    internal static WindowsFixedRuntimeService RuntimeControl()=>new(ServiceDefinition.Name,ServiceDefinition.Account,
        InstalledUpdaterLayout.RuntimeExecutable,0x35);
    internal void RequireConfiguration()
    {
        QueryServiceConfigW(service,IntPtr.Zero,0,out var size);if(size is <44 or >16384)throw new IOException("RUNTIME_SERVICE_CONFIG_BOUND");
        var memory=Marshal.AllocHGlobal((int)size);
        try
        {
            if(!QueryServiceConfigW(service,memory,size,out _))throw new Win32Exception();var value=Marshal.PtrToStructure<ServiceConfig>(memory);
            if(value.Type!=16||Marshal.PtrToStringUni(value.Account)!=account||Marshal.PtrToStringUni(value.Image)!=command)throw new UnauthorizedAccessException("RUNTIME_SERVICE_CONFIGURATION");
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal (uint State,uint Pid) Status()
    {if(!QueryServiceStatusEx(service,0,out var value,(uint)Marshal.SizeOf<ServiceStatus>(),out _))throw new Win32Exception();if(value.Type!=16)throw new UnauthorizedAccessException("RUNTIME_SERVICE_TYPE");return(value.State,value.Pid);}
    internal void Start()
    {RequireConfiguration();if(Status().State!=1)throw new IOException("RUNTIME_SERVICE_ALREADY_ACTIVE");if(!StartServiceW(service,0,IntPtr.Zero))throw new Win32Exception();}
    // Setup's removal/maintenance STOP reaches the Runtime concurrently with the
    // Authority's own. A STOP_PENDING Runtime accepts no controls, so the SCM
    // refuses the second STOP with 1061: the requested stop is then already
    // underway, not failed. Callers still observe host exit and an empty job.
    internal static bool StopUnderway(uint state)=>state is 1 or 3;
    internal static bool StopRefusalUnderway(int error,uint state)=>error==1062||error==1061&&StopUnderway(state);
    internal void Stop()
    {
        RequireConfiguration();if(StopUnderway(Status().State))return;
        if(!ControlService(service,1,out _))
        {var error=Marshal.GetLastWin32Error();if(!StopRefusalUnderway(error,Status().State))throw new Win32Exception(error);}
    }
    internal WindowsPinnedServiceProcess Pin(uint expectedPid,uint rights)
    {
        RequireConfiguration();var status=Status();if(expectedPid==0||status.Pid!=expectedPid||status.State is not(2 or 4))throw new UnauthorizedAccessException("RUNTIME_SERVICE_PID");
        var process=OpenProcess(rights,false,expectedPid);if(process.IsInvalid){process.Dispose();throw new Win32Exception();}
        try
        {
            var value=new WindowsPinnedServiceProcess(process,expectedPid,this);value.RequireAlive();
            var actual=new StringBuilder(32768);var length=(uint)actual.Capacity;
            if(!QueryFullProcessImageNameW(process,0,actual,ref length))throw new Win32Exception();
            if(!string.Equals(actual.ToString(),image,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("RUNTIME_SERVICE_IMAGE");
            if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
            using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
            {
                var expected=(SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
                if(identity.User!=expected)throw new UnauthorizedAccessException("RUNTIME_SERVICE_TOKEN");
            }
            value.RequireCurrent();return value;
        }
        catch{process.Dispose();throw;}
    }
    internal void RequirePid(uint pid)
    {RequireConfiguration();var value=Status();if(value.Pid!=pid||value.State is not(2 or 4))throw new UnauthorizedAccessException("RUNTIME_SERVICE_CHANGED");}
    public void Dispose(){service.Dispose();manager.Dispose();}
    internal sealed class WindowsPinnedServiceProcess:IDisposable
    {
        internal SafeProcessHandle Handle{get;}internal uint Pid{get;}internal long Created{get;}
        private readonly WindowsFixedRuntimeService service;
        internal WindowsPinnedServiceProcess(SafeProcessHandle handle,uint pid,WindowsFixedRuntimeService service)
        {Handle=handle;Pid=pid;this.service=service;if(!GetProcessTimes(handle,out var created,out _,out _,out _))throw new Win32Exception();Created=created;}
        internal void RequireAlive(){if(WaitForSingleObject(Handle,0)!=258)throw new UnauthorizedAccessException("RUNTIME_SERVICE_EXITED");}
        internal void RequireCurrent(){RequireAlive();service.RequirePid(Pid);if(!GetProcessTimes(Handle,out var created,out _,out _,out _)||created!=Created)throw new UnauthorizedAccessException("RUNTIME_SERVICE_GENERATION");}
        public void Dispose()=>Handle.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatus{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceConfig{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    private sealed class ScmHandle:SafeHandleZeroOrMinusOneIsInvalid{internal ScmHandle(IntPtr h):base(true){SetHandle(h);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(ScmHandle manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(ScmHandle service,int level,out ServiceStatus status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(ScmHandle service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool StartServiceW(ScmHandle service,uint count,IntPtr args);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool ControlService(ScmHandle service,uint control,out ServiceStatus status);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint rights,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long created,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder image,ref uint size);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint rights,out SafeAccessTokenHandle token);
}

// Compiled native client. Its caller supplies the real app-control forwarding and
// stop callback; no fixture success implementation or Program activation exists.
internal sealed class WindowsRuntimeAdmissionChannel(Func<string,CancellationToken,Task<bool>> prepare,
    Func<string,CancellationToken,Task> cancel,Action authorityLost,
    Func<string,CancellationToken,Task>? releaseEffects=null,bool full=false):IRuntimeAdmissionChannel,IRuntimePurposeAdmissionChannel,IDisposable
{
    private WindowsFixedRuntimeService? authority;private WindowsFixedRuntimeService.WindowsPinnedServiceProcess? server;
    private NamedPipeClientStream? stream;private RuntimeAdmissionBinding? admitted;
    private readonly CancellationTokenSource stopped=new();private Task? listener;private bool disposed;
    public string Purpose{get;private set;}="admit";
    public async Task<RuntimeAdmissionBinding> WaitForAdmissionAsync(InstallationBinding binding,RuntimeHostIdentity host,CancellationToken cancellation)
    {
        if(stream is not null||disposed)throw new IOException("RUNTIME_CHANNEL_REPLAY");
        var expected=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        ServiceIdentityPolicy.Require(new WindowsServiceTokenReader().Read(),expected.Value);
        var name=@"\\.\pipe\"+RuntimeAdmissionWire.Name(binding.InstallationId);
        SafePipeHandle handle;
        while(true)
        {
            cancellation.ThrowIfCancellationRequested();handle=CreateFileW(name,0x00100083,0,IntPtr.Zero,3,0x40110000,IntPtr.Zero);
            if(!handle.IsInvalid)break;var error=Marshal.GetLastWin32Error();handle.Dispose();if(error is not(2 or 231))throw new Win32Exception(error);await Task.Delay(50,cancellation);
        }
        try
        {
            if(!GetNamedPipeServerProcessId(handle,out var pid))throw new Win32Exception();authority=WindowsFixedRuntimeService.UpdaterRead(pid,full);server=authority.Pin(pid,0x00101000);
            stream=new NamedPipeClientStream(PipeDirection.InOut,true,true,handle);handle=null!;
            var id=Guid.NewGuid().ToString("N");await RuntimeAdmissionWire.WriteAsync(stream,new(1,"hello",id,Binding:binding,Host:host),cancellation);
            var message=await RuntimeAdmissionWire.ReadAsync(stream,cancellation);
            if(message.Kind is not("admit" or "probation")||message.Id!=id||message.Binding is not null||message.Host is not null||message.Admission is null)throw new UnauthorizedAccessException("RUNTIME_ADMISSION_REPLY");
            Purpose=message.Kind;
            message.Admission.Validate();admitted=message.Admission;RequireLaunch(admitted);return admitted;
        }
        catch{Dispose();throw;}
        finally{handle?.Dispose();}
    }
    public void RequireLaunch(RuntimeAdmissionBinding admission)
    {
        ObjectDisposedException.ThrowIf(disposed,this);
        if(stream is null||admitted!=admission||server is null||!GetNamedPipeServerProcessId(stream.SafePipeHandle,out var pid)||pid!=server.Pid)throw new UnauthorizedAccessException("RUNTIME_AUTHORITY_CHANGED");
        server.RequireCurrent();
    }
    public async Task ReportReadyAsync(RuntimeAdmissionBinding admission,CancellationToken cancellation)
    {
        RequireLaunch(admission);if(listener is not null)throw new IOException("RUNTIME_READY_REPLAY");
        await RuntimeAdmissionWire.WriteAsync(stream!,new(1,"ready",admission.Nonce,admission),cancellation);
        // Let ServiceController publish SCM RUNNING after its health method
        // returns. Authority acknowledges only after observing that actual state;
        // awaiting its acknowledgement here would deadlock those two steps.
        listener=ConfirmReadyAndListenAsync(admission);
    }
    private async Task ConfirmReadyAndListenAsync(RuntimeAdmissionBinding admission)
    {
        try
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var reply=await RuntimeAdmissionWire.ReadAsync(stream!,deadline.Token);
            if(reply.Kind!="ready-accepted"||reply.Id!=admission.Nonce||reply.Admission!=admission||reply.Binding is not null||reply.Host is not null)throw new UnauthorizedAccessException("RUNTIME_READY_REPLY");
            RequireLaunch(admission);await ListenAsync(admission);
        }
        catch{if(!stopped.IsCancellationRequested)authorityLost();}
    }
    private async Task ListenAsync(RuntimeAdmissionBinding admission)
    {
        try
        {
            while(true)
            {
                var message=await RuntimeAdmissionWire.ReadAsync(stream!,stopped.Token);RequireLaunch(admission);
                if(message.Admission!=admission||message.Binding is not null||message.Host is not null)throw new UnauthorizedAccessException("RUNTIME_CONTROL_BINDING");
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(stopped.Token);deadline.CancelAfter(TimeSpan.FromSeconds(15));
                var kind=message.Kind switch
                {
                    "prepare-update"=>await prepare(message.Id,deadline.Token)?"update-idle":"update-busy",
                    "cancel-update"=>await CancelAsync(message.Id,deadline.Token),
                    "release-effects" when Purpose=="probation"=>await ReleaseEffectsAsync(message.Id,deadline.Token),
                    _=>throw new IOException("RUNTIME_CONTROL_METHOD")
                };
                RequireLaunch(admission);await RuntimeAdmissionWire.WriteAsync(stream!,new(1,kind,message.Id,admission),deadline.Token);
            }
        }
        catch{if(!stopped.IsCancellationRequested)authorityLost();}
    }
    private async Task<string> CancelAsync(string id,CancellationToken cancellation){await cancel(id,cancellation);return "update-cancelled";}
    private async Task<string> ReleaseEffectsAsync(string id,CancellationToken cancellation)
    {await (releaseEffects??throw new IOException("PROBATION_RELEASE_UNCONFIGURED"))(id,cancellation);return "effects-released";}
    public void Dispose()
    {if(disposed)return;disposed=true;stopped.Cancel();stream?.Dispose();server?.Dispose();authority?.Dispose();}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
