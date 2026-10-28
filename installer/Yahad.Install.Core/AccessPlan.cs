namespace Yahad.Install;

public enum CertificateStrategy { LocalCa, ManagedDns01Reserved }
public enum DeviceAccessStage { Planned, HostHealthy, OtherDeviceReached, ChildEntered }
public sealed record AccessEvidence(bool HostHealthy, bool OtherDeviceObserved, bool AuthenticatedChildObserved,
    bool DeviceTrustConfirmedByOwner);
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

    public static DeviceAccessStage Stage(AccessEvidence evidence)
    {
        if (!evidence.HostHealthy) return DeviceAccessStage.Planned;
        if (!evidence.OtherDeviceObserved) return DeviceAccessStage.HostHealthy;
        if (!evidence.AuthenticatedChildObserved || !evidence.DeviceTrustConfirmedByOwner) return DeviceAccessStage.OtherDeviceReached;
        return DeviceAccessStage.ChildEntered;
    }
    public static bool IsInstallerComplete(AccessEvidence evidence) => evidence.HostHealthy && evidence.OtherDeviceObserved && evidence.DeviceTrustConfirmedByOwner;
    public static bool IsEndToEndComplete(AccessEvidence evidence) => Stage(evidence) == DeviceAccessStage.ChildEntered;
}
