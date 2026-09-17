using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Middleware;
using NetRatel.Application.Agents;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorClientAdministrationEndpointTests
{
    [Fact]
    public void Production_client_routes_use_only_exact_m2m_operator_routes()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAgentManagementService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientPresenceReadModel>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IPrimaryClientAgentBindingService>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorClientAdministrationEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/presence",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/capabilities",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/binding",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/telemetry",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/update-attempts",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/update-metadata",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/ping/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/ping/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/software-update/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/software-update/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/disable/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/disable/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/enable/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/enable/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/delete/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients/delete/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
