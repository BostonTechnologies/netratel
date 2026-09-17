using FluentAssertions;
using NetRatel.Client.Service.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportHelperVersionTests
{
    [Theory]
    [InlineData("0.5.6-rc.4+e88164d7", "0.5.6.3", true)]
    [InlineData("0.5.6+e88164d7", "0.5.6.3", true)]
    [InlineData(" 0.5.6-rc.4 ", "0.5.6", true)]
    [InlineData("0.5.1+5dbf357e", "0.5.6.3", false)]
    [InlineData("0.5.7", "0.5.6.3", false)]
    [InlineData(null, "0.5.6.3", false)]
    [InlineData("", "", false)]
    [InlineData("unknown", "unknown", false)]
    [InlineData("0.5.6", null, false)]
    public void Inventory_and_preparation_share_valid_release_core_compatibility(
        string? helperVersion, string? serviceVersion, bool expected) =>
        RemoteSupportHelperVersion.IsCompatible(helperVersion, serviceVersion).Should().Be(expected);
}
