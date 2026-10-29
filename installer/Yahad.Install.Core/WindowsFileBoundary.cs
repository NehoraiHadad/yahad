using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

public enum SecretRole { MasterKey, SetupCapability }

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
            var acl=Security(pin.Leaf);var admins=new SecurityIdentifier("S-1-5-32-544");var system=new SecurityIdentifier("S-1-5-18");
            if(acl.Owner!=admins&&acl.Owner!=system||acl.DiscretionaryAcl is null)throw new IOException("INSTALL_METADATA_OWNER");
            foreach(GenericAce raw in acl.DiscretionaryAcl)
            {
                if(raw is not CommonAce ace||ace.IsCallback)throw new IOException("INSTALL_METADATA_ACE");
                if(ace.AceQualifier==AceQualifier.AccessDenied)continue;
                if(ace.AceQualifier!=AceQualifier.AccessAllowed)throw new IOException("INSTALL_METADATA_ACE");
                if(ace.SecurityIdentifier==admins||ace.SecurityIdentifier==system)continue;
                if(!ace.SecurityIdentifier.Value.StartsWith("S-1-5-80-",StringComparison.Ordinal)||(ace.AccessMask&~0x1200a9)!=0)throw new IOException("INSTALL_METADATA_ACCESS");
            }
            return new(pin);
        }
        catch{pin.Dispose();throw;}
    }
    internal static IDisposable PinSingleServiceDirectory(string path,bool immutable)
    {var pin=Pin(path,true,false);try{ValidateAcl(Security(pin.Leaf),ApplicationSid(),immutable);return pin;}catch{pin.Dispose();throw;}}
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
    internal static FileStream CreateInstalledRoleFile(string path,InstalledFileRoles roles,InstalledFileRole role,InstalledFileRole parentRole)
    {
        // Applied at creation only; never repairs an existing file's ACL.
        using var parent=PinInstalledRoleDirectory(Path.GetDirectoryName(path)!,roles,parentRole);
        var current=ApplicationSid();if(current!=roles.Authority&&current!=roles.Runtime)throw new IOException("INSTALLED_CREATOR_ROLE");
        var runtime=role==InstalledFileRole.Recovery?"":$"(A;;{(role is InstalledFileRole.Data or InstalledFileRole.Lease?"0x1301bf":"0x1200a9")};;;{roles.Runtime.Value})";
        var authority=role is InstalledFileRole.Data or InstalledFileRole.MasterKey?"0x1200a9":"0x1301bf";
        var sddl=$"O:{current.Value}D:P(A;;FA;;;SY)(A;;FA;;;BA)(A;;{authority};;;{roles.Authority.Value}){runtime}";
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
        var pin=Pin(path,false,false);try{ValidateAcl(Security(pin.Leaf),ApplicationSid(),false);return pin;}catch{pin.Dispose();throw;}
    }
    internal static PrivateReadLease OpenPrivateRead(string path)
    {
        var installed=InstalledProcessState.ForPath(path);if(installed is not null)return installed.Open(path);
        var pinned = Pin(path, false, true);
        try { ValidateAcl(Security(pinned.Leaf), ApplicationSid(), false); return new(pinned); }
        catch { pinned.Dispose(); throw; }
    }
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
        var pinned = Pin(directory, leafDirectory: true, readContents: false);
        SafeFileHandle? gate = null;
        try
        {
            ValidateAcl(Security(pinned.Leaf), ApplicationSid(), false);
            AssertPrivateTree(directory);
            var lockPath = Path.Combine(directory, "process.lock");
            // OPEN_ALWAYS + OPEN_REPARSE_POINT: atomically create/open without following a final link.
            gate = Native.CreateFileW(Extended(lockPath), GenericRead | 0x40000000 | ReadControl, 0, IntPtr.Zero,
                4, 0x00200000, IntPtr.Zero);
            if (gate.IsInvalid) throw new Win32Exception();
            CheckKind(gate, false);
            ValidateAcl(Security(gate), ApplicationSid(), false);
            var remote = ownerProcessId.HasValue ? new RemoteGate(gate, ownerProcessId.Value) : null;
            return new(pinned, gate, remote);
        }
        catch { gate?.Dispose(); pinned.Dispose(); throw; }
    }

    private static void AssertPrivateTree(string root)
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
                ValidateAcl(Security(file), ApplicationSid(), false);
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
