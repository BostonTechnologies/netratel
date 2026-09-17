using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Realtime;
using NetRatel.API.Services;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Development compatibility adapter for the shared bounded V2 observability
/// workflows. Production operator routes use the same workflow with policy
/// admission rather than this Development target grant.
/// </summary>
public static class DevelopmentMcpClientObservabilityEndpoints
{
    private const string Authority = "development-mcp-target-gate";

    public static IEndpointRouteBuilder MapDevelopmentMcpClientObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var logs = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/logs")
            .WithTags("Development MCP Client Observability")
            .RequireAuthorization("Operator");
        logs.MapGet("/sources", SourcesAsync);
        logs.MapGet("/history", HistoryAsync);
        logs.MapGet("/search", SearchAsync);
        logs.MapGet("/tail", TailAsync);
        logs.MapPost("/resync", ResyncAsync);

        var telemetry = app.MapGroup("/api/v2/development/mcp/agents/{tenantId:int}/{agentId:guid}/telemetry")
            .WithTags("Development MCP Client Observability")
            .RequireAuthorization("Operator");
        telemetry.MapGet("/snapshot", SnapshotAsync);
        telemetry.MapGet("/stream-window", StreamWindowAsync);
        return app;
    }

    private static async Task<IResult> SourcesAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientLogRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = observability.GetSources(new ClientKey(tenantId, agentId));
        return result.IsSuccess
            ? Results.Ok(new { tenantId, agentId, sources = result.Value, authority = Authority })
            : Failure(result.FailureCode!);
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
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidHistoryRequest(sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text))
            return Failure("invalid_log_query");
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientLogRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.GetHistoryAsync(
            new ClientKey(tenantId, agentId), sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failure(result.FailureCode!);

        var history = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            sourceId = history.SourceId,
            page = history.Page,
            history.DroppedRecordCount,
            history.ResyncRequired,
            history.ResyncGuidance,
            authority = Authority
        });
    }

    private static async Task<IResult> SearchAsync(
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
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) ||
            !McpOperatorClientObservabilityService.IsValidHistoryRequest(sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text))
            return Failure("invalid_log_query");
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientLogRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.GetHistoryAsync(
            new ClientKey(tenantId, agentId), sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failure(result.FailureCode!);

        var history = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            sourceId = history.SourceId,
            page = history.Page,
            history.DroppedRecordCount,
            history.ResyncRequired,
            history.ResyncGuidance,
            authority = Authority
        });
    }

    private static async Task<IResult> TailAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] string sourceId,
        [FromQuery] int? windowSeconds,
        [FromQuery] int? maxRecords,
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidTailRequest(sourceId, windowSeconds, maxRecords))
            return Failure("invalid_log_query");
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientLogRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.GetTailAsync(new ClientKey(tenantId, agentId), sourceId, windowSeconds, maxRecords, null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failure(result.FailureCode!);

        var tail = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            sourceId = tail.SourceId,
            tail.Records,
            tail.WindowSeconds,
            tail.RequestedMaxRecords,
            tail.DroppedRecordCount,
            tail.ResyncRequired,
            tail.ResyncGuidance,
            authority = Authority
        });
    }

    private static async Task<IResult> ResyncAsync(
        int tenantId,
        Guid agentId,
        [FromBody] DevelopmentLogResyncRequest request,
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidSourceId(request.SourceId))
            return Failure("invalid_log_query");
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientLogResync, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.ResyncAsync(new ClientKey(tenantId, agentId), request.SourceId, null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failure(result.FailureCode!);

        var resync = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            sourceId = resync.SourceId,
            page = resync.Page,
            resync.DroppedRecordCount,
            resync.ResyncObserved,
            resync.ResyncRequired,
            resync.ResyncCompleted,
            resync.ResyncGuidance,
            authority = Authority
        });
    }

    private static async Task<IResult> SnapshotAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientTelemetryRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new { tenantId, agentId, snapshot = result.Value, authority = Authority })
            : Failure(result.FailureCode!);
    }

    private static async Task<IResult> StreamWindowAsync(
        int tenantId,
        Guid agentId,
        [FromQuery] int? windowSeconds,
        [FromQuery] int? maxSamples,
        HttpContext http,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidTelemetryWindow(windowSeconds, maxSamples))
            return Failure("invalid_telemetry_window");
        if (await RequireAcceptedAsync(http, tenantId, agentId, DevelopmentOperatorOperation.ClientTelemetryRead, correlation, targets, cancellationToken).ConfigureAwait(false) is { } rejection)
            return rejection;

        var result = await observability.GetWindowAsync(new ClientKey(tenantId, agentId), windowSeconds, maxSamples, null, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return Failure(result.FailureCode!);

        var window = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            window.Samples,
            window.WindowSeconds,
            window.RequestedMaxSamples,
            authority = Authority
        });
    }

    private static async Task<IResult?> RequireAcceptedAsync(
        HttpContext http,
        int tenantId,
        Guid agentId,
        DevelopmentOperatorOperation operation,
        ICorrelationContext correlation,
        IDevelopmentOperatorTargetAuthority targets,
        CancellationToken cancellationToken)
        => await DevelopmentOperatorTargetGate.RequireAcceptedAsync(http, tenantId, agentId, operation, correlation, targets, cancellationToken).ConfigureAwait(false);

    private static IResult Failure(string code) => code switch
    {
        "observability_gateway_unavailable" or "log_sources_unavailable" or "telemetry_unavailable" => Results.NotFound(),
        _ => Results.BadRequest(new { code })
    };

    private sealed record DevelopmentLogResyncRequest(string SourceId);
}
