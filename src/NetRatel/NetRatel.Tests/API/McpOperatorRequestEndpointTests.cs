using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorRequestEndpointTests
{
    [Fact]
    public void Production_request_routes_are_owned_versioned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRequestStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        var app = builder.Build();

        app.MapMcpOperatorRequestEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests/{requestId:int}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests/confirm/{action}");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
