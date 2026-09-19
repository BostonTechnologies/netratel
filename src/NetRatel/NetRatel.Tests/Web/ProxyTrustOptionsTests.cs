using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NetRatel.Web.Configuration;
using System.Net;
using Xunit;

namespace NetRatel.Tests.Web;

public sealed class ProxyTrustOptionsTests
{
    [Fact]
    public void Configured_proxy_and_network_are_retained_while_forwarded_hosts_are_allowlisted()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "203.0.113.10",
                ["ForwardedHeaders:KnownIPNetworks:0"] = "2001:db8:1234::/48",
                ["ForwardedHeaders:AllowedHosts:0"] = "netratel.example.com"
            })
            .Build();

        var options = ProxyTrustOptions.Create(configuration);

        options.KnownProxies.Should().Contain(IPAddress.Parse("203.0.113.10"));
        options.KnownIPNetworks.Should().Contain(network => network.PrefixLength == 48);
        options.AllowedHosts.Should().ContainSingle().Which.Should().Be("netratel.example.com");
        options.ForwardLimit.Should().Be(1);
    }

    [Fact]
    public void Invalid_proxy_configuration_fails_closed()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ForwardedHeaders:KnownProxies:0"] = "not-an-ip"
            })
            .Build();

        Action configure = () => ProxyTrustOptions.Create(configuration);

        configure.Should().Throw<InvalidOperationException>().WithMessage("*KnownProxies*");
    }
}
