using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using Microsoft.Win32.SafeHandles;
using Yahad.Service;

namespace Yahad.Setup;

// Installation adapter only. This class is not called by tests or --describe.
// Every opened service must match the exact fixed proxy account and image;
// collisions are never repurposed or silently repaired.
internal sealed class LanServiceRegistration : IDisposable
{
    private readonly ServiceHandle manager, service;
    private LanServiceRegistration(ServiceHandle manager, ServiceHandle service) { this.manager = manager; this.service = service; }
    internal static string ImageCommand => "\"" + LanProxyPaths.ServiceImage + "\" --lan-service";
    internal static string Account => "NT SERVICE\\" + LanProxyPaths.ServiceName;
    internal static bool Exists()
    {
        using var manager = new ServiceHandle(OpenSCManagerW(null, null, 1)); if (manager.IsInvalid) throw new Win32Exception();
        using var service = new ServiceHandle(OpenServiceW(manager, LanProxyPaths.ServiceName, 4)); if (!service.IsInvalid) return true;
        if (Marshal.GetLastPInvokeError() == 1060) return false; throw new Win32Exception();
    }
    internal static LanServiceRegistration Create(string ownerSid)
    {
        NativeInstallFiles.RequireAdministrator();
        if (!ownerSid.StartsWith("S-1-5-21-", StringComparison.Ordinal)) throw new IOException("LAN_OWNER_IDENTITY");
        // Parse before changing SCM so malformed SIDs cannot leave a service.
        var acl = new RawSecurityDescriptor($"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;0x00000005;;;{ownerSid})");
        var manager = new ServiceHandle(OpenSCManagerW(null, null, 3)); if (manager.IsInvalid) { manager.Dispose(); throw new Win32Exception(); }
        var service = new ServiceHandle(CreateServiceW(manager, LanProxyPaths.ServiceName, "Yahad home network HTTPS", 0xf01ff,
            16, 3, 1, ImageCommand, null, IntPtr.Zero, null, Account, null));
        if (service.IsInvalid) { service.Dispose(); manager.Dispose(); throw new Win32Exception(); }
        var result = new LanServiceRegistration(manager, service);
        try
        {
            var serviceSidType = 1u; Change(5, ref serviceSidType);
            var privilegeNames = Marshal.StringToHGlobalUni("SeChangeNotifyPrivilege\0\0");
            try { var privileges = new Privileges { Names = privilegeNames }; Change(6, ref privileges); }
            finally { Marshal.FreeHGlobal(privilegeNames); }
            var security = new byte[acl.BinaryLength]; acl.GetBinaryForm(security, 0);
            if (!SetServiceObjectSecurity(service, 4, security)) throw new Win32Exception();
            result.RequireOwned(); return result;
        }
        catch
        {
            // Only the handle just returned by CreateService may be removed.
            try { if (result.Status().State == 1) DeleteService(service); } catch { }
            result.Dispose(); throw;
        }
        void Change<T>(uint kind, ref T value) where T : struct
        {
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<T>());
            try { Marshal.StructureToPtr(value, memory, false); if (!ChangeServiceConfig2W(service, kind, memory)) throw new Win32Exception(); }
            finally { Marshal.FreeHGlobal(memory); }
        }
    }
    internal static LanServiceRegistration OpenOwned()
    {
        NativeInstallFiles.RequireAdministrator();
        var manager = new ServiceHandle(OpenSCManagerW(null, null, 1)); if (manager.IsInvalid) { manager.Dispose(); throw new Win32Exception(); }
        var service = new ServiceHandle(OpenServiceW(manager, LanProxyPaths.ServiceName, 0x10037));
        if (service.IsInvalid) { service.Dispose(); manager.Dispose(); throw new Win32Exception(); }
        var result = new LanServiceRegistration(manager, service);
        try { result.RequireOwned(); return result; } catch { result.Dispose(); throw; }
    }
    internal static void RequireConfiguration(uint type, string? account, string? image)
    {
        if (type != 16 || !string.Equals(account, Account, StringComparison.OrdinalIgnoreCase) || image != ImageCommand)
            throw new IOException("LAN_SERVICE_NOT_OWNED");
    }
    internal void RequireOwned()
    {
        QueryServiceConfigW(service, IntPtr.Zero, 0, out var size);
        if (size is <44 or >16384) throw new IOException("LAN_SERVICE_CONFIG_BOUND");
        var memory = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!QueryServiceConfigW(service, memory, size, out _)) throw new Win32Exception();
            var config = Marshal.PtrToStructure<Configuration>(memory);
            RequireConfiguration(config.Type, Marshal.PtrToStringUni(config.Account), Marshal.PtrToStringUni(config.Image));
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal void RequireStopped() { RequireOwned(); if (Status().State != 1) throw new IOException("LAN_SERVICE_NOT_QUIESCENT"); }
    internal void RequireCleanStop()
    {
        RequireOwned(); var state = Status();
        if (state.State != 1 || state.Pid != 0 || state.Win32 != 0 || state.Specific != 0) throw new IOException("LAN_QUIESCENCE_UNCONFIRMED");
    }
    internal void RequireConfirmedStop()
    {
        RequireOwned(); var state = Status();
        LanProxyPolicy.RequireConfirmedStopped(state.State, state.Pid, state.Win32, state.Specific);
    }
    internal void EnableAutomaticStart()
    {
        RequireOwned();
        if (!ChangeServiceConfigW(service, 0xffffffff, 2, 0xffffffff, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception();
        var memory = Marshal.AllocHGlobal(4);
        try { Marshal.WriteInt32(memory, 1); if (!ChangeServiceConfig2W(service, 3, memory)) throw new Win32Exception(); }
        finally { Marshal.FreeHGlobal(memory); }
    }
    internal void DisableAutomaticStart()
    {
        RequireOwned();
        if (!ChangeServiceConfigW(service, 0xffffffff, 3, 0xffffffff, null, null, IntPtr.Zero, null, null, null, null)) throw new Win32Exception();
    }
    internal async Task StartAsync(Action<int> observed, CancellationToken cancellation)
    {
        RequireOwned(); if (!StartServiceW(service, 0, IntPtr.Zero) && Marshal.GetLastPInvokeError() != 1056) throw new Win32Exception();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(50));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested(); var state = Status();
            if (state.State == 4) { if (state.Pid == 0) throw new IOException("LAN_SERVICE_PID_MISSING"); return; }
            if (state.State == 1) throw new IOException("LAN_SERVICE_START_FAILED");
            observed((int)state.Checkpoint); await Task.Delay(200, deadline.Token);
        }
    }
    internal async Task StopAsync(CancellationToken cancellation)
    {
        RequireOwned(); var before = Status(); using var process = before.Pid == 0 ? null : Process.GetProcessById((int)before.Pid);
        if (process is not null) _ = process.SafeHandle;
        if (before.State != 1 && !ControlService(service, 1, out _) && Marshal.GetLastPInvokeError() != 1062) throw new Win32Exception();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(35));
        while (Status().State != 1) await Task.Delay(200, deadline.Token);
        if (process is not null) await process.WaitForExitAsync(deadline.Token);
    }
    internal void Delete()
    {
        RequireOwned(); if (Status().State != 1) throw new IOException("LAN_SERVICE_NOT_QUIESCENT");
        if (!DeleteService(service)) throw new Win32Exception();
    }
    internal static async Task WaitUntilRemovedAsync(CancellationToken cancellation)
    {
        var elapsed = Stopwatch.StartNew();
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            try { if (!Exists()) return; } catch (Win32Exception error) when (error.NativeErrorCode == 1072) { }
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new IOException("LAN_SERVICE_REMOVAL_PENDING");
            await Task.Delay(100, cancellation);
        }
    }
    private StatusProcess Status()
    {
        if (!QueryServiceStatusEx(service, 0, out var state, (uint)Marshal.SizeOf<StatusProcess>(), out _)) throw new Win32Exception(); return state;
    }
    public void Dispose() { service.Dispose(); manager.Dispose(); }
    [StructLayout(LayoutKind.Sequential)] private struct Privileges { public IntPtr Names; }
    [StructLayout(LayoutKind.Sequential)] private struct Configuration { public uint Type, Start, Error; public IntPtr Image, Group; public uint Tag; public IntPtr Dependencies, Account, Display; }
    [StructLayout(LayoutKind.Sequential)] private struct StatusProcess { public uint Type, State, Accepted, Win32, Specific, Checkpoint, WaitHint, Pid, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct StatusBasic { public uint Type, State, Accepted, Win32, Specific, Checkpoint, WaitHint; }
    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    { internal ServiceHandle(IntPtr value) : base(true) { SetHandle(value); } protected override bool ReleaseHandle() => CloseServiceHandle(handle); }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManagerW(string? machine, string? database, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenServiceW(ServiceHandle manager, string name, uint rights);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateServiceW(ServiceHandle manager, string name, string display, uint rights, uint type, uint start, uint error, string image, string? group, IntPtr tag, string? dependencies, string account, string? password);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig2W(ServiceHandle service, uint kind, IntPtr info);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfigW(ServiceHandle service, uint type, uint start, uint error, string? image, string? group, IntPtr tag, string? dependencies, string? account, string? password, string? display);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool SetServiceObjectSecurity(ServiceHandle service, uint info, byte[] descriptor);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool QueryServiceStatusEx(ServiceHandle service, uint kind, out StatusProcess status, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfigW(ServiceHandle service, IntPtr config, uint size, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool StartServiceW(ServiceHandle service, uint count, IntPtr arguments);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool ControlService(ServiceHandle service, uint control, out StatusBasic status);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool DeleteService(ServiceHandle service);
    [DllImport("advapi32.dll")] private static extern bool CloseServiceHandle(IntPtr service);
}
