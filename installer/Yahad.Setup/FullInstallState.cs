using System.IO;
using System.Security.Principal;
using System.Runtime.InteropServices;
using System.Text.Json;
using Yahad.Install;
namespace Yahad.Setup;

internal sealed record FullInstallState(int Format,string InstallationId,string DataId,string PackageDigest,string PayloadDigest,string SourceCommit,
    string SystemManifestDigest,string OwnerSid,int Port,int Schema,string Phase,string SetupSha256,FirstOwnerBinding FirstOwner,UpdateSetupChoice Consent,
    InstallMember[] Files,string? RuntimeSid=null,string? AuthoritySid=null,string? PolicyDigest=null,bool ServicesCompleted=false,InstallMember[]? PreservedBinaries=null,string? PlanDigest=null);
internal sealed class FullInstallFiles
{
    // Setup embeds the complete self-contained package and is larger than an
    // individual component. Keep the ordinary member cap and aggregate bound.
    internal static long MaximumBinaryBytes(string path)=>path=="Yahad.Setup.exe"?1073741824L:536870912L;
    internal InstalledFileRoles Roles{get;}private readonly SecurityIdentifier owner;private readonly string root;private readonly bool binaries;
    internal FullInstallFiles(FullInstallState state,bool binaries=false)
    {
        if(state.RuntimeSid is null||state.AuthoritySid is null)throw new IOException("UPDATE_ROLES_UNPROVISIONED");
        Roles=new InstalledFileRoles(new(state.AuthoritySid),new(state.RuntimeSid)).WithRecoveryHelpers();owner=new(state.OwnerSid);root=binaries?InstallLocations.Binaries:InstallLocations.State;this.binaries=binaries;
    }
    internal string PathOf(string relative)
    {var path=WindowsFileBoundary.CanonicalLocalPath(Path.GetFullPath(Path.Combine(root,relative.Replace('/',Path.DirectorySeparatorChar))));if(path!=root&&!path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("UPDATE_INSTALL_PATH");return path;}
    internal InstalledFileRole Role(string path)
    {
        var relative=Path.GetRelativePath(root,path).Replace('\\','/');
        if(binaries)return relative=="Yahad.Setup.exe"||relative=="bootstrap/probe"||relative.StartsWith("bootstrap/probe/",StringComparison.Ordinal)?InstalledFileRole.Anchor:InstalledFileRole.Control;
        if(relative is "installer-state.json" or "full-installer-plan.json" or "installation.json" or "update-authority.json" or "lan-preparation.json" or "installer-readiness.json"||relative.StartsWith("Secrets/",StringComparison.Ordinal))return InstalledFileRole.Anchor;
        if(relative.StartsWith("payloads/",StringComparison.Ordinal))return InstalledFileRole.Payload;
        if(relative=="intake"||relative=="owner-ledger"||relative.StartsWith("intake/",StringComparison.Ordinal)||relative.StartsWith("owner-ledger/",StringComparison.Ordinal)||relative.StartsWith(".authority",StringComparison.Ordinal)||relative.StartsWith("authority-temporary",StringComparison.Ordinal))return InstalledFileRole.Recovery;
        if(relative=="temporary"||relative.StartsWith("temporary/",StringComparison.Ordinal))return InstalledFileRole.Data;
        if(relative.StartsWith("datasets/",StringComparison.Ordinal))
        {
            if(Path.GetFileName(path) is "master-key" or "update-policy" or "update-pin")return InstalledFileRole.MasterKey;
            if(Path.GetFileName(path)=="process.lock")return InstalledFileRole.Lease;
            if(relative.Contains("/data/",StringComparison.Ordinal)||relative.EndsWith("/data",StringComparison.Ordinal))return InstalledFileRole.Data;
            return InstalledFileRole.RecoveryEvidence;
        }
        return InstalledFileRole.Control;
    }
    private SecurityIdentifier? ReadonlyOwner(string path)
    {
        if(!binaries)return null;var relative=Path.GetRelativePath(root,path).Replace('\\','/');
        return relative=="Yahad.Setup.exe"||relative=="bootstrap/owner"||relative.StartsWith("bootstrap/owner/",StringComparison.Ordinal)?owner:null;
    }
    private bool Seed(string path)=>!binaries&&Path.GetRelativePath(root,path) is "initial-seed.json" or "initial-retry.json";
    internal void Validate(string path,bool directory)
    {
        var selected=PathOf(Path.GetRelativePath(root,path));
        if(directory){using var pin=WindowsFileBoundary.PinInstallerRoleDirectory(selected,Roles,Role(selected),ReadonlyOwner(selected));}
        else{using var pin=WindowsFileBoundary.OpenInstallerRoleRead(selected,Roles,Role(selected),ReadonlyOwner(selected),Seed(selected));}
    }
    internal byte[] Read(string relative,int maximum=16777216)
    {var path=PathOf(relative);using var pin=WindowsFileBoundary.OpenInstallerRoleRead(path,Roles,Role(path),ReadonlyOwner(path),Seed(path));return pin.Small(maximum);}
    internal void EnsureDirectory(string relative)
    {
        var path=PathOf(relative);if(path==root){Validate(path,true);return;}var parent=Path.GetDirectoryName(path)!;EnsureDirectory(Path.GetRelativePath(root,parent));
        if(Directory.Exists(path)){Validate(path,true);return;}
        NativeInstallFiles.NewDirectory(path,FullUpdateInstallerPlan.Descriptor(Roles,Role(path),true,ReadonlyOwner(path)?.Value));
    }
    internal void Publish(string relative,byte[] bytes,bool replace=false)
    {
        var path=PathOf(relative);EnsureDirectory(Path.GetRelativePath(root,Path.GetDirectoryName(path)!));
        var descriptor=Seed(path)?NativeInstallFiles.Descriptor().Replace("D:P","D:P(A;;0x1300a9;;;"+Roles.Runtime.Value+")"):FullUpdateInstallerPlan.Descriptor(Roles,Role(path),false,ReadonlyOwner(path)?.Value);
        if(File.Exists(path))
        {
            var before=Read(relative);if(before.AsSpan().SequenceEqual(bytes))return;
            if(!replace)throw new IOException("UPDATE_INSTALL_RECORD_CHANGED");
            NativeInstallFiles.ReplaceOwned(path,bytes,descriptor);
        }
        else if(Seed(path)||Role(path) is InstalledFileRole.Anchor or InstalledFileRole.MasterKey)
        {
            var prefix=Path.GetFileName(path)+".partial-";var partials=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,prefix+"*").Take(33).ToArray();if(partials.Length>32)throw new IOException("UPDATE_INSTALL_METADATA_BOUND");
            foreach(var partial in partials)
            {
                var suffix=Path.GetFileName(partial)[prefix.Length..];if(!Guid.TryParseExact(suffix,"N",out var id)||id.ToString("N")!=suffix)throw new IOException("UPDATE_INSTALL_METADATA_NAME");
                using(var held=WindowsFileBoundary.OpenInstallerRoleRead(partial,Roles,Role(path),ReadonlyOwner(path),Seed(path),true)){}
                WindowsFileBoundary.DeleteInstallerRoleMember(partial,false,Roles,Role(path),ReadonlyOwner(path),Seed(path),true);
            }
            NativeInstallFiles.PublishNewProtectedFile(path,bytes,descriptor);
        }
        else NativeInstallFiles.WriteNew(path,bytes,descriptor);
    }
    internal void CopyVerified(string source,string relative,InstallMember expected)
    {
        var path=PathOf(relative);EnsureDirectory(Path.GetRelativePath(root,Path.GetDirectoryName(path)!));
        var prefix=Path.GetFileName(path)+".install-partial-";var partials=Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(path)!,prefix+"*").Take(33).ToArray();
        if(partials.Length>32)throw new IOException("UPDATE_COPY_RESIDUE_BOUND");
        foreach(var old in partials)
        {
            if(!Guid.TryParseExact(Path.GetFileName(old)[prefix.Length..],"N",out _))throw new IOException("UPDATE_COPY_RESIDUE_KIND");
            using(var pin=WindowsFileBoundary.OpenInstallerRoleRead(old,Roles,Role(path),ReadonlyOwner(path))){}
            WindowsFileBoundary.DeleteInstallerRoleMember(old,false,Roles,Role(path),ReadonlyOwner(path));
        }
        if(File.Exists(path)){VerifyFile(relative,expected);return;}SafeDirectory.NoReparseAncestors(source);
        using var input=new FileStream(source,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length!=expected.Size||Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(input))!=expected.Sha256)throw new IOException("UPDATE_INSTALL_SOURCE_HASH");input.Position=0;
        var partial=path+".install-partial-"+Guid.NewGuid().ToString("N");
        using(var output=NativeInstallFiles.CreateNewFile(partial,FullUpdateInstallerPlan.Descriptor(Roles,Role(path),false,ReadonlyOwner(path)?.Value))){input.CopyTo(output);output.Flush(true);}
        using(var check=WindowsFileBoundary.OpenInstallerRoleRead(partial,Roles,Role(path),ReadonlyOwner(path)))
        {
            if(check.Length!=expected.Size)throw new IOException("UPDATE_INSTALL_SIZE");using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;int count;while((count=check.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}
            if(Convert.ToHexStringLower(hash.GetHashAndReset())!=expected.Sha256)throw new IOException("UPDATE_INSTALL_HASH");
        }
        if(!MoveFileExW(partial,path,8))throw new System.ComponentModel.Win32Exception();
        VerifyFile(relative,expected);
    }
    internal void VerifyFile(string relative,InstallMember expected)
    {var path=PathOf(relative);using var input=WindowsFileBoundary.OpenInstallerRoleRead(path,Roles,Role(path),ReadonlyOwner(path));if(input.Length!=expected.Size)throw new IOException("UPDATE_INSTALL_SIZE");using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);var buffer=new byte[65536];long offset=0;int count;while((count=input.Read(offset,buffer))>0){hash.AppendData(buffer,0,count);offset+=count;}if(Convert.ToHexStringLower(hash.GetHashAndReset())!=expected.Sha256)throw new IOException("UPDATE_INSTALL_HASH");}
    internal void Delete(string path,bool directory)
    {var selected=PathOf(Path.GetRelativePath(root,path));WindowsFileBoundary.DeleteInstallerRoleMember(selected,directory,Roles,Role(selected),ReadonlyOwner(selected),Seed(selected));}
    internal WindowsFileBoundary.DataLease Lease(string path)
    {
        var selected=PathOf(Path.GetRelativePath(root,path));Validate(selected,true);
        var gate=Path.Combine(selected,"process.lock");
        if(!File.Exists(gate))Publish(Path.GetRelativePath(root,gate),[]);
        return WindowsFileBoundary.AcquireInstalledDataLease(selected,Roles,Role(selected));
    }
    internal void ClearInstallerMetadataResidue()
    {
        if(binaries)throw new IOException("UPDATE_INSTALL_METADATA_SCOPE");Validate(root,true);
        foreach(var target in FullInstallerResidue.Metadata)
        {
            var candidates=Directory.EnumerateFileSystemEntries(root,target+".partial-*").Take(33).ToArray();if(candidates.Length>32)throw new IOException("UPDATE_INSTALL_METADATA_BOUND");
            foreach(var path in candidates)
            {
                if(!FullInstallerResidue.IsOwnedName(Path.GetFileName(path)))throw new IOException("UPDATE_INSTALL_METADATA_NAME");
                // No content recovery or update cancellation: these are only
                // installer-owned generated Anchor temporaries. The validated
                // canonical record remains the source of identity/consent.
                using(var held=WindowsFileBoundary.OpenInstallerRoleRead(path,Roles,InstalledFileRole.Anchor)){}
                WindowsFileBoundary.DeleteInstallerRoleMember(path,false,Roles,InstalledFileRole.Anchor);
            }
        }
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]private static extern bool MoveFileExW(string source,string destination,uint flags);
}
