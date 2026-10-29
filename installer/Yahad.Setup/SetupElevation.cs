using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record SetupWire(int Format,string Kind,SetupProgress? Progress,SetupResult? Result);
internal static class SetupElevation
{
    internal static async Task<SetupResult> RunAsync(bool uninstall,Action<SetupProgress> progress,CancellationToken cancellation)
    {
        var nonce=Guid.NewGuid().ToString("N");var name="Yahad.Setup."+nonce;using var identity=WindowsIdentity.GetCurrent();var sid=identity.User??throw new IOException("OWNER_IDENTITY");
        using var server=Server(name,sid.Value);var source=Environment.ProcessPath??throw new IOException("SETUP_IMAGE");
        var temporary=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var helper=Path.Combine(temporary,"Yahad.Setup.exe");File.Copy(source,helper,false);
        using var image=new FileStream(helper,FileMode.Open,FileAccess.Read,FileShare.Read);var sourceDigest=HashFile(source);if(Convert.ToHexStringLower(SHA256.HashData(image))!=sourceDigest)throw new IOException("ELEVATION_IMAGE_CHANGED");
        var start=new ProcessStartInfo(helper){UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=temporary};
        start.ArgumentList.Add(uninstall?"--elevated-uninstall":"--elevated-install");start.ArgumentList.Add(nonce);start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        using var child=Process.Start(start)??throw new IOException("ELEVATION_NOT_STARTED");_ =child.SafeHandle;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);deadline.CancelAfter(TimeSpan.FromMinutes(12));
        var connection=server.WaitForConnectionAsync(deadline.Token);
        var winner=await Task.WhenAny(connection,child.WaitForExitAsync(deadline.Token));if(winner!=connection){await winner;throw new IOException("ELEVATION_EXITED");}await connection;
        if(!GetNamedPipeClientProcessId(server.SafePipeHandle,out var peer)||peer!=(uint)child.Id||child.HasExited)throw new IOException("ELEVATION_PEER");
        await WriteAsync(server,new SetupWire(1,uninstall?"uninstall":"install",null,null),deadline.Token);
        while(true)
        {
            var frame=await ReadAsync(server,deadline.Token);
            if(frame.Format!=1)throw new IOException("SETUP_PROTOCOL");
            if(frame.Kind=="progress"&&frame.Progress is { } status){progress(status);continue;}
            if(frame.Kind=="result"&&frame.Result is { } result){await child.WaitForExitAsync(deadline.Token);return result;}
            throw new IOException("SETUP_PROTOCOL");
        }
    }
    internal static async Task<int> ElevatedAsync(bool uninstall,string nonce,int expectedUi)
    {
        NativeInstallFiles.RequireAdministrator();if(!Guid.TryParseExact(nonce,"N",out _)||expectedUi<1)throw new IOException("SETUP_ARGUMENTS");
        var pipeName=@"\\.\pipe\Yahad.Setup."+nonce;using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(12));
        using var handle=CreateFileW(pipeName,0x100083,0,IntPtr.Zero,3,0x40110000,IntPtr.Zero);if(handle.IsInvalid)throw new Win32Exception();
        if(!GetNamedPipeServerProcessId(handle,out var pid)||pid!=(uint)expectedUi)throw new IOException("SETUP_UI_PEER");
        using var parent=OpenProcess(0x101000,false,pid);if(parent.IsInvalid)throw new Win32Exception();
        var owner=Owner(parent,(int)pid);if(owner.ImageSha256!=HashFile(Environment.ProcessPath!))throw new IOException("SETUP_UI_IMAGE");
        using var stream=new NamedPipeClientStream(PipeDirection.InOut,true,true,handle);var request=await ReadAsync(stream,deadline.Token);
        if(request.Format!=1||request.Kind!=(uninstall?"uninstall":"install")||request.Progress is not null||request.Result is not null)throw new IOException("SETUP_COMMAND");
        SetupResult result;
        try
        {
            using var installLock=new SetupInstallLock();using var payload=InstallPackage.Embedded();using var host=new NativeSetupHost(payload);var gate=new object();
            void Progress(SetupProgress p){lock(gate)WriteAsync(stream,new SetupWire(1,"progress",p,null),deadline.Token).GetAwaiter().GetResult();}
            result=await new InstallFlow(host).RunAsync(owner,uninstall,Progress,deadline.Token);
        }
        catch(Exception failure)
        {result=new(false,Code(failure),DataPreserved:true);}
        await WriteAsync(stream,new SetupWire(1,"result",null,result),deadline.Token);return result.Ok?0:1;
    }
    internal static string Code(Exception error)=>error switch
    {
        Win32Exception w when w.NativeErrorCode==1223=>"UAC_CANCELLED",
        Win32Exception w when w.NativeErrorCode==112=>"DISK_SPACE",
        Win32Exception w when w.NativeErrorCode==32=>"FILE_LOCKED",
        UnauthorizedAccessException=>"ACCESS_DENIED",
        OperationCanceledException=>"INSTALL_TIMEOUT",
        IOException io when KnownCodes.Contains(io.Message)=>io.Message,
        _=>"INSTALL_FAILED"
    };
    private static readonly HashSet<string> KnownCodes=new(StringComparer.Ordinal){"PACKAGE_NOT_EMBEDDED","PACKAGE_HASH","PACKAGE_MEMBER_HASH","PAYLOAD_VERSION","PAYLOAD_SCHEMA","INSTALL_COLLISION","EXISTING_DATA_PRESERVED","INSTALL_REPAIR_REQUIRED","INSTALL_OWNER_CHANGED","INSTALL_SERVICE_IDENTITY","INSTALL_TARGET_CHANGED","INSTALL_BOOTSTRAP_CHANGED","DISK_SPACE","NO_LOCAL_PORT","SERVICE_START_FAILED","SERVER_NOT_READY","SERVICE_NOT_OWNED","SERVICE_IMAGE_CHANGED","UNINSTALL_UNOWNED_FILE_PRESERVED","INSTALL_STATE_INVALID","ELEVATION_EXITED","ELEVATION_PEER","SETUP_UI_PEER","SETUP_UI_IMAGE","ADMIN_REQUIRED","INSTALL_BUSY"};
    internal static string HashFile(string path){using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);return Convert.ToHexStringLower(SHA256.HashData(file));}
    internal static string RelocateForUninstall()
    {var folder=SafeDirectory.CreatePrivateSession(Path.GetTempPath());var target=Path.Combine(folder,"Yahad.Setup.exe");File.Copy(Environment.ProcessPath!,target,false);if(HashFile(target)!=HashFile(Environment.ProcessPath!))throw new IOException("SETUP_COPY_CHANGED");return target;}
    private static OwnerProcess Owner(SafeProcessHandle process,int pid)
    {
        if(WaitForSingleObject(process,0)!=258||!GetProcessTimes(process,out var created,out _,out _,out _))throw new IOException("SETUP_UI_EXITED");
        var image=new StringBuilder(32768);var length=(uint)image.Capacity;if(!QueryFullProcessImageNameW(process,0,image,ref length))throw new Win32Exception();SafeDirectory.NoReparseAncestors(image.ToString());
        if(!OpenProcessToken(process,8,out var token))throw new Win32Exception();
        using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
        {if(identity.User is null)throw new IOException("SETUP_UI_OWNER");if(!ProcessIdToSessionId((uint)pid,out var session)||session!=(uint)Process.GetCurrentProcess().SessionId)throw new IOException("SETUP_UI_SESSION");return new(identity.User.Value,pid,created,HashFile(image.ToString()));}
    }
    private static NamedPipeServerStream Server(string name,string owner)
    {
        var acl=new RawSecurityDescriptor($"O:{owner}D:P(A;;FA;;;{owner})(A;;0x100083;;;SY)(A;;0x100083;;;BA)");var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pin.AddrOfPinnedObject()};var memory=Marshal.AllocHGlobal(attributes.Length);SafePipeHandle handle;
        try{Marshal.StructureToPtr(attributes,memory,false);handle=CreateNamedPipeW(@"\\.\pipe\"+name,0x40080003,8,1,32772,32772,15000,memory);if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}}
        finally{Marshal.FreeHGlobal(memory);pin.Free();}
        return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);
    }
    // Progress can synchronously wait here on the elevated WPF dispatcher.
    // Framing I/O must never capture that blocked caller's UI context.
    private static async Task WriteAsync(Stream stream,SetupWire value,CancellationToken cancellation)
    {var bytes=JsonSerializer.SerializeToUtf8Bytes(value);if(bytes.Length>32768)throw new IOException("SETUP_FRAME_BOUND");var header=BitConverter.GetBytes(bytes.Length);await stream.WriteAsync(header,cancellation).ConfigureAwait(false);await stream.WriteAsync(bytes,cancellation).ConfigureAwait(false);await stream.FlushAsync(cancellation).ConfigureAwait(false);}
    private static async Task<SetupWire> ReadAsync(Stream stream,CancellationToken cancellation)
    {var header=new byte[4];await stream.ReadExactlyAsync(header,cancellation);var n=BitConverter.ToInt32(header);if(n is <2 or >32768)throw new IOException("SETUP_FRAME_BOUND");var bytes=new byte[n];await stream.ReadExactlyAsync(bytes,cancellation);return NativeJson.Read<SetupWire>(bytes);}
    [StructLayout(LayoutKind.Sequential)]private struct Attributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateNamedPipeW(string name,uint open,uint mode,uint instances,uint output,uint input,uint timeout,IntPtr attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafePipeHandle CreateFileW(string name,uint access,uint sharing,IntPtr attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern SafeProcessHandle OpenProcess(uint rights,bool inherit,uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeProcessHandle process,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool GetProcessTimes(SafeProcessHandle process,out long creation,out long exit,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool QueryFullProcessImageNameW(SafeProcessHandle process,uint flags,StringBuilder image,ref uint length);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool ProcessIdToSessionId(uint pid,out uint session);
    [DllImport("advapi32.dll",SetLastError=true)]private static extern bool OpenProcessToken(SafeProcessHandle process,uint rights,out SafeAccessTokenHandle token);
}
