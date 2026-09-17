using FluentAssertions;
using NetRatel.Client.Service.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportGatewayProviderRuntimeTests
{
    [Fact]
    public void GatewayRuntime_OnUnsupportedPlatform_RejectsWithoutReadyOrFallback()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var emitted = new List<RemoteSupportPipeSignal>();
        using var runtime = new RemoteSupportSessionManager(null, emitted.Add);

        runtime.OpenGatewaySession("gateway-session", "{\"targetMode\":\"auto\"}");

        emitted.Should().ContainSingle(signal => signal.SignalType == RemoteSupportSignalTypes.Reject)
            .Which.PayloadJson.Should().Contain("native_webrtc_unsupported_os");
        emitted.Should().NotContain(signal => signal.SignalType == RemoteSupportSignalTypes.Ready);
    }

    [Fact]
    public void GatewayBridge_SourceUsesProviderRuntimeInsteadOfServiceContextWebRtc()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var gateway = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.Client/Service/Gateway/AgentRemoteSupportGatewayClient.cs"));

        gateway.Should().Contain("EnsureGatewayHelperPipeHost");
        gateway.Should().Contain("OpenGatewaySession(signal.SessionId, payload)");
        gateway.Should().Contain("ProcessGatewaySignal(");
        gateway.Should().Contain("CloseGatewaySession(");
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
