using System.IO;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Yahad.Install;
using Yahad.Service;
using Yahad.Setup;

internal static class LanIntegrationTests
{
    private static void Require(bool condition, string reason = "LAN_INTEGRATION_REGRESSION")
    { if (!condition) throw new Exception(reason); }
    private static void Refuse(Action action)
    { try { action(); } catch (IOException) { return; } throw new Exception("LAN_UNEXPECTED_ADMISSION"); }
    private static async Task RefuseAsync(Func<Task> action)
    { try { await action(); } catch (IOException) { return; } catch (OperationCanceledException) { return; } throw new Exception("LAN_UNEXPECTED_ADMISSION"); }
    private static byte[] Json<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value);
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static readonly OwnerProcess Owner = new("S-1-5-21-1-2-3-1001", 111, 123, new string('a', 64));
    private static LanSetupRequest Enable(bool helper = true) => new(LanSetupAction.Enable, "fixture-home", "192.168.4.5", true, helper);

    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("LAN request requires explicit consent, canonical private address and well-formed action", () =>
        {
            Enable().Require(); Enable(false).Require();
            new LanSetupRequest(LanSetupAction.KeepCurrent).Require();
            new LanSetupRequest(LanSetupAction.Disable).Require();
            Refuse(() => (Enable() with { FirewallConsent = false }).Require());
            foreach (var adapter in new[] { "", "  ", "home\n", new string('x', 257) })
                Refuse(() => (Enable() with { AdapterId = adapter }).Require());
            foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "8.8.8.8", "169.254.1.2", "::1", "192.168.004.5", "192.168.4.5:443", "192.168.4.5\n" })
                Refuse(() => (Enable() with { Address = address }).Require());
            Refuse(() => (Enable() with { Address = null }).Require());
            Refuse(() => new LanSetupRequest((LanSetupAction)999).Require());
            Refuse(() => new LanSetupRequest(LanSetupAction.Disable, Address: "192.168.4.5").Require());
            Refuse(() => new LanSetupRequest(LanSetupAction.KeepCurrent, FirewallConsent: true).Require());
            return Task.CompletedTask;
        });
        await check("installer passes validated LAN request and owner between publication and server observation", async () =>
        {
            var request = Enable(false); var host = new NetworkHost(); var phases = new List<string>();
            var result = await new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default, lan: request);
            Require(host.Calls.SequenceEqual(["prepare", "publish", "lan:Enable", "start"]));
            Require(ReferenceEquals(host.Request, request) && host.Owner == Owner && !host.Request!.HttpHelp);
            Require(result.Ok && result.Url == "http://127.0.0.1:47123/" && phases.Last() == "ready");
            Require(phases.IndexOf("configuring_lan") > phases.IndexOf("installing_service") && phases.IndexOf("configuring_lan") < phases.IndexOf("starting_server"));
        });
        await check("invalid LAN consent stops installer before any preparation or publication", async () =>
        {
            var host = new NetworkHost(); var phases = new List<string>();
            await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default,
                lan: Enable() with { FirewallConsent = false }));
            Require(host.Calls.Count == 0 && phases.Count == 0);
        });
        foreach (var failure in new[] { "prepare", "publish", "lan:Enable", "start" })
            await check("LAN install failure at " + failure + " cannot reach browser-ready or uninstall", async () =>
            {
                var host = new NetworkHost { Failure = failure }; var phases = new List<string>();
                await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default, lan: Enable()));
                var full = new[] { "prepare", "publish", "lan:Enable", "start" };
                Require(host.Calls.SequenceEqual(full.Take(Array.IndexOf(full, failure) + 1)));
                Require(!phases.Contains("ready") && !host.Calls.Any(c => c.StartsWith("uninstall", StringComparison.Ordinal)));
            });
        await check("LAN selection cancellation prevents subsequent host operation", async () =>
        {
            using var cancel = new CancellationTokenSource(); var host = new NetworkHost { AfterPrepare = cancel.Cancel };
            await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, _ => { }, cancel.Token, lan: Enable()));
            Require(host.Calls.SequenceEqual(["prepare"]));
        });
        await check("keep-current and omitted LAN preserve ordinary install while disable is explicitly conveyed", async () =>
        {
            foreach (var request in new LanSetupRequest?[] { null, new(LanSetupAction.KeepCurrent) })
            {
                var host = new NetworkHost(); await new InstallFlow(host).RunAsync(Owner, false, _ => { }, default, lan: request);
                Require(host.Calls.SequenceEqual(["prepare", "publish", "start"]) && host.Request is null);
            }
            var disableHost = new NetworkHost();
            await new InstallFlow(disableHost).RunAsync(Owner, false, _ => { }, default, lan: new(LanSetupAction.Disable));
            Require(disableHost.Calls.SequenceEqual(["prepare", "publish", "lan:Disable", "start"]));
        });
        await check("unsupported LAN host refuses before server or browser observation", async () =>
        {
            var host = new BasicHost(); var phases = new List<string>();
            await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default, lan: Enable()));
            Require(host.Calls.SequenceEqual(["prepare", "publish"]) && !phases.Contains("ready"));
        });
        await check("uninstall preserves keep/delete mode and never calls LAN configuration", async () =>
        {
            foreach (var mode in new[] { UninstallMode.KeepData, UninstallMode.DeleteData })
            {
                var host = new NetworkHost(); var result = await new InstallFlow(host).RunAsync(Owner, true, _ => { }, default, mode);
                Require(host.Calls.SequenceEqual(["uninstall:" + mode]) && host.Request is null && host.Removal == mode);
                Require(result.DataPreserved == (mode == UninstallMode.KeepData));
            }
            var forbidden = new NetworkHost();
            await RefuseAsync(() => new InstallFlow(forbidden).RunAsync(Owner, true, _ => { }, default, lan: Enable()));
            Require(forbidden.Calls.Count == 0);
        });

        var state = FixtureState(); var before = FixtureSnapshot(state);
        await check("verified payload schema accepts supported numeric revisions and refuses missing or malformed values", () =>
        {
            foreach (var schema in new[] { 1, 5, 6, 64 })
                Require(PayloadSchema.Read(NativeJson.Read<JsonElement>(Json(new { databaseSchema = schema }))) == schema);
            foreach (var malformed in new[] { "{}", "{\"databaseSchema\":null}", "{\"databaseSchema\":\"6\"}",
                "{\"databaseSchema\":true}", "{\"databaseSchema\":false}", "{\"databaseSchema\":0}",
                "{\"databaseSchema\":65}", "{\"databaseSchema\":5.5}" })
                Refuse(() => PayloadSchema.Read(NativeJson.Read<JsonElement>(Encoding.UTF8.GetBytes(malformed))));
            return Task.CompletedTask;
        });
        await check("legacy installer receipt without PayloadSchema retains schema five without changing other fields", () =>
        {
            var schemaSix = state with { PayloadSchema = 6 };
            var legacyFields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Json(schemaSix))!;
            Require(legacyFields.Remove(nameof(InstallerState.PayloadSchema)));
            var legacy = NativeJson.Read<InstallerState>(Json(legacyFields));
            Require(legacy.PayloadSchema == 5);
            Require(Json(legacy).SequenceEqual(Json(schemaSix with { PayloadSchema = 5 })));
            return Task.CompletedTask;
        });
        await check("schema six LAN origin transaction preserves six and refuses a silent downgrade to five", () =>
        {
            var schemaSix = state with { PayloadSchema = 6 };
            var snapshot = FixtureSnapshot(schemaSix);
            LanWebConfiguration.Require(snapshot, schemaSix);
            var enabled = LanWebConfiguration.WithOrigin(snapshot, schemaSix, "https://192.168.4.5");
            var activation = NativeJson.Read<InstalledActivation>(enabled.Activation);
            Require(activation.MinimumSchema == 6 && activation.MaximumSchema == 6);
            var disabled = LanWebConfiguration.WithOrigin(enabled, schemaSix, "http://127.0.0.1:" + schemaSix.Port);
            Require(NativeJson.Read<InstalledActivation>(disabled.Activation).MinimumSchema == 6);
            Refuse(() => LanWebConfiguration.Require(enabled, schemaSix with { PayloadSchema = 5 }));
            Refuse(() => LanWebConfiguration.WithOrigin(snapshot, schemaSix with { PayloadSchema = 5 }, "https://192.168.4.5"));
            Refuse(() => LanWebConfiguration.Require(FixtureSnapshot(schemaSix with { PayloadSchema = 5 }), schemaSix));
            return Task.CompletedTask;
        });
        await check("LAN preparation survives the service-created owner-not-yet-saved checkpoint without changing consent", () =>
        {
            var request = Enable();
            var initial = new LanPreparation(1, state.InstallationId, state.OwnerSid, state.PayloadDigest, request);
            LanRecoveryPolicy.RequirePreparation(initial, state, request);
            var created = initial with { ServiceSid = "S-1-5-80-9-8-7-6-5" };
            LanRecoveryPolicy.RequirePreparation(NativeJson.Read<LanPreparation>(Json(created)), state, request);
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { Format = 2 }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { InstallationId = new string('f', 32) }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { OwnerSid = "S-1-5-21-4-3-2-1001" }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { PayloadDigest = new string('f', 64) }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { ServiceSid = state.ServiceSid }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { ServiceSid = "S-1-5-18" }, state, request));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { Request = request with { FirewallConsent = false } }, state));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(initial with { Request = new(LanSetupAction.Disable) }, state));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(created, state, request with { Address = "192.168.4.6" }));
            Refuse(() => LanRecoveryPolicy.RequirePreparation(created, state, request with { HttpHelp = false }));
            return Task.CompletedTask;
        });
        await check("LAN removal receipt alone preserves confirmed mode and identity after owner-record removal", () =>
        {
            var plan = LanDeployment.Create(state.InstallationId, [new("fixture-home", "Synthetic home", "192.168.4.5")],
                "fixture-home", "192.168.4.5", state.Port, LanProxyPaths.Caddy, LanProxyPaths.Data, LanProxyPaths.PublicJoin);
            var owned = new LanOwnedState(1, state.InstallationId, state.OwnerSid, "S-1-5-80-9-8-7-6-5", "disabled",
                "fixture-home", "192.168.4.5", state.Port, true, string.Join(':', Enumerable.Repeat("AB", 32)), plan.FirewallRules.ToArray(), []);
            foreach (var mode in new[] { UninstallMode.KeepData, UninstallMode.DeleteData })
            {
                // No owner.json read callback exists here: this is exactly the
                // surviving external receipt, serialized and read on its own.
                var receipt = NativeJson.Read<LanRemovalIntent>(Json(new LanRemovalIntent(1, mode, owned)));
                LanRecoveryPolicy.RequireRemoval(receipt, state, mode);
                LanRecoveryPolicy.RequireRemoval(receipt, state);
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt, state, mode == UninstallMode.KeepData ? UninstallMode.DeleteData : UninstallMode.KeepData));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { Format = 2 }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { Mode = (UninstallMode)999 }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { State = owned with { OwnerSid = "S-1-5-21-4-3-2-1001" } }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { State = owned with { InstallationId = new string('f', 32) } }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { State = owned with { AppPort = state.Port + 1 } }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { State = owned with { ServiceSid = state.ServiceSid } }, state));
                Refuse(() => LanRecoveryPolicy.RequireRemoval(receipt with { State = owned with { ServiceSid = "S-1-5-18" } }, state));
            }
            return Task.CompletedTask;
        });
        await check("LAN origin transaction preserves data, schema, key, policy, pin and owner intent anchors", () =>
        {
            var after = LanWebConfiguration.WithOrigin(before, state, "https://192.168.4.5");
            var previous = NativeJson.Read<InstalledActivation>(before.Activation);
            var next = NativeJson.Read<InstalledActivation>(after.Activation);
            Require(next with { WebDigest = previous.WebDigest } == previous);
            Require(next.MasterKeyDigest == previous.MasterKeyDigest && next.OwnerIntentDigest == previous.OwnerIntentDigest);
            Require(NativeJson.Read<InstalledWeb>(after.Web) == new InstalledWeb(1, state.InstallationId, "https://192.168.4.5", state.Port));
            var epoch = NativeJson.Read<InstalledEpoch>(after.Epoch); var boot = NativeJson.Read<JsonElement>(after.Bootstrap);
            Require(epoch.ActivationDigest == Hash(after.Activation) && epoch.Epoch == previous.Epoch);
            Require(boot.GetProperty("activationReceiptDigest").GetString() == Hash(after.Activation));
            var restored = LanWebConfiguration.WithOrigin(after, state, "http://127.0.0.1:" + state.Port);
            Require(NativeJson.Read<InstalledActivation>(restored.Activation) == previous);
            return Task.CompletedTask;
        });
        await check("LAN origin transaction refuses public, HTTP LAN, noncanonical and credential-bearing browser URLs", () =>
        {
            foreach (var url in new[] { "https://evil.example", "https://8.8.8.8", "http://192.168.4.5", "https://192.168.4.5:444", "https://user@192.168.4.5", "https://192.168.4.5/path", "https://192.168.4.5/?q=1", "https://192.168.4.5/#x", "http://localhost:" + state.Port, "http://127.0.0.1:47124" })
                Refuse(() => LanWebConfiguration.WithOrigin(before, state, url));
            return Task.CompletedTask;
        });
        await check("LAN journal validates both snapshots and every current control part before recovery", () =>
        {
            var after = LanWebConfiguration.WithOrigin(before, state, "https://192.168.4.5");
            var journal = new LanWebJournal(1, state.InstallationId, before, after);
            var first = LanWebConfiguration.Parts(before); var second = LanWebConfiguration.Parts(after);
            for (var mask = 0; mask < 16; mask++)
            {
                var read = new List<string>();
                LanWebConfiguration.RequireRecoverable(journal, state, name =>
                { read.Add(name); var index = Array.IndexOf(LanWebConfiguration.Names, name); return (mask & (1 << index)) == 0 ? first[index] : second[index]; });
                Require(read.SequenceEqual(LanWebConfiguration.Names));
            }
            foreach (var changed in LanWebConfiguration.Names)
                Refuse(() => LanWebConfiguration.RequireRecoverable(journal, state, name => name == changed ? Encoding.UTF8.GetBytes("changed current control") : first[Array.IndexOf(LanWebConfiguration.Names, name)]));
            Refuse(() => LanWebConfiguration.RequireRecoverable(journal with { Format = 2 }, state, _ => throw new Exception("read before journal validation")));
            Refuse(() => LanWebConfiguration.RequireRecoverable(journal with { InstallationId = new string('f', 32) }, state, _ => throw new Exception("read before identity validation")));
            var foreign = FixtureState() with { InstallationId = new string('f', 32) };
            Refuse(() => LanWebConfiguration.RequireRecoverable(journal with { After = FixtureSnapshot(foreign) }, state, _ => throw new Exception("read before snapshot validation")));
            return Task.CompletedTask;
        });
        await check("LAN snapshot validation rejects a changed anchor or foreign data identity", () =>
        {
            foreach (var part in Enumerable.Range(0, 4))
            {
                var pieces = LanWebConfiguration.Parts(before).Select(p => (byte[])p.Clone()).ToArray(); pieces[part] = Encoding.UTF8.GetBytes("{}");
                try { LanWebConfiguration.Require(new(pieces[0], pieces[1], pieces[2], pieces[3]), state); }
                catch (Exception failure) when (failure is IOException or JsonException or KeyNotFoundException) { continue; }
                throw new Exception("changed anchor admitted");
            }
            Refuse(() => LanWebConfiguration.Require(before, state with { DataId = new string('e', 32) }));
            Refuse(() => LanWebConfiguration.Require(before, state with { PayloadDigest = new string('f', 64) }));
            return Task.CompletedTask;
        });
        await check("LAN snapshot rejects foreign control formats and bootstrap schema or service even with matching hashes", () =>
        {
            var activation = NativeJson.Read<InstalledActivation>(before.Activation);
            var epoch = NativeJson.Read<InstalledEpoch>(before.Epoch);
            LanWebSnapshot Reanchor(InstalledActivation changed)
            {
                var bytes = Json(changed);
                return before with { Activation = bytes, Epoch = Json(epoch with { Epoch = changed.Epoch, ActivationDigest = Hash(bytes) }),
                    Bootstrap = Json(new { schema = 1, service = InstallLocations.ServiceName, installationId = state.InstallationId, activationReceiptDigest = Hash(bytes) }) };
            }
            foreach (var changed in new[] { activation with { Format = 2 }, activation with { Epoch = 0 }, activation with { MinimumSchema = 4 }, activation with { MaximumSchema = 6 } })
                Refuse(() => LanWebConfiguration.Require(Reanchor(changed), state));
            Refuse(() => LanWebConfiguration.Require(before with { Epoch = Json(epoch with { Format = 2 }) }, state));
            Refuse(() => LanWebConfiguration.Require(before with { Bootstrap = Json(new { schema = 2, service = InstallLocations.ServiceName, installationId = state.InstallationId, activationReceiptDigest = Hash(before.Activation) }) }, state));
            Refuse(() => LanWebConfiguration.Require(before with { Bootstrap = Json(new { schema = 1, service = "Foreign.Service", installationId = state.InstallationId, activationReceiptDigest = Hash(before.Activation) }) }, state));
            return Task.CompletedTask;
        });
        await check("LAN native UI renders synthetic RTL choices, explicit consent and preserved uninstall options without showing", RenderUiAsync);
    }

    private static InstallerState FixtureState() => new(1, new string('a', 32), new string('b', 32), new string('c', 64),
        new string('d', 64), new string('e', 40), Owner.Sid, 47123, "committed", [], "S-1-5-80-1-2-3-4-5", Owner.ImageSha256);
    private static LanWebSnapshot FixtureSnapshot(InstallerState state)
    {
        var web = Json(new InstalledWeb(1, state.InstallationId, "http://127.0.0.1:" + state.Port, state.Port));
        var activation = Json(new InstalledActivation(1, state.InstallationId, 7, state.PayloadDigest, state.DataId,
            state.PayloadSchema, state.PayloadSchema, new string('1', 64), new string('2', 64), new string('3', 64), new string('4', 64), Hash(web), "committed", null));
        return new(web, activation, Json(new InstalledEpoch(1, state.InstallationId, 7, Hash(activation))),
            Json(new { schema = 1, service = InstallLocations.ServiceName, installationId = state.InstallationId, activationReceiptDigest = Hash(activation) }));
    }

    private static Task RenderUiAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var app = new App(); app.InitializeComponent();
                var choices = new LanChoice[] { new("fixture-home", "Synthetic home", "192.168.4.5"), new("fixture-other", "Synthetic second adapter", "10.2.3.4") };
                var output = Environment.GetEnvironmentVariable("YAHAD_LAN_UI_OUTPUT");
                if (output is not null) { Require(Path.IsPathFullyQualified(output)); Directory.CreateDirectory(output); }
                foreach (var size in new[] { new Size(980, 730), new Size(760, 620) })
                {
                    var window = new MainWindow(lanChoices: choices);
                    T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    var address = Control<ComboBox>("LanAddress"); var consent = Control<CheckBox>("LanConsent");
                    var primary = Control<Button>("Primary"); var home = Control<RadioButton>("HomeLan");
                    Require(Control<RadioButton>("KeepLan").IsChecked == true && address.SelectedIndex == -1 && primary.IsEnabled);
                    Require(Invoke(window, "SelectedLanRequest") is null);
                    Snapshot(window, size, output, "lan-default");
                    home.IsChecked = true;
                    Require(!primary.IsEnabled && address.SelectedIndex == -1);
                    Refuse(() => Invoke(window, "SelectedLanRequest"));
                    address.SelectedIndex = 0; Require(!primary.IsEnabled && consent.IsChecked != true);
                    consent.IsChecked = true; Require(primary.IsEnabled);
                    var request = (LanSetupRequest)Invoke(window, "SelectedLanRequest")!;
                    Require(request.Action == LanSetupAction.Enable && request.Address == "192.168.4.5" && request.FirewallConsent);
                    address.SelectedIndex = 1; Require(!primary.IsEnabled && consent.IsChecked == false);
                    address.SelectedIndex = 0; consent.IsChecked = true;
                    Control<ScrollViewer>("ContentScroll").ScrollToEnd(); Snapshot(window, size, output, "lan-home-consent");
                    var fingerprint = string.Join(':', Enumerable.Repeat("AB", 32));
                    Invoke(window, "ShowLanResult", new LanSetupResult("https://192.168.4.5", "http://192.168.4.5", fingerprint, true));
                    Require(Control<TextBox>("LanRootFingerprint").Text == fingerprint && Control<TextBox>("LanRootFingerprint").IsReadOnly);
                    Require(Control<Border>("LanResultPanel").Visibility == Visibility.Visible && Control<StackPanel>("LanPanel").Visibility == Visibility.Collapsed);
                    Require(Control<TextBlock>("LanResultText").Text.Contains("https://192.168.4.5", StringComparison.Ordinal));
                    Control<ScrollViewer>("ContentScroll").ScrollToEnd(); Snapshot(window, size, output, "lan-result-host-only");

                    var removal = new MainWindow(uninstall: true, lanChoices: choices);
                    Require(((FrameworkElement)removal.FindName("LanPanel")).Visibility == Visibility.Collapsed);
                    var keep = (RadioButton)removal.FindName("KeepOption"); var delete = (RadioButton)removal.FindName("DeleteOption");
                    var confirmation = (TextBox)removal.FindName("ConfirmationInput"); var remove = (Button)removal.FindName("Primary");
                    Require(keep.IsChecked == true && remove.IsEnabled);
                    Snapshot(removal, size, output, "lan-uninstall-keep");
                    delete.IsChecked = true; Require(!remove.IsEnabled);
                    confirmation.Text = UninstallChoice.ConfirmationPhrase; Require(remove.IsEnabled);
                    ((ScrollViewer)removal.FindName("ContentScroll")).ScrollToEnd(); Snapshot(removal, size, output, "lan-uninstall-delete");
                    keep.IsChecked = true; delete.IsChecked = true; Require(confirmation.Text == "" && !remove.IsEnabled);
                }
                completion.SetResult();
            }
            catch (Exception failure) { completion.SetException(failure); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
    private static object? Invoke(object target, string method, params object[] args)
    {
        try { return target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args); }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }
    private static void Snapshot(MainWindow window, Size size, string? output, string name)
    {
        var content = (FrameworkElement)window.Content; content.Width = size.Width; content.Height = size.Height;
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout(); Require(content.FlowDirection == FlowDirection.RightToLeft);
        Require(!window.IsVisible, "SYNTHETIC_WINDOW_WAS_SHOWN");
        var bitmap = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(content);
        if (output is null) return;
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(output, name + "-" + size.Width + ".png")); png.Save(file);
    }
    private class BasicHost : ISetupHostOperations
    {
        internal readonly List<string> Calls = [];
        internal string? Failure; internal Action? AfterPrepare; internal UninstallMode? Removal;
        protected void Step(string name) { Calls.Add(name); if (Failure == name) throw new IOException("SYNTHETIC_FAILURE"); }
        public Task PrepareAsync(OwnerProcess owner, Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("prepare"); AfterPrepare?.Invoke(); return Task.CompletedTask; }
        public Task PublishServiceAsync(OwnerProcess owner, Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("publish"); return Task.CompletedTask; }
        public Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("start"); return Task.FromResult(new SetupResult(true, "READY", "http://127.0.0.1:47123/", new string('a', 43))); }
        public Task<SetupResult> UninstallAsync(OwnerProcess owner, Action<SetupProgress> progress, CancellationToken cancellation, UninstallMode mode = UninstallMode.KeepData)
        { Step("uninstall:" + mode); Removal = mode; return Task.FromResult(new SetupResult(true, "REMOVED", DataPreserved: mode == UninstallMode.KeepData)); }
    }
    private sealed class NetworkHost : BasicHost, ILanSetupHostOperations
    {
        internal LanSetupRequest? Request; internal OwnerProcess? Owner;
        public Task ConfigureLanAsync(OwnerProcess owner, LanSetupRequest request, Action<SetupProgress> progress, CancellationToken cancellation)
        { request.Require(); Step("lan:" + request.Action); Request = request; Owner = owner; return Task.CompletedTask; }
    }
}
