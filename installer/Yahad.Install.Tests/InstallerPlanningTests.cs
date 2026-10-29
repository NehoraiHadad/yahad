using System.Text.Json;
using Yahad.Install;

internal static class InstallerPlanningTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Reject(Action action) { try { action(); } catch (InvalidOperationException) { return; } throw new Exception("expected refusal"); }
        await check("LAN plan cannot authorize network actions or Internet exposure", () =>
        {
            var plan = AccessPlanner.Build([new("home", "Fixture LAN", "192.168.4.5")], "home", "192.168.4.5");
            Assert(plan.CurrentLanOrigin == "https://192.168.4.5" && !plan.InternetEnabled && !plan.ActionsAuthorized && !plan.StableNameProvisioned);
            Assert(plan.FirewallProfile == "Private" && plan.FirewallRemoteScope == "LocalSubnet" && plan.TrustRequirement == "explicit-trust-on-each-device");
            return Task.CompletedTask;
        });
        await check("unselected managed certificate provider stays blocked", () =>
        { Reject(() => AccessPlanner.Build([], "home", "192.168.4.5", CertificateStrategy.ManagedDns01Reserved)); return Task.CompletedTask; });
        await check("host health cannot claim other-device web reachability", () =>
        {
            Assert(!AccessPlanner.IsInstallerComplete(new(true, false, false, true)));
            Assert(!AccessPlanner.IsInstallerComplete(new(true, true, false, false)));
            Assert(AccessPlanner.IsInstallerComplete(new(true, true, false, true)));
            Assert(!AccessPlanner.IsEndToEndComplete(new(true, true, false, true)));
            Assert(AccessPlanner.IsEndToEndComplete(new(true, true, true, true)));
            return Task.CompletedTask;
        });
        var now = DateTimeOffset.UtcNow; var token = new string('a', 43);
        await check("existing family handoff discards obsolete setup link", () =>
        { var link = WebHandoff.Create(new("https://192.168.4.5"), WebSetupState.Complete, token, now.AddMinutes(5), now); Assert(link.ExistingFamily && link.GetLaunchUri().Fragment == ""); return Task.CompletedTask; });
        await check("fresh handoff reuses web fragment without serializing secrets", () =>
        {
            var link = WebHandoff.Create(new("http://127.0.0.1:3000"), WebSetupState.Ready, token, now.AddMinutes(5), now);
            Assert(link.GetLaunchUri().Fragment == "#setup=" + token && link.GetLaunchUri().Query == "");
            Assert(!link.ToString().Contains(token) && !JsonSerializer.Serialize(link).Contains(token)); return Task.CompletedTask;
        });
        await check("expired or closed setup never reopens family onboarding", () =>
        {
            Reject(() => WebHandoff.Create(new("http://127.0.0.1:3000"), WebSetupState.Ready, token, now.AddSeconds(-1), now));
            Reject(() => WebHandoff.Create(new("http://127.0.0.1:3000"), WebSetupState.Closed, token, now.AddMinutes(1), now));
            return Task.CompletedTask;
        });
        await check("public or insecure LAN handoff is refused", () =>
        {
            Reject(() => WebHandoff.Create(new("http://192.168.4.5"), WebSetupState.Complete, null, null, now));
            Reject(() => WebHandoff.Create(new("https://example.com"), WebSetupState.Complete, null, null, now));
            return Task.CompletedTask;
        });
    }
}
