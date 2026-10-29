using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

internal static class LocalControlPipe
{
    public static NamedPipeServerStream Create(string name)
    {
        var sid=WindowsIdentity.GetCurrent().User??throw new IOException("IPC_IDENTITY");
        if(!ConvertStringSecurityDescriptorToSecurityDescriptorW($"D:P(A;;GRGW;;;{sid.Value})",1,out var descriptor,out _))throw new Win32Exception();
        try
        {
            var attributes=new SecurityAttributes{Length=Marshal.SizeOf<SecurityAttributes>(),Descriptor=descriptor};
            // Duplex + overlapped + FIRST_PIPE_INSTANCE; explicitly reject remote
            // clients rather than relying on SMB/firewall or inherited defaults.
            var handle=CreateNamedPipeW(@"\\.\pipe\"+name,3|0x40000000|0x00080000,0x8,1,1024,1024,0,ref attributes);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}
            try{return new NamedPipeServerStream(PipeDirection.InOut,true,false,handle);}
            catch{handle.Dispose();throw;}
        }
        finally{LocalFree(descriptor);}
    }
    [StructLayout(LayoutKind.Sequential)]private struct SecurityAttributes
    {public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string text,uint revision,out IntPtr descriptor,out uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    private static extern SafePipeHandle CreateNamedPipeW(string name,uint openMode,uint pipeMode,uint instances,uint outputSize,uint inputSize,uint timeout,ref SecurityAttributes attributes);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
}
