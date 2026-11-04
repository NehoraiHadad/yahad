using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
namespace Yahad.Install;

// Exactly two ACLs exist, each set once on the root of a tree, and everything
// below inherits it. Nothing is created with a descriptor of its own.
internal static class NativeInstallFiles
{
    internal static void RequireAdministrator()
    {using var user=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("ADMIN_REQUIRED");}
    // Administrators write and the service only reads: binaries and the records
    // elevated code later trusts, so the service can never shape what they consume.
    // The binary tree alone also names the home-network proxy, which runs from it.
    internal static string InstallAcl(params string[] serviceSids)=>Acl("0x1200a9",serviceSids);
    // The service's own data, which it may modify. Users have no access to either.
    // The service owns what it creates there, and an owner may rewrite the ACL unless an
    // OWNER RIGHTS entry says otherwise, so one states what an owner gets here.
    internal static string DataAcl(string serviceSid)=>Acl("0x1301bf",serviceSid)+"(A;OICI;0x1301bf;;;OW)";
    private static string Acl(string rights,params string[] serviceSids)=>"O:BAD:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"+string.Concat(serviceSids.Select(sid=>$"(A;OICI;{rights};;;{sid})"));
    // Without an ACL the directory inherits from its parent. It never already exists.
    internal static void NewDirectory(string path,string? acl=null)
    {
        var bytes=acl is null?null:Bytes(acl);var pin=bytes is null?default:GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=bytes is null?IntPtr.Zero:pin.AddrOfPinnedObject()};
        try{if(!CreateDirectoryW(Extended(path),ref attributes))throw new Win32Exception();}finally{if(bytes is not null)pin.Free();}
    }
    internal static byte[] ReadRecord(string path,int maximum=16777216)
    {using var file=WindowsFileBoundary.OpenRead(path);return file.Small(maximum);}
    internal static FileStream CreateNewFile(string path)
    {
        var handle=WindowsFileBoundary.Native.CreateFileW(Extended(path),0x40000000,0,IntPtr.Zero,1,0x80200080,IntPtr.Zero);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}
        return new FileStream(handle,FileAccess.Write,1048576,false);
    }
    internal static void EnsureDirectoryUnder(string root,string path)
    {
        var full=Path.GetFullPath(path);var parent=Path.GetFullPath(root);
        if(full!=parent&&!full.StartsWith(parent+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
        var relative=Path.GetRelativePath(parent,full);var cursor=parent;
        if(relative==".")return;
        foreach(var component in relative.Split(Path.DirectorySeparatorChar))
        {cursor=Path.Combine(cursor,component);if(!Directory.Exists(cursor))NewDirectory(cursor);}
    }
    internal static void WriteNew(string path,byte[] bytes)
    {using var file=CreateNewFile(path);file.Write(bytes);file.Flush(true);}
    internal static void PublishNew(string path,byte[] bytes)
    {
        var partial=path+".partial-"+Guid.NewGuid().ToString("N");WriteNew(partial,bytes);
        CommitNew(partial,path);
    }
    // No replacement: a competing canonical file is never overwritten, so a
    // present name is always a whole, previously committed file.
    internal static void CommitNew(string partial,string path){if(!MoveFileExW(Extended(partial),Extended(path),8))throw new Win32Exception();}
    // Both branches publish through a partial, so an interruption never leaves
    // a truncated record under the canonical name.
    internal static void Publish(string path,byte[] bytes){if(File.Exists(path))Replace(path,bytes);else PublishNew(path,bytes);}
    internal static void Replace(string path,byte[] bytes)
    {
        var partial=path+".partial-"+Guid.NewGuid().ToString("N");WriteNew(partial,bytes);
        if(!MoveFileExW(Extended(partial),Extended(path),9))
        {
            var error=Marshal.GetLastPInvokeError();
            try{File.Delete(partial);}catch{}
            throw new Win32Exception(error);
        }
    }
    // Installed trees nest digests and preserved copies past MAX_PATH; the raw
    // Win32 calls below need the extended form that System.IO adds itself.
    internal static string Extended(string path)=>@"\\?\"+Path.GetFullPath(path);
    internal static byte[] Bytes(string descriptor){var acl=new RawSecurityDescriptor(descriptor);var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);return bytes;}
    [StructLayout(LayoutKind.Sequential)]internal struct Attributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateDirectoryW(string path,ref Attributes attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string old,string target,uint flags);
}
