using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Kadraj.Server;

public sealed record LanAddress(IPAddress Address, string InterfaceName);

public static class NetworkInfo
{
    static readonly string[] VirtualHints = ["vEthernet", "VirtualBox", "VMware", "Hyper-V", "WSL", "Bluetooth", "Loopback", "TAP", "Tailscale", "ZeroTier"];

    /// <summary>IPv4 addresses a phone on the same network could reach, most likely first.</summary>
    public static IReadOnlyList<LanAddress> GetLanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                     && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n =>
            {
                var props = n.GetIPProperties();
                bool hasGateway = props.GatewayAddresses.Any(g =>
                    g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
                bool isVirtual = VirtualHints.Any(h =>
                    n.Name.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                    n.Description.Contains(h, StringComparison.OrdinalIgnoreCase));
                int rank = (hasGateway ? 0 : 2) + (isVirtual ? 1 : 0);

                return props.UnicastAddresses
                    .Where(u => u.Address.AddressFamily == AddressFamily.InterNetwork
                             && !IPAddress.IsLoopback(u.Address)
                             && !u.Address.ToString().StartsWith("169.254."))
                    .Select(u => (Rank: rank, Item: new LanAddress(u.Address, n.Name)));
            })
            .OrderBy(x => x.Rank)
            .Select(x => x.Item)
            .ToList();
    }
}
