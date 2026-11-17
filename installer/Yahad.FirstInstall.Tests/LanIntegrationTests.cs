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
    private static readonly SetupOwner Owner = new("S-1-5-21-1-2-3-1001", new string('a', 64));
    private static LanSetupRequest Enable(bool helper = true) => new(LanSetupAction.Enable, "fixture-home", "192.168.4.5", true, helper);

    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("LAN profile admits only Private and refuses Public, Domain and unknown categories",()=>
        {
            NativeNetworkProfile.RequirePrivateCategory(1);
            foreach(var category in new[]{0,2,-1,3,int.MaxValue})Refuse(()=>NativeNetworkProfile.RequirePrivateCategory(category));
            foreach(var adapter in new[]{"","fixture-home",Guid.Empty.ToString(),"{invalid}"})Refuse(()=>NativeNetworkProfile.ReadCategory(adapter));
            Require(NativeNetworkProfile.RequireConsistentCategory([1,1])==1);
            foreach(var categories in new int[][]{[],[1,0],[0,1],[1,2],[2,1]})Refuse(()=>NativeNetworkProfile.RequireConsistentCategory(categories));
            Refuse(()=>NativeNetworkProfile.RequirePrivateCategory(NativeNetworkProfile.RequireConsistentCategory([0,0])));
            return Task.CompletedTask;
        });
        await check("a stopped or never-started LAN service counts as stopped; anything still running or unknown does not",()=>
        {
            LanProxyPolicy.RequireConfirmedStopped(1,0,1077,0);
            LanProxyPolicy.RequireConfirmedStopped(1,0,0,0);
            LanProxyPolicy.RequireConfirmedStopped(1,0,1066,(uint)ServiceFailure.ActivationUnavailable);
            Refuse(()=>LanProxyPolicy.RequireConfirmedStopped(4,0,1077,0));
            Refuse(()=>LanProxyPolicy.RequireConfirmedStopped(1,123,1077,0));
            Refuse(()=>LanProxyPolicy.RequireConfirmedStopped(1,0,1077,1));
            Refuse(()=>LanProxyPolicy.RequireConfirmedStopped(1,0,1067,0));
            Refuse(()=>LanProxyPolicy.RequireConfirmedStopped(1,0,1066,(uint)ServiceFailure.QuiescenceUnknown));
            return Task.CompletedTask;
        });
        await check("a failed LAN cleanup is reported beside the failure that caused it, whose code survives",()=>
        {
            var cause=new IOException("LAN_PAYLOAD_NOT_SUPPORTED");var incomplete=new IOException("LAN_QUIESCENCE_UNCONFIRMED");
            var both=SetupFailureDetail.Besides(cause,incomplete);
            Require(SetupOperation.Code(both)=="LAN_PAYLOAD_NOT_SUPPORTED"&&SetupOperation.Code(new SetupOperationException(UninstallPhase.FullNetwork,both))=="LAN_PAYLOAD_NOT_SUPPORTED");
            Require(both.InnerExceptions.Contains(incomplete));
            return Task.CompletedTask;
        });
        await check("an upgrade starts only an enabled proxy whose address is still here",()=>
        {
            var receipt=new LanProxyReceipt("adapter-home","192.168.1.50");var here=new[]{new AdapterAddress("adapter-home","Home","192.168.1.50",true)};
            Require(LanProxyPolicy.ShouldRun(2,receipt,here));
            foreach(var startType in new uint[]{3,4})Require(!LanProxyPolicy.ShouldRun(startType,receipt,here));
            Require(!LanProxyPolicy.ShouldRun(2,null,here));
            Require(!LanProxyPolicy.ShouldRun(2,receipt,[new("adapter-home","Home","192.168.1.51",true)]));
            return Task.CompletedTask;
        });
        await check("a LAN firewall rule moves only between the two releases of an upgrade, and reads back as Windows reports it",()=>
        {
            var rule=new LanFirewallRule("n","d","g",@"C:\v2\caddy.exe","192.168.1.50","443");
            Require(rule.MatchesEitherRelease(rule,null));
            Require(rule.MatchesEitherRelease(rule with{ApplicationName=@"C:\v1\caddy.exe"},@"C:\v1\caddy.exe"));
            Require(!rule.MatchesEitherRelease(rule with{ApplicationName=@"C:\v1\caddy.exe"},null));
            Require(!rule.MatchesEitherRelease(rule with{ApplicationName=@"C:\other.exe"},@"C:\v1\caddy.exe"));
            Require(!rule.MatchesEitherRelease(rule with{ApplicationName=@"C:\v1\caddy.exe",LocalAddresses="*"},@"C:\v1\caddy.exe"));
            Require(NativeLanFirewall.SingleAddress("192.168.1.50/255.255.255.255")=="192.168.1.50"&&NativeLanFirewall.SingleAddress("192.168.1.50")=="192.168.1.50");
            Require(NativeLanFirewall.SingleAddress("192.168.1.0/255.255.255.0")=="192.168.1.0/255.255.255.0");
            return Task.CompletedTask;
        });
        await check("LAN request requires explicit consent, canonical private address and well-formed action", () =>
        {
            Enable().Require(); Enable(false).Require();
            new LanSetupRequest(LanSetupAction.Disable).Require();
            Refuse(() => (Enable() with { FirewallConsent = false }).Require());
            foreach (var adapter in new[] { "", "  ", "home\n", new string('x', 257) })
                Refuse(() => (Enable() with { AdapterId = adapter }).Require());
            foreach (var address in new[] { "0.0.0.0", "127.0.0.1", "8.8.8.8", "169.254.1.2", "::1", "192.168.004.5", "192.168.4.5:443", "192.168.4.5\n" })
                Refuse(() => (Enable() with { Address = address }).Require());
            Refuse(() => (Enable() with { Address = null }).Require());
            Refuse(() => new LanSetupRequest((LanSetupAction)999).Require());
            Refuse(() => new LanSetupRequest(LanSetupAction.Disable, Address: "192.168.4.5").Require());
            Refuse(() => new LanSetupRequest(LanSetupAction.Disable, FirewallConsent: true).Require());
            return Task.CompletedTask;
        });
        await check("installer passes validated LAN request and owner between publication and server observation", async () =>
        {
            var request = Enable(false); var host = new NetworkHost(); var phases = new List<string>();
            var result = await new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default, lan: request);
            Require(host.Calls.SequenceEqual(["prepare", "provision", "lan:Enable", "start", "commit"]));
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
        foreach (var failure in new[] { "prepare", "provision", "lan:Enable", "start", "commit" })
            await check("LAN install failure at " + failure + " cannot reach browser-ready or uninstall", async () =>
            {
                var host = new NetworkHost { Failure = failure }; var phases = new List<string>();
                await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, p => phases.Add(p.Phase), default, lan: Enable()));
                var full = new[] { "prepare", "provision", "lan:Enable", "start", "commit" };
                Require(host.Calls.SequenceEqual(full.Take(Array.IndexOf(full, failure) + 1)));
                Require(!phases.Contains("ready") && !host.Calls.Any(c => c.StartsWith("uninstall", StringComparison.Ordinal)));
            });
        await check("LAN selection cancellation prevents subsequent host operation", async () =>
        {
            using var cancel = new CancellationTokenSource(); var host = new NetworkHost { AfterPrepare = cancel.Cancel };
            await RefuseAsync(() => new InstallFlow(host).RunAsync(Owner, false, _ => { }, cancel.Token, lan: Enable()));
            Require(host.Calls.SequenceEqual(["prepare"]));
        });
        await check("omitted LAN preserves ordinary install while disable is explicitly conveyed", async () =>
        {
            var host = new NetworkHost(); await new InstallFlow(host).RunAsync(Owner, false, _ => { }, default);
            Require(host.Calls.SequenceEqual(["prepare", "provision", "start", "commit"]) && host.Request is null);
            var disableHost = new NetworkHost();
            await new InstallFlow(disableHost).RunAsync(Owner, false, _ => { }, default, lan: new(LanSetupAction.Disable));
            Require(disableHost.Calls.SequenceEqual(["prepare", "provision", "lan:Disable", "start", "commit"]));
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

        await check("LAN preparation resumes only the owner's exact selection", () =>
        {
            var request = Enable();
            var prepared = NativeJson.Read(Json(request),SetupJsonContext.Default.LanSetupRequest);
            NativeLanInstall.RequireSameSelection(prepared, request);
            Refuse(() => NativeLanInstall.RequireSameSelection(prepared, request with { Address = "192.168.4.6" }));
            Refuse(() => NativeLanInstall.RequireSameSelection(prepared, request with { HttpHelp = false }));
            return Task.CompletedTask;
        });
        await check("LAN removal receipt alone preserves the confirmed mode after owner-record removal", () =>
        {
            var owned = new LanOwnedState("disabled", "fixture-home", "192.168.4.5", true, string.Join(':', Enumerable.Repeat("AB", 32)));
            foreach (var mode in new[] { UninstallMode.KeepData, UninstallMode.DeleteData })
            {
                // No owner.json read callback exists here: this is exactly the
                // surviving external receipt, serialized and read on its own.
                var receipt = NativeJson.Read(Json(new LanRemovalIntent(mode, owned)),SetupJsonContext.Default.LanRemovalIntent);
                Require(receipt.State.Address == owned.Address && receipt.State.RootSha256 == owned.RootSha256);
                NativeLanInstall.RequireRemovalMode(receipt, mode);
                Refuse(() => NativeLanInstall.RequireRemovalMode(receipt, mode == UninstallMode.KeepData ? UninstallMode.DeleteData : UninstallMode.KeepData));
            }
            return Task.CompletedTask;
        });
        await check("LAN native UI renders synthetic RTL choices, explicit consent and preserved uninstall options without showing", RenderUiAsync);
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
                    var categories = new Dictionary<string, int> { ["fixture-home"] = 1, ["fixture-other"] = 1 };
                    int Category(string adapter) => categories.TryGetValue(adapter, out var c) ? c : throw new IOException("LAN_NETWORK_PROFILE_UNAVAILABLE");
                    var window = new MainWindow(lanChoices: choices, lanCategory: Category);
                    T Control<T>(string name) where T : FrameworkElement => (T)window.FindName(name);
                    var address = Control<ComboBox>("LanAddress"); var consent = Control<CheckBox>("LanConsent");
                    var primary = Control<Button>("Primary"); var home = Control<RadioButton>("HomeLan");
                    Require(Control<RadioButton>("KeepLan").IsChecked == true && address.SelectedIndex == -1 && primary.IsEnabled);
                    Require((string)Control<RadioButton>("KeepLan").Content=="רק במחשב הזה");
                    Require(Control<RadioButton>("DisableLan").Visibility==Visibility.Collapsed&&Control<TextBlock>("DisableLanHint").Visibility==Visibility.Collapsed);
                    Require(!Control<Expander>("LanTechnicalDetails").IsExpanded);
                    Require(Invoke(window, "SelectedLanRequest") is null);
                    Control<RadioButton>("DisableLan").IsChecked=true;Refuse(()=>Invoke(window,"SelectedLanRequest"));
                    Control<RadioButton>("KeepLan").IsChecked=true;
                    Snapshot(window, size, output, "lan-default");
                    home.IsChecked = true;
                    Require(!primary.IsEnabled && address.SelectedIndex == -1);
                    Refuse(() => Invoke(window, "SelectedLanRequest"));
                    address.SelectedIndex = 0; Require(!primary.IsEnabled && consent.IsChecked != true);
                    consent.IsChecked = true; Require(primary.IsEnabled);
                    var request = (LanSetupRequest)Invoke(window, "SelectedLanRequest")!;
                    Require(request.Action == LanSetupAction.Enable && request.Address == "192.168.4.5" && request.FirewallConsent);
                    Control<CheckBox>("LanHttpHelp").IsChecked=false;
                    Require(((LanSetupRequest)Invoke(window,"SelectedLanRequest")!).HttpHelp==false);
                    Control<CheckBox>("LanHttpHelp").IsChecked=true;
                    Require(!Control<TextBlock>("LanConsentLabel").Text.Contains("TCP",StringComparison.Ordinal)&&Control<TextBlock>("LanConsentLabel").Text.Contains("אני מאשר",StringComparison.Ordinal));
                    address.SelectedIndex = 1; Require(!primary.IsEnabled && consent.IsChecked == false);
                    address.SelectedIndex = 0; consent.IsChecked = true;
                    // A Public network is explained before anything is installed; the
                    // consent area hides and the primary action waits for Private.
                    var help = Control<Border>("LanProfileHelp"); var consentArea = Control<StackPanel>("LanConsentArea");
                    Require(help.Visibility == Visibility.Collapsed && consentArea.Visibility == Visibility.Visible && primary.IsEnabled);
                    categories["fixture-home"] = 0; Invoke(window, "RecheckNetwork_Click", window, new RoutedEventArgs());
                    Require(help.Visibility == Visibility.Visible && consentArea.Visibility == Visibility.Collapsed && !primary.IsEnabled);
                    Require(Control<TextBlock>("LanProfileHeading").Text.Contains("ציבורית", StringComparison.Ordinal));
                    Require(Control<StackPanel>("LanProfileSteps").Visibility == Visibility.Visible && Control<Button>("OpenNetworkSettings").Visibility == Visibility.Visible);
                    Require(Invoke(window, "ReadProfile", "fixture-home")!.ToString() == "Public");
                    Control<ScrollViewer>("ContentScroll").ScrollToEnd(); Snapshot(window, size, output, "lan-public-network");
                    Control<RadioButton>("KeepLan").IsChecked = true; Require(primary.IsEnabled && help.Visibility == Visibility.Collapsed);
                    home.IsChecked = true; Require(!primary.IsEnabled && help.Visibility == Visibility.Visible);
                    categories["fixture-home"] = 2; Invoke(window, "RecheckNetwork_Click", window, new RoutedEventArgs());
                    Require(!primary.IsEnabled && Control<Button>("OpenNetworkSettings").Visibility == Visibility.Collapsed && Control<TextBlock>("LanProfileHeading").Text.Contains("עבודה", StringComparison.Ordinal));
                    categories.Remove("fixture-home"); Invoke(window, "RecheckNetwork_Click", window, new RoutedEventArgs());
                    Require(!primary.IsEnabled && help.Visibility == Visibility.Visible && Control<Button>("RecheckNetwork").Visibility == Visibility.Visible);
                    categories["fixture-home"] = 1; Invoke(window, "RecheckNetwork_Click", window, new RoutedEventArgs());
                    Require(help.Visibility == Visibility.Collapsed && consentArea.Visibility == Visibility.Visible && consent.IsChecked == true && primary.IsEnabled);
                    Require(((LanSetupRequest)Invoke(window, "SelectedLanRequest")!).AdapterId == "fixture-home");
                    Control<ScrollViewer>("ContentScroll").ScrollToEnd(); Snapshot(window, size, output, "lan-home-consent");
                    var fingerprint = string.Join(':', Enumerable.Repeat("AB", 32));
                    Invoke(window, "ShowLanResult", new LanSetupResult("https://192.168.4.5", "http://192.168.4.5", fingerprint));
                    Require(Control<TextBox>("LanRootFingerprint").Text == fingerprint && Control<TextBox>("LanRootFingerprint").IsReadOnly);
                    Require(Control<Border>("LanResultPanel").Visibility == Visibility.Visible && Control<StackPanel>("LanPanel").Visibility == Visibility.Collapsed);
                    Require(Control<TextBlock>("LanResultText").Text.Contains("https://192.168.4.5", StringComparison.Ordinal));
                    Control<ScrollViewer>("ContentScroll").ScrollToEnd(); Snapshot(window, size, output, "lan-result-host-only");

                    var existing=new MainWindow(lanChoices:choices,existingAccess:true,lanCategory:Category);
                    Require((string)((RadioButton)existing.FindName("KeepLan")).Content=="להשאיר את אפשרויות הגישה הקיימות");
                    Require(Invoke(existing,"SelectedLanRequest") is null);
                    var disable=(RadioButton)existing.FindName("DisableLan");Require(disable.Visibility==Visibility.Visible);
                    disable.IsChecked=true;Require(((LanSetupRequest)Invoke(existing,"SelectedLanRequest")!).Action==LanSetupAction.Disable);
                    ((RadioButton)existing.FindName("KeepLan")).IsChecked=true;Require(Invoke(existing,"SelectedLanRequest") is null);
                    ((ScrollViewer)existing.FindName("ContentScroll")).ScrollToEnd();Snapshot(existing,size,output,"lan-existing-access");

                    // A failure shows the catalog's words, the support code, and the record
                    // collapsed; no record is written outside the synthetic writer.
                    const string log=@"C:\ProgramData\Yahad.FirstInstall.setup-logs\setup-20261117T101530000Z-0a1b2c3d.json";
                    var written=new List<SetupFailureReport>();
                    var failed=new MainWindow(lanChoices:choices,lanCategory:Category,failureLog:report=>{written.Add(report);return log;});
                    T Failed<T>(string name) where T:FrameworkElement=>(T)failed.FindName(name);
                    var service=SetupErrorCatalog.Resolve("SERVICE_START_FAILED");
                    Invoke(failed,"Failure","SERVICE_START_FAILED",new SetupFailureDetail("FullStartService",1053,unchecked((int)0x8007041D),Exception:"System.ComponentModel.Win32Exception"));
                    Require(written.Count==1&&written[0] is {Code:"SERVICE_START_FAILED",Operation:"install",Phase:"FullStartService",Win32:1053});
                    Require(Failed<Border>("FailurePanel").Visibility==Visibility.Visible&&Failed<Border>("Notice").Visibility==Visibility.Collapsed);
                    Require(Failed<TextBlock>("FailureTitle").Text=="ההתקנה לא הושלמה"&&Failed<TextBlock>("FailureWhat").Text==service.What);
                    Require(Failed<TextBlock>("SupportCode").Text=="קוד לתמיכה: SERVICE_START_FAILED"&&Failed<TextBlock>("Description").Text==service.What&&Failed<TextBlock>("FailureNext").Text==service.Next);
                    Require(!Failed<Expander>("FailureTechnical").IsExpanded&&Failed<TextBox>("FailureTechnicalText").Text.Contains(log,StringComparison.Ordinal)&&Failed<TextBox>("FailureTechnicalText").FlowDirection==FlowDirection.LeftToRight);
                    Require(Failed<Button>("FailureAction").Visibility==(service.Action==SetupErrorAction.None?Visibility.Collapsed:Visibility.Visible)&&(string)Failed<Button>("Primary").Content=="ניסיון נוסף");
                    Reveal(failed,size);Snapshot(failed,size,output,"setup-failure");
                    Invoke(failed,"Failure","LAN_PRIVATE_PROFILE_REQUIRED",new SetupFailureDetail("LanConfigure",null,unchecked((int)0x80131620)));
                    Require(Failed<Button>("FailureAction").Visibility==Visibility.Visible&&(string)Failed<Button>("FailureAction").Content==SetupErrorCatalog.Label(SetupErrorAction.OpenNetworkSettings)&&written.Count==2);
                    Reveal(failed,size);Snapshot(failed,size,output,"setup-failure-network");
                    // A refused choice is a failure screen too, never an exception out of the click.
                    var refused=new MainWindow(lanChoices:choices,lanCategory:Category,failureLog:report=>{written.Add(report);return null;});
                    ((RadioButton)refused.FindName("HomeLan")).IsChecked=true;((ComboBox)refused.FindName("LanAddress")).SelectedIndex=0;
                    Invoke(refused,"Start_Click",refused,new RoutedEventArgs());
                    Require(written[^1].Code=="LAN_CONSENT_REQUIRED"&&((Border)refused.FindName("FailurePanel")).Visibility==Visibility.Visible&&((TextBlock)refused.FindName("SupportCode")).Text.EndsWith("LAN_CONSENT_REQUIRED",StringComparison.Ordinal));
                    Require(!((TextBox)refused.FindName("FailureTechnicalText")).Text.Contains("Log:",StringComparison.Ordinal));

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

                    // Removal uses the same catalog and screen; a stopped data deletion says it may be partial.
                    var stopped=new MainWindow(uninstall:true,lanChoices:choices,failureLog:report=>{written.Add(report);return log;});
                    ((RadioButton)stopped.FindName("DeleteOption")).IsChecked=true;((TextBox)stopped.FindName("ConfirmationInput")).Text=UninstallChoice.ConfirmationPhrase;
                    Invoke(stopped,"Failure","FILE_LOCKED",new SetupFailureDetail("DeleteData",32,unchecked((int)0x80070020),Exception:"System.IO.IOException"));
                    Require(written[^1] is {Code:"FILE_LOCKED",Operation:"uninstall"}&&((TextBlock)stopped.FindName("Heading")).Text=="ההסרה נעצרה.");
                    Require(((TextBlock)stopped.FindName("FailureTitle")).Text=="ההסרה נעצרה"&&((TextBlock)stopped.FindName("FailureWhat")).Text==SetupErrorCatalog.Resolve("FILE_LOCKED").What);
                    Require(((TextBlock)stopped.FindName("FailureContext")).Visibility==Visibility.Visible&&(string)((Button)stopped.FindName("Primary")).Content=="המשך המחיקה");
                    Require(((Button)stopped.FindName("FailureAction")).Visibility==(SetupErrorCatalog.Resolve("FILE_LOCKED").Action is SetupErrorAction.None or SetupErrorAction.ContinueRemoval?Visibility.Collapsed:Visibility.Visible));
                    Reveal(stopped,size);Snapshot(stopped,size,output,"setup-uninstall-failure");
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
    // Lays the window out at this size, runs the window's own reveal (queued after layout
    // when shown), and requires the whole failure card inside the scrolled viewport.
    private static void Reveal(MainWindow window, Size size)
    {
        var content = (FrameworkElement)window.Content; content.Width = size.Width; content.Height = size.Height;
        content.Measure(size); content.Arrange(new Rect(size)); content.UpdateLayout();
        var scroll = (ScrollViewer)window.FindName("ContentScroll"); scroll.ScrollToTop(); content.UpdateLayout();
        Invoke(window, "RevealFailure"); content.UpdateLayout();
        var card = (FrameworkElement)window.FindName("FailurePanel");
        var bounds = card.TransformToAncestor(scroll).TransformBounds(new Rect(card.RenderSize));
        Require(bounds.Top >= -0.5 && bounds.Bottom <= scroll.ViewportHeight + 0.5, "FAILURE_CARD_OUT_OF_VIEW");
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
    private sealed class NetworkHost : ISetupHostOperations
    {
        internal readonly List<string> Calls = [];
        internal string? Failure; internal Action? AfterPrepare; internal UninstallMode? Removal;
        internal LanSetupRequest? Request; internal SetupOwner? Owner;
        private void Step(string name) { Calls.Add(name); if (Failure == name) throw new IOException("SYNTHETIC_FAILURE"); }
        public Task PrepareAsync(SetupOwner owner, Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("prepare"); Owner = owner; AfterPrepare?.Invoke(); return Task.CompletedTask; }
        public Task ProvisionAsync(Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("provision"); return Task.CompletedTask; }
        public Task ConfigureLanAsync(LanSetupRequest request, Action<SetupProgress> progress, CancellationToken cancellation)
        { request.Require(); Step("lan:" + request.Action); Request = request; return Task.CompletedTask; }
        public Task<SetupResult> StartAndObserveAsync(Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("start"); return Task.FromResult(new SetupResult(true, "READY", "http://127.0.0.1:47123/", new string('a', 43))); }
        public Task CommitAsync(Action<SetupProgress> progress, CancellationToken cancellation)
        { Step("commit"); return Task.CompletedTask; }
        public Task<SetupResult> UninstallAsync(Action<SetupProgress> progress, CancellationToken cancellation, UninstallMode mode = UninstallMode.KeepData)
        { Step("uninstall:" + mode); Removal = mode; return Task.FromResult(new SetupResult(true, "REMOVED", DataPreserved: mode == UninstallMode.KeepData)); }
    }
}
