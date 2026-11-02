using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Authority;

internal sealed record MigrationScopeObject(string Path,bool Directory,bool PackageWrite,InstalledFileRole NormalRole);
internal interface IMigrationScopeAclKernel
{
    void RequireAuthority(string sid);
    string ActorSid(string authority)=>authority;
    IReadOnlyList<(string Path,bool Directory)> Enumerate(string root,CancellationToken cancellation);
    RawSecurityDescriptor Read(MigrationScopeObject item);
    void SetDacl(MigrationScopeObject item,RawSecurityDescriptor descriptor);
    void SetOwnerAndLabel(MigrationScopeObject item,RawSecurityDescriptor descriptor);
}

// Called by NativeOfflineMigration's prepare/normalize callbacks. Normalize is
// reached only after that owner has observed the root AND entire actual job
// empty (including failed-start retry). No profile, live/source ACL, service or
// global security setting is created or repaired here.
internal sealed class NativeMigrationScopeAccess
{
    private const int Read=0x1200a9,Modify=0x1301bf,WriteDac=0x40000,WriteOwner=0x80000;
    private readonly string root;private readonly InstalledFileRoles roles;private readonly IMigrationScopeAclKernel kernel;
    internal NativeMigrationScopeAccess(InstalledStateFiles files):this(files.Root,files.Roles??throw new IOException("MIGRATION_SCOPE_INSTALLED_ROLES_REQUIRED"),new WindowsMigrationScopeAclKernel()){}
    // Trusted in-process fake seam; never selected through IPC/CLI/environment.
    internal NativeMigrationScopeAccess(string root,InstalledFileRoles roles,IMigrationScopeAclKernel kernel)
    {this.root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));this.roles=roles;this.kernel=kernel;}
    internal Task GrantAsync(VerifiedMigrationSpec spec,CancellationToken cancellation)
        =>ApplyAsync(spec,false,cancellation);
    internal Task NormalizeAsync(VerifiedMigrationSpec spec,CancellationToken cancellation)
        =>ApplyAsync(spec,true,cancellation);
    private Task ApplyAsync(VerifiedMigrationSpec spec,bool normalize,CancellationToken cancellation)
    {
        var items=Inventory(spec,cancellation);kernel.RequireAuthority(roles.Authority.Value);
        var actor=kernel.ActorSid(roles.Authority.Value);
        if(!roles.IsUpdater(new SecurityIdentifier(actor)))throw new IOException("MIGRATION_SCOPE_ACTOR");
        // Validate the complete selected closure before mutating any member.
        // Partially completed prior grants/normalization are accepted only if
        // they are ordinary roles or this exact operation's bounded scope.
        foreach(var item in items){cancellation.ThrowIfCancellationRequested();RequireAcceptable(kernel.Read(item),item,spec);}
        foreach(var item in normalize?items.OrderByDescending(x=>x.Path.Length):items.OrderBy(x=>x.Path.Length))
        {
            cancellation.ThrowIfCancellationRequested();
            var final=Descriptor(item,spec,false,false,actor);var observed=kernel.Read(item);
            if(normalize&&WindowsFileBoundary.SameRoleAccess(observed,final)&&Integrity(observed)==Integrity(final))
            {
                // A different fixed updater may have already normalized this
                // member. Preserve its validated owner; no security write is
                // needed once the exact normal DACL/label is present.
                roles.Validate(observed,item.NormalRole,item.Directory);continue;
            }
            var transition=Descriptor(item,spec,true,normalize?false:item.PackageWrite,actor);
            // WRITE_OWNER is temporary and restricted to NEW selected objects.
            // LABEL_SECURITY_INFORMATION requires it, rather than an elevated
            // token/SeSecurityPrivilege. WRITE_DAC is also inherited here so
            // Runtime-created SQLite/result files can be normalized later.
            kernel.SetDacl(item,transition);kernel.SetOwnerAndLabel(item,transition);
            var desired=normalize?final:transition;
            if(normalize)kernel.SetDacl(item,desired);
            RequireExact(kernel.Read(item),desired,item,normalize);
        }
        return Task.CompletedTask;
    }
    internal IReadOnlyList<MigrationScopeObject> Inventory(VerifiedMigrationSpec spec,CancellationToken cancellation)
    {
        spec.Validate();if(!VerifiedMigrationSpec.Same(root,spec.ControlRoot)||roles.Authority.Value!=spec.ExpectedAuthoritySid||roles.Runtime.Value!=spec.ExpectedRuntimeSid)
            throw new IOException("MIGRATION_SCOPE_BINDING");
        var result=new List<MigrationScopeObject>();var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var selected in new[]{spec.PayloadRoot,spec.DataDirectory,Path.GetDirectoryName(spec.RequestFile)!,spec.TemporaryDirectory})
        {
            foreach(var value in kernel.Enumerate(selected,cancellation))
            {
                cancellation.ThrowIfCancellationRequested();var path=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(value.Path));
                if(!VerifiedMigrationSpec.Same(path,selected)&&!VerifiedMigrationSpec.Within(path,selected)||!names.Add(path)||result.Count>=100000)
                    throw new IOException("MIGRATION_SCOPE_CLOSURE");
                if(VerifiedMigrationSpec.Same(path,spec.ControlRoot)||VerifiedMigrationSpec.Same(path,spec.SourceDataDirectory)||VerifiedMigrationSpec.Within(path,spec.SourceDataDirectory)||
                    VerifiedMigrationSpec.Same(path,spec.CloneRoot)||!VerifiedMigrationSpec.Within(path,spec.ControlRoot))throw new IOException("MIGRATION_SCOPE_FORBIDDEN_OBJECT");
                var data=VerifiedMigrationSpec.Same(path,spec.DataDirectory)||VerifiedMigrationSpec.Within(path,spec.DataDirectory);
                var writable=!VerifiedMigrationSpec.Same(selected,spec.PayloadRoot)&&!VerifiedMigrationSpec.Same(path,spec.RequestFile);
                var role=data?(!value.Directory&&Path.GetFileName(path)=="process.lock"?InstalledFileRole.Lease:InstalledFileRole.Data):InstalledFileRole.Control;
                result.Add(new(path,value.Directory,writable,role));
            }
        }
        foreach(var required in new[]{(spec.PayloadRoot,true),(spec.NodePath,false),(spec.EntryPath,false),(spec.DataDirectory,true),(Path.GetDirectoryName(spec.RequestFile)!,true),(spec.RequestFile,false),(spec.TemporaryDirectory,true)})
            if(!names.Contains(required.Item1)||result.Single(x=>VerifiedMigrationSpec.Same(x.Path,required.Item1)).Directory!=required.Item2)throw new IOException("MIGRATION_SCOPE_MISSING_OBJECT");
        return result;
    }
    private static int AuthorityNormal(InstalledFileRole role)=>role is InstalledFileRole.Data or InstalledFileRole.MasterKey?Read:Modify;
    private static int RuntimeNormal(InstalledFileRole role)=>role is InstalledFileRole.Data or InstalledFileRole.Lease?Modify:Read;
    internal RawSecurityDescriptor Descriptor(MigrationScopeObject item,VerifiedMigrationSpec spec,bool scoped,bool low)
        =>Descriptor(item,spec,scoped,low,kernel.ActorSid(roles.Authority.Value));
    private RawSecurityDescriptor Descriptor(MigrationScopeObject item,VerifiedMigrationSpec spec,bool scoped,bool low,string actor)
    {
        var inheritance=item.Directory?"OICI":"";var authority=scoped?Read|WriteDac|WriteOwner:AuthorityNormal(item.NormalRole);
        var package=scoped?$"(A;{inheritance};0x{(item.PackageWrite?Modify:Read):x};;;{spec.ExpectedAppContainerSid})(A;{inheritance};0x20000;;;OW)":"";
        var actors=string.Concat(roles.UpdaterActors.Select(sid=>$"(A;{inheritance};0x{authority:x};;;{sid.Value})"));
        var sddl=$"O:{actor}D:P(A;{inheritance};FA;;;SY)(A;{inheritance};FA;;;BA){actors}(A;{inheritance};0x{RuntimeNormal(item.NormalRole):x};;;{roles.Runtime.Value}){package}S:(ML;{inheritance};NW;;;{(low?"LW":"ME")})";
        return new RawSecurityDescriptor(sddl);
    }
    private void RequireAcceptable(RawSecurityDescriptor descriptor,MigrationScopeObject item,VerifiedMigrationSpec spec)
    {
        _=Integrity(descriptor);
        try{roles.Validate(descriptor,item.NormalRole,item.Directory);return;}catch(IOException){}
        if(descriptor.Owner is null||!roles.IsUpdater(descriptor.Owner)&&!(item.PackageWrite&&descriptor.Owner==roles.Runtime)||descriptor.DiscretionaryAcl is null||
            (descriptor.ControlFlags&ControlFlags.DiscretionaryAclPresent)==0)throw new IOException("MIGRATION_SCOPE_OWNER");
        var authority=false;var package=false;
        foreach(GenericAce raw in descriptor.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||(ace.AceFlags&~(AceFlags.ObjectInherit|AceFlags.ContainerInherit|AceFlags.Inherited))!=0)
                throw new IOException("MIGRATION_SCOPE_ACE");
            var sid=ace.SecurityIdentifier.Value;
            var allowed=roles.IsUpdater(ace.SecurityIdentifier)?AuthorityNormal(item.NormalRole)|WriteDac|WriteOwner:sid==roles.Runtime.Value?RuntimeNormal(item.NormalRole):
                sid==spec.ExpectedAppContainerSid?item.PackageWrite?Modify:Read:sid=="S-1-3-4"?0x20000:sid is "S-1-5-18" or "S-1-5-32-544"?0x1f01ff:0;
            if(allowed==0||(ace.AccessMask&~allowed)!=0)throw new IOException("MIGRATION_SCOPE_ACCESS");
            if(sid==roles.Authority.Value&&(ace.AccessMask&(WriteDac|WriteOwner))==(WriteDac|WriteOwner))authority=true;
            if(sid==spec.ExpectedAppContainerSid)package=true;
        }
        if(!authority||!package)throw new IOException("MIGRATION_SCOPE_RECOVERY_RIGHTS");
    }
    private void RequireExact(RawSecurityDescriptor observed,RawSecurityDescriptor expected,MigrationScopeObject item,bool normalized)
    {
        if(observed.Owner!=expected.Owner||observed.DiscretionaryAcl is null||expected.DiscretionaryAcl is null||
            (observed.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0||observed.DiscretionaryAcl.Count!=expected.DiscretionaryAcl.Count||Integrity(observed)!=Integrity(expected))
            throw new IOException("MIGRATION_SCOPE_PUBLICATION");
        static Dictionary<string,(int Mask,AceFlags Flags)> Entries(RawAcl acl)
        {
            var result=new Dictionary<string,(int,AceFlags)>(StringComparer.Ordinal);
            foreach(GenericAce raw in acl)
            {
                if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||!result.TryAdd(ace.SecurityIdentifier.Value,(ace.AccessMask,ace.AceFlags)))
                    throw new IOException("MIGRATION_SCOPE_PUBLICATION_ACE");
            }
            return result;
        }
        var actual=Entries(observed.DiscretionaryAcl);var wanted=Entries(expected.DiscretionaryAcl);
        if(actual.Count!=wanted.Count||wanted.Any(x=>!actual.TryGetValue(x.Key,out var value)||value!=x.Value))throw new IOException("MIGRATION_SCOPE_PUBLICATION_ACCESS");
        if(normalized)roles.Validate(observed,item.NormalRole,item.Directory);
    }
    internal static string Integrity(RawSecurityDescriptor descriptor)
    {
        var acl=descriptor.SystemAcl;if(acl is null||acl.Count==0)return "S-1-16-8192";
        if(acl.Count!=1||(byte)acl[0].AceType!=0x11)throw new IOException("MIGRATION_SCOPE_LABEL");
        var bytes=new byte[acl[0].BinaryLength];acl[0].GetBinaryForm(bytes,0);
        if(bytes.Length<12||BitConverter.ToInt32(bytes,4)!=1)throw new IOException("MIGRATION_SCOPE_LABEL_POLICY");var sid=new SecurityIdentifier(bytes,8).Value;
        if(sid is not("S-1-16-4096" or "S-1-16-8192" or "S-1-16-12288" or "S-1-16-16384"))throw new IOException("MIGRATION_SCOPE_LABEL_LEVEL");return sid;
    }
}

internal sealed class WindowsMigrationScopeAclKernel:IMigrationScopeAclKernel
{
    public void RequireAuthority(string sid)=>_=ActorSid(sid);
    public string ActorSid(string authority)
    {
        var actual=InstalledAuthorityReader.RequireUpdater();var roles=SystemBootstrapHelperNative.Roles();
        if(roles.Authority.Value!=authority||!roles.IsUpdater(new SecurityIdentifier(actual)))throw new IOException("MIGRATION_SCOPE_BINDING");
        return actual;
    }
    public IReadOnlyList<(string Path,bool Directory)> Enumerate(string root,CancellationToken cancellation)
    {
        SafeDirectory.NoReparseAncestors(root);var result=new List<(string,bool)>();
        void Walk(string path,int depth)
        {
            cancellation.ThrowIfCancellationRequested();if(depth>64||result.Count>=100000)throw new IOException("MIGRATION_SCOPE_TREE_BOUND");
            var attributes=File.GetAttributes(path);if((attributes&FileAttributes.ReparsePoint)!=0)throw new IOException("MIGRATION_SCOPE_REPARSE");
            var directory=(attributes&FileAttributes.Directory)!=0;result.Add((path,directory));
            if(directory)foreach(var child in Directory.EnumerateFileSystemEntries(path))Walk(child,depth+1);
        }
        Walk(root,0);return result;
    }
    public RawSecurityDescriptor Read(MigrationScopeObject item)
    {
        using var handle=Open(item,0x20000);var error=GetSecurityInfo(handle,1,1|4|0x10,out _,out _,out _,out _,out var memory);
        if(error!=0)throw new Win32Exception((int)error);
        try{if(memory==IntPtr.Zero)throw new IOException("MIGRATION_SCOPE_SECURITY_BOUND");var count=GetSecurityDescriptorLength(memory);if(count is <20 or >65536)throw new IOException("MIGRATION_SCOPE_SECURITY_BOUND");var bytes=new byte[count];Marshal.Copy(memory,bytes,0,(int)count);return new(bytes,0);}
        finally{if(memory!=IntPtr.Zero)_=LocalFree(memory);}
    }
    public void SetDacl(MigrationScopeObject item,RawSecurityDescriptor descriptor)
    {
        using var handle=Open(item,0x20000|0x40000);WithDescriptor(descriptor,(owner,dacl,sacl)=>
        {var error=SetSecurityInfo(handle,1,4|0x80000000,IntPtr.Zero,IntPtr.Zero,dacl,IntPtr.Zero);if(error!=0)throw new Win32Exception((int)error);});
    }
    public void SetOwnerAndLabel(MigrationScopeObject item,RawSecurityDescriptor descriptor)
    {
        using var handle=Open(item,0x20000|0x40000|0x80000);WithDescriptor(descriptor,(owner,dacl,sacl)=>
        {var error=SetSecurityInfo(handle,1,1|0x10,owner,IntPtr.Zero,IntPtr.Zero,sacl);if(error!=0)throw new Win32Exception((int)error);});
    }
    private static void WithDescriptor(RawSecurityDescriptor descriptor,Action<IntPtr,IntPtr,IntPtr> apply)
    {
        var bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        try
        {
            var pointer=pinned.AddrOfPinnedObject();if(!GetSecurityDescriptorOwner(pointer,out var owner,out _)||!GetSecurityDescriptorDacl(pointer,out var present,out var dacl,out _)||!present||
                !GetSecurityDescriptorSacl(pointer,out present,out var sacl,out _)||!present)throw new IOException("MIGRATION_SCOPE_DESCRIPTOR");apply(owner,dacl,sacl);
        }
        finally{pinned.Free();}
    }
    private static SafeFileHandle Open(MigrationScopeObject item,uint rights)
    {
        SafeDirectory.NoReparseAncestors(item.Path);var handle=CreateFileW(@"\\?\"+Path.GetFullPath(item.Path),rights|0x80,7,IntPtr.Zero,3,0x00200000u|(item.Directory?0x02000000u:0),IntPtr.Zero);
        if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}
        try
        {
            if(!GetFileInformationByHandle(handle,out var value)||(value.Attributes&0x400)!=0||((value.Attributes&0x10)!=0)!=item.Directory||!item.Directory&&value.Links!=1)
                throw new IOException("MIGRATION_SCOPE_OBJECT_KIND");return handle;
        }
        catch{handle.Dispose();throw;}
    }
    [StructLayout(LayoutKind.Sequential)]private struct FileInformation{public uint Attributes;public System.Runtime.InteropServices.ComTypes.FILETIME Created,Accessed,Written;public uint Volume,SizeHigh,SizeLow,Links,IndexHigh,IndexLow;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern SafeFileHandle CreateFileW(string path,uint access,uint share,IntPtr security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetFileInformationByHandle(SafeFileHandle handle,out FileInformation value);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityInfo(SafeFileHandle handle,uint kind,uint information,out IntPtr owner,out IntPtr group,out IntPtr dacl,out IntPtr sacl,out IntPtr descriptor);
    [DllImport("advapi32.dll")]private static extern uint SetSecurityInfo(SafeFileHandle handle,uint kind,uint information,IntPtr owner,IntPtr group,IntPtr dacl,IntPtr sacl);
    [DllImport("advapi32.dll")]private static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetSecurityDescriptorOwner(IntPtr descriptor,out IntPtr owner,[MarshalAs(UnmanagedType.Bool)]out bool defaulted);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetSecurityDescriptorDacl(IntPtr descriptor,[MarshalAs(UnmanagedType.Bool)]out bool present,out IntPtr acl,[MarshalAs(UnmanagedType.Bool)]out bool defaulted);
    [DllImport("advapi32.dll",SetLastError=true)][return:MarshalAs(UnmanagedType.Bool)]private static extern bool GetSecurityDescriptorSacl(IntPtr descriptor,[MarshalAs(UnmanagedType.Bool)]out bool present,out IntPtr acl,[MarshalAs(UnmanagedType.Bool)]out bool defaulted);
    [DllImport("kernel32.dll")]private static extern IntPtr LocalFree(IntPtr memory);
}
