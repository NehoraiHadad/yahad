using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Yahad.Install;

internal static class LanDeploymentTests
{
    public static async Task Run(Func<string, Func<Task>, Task> check)
    {
        const string id = "0123456789abcdef0123456789abcdef";
        var choices = new LanChoice[] { new("home", "Home", "192.168.4.5"), new("vpn", "VPN", "10.10.10.10") };
        LanDeploymentPlan Plan(bool helper = true) => LanDeployment.Create(id, choices, "home", "192.168.4.5", 47123,
            @"C:\Program Files\Yahad\runtime\caddy.exe", @"C:\ProgramData\Yahad\lan\ca", @"C:\ProgramData\Yahad\lan\join-public", helper);
        void Assert(bool value) { if (!value) throw new Exception("assertion failed"); }
        void Reject(Action action)
        { try { action(); } catch (InvalidDataException) { return; } throw new Exception("expected refusal"); }

        await check("LAN deployment preserves exact selected adapter and never selects VPN automatically", () =>
        {
            var plan = Plan();
            Assert(plan.AdapterId == "home" && plan.Address == "192.168.4.5" && plan.Origin == "https://192.168.4.5");
            Assert(plan.HelpOrigin == "http://192.168.4.5" && !plan.Caddyfile.Contains("10.10.10.10", StringComparison.Ordinal));
            Reject(() => LanDeployment.Create(id, choices, "vpn", "192.168.4.5", 47123, @"C:\caddy.exe", @"C:\ca", @"C:\join"));
            Reject(() => LanDeployment.Create(id, choices, "home", "8.8.8.8", 47123, @"C:\caddy.exe", @"C:\ca", @"C:\join"));
            return Task.CompletedTask;
        });
        await check("LAN resume detects address disappearance without adopting another private address", () =>
        {
            var plan = Plan(); LanDeployment.RequireSelectedAdapter(plan, choices);
            Reject(() => LanDeployment.RequireSelectedAdapter(plan, [choices[1]]));
            Reject(() => LanDeployment.RequireSelectedAdapter(plan, [new("home", "Home", "192.168.4.6")]));
            return Task.CompletedTask;
        });
        await check("LAN Caddy generation pins private bind, loopback upstream and local CA without trust installation", () =>
        {
            var config = Plan().Caddyfile;
            Assert(config.Contains("https://192.168.4.5 {\n    bind 192.168.4.5\n    tls internal", StringComparison.Ordinal));
            Assert(config.Contains("reverse_proxy 127.0.0.1:47123", StringComparison.Ordinal));
            Assert(config.Contains("flush_interval -1", StringComparison.Ordinal));
            Assert(config.Contains("skip_install_trust", StringComparison.Ordinal) && config.Contains("admin off", StringComparison.Ordinal));
            Assert(config.Contains("persist_config off", StringComparison.Ordinal) && config.Contains("auto_https disable_redirects", StringComparison.Ordinal));
            Assert(config.Contains("log {\n        output discard\n    }", StringComparison.Ordinal));
            Assert(!config.Contains("log {\n        output file", StringComparison.Ordinal));
            Assert(config.Contains("protocols h1 h2", StringComparison.Ordinal) && !config.Contains("h3", StringComparison.Ordinal));
            Assert(config.Contains("@ownerOnly path /health /health/* /api/setup /api/setup/*", StringComparison.Ordinal));
            Assert(!config.Contains("0.0.0.0", StringComparison.Ordinal) && !config.Contains("acme", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("LAN HTTP helper exposes only two public files and has no application proxy", () =>
        {
            var config = Plan().Caddyfile; var helper = config[config.IndexOf("http://", StringComparison.Ordinal)..];
            Assert(helper.Contains("@help path / /index.html", StringComparison.Ordinal));
            Assert(helper.Contains("handle /certificate.crt", StringComparison.Ordinal));
            Assert(helper.Contains("rewrite * /certificate.crt", StringComparison.Ordinal));
            Assert(helper.Contains("respond 404", StringComparison.Ordinal));
            Assert(helper.Contains("@unsupportedMethod not method GET HEAD", StringComparison.Ordinal));
            Assert(helper.Contains("route {\n        respond @unsupportedMethod 405\n        handle @help", StringComparison.Ordinal));
            Assert(!helper.Contains("reverse_proxy", StringComparison.Ordinal) && !helper.Contains("/api/", StringComparison.Ordinal));
            Assert(!helper.Contains("/lan/ca", StringComparison.Ordinal));
            Assert(helper.Contains("form-action 'none'", StringComparison.Ordinal));
            var noHelp = Plan(false);
            Assert(noHelp.HelpOrigin is null && noHelp.FirewallRules.Count == 1 && !noHelp.Caddyfile.Contains("http://", StringComparison.Ordinal));
            return Task.CompletedTask;
        });
        await check("LAN firewall is application-bound TCP Private LocalSubnet and rejects same-name drift", () =>
        {
            var plan = Plan(); Assert(plan.FirewallRules.Count == 2);
            foreach (var rule in plan.FirewallRules)
            {
                Assert(rule.Profiles == 2 && rule.Protocol == 6 && rule.Direction == 1 && rule.Action == 1 && rule.Enabled);
                Assert(!rule.EdgeTraversal && rule.RemoteAddresses == "LocalSubnet" && rule.LocalAddresses == "192.168.4.5");
                Assert(rule.ApplicationName == plan.CaddyExecutable && rule.Name.StartsWith("Yahad.LAN." + id, StringComparison.Ordinal));
                Assert(rule.Matches(rule with { }));
                Assert(!rule.Matches(rule with { Profiles = 7 }));
                Assert(!rule.Matches(rule with { RemoteAddresses = "*" }));
                Assert(!rule.Matches(rule with { ApplicationName = @"C:\Other\caddy.exe" }));
                Assert(!rule.Matches(rule with { LocalPorts = "80,443" }));
                Assert(!rule.Matches(rule with { LocalAddresses = "*" }));
                Assert(!rule.Matches(rule with { EdgeTraversal = true }));
            }
            Assert(plan.FirewallRules[0].LocalPorts == "443" && plan.FirewallRules[1].LocalPorts == "80");
            return Task.CompletedTask;
        });
        await check("LAN Caddy paths reject interpolation, control tokens, device paths and public-private overlap", () =>
        {
            LanDeploymentPlan With(string storage, string join, string executable = @"C:\Tools\caddy.exe") =>
                LanDeployment.Create(id, choices, "home", "192.168.4.5", 47123, executable, storage, join);
            foreach (var bad in new[] { "relative", @"\\server\share\ca", "C:\\ca\nadmin on", @"C:\ca{env.SECRET}", @"C:\ca`bad", "C:\\ca\"bad", @"C:\ca:stream", @"C:\ca*", @"C:\ca?", @"C:\ca|bad" })
                Reject(() => With(bad, @"C:\join"));
            Reject(() => With(@"C:\private", @"C:\private\public"));
            Reject(() => With(@"C:\public\private", @"C:\public"));
            Reject(() => With(@"C:\same", @"C:\same"));
            Reject(() => With(@"C:\Tools", @"C:\join"));
            Reject(() => With(@"C:\ca", @"C:\join", @"C:\Tools\caddy.cmd"));
            Reject(() => LanDeployment.Create("not-a-guid", choices, "home", "192.168.4.5", 47123, @"C:\caddy.exe", @"C:\ca", @"C:\join"));
            Reject(() => LanDeployment.Create(id, choices, "home", "192.168.4.5", 443, @"C:\caddy.exe", @"C:\ca", @"C:\join"));
            Assert(With(@"C:\A B\ca", @"C:\A B\join").Caddyfile.Contains("root \"C:/A B/ca\"", StringComparison.Ordinal));
            return Task.CompletedTask;
        });

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Yahad synthetic root", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        using var root = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
        var der = root.Export(X509ContentType.Cert); var pem = root.ExportCertificatePem();
        await check("LAN public CA export re-encodes certificate only and exposes a stable independent fingerprint", () =>
        {
            var fromDer = LanCertificate.ExportPublicRoot(der);
            var fromPem = LanCertificate.ExportPublicRoot(Encoding.UTF8.GetBytes(pem));
            Assert(fromDer.Der.SequenceEqual(der) && fromPem.Der.SequenceEqual(der));
            Assert(fromDer.Sha256Fingerprint == fromPem.Sha256Fingerprint && fromDer.Sha256Fingerprint.Length == 95);
            var supplied = fromDer.Der; supplied[0] ^= 255;
            Assert(fromDer.Der.SequenceEqual(der));
            using var decoded = X509CertificateLoader.LoadCertificate(fromDer.Der);
            Assert(!decoded.HasPrivateKey);
            return Task.CompletedTask;
        });
        await check("LAN public CA export refuses whole mixed private-key files, PFX, trailing bytes and leaf certificates", () =>
        {
            Reject(() => LanCertificate.ExportPublicRoot(Encoding.UTF8.GetBytes(pem + "\n" + key.ExportRSAPrivateKeyPem())));
            Reject(() => LanCertificate.ExportPublicRoot(root.Export(X509ContentType.Pkcs12, "fixture")));
            Reject(() => LanCertificate.ExportPublicRoot([..der, 0]));
            Reject(() => LanCertificate.ExportPublicRoot(Encoding.UTF8.GetBytes(pem + "\n" + pem)));
            Reject(() => LanCertificate.ExportPublicRoot(new byte[65537]));
            var leafRequest = new CertificateRequest("CN=Yahad synthetic leaf", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var leaf = leafRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(2));
            Reject(() => LanCertificate.ExportPublicRoot(leaf.Export(X509ContentType.Cert)));
            return Task.CompletedTask;
        });
    }
}
