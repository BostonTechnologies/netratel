using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class RemoteSupportLegacyRetirementSourceTests
{
    private static readonly string RepoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));

    [Fact]
    public void Unreachable_spacetime_remote_support_api_and_web_surfaces_are_removed()
    {
        var retiredSources = new[]
        {
            "src/NetRatel/NetRatel.API/Endpoints/RemoteAccess/RemoteSupportEndpoints.cs",
            "src/NetRatel/NetRatel.API/Services/RemoteSupport/RemoteSupportSessionRegistry.cs",
            "src/NetRatel/NetRatel.API/Services/RemoteSupport/RemoteSupportControlTunnelRegistry.cs",
            "src/NetRatel/NetRatel.API/Services/RemoteSupport/RemoteSupportWindowsSessionInventoryCache.cs",
            "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientRemoteSupportDialog.razor",
            "src/NetRatel/NetRatel.Web/Components/Dialogs/ClientRemoteSupportDialog.razor.css",
            "src/NetRatel/NetRatel.Web/Services/RemoteSupport/RemoteSupportApiService.cs",
            "src/NetRatel/NetRatel.Web/Services/RemoteSupport/RemoteSupportInventoryConvergenceService.cs"
        };

        retiredSources.Should().OnlyContain(path => !File.Exists(Path.Combine(RepoRoot, path)));
    }

    [Fact]
    public void Current_clients_ui_uses_the_canonical_gateway_remote_support_dialog()
    {
        var cardGrid = Read("src/NetRatel/NetRatel.Web/Components/Pages/Clients/ClientCardGrid.razor");
        var gridView = Read("src/NetRatel/NetRatel.Web/Components/Pages/Clients/ClientGridView.razor");
        var apiMap = Read("src/NetRatel/NetRatel.API/Endpoints/ApiEndpointRegistrationExtensions.cs");

        cardGrid.Should().Contain("GatewayRemoteSupportDialog");
        gridView.Should().Contain("GatewayRemoteSupportDialog");
        apiMap.Should().Contain("app.MapAgentRemoteSupportV2Endpoints();");
        apiMap.Should().NotContain("app.MapRemoteSupportEndpoints();");

        var gatewayEndpoints = Read("src/NetRatel/NetRatel.API/Endpoints/RemoteAccess/AgentRemoteSupportGatewayEndpoints.cs");
        gatewayEndpoints.Should().Contain("/v2/lifecycle/sessions");
        gatewayEndpoints.Should().Contain("RequireAuthorization(\"RemoteSupportOperator\")");
        gatewayEndpoints.Should().NotContain("MapPost(\"/sessions\"");
        gatewayEndpoints.Should().NotContain("/api/v2/gateway-remote-support");
        gatewayEndpoints.Should().NotContain("IGatewayRemoteSupportSessionRegistry");
        gatewayEndpoints.Should().NotContain("GetService<IRequiredActor");
        gatewayEndpoints.Should().Contain("GetRequiredService<IRequiredActor<RemoteSupportSessionAuthorityRegion>>");
        gatewayEndpoints.Should().Contain("catch (AskTimeoutException)");
        gatewayEndpoints.Should().Contain("StatusCodes.Status503ServiceUnavailable");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
