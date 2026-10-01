using FluentAssertions;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportGatewayProviderRuntimeTests
{
    [Fact]
    public async Task V2ConsolePreparation_OnUnsupportedPlatform_ReturnsNoProviderWithoutLegacyFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var runtime = new RemoteSupportSessionManager(null, _ => { });
        var provider = await runtime.PrepareV2ConsoleProviderAsync(null!, CancellationToken.None);
        provider.Should().BeNull();
    }

    [Fact]
    public void GatewayBridge_SourceUsesProviderRuntimeInsteadOfServiceContextWebRtc()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var gateway = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Client/Service/Gateway/AgentRemoteSupportGatewayClient.cs"));

        gateway.Should().Contain("EnsureGatewayHelperPipeHost");
        gateway.Should().Contain("using var v2Media = new V2GatewayRemoteSupportBridge");
        gateway.Should().Contain("WriteV2RegistrationAsync");
        gateway.Should().Contain("WriteV2EnvelopeAsync");
        gateway.Should().NotContain("OpenGatewaySession");
        gateway.Should().NotContain("ProcessGatewaySignal");
        gateway.Should().NotContain("WriteSignalAsync");
        gateway.Should().NotContain("WriteClosedAsync");
        gateway.Should().NotContain("RemoteSupportInteractiveWebRtcManager _webrtc");
        gateway.Should().NotContain("SignalBridgeStream");
    }

    [Fact]
    public void ProviderRuntime_SourceHasNoLegacySpacetimeTransport()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var runtime = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Client/Service/RemoteSupport/RemoteSupportSessionManager.cs"));

        runtime.Should().NotContain("Spacetime");
        runtime.Should().NotContain("DbConnection");
        runtime.Should().NotContain("RemoteSupportSessionProjection");
    }
}
