namespace Yahad.Install;

public enum CertificateStrategy { LocalCa, ManagedDns01Reserved }
public sealed record AccessPlan(int Format, string Kind, string AdapterId, string CurrentLanOrigin,
    bool StableNameProvisioned, string CertificateProvider, string TrustRequirement, bool InternetEnabled,
    string FirewallProfile, string FirewallRemoteScope, int HttpsPort, bool ActionsAuthorized);

/// <summary>Configuration/planning only. No certificate, DNS, firewall or router operations.</summary>
public static class AccessPlanner
{
    public static AccessPlan Build(IReadOnlyList<LanChoice> addresses, string adapter, string address,
        CertificateStrategy certificate = CertificateStrategy.LocalCa)
    {
        if (certificate != CertificateStrategy.LocalCa) throw new InvalidOperationException("CERTIFICATE_PROVIDER_NOT_CONFIGURED");
        return new(1, "local-install-plan", adapter, NetworkPlan.LanOrigin(addresses, adapter, address), false,
            "local-ca", "explicit-trust-on-each-device", false, "Private", "LocalSubnet", 443, false);
    }
}
