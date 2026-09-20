using Microsoft.AspNetCore.Mvc;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Akka.Configuration;
using NetRatel.API.Realtime.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Authorized browser query surface for additive gateway log data.</summary>
public static class AgentLogGatewayEndpoints
{
    public static IEndpointRouteBuilder MapAgentLogGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/logs")
            .WithTags("Gateway Logs")
            .RequireAuthorization("TelemetryReader");

        group.MapGet("/sources", SourcesAsync);
        group.MapGet("/history", HistoryAsync);
        return app;
    }

    private static IResult SourcesAsync(
        int tenantId,
        Guid agentId,
        HttpContext context,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services)
    {
        if (!options.IsLogAuthorityActive) return Results.NotFound();
        var tenantAuthorizer = services.GetRequiredService<IOperationsLogTenantAuthorizer>();
        if (!tenantAuthorizer.IsAuthorized(context.User, tenantId)) return Results.Forbid();
        var sessions = services.GetRequiredService<IAgentLogGatewaySessionRegistry>();
        var sources = sessions.GetSources(new ClientKey(tenantId, agentId));
        return sources.Count == 0 ? Results.NotFound() : Results.Ok(sources);
    }

    private static async Task<IResult> HistoryAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string sourceId,
        [FromQuery] string? cursor,
        [FromQuery] int? pageSize,
        [FromQuery] DateTimeOffset? fromUtc,
        [FromQuery] DateTimeOffset? toUtc,
        [FromQuery] string[]? severity,
        [FromQuery] string[]? prefix,
        [FromQuery] string[]? category,
        [FromQuery] string[]? provider,
        [FromQuery] long[]? eventId,
        [FromQuery] string? text,
        HttpContext context,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services)
    {
        if (!options.IsLogAuthorityActive) return Results.NotFound();
        var tenantAuthorizer = services.GetRequiredService<IOperationsLogTenantAuthorizer>();
        if (!tenantAuthorizer.IsAuthorized(context.User, tenantId)) return Results.Forbid();
        if (string.IsNullOrWhiteSpace(sourceId) || sourceId.Length > 128 || cursor?.Length > 256 || text?.Length > 512 ||
            severity?.Length > 8 || prefix?.Length > 32 || category?.Length > 32 || provider?.Length > 32 || eventId?.Length > 32 ||
            category?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) == true || provider?.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 256) == true || eventId?.Any(value => value < 0) == true || fromUtc > toUtc ||
            (fromUtc.HasValue && toUtc.HasValue && toUtc.Value - fromUtc.Value > TimeSpan.FromDays(31)))
        {
            return Results.BadRequest(new { code = "invalid_log_query" });
        }

        var client = new ClientKey(tenantId, agentId);
        var sessions = services.GetRequiredService<IAgentLogGatewaySessionRegistry>();
        var source = sessions.GetSources(client).FirstOrDefault(candidate => string.Equals(candidate.SourceId, sourceId, StringComparison.Ordinal));
        if (source is null) return Results.BadRequest(new { code = "invalid_source" });
        if (!source.Available) return Results.BadRequest(new { code = "source_unavailable" });

        var request = new GatewayLogPageRequest(
            sourceId,
            cursor,
            Math.Clamp(pageSize ?? 100, 1, 100),
            fromUtc,
            toUtc,
            severity,
            prefix,
            text,
            provider,
            eventId,
            category);
        var dispatcher = services.GetRequiredService<IAgentLogGatewayQueryDispatcher>();
        var page = await dispatcher.QueryAsync(client, request, LogQueryOperation.History, context.RequestAborted).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(page.ErrorCode)) return Results.BadRequest(new { code = page.ErrorCode });
        return Results.Ok(page);
    }
}
