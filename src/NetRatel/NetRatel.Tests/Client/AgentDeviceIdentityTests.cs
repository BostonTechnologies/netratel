using System.Net;
using NetRatel.Infrastructure.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentDeviceIdentityTests
{
    [Fact]
    public void ReportedAddress_UsesActualRoutedNicAddressAndExcludesLoopbackUnspecifiedAndLinkLocal()
    {
        var candidates = new[] { "127.0.0.1", "0.0.0.0", "::", "::1", "fe80::1", "169.254.1.2", "239.1.2.3" }
            .Select(address => (IPAddress.Parse(address), true))
            .Concat([(IPAddress.Parse("10.0.0.3"), false), (IPAddress.Parse("192.168.1.20"), true)]);
        Assert.Equal("192.168.1.20", AgentDeviceIdentity.SelectReportedAddress(candidates));
    }

    [Fact]
    public void ReportedAddress_SupportsIpv6AndHonestMissingMetadata()
    {
        Assert.Equal("2001:db8::1", AgentDeviceIdentity.SelectReportedAddress([(IPAddress.Parse("2001:db8::1"), true)]));
        Assert.Null(AgentDeviceIdentity.SelectReportedAddress([(IPAddress.Loopback, true)]));
    }
}
