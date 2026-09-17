using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class RemoteSupportV2PreparedRouteSourceTests
{
    [Fact]
    public void V2Media_runtime_uses_the_prepared_helper_route_without_current_helper_fallback()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var runtime = File.ReadAllText(Path.Combine(root, "src/NetRatel/NetRatel.Client/Service/RemoteSupport/RemoteSupportSessionManager.cs"));

        runtime.Should().Contain("OpenPreparedV2Session");
        runtime.Should().Contain("ProcessPreparedV2Signal");
        runtime.Should().Contain("RemoteSupportSessionIceConfiguration? iceConfiguration");
        runtime.Should().Contain("IceServers: iceConfiguration.Servers");
        runtime.Should().Contain("helper.RouteId == prepared.HelperRoute.HelperRouteId");
        runtime.Should().Contain("SendExactHelperMessage");
        runtime.Should().NotContain("GetConnectedHelper();\n        return helper is not null && helper.SessionId == prepared");
    }

    [Fact]
    public void V2Gateway_bridge_survives_stream_reconnect_without_disposing_the_media_runtime()
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var gateway = File.ReadAllText(Path.Combine(root, "src/NetRatel/NetRatel.Client/Service/Gateway/AgentRemoteSupportGatewayClient.cs"));

        gateway.Should().Contain("using var v2Media");
        gateway.Should().Contain("v2Media?.Detach();");
        gateway.Should().Contain("_providerRuntime.Dispose();");
        gateway.IndexOf("v2Media?.Detach();", StringComparison.Ordinal)
            .Should().BeLessThan(gateway.LastIndexOf("_providerRuntime.Dispose();", StringComparison.Ordinal));
    }
}
