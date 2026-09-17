using FluentAssertions;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class PrimaryClientAgentBindingEndpointSourceTests
{
    [Fact]
    public void BindingEndpoints_UsePostgreSqlAuthorityWithoutSpacetimeValidation()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.API/Endpoints/Client/PrimaryClientAgentBindingEndpoints.cs"));

        source.Should().Contain("PrimaryClientAgentBindingService.NormalizePrimaryClientIdentity");
        source.Should().NotContain("SpacetimeDbService");
        source.Should().NotContain("SpacetimeDB");
        source.Should().NotContain("SpacetimeIdentityHelpers");
        source.Should().NotContain("Connection.Db.Client");
    }

    [Fact]
    public void ReadEndpoints_AreSeparatedFrom_OperationalBindingManagement()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.API/Endpoints/Client/PrimaryClientAgentBindingEndpoints.cs"));

        source.Should().Contain("MapPrimaryClientAgentBindingReadEndpoints");
        source.Should().Contain("MapPrimaryClientAgentBindingManagementEndpoints");
        source.Should().Contain("RequireAuthorization(\"Operator\")");
    }

    [Fact]
    public void GatewayCardReadMapper_ExcludesTheActivePingRoute()
    {
        var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        var source = File.ReadAllText(Path.Combine(
            repositoryRoot,
            "src/NetRatel/NetRatel.API/Endpoints/Client/PrimaryClientGatewayCardReadEndpoints.cs"));

        var readMapper = source[..source.IndexOf("MapPrimaryClientGatewayCardActionEndpoints", StringComparison.Ordinal)];
        readMapper.Should().Contain("MapGet(\"/api/v2/tenants/{tenantId:int}/primary-client-cards/gateway\"");
        readMapper.Should().NotContain("MapPost(");
        source.Should().Contain("MapPrimaryClientGatewayCardActionEndpoints");
        source.Should().Contain("MapPost(\"/api/v2/tenants/{tenantId:int}/primary-client-cards/{primaryClientIdentity}/gateway/ping\"");
    }
}
