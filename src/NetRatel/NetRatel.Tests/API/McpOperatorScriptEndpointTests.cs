using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorScriptEndpointTests
{
    [Fact]
    public void Production_script_routes_are_explicitly_versioned_and_require_the_mcp_m2m_identity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorScriptStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorTaskStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorScriptEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/params",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/validate",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/confirm/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/runs/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/runs/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
