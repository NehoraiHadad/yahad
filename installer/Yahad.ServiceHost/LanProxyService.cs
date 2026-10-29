using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Yahad.Install;

namespace Yahad.Service;

// Separate virtual account and fixed directories: the application identity is
// never granted access to this service's private CA storage.
internal static class LanProxyPaths
{
    internal const string ServiceName = "Yahad.FirstInstall.Lan";
    internal static string State => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), ServiceName);
    internal static string Binaries => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), ServiceName);
    internal static string Receipt => Path.Combine(State, "proxy.json");
    internal static string Config => Path.Combine(State, "Caddyfile");
    internal static string Data => Path.Combine(State, "data");
    internal static string Temporary => Path.Combine(State, "temporary");
    internal static string PublicJoin => Path.Combine(State, "join-public");
    internal static string Caddy => Path.Combine(Binaries, "caddy.exe");
    internal static string ServiceImage => Path.Combine(Binaries, "host", "Yahad.ServiceHost.exe");
    internal static string ServiceSid() => ((SecurityIdentifier)new NTAccount("NT SERVICE", ServiceName).Translate(typeof(SecurityIdentifier))).Value;
}

internal sealed record LanProxyReceipt(int Format, string InstallationId, string ServiceSid, string AdapterId,
    string Address, int AppPort, string CaddySha256, string ConfigSha256);

internal static class LanProxyPolicy
{
    // Our runtime publishes STOPPED only after its exact proxy tree is empty.
    // A known reported failure can therefore be repaired, while an OS crash,
    // active PID or unknown service status is never treated as cleanup proof.
    internal static void RequireConfirmedStopped(uint state, uint pid, uint win32, uint specific)
    {
        var clean = win32 == 0 && specific == 0;
        var reportedQuiescentFailure = win32 == 1066 && (ServiceFailure)specific is
            ServiceFailure.IdentityDenied or ServiceFailure.BootstrapUnavailable or ServiceFailure.ActivationUnavailable or
            ServiceFailure.StartFailed or ServiceFailure.UnexpectedExit or ServiceFailure.ArgumentsRejected;
        if (state != (uint)ServiceState.Stopped || pid != 0 || !(clean || reportedQuiescentFailure))
            throw new IOException("LAN_QUIESCENCE_UNCONFIRMED");
    }
    internal static void Require(LanProxyReceipt receipt, string expectedServiceSid)
    {
        if (receipt.Format != 1 || !Guid.TryParseExact(receipt.InstallationId, "N", out _) ||
            !expectedServiceSid.StartsWith("S-1-5-80-", StringComparison.Ordinal) || receipt.ServiceSid != expectedServiceSid ||
            receipt.AdapterId.Length is <1 or >512 || receipt.AdapterId.Any(char.IsControl) ||
            !NetworkPlan.IsPrivateLanV4(receipt.Address) || receipt.AppPort is <1024 or >65535 ||
            !Digest(receipt.CaddySha256) || !Digest(receipt.ConfigSha256))
            throw new IOException("LAN_PROXY_RECEIPT");
    }
    private static bool Digest(string value) => Regex.IsMatch(value, "^[a-f0-9]{64}$", RegexOptions.CultureInvariant);
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
internal sealed class LanProxyStartUnconfirmed(ILanProxyProcess process, Exception cause) : IOException("LAN_PROXY_START_UNCONFIRMED", cause)
{
    internal ILanProxyProcess Process => process;
}

internal sealed class WindowsLanProxyAdmission : ILanProxyAdmission
{
    private LanProxyReceipt? receipt;
    public bool SelectedAddressPresent() => receipt is not null && LanProxyPolicy.AddressPresent(receipt, LanProxyPolicy.Addresses());
    public Task<ILanProxyProcess> StartAsync(CancellationToken cancellation) => Task.Run<ILanProxyProcess>(() =>
    {
        var pins = new List<IDisposable>();
        try
        {
            cancellation.ThrowIfCancellationRequested();
            var expected = LanProxyPaths.ServiceSid();
            ServiceIdentityPolicy.Require(new WindowsServiceTokenReader().Read(), expected);
            pins.Add(WindowsFileBoundary.PinSingleServiceDirectory(LanProxyPaths.State, true));
            pins.Add(WindowsFileBoundary.PinSingleServiceDirectory(LanProxyPaths.Binaries, true));
            pins.Add(WindowsFileBoundary.PinSingleServiceDirectory(LanProxyPaths.PublicJoin, true));
            pins.Add(WindowsFileBoundary.PinPrivateDirectory(LanProxyPaths.Data));
            pins.Add(WindowsFileBoundary.PinPrivateDirectory(LanProxyPaths.Temporary));
            var record = WindowsFileBoundary.OpenInstalledRead(LanProxyPaths.Receipt); pins.Add(record);
            var candidate = NativeJson.Read<LanProxyReceipt>(record.Small(4096)); LanProxyPolicy.Require(candidate, expected);
            var config = WindowsFileBoundary.OpenInstalledRead(LanProxyPaths.Config); pins.Add(config);
            if (Hash(config.Small(131072)) != candidate.ConfigSha256) throw new IOException("LAN_PROXY_CONFIG_CHANGED");
            var image = WindowsFileBoundary.OpenInstalledRead(LanProxyPaths.Caddy); pins.Add(image);
            if (image.Length is <1024 or >268435456 || Hash(image) != candidate.CaddySha256) throw new IOException("LAN_PROXY_IMAGE_CHANGED");
            if (!LanProxyPolicy.AddressPresent(candidate, LanProxyPolicy.Addresses())) throw new IOException("LAN_ADDRESS_CHANGED");
            receipt = candidate;
            cancellation.ThrowIfCancellationRequested();
            var environment = LaunchEnvironment.Clean(LanProxyPaths.Temporary);
            // Storage is explicitly fixed in the pinned Caddyfile. These paths
            // keep incidental OS caches/config away from either app's storage.
            environment["HOME"] = LanProxyPaths.Data;
            environment["XDG_DATA_HOME"] = LanProxyPaths.Data;
            environment["XDG_CONFIG_HOME"] = LanProxyPaths.Temporary;
            try
            {
                var job = JobProcess.Start(LanProxyPaths.Caddy,
                    ["run", "--config", LanProxyPaths.Config, "--adapter", "caddyfile"], LanProxyPaths.State, environment);
                var process = new OwnedLanProxyProcess(job, pins); pins = [];
                return process;
            }
            catch (JobStartQuiescenceUnconfirmedException failure)
            {
                var process = new OwnedLanProxyProcess(failure.OwnedProcess, pins); pins = [];
                throw new LanProxyStartUnconfirmed(process, failure);
            }
        }
        finally { for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose(); }
    }, cancellation);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static string Hash(WindowsFileBoundary.PrivateReadLease image)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536]; long offset = 0;
        while (offset < image.Length)
        {
            var count = image.Read(offset, buffer.AsSpan(0, (int)Math.Min(buffer.Length, image.Length - offset)));
            if (count == 0) throw new IOException("LAN_PROXY_IMAGE_SHORT_READ");
            sha.AppendData(buffer, 0, count); offset += count;
        }
        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
    private sealed class OwnedLanProxyProcess(JobProcess job, List<IDisposable> pins) : ILanProxyProcess
    {
        private bool empty;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => job.WaitAsync(cancellation);
        public async Task StopAsync(CancellationToken cancellation)
        {
            // Admin API is disabled. Terminating this contained proxy tree is
            // intentional: it has no app DB and cannot outlive its service.
            await job.TerminateAndWaitForTreeEmptyAsync(cancellation); empty = true;
        }
        public void Dispose()
        {
            if (!empty) throw new IOException("LAN_PROXY_QUIESCENCE_UNCONFIRMED");
            job.Dispose(); for (var i = pins.Count - 1; i >= 0; i--) pins[i].Dispose(); pins.Clear();
        }
    }
}

internal sealed class LanProxyServiceController(ILanProxyAdmission admission, TimeSpan? addressInterval = null,
    TimeSpan? admissionBudget = null, TimeSpan? cleanupBudget = null)
{
    private readonly TaskCompletionSource stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ILanProxyProcess? unresolved;
    private Task<ILanProxyProcess>? unresolvedStart;
    private int terminal;
    internal uint Control(uint control)
    {
        if (control == ServiceDefinition.Interrogate) return 0;
        if (control is not (ServiceDefinition.Stop or ServiceDefinition.Shutdown)) return 120;
        if (Volatile.Read(ref terminal) != 0) return 1062;
        stop.TrySetResult(); return 0;
    }
    internal async Task<ServiceRunResult> RunAsync(Action<ServiceReport> publish)
    {
        var startBudget = admissionBudget ?? TimeSpan.FromSeconds(30);
        var stopBudget = cleanupBudget ?? TimeSpan.FromSeconds(10);
        var interval = addressInterval ?? TimeSpan.FromSeconds(5);
        if (startBudget <= TimeSpan.Zero || startBudget > TimeSpan.FromSeconds(30) || stopBudget <= TimeSpan.Zero ||
            stopBudget > TimeSpan.FromSeconds(20) || interval <= TimeSpan.Zero || interval > TimeSpan.FromSeconds(30)) throw new ArgumentOutOfRangeException();
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
                catch (TimeoutException) { unresolvedStart = starting; quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
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
        catch (LanProxyStartUnconfirmed owned) { child = owned.Process; failure = ServiceFailure.StartFailed; }
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
                catch { unresolved = child; quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            }
            if (starting is { IsCompleted: false }) { unresolvedStart = starting; quiescent = false; failure = ServiceFailure.QuiescenceUnknown; }
            Volatile.Write(ref terminal, 1);
        }
        if (quiescent)
        {
            try { publish(new(ServiceState.Stopped, 0, 0, 0, failure == ServiceFailure.None ? 0u : 1066u, (uint)failure)); }
            catch { return new(ServiceFailure.StatusTransport, true); }
        }
        // Fields retain all unresolved owners until the application exits. Do
        // not publish STOPPED or release pinned CA/config paths on uncertainty.
        GC.KeepAlive(unresolved); GC.KeepAlive(unresolvedStart);
        return new(failure, quiescent);
    }
}

internal sealed class LanProxyServiceApplication(IScmApi api, Action<int> unsafeExit, ILanProxyAdmission? admission = null)
{
    internal int Run()
    {
        var result = new ServiceRunResult(ServiceFailure.StatusTransport, false);
        api.Dispatch(LanProxyPaths.ServiceName, arguments =>
        {
            var controller = new LanProxyServiceController(admission ?? new WindowsLanProxyAdmission());
            api.Register(LanProxyPaths.ServiceName, controller.Control);
            if (arguments.Length != 1 || arguments[0] != LanProxyPaths.ServiceName)
            {
                api.Publish(new(ServiceState.Stopped, 0, 0, 0, 1066, (uint)ServiceFailure.ArgumentsRejected));
                result = new(ServiceFailure.ArgumentsRejected, true); return;
            }
            result = controller.RunAsync(api.Publish).GetAwaiter().GetResult();
            if (!result.QuiescenceConfirmed || result.Failure == ServiceFailure.StatusTransport) unsafeExit(1);
        });
        return result.Failure == ServiceFailure.None && result.QuiescenceConfirmed ? 0 : 1;
    }
}
