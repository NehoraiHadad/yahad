using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Setup;

// The one global Setup mutex: install, removal, upgrade and repair all hold it.
// Windows releases a mutex whose owner died (WAIT_ABANDONED), so a killed Setup
// never blocks the repair that follows it. Ownership belongs to a thread and the
// operation crosses threads, so a dedicated thread owns it from wait to release.
internal sealed class SetupInstallLock:IDisposable
{
    private const uint Signaled=0,Abandoned=0x80;
    private readonly ManualResetEventSlim release=new(false);private readonly Thread holder;
    internal static bool Acquired(uint wait)=>wait is Signaled or Abandoned;
    internal SetupInstallLock(TimeSpan? wait=null)
    {
        Exception? failure=null;using var settled=new ManualResetEventSlim(false);
        holder=new Thread(()=>
        {
            SafeWaitHandle? handle=null;var owned=false;
            try
            {
                handle=Create();owned=Acquired(WaitForSingleObject(handle,(uint)(wait??TimeSpan.Zero).TotalMilliseconds));
                if(!owned)throw new IOException("INSTALL_BUSY");
                settled.Set();release.Wait();
            }
            catch(Exception error){failure=error;settled.Set();}
            finally{if(owned)ReleaseMutex(handle!);handle?.Dispose();}
        }){IsBackground=true,Name="Yahad Setup lock"};
        holder.Start();settled.Wait();
        if(failure is not null){holder.Join();release.Dispose();throw failure;}
    }
    private static SafeWaitHandle Create()
    {
        var acl=new RawSecurityDescriptor("O:BAD:P(A;;GA;;;SY)(A;;GA;;;BA)");var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pin.AddrOfPinnedObject()};
        try
        {
            var handle=CreateMutexW(ref attributes,false,@"Global\Yahad.FirstInstall.Setup");
            if(handle.IsInvalid){var error=Marshal.GetLastPInvokeError();handle.Dispose();throw new Win32Exception(error);}
            return handle;
        }
        finally{pin.Free();}
    }
    public void Dispose(){if(release.IsSet)return;release.Set();holder.Join();release.Dispose();}
    [StructLayout(LayoutKind.Sequential)]private struct Attributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeWaitHandle CreateMutexW(ref Attributes attributes,bool initialOwner,string name);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern uint WaitForSingleObject(SafeWaitHandle mutex,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool ReleaseMutex(SafeWaitHandle mutex);
}
