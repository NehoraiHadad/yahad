using System.IO;
using Yahad.Install;
using Yahad.Service;
using Yahad.Setup;

internal static class LanRuntimeTests
{
    private static void Require(bool value, string code = "LAN_RUNTIME_REGRESSION") { if (!value) throw new Exception(code); }
    private static void Refuse(Action action)
    { try { action(); } catch (IOException) { return; } throw new Exception("LAN_UNEXPECTED_ADMISSION"); }
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        var valid = new LanProxyReceipt("adapter-home", "192.168.1.50");
        await check("LAN address watch cannot substitute a different adapter or newly assigned private address", () =>
        {
            Require(LanProxyPolicy.AddressPresent(valid, [new("adapter-home", "Home", valid.Address, true)]));
            Require(!LanProxyPolicy.AddressPresent(valid, [new("adapter-vpn", "VPN", valid.Address, true)]));
            Require(!LanProxyPolicy.AddressPresent(valid, [new("adapter-home", "Home", "192.168.1.51", true)]));
            Require(!LanProxyPolicy.AddressPresent(valid, [new("adapter-home", "Home", valid.Address, false)]));
            foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "8.8.8.8", "169.254.1.2", "::1", "192.168.001.50", "192.168.1.50\n" })
                Require(!LanProxyPolicy.AddressPresent(valid with { Address = address }, [new("adapter-home", "Home", address, true)]));
            return Task.CompletedTask;
        });
        await check("LAN service runs its own image argument under its own account", () =>
        {
            Require(ServiceSpec.Lan.Command("1.2.3").EndsWith(" --lan-service", StringComparison.Ordinal));
            Require(ServiceSpec.Lan.Name != ServiceDefinition.Name && ServiceSpec.Lan.Account != ServiceDefinition.Account);
            return Task.CompletedTask;
        });
        await check("LAN repair accepts confirmed empty stopped status after known address start or exit failures", () =>
        {
            LanProxyPolicy.RequireConfirmedStopped(1, 0, 0, 0);
            foreach (var failure in new[] { ServiceFailure.ActivationUnavailable, ServiceFailure.StartFailed, ServiceFailure.UnexpectedExit, ServiceFailure.ArgumentsRejected })
                LanProxyPolicy.RequireConfirmedStopped(1, 0, 1066, (uint)failure);
            return Task.CompletedTask;
        });
        await check("LAN repair refuses crashes unknown status active process or unconfirmed cleanup", () =>
        {
            foreach (var failure in new[] { ServiceFailure.QuiescenceUnknown, ServiceFailure.StatusTransport, ServiceFailure.StopFailed, ServiceFailure.None })
                Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 0, 1066, (uint)failure));
            Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 0, 1067, 0));
            Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 0, 1066, 999999));
            Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 0, 0, (uint)ServiceFailure.StartFailed));
            Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 123, 0, 0));
            Refuse(() => LanProxyPolicy.RequireConfirmedStopped(1, 123, 1066, (uint)ServiceFailure.ActivationUnavailable));
            foreach (var state in new uint[] { 0, 2, 3, 4, 99 })
                Refuse(() => LanProxyPolicy.RequireConfirmedStopped(state, 0, 0, 0));
            return Task.CompletedTask;
        });
        await check("LAN SCM stop is nonblocking and releases exact proxy only after observed empty tree", async () =>
        {
            var process = new FakeProcess(); var admission = new FakeAdmission(process);
            var controller = new LanProxyServiceController(admission, TimeSpan.FromMilliseconds(1)); var states = new List<ServiceReport>();
            var result = await controller.RunAsync(status =>
            {
                states.Add(status);
                if (status.State == ServiceState.Running)
                {
                    Require(controller.Control(4) == 0 && controller.Control(99) == 120);
                    Require(controller.Control(1) == 0);
                }
            });
            Require(result == new ServiceRunResult(ServiceFailure.None, true));
            Require(process.Stops == 1 && process.Disposed && process.EmptyObserved);
            Require(states.Select(s => s.State).SequenceEqual([ServiceState.StartPending, ServiceState.Running, ServiceState.StopPending, ServiceState.Stopped]));
            Require(states.Last().Win32ExitCode == 0 && controller.Control(1) == 1062);
        });
        await check("LAN stop before admission starts performs no process start and publishes stopped", async () =>
        {
            var admission = new FakeAdmission(new FakeProcess()); var controller = new LanProxyServiceController(admission); var states = new List<ServiceReport>();
            controller.Control(1); var result = await controller.RunAsync(states.Add);
            Require(result.QuiescenceConfirmed && result.Failure == ServiceFailure.None && admission.Starts == 0);
            Require(states.Last().State == ServiceState.Stopped);
        });
        await check("LAN DHCP or wake address loss stops proxy without rebinding or claiming ready", async () =>
        {
            var process = new FakeProcess(); var admission = new FakeAdmission(process);
            var controller = new LanProxyServiceController(admission, TimeSpan.FromMilliseconds(1)); var states = new List<ServiceReport>();
            var result = await controller.RunAsync(status => { states.Add(status); if (status.State == ServiceState.Running) admission.AddressPresent = false; });
            Require(result.Failure == ServiceFailure.ActivationUnavailable && result.QuiescenceConfirmed);
            Require(admission.Starts == 1 && process.Stops == 1 && process.Disposed && states.Last().Win32ExitCode != 0);
        });
        await check("LAN unexpected proxy exit returns failure after exact tree cleanup", async () =>
        {
            var process = new FakeProcess(); var admission = new FakeAdmission(process);
            var controller = new LanProxyServiceController(admission); var states = new List<ServiceReport>();
            var result = await controller.RunAsync(status => { states.Add(status); if (status.State == ServiceState.Running) process.Exit.TrySetResult(17); });
            Require(result.Failure == ServiceFailure.UnexpectedExit && result.QuiescenceConfirmed && process.Disposed);
        });
        await check("LAN uncertain proxy cleanup never publishes stopped or disposes owned process", async () =>
        {
            var process = new FakeProcess { FailCleanup = true }; var admission = new FakeAdmission(process);
            var controller = new LanProxyServiceController(admission); var states = new List<ServiceReport>();
            var result = await controller.RunAsync(status => { states.Add(status); if (status.State == ServiceState.Running) controller.Control(1); });
            Require(result.Failure == ServiceFailure.QuiescenceUnknown && !result.QuiescenceConfirmed && !process.Disposed);
            Require(states.All(s => s.State != ServiceState.Stopped));
        });
        await check("LAN stop racing a late successful start still cleans the returned exact process", async () =>
        {
            var process = new FakeProcess();
            var pending = new TaskCompletionSource<ILanProxyProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = new FakeAdmission(process) { PendingStart = pending.Task };
            var controller = new LanProxyServiceController(admission); var states = new List<ServiceReport>();
            var running = controller.RunAsync(states.Add);
            // RunAsync has reached its first awaited admission race. Stop wins;
            // a successful late return cannot be treated as canceled/no process.
            controller.Control(1); pending.SetResult(process);
            var result = await running;
            Require(result.Failure == ServiceFailure.None && result.QuiescenceConfirmed && process.Disposed && process.Stops == 1);
            Require(states.All(s => s.State != ServiceState.Running));
        });
        await check("LAN start timeout with unknown ownership never reports stopped", async () =>
        {
            var pending = new TaskCompletionSource<ILanProxyProcess>(TaskCreationOptions.RunContinuationsAsynchronously);
            var admission = new FakeAdmission(new FakeProcess()) { PendingStart = pending.Task };
            var controller = new LanProxyServiceController(admission, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(2), TimeSpan.FromMilliseconds(2));
            var states = new List<ServiceReport>(); var result = await controller.RunAsync(states.Add);
            Require(!result.QuiescenceConfirmed && result.Failure == ServiceFailure.QuiescenceUnknown && states.All(s => s.State != ServiceState.Stopped));
            // Synthetic task contains no native resources. Finish it after the
            // observation to avoid leaving an unobserved background test task.
            pending.SetCanceled();
        });
        await check("LAN SCM dispatcher refuses another service name without native process admission", () =>
        {
            var scm = new FakeScm { Arguments = ["Unrelated.Service"] }; var admission = new FakeAdmission(new FakeProcess());
            var result = ScmController.Dispatch(scm, LanProxyPaths.ServiceName, _ => throw new Exception("unexpected unsafe exit"), () => new LanProxyServiceController(admission));
            Require(result == 1 && admission.Starts == 0 && scm.Name == LanProxyPaths.ServiceName && scm.Reports.Last().State == ServiceState.Stopped);
            return Task.CompletedTask;
        });
    }
    private sealed class FakeAdmission(FakeProcess process) : ILanProxyAdmission
    {
        internal bool AddressPresent = true;
        internal int Starts; internal Task<ILanProxyProcess>? PendingStart;
        public Task<ILanProxyProcess> StartAsync(CancellationToken cancellation)
        {
            Starts++;
            return PendingStart ?? Task.FromResult<ILanProxyProcess>(process);
        }
        public bool SelectedAddressPresent() => AddressPresent;
    }
    private sealed class FakeProcess : ILanProxyProcess
    {
        internal readonly TaskCompletionSource<int> Exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Stops; internal bool FailCleanup, EmptyObserved, Disposed;
        public Task<int> WaitForExitAsync(CancellationToken cancellation) => Exit.Task.WaitAsync(cancellation);
        public Task StopAsync(CancellationToken cancellation)
        {
            Stops++; if (FailCleanup) throw new IOException("synthetic unknown tree");
            EmptyObserved = true; Exit.TrySetResult(1); return Task.CompletedTask;
        }
        public void Dispose() { Require(EmptyObserved); Disposed = true; }
    }
    private sealed class FakeScm : IScmApi
    {
        internal string[] Arguments = []; internal string? Name; internal readonly List<ServiceReport> Reports = [];
        public void Dispatch(string name, Action<string[]> main) { Name = name; main(Arguments); }
        public void Register(string name, Func<uint, uint> handler) { Require(name == LanProxyPaths.ServiceName); }
        public void Publish(ServiceReport report) => Reports.Add(report);
    }
}
