using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

/// <summary>
/// Reaches a file by a path a less privileged account may have shaped: a link is
/// never followed, a hard link is refused, and no ancestor can be swapped while
/// the file is held. Who may write where is the job of the two inherited ACLs in
/// <see cref="NativeInstallFiles"/>; nothing here inspects them.
/// </summary>
public static class WindowsFileBoundary
{
    private const uint ReadAttributes = 0x80, GenericRead = 0x80000000, GenericWrite = 0x40000000, Delete = 0x10000;
    private const uint FollowNothing = 0x00200000, DirectoriesToo = 0x02000000;
    private const uint Reparse = 0x400, DirectoryAttribute = 0x10;

    public static ReadLease OpenRead(string path)
    {
        var pinned = Pin(path, false, true);
        return new(pinned);
    }

    internal static void RequireNoLink(string path, bool directory) { using var pin = Pin(path, directory, false); }

    internal static void DeleteNoFollow(string path, bool directory)
    {
        using var pin = Pin(path, directory, false, true);
        byte disposition = 1;
        if (!SetFileInformationByHandle(pin.Leaf, 4, ref disposition, 1)) throw new Win32Exception();
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int kind, ref byte value, int bytes);

    // Any user may create a folder in ProgramData, so a state root's owner is what
    // proves Setup made it: only an administrator can give a folder that owner.
    internal static void RequireAdministratorOwned(string directory)
    {
        using var pin = Pin(directory, true, false);
        var owner = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier));
        if (!IsAdministrativeOwner(owner as SecurityIdentifier)) throw new IOException("INSTALL_ROOT_OWNER");
    }
    internal static bool IsAdministrativeOwner(SecurityIdentifier? owner) =>
        owner is not null && (owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid) || owner.IsWellKnown(WellKnownSidType.LocalSystemSid));

    public sealed class ReadLease : IDisposable
    {
        private readonly PinnedPath pinned;
        internal ReadLease(PinnedPath pinned) { this.pinned = pinned; }
        public long Length { get { if (!Native.GetFileSizeEx(pinned.Leaf, out var length)) throw new Win32Exception(); return length; } }
        public int Read(long offset, Span<byte> bytes) => RandomAccess.Read(pinned.Leaf, bytes, offset);
        public byte[] Small(int maximum)
        {
            var length = Length; if (length < 1 || length > maximum) throw new IOException("NATIVE_FILE_BOUND");
            var bytes = new byte[(int)length]; var offset = 0;
            while (offset < bytes.Length) { var count = Read(offset, bytes.AsSpan(offset)); if (count == 0) throw new IOException("NATIVE_SHORT_READ"); offset += count; }
            return bytes;
        }
        internal string Sha256(long maximum)
        {
            var length = Length; if (length < 1 || length > maximum) throw new IOException("NATIVE_FILE_BOUND");
            using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
            var buffer = new byte[1048576]; long offset = 0;
            while (offset < length) { var count = Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset))); if (count == 0) throw new IOException("NATIVE_SHORT_READ"); hash.AppendData(buffer.AsSpan(0, count)); offset += count; }
            if (Length != length) throw new IOException("NATIVE_FILE_CHANGED"); return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        public void Dispose() => pinned.Dispose();
    }

    public static DataLease AcquireDataLease(string directory)
    {
        var pinned = Pin(directory, leafDirectory: true, readContents: false);
        SafeFileHandle? gate = null;
        try
        {
            // OPEN_ALWAYS + no-follow: atomically create or open without following a final link.
            gate = Native.CreateFileW(NativeInstallFiles.Extended(Path.Combine(directory, "process.lock")), GenericRead | GenericWrite, 0, IntPtr.Zero, 4, FollowNothing, IntPtr.Zero);
            if (gate.IsInvalid) throw new Win32Exception();
            CheckKind(gate, false);
            return new(pinned, gate);
        }
        catch { gate?.Dispose(); pinned.Dispose(); throw; }
    }

    public sealed class DataLease : IDisposable
    {
        private readonly PinnedPath pinned; private readonly SafeFileHandle gate;
        internal DataLease(PinnedPath pinned, SafeFileHandle gate) { this.pinned = pinned; this.gate = gate; }
        internal ulong DuplicateTo(SafeProcessHandle child)
        {
            if (gate.IsClosed || child.IsInvalid || child.IsClosed) throw new IOException("NATIVE_HANDOFF_CLOSED");
            if (!Native.DuplicateHandle(Native.GetCurrentProcess(), gate.DangerousGetHandle(), child.DangerousGetHandle(), out var copy, 0, false, 2)) throw new Win32Exception();
            return unchecked((ulong)copy.ToInt64()); // child owns this noninheritable duplicate until exit
        }
        public void Dispose() { gate.Dispose(); pinned.Dispose(); }
    }

    private static void RequireSameIdentity(SafeProcessHandle owner)
    {
        if (!Native.OpenProcessToken(owner, 8, out var token)) throw new Win32Exception();
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        using (var current = WindowsIdentity.GetCurrent())
            if (current.User is null || identity.User?.Equals(current.User) != true) throw new IOException("NATIVE_LOCK_OWNER_IDENTITY");
    }

    public static void ValidateHostLease(string descriptor, string directory, int ownerPid, string nonce)
    {
        using var file = OpenRead(descriptor);
        var grant = NativeJson.Read<HostLeaseGrant>(file.Small(4096));
        if (grant.Format != 1 || grant.ProcessId != ownerPid || ownerPid < 1 || grant.Nonce != nonce || nonce.Length != 64 ||
            !string.Equals(grant.DataDirectory, directory, StringComparison.OrdinalIgnoreCase) || grant.Handle == 0 || grant.Handle > long.MaxValue) throw new IOException("NATIVE_HANDOFF_IDENTITY");
        using var owner = Native.OpenProcess(0x1000 | 0x40, false, ownerPid); if (owner.IsInvalid) throw new Win32Exception(); RequireSameIdentity(owner);
        if (!Native.DuplicateHandle(owner.DangerousGetHandle(), (IntPtr)(long)grant.Handle, Native.GetCurrentProcess(), out var local, 0, false, 2)) throw new Win32Exception();
        using var duplicate = new SafeFileHandle(local, true); CheckKind(duplicate, false);
        using var expected = Pin(Path.Combine(directory, "process.lock"), false, false);
        if (!Native.GetFileInformationByHandle(duplicate, out var actual) || !Native.GetFileInformationByHandle(expected.Leaf, out var other)) throw new Win32Exception();
        if (actual.Volume != other.Volume || actual.IndexHigh != other.IndexHigh || actual.IndexLow != other.IndexLow) throw new IOException("NATIVE_HANDOFF_FILE");
        var contender = Native.CreateFileW(NativeInstallFiles.Extended(Path.Combine(directory, "process.lock")), GenericRead | GenericWrite, 0, IntPtr.Zero, 3, FollowNothing, IntPtr.Zero);
        using (contender) { if (!contender.IsInvalid) throw new IOException("NATIVE_HANDOFF_NOT_LOCKED"); if (Marshal.GetLastPInvokeError() != 32) throw new IOException("NATIVE_HANDOFF_UNOBSERVED"); }
    }

    // Pin walks a path one component at a time from its drive root.
    public static string CanonicalLocalPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Length < 4 || path[1] != ':' || path[2] != '\\' ||
            !char.IsAsciiLetter(path[0]) || !string.Equals(Path.GetFullPath(path), path, StringComparison.OrdinalIgnoreCase))
            throw new IOException("NATIVE_LOCAL_PATH");
        return path;
    }

    private static PinnedPath Pin(string path, bool leafDirectory, bool readContents, bool delete = false)
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
                result.Handles.Add(Open(prefix, i < parts.Length - 1 || leafDirectory, i == parts.Length - 1 && readContents, i == parts.Length - 1 && delete));
            }
            return result;
        }
        catch { result.Dispose(); throw; }
    }


    private static SafeFileHandle Open(string path, bool directory, bool contents, bool delete = false)
    {
        var handle = Native.CreateFileW(NativeInstallFiles.Extended(path), ReadAttributes | (contents ? GenericRead : 0) | (delete ? Delete : 0),
            contents ? 1u : 3u, IntPtr.Zero, 3, FollowNothing | DirectoriesToo, IntPtr.Zero);
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

    internal sealed class PinnedPath : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = [];
        internal SafeFileHandle Leaf => Handles[^1];
        public void Dispose() { for (var i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose(); }
    }

    internal static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct FileInfo
        { public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint rights, [MarshalAs(UnmanagedType.Bool)] bool inherit, int id);
        [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool OpenProcessToken(SafeProcessHandle process, uint rights, out SafeAccessTokenHandle token);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DuplicateHandle(IntPtr sourceProcess, IntPtr source, IntPtr targetProcess, out IntPtr target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    }
}
