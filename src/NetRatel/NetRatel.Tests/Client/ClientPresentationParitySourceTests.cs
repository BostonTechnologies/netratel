using FluentAssertions;
using NetRatel.Web.Components.Pages.Clients;
using NetRatel.Web.Models.Clients;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class ClientPresentationParitySourceTests
{
    [Fact]
    public void ClientCards_PreserveHistoricalHierarchy_ThroughAkkaV2Actions()
    {
        var source = ReadRepositoryFile("NetRatel.Web", "Components", "Pages", "Clients", "ClientCardGrid.razor");

        source.Should().ContainAll(
            "client-meta-grid",
            ">Tenant<",
            ">OS<",
            ">Latency<",
            ">Last heartbeat<",
            ">Telemetry<",
            ">Version<",
            "ClientPresentationFormatting.Version",
            "GatewayActions.PingAsync(client.TenantId, client.AgentId)",
            "GatewayTelemetryDialog",
            "ClientTerminalSessionDialog",
            "ClientFileSystemViewer",
            "GatewayRemoteSupportDialog",
            "telemetry-split-grid",
            ">Disk<");
        source.Should().NotContain("/api/v1/clients");
        source.Should().NotContain("SpacetimeDbService");
    }

    [Fact]
    public void ClientPresentationCss_IsExplicitlyLoaded_ShellScoped_AndKeepsResponsiveUniformGeometry()
    {
        var source = ReadRepositoryFile("NetRatel.Web", "wwwroot", "css", "clients-presentation.css");
        var app = ReadRepositoryFile("NetRatel.Web", "Components", "App.razor");

        source.Should().ContainAll(
            ".client-card-grid-shell .client-grid .mud-grid-item",
            ".client-card-grid-shell .client-card.mud-card",
            "height: 500px !important",
            ".client-card-grid-shell .client-meta-label",
            "grid-template-columns: auto minmax(0, 1fr)",
            ".client-card-grid-shell .telemetry-split-grid",
            ".client-grid-view-shell .client-grid-actions",
            "@media (max-width: 720px)");
        source.Should().NotContain("::deep");
        app.Should().Contain("@Assets[\"css/clients-presentation.css\"]");
        app.Should().Contain("@Assets[\"NetRatel.Web.styles.css\"]");
    }

    [Fact]
    public void TelemetryToolbar_UsesExplicitNonOverlappingPhoneGridAreas()
    {
        var source = ReadRepositoryFile("NetRatel.Web", "Components", "Dialogs", "GatewayTelemetryDialog.razor.css");

        source.Should().ContainAll(
            "display: grid !important",
            "grid-template-areas: \"identity state close\"",
            "@media (max-width: 480px)",
            "\"identity close\"",
            "\"state close\"",
            ".telemetry-identity { grid-area: identity",
            ".telemetry-stream-state { grid-area: state",
            ".telemetry-close-button { grid-area: close");
    }

    [Fact]
    public void TelemetryDashboard_BindsTheLiveCadenceState()
    {
        var source = ReadRepositoryFile("NetRatel.Web", "Components", "Dialogs", "GatewayTelemetryDialog.razor");

        source.Should().Contain("CadenceLabel=\"@_streamState\"");
    }

    [Fact]
    public void CardsAndTable_ConsumeTheSameCanonicalPresentationDirectory()
    {
        var page = ReadRepositoryFile("NetRatel.Web", "Components", "Pages", "Clients", "ClientsPage.razor");
        var table = ReadRepositoryFile("NetRatel.Web", "Components", "Pages", "Clients", "ClientGridView.razor");
        var presentation = ReadRepositoryFile("NetRatel.Web", "Services", "Clients", "ClientPresentationService.cs");

        page.Should().ContainAll(
            "ClientGridView Clients=\"@FilteredClients\"",
            "ClientCardGrid Clients=\"@FilteredClients\"",
            "SelectedTenantId",
            "SelectedOs",
            "ClientStatusFilter");
        table.Should().ContainAll(">Host / IP<", ">Latency<", "ClientPresentationFormatting.Version", "GatewayActions.PingAsync(client.TenantId, client.AgentId)");
        presentation.Should().Contain("GetDirectoryAsync");
        presentation.Should().Contain("(client.TenantId, client.AgentId)");
        presentation.Should().NotContain("GroupBy(client => client.HostName");
    }

    [Theory]
    [InlineData("0.4.101+4e2c87e72f8bd4b9", "0.4.101")]
    [InlineData("0.4.101", "0.4.101")]
    [InlineData(null, ClientPresentationFormatting.PendingVersion)]
    public void SharedVersionFormatter_KeepsDisplayConcise(string? version, string expected)
    {
        ClientPresentationFormatting.Version(version).Should().Be(expected);
        ClientPresentationFormatting.VersionTooltip(version).Should().Be(version ?? ClientPresentationFormatting.PendingVersion);
    }

    [Fact]
    public void SharedPresenceFormatters_PreserveWaitingAndMeasuredStates()
    {
        var now = new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

        ClientPresentationFormatting.Latency(true, 18.4).Should().Be("18 ms");
        ClientPresentationFormatting.Latency(true, null).Should().Be("waiting for heartbeat");
        ClientPresentationFormatting.Latency(false, null).Should().Be("n/a");
        ClientPresentationFormatting.TelemetryAge(null, now).Should().Be("waiting");
        ClientPresentationFormatting.TelemetryAge(now.AddSeconds(-20), now).Should().Be("just now");
        ClientPresentationFormatting.TelemetryAge(now.AddMinutes(-5), now).Should().Be("5m ago");
    }

    [Fact]
    public void AutomaticLatencyFormatter_DistinguishesFreshStaleUnmeasuredAndOfflineClients()
    {
        var measuredAt = new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero);
        var expiresAt = measuredAt.AddSeconds(50);
        var client = new ClientPresentationModel(
            3, Guid.NewGuid(), "agent", "agent", "tenant", "Linux", "x64", true, true,
            measuredAt, "latency-test", ["heartbeat-latency"], null, null,
            LatencyMilliseconds: 18.75, LatencyMeasuredAtUtc: measuredAt, LatencyExpiresAtUtc: expiresAt);

        ClientPresentationFormatting.Latency(client, measuredAt.AddSeconds(15)).Should().Be("19 ms");
        ClientPresentationFormatting.Latency(client, expiresAt).Should().Be("stale");
        ClientPresentationFormatting.Latency(client with { Online = false }, measuredAt.AddSeconds(15)).Should().Be("n/a");
        ClientPresentationFormatting.Latency(client with { LatencyMeasuredAtUtc = null }, measuredAt.AddSeconds(15))
            .Should().Be("waiting for heartbeat");
        ClientPresentationFormatting.Latency(client with { LatencyMilliseconds = double.NaN }, measuredAt.AddSeconds(15))
            .Should().Be("waiting for heartbeat");
        ClientPresentationFormatting.Latency(client with { LatencyMilliseconds = null, Capabilities = [] }, measuredAt.AddSeconds(15))
            .Should().Be("upgrade required");
    }

    private static string ReadRepositoryFile(params string[] segments)
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        return File.ReadAllText(Path.Combine([repositoryRoot, "src", "NetRatel", .. segments]));
    }
}
