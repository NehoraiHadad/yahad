using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Yahad.Install;

internal enum UpdaterServiceActor { Authority, RecoveryA, RecoveryB }

// Read-only SCM binding for the three compiled updater actors. No arbitrary
// service/account/image/mask can be supplied by an IPC frame or plan.
internal static class UpdaterServiceIdentity
{
    internal static UpdaterServiceActor ForSlot(RecoveryHelperSlot slot) => slot switch
    {
        RecoveryHelperSlot.A => UpdaterServiceActor.RecoveryA,
        RecoveryHelperSlot.B => UpdaterServiceActor.RecoveryB,
        _ => throw new IOException("HELPER_SLOT")
    };
    internal static string Name(UpdaterServiceActor actor) => actor switch
    {
        UpdaterServiceActor.Authority => InstalledUpdaterLayout.AuthorityServiceName,
        UpdaterServiceActor.RecoveryA => BootstrapRecoveryLayout.ServiceName(RecoveryHelperSlot.A),
        UpdaterServiceActor.RecoveryB => BootstrapRecoveryLayout.ServiceName(RecoveryHelperSlot.B),
        _ => throw new IOException("UPDATER_ACTOR")
    };
    internal static string Image(UpdaterServiceActor actor) => actor switch
    {
        UpdaterServiceActor.Authority => InstalledUpdaterLayout.AuthorityExecutable,
        UpdaterServiceActor.RecoveryA => BootstrapRecoveryLayout.Image(RecoveryHelperSlot.A),
        UpdaterServiceActor.RecoveryB => BootstrapRecoveryLayout.Image(RecoveryHelperSlot.B),
        _ => throw new IOException("UPDATER_ACTOR")
    };
    internal static string Command(UpdaterServiceActor actor) => "\"" + Image(actor) + "\" " + (actor switch
    {
        UpdaterServiceActor.Authority => "--service",
        UpdaterServiceActor.RecoveryA => "--system-recovery-service A",
        UpdaterServiceActor.RecoveryB => "--system-recovery-service B",
        _ => throw new IOException("UPDATER_ACTOR")
    });
    internal static UpdaterServiceActor FromUser(SecurityIdentifier user, SecurityIdentifier authority)
    {
        if (user == authority) return UpdaterServiceActor.Authority;
        if (user == UpdaterServicePrincipals.RecoveryA) return UpdaterServiceActor.RecoveryA;
        if (user == UpdaterServicePrincipals.RecoveryB) return UpdaterServiceActor.RecoveryB;
        throw new UnauthorizedAccessException("CUSTODY_TARGET_ACCOUNT");
    }
    internal static void RequireObserved(UpdaterServiceActor actor, string account, string command, uint type, uint start,
        uint state, uint pid, int? expectedPid, bool stopped = false)
    {
        if (type != 16 || start != 2 || !string.Equals(account, @"NT SERVICE\" + Name(actor), StringComparison.OrdinalIgnoreCase) || command != Command(actor))
            throw new UnauthorizedAccessException("HELPER_BOOT_FIXED_CONFIGURATION");
        if (stopped && (state != 1 || pid != 0) || expectedPid is { } wanted && (pid != (uint)wanted || state is not (2 or 4)))
            throw new UnauthorizedAccessException("HELPER_BOOT_SCM_GENERATION");
    }
    internal static void Require(UpdaterServiceActor actor, int? expectedPid, bool stopped = false)
    {
        using var manager = new Handle(OpenSCManagerW(null, null, 1)); if (manager.IsInvalid) throw new Win32Exception();
        using var service = new Handle(OpenServiceW(manager, Name(actor), 5)); if (service.IsInvalid) throw new Win32Exception();
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var size);
        if (size is < 44 or > 16384) throw new IOException("HELPER_BOOT_CONFIG_BOUND");
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceConfigW(service, memory, size, out _)) throw new Win32Exception();
            var config = Marshal.PtrToStructure<Configuration>(memory);
            if (!QueryServiceStatusEx(service, 0, out var status, (uint)Marshal.SizeOf<Status>(), out _)) throw new Win32Exception();
            if (status.Type != 16) throw new UnauthorizedAccessException("HELPER_BOOT_SCM_GENERATION");
            RequireObserved(actor, Read(config.Account), Read(config.Image), config.Type, config.Start, status.State, status.Pid, expectedPid, stopped);
            string Read(IntPtr text)
            {
                var offset = text.ToInt64() - memory.ToInt64();
                if (text == IntPtr.Zero || offset < 0 || offset >= size || offset % 2 != 0) throw new IOException("HELPER_BOOT_CONFIG_POINTER");
                var chars = new List<char>();
                for (var i = offset; i + 2 <= size; i += 2)
                { var c = (char)Marshal.ReadInt16(memory, checked((int)i)); if (c == '\0') return new string(chars.ToArray()); chars.Add(c); }
                throw new IOException("HELPER_BOOT_CONFIG_STRING");
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration { public uint Type, Start, Error; public IntPtr Image, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] private struct Status { public uint Type, State, Accepted, Win32, Specific, Checkpoint, WaitHint, Pid, Flags; }
    private sealed class Handle : SafeHandleZeroOrMinusOneIsInvalid
    { internal Handle(IntPtr value) : base(true) { SetHandle(value); } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(Handle manager, string name, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfigW(Handle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(Handle service, int kind, out Status status, uint size, out uint needed);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr handle);
}
