using FluentAssertions;
using NetRatel.Web.Services.RemoteSupport;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public class RemoteSupportHandoverOptionsTests
{
    [Fact]
    public void Defaults_DisableHandover()
    {
        var options = new RemoteSupportIceServerOptions();

        options.Handover.Enabled.Should().BeFalse();
        options.Handover.AutoReconnectEnabled.Should().BeFalse();
        options.Handover.ProviderGenerationEnabled.Should().BeFalse();
        options.Handover.CoordinatorEnabled.Should().BeFalse();
        options.Handover.AnyEnabled.Should().BeFalse();
    }
}
