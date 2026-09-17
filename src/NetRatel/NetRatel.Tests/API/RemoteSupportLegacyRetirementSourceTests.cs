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
        apiMap.Should().Contain("app.MapAgentRemoteSupportGatewayEndpoints();");
        apiMap.Should().NotContain("app.MapRemoteSupportEndpoints();");
    }

    private static string Read(string relativePath) => File.ReadAllText(Path.Combine(RepoRoot, relativePath));
}
