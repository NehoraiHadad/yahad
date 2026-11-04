using System.Net.NetworkInformation;
using System.Net.Sockets;
using Yahad.Install;

namespace Yahad.Service;

// Separate virtual account and fixed directories: the application identity is
// never granted access to this service's private CA storage.
internal static class LanProxyPaths
{
    internal const string ServiceName = "Yahad.FirstInstall.Lan";
    internal static string State => Path.Combine(MachineFolders.Required(Environment.SpecialFolder.CommonApplicationData), ServiceName);
    internal static string Receipt => Path.Combine(State, "proxy.json");
    internal static string Config => Path.Combine(State, "Caddyfile");
    internal static string Data => Path.Combine(State, "data");
    internal static string Temporary => Path.Combine(State, "temporary");
    internal static string PublicJoin => Path.Combine(State, "join-public");
    // The proxy runs from the installed release's own folder, like the Runtime.
    internal static string Caddy(string version) => Path.Combine(InstalledLayout.VersionRoot(version), "payload", "runtime", "caddy", "caddy.exe");
    internal static string Command(string version) => "\"" + Path.Combine(InstalledLayout.VersionRoot(version), "host", "Yahad.ServiceHost.exe") + "\" --lan-service";
}

// The address the proxy may serve on, and nothing else: Setup wrote it and this
// service can only read it.
internal sealed record LanProxyReceipt(string AdapterId, string Address);

internal static class LanProxyPolicy
{
    private const uint NeverStarted = 1077, AutomaticStart = 2;
    // Only an enabled proxy starts automatically: pausing and disabling set it back
    // to demand start. Enabled but with its address gone, it cannot serve.
    internal static bool ShouldRun(uint startType, LanProxyReceipt? receipt, IEnumerable<AdapterAddress> addresses) =>
        startType == AutomaticStart && receipt is not null && AddressPresent(receipt, addresses);
    // Our runtime publishes STOPPED only after its exact proxy tree is empty.
    // A known reported failure can therefore be repaired, and a service that never
    // started has no tree. An OS crash, active PID or unknown service status is
    // never treated as cleanup proof.
    internal static void RequireConfirmedStopped(uint state, uint pid, uint win32, uint specific)
    {
        var clean = win32 is 0 or NeverStarted && specific == 0;
        var reportedQuiescentFailure = win32 == 1066 && (ServiceFailure)specific is
            ServiceFailure.ActivationUnavailable or ServiceFailure.StartFailed or ServiceFailure.UnexpectedExit or ServiceFailure.ArgumentsRejected;
        if (state != (uint)ServiceState.Stopped || pid != 0 || !(clean || reportedQuiescentFailure))
            throw new IOException("LAN_QUIESCENCE_UNCONFIRMED");
    }
    internal static bool AddressPresent(LanProxyReceipt receipt, IEnumerable<AdapterAddress> addresses) =>
        addresses.Any(a => a.Up && a.AdapterId == receipt.AdapterId && a.Address == receipt.Address && NetworkPlan.IsPrivateLanV4(a.Address));
    internal static IEnumerable<AdapterAddress> Addresses() => NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(adapter => adapter.GetIPProperties().UnicastAddresses
            .Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => new AdapterAddress(adapter.Id, adapter.Name, ip.Address.ToString(), adapter.OperationalStatus == OperationalStatus.Up)));
}

internal interface ILanProxyProcess : IDisposable
{
    Task<int> WaitForExitAsync(CancellationToken cancellation);
    Task StopAsync(CancellationToken cancellation);
}
internal interface ILanProxyAdmission
{
    Task<ILanProxyProcess> StartAsync(CancellationToken cancellation);
    bool SelectedAddressPresent();
}

internal sealed class WindowsLanProxyAdmission : ILanProxyAdmission
{
    private LanProxyReceipt? receipt;
    public bool SelectedAddressPresent() => receipt is not null && LanProxyPolicy.AddressPresent(receipt, LanProxyPolicy.Addresses());
    public Task<ILanProxyProcess> StartAsync(CancellationToken cancellation) => Task.Run<ILanProxyProcess>(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        var candidate = NativeJson.Read<LanProxyReceipt>(NativeInstallFiles.ReadRecord(LanProxyPaths.Receipt, 4096));
        if (!LanProxyPolicy.AddressPresent(candidate, LanProxyPolicy.Addresses())) throw new IOException("LAN_ADDRESS_CHANGED");
        receipt = candidate;
        cancellation.ThrowIfCancellationRequested();
        var environment = LaunchEnvironment.Clean(LanProxyPaths.Temporary);
        // Storage is explicitly fixed in the Caddyfile. These paths keep incidental
        // OS caches/config away from either app's storage.
        environment["HOME"] = LanProxyPaths.Data;
        environment["XDG_DATA_HOME"] = LanProxyPaths.Data;
        environment["XDG_CONFIG_HOME"] = LanProxyPaths.Temporary;
        return new OwnedLanProxyProcess(JobProcess.Start(LanProxyPaths.Caddy(ApplicationRelease.Version),
            ["run", "--config", LanProxyPaths.Config, "--adapter", "caddyfile"], LanProxyPaths.State, environment));
    }, cancellation);
    private sealed class OwnedLanProxyProcess(JobProcess job) : ILanProxyProcess
    {
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => job.WaitAsync(cancellation);
        // Admin API is disabled. Terminating this contained proxy tree is
        // intentional: it has no app DB and cannot outlive its service.
        public Task StopAsync(CancellationToken cancellation) => job.TerminateAndWaitForTreeEmptyAsync(cancellation);
        public void Dispose() => job.Dispose();
    }
}

internal sealed class LanProxyServiceController(ILanProxyAdmission admission, TimeSpan? addressInterval = null,
    TimeSpan? admissionBudget = null, TimeSpan? cleanupBudget = null) : ScmController
{
    internal override async Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish)
    {
        var startBudget = admissionBudget ?? TimeSpan.FromSeconds(30);
        var stopBudget = cleanupBudget ?? TimeSpan.FromSeconds(10);
        var interval = addressInterval ?? TimeSpan.FromSeconds(5);
        ILanProxyProcess? child = null; Task<ILanProxyProcess>? starting = null;
        var failure = ServiceFailure.None; var quiescent = true;
        using var startCancellation = new CancellationTokenSource(startBudget);
        using var observations = new CancellationTokenSource();
        try
        {
            publish(new(ServiceState.StartPending, 0, 1, 40000, 0, 0));
            if (!stop.Task.IsCompleted)
            {
            starting = admission.StartAsync(startCancellation.Token);
            var first = await Task.WhenAny(starting, stop.Task, Task.Delay(startBudget));
            if (first != starting)
            {
                startCancellation.Cancel();
                if (first != stop.Task) failure = ServiceFailure.StartFailed;
                // A late successful start still transfers its owned process to
                // cleanup; cancellation is not proof that no process exists.
                try { child = await starting.WaitAsync(stopBudget); }
                catch (OperationCanceledException) when (starting.IsCanceled) { }
                catch (TimeoutException) { quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            }
            else child = await starting;
            if (child is not null && failure == ServiceFailure.None && !stop.Task.IsCompleted)
            {
                if (!admission.SelectedAddressPresent()) throw new IOException("LAN_ADDRESS_CHANGED");
                var exit = child.WaitForExitAsync(observations.Token);
                // SCM RUNNING confirms owned-process startup only. Installer
                // performs a separate pinned HTTPS check before claiming LAN ready.
                if (exit.IsCompleted) { await exit; failure = ServiceFailure.UnexpectedExit; }
                else
                {
                    publish(new(ServiceState.Running, 5, 0, 0, 0, 0));
                    while (!stop.Task.IsCompleted)
                    {
                        var winner = await Task.WhenAny(exit, stop.Task, Task.Delay(interval));
                        if (winner == exit) { await exit; failure = ServiceFailure.UnexpectedExit; break; }
                        if (winner == stop.Task) break;
                        // Includes wake-up and lost/reassigned DHCP addresses.
                        // Stop; never rebind to another adapter or all interfaces.
                        if (!admission.SelectedAddressPresent()) { failure = ServiceFailure.ActivationUnavailable; break; }
                    }
                }
            }
            }
        }
        catch { if (failure == ServiceFailure.None) failure = ServiceFailure.StartFailed; }
        finally
        {
            observations.Cancel();
            if (child is not null)
            {
                try { publish(new(ServiceState.StopPending, 0, 1, 20000, 0, 0)); }
                catch { failure = ServiceFailure.StatusTransport; }
                try
                {
                    using var cleanup = new CancellationTokenSource(stopBudget);
                    await child.StopAsync(cleanup.Token).WaitAsync(cleanup.Token); child.Dispose();
                }
                catch { quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            }
            if (starting is { IsCompleted: false }) { quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            Volatile.Write(ref terminal, 1);
        }
        // On uncertainty do not publish STOPPED: the dispatcher exits the process
        // at once, and only that ends what may still be running.
        if (quiescent)
        {
            try { publish(new(ServiceState.Stopped, 0, 0, 0, failure == ServiceFailure.None ? 0u : 1066u, (uint)failure)); }
            catch { return new(ServiceFailure.StatusTransport, true); }
        }
        return new(failure, quiescent);
    }
}
