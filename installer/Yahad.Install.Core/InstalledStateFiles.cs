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
    internal string ProbeTemporary=>Roles is null?RuntimeTemporary:Path.Combine(Root,"authority-temporary");
    private readonly Action<string,InstalledFileRole>? projectedRole;
    private InstalledStateFiles(string root,InstalledFileRoles? roles,string probe,string probeDigest,Action<string,InstalledFileRole>? projection,bool singleService=false)
    {IsSingleService=singleService;Root=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(root));Roles=roles;ProbeExecutable=probe;ProbeDigest=probeDigest;projectedRole=projection;ValidateRoot();}
    internal static InstalledStateFiles SingleService(string root)=>new(root,null,"","",null,true);
    internal static InstalledStateFiles Private(string root,string probe="",string digest="",Action<string,InstalledFileRole>? projection=null)=>new(root,null,probe,digest,projection);
    internal static InstalledStateFiles Installed(string root,InstalledFileRoles roles,string probe,string digest)=>new(root,roles,probe,digest,null);
    internal void ValidateRoot(){if(IsSingleService){using var root=WindowsFileBoundary.PinSingleServiceDirectory(Root,true);}
        else if(Roles is null)SafeDirectory.ValidateSession(Root);else using(WindowsFileBoundary.PinInstalledRoleDirectory(Root,Roles,InstalledFileRole.Control)){}projectedRole?.Invoke(Root,InstalledFileRole.Control);}
    internal string PathOf(string relative)
    {var path=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(Path.Combine(Root,relative)));if(!path.StartsWith(Root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("AUTHORITY_STORAGE_PATH");return path;}
    internal InstalledFileRole SecretClass(string path,SecretRole role)=>role==SecretRole.MasterKey?InstalledFileRole.MasterKey:string.Equals(Path.GetFullPath(path),PathOf("bootstrap.json"),StringComparison.OrdinalIgnoreCase)?InstalledFileRole.Control:InstalledFileRole.Anchor;
    internal InstalledFileRole Role(string path)
    {
        var relative=Path.GetRelativePath(Root,PathOf(Path.GetRelativePath(Root,path))).Replace('\\','/');
        if(relative=="installation.json"||relative.StartsWith("Secrets/",StringComparison.Ordinal))return InstalledFileRole.Anchor;
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
    {if(IsSingleService&&!Mutable(path))return WindowsFileBoundary.OpenInstalledRead(path);var role=Role(path);projectedRole?.Invoke(path,role);return Roles is null?WindowsFileBoundary.OpenPrivateRead(path):WindowsFileBoundary.OpenInstalledRoleRead(path,Roles,role);}
    internal byte[] Read(string path,int bound){using var f=Open(path);return f.Small(bound);}
    internal IDisposable PinDirectory(string path)
    {if(IsSingleService)return WindowsFileBoundary.PinSingleServiceDirectory(path,!Mutable(path));var role=path==Root?InstalledFileRole.Control:Role(path);projectedRole?.Invoke(path,role);return Roles is null?WindowsFileBoundary.PinPrivateDirectory(path):WindowsFileBoundary.PinInstalledRoleDirectory(path,Roles,role);}
    internal void EnsureDirectory(string path)
    {if(Roles is null&&!IsSingleService)Directory.CreateDirectory(path);using var pin=PinDirectory(path);}
    internal void CreateDirectory(string path)
    {
        if(Roles is null){Directory.CreateDirectory(path);return;}
        using var parent=PinDirectory(Path.GetDirectoryName(path)!);if(Directory.Exists(path)||File.Exists(path))throw new IOException("INSTALLED_DIRECTORY_EXISTS");
        var role=Role(path);var owner=System.Security.Principal.WindowsIdentity.GetCurrent().User!;
        var runtime=role==InstalledFileRole.Recovery?"":$"(A;OICI;{(role is InstalledFileRole.Data or InstalledFileRole.Lease?"0x1301bf":"0x1200a9")};;;{Roles.Runtime.Value})";
        var authority=role is InstalledFileRole.Data or InstalledFileRole.MasterKey?"0x1200a9":"0x1301bf";
        var sddl=$"O:{owner.Value}D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;{authority};;;{Roles.Authority.Value}){runtime}";
        var acl=new DirectorySecurity();acl.SetSecurityDescriptorSddlForm(sddl);Roles.Validate(new RawSecurityDescriptor(sddl),role);new DirectoryInfo(path).Create(acl);
    }
    internal WindowsFileBoundary.DataLease Lease(string path)
    {projectedRole?.Invoke(path,Role(path));if(Roles is not null&&!File.Exists(Path.Combine(path,"process.lock"))){using var created=Create(Path.Combine(path,"process.lock"));created.Flush(true);}return Roles is null?WindowsFileBoundary.AcquireDataLease(path):WindowsFileBoundary.AcquireInstalledDataLease(path,Roles,Role(path));}
    internal FileStream Create(string path)
    {var role=Role(path);projectedRole?.Invoke(path,role);return Roles is null?new(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough):WindowsFileBoundary.CreateInstalledRoleFile(path,Roles,role,Path.GetDirectoryName(path)==Root?InstalledFileRole.Control:Role(Path.GetDirectoryName(path)!));}
}
