using System.Buffers.Binary;
using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal static class WindowsAuthorityEndpoint
{
    internal static string Name(string installation,AuthorityEndpointRole role)
    {InstalledActivationResolver.Id(installation);return "Yahad.Authority."+installation+"."+(role==AuthorityEndpointRole.Owner?"owner":"runtime");}
    // Creation is compiled but never invoked by either current Program or tests.
    internal static async Task ServeOnceAsync(InstalledAuthorityComposition composition,AuthorityEndpointRole role,CancellationToken cancellation)
    {
        var authority=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledAuthorityReader.AuthorityService).Translate(typeof(SecurityIdentifier));
        var runtime=(SecurityIdentifier)new NTAccount(ServiceDefinition.Account).Translate(typeof(SecurityIdentifier));
        ServiceIdentityPolicy.Require(new WindowsServiceTokenReader().Read(),authority.Value);
        var client=role==AuthorityEndpointRole.Owner?composition.Anchor.OwnerSid:runtime.Value;
        var descriptor=new RawSecurityDescriptor($"O:{authority.Value}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;FA;;;{authority.Value})(A;;0x00100083;;;{client})");
        var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var security=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=pin.AddrOfPinnedObject()};var attributes=Marshal.AllocHGlobal(security.Length);
        SafePipeHandle handle;
        try{Marshal.StructureToPtr(security,attributes,false);handle=CreateNamedPipeW(@"\\.\pipe\"+Name(composition.Anchor.InstallationId,role),0x40080003,0x8,1,32772,32772,15000,attributes);if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}}
        finally{Marshal.FreeHGlobal(attributes);pin.Free();}
        using var stream=new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
        await stream.WaitForConnectionAsync(cancellation);
        await AuthorityEndpointConnection.ServeNativeConnectedAsync(stream,handle,composition.Router,role,cancellation);
    }
    internal static async Task<EndpointReply> SendOwnerAsync(string installation,AuthorityFrame frame,CancellationToken cancellation)
    {
        var name=@"\\.\pipe\"+Name(installation,AuthorityEndpointRole.Owner);
        // Exact individual data rights; identification SQOS, never delegation.
        using var handle=CreateFileW(name,0x00100083,0,IntPtr.Zero,3,0x40110000,IntPtr.Zero);
        if(handle.IsInvalid)throw new Win32Exception();
        if(!GetNamedPipeServerProcessId(handle,out var pid)||!WindowsAuthorityServiceIdentity.IsCurrentServer(pid))throw new UnauthorizedAccessException("OWNER_SERVER_IDENTITY");
        using var server=WindowsAuthorityServiceIdentity.Pin(pid);
        using var stream=new NamedPipeClientStream(PipeDirection.InOut,true,true,handle);
        var bytes=JsonSerializer.SerializeToUtf8Bytes(frame);if(bytes.Length>32768)throw new IOException("OWNER_FRAME_BOUND");var header=new byte[4];BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await stream.WriteAsync(header,deadline.Token);await stream.WriteAsync(bytes,deadline.Token);await stream.FlushAsync(deadline.Token);
        await stream.ReadExactlyAsync(header,deadline.Token);var length=BinaryPrimitives.ReadInt32LittleEndian(header);if(length is <2 or >32768)throw new IOException("OWNER_REPLY_BOUND");var reply=new byte[length];await stream.ReadExactlyAsync(reply,deadline.Token);
        if(!GetNamedPipeServerProcessId(handle,out var after)||after!=pid||!WindowsAuthorityServiceIdentity.IsCurrentServer(pid))throw new UnauthorizedAccessException("OWNER_SERVER_CHANGED");
        WindowsAuthorityServiceIdentity.RequireAlive(server);
        var value=NativeJson.Read<EndpointReply>(reply);if(value.Format!=1||value.Id!=frame.Id)throw new IOException("OWNER_REPLY_BINDING");return value;
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateNamedPipeW(string name,uint open,uint mode,uint instances,uint output,uint input,uint timeout,IntPtr security);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateFileW(string name,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
}
internal static class WindowsAuthorityServiceIdentity
{
    // Client authentication uses trusted SCM account/config/PID, not a claimed SID
    // in the wire response. Read-only owner service rights must be provisioned.
    internal static bool IsCurrentServer(uint expectedPid)
    {
        using var manager=new ServiceHandle(OpenSCManagerW(null,null,1));if(manager.IsInvalid)throw new Win32Exception();
        using var service=new ServiceHandle(OpenServiceW(manager,InstalledAuthorityReader.AuthorityService,5));if(service.IsInvalid)throw new Win32Exception();
        if(!QueryServiceStatusEx(service,0,out var status,(uint)Marshal.SizeOf<ServiceStatus>(),out _))throw new Win32Exception();
        if(status.Pid!=expectedPid||status.State is not(2 or 4)||status.Type!=16)return false;
        QueryServiceConfigW(service,IntPtr.Zero,0,out var size);if(size is <44 or >16384)throw new IOException("OWNER_SERVICE_CONFIG_BOUND");var memory=Marshal.AllocHGlobal((int)size);
        try
        {
            if(!QueryServiceConfigW(service,memory,size,out _))throw new Win32Exception();var config=Marshal.PtrToStructure<ServiceConfig>(memory);
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ServiceDefinition.Name,"bootstrap","authority","Yahad.Authority.exe");
            return config.Type==16&&Marshal.PtrToStringUni(config.Account)==@"NT SERVICE\Yahad.Acceptance.Authority"&&Marshal.PtrToStringUni(config.Image)=="\""+expected+"\" --service";
        }
        finally{Marshal.FreeHGlobal(memory);}
    }
    internal static SafeProcessHandle Pin(uint pid)
    {
        var process=OpenProcess(0x00101000,false,pid);if(process.IsInvalid){process.Dispose();throw new Win32Exception();}
        try
        {
            RequireAlive(process);var image=new System.Text.StringBuilder(32768);var size=(uint)image.Capacity;
            if(!QueryFullProcessImageNameW(process,0,image,ref size))throw new Win32Exception();
            var expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),ServiceDefinition.Name,"bootstrap","authority","Yahad.Authority.exe");
            if(!string.Equals(image.ToString(),expected,StringComparison.OrdinalIgnoreCase))throw new UnauthorizedAccessException("OWNER_SERVER_IMAGE");
            if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
            using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
            {
                var service=(SecurityIdentifier)new NTAccount("NT SERVICE",InstalledAuthorityReader.AuthorityService).Translate(typeof(SecurityIdentifier));
                if(identity.User!=service)throw new UnauthorizedAccessException("OWNER_SERVER_TOKEN");
            }
            if(!IsCurrentServer(pid))throw new UnauthorizedAccessException("OWNER_SERVER_CHANGED");return process;
        }
        catch{process.Dispose();throw;}
    }
    internal static void RequireAlive(SafeProcessHandle process)
    {if(WaitForSingleObject(process,0)!=258)throw new UnauthorizedAccessException("OWNER_SERVER_EXITED");}
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,System.Text.StringBuilder image,ref uint size);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint access,out SafeAccessTokenHandle token);
    [StructLayout(LayoutKind.Sequential)]private struct ServiceStatus{public uint Type,State,Accepted,Win32,Specific,Checkpoint,WaitHint,Pid,Flags;}
    [StructLayout(LayoutKind.Sequential)]private struct ServiceConfig{public uint Type,Start,Error;public IntPtr Image,Group;public uint Tag;public IntPtr Dependencies,Account,Display;}
    private sealed class ServiceHandle:SafeHandleZeroOrMinusOneIsInvalid{internal ServiceHandle(IntPtr h):base(true){SetHandle(h);}protected override bool ReleaseHandle()=>CloseServiceHandle(handle);}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint rights);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern IntPtr OpenServiceW(ServiceHandle manager,string name,uint rights);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool QueryServiceStatusEx(ServiceHandle service,int level,out ServiceStatus status,uint size,out uint needed);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryServiceConfigW(ServiceHandle service,IntPtr config,uint size,out uint needed);
    [DllImport("advapi32.dll")]private static extern bool CloseServiceHandle(IntPtr service);
}
