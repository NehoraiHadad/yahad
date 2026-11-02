using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;

// Test-only ownership of one freshly created directory. This is not an orphan
// collector: no caller-supplied existing root, name sweep or process discovery.
internal sealed class OwnedFixtureDirectory
{
    private const string Marker=".yahad-fixture";
    private readonly Identity originalRoot,originalMarker;
    private readonly SecurityIdentifier owner;
    private bool cleaned,markerRemoved;
    internal string Root{get;}
    private OwnedFixtureDirectory(string root)
    {
        Root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));
        owner=WindowsIdentity.GetCurrent().User??throw new IOException("FIXTURE_OWNER_MISSING");
        SafeDirectory.ValidateSession(Root);
        using var directory=Open(Root,true,false);originalRoot=Observe(directory,true);
        using var marker=Open(Path.Combine(Root,Marker),false,false,true);originalMarker=Observe(marker,false);
        RequireMarker(marker);
    }
    internal static OwnedFixtureDirectory Create(string parent)
    {
        var root=SafeDirectory.CreatePrivateSession(Path.GetFullPath(parent));
        try{return new(root);}
        catch(Exception error){throw new IOException("FIXTURE_OWNERSHIP_CAPTURE_FAILED: "+root,error);}
    }

    // The caller owns the child/controller cleanup proof. Refusing or throwing
    // is visible to its runner; neither means a passed test or a deleted root.
    internal void Cleanup(Func<bool> confirmedQuiescence)
    {
        ArgumentNullException.ThrowIfNull(confirmedQuiescence);
        if(cleaned)return;
        void RequireQuiescence(){if(!confirmedQuiescence())throw new IOException("FIXTURE_QUIESCENCE_UNCONFIRMED");}
        RequireQuiescence();
        var ancestors=new List<SafeFileHandle>();
        try
        {
            var current=Path.GetPathRoot(Root)!;
            ancestors.Add(Open(current,true,false));
            foreach(var part in Path.GetRelativePath(current,Path.GetDirectoryName(Root)!).Split(Path.DirectorySeparatorChar))
            {if(part==".")continue;current=Path.Combine(current,part);ancestors.Add(Open(current,true,false));}
            using(var directory=Open(Root,true,true))
            {
                if(Observe(directory,true)!=originalRoot)throw new IOException("FIXTURE_ROOT_CHANGED");
                RequirePrivate(directory,true);
                if(!markerRemoved)
                {
                    using var marker=Open(Path.Combine(Root,Marker),false,false,true);
                    if(Observe(marker,false)!=originalMarker)throw new IOException("FIXTURE_MARKER_CHANGED");
                    RequirePrivate(marker,false);RequireMarker(marker);
                }
                else if(Directory.EnumerateFileSystemEntries(Root).Any())throw new IOException("FIXTURE_ROOT_CHANGED");
                var count=0;
                void RemoveContents(string path,int depth)
                {
                    if(depth>128)throw new IOException("FIXTURE_TREE_LIMIT");
                    foreach(var entry in Directory.EnumerateFileSystemEntries(path))
                    {
                        if(path==Root&&Path.GetFileName(entry)==Marker)continue;
                        RequireQuiescence();if(++count>200000)throw new IOException("FIXTURE_TREE_LIMIT");
                        var child=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(entry));
                        if(!child.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("FIXTURE_TREE_SCOPE");
                        var isDirectory=(File.GetAttributes(child)&FileAttributes.Directory)!=0;
                        using var held=Open(child,isDirectory,true);RequirePrivate(held,false);
                        if(isDirectory)RemoveContents(child,depth+1);
                        RequireQuiescence();Delete(held);
                    }
                }
                RemoveContents(Root,0);
                if(!markerRemoved)
                {
                    using(var marker=Open(Path.Combine(Root,Marker),false,true,true))
                    {
                        if(Observe(marker,false)!=originalMarker)throw new IOException("FIXTURE_MARKER_CHANGED");
                        RequirePrivate(marker,false);RequireMarker(marker);RequireQuiescence();Delete(marker);
                    }
                    markerRemoved=true;
                }
                RequireQuiescence();Delete(directory);
            }
        }
        finally{for(var i=ancestors.Count-1;i>=0;i--)ancestors[i].Dispose();}
        try{_=File.GetAttributes(Root);throw new IOException("FIXTURE_ROOT_NOT_REMOVED");}
        catch(FileNotFoundException){}
        catch(DirectoryNotFoundException){}
        cleaned=true;
    }
    private void RequirePrivate(SafeFileHandle handle,bool root)
    {
        var error=GetSecurityInfo(handle,1,5,out _,out _,out _,out _,out var descriptor);
        if(error!=0)throw new Win32Exception((int)error);
        try
        {
            var size=GetSecurityDescriptorLength(descriptor);if(size is <20 or >65536)throw new IOException("FIXTURE_SECURITY_BOUND");
            var bytes=new byte[size];Marshal.Copy(descriptor,bytes,0,bytes.Length);var acl=new RawSecurityDescriptor(bytes,0);
            WindowsFileBoundary.ValidateAcl(acl,owner,false);
            if(root&&(acl.Owner!=owner||(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0))throw new IOException("FIXTURE_ROOT_OWNERSHIP");
        }
        finally{_=LocalFree(descriptor);}
    }
    private static void RequireMarker(SafeFileHandle handle)
    {
        var expected=Encoding.UTF8.GetBytes("fixture-v1");
        if(!GetFileSizeEx(handle,out var length))throw new Win32Exception();
        if(length!=expected.Length)throw new IOException("FIXTURE_MARKER_CHANGED");
        var bytes=new byte[expected.Length];if(RandomAccess.Read(handle,bytes,0)!=bytes.Length||!bytes.AsSpan().SequenceEqual(expected))throw new IOException("FIXTURE_MARKER_CHANGED");
    }
    private static SafeFileHandle Open(string path,bool directory,bool delete,bool contents=false)
    {
        // Retain every ancestor without delete-sharing. Children are opened
        // directly under that pinned tree, never by following a reparse point.
        var handle=CreateFileW(@"\\?\"+path,0x20080u|(delete?0x10000u:0)|(contents?0x80000000u:0),contents?1u:3u,IntPtr.Zero,3,0x02200000,IntPtr.Zero);
        if(handle.IsInvalid){var error=Marshal.GetLastPInvokeError();handle.Dispose();throw new Win32Exception(error);}
        try{_=Observe(handle,directory);return handle;}catch{handle.Dispose();throw;}
    }
    private static Identity Observe(SafeFileHandle handle,bool directory)
    {
        if(!GetFileInformationByHandle(handle,out var info))throw new Win32Exception();
        if((info.Attributes&0x400)!=0||((info.Attributes&0x10)!=0)!=directory||!directory&&info.Links!=1)throw new IOException("FIXTURE_LINK_OR_KIND");
        return new(info.Volume,info.IndexHigh,info.IndexLow,info.CreationHigh,info.CreationLow);
    }
    private static void Delete(SafeFileHandle handle)
    {byte disposition=1;if(!SetFileInformationByHandle(handle,4,ref disposition,1))throw new Win32Exception();}
    private readonly record struct Identity(uint Volume,uint IndexHigh,uint IndexLow,uint CreationHigh,uint CreationLow);
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation
    {public uint Attributes,CreationLow,CreationHigh,AccessLow,AccessHigh,WriteLow,WriteHigh,Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation info);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileSizeEx(SafeFileHandle handle,out long size);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool SetFileInformationByHandle(SafeFileHandle handle,uint kind,ref byte value,uint size);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle handle,uint type,uint flags,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr value);
}
