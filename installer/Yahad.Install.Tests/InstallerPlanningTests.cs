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
    }
}
