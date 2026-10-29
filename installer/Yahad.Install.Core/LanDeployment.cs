using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Yahad.Install;

/// <summary>
/// A fully bounded firewall rule, not an instruction to create one. The privileged
/// installer must inspect an existing same-name rule and require an exact match
/// before either reusing or removing it. A name alone never proves ownership.
/// </summary>
public sealed record LanFirewallRule(
    string Name, string Description, string Grouping, string ApplicationName,
    string LocalAddresses, string LocalPorts, string RemoteAddresses = "LocalSubnet",
    string RemotePorts = "*", string InterfaceTypes = "All", int Profiles = 2,
    int Protocol = 6, int Direction = 1, int Action = 1, bool Enabled = true,
    bool EdgeTraversal = false, string ServiceName = "")
{
    public bool Matches(LanFirewallRule observed) => this == observed;
}

/// <summary>No plan is consent. The caller must obtain explicit owner consent and recheck the adapter before execution.</summary>
public sealed record LanDeploymentPlan(
    string InstallationId, string AdapterId, string Address, int AppPort,
    string Origin, string? HelpOrigin, string CaddyExecutable, string StorageDirectory,
    string PublicJoinDirectory, string Caddyfile, IReadOnlyList<LanFirewallRule> FirewallRules);

/// <summary>
/// Pure LAN deployment description. This class never starts a listener/process,
/// changes a firewall, installs certificate trust, or contacts a provider.
/// </summary>
public static class LanDeployment
{
    public const int HttpsPort = 443;
    public const int HttpHelpPort = 80;

    public static LanDeploymentPlan Create(string installationId, IReadOnlyList<LanChoice> choices,
        string selectedAdapterId, string selectedAddress, int appPort, string caddyExecutable,
        string storageDirectory, string publicJoinDirectory, bool enableHttpHelp = true)
    {
        if (!Guid.TryParseExact(installationId, "N", out var installation) || installation.ToString("N") != installationId)
            throw new InvalidDataException("LAN_INSTALLATION_ID");
        if (string.IsNullOrWhiteSpace(selectedAdapterId) || selectedAdapterId.Length > 256)
            throw new InvalidDataException("LAN_ADAPTER");
        var origin = NetworkPlan.LanOrigin(choices, selectedAdapterId, selectedAddress);
        if (appPort is < 1024 or > 65535) throw new InvalidDataException("LAN_APP_PORT");
        var executable = SafePath(caddyExecutable);
        if (!executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("LAN_CADDY_EXECUTABLE");
        var storage = SafePath(storageDirectory);
        var publicJoin = SafePath(publicJoinDirectory);
        if (Overlaps(storage, publicJoin)) throw new InvalidDataException("LAN_PUBLIC_PRIVATE_OVERLAP");
        if (Overlaps(executable, storage) || Overlaps(executable, publicJoin))
            throw new InvalidDataException("LAN_EXECUTABLE_STORAGE_OVERLAP");
        var grouping = "Yahad.LAN." + installationId;
        LanFirewallRule Rule(int port) => new(grouping + ".TCP." + port,
            "Yahad home network; installation " + installationId + "; TCP " + port,
            grouping, executable, selectedAddress, port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var rules = new List<LanFirewallRule> { Rule(HttpsPort) };
        if (enableHttpHelp) rules.Add(Rule(HttpHelpPort));
        var config = new StringBuilder();
        config.Append("{\n    admin off\n    persist_config off\n    skip_install_trust\n    auto_https disable_redirects\n");
        // No access log is enabled. Discard the default runtime logger as well:
        // reverse-proxy errors may otherwise contain private request paths.
        config.Append("    log {\n        output discard\n    }\n");
        config.Append("    storage file_system {\n        root ").Append(Quote(storage)).Append("\n    }\n");
        // There is deliberately no UDP/HTTP3 listener or implicit port-80 redirect.
        config.Append("    servers {\n        protocols h1 h2\n    }\n}\n");
        config.Append(origin).Append(" {\n    bind ").Append(selectedAddress).Append("\n    tls internal\n");
        config.Append("    @ownerOnly path /health /health/* /api/setup /api/setup/*\n    respond @ownerOnly 404\n");
        config.Append("    reverse_proxy 127.0.0.1:").Append(appPort).Append(" {\n        flush_interval -1\n    }\n}\n");
        if (enableHttpHelp)
        {
            config.Append("http://").Append(selectedAddress).Append(" {\n    bind ").Append(selectedAddress).Append('\n');
            config.Append("    @unsupportedMethod not method GET HEAD\n    @help path / /index.html\n");
            config.Append("    header {\n        Cache-Control \"no-store\"\n        X-Content-Type-Options \"nosniff\"\n        Referrer-Policy \"no-referrer\"\n");
            config.Append("        Content-Security-Policy \"default-src 'none'; style-src 'unsafe-inline'; img-src data:; form-action 'none'; frame-ancestors 'none'; base-uri 'none'\"\n    }\n");
            // route preserves execution order; without it Caddy sorts `handle`
            // before `respond`, which would make the method guard unreachable.
            config.Append("    route {\n        respond @unsupportedMethod 405\n");
            config.Append("        handle @help {\n            root * ").Append(Quote(publicJoin)).Append("\n            rewrite * /index.html\n            file_server\n        }\n");
            config.Append("        handle /certificate.crt {\n            root * ").Append(Quote(publicJoin)).Append("\n            header Content-Type \"application/pkix-cert\"\n            header Content-Disposition \"attachment; filename=yahad-home-root.crt\"\n            rewrite * /certificate.crt\n            file_server\n        }\n");
            config.Append("        handle {\n            respond 404\n        }\n    }\n}\n");
        }
        return new(installationId, selectedAdapterId, selectedAddress, appPort, origin,
            enableHttpHelp ? "http://" + selectedAddress : null, executable, storage, publicJoin,
            config.ToString(), rules.AsReadOnly());
    }

    /// <summary>Never migrate silently to an arbitrary new adapter or IP after sleep, DHCP change, or VPN connection.</summary>
    public static void RequireSelectedAdapter(LanDeploymentPlan plan, IReadOnlyList<LanChoice> current)
    {
        if (NetworkPlan.LanOrigin(current, plan.AdapterId, plan.Address) != plan.Origin)
            throw new InvalidDataException("NETWORK_SELECTION_CHANGED");
    }

    private static bool Overlaps(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.StartsWith(right.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
        right.StartsWith(left.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string SafePath(string path)
    {
        // Reject Caddy interpolation, unquoted tokens and device/UNC paths. Only
        // local, administrator-owned Windows paths may be supplied by the installer.
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || !Path.IsPathFullyQualified(path) ||
            path.StartsWith("\\\\", StringComparison.Ordinal) || path.Any(c => char.IsControl(c) || c is '"' or '{' or '}' or '`' or '*' or '?' or '<' or '>' or '|'))
            throw new InvalidDataException("LAN_PATH");
        var full = Path.GetFullPath(path);
        if (full.Length < 4 || !char.IsAsciiLetter(full[0]) || full[1] != ':' || full[2] != '\\' ||
            full.AsSpan(2).Contains(':')) throw new InvalidDataException("LAN_PATH");
        return Path.TrimEndingDirectorySeparator(full);
    }

    private static string Quote(string path) => "\"" + path.Replace('\\', '/') + "\"";
}

/// <summary>Public certificate bytes and fingerprint; private-key or original input bytes are never exposed.</summary>
public sealed class LanPublicRoot
{
    private readonly byte[] der;
    public byte[] Der => (byte[])der.Clone();
    public string Sha256Fingerprint { get; }
    internal LanPublicRoot(byte[] certificate)
    {
        der = (byte[])certificate.Clone();
        Sha256Fingerprint = string.Join(':', SHA256.HashData(der).Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));
    }
}

public static class LanCertificate
{
    public static LanPublicRoot ExportPublicRoot(ReadOnlySpan<byte> input)
    {
        if (input.Length is < 64 or > 65536) throw new InvalidDataException("LAN_PUBLIC_CERTIFICATE_BOUND");
        X509Certificate2 certificate;
        try
        {
            // A PEM certificate file with trailing key text must fail as a whole,
            // even though many X509 parsers would happily ignore that trailing key.
            if (input[0] == (byte)'-')
            {
                var text = new UTF8Encoding(false, true).GetString(input).Trim();
                const string begin = "-----BEGIN CERTIFICATE-----";
                const string end = "-----END CERTIFICATE-----";
                if (!text.StartsWith(begin, StringComparison.Ordinal) || !text.EndsWith(end, StringComparison.Ordinal) ||
                    text.IndexOf(begin, begin.Length, StringComparison.Ordinal) >= 0 ||
                    text.IndexOf(end, StringComparison.Ordinal) != text.Length - end.Length)
                    throw new InvalidDataException("LAN_PUBLIC_CERTIFICATE_ONLY");
                certificate = X509Certificate2.CreateFromPem(text);
            }
            else
            {
                certificate = X509CertificateLoader.LoadCertificate(input);
                // Strict DER only: no PFX, extra ASN.1 objects or trailing material.
                if (!input.SequenceEqual(certificate.RawData))
                { certificate.Dispose(); throw new InvalidDataException("LAN_PUBLIC_CERTIFICATE_ONLY"); }
            }
        }
        catch (CryptographicException) { throw new InvalidDataException("LAN_PUBLIC_CERTIFICATE_INVALID"); }
        catch (DecoderFallbackException) { throw new InvalidDataException("LAN_PUBLIC_CERTIFICATE_INVALID"); }
        using (certificate)
        {
            if (certificate.HasPrivateKey || !certificate.Extensions.OfType<X509BasicConstraintsExtension>().Any(e => e.CertificateAuthority))
                throw new InvalidDataException("LAN_PUBLIC_ROOT_REQUIRED");
            var usage = certificate.Extensions.OfType<X509KeyUsageExtension>().FirstOrDefault();
            if (usage is not null && !usage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
                throw new InvalidDataException("LAN_PUBLIC_ROOT_REQUIRED");
            return new(certificate.Export(X509ContentType.Cert));
        }
    }
}
