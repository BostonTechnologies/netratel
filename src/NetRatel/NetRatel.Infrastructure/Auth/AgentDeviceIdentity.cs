using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace NetRatel.Infrastructure.Auth;

/// <summary>Public device context observed by the native client, never inferred from its proxy.</summary>
public static class AgentDeviceIdentity
{
    public static string? GetReportedAddress()
    {
        try
        {
            return SelectReportedAddress(NetworkInterface.GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up &&
                    adapter.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(adapter =>
                {
                    var properties = adapter.GetIPProperties();
                    var hasGateway = properties.GatewayAddresses.Any(gateway => !IPAddress.IsLoopback(gateway.Address) &&
                        !gateway.Address.Equals(IPAddress.Any) && !gateway.Address.Equals(IPAddress.IPv6Any));
                    return properties.UnicastAddresses.Select(unicast => (unicast.Address, HasGateway: hasGateway));
                }));
        }
        catch (Exception exception) when (exception is NetworkInformationException or PlatformNotSupportedException)
        {
            // Missing metadata must not interfere with admission or enrollment.
            return null;
        }
    }

    internal static string? SelectReportedAddress(IEnumerable<(IPAddress Address, bool HasGateway)> candidates) =>
        candidates.Where(candidate => IsUsable(candidate.Address))
            .OrderByDescending(candidate => candidate.HasGateway)
            .ThenBy(candidate => candidate.Address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ThenBy(candidate => candidate.Address.ToString(), StringComparer.Ordinal)
            .Select(candidate => candidate.Address.ToString()).FirstOrDefault();

    private static bool IsUsable(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return false;
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return !address.IsIPv6LinkLocal && !address.IsIPv6Multicast;
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var bytes = address.GetAddressBytes();
        return bytes[0] != 0 && bytes[0] < 224 && !(bytes[0] == 169 && bytes[1] == 254);
    }
}
