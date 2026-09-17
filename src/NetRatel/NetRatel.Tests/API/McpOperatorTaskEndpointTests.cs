using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Akka.Configuration;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorTaskEndpointTests
{
    [Fact]
    public void Production_task_routes_are_owned_versioned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorTaskStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorScriptStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAgentCommandGatewaySessionRegistry>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorTaskEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/recent",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/{taskId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/{taskId:long}/logs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/logs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/tasks/confirm/{action}");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
