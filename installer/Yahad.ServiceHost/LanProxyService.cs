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
    // The home name (docs/HOME-NAME.md 3): the one subtree this service may write, for renewal.
    // Setup's record of the name stays beside owner.json under the install ACL.
    internal static string Name => Path.Combine(State, "name");
    internal static string HomeNameOwner => Path.Combine(State, HomeNameOwnerRecord.FileName);
    // The proxy runs from its infrastructure tree, like the Runtime: Caddy and the
    // host are infrastructure, so an app update never moves its image or firewall rule.
    internal static string Caddy(string infrastructure) => InstalledLayout.Caddy(infrastructure);
    internal static string Command(string infrastructure) => "\"" + InstalledLayout.HostImage(infrastructure) + "\" --lan-service";
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
    internal string? Address => receipt?.Address;
    public Task<ILanProxyProcess> StartAsync(CancellationToken cancellation) => Task.Run<ILanProxyProcess>(() =>
    {
        cancellation.ThrowIfCancellationRequested();
        ActivePointer.ReadVerified(app: false);
        var candidate = NativeJson.Read(NativeInstallFiles.ReadRecord(LanProxyPaths.Receipt, 4096), ServiceHostJsonContext.Default.LanProxyReceipt);
        if (!LanProxyPolicy.AddressPresent(candidate, LanProxyPolicy.Addresses())) throw new IOException("LAN_ADDRESS_CHANGED");
        receipt = candidate;
        // A swap that a crash interrupted is settled before Caddy reads live/.
        if (HomeNameRelease.Configured && Directory.Exists(LanProxyPaths.Name)) new HomeNameStore(LanProxyPaths.Name).Recover();
        cancellation.ThrowIfCancellationRequested();
        var environment = LaunchEnvironment.Clean(LanProxyPaths.Temporary);
        // Storage is explicitly fixed in the Caddyfile. These paths keep incidental
        // OS caches/config away from either app's storage.
        environment["HOME"] = LanProxyPaths.Data;
        environment["XDG_DATA_HOME"] = LanProxyPaths.Data;
        environment["XDG_CONFIG_HOME"] = LanProxyPaths.Temporary;
        return new OwnedLanProxyProcess(JobProcess.Start(LanProxyPaths.Caddy(InfrastructureRelease.Version),
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
    TimeSpan? admissionBudget = null, TimeSpan? cleanupBudget = null, ILanHomeNameRenewal? renewal = null,
    TimeSpan? renewalFirst = null, TimeSpan? renewalEvery = null, TimeSpan? renewalVerifyBudget = null) : ScmController
{
    internal override async Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish)
    {
        var startBudget = admissionBudget ?? TimeSpan.FromSeconds(30);
        var stopBudget = cleanupBudget ?? TimeSpan.FromSeconds(10);
        var interval = addressInterval ?? TimeSpan.FromSeconds(5);
        var every = renewalEvery ?? TimeSpan.FromHours(24);
        var verifyBudget = renewalVerifyBudget ?? TimeSpan.FromSeconds(20);
        ILanProxyProcess? child = null; Task<ILanProxyProcess>? starting = null; Task? staging = null;
        var failure = ServiceFailure.None; var quiescent = true;
        using var observations = new CancellationTokenSource();
        // A start races STOP and its own budget. A late successful start still transfers its
        // owned process to cleanup; cancellation is not proof that no process exists.
        async Task<bool> StartChildAsync()
        {
            using var startCancellation = new CancellationTokenSource(startBudget);
            starting = admission.StartAsync(startCancellation.Token);
            var first = await Task.WhenAny(starting, stop.Task, Task.Delay(startBudget));
            if (first == starting) { child = await starting; return true; }
            startCancellation.Cancel();
            if (first != stop.Task) failure = ServiceFailure.StartFailed;
            try { child = await starting.WaitAsync(stopBudget); }
            catch (OperationCanceledException) when (starting.IsCanceled) { }
            catch (TimeoutException) { quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            return false;
        }
        async Task<bool> StopChildAsync()
        {
            try
            {
                using var cleanup = new CancellationTokenSource(stopBudget);
                await child!.StopAsync(cleanup.Token).WaitAsync(cleanup.Token); child.Dispose(); child = null; return true;
            }
            catch { quiescent = false; failure = ServiceFailure.QuiescenceUnknown; return false; }
        }
        // The planned replacement (docs/HOME-NAME.md 3): the confirmed stop, the swap, a start,
        // and the new leaf served for the name; anything less swaps back. False leaves the loop.
        async Task<bool> ReplaceAsync(ILanHomeNameRenewal name)
        {
            if (!await StopChildAsync()) return false;
            // A Swap that throws leaves the previous certificate live (or journaled for the admission's recovery).
            var swapped = true;
            try { name.Swap(); } catch { swapped = false; }
            bool started;
            try { started = await StartChildAsync(); }
            catch { started = false; failure = ServiceFailure.StartFailed; }
            if (started)
            {
                if (!swapped || await ServedAsync(name)) return true;
                if (stop.Task.IsCompleted || !await StopChildAsync()) return false;
            }
            // The new certificate did not start: the previous one goes back, but only when the failed
            // start owns nothing (no child, no start still running) and no STOP is pending.
            else if (!swapped || child is not null || !quiescent || starting is not { IsCompleted: true } || stop.Task.IsCompleted) return false;
            // A failed swap back leaves its journal; the admission's recovery settles live/ first.
            try { name.SwapBack(); } catch { }
            failure = ServiceFailure.None;
            return await StartChildAsync();
        }
        async Task<bool> ServedAsync(ILanHomeNameRenewal name)
        {
            using var verify = CancellationTokenSource.CreateLinkedTokenSource(observations.Token);
            verify.CancelAfter(verifyBudget);
            var serving = name.ServesLiveAsync(verify.Token);
            var exited = child!.WaitForExitAsync(verify.Token);
            await Task.WhenAny(serving, exited, stop.Task);
            verify.Cancel();
            return serving.IsCompletedSuccessfully && serving.Result;
        }
        try
        {
            publish(new(ServiceState.StartPending, 0, 1, 40000, 0, 0));
            if (!stop.Task.IsCompleted && await StartChildAsync() && !stop.Task.IsCompleted)
            {
                if (!admission.SelectedAddressPresent()) throw new IOException("LAN_ADDRESS_CHANGED");
                var exit = child!.WaitForExitAsync(observations.Token);
                // SCM RUNNING confirms owned-process startup only. Installer
                // performs a separate pinned HTTPS check before claiming LAN ready.
                if (exit.IsCompleted) { await exit; failure = ServiceFailure.UnexpectedExit; }
                else
                {
                    publish(new(ServiceState.Running, 5, 0, 0, 0, 0));
                    var renewalAt = Environment.TickCount64 + (long)(renewalFirst ?? TimeSpan.FromMinutes(10)).TotalMilliseconds;
                    while (!stop.Task.IsCompleted)
                    {
                        var winner = staging is null ? await Task.WhenAny(exit, stop.Task, Task.Delay(interval)) : await Task.WhenAny(exit, stop.Task, Task.Delay(interval), staging);
                        if (winner == exit) { await exit; failure = ServiceFailure.UnexpectedExit; break; }
                        if (winner == stop.Task) break;
                        // Includes wake-up and lost/reassigned DHCP addresses.
                        // Stop; never rebind to another adapter or all interfaces.
                        if (!admission.SelectedAddressPresent()) { failure = ServiceFailure.ActivationUnavailable; break; }
                        if (renewal is null) continue;
                        if (staging is { IsCompleted: true })
                        {
                            // A failed staging leaves live/ and the running proxy as they are; the next check retries.
                            var staged = staging.IsCompletedSuccessfully; staging = null;
                            renewalAt = Environment.TickCount64 + (long)every.TotalMilliseconds;
                            if (!staged) continue;
                            if (!await ReplaceAsync(renewal) || stop.Task.IsCompleted) break;
                            if (!admission.SelectedAddressPresent()) { failure = ServiceFailure.ActivationUnavailable; break; }
                            exit = child!.WaitForExitAsync(observations.Token);
                        }
                        else if (staging is null && Environment.TickCount64 >= renewalAt)
                        {
                            bool due;
                            try { due = renewal.Due(DateTimeOffset.UtcNow); } catch { due = false; }
                            // ACME runs beside the proxy: STOP and the address check never wait for it.
                            if (due) staging = Task.Run(() => renewal.StageAsync(observations.Token));
                            else renewalAt = Environment.TickCount64 + (long)every.TotalMilliseconds;
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
            // Cancelled staging writes only next/, which nothing serves; a short bound keeps it from
            // racing Setup's use of name/ after STOPPED without making STOP wait for ACME.
            if (staging is not null) { try { await staging.WaitAsync(stopBudget); } catch { } }
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
