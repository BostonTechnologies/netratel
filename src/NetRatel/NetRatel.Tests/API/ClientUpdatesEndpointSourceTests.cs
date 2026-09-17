using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdatesEndpointSourceTests
{
    [Fact]
    public void ClientUpdatesEndpoints_Expose_Releases_Attempts_And_Recheck()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.API/Endpoints/Client/ClientUpdatesEndpoints.cs"));

        source.Should().Contain("/api/v1/client-updates");
        source.Should().Contain("/releases");
        source.Should().Contain("/attempts");
        source.Should().Contain("/recheck/{clientIdentity}");
        source.Should().Contain("/api/v2/agent-updates");
        source.Should().Contain("/disable");
        source.Should().Contain("/resume");
        source.Should().Contain("/releases/reconcile");
        source.Should().Contain("GetMetadataAsync");
        source.Should().Contain("ClientArtifactManifestValidator.ValidateAsync");
        source.Should().Contain("OrchestratorDbContext");
        source.Should().Contain("TenantName");
        source.Should().Contain("AgentName");
        source.Should().Contain("IgnoreQueryFilters");
    }

    [Fact]
    public void Endpoint_Bootstrap_Registers_Postgres_ClientUpdates_Endpoints()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.API/Endpoints/ApiEndpointRegistrationExtensions.cs"));

        source.Should().Contain("app.MapClientUpdatesEndpoints();");
    }

    [Fact]
    public void ClientUpdateEndpoints_DoNotDepend_On_Spacetime()
    {
        var source = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "../../../../NetRatel.API/Endpoints/Client/ClientUpdatesEndpoints.cs"));
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("PublishClientUpdateRelease");
    }
}
