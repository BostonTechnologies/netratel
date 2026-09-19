using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

namespace NetRatel.Web.Configuration;

/// <summary>
/// Binds the narrow set of reverse proxies allowed to supply forwarding headers.
/// Loopback remains trusted for local evaluation through the framework default.
/// </summary>
public sealed class ProxyTrustOptions
{
    public const string SectionName = "ForwardedHeaders";

    public string[] KnownProxies { get; init; } = [];
    public string[] KnownIPNetworks { get; init; } = [];
    public string[] AllowedHosts { get; init; } = [];

    public static ForwardedHeadersOptions Create(IConfiguration configuration)
    {
        var configured = configuration.GetSection(SectionName).Get<ProxyTrustOptions>() ?? new ProxyTrustOptions();
        var options = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedHost |
                               ForwardedHeaders.XForwardedProto,
            ForwardLimit = 1
        };

        foreach (var proxy in configured.KnownProxies.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!IPAddress.TryParse(proxy, out var address))
            {
                throw new InvalidOperationException($"{SectionName}:KnownProxies contains invalid IP address '{proxy}'.");
            }

            options.KnownProxies.Add(address);
        }

        foreach (var network in configured.KnownIPNetworks.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!TryParseNetwork(network, out var parsedNetwork))
            {
                throw new InvalidOperationException($"{SectionName}:KnownIPNetworks contains invalid CIDR network '{network}'.");
            }

            options.KnownIPNetworks.Add(parsedNetwork);
        }

        var hosts = configured.AllowedHosts.Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (hosts.Length == 0)
        {
            hosts = ["localhost", "127.0.0.1", "[::1]"];
        }

        options.AllowedHosts = hosts;
        return options;
    }

    private static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
    {
        network = default!;
        var separator = value.LastIndexOf('/');
        if (separator <= 0 || separator == value.Length - 1 ||
            !IPAddress.TryParse(value[..separator], out var address) ||
            !int.TryParse(value[(separator + 1)..], out var prefixLength))
        {
            return false;
        }

        var maximumPrefixLength = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
        if (prefixLength < 0 || prefixLength > maximumPrefixLength)
        {
            return false;
        }

        network = new System.Net.IPNetwork(address, prefixLength);
        return true;
    }
}
