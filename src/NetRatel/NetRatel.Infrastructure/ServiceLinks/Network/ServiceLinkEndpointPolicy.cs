using System.Net;

namespace NetRatel.Infrastructure.ServiceLinks.Network;

/// <summary>
/// Common validation for administrator-selected outbound integration targets.
/// Private RFC1918 addresses remain valid for deliberate private deployments;
/// link-local, multicast, unspecified, and well-known metadata targets do not.
/// </summary>
public static class ServiceLinkEndpointPolicy
{
    private static readonly byte[] AwsIpv6MetadataAddress = IPAddress.Parse("fd00:ec2::254").GetAddressBytes();
    private static readonly string[] ReservedMetadataHosts =
    [
        "metadata",
        "metadata.google.internal",
        "instance-data.ec2.internal"
    ];

    public static void Validate(Uri uri, string fieldName, bool allowPrivateHttp = false)
    {
        if (uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 ||
            uri.Fragment.Length != 0)
        {
            throw Rejected($"{fieldName} must be an absolute HTTP or HTTPS URL without credentials, a query string, or a fragment.", fieldName);
        }

        if (IsRejectedHost(uri.Host))
        {
            throw Rejected($"{fieldName} targets a reserved link-local, multicast, unspecified, or metadata address.", fieldName);
        }

        if (!allowPrivateHttp && IPAddress.TryParse(uri.Host, out var literal) && IsPrivateNetworkAddress(literal))
            throw Rejected($"{fieldName} targets a private address without the current deployment opt-in.", fieldName);

        if (uri.Scheme == "http" && (!allowPrivateHttp || IsPublicIp(uri.Host)))
        {
            throw Rejected($"{fieldName} must use HTTPS unless explicit private HTTP is enabled.", fieldName);
        }
    }

    public static bool IsAllowed(Uri uri, bool allowPrivateHttp = false) =>
        uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0 &&
        !IsRejectedHost(uri.Host) &&
        (allowPrivateHttp || !IPAddress.TryParse(uri.Host, out var literal) || !IsPrivateNetworkAddress(literal)) &&
        (uri.Scheme == "https" || (allowPrivateHttp && !IsPublicIp(uri.Host)));

    public static void ValidateResolvedAddresses(
        Uri uri,
        IReadOnlyCollection<IPAddress> addresses,
        string fieldName,
        bool allowPrivateHttp)
    {
        if (!IsAllowed(uri, allowPrivateHttp))
            throw Rejected($"{fieldName} is not allowed by the outbound integration policy.", fieldName);
        ValidateResolvedAddressesCore(uri, addresses, fieldName, allowPrivateHttp);
    }

    private static void ValidateResolvedAddressesCore(
        Uri uri,
        IReadOnlyCollection<IPAddress> addresses,
        string fieldName,
        bool allowPrivateHttp)
    {
        if (addresses.Count == 0)
            throw Rejected($"{fieldName} did not resolve to an address.", fieldName);
        if (addresses.Any(IsRejectedAddress))
            throw Rejected($"{fieldName} resolved to a reserved link-local, multicast, unspecified, or metadata address.", fieldName);
        if (!allowPrivateHttp && addresses.Any(IsPrivateNetworkAddress))
            throw Rejected($"{fieldName} resolved to a private address without the current deployment opt-in.", fieldName);
        if (uri.Scheme == "http" && (!allowPrivateHttp || addresses.Any(address => !IsPrivateNetworkAddress(address))))
            throw Rejected($"{fieldName} resolved to a public address while private HTTP is enabled.", fieldName);
    }

    private static ArgumentException Rejected(string message, string parameter)
    {
        var error = new ArgumentException(message, parameter);
        error.Data["ServiceLink.NetworkPolicyRejected"] = true;
        return error;
    }

    public static bool IsPrivateNetworkAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 4)
            return bytes[0] == 10 || bytes[0] == 192 && bytes[1] == 168 || bytes[0] == 172 && bytes[1] is >= 16 and <= 31;
        return bytes.Length == 16 && (bytes[0] & 0xfe) == 0xfc;
    }

    private static bool IsRejectedHost(string host)
    {
        var normalizedHost = host.TrimEnd('.');
        if (ReservedMetadataHosts.Contains(normalizedHost, StringComparer.OrdinalIgnoreCase) ||
            normalizedHost.EndsWith(".metadata.google.internal", StringComparison.OrdinalIgnoreCase)) return true;
        if (!IPAddress.TryParse(normalizedHost, out var address)) return false;
        if (IPAddress.IsLoopback(address)) return false;
        if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)) return true;
        if (address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal) return true;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IsRejectedAddress(address);
    }

    private static bool IsRejectedAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.GetAddressBytes().AsSpan().SequenceEqual(AwsIpv6MetadataAddress)) return true;
        if (address.Equals(IPAddress.IPv6Any) || address.IsIPv6LinkLocal || address.IsIPv6Multicast || address.IsIPv6SiteLocal)
            return true;
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 &&
            (bytes[0] == 0 || bytes[0] == 169 && bytes[1] == 254 || bytes[0] >= 224);
    }

    private static bool IsPublicIp(string host)
    {
        return IPAddress.TryParse(host.TrimEnd('.'), out var address) && !IsPrivateNetworkAddress(address);
    }

}
