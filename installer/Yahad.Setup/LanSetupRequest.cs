using System.IO;
using Yahad.Install;
using Yahad.Service;
namespace Yahad.Setup;

internal enum LanSetupAction { Enable, Disable }
internal sealed record LanSetupRequest(LanSetupAction Action, string? AdapterId = null, string? Address = null,
    bool FirewallConsent = false, bool HttpHelp = true)
{
    internal void Require()
    {
        if (!Enum.IsDefined(Action)) throw new IOException("LAN_ACTION_INVALID");
        if (Action == LanSetupAction.Enable)
        {
            if (!FirewallConsent || string.IsNullOrWhiteSpace(AdapterId) || AdapterId.Length > 256 ||
                AdapterId.Any(char.IsControl) || Address is null || !NetworkPlan.IsPrivateLanV4(Address))
                throw new IOException("LAN_CONSENT_REQUIRED");
        }
        else if (AdapterId is not null || Address is not null || FirewallConsent)
            throw new IOException("LAN_ACTION_INVALID");
    }
    internal static IReadOnlyList<LanChoice> Choices() => NetworkPlan.Candidates(LanProxyPolicy.Addresses());
}
internal sealed record LanSetupResult(string Origin, string? HelpOrigin, string RootSha256, string? CertificateDerBase64 = null);
