using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

public enum SecretRole { MasterKey, SetupCapability }

// Fixed diagnostic categories only; no installation values cross elevation.
internal enum ReceiptRecoveryCheck
{
    ReceiptOwnerOrDacl, ReceiptAce, BinaryOwnerOrDacl, BinaryAce, BinaryPrincipal,
    StateShape, StateAuthority, StateDigests, ManifestShape, ReceiptIdentity,
    HostRecord, HostDigest, SetupDigest, PostWriteIdentity, ContentUnchanged
}
internal sealed class ReceiptRecoveryRefusal(ReceiptRecoveryCheck check):IOException("UNINSTALL_RECEIPT_RECOVERY_REFUSED")
{internal ReceiptRecoveryCheck Check{get;}=Enum.IsDefined(check)?check:throw new ArgumentOutOfRangeException(nameof(check));}

/// <summary>Windows app file boundary. Never changes an existing ACL to make a check pass.</summary>
public static class WindowsFileBoundary
{
    private const uint ReadControl = 0x20000, ReadAttributes = 0x80, GenericRead = 0x80000000;
    private const uint Reparse = 0x400, DirectoryAttribute = 0x10;

    public static void ValidateAcl(RawSecurityDescriptor acl, SecurityIdentifier application, bool bootstrap)
    {
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        if (acl.Owner is null || !(acl.Owner.Equals(system) || acl.Owner.Equals(admins) || !bootstrap && acl.Owner.Equals(application)) ||
            acl.DiscretionaryAcl is null || (acl.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw new IOException("NATIVE_OWNER_OR_DACL");
        foreach (GenericAce entry in acl.DiscretionaryAcl)
        {
            // Reject conditional/object/unknown ACE shapes rather than accidentally treating them as restrictive.
            if (entry is not CommonAce ace || ace.IsCallback) throw new IOException("NATIVE_DACL_SHAPE");
            if (ace.AceQualifier == AceQualifier.AccessDenied) continue;
            if (ace.AceQualifier != AceQualifier.AccessAllowed) throw new IOException("NATIVE_DACL_SHAPE");
            if (ace.SecurityIdentifier.Equals(system) || ace.SecurityIdentifier.Equals(admins)) continue;
            if (!ace.SecurityIdentifier.Equals(application)) throw new IOException("NATIVE_DACL_PRINCIPAL");
            var readOnly = (int)(FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize | FileSystemRights.ReadPermissions);
            if (bootstrap && (ace.AccessMask & ~readOnly) != 0) throw new IOException("NATIVE_BOOTSTRAP_WRITABLE");
        }
    }

    public static SecretLease OpenSecret(string path, SecretRole role)
    {
        var installed=InstalledProcessState.ForPath(path);
        var pinned = Pin(path, leafDirectory: false, readContents: true);
        try
        {
            if(installed is null)ValidateAcl(Security(pinned.Leaf), ApplicationSid(), role == SecretRole.SetupCapability);else installed.Roles!.Validate(Security(pinned.Leaf),installed.SecretClass(path,role),false);
            return new(pinned, role == SecretRole.MasterKey ? 4096 : 1024);
        }
        catch { pinned.Dispose(); throw; }
    }

    internal static PrivateReadLease OpenBootstrapForInstaller(string path,SecurityIdentifier expectedService)
    {var pin=Pin(path,false,true);try{ValidateAcl(Security(pin.Leaf),expectedService,true);return new(pin);}catch{pin.Dispose();throw;}}
    internal static PrivateReadLease OpenInstallerInitialSeed(string path,SecurityIdentifier expectedService)
    {var pin=Pin(path,false,true);try{ValidateInitialSeedAcl(Security(pin.Leaf),expectedService);return new(pin);}catch{pin.Dispose();throw;}}
    internal static PrivateReadLease OpenInstallerMetadata(string path)
    {
        var pin=Pin(path,false,true);try
        {
            ValidateInstallerMetadataAcl(Security(pin.Leaf));
            return new(pin);
        }
        catch{pin.Dispose();throw;}
    }
    internal static void ValidateInstallerMetadataAcl(RawSecurityDescriptor acl)
    {
            var admins=new SecurityIdentifier("S-1-5-32-544");var system=new SecurityIdentifier("S-1-5-18");
            if(acl.Owner!=admins&&acl.Owner!=system||acl.DiscretionaryAcl is null)throw new IOException("INSTALL_METADATA_OWNER");
            foreach(GenericAce raw in acl.DiscretionaryAcl)
            {
                if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("INSTALL_METADATA_ACE");
                if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
                if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("INSTALL_METADATA_ACE");
                if(ace.SecurityIdentifier==admins||ace.SecurityIdentifier==system)continue;
                if(!ace.SecurityIdentifier.Value.StartsWith("S-1-5-80-",StringComparison.Ordinal)||(ace.AccessMask&~0x1200a9)!=0)throw new IOException("INSTALL_METADATA_ACCESS");
            }
    }
    internal static void ValidatePreparingInstallAcl(RawSecurityDescriptor acl,bool directory)
    {
        ValidateInstallerMetadataAcl(acl);
        if(directory&&(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new IOException("UPDATE_PREPARING_ACCESS");
        foreach(GenericAce raw in acl.DiscretionaryAcl!)
            if(raw is CommonAce ace&&ace.AceQualifier==AceQualifier.AccessAllowed&&ace.AccessMask!=0&&ace.SecurityIdentifier.Value is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("UPDATE_PREPARING_ACCESS");
    }
    internal static IDisposable PinPreparingInstallMember(string path,bool directory)
    {var pin=Pin(path,directory,false);try{ValidatePreparingInstallAcl(Security(pin.Leaf),directory);return pin;}catch{pin.Dispose();throw;}}
    internal static void DeletePreparingInstallMember(string path,bool directory,long size=0,string? sha256=null)
    {
        using var pin=Pin(path,directory,!directory,true);ValidatePreparingInstallAcl(Security(pin.Leaf),directory);
        if(!directory)
        {
            using var content=new FileStream(pin.Leaf,FileAccess.Read);
            if(content.Length!=size||sha256 is null||Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content))!=sha256)throw new IOException("UPDATE_PREPARING_FILE_CHANGED");
            byte disposition=1;if(!SetFileInformationByHandle(pin.Leaf,4,ref disposition,1))throw new Win32Exception();
        }
        else{byte disposition=1;if(!SetFileInformationByHandle(pin.Leaf,4,ref disposition,1))throw new Win32Exception();}
    }
    internal static PrivateReadLease OpenInstallerDiagnostic(string path,SecurityIdentifier owner)
    {
        var pin=Pin(path,false,true);try{ValidateInstallerDiagnosticAcl(Security(pin.Leaf),owner);return new(pin);}catch{pin.Dispose();throw;}
    }
    // Comparison only: this is the exact inherited ACL left by the old receipt
    // writer before protection failed. SYSTEM/Administrators inherited Modify,
    // not FullControl. FileSecurity shows six effective ACEs; the native handle
    // descriptor additionally retains seven exact InheritOnly propagation ACEs.
    // Neither representation is removal authority.
    internal static PrivateReadLease OpenInterruptedReceiptForComparison(string path)
    {
        var pin=Pin(path,false,true);try{ValidateInterruptedReceiptAcl(Security(pin.Leaf));return new(pin);}catch{pin.Dispose();throw;}
    }
    internal static void ValidateInterruptedReceiptAcl(RawSecurityDescriptor acl)
    {
        var expected=new HashSet<(string Sid,int Mask,AceFlags Flags)>
        {
            ("S-1-5-18",0x1301bf,AceFlags.None),("S-1-5-32-544",0x1301bf,AceFlags.None),("S-1-5-32-545",0x1200a9,AceFlags.None),
            ("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",0x1f01ff,AceFlags.None),
            ("S-1-15-2-1",0x1200a9,AceFlags.None),("S-1-15-2-2",0x1200a9,AceFlags.None)
        };
        if(acl.DiscretionaryAcl?.Count==13)
        {
            var propagation=AceFlags.ObjectInherit|AceFlags.ContainerInherit|AceFlags.InheritOnly;
            expected.UnionWith(new[]{
                ("S-1-3-0",0x10000000,propagation),("S-1-5-18",0x10000000,propagation),("S-1-5-32-544",0x10000000,propagation),
                ("S-1-5-32-545",unchecked((int)0xa0000000),propagation),
                ("S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464",0x10000000,AceFlags.ContainerInherit|AceFlags.InheritOnly),
                ("S-1-15-2-1",unchecked((int)0xa0000000),propagation),("S-1-15-2-2",unchecked((int)0xa0000000),propagation)
            });
        }
        if(acl.Owner?.Value!="S-1-5-32-544"||acl.DiscretionaryAcl is null||acl.DiscretionaryAcl.Count!=expected.Count||(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.ReceiptOwnerOrDacl);
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||!expected.Remove((ace.SecurityIdentifier.Value,ace.AccessMask,ace.AceFlags)))throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.ReceiptAce);
        }
    }
    internal static PrivateReadLease OpenInstallerBinaryForComparison(string path,SecurityIdentifier service,SecurityIdentifier owner)
    {
        var pin=Pin(path,false,true);try{ValidateInstallerBinaryAcl(Security(pin.Leaf),service,owner);return new(pin);}catch{pin.Dispose();throw;}
    }
    internal static void ValidateInstallerBinaryAcl(RawSecurityDescriptor acl,SecurityIdentifier service,SecurityIdentifier owner)
    {
        if(acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544")||acl.DiscretionaryAcl is null)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.BinaryOwnerOrDacl);
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.BinaryAce);
            if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
            if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.BinaryAce);
            if(ace.SecurityIdentifier.Value is "S-1-5-18" or "S-1-5-32-544")continue;
            if((ace.SecurityIdentifier!=service&&ace.SecurityIdentifier!=owner)||(ace.AccessMask&~0x1200a9)!=0)throw new ReceiptRecoveryRefusal(ReceiptRecoveryCheck.BinaryPrincipal);
        }
    }
    internal static void ValidateInstallerDiagnosticAcl(RawSecurityDescriptor acl,SecurityIdentifier owner)
    {
        var admins=new SecurityIdentifier("S-1-5-32-544");var system=new SecurityIdentifier("S-1-5-18");
        if((acl.Owner!=admins&&acl.Owner!=system)||acl.DiscretionaryAcl is null||(acl.ControlFlags&ControlFlags.DiscretionaryAclProtected)==0)throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");
        foreach(GenericAce raw in acl.DiscretionaryAcl)
        {
            if(raw is not CommonAce ace||ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");
            if(ace.SecurityIdentifier==admins||ace.SecurityIdentifier==system)continue;
            // The old full-installer writer emitted this exact read/execute
            // owner ACE on JSON diagnostics. It grants no mutation authority.
            var legacyRead=ace.AccessMask==0x1200a9&&ace.AceFlags==AceFlags.None;
            if(ace.SecurityIdentifier!=owner||((ace.AccessMask&~0x120089)!=0&&!legacyRead))throw new IOException("UNINSTALL_DIAGNOSTIC_CHANGED");
        }
    }
    internal static IDisposable PinSingleServiceDirectory(string path,bool immutable)
    {var pin=Pin(path,true,false);try{ValidateAcl(Security(pin.Leaf),ApplicationSid(),immutable);return pin;}catch{pin.Dispose();throw;}}
    internal static IDisposable PinInstallerRemovalRoot(string path,SecurityIdentifier application)
    {var pin=Pin(path,true,false);try{ValidateAcl(Security(pin.Leaf),application,true);return pin;}catch{pin.Dispose();throw;}}
    internal static void ValidateRemovalMember(string path,bool directory,SecurityIdentifier application,bool requireDeleteAccess=false)
    {using var pin=Pin(path,directory,false,requireDeleteAccess);ValidateAcl(Security(pin.Leaf),application,false);}
    internal static void DeleteRemovalMember(string path,bool directory,SecurityIdentifier application)
    {
        using(var pin=Pin(path,directory,false,true))
        {ValidateAcl(Security(pin.Leaf),application,false);byte disposition=1;if(!SetFileInformationByHandle(pin.Leaf,4,ref disposition,1))throw new Win32Exception();}
    }
    internal static void ValidateInitialSeedAcl(RawSecurityDescriptor acl,SecurityIdentifier service)
    {
        var copy=new RawSecurityDescriptor(acl.GetSddlForm(AccessControlSections.All));
        if(copy.DiscretionaryAcl is null)throw new IOException("FIRST_SEED_ACL");
        foreach(GenericAce entry in copy.DiscretionaryAcl)
        {if(entry is CommonAce ace&&ace.AceQualifier==AceQualifier.AccessAllowed&&ace.SecurityIdentifier==service){if((ace.AccessMask&~0x001300a9)!=0)throw new IOException("FIRST_SEED_WRITABLE");ace.AccessMask&=~0x10000;}}
        ValidateAcl(copy,service,true);
    }
    internal static byte[] ReadInitialSeed(string path)
    {var pin=Pin(path,false,true);using var lease=new PrivateReadLease(pin);ValidateInitialSeedAcl(Security(pin.Leaf),ApplicationSid());return lease.Small(4096);}
    internal static void ConsumeInitialSeed(string path,string expectedDigest)
    {
        var pin=Pin(path,false,true,true);using(var lease=new PrivateReadLease(pin))
        {ValidateInitialSeedAcl(Security(pin.Leaf),ApplicationSid());if(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(lease.Small(4096)))!=expectedDigest)throw new IOException("FIRST_SEED_CHANGED");
        byte disposition=1;if(!SetFileInformationByHandle(pin.Leaf,4,ref disposition,1))throw new Win32Exception();}
        if(File.Exists(path))throw new IOException("FIRST_SEED_NOT_CONSUMED");
    }
    [DllImport("kernel32.dll",SetLastError=true)]private static extern bool SetFileInformationByHandle(SafeFileHandle file,int kind,ref byte value,int bytes);

    // Recovery reads use the same pinned path, no-follow, link-count and ACL
    // checks as secrets, with bounded streaming rather than loading media in RAM.
    internal static PrivateReadLease OpenInstalledRoleRead(string path,InstalledFileRoles roles,InstalledFileRole role)
    {
        var pinned=Pin(path,false,true);try{roles.Validate(Security(pinned.Leaf),role,false);return new(pinned);}catch{pinned.Dispose();throw;}
    }
    internal static IDisposable PinInstalledRoleDirectory(string path,InstalledFileRoles roles,InstalledFileRole role)
    {var pinned=Pin(path,true,false);try{roles.Validate(Security(pinned.Leaf),role);return pinned;}catch{pinned.Dispose();throw;}}
    internal static bool HasExactInstalledRoleAccess(string path,bool directory,InstalledFileRoles roles,InstalledFileRole role,string expected)
    {
        using var pin=Pin(path,directory,!directory);var actual=Security(pin.Leaf);
        var wanted=new RawSecurityDescriptor(expected);
        try{roles.Validate(actual,role,directory);}catch(IOException){return false;}
        return SameRoleAccess(actual,wanted);
    }
    internal static bool SameRoleAccess(RawSecurityDescriptor actual,RawSecurityDescriptor wanted)
    {
        if((actual.ControlFlags&ControlFlags.DiscretionaryAclProtected)!=(wanted.ControlFlags&ControlFlags.DiscretionaryAclProtected)||actual.DiscretionaryAcl is null||wanted.DiscretionaryAcl is null)return false;
        static HashSet<(AceFlags,AceQualifier,int,string)> Entries(RawAcl acl)
        {
            var result=new HashSet<(AceFlags,AceQualifier,int,string)>();
            foreach(GenericAce raw in acl)
            {
                if(raw is not CommonAce ace||ace.IsCallback||!result.Add((ace.AceFlags,ace.AceQualifier,ace.AccessMask,ace.SecurityIdentifier.Value)))return [];
            }
            return result;
        }
        var observed=Entries(actual.DiscretionaryAcl);var expected=Entries(wanted.DiscretionaryAcl);
        return observed.Count==actual.DiscretionaryAcl.Count&&expected.Count==wanted.DiscretionaryAcl.Count&&observed.SetEquals(expected);
    }
    // Installer-owned full-profile reads/deletion. This does not relax the
    // ordinary role boundary. Only the fixed live Owner closure/Setup image
    // may carry the explicitly selected original Owner's additional RX ACE.
    internal static void ValidateInstallerRoleAcl(RawSecurityDescriptor descriptor,InstalledFileRoles roles,InstalledFileRole role,bool directory,SecurityIdentifier? readonlyOwner=null,bool seed=false)
    {
        if(seed){if(directory||readonlyOwner is not null)throw new IOException("UPDATE_SEED_KIND");ValidateInitialSeedAcl(descriptor,roles.Runtime);return;}
        if(readonlyOwner is null){roles.Validate(descriptor,role,directory);return;}
        if(role is not(InstalledFileRole.Control or InstalledFileRole.Anchor)||!readonlyOwner.IsAccountSid()||!readonlyOwner.Value.StartsWith("S-1-5-21-",StringComparison.Ordinal))throw new IOException("UPDATE_OWNER_CLOSURE_ACCESS");
        var copy=new RawSecurityDescriptor(descriptor.GetSddlForm(AccessControlSections.Owner|AccessControlSections.Access));
        if(copy.DiscretionaryAcl is null)throw new IOException("UPDATE_OWNER_CLOSURE_ACCESS");var found=false;
        for(var i=copy.DiscretionaryAcl.Count-1;i>=0;i--)
            if(copy.DiscretionaryAcl[i] is CommonAce ace&&ace.SecurityIdentifier==readonlyOwner)
            {if(ace.IsCallback||ace.AceQualifier!=AceQualifier.AccessAllowed||(ace.AccessMask&~0x1200a9)!=0)throw new IOException("UPDATE_OWNER_CLOSURE_ACCESS");found=true;copy.DiscretionaryAcl.RemoveAce(i);}
        if(!found)throw new IOException("UPDATE_OWNER_CLOSURE_ACCESS");roles.Validate(copy,role,directory);
    }
    internal static PrivateReadLease OpenInstallerRoleRead(string path,InstalledFileRoles roles,InstalledFileRole role,SecurityIdentifier? readonlyOwner=null,bool seed=false,bool administrativeOwner=false)
    {var pin=Pin(path,false,true);try{var acl=Security(pin.Leaf);if(administrativeOwner&&acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("UPDATE_INSTALL_METADATA_OWNER");ValidateInstallerRoleAcl(acl,roles,role,false,readonlyOwner,seed);return new(pin);}catch{pin.Dispose();throw;}}
    internal static IDisposable PinInstallerRoleDirectory(string path,InstalledFileRoles roles,InstalledFileRole role,SecurityIdentifier? readonlyOwner=null)
    {var pin=Pin(path,true,false);try{ValidateInstallerRoleAcl(Security(pin.Leaf),roles,role,true,readonlyOwner);return pin;}catch{pin.Dispose();throw;}}
    internal static void DeleteInstallerRoleMember(string path,bool directory,InstalledFileRoles roles,InstalledFileRole role,SecurityIdentifier? readonlyOwner=null,bool seed=false,bool administrativeOwner=false)
    {
        using var pin=Pin(path,directory,false,true);var acl=Security(pin.Leaf);if(administrativeOwner&&acl.Owner?.Value is not("S-1-5-18" or "S-1-5-32-544"))throw new IOException("UPDATE_INSTALL_METADATA_OWNER");ValidateInstallerRoleAcl(acl,roles,role,directory,readonlyOwner,seed);
        byte disposition=1;if(!SetFileInformationByHandle(pin.Leaf,4,ref disposition,1))throw new Win32Exception();
    }
    internal static FileStream CreateInstalledRoleFile(string path,InstalledFileRoles roles,InstalledFileRole role,InstalledFileRole parentRole)
    {
        // Applied at creation only; never repairs an existing file's ACL.
        using var parent=PinInstalledRoleDirectory(Path.GetDirectoryName(path)!,roles,parentRole);
        var current=ApplicationSid();if(!roles.IsUpdater(current)&&current!=roles.Runtime)throw new IOException("INSTALLED_CREATOR_ROLE");
        var runtime=InstalledFileRoles.RuntimeMask(role)==0?"":$"(A;;0x{InstalledFileRoles.RuntimeMask(role):x};;;{roles.Runtime.Value})";
        var authority=$"0x{roles.UpdaterMask(role):x}";
        var updater=string.Concat(roles.UpdaterActors.Select(sid=>$"(A;;{authority};;;{sid.Value})"));
        var sddl=$"O:{current.Value}D:P(A;;FA;;;SY)(A;;FA;;;BA){updater}{runtime}";
        var acl=new RawSecurityDescriptor(sddl);roles.Validate(acl,role,false);var bytes=new byte[acl.BinaryLength];acl.GetBinaryForm(bytes,0);
        var descriptor=GCHandle.Alloc(bytes,GCHandleType.Pinned);var attributes=new CreationSecurity{Length=Marshal.SizeOf<CreationSecurity>(),Descriptor=descriptor.AddrOfPinnedObject()};
        var pointer=Marshal.AllocHGlobal(attributes.Length);
        try
        {
            Marshal.StructureToPtr(attributes,pointer,false);var handle=Native.CreateFileW(Extended(path),0x40000000|ReadControl,0,pointer,1,0x80200080,IntPtr.Zero);
            if(handle.IsInvalid){handle.Dispose();throw new Win32Exception();}try{CheckKind(handle,false);roles.Validate(Security(handle),role,false);return new FileStream(handle,FileAccess.Write,4096,false);}catch{handle.Dispose();throw;}
        }
        finally{Marshal.FreeHGlobal(pointer);descriptor.Free();}
    }
    [StructLayout(LayoutKind.Sequential)]private struct CreationSecurity{public int Length;public IntPtr Descriptor;public int Inherit;}
    internal static IDisposable PinLiveDataFile(string path)
    {
        // Metadata-only, share read/write but never delete: compatible with a
        // live SQLite writer while preventing a missing-DB completion race.
        var installed=InstalledProcessState.ForPath(path);
        var pin=Pin(path,false,false);try{if(installed is null)ValidateAcl(Security(pin.Leaf),ApplicationSid(),false);else installed.Roles!.Validate(Security(pin.Leaf),installed.Role(path),false);return pin;}catch{pin.Dispose();throw;}
    }
    internal static PrivateReadLease OpenPrivateRead(string path)
    {
        var installed=InstalledProcessState.ForPath(path);if(installed is not null)return installed.Open(path);
        var pinned = Pin(path, false, true);
        try { ValidateAcl(Security(pinned.Leaf), ApplicationSid(), false); return new(pinned); }
        catch { pinned.Dispose(); throw; }
    }
    // Only the Authority-created, generated private SQLite probe scope may bypass
    // installed-role autodiscovery. It still uses no-follow native handles and
    // exact private ACL validation, never an environment/CLI trust switch.
    private static void OwnedPrivateProbePath(string root,string path)
    {
        var canonical=CanonicalLocalPath(Path.GetFullPath(root));var child=CanonicalLocalPath(Path.GetFullPath(path));
        if(!child.Equals(canonical,StringComparison.OrdinalIgnoreCase)&&!child.StartsWith(canonical+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new IOException("RECOVERY_PROBE_SCOPE");
        if(!Path.GetFileName(canonical).StartsWith("yahad-fixture-",StringComparison.Ordinal)||!Guid.TryParseExact(Path.GetFileName(canonical)[14..],"N",out _))throw new IOException("RECOVERY_PROBE_SCOPE");
        using var pinned=Pin(canonical,true,false);ValidateAcl(Security(pinned.Leaf),ApplicationSid(),false);
    }
    internal static PrivateReadLease OpenOwnedPrivateProbeRead(string root,string path)
    {
        OwnedPrivateProbePath(root,path);var pinned=Pin(path,false,true);
        try{ValidateAcl(Security(pinned.Leaf),ApplicationSid(),false);return new(pinned);}catch{pinned.Dispose();throw;}
    }
    internal static IDisposable PinOwnedPrivateProbeDirectory(string root,string path)
    {
        OwnedPrivateProbePath(root,path);var pinned=Pin(path,true,false);
        try{ValidateAcl(Security(pinned.Leaf),ApplicationSid(),false);return pinned;}catch{pinned.Dispose();throw;}
    }
    internal static DataLease AcquireOwnedPrivateProbeLease(string root)
    {OwnedPrivateProbePath(root,root);return AcquireDataLeaseFor(root,ApplicationSid(),null);}
    internal static PrivateReadLease OpenInstalledRead(string path)
    {
        var pinned=Pin(path,false,true);
        try{ValidateAcl(Security(pinned.Leaf),ApplicationSid(),true);return new(pinned);}
        catch{pinned.Dispose();throw;}
    }
    internal static IDisposable PinPrivateDirectory(string path)
    {
        var installed=InstalledProcessState.ForPath(path);if(installed is not null)return installed.PinDirectory(path);
        var pinned = Pin(path, true, false);
        try { ValidateAcl(Security(pinned.Leaf), ApplicationSid(), false); return pinned; }
        catch { pinned.Dispose(); throw; }
    }
    internal sealed class PrivateReadLease(PinnedPath pinned) : IDisposable
    {
        public long Length { get { if (!Native.GetFileSizeEx(pinned.Leaf, out var length)) throw new Win32Exception(); return length; } }
        public int Read(long offset, Span<byte> bytes) => RandomAccess.Read(pinned.Leaf, bytes, offset);
        public byte[] Small(int maximum)
        {
            var length=Length; if(length<1||length>maximum)throw new IOException("RECOVERY_FILE_BOUND");
            var bytes=new byte[(int)length];var offset=0;
            while(offset<bytes.Length){var count=Read(offset,bytes.AsSpan(offset));if(count==0)throw new IOException("RECOVERY_SHORT_READ");offset+=count;}
            return bytes;
        }
        internal string Sha256(long maximum)
        {
            var length=Length;if(length<1||length>maximum)throw new IOException("RECOVERY_FILE_BOUND");
            using var hash=System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer=new byte[1048576];long offset=0;
            while(offset<length){var count=Read(offset,buffer.AsSpan(0,(int)Math.Min(buffer.Length,length-offset)));if(count==0)throw new IOException("RECOVERY_SHORT_READ");hash.AppendData(buffer.AsSpan(0,count));offset+=count;}
            if(Length!=length)throw new IOException("RECOVERY_FILE_CHANGED");return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        public void Dispose()=>pinned.Dispose();
    }

    internal static DataLease AcquireInstalledDataLease(string directory,InstalledFileRoles roles,InstalledFileRole directoryRole=InstalledFileRole.Data)
    {
        var pinned=Pin(directory,true,false);SafeFileHandle? gate=null;
        try
        {
            roles.Validate(Security(pinned.Leaf),directoryRole);
            var count=0;
            void Walk(string parent,int depth)
            {
                if(depth>128)throw new IOException("NATIVE_TREE_DEPTH");
                foreach(var name in System.IO.Directory.EnumerateFileSystemEntries(parent))
                {
                    if(++count>100000)throw new IOException("NATIVE_TREE_COUNT");
                    var directoryEntry=(File.GetAttributes(name)&FileAttributes.Directory)!=0;
                    using var entry=Pin(name,directoryEntry,false);
                    roles.Validate(Security(entry.Leaf),directoryRole==InstalledFileRole.Data?(directoryEntry||Path.GetFileName(name)!="process.lock"?InstalledFileRole.Data:InstalledFileRole.Lease):directoryRole,directoryEntry);
                    if(directoryEntry)Walk(name,depth+1);
                }
            }
            Walk(directory,0);
            gate=Native.CreateFileW(Extended(Path.Combine(directory,"process.lock")),GenericRead|0x40000000|ReadControl,0,IntPtr.Zero,4,0x00200000,IntPtr.Zero);
            if(gate.IsInvalid)throw new Win32Exception();CheckKind(gate,false);roles.Validate(Security(gate),directoryRole==InstalledFileRole.Data?InstalledFileRole.Lease:directoryRole,false);return new(pinned,gate,null);
        }
        catch{gate?.Dispose();pinned.Dispose();throw;}
    }

    public static DataLease AcquireDataLease(string directory, int? ownerProcessId = null)
    {
        var installed=InstalledProcessState.ForPath(directory);if(installed is not null){if(ownerProcessId is not null)throw new IOException("INSTALLED_LEASE_REQUIRES_HOST_HANDOFF");return installed.Lease(directory);}
        return AcquireDataLeaseFor(directory,ApplicationSid(),ownerProcessId);
    }

    public static DataLease AcquireInstallerDataLease(string directory,SecurityIdentifier application)
    {
        using var identity=WindowsIdentity.GetCurrent();
        if(!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))throw new UnauthorizedAccessException("ADMIN_REQUIRED");
        if(!application.Value.StartsWith("S-1-5-80-",StringComparison.Ordinal))throw new IOException("INSTALL_SERVICE_IDENTITY");
        return AcquireDataLeaseFor(directory,application,null);
    }
    private static DataLease AcquireDataLeaseFor(string directory,SecurityIdentifier application,int? ownerProcessId)
    {
        var pinned = Pin(directory, leafDirectory: true, readContents: false);
        SafeFileHandle? gate = null;
        try
        {
            ValidateAcl(Security(pinned.Leaf), application, false);
            AssertPrivateTree(directory,application);
            var lockPath = Path.Combine(directory, "process.lock");
            // OPEN_ALWAYS + OPEN_REPARSE_POINT: atomically create/open without following a final link.
            gate = Native.CreateFileW(Extended(lockPath), GenericRead | 0x40000000 | ReadControl, 0, IntPtr.Zero,
                4, 0x00200000, IntPtr.Zero);
            if (gate.IsInvalid) throw new Win32Exception();
            CheckKind(gate, false);
            ValidateAcl(Security(gate), application, false);
            var remote = ownerProcessId.HasValue ? new RemoteGate(gate, ownerProcessId.Value) : null;
            return new(pinned, gate, remote);
        }
        catch { gate?.Dispose(); pinned.Dispose(); throw; }
    }

    private static void AssertPrivateTree(string root,SecurityIdentifier? application=null)
    {
        var seen = 0;
        void Walk(string directory, int depth)
        {
            if (depth > 128) throw new IOException("NATIVE_TREE_DEPTH");
            // The caller holds this directory and all of its ancestors open.
            // Reuse that stable chain rather than reopening it for every media file.
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                if (++seen > 100000) throw new IOException("NATIVE_TREE_LIMIT");
                CanonicalName(child);
                var isDirectory = (File.GetAttributes(child) & FileAttributes.Directory) != 0;
                using var file = Open(child, isDirectory, false);
                ValidateAcl(Security(file), application??ApplicationSid(), false);
                if (isDirectory) Walk(child, depth + 1);
            }
        }
        Walk(root, 0);
    }

    public static string CanonicalLocalPath(string path)
    {
        CanonicalName(path);
        var volume = Path.GetPathRoot(path)!;
        if (!Native.GetVolumeInformationW(volume, null, 0, out _, out _, out var flags, null, 0) || (flags & 8) == 0)
            throw new IOException("NATIVE_PERSISTENT_ACL_REQUIRED");
        return path;
    }

    private static void CanonicalName(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
            !char.IsAsciiLetter(path[0]) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("NATIVE_LOCAL_PATH");
        foreach (var part in path[3..].Split('\\'))
        {
            var device = part.Split('.')[0].ToUpperInvariant();
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || device is "CON" or "PRN" or "AUX" or "NUL" ||
                System.Text.RegularExpressions.Regex.IsMatch(device, "^(COM|LPT)[0-9]$"))
                throw new IOException("NATIVE_LOCAL_PATH");
        }
    }

    private static PinnedPath Pin(string path, bool leafDirectory, bool readContents,bool delete=false)
    {
        CanonicalLocalPath(path);
        var result = new PinnedPath();
        try
        {
            var prefix = Path.GetPathRoot(path)!;
            var parts = path[3..].Split('\\');
            // Holding every ancestor without FILE_SHARE_DELETE prevents rename/replacement
            // between its no-follow check and opening the next component.
            result.Handles.Add(Open(prefix, true, false));
            for (var i = 0; i < parts.Length; i++)
            {
                prefix = Path.Combine(prefix, parts[i]);
                result.Handles.Add(Open(prefix, i < parts.Length - 1 || leafDirectory, i == parts.Length - 1 && readContents,i==parts.Length-1&&delete));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }

    private static string Extended(string canonical) => @"\\?\" + canonical;

    private static SafeFileHandle Open(string path, bool directory, bool contents,bool delete=false)
    {
        var handle = Native.CreateFileW(Extended(path), ReadControl | ReadAttributes | (contents ? GenericRead : 0) | (delete?0x10000u:0),
            contents ? 1u : 3u, IntPtr.Zero, 3, 0x00200000 | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastPInvokeError(); handle.Dispose(); throw new Win32Exception(error); }
        try { CheckKind(handle, directory); return handle; }
        catch { handle.Dispose(); throw; }
    }

    private static void CheckKind(SafeFileHandle handle, bool directory)
    {
        if (!Native.GetFileInformationByHandle(handle, out var info)) throw new Win32Exception();
        if ((info.Attributes & Reparse) != 0 || ((info.Attributes & DirectoryAttribute) != 0) != directory || !directory && info.Links != 1)
            throw new IOException("NATIVE_REPARSE_KIND_OR_HARDLINK");
    }

    private static RawSecurityDescriptor Security(SafeFileHandle handle)
    {
        var error = Native.GetSecurityInfo(handle, 1, 5, out _, out _, out _, out _, out var descriptor);
        if (error != 0) throw new Win32Exception((int)error);
        try
        {
            var length = Native.GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 65536) throw new IOException("NATIVE_DACL_SIZE");
            var bytes = new byte[length]; Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally { Native.LocalFree(descriptor); }
    }

    private static SecurityIdentifier ApplicationSid()
    {
        var sid = WindowsIdentity.GetCurrent().User ?? throw new IOException("NATIVE_IDENTITY");
        if (sid.IsWellKnown(WellKnownSidType.LocalSystemSid) || sid.IsWellKnown(WellKnownSidType.LocalServiceSid) ||
            sid.IsWellKnown(WellKnownSidType.NetworkServiceSid)) throw new IOException("NATIVE_SHARED_SERVICE_IDENTITY");
        return sid;
    }

    internal sealed class PinnedPath : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        internal SafeFileHandle Leaf => Handles[^1];
        public void Dispose() { for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }
    public sealed class SecretLease : IDisposable
    {
        private readonly PinnedPath pinned; private readonly int maximum;
        internal SecretLease(PinnedPath pinned, int maximum) { this.pinned = pinned; this.maximum = maximum; }
        public byte[] Read()
        {
            if (!Native.GetFileSizeEx(pinned.Leaf, out var length)) throw new Win32Exception();
            if (length < 1 || length > maximum) throw new IOException("NATIVE_SECRET_SIZE");
            var bytes = new byte[length]; var offset = 0;
            while (offset < bytes.Length)
            { var count = RandomAccess.Read(pinned.Leaf, bytes.AsSpan(offset), offset); if (count == 0) throw new IOException("NATIVE_SHORT_READ"); offset += count; }
            return bytes;
        }
        public void Dispose() => pinned.Dispose();
    }
    public sealed class DataLease : IDisposable
    {
        private readonly PinnedPath pinned; private readonly SafeFileHandle gate;
        private readonly RemoteGate? remote;
        internal DataLease(PinnedPath pinned, SafeFileHandle gate, RemoteGate? remote) { this.pinned = pinned; this.gate = gate; this.remote = remote; }
        // Metadata-only inspection of THIS retained exclusive gate. A normal
        // contents read would conflict with our own FILE_SHARE_NONE handle.
        // Bind both its canonical parent and single-link file identity; never
        // exempt another file merely because it is named process.lock.
        internal bool TryValidateHeldGate(string path,Action<RawSecurityDescriptor> validate)
        {
            CanonicalLocalPath(path);
            if(!Path.GetFileName(path).Equals("process.lock",StringComparison.OrdinalIgnoreCase))return false;
            if(gate.IsClosed||gate.IsInvalid)throw new IOException("NATIVE_LEASE_CLOSED");
            using var parent=Pin(Path.GetDirectoryName(path)!,true,false);
            if(!Native.GetFileInformationByHandle(pinned.Leaf,out var heldParent)||!Native.GetFileInformationByHandle(parent.Leaf,out var candidateParent))throw new Win32Exception();
            if(heldParent.Volume!=candidateParent.Volume||heldParent.IndexHigh!=candidateParent.IndexHigh||heldParent.IndexLow!=candidateParent.IndexLow)return false;
            using var candidate=Pin(path,false,false);CheckKind(gate,false);
            if(!Native.GetFileInformationByHandle(gate,out var held)||!Native.GetFileInformationByHandle(candidate.Leaf,out var actual))throw new Win32Exception();
            if(held.Volume!=actual.Volume||held.IndexHigh!=actual.IndexHigh||held.IndexLow!=actual.IndexLow)throw new IOException("NATIVE_LEASE_FILE_BINDING");
            validate(Security(gate));return true;
        }
        internal DataLease DuplicateForDirectory(string directory)
        {
            if(gate.IsClosed||gate.IsInvalid)throw new IOException("NATIVE_LEASE_CLOSED");
            using var expected=Pin(directory,true,false);
            if(!Native.GetFileInformationByHandle(pinned.Leaf,out var actual)||!Native.GetFileInformationByHandle(expected.Leaf,out var wanted))throw new Win32Exception();
            if(actual.Volume!=wanted.Volume||actual.IndexHigh!=wanted.IndexHigh||actual.IndexLow!=wanted.IndexLow)throw new IOException("NATIVE_LEASE_DIRECTORY_BINDING");
            var duplicates=new PinnedPath();SafeFileHandle? gateCopy=null;
            SafeFileHandle Copy(SafeFileHandle original)
            {
                var added=false;try{original.DangerousAddRef(ref added);if(!Native.DuplicateHandle(Native.GetCurrentProcess(),original.DangerousGetHandle(),Native.GetCurrentProcess(),out var copied,0,false,2))throw new Win32Exception();return new(copied,true);}
                finally{if(added)original.DangerousRelease();}
            }
            try{foreach(var handle in pinned.Handles)duplicates.Handles.Add(Copy(handle));gateCopy=Copy(gate);return new(duplicates,gateCopy,null);}
            catch{gateCopy?.Dispose();duplicates.Dispose();throw;}
        }
        internal NativeDataLeaseTransfer DuplicateNativeHandlesForProcess(SafeProcessHandle target)
        {
            if(gate.IsClosed||gate.IsInvalid)throw new IOException("NATIVE_LEASE_CLOSED");
            var receiver=NativeCustodyTransfer.RequireTarget(target);
            var handles=NativeCustodyTransfer.DuplicateTo(target,pinned.Handles.Cast<SafeHandle>().Append(gate).ToArray());
            return new(receiver.Pid,receiver.Created,handles[..^1],handles[^1]);
        }
        // Readonly witness: no ownership transfers and these numbers must never
        // be adopted directly. The actual owner retains this lease; observers
        // duplicate from its pinned process before normal kernel validation.
        internal NativeDataLeaseTransfer BorrowNativeWitness()
        {
            if(gate.IsClosed||gate.IsInvalid)throw new IOException("NATIVE_LEASE_CLOSED");var self=NativeCustodyTransfer.CurrentIdentity();
            return new(self.Pid,self.Created,pinned.Handles.Select(h=>unchecked((ulong)h.DangerousGetHandle().ToInt64())).ToArray(),unchecked((ulong)gate.DangerousGetHandle().ToInt64()));
        }
        internal static DataLease AdoptTransferred(string directory,NativeDataLeaseTransfer transfer)
        {
            NativeCustodyTransfer.RequireReceiver(transfer.TargetPid,transfer.TargetCreated);
            if(transfer.Ancestors is null||transfer.Ancestors.Length is <1 or >128||transfer.Ancestors.Append(transfer.Gate).Distinct().Count()!=transfer.Ancestors.Length+1)throw new IOException("CUSTODY_LEASE_HANDLES");
            var owned=new PinnedPath();SafeFileHandle? gate=null;
            try
            {
                foreach(var value in transfer.Ancestors)owned.Handles.Add(new(NativeCustodyTransfer.Pointer(value),true));
                gate=new(NativeCustodyTransfer.Pointer(transfer.Gate),true);
                using var expected=Pin(directory,true,false);
                if(expected.Handles.Count!=owned.Handles.Count)throw new IOException("CUSTODY_LEASE_ANCESTORS");
                void Same(SafeFileHandle first,SafeFileHandle second)
                {
                    if(!Native.GetFileInformationByHandle(first,out var a)||!Native.GetFileInformationByHandle(second,out var b))throw new Win32Exception();
                    if(a.Volume!=b.Volume||a.IndexHigh!=b.IndexHigh||a.IndexLow!=b.IndexLow)throw new IOException("CUSTODY_LEASE_FILE_BINDING");
                }
                for(var i=0;i<owned.Handles.Count;i++){CheckKind(owned.Handles[i],true);Same(owned.Handles[i],expected.Handles[i]);}
                CheckKind(gate,false);var installed=InstalledProcessState.ForPath(directory)??throw new IOException("CUSTODY_INSTALLED_LEASE_ONLY");
                installed.Roles!.Validate(Security(gate),InstalledFileRole.Lease,false);
                using(var lockFile=Pin(Path.Combine(directory,"process.lock"),false,false))Same(gate,lockFile.Leaf);
                using(var contender=Native.CreateFileW(Extended(Path.Combine(directory,"process.lock")),GenericRead|0x40000000,0,IntPtr.Zero,3,0x00200000,IntPtr.Zero))
                {if(!contender.IsInvalid||Marshal.GetLastPInvokeError()!=32)throw new IOException("CUSTODY_LEASE_NOT_EXCLUSIVE");}
                return new(owned,gate,null);
            }
            catch{gate?.Dispose();owned.Dispose();throw;}
        }
        internal ulong DuplicateTo(SafeProcessHandle child)
        {
            if(gate.IsClosed||child.IsInvalid||child.IsClosed)throw new IOException("NATIVE_HANDOFF_CLOSED");
            RequireSameIdentity(child);
            if(!Native.DuplicateHandle(Native.GetCurrentProcess(),gate.DangerousGetHandle(),child.DangerousGetHandle(),out var copy,0,false,2))throw new Win32Exception();
            return unchecked((ulong)copy.ToInt64()); // child owns this noninheritable duplicate until exit
        }
        public void Dispose() { try { remote?.Dispose(); } finally { gate.Dispose(); pinned.Dispose(); } }
    }

    private static void RequireSameIdentity(SafeProcessHandle owner)
    {
        if(!Native.OpenProcessToken(owner,8,out var token))throw new Win32Exception();
        using(token)using(var identity=new WindowsIdentity(token.DangerousGetHandle()))
            if(identity.User?.Equals(ApplicationSid())!=true)throw new IOException("NATIVE_LOCK_OWNER_IDENTITY");
    }
    public static void ValidateHostLease(string descriptor,string directory,int ownerPid,string nonce)
    {
        using var file=OpenPrivateRead(descriptor);
        var grant=NativeJson.Read<HostLeaseGrant>(file.Small(4096));
        if(grant.Format!=1||grant.ProcessId!=ownerPid||ownerPid<1||grant.Nonce!=nonce||nonce.Length!=64||
            !string.Equals(grant.DataDirectory,directory,StringComparison.OrdinalIgnoreCase)||grant.Handle==0||grant.Handle>long.MaxValue)throw new IOException("NATIVE_HANDOFF_IDENTITY");
        using var owner=Native.OpenProcess(0x1000|0x40,false,ownerPid);if(owner.IsInvalid)throw new Win32Exception();RequireSameIdentity(owner);
        if(!Native.DuplicateHandle(owner.DangerousGetHandle(),(IntPtr)(long)grant.Handle,Native.GetCurrentProcess(),out var local,0,false,2))throw new Win32Exception();
        using var duplicate=new SafeFileHandle(local,true);CheckKind(duplicate,false);var installed=InstalledProcessState.ForPath(directory);if(installed is null)ValidateAcl(Security(duplicate),ApplicationSid(),false);else installed.Roles!.Validate(Security(duplicate),InstalledFileRole.Lease,false);
        using var expected=Pin(Path.Combine(directory,"process.lock"),false,false);
        if(!Native.GetFileInformationByHandle(duplicate,out var actual)||!Native.GetFileInformationByHandle(expected.Leaf,out var other))throw new Win32Exception();
        if(actual.Volume!=other.Volume||actual.IndexHigh!=other.IndexHigh||actual.IndexLow!=other.IndexLow)throw new IOException("NATIVE_HANDOFF_FILE");
        var contender=Native.CreateFileW(Extended(Path.Combine(directory,"process.lock")),GenericRead|0x40000000,0,IntPtr.Zero,3,0x00200000,IntPtr.Zero);
        using(contender){if(!contender.IsInvalid)throw new IOException("NATIVE_HANDOFF_NOT_LOCKED");if(Marshal.GetLastPInvokeError()!=32)throw new IOException("NATIVE_HANDOFF_UNOBSERVED");}
    }
    internal sealed class RemoteGate : IDisposable
    {
        private readonly SafeProcessHandle owner; private readonly IntPtr remote; private bool disposed;
        internal RemoteGate(SafeFileHandle gate, int processId)
        {
            if (processId < 1 || processId == Environment.ProcessId) throw new IOException("NATIVE_LOCK_OWNER");
            owner = Native.OpenProcess(0x00100000 | 0x1000 | 0x40, false, processId); // synchronize/query-limited/duplicate only
            try
            {
                if (owner.IsInvalid) throw new Win32Exception();
                if (!Native.OpenProcessToken(owner, 8, out var token)) throw new Win32Exception();
                using (token)
                using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
                    if (identity.User?.Equals(ApplicationSid()) != true) throw new IOException("NATIVE_LOCK_OWNER_IDENTITY");
                if (!Native.DuplicateHandle(Native.GetCurrentProcess(), gate.DangerousGetHandle(), owner.DangerousGetHandle(), out remote, 0, false, 2))
                    throw new Win32Exception();
                // The noninheritable duplicate lives in Node, not in this helper.
                // Helper death cannot open a second-writer window while Node is alive.
            }
            catch { owner.Dispose(); throw; }
        }
        public void Dispose()
        {
            if (disposed) return; disposed = true;
            try
            {
                var wait = Native.WaitForSingleObject(owner, 0);
                if (wait == 0) return; // Exited owner already closed its entire handle table.
                if (wait != 258 || !Native.DuplicateHandle(owner.DangerousGetHandle(), remote, IntPtr.Zero, out _, 0, false, 1))
                    throw new Win32Exception();
            }
            finally { owner.Dispose(); }
        }
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileInfo
        { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetVolumeInformationW(string root, System.Text.StringBuilder? name, uint nameSize,
            out uint serial, out uint maximum, out uint flags, System.Text.StringBuilder? fileSystem, uint fileSystemSize);
        [DllImport("advapi32.dll", SetLastError = true)]
        internal static extern uint GetSecurityInfo(SafeFileHandle handle, uint type, uint flags, out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);
        [DllImport("advapi32.dll")] internal static extern uint GetSecurityDescriptorLength(IntPtr descriptor);
        [DllImport("kernel32.dll")] internal static extern IntPtr LocalFree(IntPtr pointer);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint rights, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    }
}
