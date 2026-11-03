using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
namespace Yahad.Setup;

internal static class NativeInstallFiles
{
    private const string Admin="S-1-5-32-544";
    internal static void RequireAdministrator()
    {using var user=WindowsIdentity.GetCurrent();if(!new WindowsPrincipal(user).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("ADMIN_REQUIRED");}
    internal static string Descriptor(string? serviceSid=null,bool mutable=false,string? ownerSid=null)=>
        $"O:{Admin}D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)"+(serviceSid is null?"":$"(A;OICI;{(mutable?"0x1301bf":"0x1200a9")};;;{serviceSid})")+(ownerSid is null?"":$"(A;OICI;0x1200a9;;;{ownerSid})");
    internal static void NewDirectory(string path,string descriptor)
    {
        SafeDirectory.NoReparseAncestors(path);var bytes=Bytes(descriptor);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pin.AddrOfPinnedObject()};
        try{if(!CreateDirectoryW(Extended(path),ref attributes))throw new Win32Exception();}finally{pin.Free();}
    }
    internal static void SetOwnedFile(string path,string descriptor)
    {SafeDirectory.NoReparseAncestors(path);var acl=new FileSecurity();acl.SetSecurityDescriptorSddlForm(descriptor.Replace("OICI",""),AccessControlSections.Owner|AccessControlSections.Access);new FileInfo(path).SetAccessControl(acl);}
    internal static byte[] ReadOwned(string path,string serviceSid)
    {using var file=WindowsFileBoundary.OpenBootstrapForInstaller(path,new SecurityIdentifier(serviceSid));return file.Small(16777216);}
    internal static FileStream CreateNewFile(string path)
    {
        SafeDirectory.NoReparseAncestors(path);var parent=new DirectoryInfo(Path.GetDirectoryName(path)!).GetAccessControl();
        return CreateNewFile(path,"O:BA"+parent.GetSecurityDescriptorSddlForm(AccessControlSections.Access));
    }
    internal static FileStream CreateNewFile(string path,string descriptor)
    {
        SafeDirectory.NoReparseAncestors(path);var bytes=Bytes(descriptor);var pin=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pin.AddrOfPinnedObject()};
        try{var handle=CreateFileW(Extended(path),0x40020000,0,ref attributes,1,0x80200080,IntPtr.Zero);if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}return new FileStream(handle,FileAccess.Write,1048576,false);}finally{pin.Free();}
    }
    internal static void EnsureStageDirectory(string staging,string path,string descriptor)
    {
        var full=Path.GetFullPath(path);var root=Path.GetFullPath(staging);
        if(full!=root&&!full.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("PACKAGE_PATH");
        var relative=Path.GetRelativePath(root,full);var cursor=root;
        if(relative==".")return;
        foreach(var component in relative.Split(Path.DirectorySeparatorChar))
        {cursor=Path.Combine(cursor,component);if(!Directory.Exists(cursor))NewDirectory(cursor,descriptor);else SafeDirectory.NoReparseAncestors(cursor);}
    }
    internal static void WriteNew(string path,byte[] bytes,string? descriptor=null)
    {using var file=descriptor is null?CreateNewFile(path):CreateNewFile(path,descriptor);file.Write(bytes);file.Flush(true);}
    internal static void PublishNewProtectedFile(string path,byte[] bytes,string descriptor,Action? beforeCommit=null)
    {
        var partial=path+".partial-"+Guid.NewGuid().ToString("N");WriteNew(partial,bytes,descriptor);
        beforeCommit?.Invoke(); // Internal synthetic crash cut; never supplied by product input.
        CommitNew(partial,path);
    }
    // No replacement: a competing canonical file is never overwritten, so a
    // present name is always a whole, previously committed file.
    internal static void CommitNew(string partial,string path){if(!MoveFileExW(Extended(partial),Extended(path),8))throw new Win32Exception();}
    internal static string FileDescriptor(string? serviceSid=null,bool mutable=false)=>Descriptor(serviceSid,mutable).Replace("OICI","");
    // Control records beside Program Files must be protected at creation,
    // including the replacement partial. Never inherit that parent's Modify
    // ACL and depend on a later ownership write to make the record private.
    internal static void PublishProtectedFile(string path,byte[] bytes,string descriptor)
    {
        ArgumentException.ThrowIfNullOrEmpty(descriptor);descriptor=descriptor.Replace("OICI","");var acl=new RawSecurityDescriptor(descriptor);
        if(acl.Owner is null||acl.DiscretionaryAcl is null||(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new IOException("INSTALL_METADATA_ACCESS");
        // Both branches publish through a partial, so an interruption never
        // leaves a truncated record under the canonical name.
        if(File.Exists(path))ReplaceOwned(path,bytes,descriptor);else PublishNewProtectedFile(path,bytes,descriptor);
    }
    internal static void ReplaceOwned(string path,byte[] bytes,string? descriptor=null)
    {
        var partial=path+".partial-"+Guid.NewGuid().ToString("N");WriteNew(partial,bytes,descriptor);
        if(!MoveFileExW(Extended(partial),Extended(path),9))
        {
            var error=Marshal.GetLastPInvokeError();
            if(descriptor is not null){try{File.Delete(partial);}catch{}}
            throw new Win32Exception(error);
        }
    }
    // Installed trees nest digests and preserved copies past MAX_PATH; the raw
    // Win32 calls below need the extended form that System.IO adds itself.
    internal static string Extended(string path)=>@"\\?\"+Path.GetFullPath(path);
    private static byte[] Bytes(string descriptor){var acl=new RawSecurityDescriptor(descriptor);var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);return bytes;}
    [StructLayout(LayoutKind.Sequential)]private struct Attributes{public int Length;public IntPtr Descriptor;public int Inherit;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,ref Attributes attributes,uint creation,uint flags,IntPtr template);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool CreateDirectoryW(string path,ref Attributes attributes);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string old,string target,uint flags);
}
