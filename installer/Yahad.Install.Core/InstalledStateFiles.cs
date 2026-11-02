using System.Security.AccessControl;
namespace Yahad.Install;

// Concrete rooted storage shared by installed composition and private fixtures.
// Projection validates synthetic service-role observations while retaining actual
// private-file checks; it is internal and never selectable by CLI/JSON/env.
internal sealed class InstalledStateFiles
{
    internal bool IsSingleService{get;}
    internal string Root{get;}internal InstalledFileRoles? Roles{get;}
    internal string ProbeExecutable{get;}internal string ProbeDigest{get;}
    internal string RuntimeTemporary=>Path.Combine(Root,"temporary");
    internal bool IsRuntimeProfile{get;private set;}
    internal string ProbeTemporary=>Roles is null||IsRuntimeProfile?RuntimeTemporary:Path.Combine(Root,"authority-temporary");
    private readonly Action<string,InstalledFileRole>? projectedRole;
    private string? recoveryStaging;
    private InstalledStateFiles(string root,InstalledFileRoles? roles,string probe,string probeDigest,Action<string,InstalledFileRole>? projection,bool singleService=false)
    {IsSingleService=singleService;Root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));Roles=roles;ProbeExecutable=probe;ProbeDigest=probeDigest;projectedRole=projection;ValidateRoot();}
    internal static InstalledStateFiles SingleService(string root)=>new(root,null,"","",null,true);
    internal static InstalledStateFiles Private(string root,string probe="",string digest="",Action<string,InstalledFileRole>? projection=null)=>new(root,null,probe,digest,projection);
    internal static InstalledStateFiles Installed(string root,InstalledFileRoles roles,string probe,string digest)=>new(root,roles,probe,digest,null);
    internal static InstalledStateFiles InstalledRuntime(string root,InstalledFileRoles roles,string probe,string digest)=>new(root,roles,probe,digest,null){IsRuntimeProfile=true};
    internal void ValidateRoot(){if(IsSingleService){using var root=WindowsFileBoundary.PinSingleServiceDirectory(Root,true);}
        else if(Roles is null)SafeDirectory.ValidateSession(Root);else using(WindowsFileBoundary.PinInstalledRoleDirectory(Root,Roles,InstalledFileRole.Control)){}projectedRole?.Invoke(Root,InstalledFileRole.Control);}
    internal string PathOf(string relative)
    {var path=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(Path.Combine(Root,relative)));if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("AUTHORITY_STORAGE_PATH");return path;}
    internal InstalledFileRole SecretClass(string path,SecretRole role)=>role==SecretRole.MasterKey?InstalledFileRole.MasterKey:string.Equals(Path.GetFullPath(path),PathOf("bootstrap.json"),StringComparison.OrdinalIgnoreCase)?InstalledFileRole.Control:InstalledFileRole.Anchor;
    internal InstalledFileRole Role(string path)
    {
        if(recoveryStaging is not null&&(path.Equals(recoveryStaging,StringComparison.OrdinalIgnoreCase)||path.StartsWith(recoveryStaging+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)))return Roles?.HasRecoveryHelpers==true?InstalledFileRole.RecoveryStaging:InstalledFileRole.RecoveryEvidence;
        return PublishedRole(path);
    }
    private InstalledFileRole PublishedRole(string path)
    {
        var relative=Path.GetRelativePath(Root,PathOf(Path.GetRelativePath(Root,path))).Replace('\\','/');
        if(relative is "installation.json" or InstalledUpdaterLayout.AnchorFile||relative.StartsWith("Secrets/",StringComparison.Ordinal))return InstalledFileRole.Anchor;
        if(relative.StartsWith("payloads/",StringComparison.Ordinal))return InstalledFileRole.Payload;
        if(relative.StartsWith(".authority",StringComparison.Ordinal)||relative.StartsWith("owner-ledger",StringComparison.Ordinal)||relative.StartsWith("intake",StringComparison.Ordinal)||relative.StartsWith("authority-temporary",StringComparison.Ordinal)||relative.StartsWith("pending.json",StringComparison.Ordinal))return InstalledFileRole.Recovery;
        if(relative.StartsWith("temporary",StringComparison.Ordinal))return InstalledFileRole.Data;
        if(relative.StartsWith("datasets/",StringComparison.Ordinal))
        {
            var leaf=Path.GetFileName(path);
            if(leaf is "master-key" or "update-policy" or "update-pin")return InstalledFileRole.MasterKey;
            if(leaf=="process.lock")return InstalledFileRole.Lease;
            if(relative.Contains("/data/",StringComparison.Ordinal)||relative.EndsWith("/data",StringComparison.Ordinal))return InstalledFileRole.Data;
            return InstalledFileRole.RecoveryEvidence;
        }
        return InstalledFileRole.Control;
    }
    private bool Mutable(string path){var n=Path.GetRelativePath(Root,path).Replace('\\','/');return n=="temporary"||n.StartsWith("temporary/",StringComparison.Ordinal)||n.EndsWith("/data",StringComparison.Ordinal)||n.Contains("/data/",StringComparison.Ordinal);}
    internal WindowsFileBoundary.PrivateReadLease Open(string path)
    {
        if(IsSingleService&&!Mutable(path))return WindowsFileBoundary.OpenInstalledRead(path);var role=Role(path);projectedRole?.Invoke(path,role);
        if(Roles is null)return WindowsFileBoundary.OpenPrivateRead(path);
        try{return WindowsFileBoundary.OpenInstalledRoleRead(path,Roles,role);}
        catch(IOException)when(recoveryStaging is not null&&role!=PublishedRole(path))
        {return WindowsFileBoundary.OpenInstalledRoleRead(path,Roles,PublishedRole(path));} // Interrupted per-object promotion: accept only the exact final role.
    }
    internal byte[] Read(string path,int bound){using var f=Open(path);return f.Small(bound);}
    internal IDisposable PinDirectory(string path)
    {
        if(IsSingleService)return WindowsFileBoundary.PinSingleServiceDirectory(path,!Mutable(path));var role=path==Root?InstalledFileRole.Control:Role(path);projectedRole?.Invoke(path,role);
        if(Roles is null)return WindowsFileBoundary.PinPrivateDirectory(path);
        try{return WindowsFileBoundary.PinInstalledRoleDirectory(path,Roles,role);}
        catch(IOException)when(recoveryStaging is not null&&role!=PublishedRole(path))
        {return WindowsFileBoundary.PinInstalledRoleDirectory(path,Roles,PublishedRole(path));}
    }
    // Explicit preactivation capability for a NEW, generated restore directory.
    // Published/live Role() semantics on the original files object do not change.
    internal InstalledStateFiles ForRecoveryStaging(string directory)
    {
        if(IsSingleService||IsRuntimeProfile||recoveryStaging is not null)throw new IOException("RECOVERY_STAGING_SCOPE");
        var path=PathOf(Path.GetRelativePath(Root,directory));var parts=Path.GetRelativePath(Root,path).Replace('\\','/').Split('/');
        if(parts.Length!=3||parts[0]!="datasets"||!CanonicalId(parts[1])||!parts[2].StartsWith("restore-",StringComparison.Ordinal)||!CanonicalId(parts[2][8..]))throw new IOException("RECOVERY_STAGING_SCOPE");
        return new InstalledStateFiles(Root,Roles,ProbeExecutable,ProbeDigest,projectedRole){recoveryStaging=path,IsRuntimeProfile=IsRuntimeProfile};
    }
    private static bool CanonicalId(string id)=>Guid.TryParseExact(id,"N",out var parsed)&&parsed.ToString("N")==id;
    internal void PromoteRecoveryDirectory(string completed,Action<string>? fault=null)
    {
        if(recoveryStaging is null||!completed.Equals(Path.Combine(recoveryStaging,"completed"),StringComparison.OrdinalIgnoreCase))throw new IOException("RECOVERY_PUBLICATION_SCOPE");
        var members=new List<(string Path,bool Directory)>();var held=new List<IDisposable>();
        try
        {
            void Walk(string path,int depth)
            {
                if(depth>64)throw new IOException("RECOVERY_TREE_DEPTH");held.Add(PinDirectory(path));
                foreach(var child in Directory.EnumerateFileSystemEntries(path))
                {
                    if((File.GetAttributes(child)&FileAttributes.ReparsePoint)!=0)throw new IOException("RECOVERY_REPARSE");
                    if(Directory.Exists(child))Walk(child,depth+1);else{held.Add(Open(child));members.Add((child,false));}
                    if(members.Count>100000)throw new IOException("RECOVERY_TREE_BOUND");
                }
                members.Add((path,true));
            }
            Walk(completed,0);
            foreach(var member in members)
            {
                var role=PublishedRole(member.Path);projectedRole?.Invoke(member.Path,role);
                if(Roles is not null)
                {
                    var current=System.Security.Principal.WindowsIdentity.GetCurrent().User!;
                    if(!Roles.IsUpdater(current))throw new IOException("RECOVERY_PUBLICATION_ROLE");
                    var inheritance=member.Directory?"OICI;":";";
                    var runtime=role==InstalledFileRole.Recovery?"":$"(A;{inheritance}{(role is InstalledFileRole.Data or InstalledFileRole.Lease?"0x1301bf":"0x1200a9")};;;{Roles.Runtime.Value})";
                    var authority=role is InstalledFileRole.Data or InstalledFileRole.MasterKey?"0x1200a9":"0x1301bf";
                    var updater=string.Concat(Roles.UpdaterActors.Select(sid=>$"(A;{inheritance}{authority};;;{sid.Value})"));
                    var sddl=$"O:{current.Value}D:P(A;{inheritance}FA;;;SY)(A;{inheritance}FA;;;BA){updater}{runtime}";
                    Roles.Validate(new RawSecurityDescriptor(sddl),role,member.Directory);
                    if(!WindowsFileBoundary.HasExactInstalledRoleAccess(member.Path,member.Directory,Roles,role,sddl))
                    {
                        // A prior fixed updater may own the new output. The
                        // scoped creation grant permits DACL-only publication;
                        // already-final members need no second security write.
                        using IDisposable stagingPin=member.Directory?WindowsFileBoundary.PinInstalledRoleDirectory(member.Path,Roles,Role(member.Path)):
                            WindowsFileBoundary.OpenInstalledRoleRead(member.Path,Roles,Role(member.Path));
                        if(Roles.HasRecoveryHelpers)
                        {
                            var scoped=$"O:{current.Value}D:P(A;{inheritance}FA;;;SY)(A;{inheritance}FA;;;BA)"+
                                string.Concat(Roles.UpdaterActors.Select(sid=>$"(A;{inheritance}0x1701bf;;;{sid.Value})"));
                            if(!WindowsFileBoundary.HasExactInstalledRoleAccess(member.Path,member.Directory,Roles,InstalledFileRole.RecoveryStaging,scoped))throw new IOException("RECOVERY_STAGING_ACCESS_CHANGED");
                        }
                        PersistRecoveryAccess(member.Path,member.Directory,sddl);
                        if(!WindowsFileBoundary.HasExactInstalledRoleAccess(member.Path,member.Directory,Roles,role,sddl))throw new IOException("RECOVERY_ROLE_PUBLICATION_UNCONFIRMED");
                    }
                }
                fault?.Invoke("recovery-role-published");
            }
        }
        finally{for(var i=held.Count-1;i>=0;i--)held[i].Dispose();}
    }
    // The pinned source/final-role checks above already prove an allowed owner.
    // Preserve that owner and the audit policy; do not request SACL or ownership
    // writes from the intentionally minimal Authority service token.
    internal static void PersistRecoveryAccess(string path,bool directory,string descriptor)
    {
        if(directory){var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(descriptor,AccessControlSections.Access);new DirectoryInfo(path).SetAccessControl(acl);}
        else{var acl=new FileSecurity();acl.SetSecurityDescriptorSddlForm(descriptor,AccessControlSections.Access);new FileInfo(path).SetAccessControl(acl);}
    }
    internal void EnsureDirectory(string path)
    {if(Roles is null&&!IsSingleService)Directory.CreateDirectory(path);using var pin=PinDirectory(path);}
    internal void CreateDirectory(string path)
    {
        if(Roles is null){Directory.CreateDirectory(path);return;}
        using var parent=PinDirectory(Path.GetDirectoryName(path)!);if(Directory.Exists(path)||File.Exists(path))throw new IOException("INSTALLED_DIRECTORY_EXISTS");
        var role=Role(path);var owner=System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var runtime=InstalledFileRoles.RuntimeMask(role)==0?"":$"(A;OICI;0x{InstalledFileRoles.RuntimeMask(role):x};;;{Roles.Runtime.Value})";
        var authority=$"0x{Roles.UpdaterMask(role):x}";
        var updater=string.Concat(Roles.UpdaterActors.Select(sid=>$"(A;OICI;{authority};;;{sid.Value})"));
        var sddl=$"O:{owner.Value}D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA){updater}{runtime}";
        var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(sddl);Roles.Validate(new RawSecurityDescriptor(sddl),role);new DirectoryInfo(path).Create(acl);
    }
    internal WindowsFileBoundary.DataLease Lease(string path)
    {projectedRole?.Invoke(path,Role(path));if(Roles is not null&&!File.Exists(Path.Combine(path,"process.lock"))){using var created=Create(Path.Combine(path,"process.lock"));created.Flush(true);}return Roles is null?WindowsFileBoundary.AcquireDataLease(path):WindowsFileBoundary.AcquireInstalledDataLease(path,Roles,Role(path));}
    internal FileStream Create(string path)
    {var role=Role(path);projectedRole?.Invoke(path,role);return Roles is null?new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough):WindowsFileBoundary.CreateInstalledRoleFile(path,Roles,role,Path.GetDirectoryName(path)==Root?InstalledFileRole.Control:Role(Path.GetDirectoryName(path)!));}
}
