using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Middleware;
using NetRatel.API.Services;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production-shaped, policy-admitted access to bounded V2 client logs and
/// telemetry. The gateway workflows are shared with the Development adapter;
/// this route owns only M2M/delegation binding, current policy admission, and
/// accepted-operation audit evidence.
/// </summary>
public static class McpOperatorClientObservabilityEndpoints
{
    private const string LogTool = "netratel_client_logs";
    private const string TelemetryTool = "netratel_client_telemetry";

    public static IEndpointRouteBuilder MapMcpOperatorClientObservabilityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}")
            .WithTags("MCP Operator")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/logs/sources", SourcesAsync);
        group.MapGet("/logs/history", HistoryAsync);
        group.MapGet("/logs/search", SearchAsync);
        group.MapGet("/logs/tail", TailAsync);
        group.MapPost("/logs/resync/preview", ResyncPreviewAsync);
        group.MapPost("/logs/resync/confirm", ResyncConfirmAsync);
        group.MapGet("/telemetry/snapshot", SnapshotAsync);
        group.MapGet("/telemetry/stream-window", StreamWindowAsync);
        return app;
    }

    private static async Task<IResult> SourcesAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        [FromServices] McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateReadContextAsync(
            http, environment, presence, admission, tenantId, agentId, LogTool, "sources", observability.IsLogCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        var result = observability.GetSources(new ClientKey(tenantId, agentId));
        return result.IsSuccess
            ? Results.Ok(new { tenantId, agentId, sources = result.Value, authority = "mcp-operator-policy", correlationId = context.CorrelationId })
            : GatewayFailure(result.FailureCode!, context);
    }

    private static Task<IResult> HistoryAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken) =>
        LogPageAsync("history", requiresText: false, tenantId, agentId, sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, http, environment, presence, admission, observability, cancellationToken);

    private static Task<IResult> SearchAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken) =>
        LogPageAsync("search", requiresText: true, tenantId, agentId, sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, http, environment, presence, admission, observability, cancellationToken);

    private static async Task<IResult> LogPageAsync(
        string operation,
        bool requiresText,
        int tenantId,
        Guid agentId,
        string sourceId,
        string? cursor,
        int? pageSize,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        string[]? severity,
        string[]? prefix,
        string[]? category,
        string[]? provider,
        long[]? eventId,
        string? text,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if ((requiresText && string.IsNullOrWhiteSpace(text)) ||
            !McpOperatorClientObservabilityService.IsValidHistoryRequest(sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text))
            return GatewayFailure("invalid_log_query", MinimalContext(tenantId, agentId, LogTool, operation, http.TraceIdentifier));

        var admitted = await TryCreateReadContextAsync(
            http, environment, presence, admission, tenantId, agentId, LogTool, operation, observability.IsLogCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        var result = await observability.GetHistoryAsync(
            new ClientKey(tenantId, agentId), sourceId, cursor, pageSize, fromUtc, toUtc, severity, prefix, category, provider, eventId, text, cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return GatewayFailure(result.FailureCode!, context);

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
            authority = "mcp-operator-policy",
            correlationId = context.CorrelationId
        });
    }

    private static async Task<IResult> TailAsync(
        int tenantId,
        Guid agentId,
        string sourceId,
        int? windowSeconds,
        int? maxRecords,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidTailRequest(sourceId, windowSeconds, maxRecords))
            return GatewayFailure("invalid_log_query", MinimalContext(tenantId, agentId, LogTool, "tail", http.TraceIdentifier));

        var admitted = await TryCreateReadContextAsync(
            http, environment, presence, admission, tenantId, agentId, LogTool, "tail", observability.IsLogCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        var result = await observability.GetTailAsync(
            new ClientKey(tenantId, agentId),
            sourceId,
            windowSeconds,
            maxRecords,
            token => RecheckAdmissionAsync(
                admission,
                presence,
                new ClientKey(tenantId, agentId),
                () => observability.IsLogCapabilityAvailable,
                context.Request,
                token),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result.IsAdmissionFailure
                ? Failure(result.FailureCode!, context)
                : GatewayFailure(result.FailureCode!, context);

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
            authority = "mcp-operator-policy",
            correlationId = context.CorrelationId
        });
    }

    private static async Task<IResult> SnapshotAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
        => await TelemetrySnapshotAsync(
            tenantId,
            agentId,
            http,
            environment,
            presence,
            admission,
            observability,
            TelemetryTool,
            "snapshot",
            cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Client-administration alias for one bounded current telemetry snapshot.
    /// The policy/audit operation remains <c>netratel_clients/telemetry</c>, so
    /// it cannot inherit the broader observability tool's delegation.
    /// </summary>
    internal static Task<IResult> ClientTelemetryAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        [FromServices] McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken) =>
        TelemetrySnapshotAsync(
            tenantId,
            agentId,
            http,
            environment,
            presence,
            admission,
            observability,
            "netratel_clients",
            "telemetry",
            cancellationToken);

    private static async Task<IResult> TelemetrySnapshotAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        string tool,
        string operation,
        CancellationToken cancellationToken)
    {
        var admitted = await TryCreateReadContextAsync(
            http, environment, presence, admission, tenantId, agentId, tool, operation, observability.IsTelemetryCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        var result = await observability.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? Results.Ok(new { tenantId, agentId, snapshot = result.Value, authority = "mcp-operator-policy", correlationId = context.CorrelationId })
            : GatewayFailure(result.FailureCode!, context);
    }

    private static async Task<IResult> StreamWindowAsync(
        int tenantId,
        Guid agentId,
        int? windowSeconds,
        int? maxSamples,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        if (!McpOperatorClientObservabilityService.IsValidTelemetryWindow(windowSeconds, maxSamples))
            return GatewayFailure("invalid_telemetry_window", MinimalContext(tenantId, agentId, TelemetryTool, "stream_window", http.TraceIdentifier));

        var admitted = await TryCreateReadContextAsync(
            http, environment, presence, admission, tenantId, agentId, TelemetryTool, "stream_window", observability.IsTelemetryCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (await RecordAcceptedAsync(admission, context, cancellationToken).ConfigureAwait(false) is { } rejected)
            return rejected;

        var result = await observability.GetWindowAsync(
            new ClientKey(tenantId, agentId),
            windowSeconds,
            maxSamples,
            token => RecheckAdmissionAsync(
                admission,
                presence,
                new ClientKey(tenantId, agentId),
                () => observability.IsTelemetryCapabilityAvailable,
                context.Request,
                token),
            cancellationToken).ConfigureAwait(false);
        if (!result.IsSuccess)
            return result.IsAdmissionFailure
                ? Failure(result.FailureCode!, context)
                : GatewayFailure(result.FailureCode!, context);

        var window = result.Value!;
        return Results.Ok(new
        {
            tenantId,
            agentId,
            window.Samples,
            window.WindowSeconds,
            window.RequestedMaxSamples,
            authority = "mcp-operator-policy",
            correlationId = context.CorrelationId
        });
    }

    private static async Task<IResult> ResyncPreviewAsync(
        int tenantId,
        Guid agentId,
        McpOperatorLogResyncPreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, LogTool, "resync", http.TraceIdentifier);
        if (!McpOperatorClientObservabilityService.IsValidSourceId(request.SourceId))
            return GatewayFailure("invalid_log_query", fallback);

        var admitted = await TryCreateResyncContextAsync(
            http, environment, presence, admission, tenantId, agentId, "preview_resync", observability.IsLogCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!SourceExists(observability, tenantId, agentId, request.SourceId, out var sourceFailure))
            return GatewayFailure(sourceFailure!, context.FailureContext);

        var payloadHash = PayloadHash(request.SourceId);
        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, payloadHash), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorLogResyncPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                request.SourceId,
                context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context.FailureContext);
        }
    }

    private static async Task<IResult> ResyncConfirmAsync(
        int tenantId,
        Guid agentId,
        McpOperatorLogResyncConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        McpOperatorClientObservabilityService observability,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, LogTool, "resync", http.TraceIdentifier);
        if (!McpOperatorClientObservabilityService.IsValidSourceId(request.SourceId) ||
            !HasPlanCredentials(request.PlanToken, request.IdempotencyKey))
        {
            return GatewayFailure("invalid_log_query", fallback);
        }

        var admitted = await TryCreateResyncContextAsync(
            http, environment, presence, admission, tenantId, agentId, "confirm_resync", observability.IsLogCapabilityAvailable, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure)
            return failure;

        var context = admitted.Context!;
        if (!SourceExists(observability, tenantId, agentId, request.SourceId, out var sourceFailure))
            return GatewayFailure(sourceFailure!, context.FailureContext);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, PayloadHash(request.SourceId), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure)
            return Failure(confirmationFailure, context.FailureContext);
        if (confirmation.IsReplay)
            return ReplayResync(confirmation, context, request.SourceId);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId)
            return Failure("confirmation_plan_invalid", context.FailureContext);

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, cancellationToken).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context.FailureContext);
        }

        try
        {
            var result = await observability.ResyncAsync(
                new ClientKey(tenantId, agentId),
                request.SourceId,
                token => RecheckAdmissionAsync(
                    admission,
                    presence,
                    new ClientKey(tenantId, agentId),
                    () => observability.IsLogCapabilityAvailable,
                    context.Request,
                    token),
                cancellationToken).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, result.FailureCode, cancellationToken).ConfigureAwait(false);
                return result.IsAdmissionFailure
                    ? Failure(result.FailureCode!, context.FailureContext)
                    : GatewayFailure(result.FailureCode!, context.FailureContext);
            }

            var response = ToResyncResult(result.Value!, false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, ResultReference(response), cancellationToken).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (OperationCanceledException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<McpOperatorObservabilityRouteContextResult> TryCreateReadContextAsync(
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        int tenantId,
        Guid agentId,
        string tool,
        string operation,
        bool capabilityAvailable,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, tool, operation, http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
            return Rejected("delegated_identity_required", fallback);

        var operatorEnvironment = environment.IsDevelopment()
            ? McpOperatorEnvironment.Development
            : environment.IsProduction()
                ? McpOperatorEnvironment.Production
                : (McpOperatorEnvironment?)null;
        var expectedInstance = operatorEnvironment switch
        {
            McpOperatorEnvironment.Development => "dev",
            McpOperatorEnvironment.Production => "prod",
            _ => null
        };
        var access = McpOperationAccessCatalog.Find(tool, operation);
        if (operatorEnvironment is null || expectedInstance is null || access is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, tool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, operation, StringComparison.Ordinal) ||
            delegation.TenantId != tenantId || delegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
        {
            return Rejected("delegated_identity_invalid", fallback);
        }

        var targetOnline = (await presence.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var request = new McpOperatorRouteAccessRequest(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                delegation.Identity.Subject,
                delegation.Identity.ClientId,
                delegation.Identity.AuthorizedParty,
                delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            delegation.ServicePrincipal,
            delegation.Resource!,
            delegation.Instance!,
            tool,
            operation,
            tenantId,
            agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            delegation.CorrelationId!,
            delegation.RequestId,
            targetOnline,
            capabilityAvailable);
        var evaluated = await admission.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorObservabilityRouteContext(
            request,
            McpOperationAccessScopeNames.Canonical(access.RequiredScope));
        return evaluated.Decision.IsAllowed
            ? new(context, null)
            : Rejected(evaluated.Decision.FailureCode ?? "target_policy_missing", context);
    }

    private static async Task<McpOperatorLogResyncContextResult> TryCreateResyncContextAsync(
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IClientPresenceRouter presence,
        IMcpOperatorRouteAdmission admission,
        int tenantId,
        Guid agentId,
        string delegatedOperation,
        bool capabilityAvailable,
        CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(tenantId, agentId, LogTool, "resync", http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var delegation) || delegation is null)
            return new(null, Failure("delegated_identity_required", fallback));

        var operatorEnvironment = environment.IsDevelopment()
            ? McpOperatorEnvironment.Development
            : environment.IsProduction()
                ? McpOperatorEnvironment.Production
                : (McpOperatorEnvironment?)null;
        var expectedInstance = operatorEnvironment switch
        {
            McpOperatorEnvironment.Development => "dev",
            McpOperatorEnvironment.Production => "prod",
            _ => null
        };
        var access = McpOperationAccessCatalog.Find(LogTool, "resync");
        if (operatorEnvironment is null || expectedInstance is null || access is null ||
            !string.Equals(delegation.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(delegation.Tool, LogTool, StringComparison.Ordinal) ||
            !string.Equals(delegation.Operation, delegatedOperation, StringComparison.Ordinal) ||
            delegation.TenantId != tenantId || delegation.AgentId != agentId ||
            string.IsNullOrWhiteSpace(delegation.Resource) || string.IsNullOrWhiteSpace(delegation.CorrelationId))
        {
            return new(null, Failure("delegated_identity_invalid", fallback));
        }

        var targetOnline = (await presence.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var request = new McpOperatorRouteAccessRequest(
            operatorEnvironment.Value,
            new McpOperatorPrincipal(
                delegation.Identity.Subject,
                delegation.Identity.ClientId,
                delegation.Identity.AuthorizedParty,
                delegation.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                delegation.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            delegation.ServicePrincipal,
            delegation.Resource!,
            delegation.Instance!,
            LogTool,
            "resync",
            tenantId,
            agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            delegation.CorrelationId!,
            delegation.RequestId,
            targetOnline,
            capabilityAvailable);
        var evaluated = await admission.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var failureContext = new McpOperatorObservabilityRouteContext(
            request,
            McpOperationAccessScopeNames.Canonical(access.RequiredScope));
        return evaluated.Decision.IsAllowed
            ? new(new McpOperatorLogResyncContext(request, evaluated.Decision, failureContext), null)
            : new(null, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", failureContext));
    }

    private static async Task<IResult?> RecordAcceptedAsync(
        IMcpOperatorRouteAdmission admission,
        McpOperatorObservabilityRouteContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, context);
        }
    }

    private static async Task<string?> RecheckAdmissionAsync(
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        ClientKey client,
        Func<bool> capabilityAvailable,
        McpOperatorRouteAccessRequest request,
        CancellationToken cancellationToken)
    {
        var targetOnline = (await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false)).Status == ShadowPresenceStatus.Online;
        var currentRequest = request with
        {
            TargetOnline = targetOnline,
            CapabilityAvailable = capabilityAvailable()
        };
        var reevaluated = await admission.EvaluateAsync(currentRequest, cancellationToken).ConfigureAwait(false);
        return reevaluated.Decision.IsAllowed
            ? null
            : reevaluated.Decision.FailureCode ?? "target_policy_missing";
    }

    private static bool SourceExists(
        McpOperatorClientObservabilityService observability,
        int tenantId,
        Guid agentId,
        string sourceId,
        out string? failure)
    {
        var sources = observability.GetSources(new ClientKey(tenantId, agentId));
        if (!sources.IsSuccess)
        {
            failure = sources.FailureCode;
            return false;
        }

        failure = sources.Value!.Any(source => string.Equals(source.SourceId, sourceId, StringComparison.Ordinal))
            ? null
            : "log_source_not_found";
        return failure is null;
    }

    private static IResult ReplayResync(
        McpOperatorConfirmationAdmission admission,
        McpOperatorLogResyncContext context,
        string sourceId)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending)
            return Failure("idempotency_pending", context.FailureContext);
        if (admission.Outcome != McpOperatorIdempotencyOutcome.Succeeded ||
            !TryReadResultReference(admission.ResultReference, out var receipt) ||
            !string.Equals(receipt.SourceId, sourceId, StringComparison.Ordinal))
        {
            return Failure("idempotency_replay_unavailable", context.FailureContext);
        }

        return Results.Ok(new McpOperatorLogResyncResult(
            receipt.SourceId,
            receipt.ResyncObserved,
            receipt.ResyncRequired,
            receipt.ResyncCompleted,
            receipt.ResyncGuidance,
            true,
            context.CorrelationId));
    }

    private static McpOperatorLogResyncResult ToResyncResult(
        McpOperatorLogResyncResponse response,
        bool replayed,
        string correlationId) =>
        new(
            response.SourceId,
            response.ResyncObserved,
            response.ResyncRequired,
            response.ResyncCompleted,
            response.ResyncGuidance,
            replayed,
            correlationId);

    private static string PayloadHash(string sourceId)
    {
        var canonical = JsonSerializer.Serialize($"resync:{sourceId}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static bool HasPlanCredentials(string? planToken, string? idempotencyKey) =>
        IsOpaqueCredential(planToken) && IsOpaqueCredential(idempotencyKey);

    private static bool IsOpaqueCredential(string? value) =>
        value is { Length: >= 32 and <= 128 } && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static string ResultReference(McpOperatorLogResyncResult response) =>
        JsonSerializer.Serialize(new McpOperatorLogResyncReceipt(
            response.SourceId,
            response.ResyncObserved,
            response.ResyncRequired,
            response.ResyncCompleted,
            response.ResyncGuidance));

    private static bool TryReadResultReference(string? reference, out McpOperatorLogResyncReceipt receipt)
    {
        receipt = default!;
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 512)
            return false;

        try
        {
            receipt = JsonSerializer.Deserialize<McpOperatorLogResyncReceipt>(reference)!;
            return receipt is { SourceId.Length: > 0 and <= 128, ResyncGuidance.Length: > 0 and <= 512 };
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static McpOperatorObservabilityRouteContextResult Rejected(string code, McpOperatorObservabilityRouteContext context) =>
        new(null, Failure(code, context));

    private static McpOperatorObservabilityRouteContext MinimalContext(int tenantId, Guid agentId, string tool, string operation, string correlationId)
    {
        var access = McpOperationAccessCatalog.Find(tool, operation)!;
        return new(null!, McpOperationAccessScopeNames.Canonical(access.RequiredScope), tenantId, agentId, tool, operation, correlationId);
    }

    private static IResult GatewayFailure(string code, McpOperatorObservabilityRouteContext context) => code switch
    {
        "observability_gateway_unavailable" => Failure("capability_unavailable", context),
        "log_sources_unavailable" or "telemetry_unavailable" => Results.Problem(
            statusCode: StatusCodes.Status404NotFound,
            title: "No current bounded observability data is available for the policy-admitted target.",
            extensions: new Dictionary<string, object?> { ["code"] = code, ["correlationId"] = context.CorrelationId }),
        _ => Results.BadRequest(new { success = false, code, correlationId = context.CorrelationId })
    };

    private static IResult Failure(string code, McpOperatorObservabilityRouteContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" => StatusCodes.Status404NotFound,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "target_offline" or "capability_unavailable" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" => "target",
            "capability_unavailable" => "capability",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_failed" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(
            statusCode: status,
            title: "MCP operator observability was not admitted.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["summary"] = "MCP operator observability was not admitted.",
                ["failure"] = new
                {
                    code,
                    layer,
                    retryable = code is "target_offline" or "capability_unavailable" or "idempotency_pending",
                    requiredScopes = new[] { context.RequiredScope },
                    requiredOperation = $"{context.Tool}/{context.Operation}",
                    target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                    safeDetails = SafeDetails(code),
                    remediation = Remediation(code, context.RequiredScope)
                },
                ["correlationId"] = context.CorrelationId
            });
    }

    private static string SafeDetails(string code) => code switch
    {
        "delegated_identity_required" or "delegated_identity_invalid" => "A valid signed operator delegation was not available for this exact observability operation.",
        "oauth_scope_missing" => "The signed delegation does not include the catalogued observability scope.",
        "tenant_not_authorized" => "Target identifiers are withheld until tenant visibility is admitted.",
        "target_not_found" => "No persisted target matched the requested identifier after tenant visibility was admitted.",
        "target_disabled" => "The persisted target is disabled and cannot provide operator observability.",
        "target_offline" => "The persisted target is not currently online through the V2 gateway.",
        "capability_unavailable" => "The target does not currently provide the requested bounded observability capability.",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "The supplied confirmation plan is not valid for this exact current target, source, policy decision, and payload.",
        "idempotency_conflict" => "The supplied idempotency key is already bound to a different confirmed resync request.",
        "idempotency_pending" => "The same confirmed resync is still completing; retry with the unchanged plan credentials later.",
        "idempotency_replay_unavailable" => "The prior confirmed resync did not produce a replayable bounded receipt.",
        _ => "The observability request did not satisfy the current operator policy."
    };

    private static string Remediation(string code, string requiredScope) => code switch
    {
        "delegated_identity_required" => "Invoke the route only through the authenticated MCP service so it can issue a signed delegation assertion.",
        "delegated_identity_invalid" => "Obtain a fresh MCP delegation for this exact operation and target.",
        "oauth_scope_missing" => $"Request the '{requiredScope}' OAuth scope and reauthorize the MCP consumer.",
        "tenant_not_authorized" => "Ask a policy administrator to grant a reviewed tenant observability policy.",
        "target_policy_missing" or "target_operation_not_authorized" => "Ask a policy administrator to review the target profile and observability policy.",
        "target_offline" or "capability_unavailable" => "Reconnect the target gateway capability, then retry the read operation.",
        "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "Start a new resync preview for the unchanged target and source, then confirm it before the plan expires.",
        "idempotency_conflict" => "Use the server-issued idempotency key from this resync preview only with its unchanged confirmation request.",
        "idempotency_pending" => "Retry the same confirmation after the in-flight resync has completed.",
        _ => "Review the caller's scopes, target profile, and current operator policy."
    };

    private sealed record McpOperatorObservabilityRouteContext(
        McpOperatorRouteAccessRequest Request,
        string RequiredScope,
        int TenantId,
        Guid AgentId,
        string Tool,
        string Operation,
        string CorrelationId)
    {
        public McpOperatorObservabilityRouteContext(McpOperatorRouteAccessRequest request, string requiredScope)
            : this(request, requiredScope, request.TenantId, request.AgentId, request.Tool, request.Operation, request.CorrelationId)
        {
        }
    }

    private sealed record McpOperatorObservabilityRouteContextResult(
        McpOperatorObservabilityRouteContext? Context,
        IResult? Failure);

    private sealed record McpOperatorLogResyncContext(
        McpOperatorRouteAccessRequest Request,
        McpOperatorDecision Decision,
        McpOperatorObservabilityRouteContext FailureContext)
    {
        public string CorrelationId => Request.CorrelationId;
    }

    private sealed record McpOperatorLogResyncContextResult(
        McpOperatorLogResyncContext? Context,
        IResult? Failure);

    private sealed record McpOperatorLogResyncPreviewRequest(string SourceId);

    private sealed record McpOperatorLogResyncConfirmRequest(
        string SourceId,
        string PlanToken,
        string IdempotencyKey);

    private sealed record McpOperatorLogResyncPreview(
        string PlanToken,
        string IdempotencyKey,
        DateTimeOffset ExpiresAtUtc,
        McpOperatorConfirmationClass ConfirmationClass,
        int TenantId,
        Guid AgentId,
        string SourceId,
        string CorrelationId);

    private sealed record McpOperatorLogResyncResult(
        string SourceId,
        bool ResyncObserved,
        bool ResyncRequired,
        bool ResyncCompleted,
        string ResyncGuidance,
        bool Replayed,
        string CorrelationId);

    private sealed record McpOperatorLogResyncReceipt(
        string SourceId,
        bool ResyncObserved,
        bool ResyncRequired,
        bool ResyncCompleted,
        string ResyncGuidance);
}
