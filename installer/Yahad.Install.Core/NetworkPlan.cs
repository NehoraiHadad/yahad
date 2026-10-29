using System.Net;
using System.Net.Sockets;

namespace Yahad.Install;

public sealed record AdapterAddress(string AdapterId, string DisplayName, string Address, bool Up);
public sealed record LanChoice(string AdapterId, string DisplayName, string Address);

/// <summary>Planning only: never opens firewall, trusts a CA, or chooses a VPN/LAN automatically.</summary>
public static class NetworkPlan
{
    public static bool IsPrivateLanV4(string value)
    {
        if (!IPAddress.TryParse(value, out var address) || address.AddressFamily != AddressFamily.InterNetwork ||
            address.ToString() != value) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168;
    }

    public static IReadOnlyList<LanChoice> Candidates(IEnumerable<AdapterAddress> addresses) => addresses
        .Where(a => a.Up && IsPrivateLanV4(a.Address))
        .DistinctBy(a => (a.AdapterId, a.Address))
        .Select(a => new LanChoice(a.AdapterId, a.DisplayName, a.Address)).ToArray();

    public static string LanOrigin(IReadOnlyList<LanChoice> choices, string selectedAdapter, string selectedAddress)
    {
        if (!IsPrivateLanV4(selectedAddress) || !choices.Any(c => c.AdapterId == selectedAdapter && c.Address == selectedAddress))
            throw new InvalidDataException("NETWORK_SELECTION_CHANGED");
        return "https://" + selectedAddress;
    }

    // A momentary preflight only. The real listener must handle a later bind failure
    // without killing the process that acquired the port in the meantime.
    public static bool CanBindLoopback(int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.ExclusiveAddressUse = true;
        try { socket.Bind(new IPEndPoint(IPAddress.Loopback, port)); return true; }
        catch (SocketException) { return false; }
    }
}
