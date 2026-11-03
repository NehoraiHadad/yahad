using System.Net;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Yahad.Install;
using Yahad.Service;

// Runs the real packaged entry point through the native lease/ready channel.
// No SCM, installed state, real connector or update application is used.
internal static class PackagedNativeStartupProbe
{
    internal static async Task<int> Run(string prepared, string report)
    {
        var owned = OwnedFixtureDirectory.Create(Path.GetTempPath());
        var jobs = new FixtureProcesses();
        ServiceController? controller = null;
        Task<ServiceRunResult>? run = null;
        WindowsFileBoundary.DataLease? lease = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var timer = System.Diagnostics.Stopwatch.StartNew();
        bool healthy = false, cleaned = false;
        string? failure = null, cleanupFailure = null;
        ServiceRunResult? result = null;
        try
        {
            var data = Path.Combine(owned.Root, "data");
            var temporary = Path.Combine(owned.Root, "temporary");
            Directory.CreateDirectory(data); Directory.CreateDirectory(temporary);
            var key = Path.Combine(owned.Root, "master-key");
            File.WriteAllText(key, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            var node = Path.Combine(prepared, "runtime", "node", "node.exe");
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop();
            var origin = "http://127.0.0.1:" + port;
            var environment = new Dictionary<string, string>
            {
                ["HOST"] = "127.0.0.1", ["PORT"] = port.ToString(), ["APP_ORIGIN"] = origin,
                ["COOKIE_SECURE"] = "false", ["DATA_DIR"] = data, ["MASTER_KEY_FILE"] = key,
                ["CONNECTOR"] = "mock", ["ALLOW_REAL_WHATSAPP"] = "no",
                ["SETUP_CAPABILITY_FILE"] = Path.Combine(owned.Root, "absent-capability"),
                ["DIAGNOSTICS_ENABLED"] = "false"
            };
            lease = WindowsFileBoundary.AcquireDataLease(data);
            var spec = new VerifiedProcessSpec(node, ["server/main.mjs"],
                Path.Combine(prepared, "app"), temporary, Path.Combine(prepared, "runtime", "ffmpeg", "ffmpeg.exe"), environment, lease);
            var launcher = jobs.Launcher(new ServiceProcessLauncher(spec, new LocalChildControlFactory()));
            controller = new ServiceController(new Start(launcher), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20));
            run = controller.RunAsync(status => { if (status.State == ServiceState.Running) ready.TrySetResult(); });
            await Task.WhenAny(ready.Task, run).WaitAsync(TimeSpan.FromSeconds(35));
            if (ready.Task.IsCompletedSuccessfully)
            {
                using var client = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
                using var response = await client.GetAsync(origin + "/health/ready");
                healthy = response.IsSuccessStatusCode;
            }
        }
        catch (Exception error) { failure = error.GetType().Name; }
        finally
        {
            try
            {
                if (run is not null)
                {
                    if (!run.IsCompleted) controller!.Control(ServiceDefinition.Stop);
                    result = await run.WaitAsync(TimeSpan.FromSeconds(30));
                }
            }
            catch (Exception error) { cleanupFailure = "controller_" + error.GetType().Name; }
            // A controller failure/timeout must not skip its owned job cleanup.
            try { await jobs.CleanupAsync(); }
            catch (Exception error) { cleanupFailure = (cleanupFailure is null ? "" : cleanupFailure + ";") + "jobs_" + error.GetType().Name; }
            try
            {
                if (jobs.Confirmed)
                {
                    if (run is not null && !run.IsCompleted) result = await run.WaitAsync(TimeSpan.FromSeconds(5));
                    lease?.Dispose(); lease = null;
                    owned.Cleanup(() => jobs.Confirmed); cleaned = true;
                }
            }
            catch (Exception error) { cleanupFailure = (cleanupFailure is null ? "" : cleanupFailure + ";") + "fixture_" + error.GetType().Name; }
            var observed = new { healthy, cleaned, failure, cleanupFailure, elapsedMilliseconds = timer.ElapsedMilliseconds,
                result, startup = controller?.StartupObservation, rootExit = controller?.RootExitObservation,
                installedServicesChanged = false, realProvider = false };
            File.WriteAllText(report, JsonSerializer.Serialize(observed, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine(JsonSerializer.Serialize(observed));
        }
        return healthy && cleaned && failure is null && cleanupFailure is null && result is { Failure: ServiceFailure.None } ? 0 : 1;
    }
    internal static async Task<int> CheckBudget()
    {
        var launcher = new SlowReady();
        var controller = new ServiceController(new Start(launcher, TimeSpan.FromSeconds(2)),
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(50));
        var result = await controller.RunAsync(status =>
        { if (status.State == ServiceState.Running) controller.Control(ServiceDefinition.Stop); });
        var passed = result is { Failure: ServiceFailure.None, QuiescenceConfirmed: true }
            && controller.StartupObservation.Stage == ServiceStartupStage.Running && launcher.Session.Disposed;
        Console.WriteLine(JsonSerializer.Serialize(new { suite="startup-budget-is-independent-of-stop-budget", passed,
            readinessMilliseconds=150, stopBudgetMilliseconds=50, startupBudgetMilliseconds=2000, result,
            startup=controller.StartupObservation, realServices=false }));
        return passed ? 0 : 1;
    }
    private sealed class Start(IPrivateAppLauncher launcher, TimeSpan? budget = null) : IServiceStartAuthorizer, IObservedAdmission
    {
        public TimeSpan AdmissionBudget => budget ?? TimeSpan.FromSeconds(30);
        public event Action? ObservedWork { add { } remove { } }
        public Task<AuthorizedServiceStart> AuthorizeAsync(CancellationToken cancellation)
            => Task.FromResult(new AuthorizedServiceStart(new(new string('a', 64)), launcher, new Barrier()));
    }
    private sealed class SlowReady : IPrivateAppLauncher
    {
        internal readonly EndedSession Session = new();
        public Task<IContainedAppSession> StartAsync(AppRelease release, CancellationToken cancellation)
            => Task.FromResult<IContainedAppSession>(Session);
        public Task WaitForHealthyAsync(IContainedAppSession session, CancellationToken cancellation)
            => Task.Delay(150, cancellation);
    }
    private sealed class EndedSession : IContainedAppSession
    {
        private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed;
        public Task RequestGracefulStopAsync(CancellationToken cancellation) { exit.TrySetResult(0); return Task.CompletedTask; }
        public Task<int> WaitForRootExitAsync(CancellationToken cancellation) => exit.Task.WaitAsync(cancellation);
        public Task WaitForTreeEmptyAsync(CancellationToken cancellation) => exit.Task.WaitAsync(cancellation);
        public Task TerminateAndWaitForTreeEmptyAsync(CancellationToken cancellation) { exit.TrySetResult(1); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposed=true; return ValueTask.CompletedTask; }
    }
    private sealed class Barrier : IAppRecoveryBarrier
    {
        public Task RequireStartAllowedAsync(AppRelease release, CancellationToken cancellation)
        { cancellation.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    }
}
