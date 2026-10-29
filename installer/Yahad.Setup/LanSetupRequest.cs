using System.IO;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Yahad.Install;
namespace Yahad.Setup;

internal enum LanSetupAction { KeepCurrent, Enable, Disable }
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
    internal static IReadOnlyList<LanChoice> Choices() => NetworkPlan.Candidates(NetworkInterface.GetAllNetworkInterfaces()
        .SelectMany(a => a.GetIPProperties().UnicastAddresses.Where(ip => ip.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(ip => new AdapterAddress(a.Id, a.Name, ip.Address.ToString(), a.OperationalStatus == OperationalStatus.Up))));
}
internal sealed record LanSetupResult(string Origin, string? HelpOrigin, string RootSha256,
    bool HostConnectionVerified, bool OtherDeviceVerified = false, string? CertificateDerBase64 = null);
internal interface ILanSetupHostOperations
{
    Task ConfigureLanAsync(OwnerProcess owner, LanSetupRequest request, Action<SetupProgress> progress, CancellationToken cancellation);
}
