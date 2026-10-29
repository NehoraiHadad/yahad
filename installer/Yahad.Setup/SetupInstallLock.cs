using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Setup;

// Only the elevated installer owns this exact fixed namespace. The application
// service does not gain registration/update authority through it.
internal sealed class SetupInstallLock:IDisposable
{
    private readonly SafeWaitHandle handle;
    internal SetupInstallLock()
    {
        var acl=new RawSecurityDescriptor("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)");var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pin.AddrOfPinnedObject()};
        try{handle=CreateSemaphoreW(ref attributes,1,1,@"Global\Yahad.FirstInstall.Setup");if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}}
        finally{pin.Free();}
        var observed=WaitForSingleObject(handle,0);if(observed!=0){handle.Dispose();throw new IOException("INSTALL_BUSY");}
    }
    public void Dispose(){if(!handle.IsClosed&&!handle.IsInvalid){ReleaseSemaphore(handle,1,IntPtr.Zero);handle.Dispose();}}
    [StructLayout(LayoutKind.Sequential)]private struct Attributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeWaitHandle CreateSemaphoreW(ref Attributes attributes,int initial,int maximum,string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeWaitHandle mutex,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool ReleaseSemaphore(SafeWaitHandle mutex,int count,IntPtr previous);
}
