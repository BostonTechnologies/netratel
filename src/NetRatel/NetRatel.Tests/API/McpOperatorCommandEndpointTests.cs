using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorCommandEndpointTests
{
    [Fact]
    public void Production_command_routes_are_explicitly_versioned_and_require_the_mcp_m2m_identity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorCommandStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAgentCommandGatewaySessionRegistry>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorCommandEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands/availability",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands/{commandId}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/commands/{commandId}/cancel");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
