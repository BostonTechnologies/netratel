using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Application.Agents;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Operations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production operator reads for one persisted V2 client. These routes are
/// intentionally separate from browser administration and legacy collection
/// endpoints: every request is bound to one delegated tenant/agent target and
/// is admitted by the general operator policy before it reads any client fact.
/// </summary>
public static class McpOperatorClientAdministrationEndpoints
{
    private const string Tool = "netratel_clients";

    public static IEndpointRouteBuilder MapMcpOperatorClientAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/clients")
            .WithTags("MCP Operator Clients")
            .RequireAuthorization("M2MOnly");

        group.MapGet(string.Empty, GetAsync);
        group.MapGet("/presence", PresenceAsync);
        group.MapGet("/capabilities", CapabilitiesAsync);
        group.MapGet("/binding", BindingAsync);
        group.MapGet("/telemetry", McpOperatorClientObservabilityEndpoints.ClientTelemetryAsync);
        group.MapGet("/update-attempts", UpdateAttemptsAsync);
        group.MapGet("/update-metadata", UpdateMetadataAsync);
        group.MapPost("/ping/preview", PreviewPingAsync);
        group.MapPost("/ping/confirm", ConfirmPingAsync);
        group.MapPost("/software-update/preview", PreviewSoftwareUpdateAsync);
        group.MapPost("/software-update/confirm", ConfirmSoftwareUpdateAsync);
        group.MapPost("/disable/preview", PreviewDisableAsync);
        group.MapPost("/disable/confirm", ConfirmDisableAsync);
        group.MapPost("/enable/preview", PreviewEnableAsync);
        group.MapPost("/enable/confirm", ConfirmEnableAsync);
        group.MapPost("/delete/preview", PreviewDeleteAsync);
        group.MapPost("/delete/confirm", ConfirmDeleteAsync);
        return app;
    }

    private static async Task<IResult> GetAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("get", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var agent = await agents.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (agent is null) return Failure("target_not_found", context);

            var deviceInfoJson = await db.Agents.AsNoTracking()
                .Where(candidate => candidate.TenantId == tenantId && candidate.Id == agentId)
                .Select(candidate => candidate.DeviceInfoJson)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientDetails(agent, context.CorrelationId)
            {
                Identity = McpOperatorClientIdentity.Create(agent, deviceInfoJson)
            });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PresenceAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceReadModel presence,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("presence", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var projection = await presence.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var item = projection.Items.SingleOrDefault(candidate => candidate.Client.TenantId == tenantId && candidate.Client.AgentId == agentId);
            return Results.Ok(new McpOperatorClientPresence(
                tenantId,
                agentId,
                item?.Status.ToString(),
                item?.LastReceivedAtUtc,
                item?.AgentVersion,
                item?.Capabilities ?? [],
                projection.Revision,
                context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> CapabilitiesAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceReadModel presence,
        [FromServices] IAgentFileGatewaySessionRegistry? files,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("capabilities", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var projection = await presence.GetSnapshotAsync(cancellationToken).ConfigureAwait(false);
            var item = projection.Items.SingleOrDefault(candidate => candidate.Client.TenantId == tenantId && candidate.Client.AgentId == agentId);
            var availability = files?.GetAvailability(new ClientKey(tenantId, agentId));
            var advertised = item?.Capabilities.Contains("file-gateway", StringComparer.OrdinalIgnoreCase) == true;
            var fenceMatchesPresence = availability is not null && item is { Status: ShadowPresenceStatus.Online, ConnectionId: var connectionId, ConnectionEpoch: var epoch } &&
                connectionId == availability.ConnectionId && epoch == checked((long)availability.ConnectionEpoch);
            var readinessReason = item?.Status != ShadowPresenceStatus.Online
                ? "file_gateway_presence_offline"
                : !advertised
                    ? "file_gateway_not_advertised"
                    : availability is null
                        ? "file_gateway_not_admitted"
                        : !fenceMatchesPresence
                            ? "file_gateway_fenced"
                            : null;
            var file = new GatewayFileCapabilityDto(
                Configured: advertised,
                Advertised: advertised,
                SessionActive: availability is not null,
                FenceMatchesPresence: fenceMatchesPresence,
                ClientVersion: item?.AgentVersion,
                NegotiatedCapabilities: availability?.NegotiatedCapabilities ?? [],
                PresenceObservedAtUtc: item?.LastReceivedAtUtc,
                SessionRegisteredAtUtc: availability?.RegisteredAtUtc,
                ReadinessReason: readinessReason);
            return Results.Ok(new McpOperatorClientCapabilities(tenantId, agentId, item?.Capabilities ?? [], item?.LastReceivedAtUtc, projection.Revision, context.CorrelationId, file));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> BindingAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IPrimaryClientAgentBindingService bindings,
        [FromServices] IAgentManagementService agents,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("binding", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (await agents.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false) is null)
                return Failure("target_not_found", context);
            var binding = await bindings.GetByAgentAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientBinding(binding, context.CorrelationId)
            {
                TenantId = tenantId, AgentId = agentId
            });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> UpdateAttemptsAsync(
        int tenantId,
        Guid agentId,
        int? limit,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        if (limit is <= 0 or > 100)
            return Failure("invalid_update_attempt_limit", new McpOperatorClientContext(null!, null!, "netratel.mcp.observe", "update_attempts", http.TraceIdentifier, tenantId, agentId));

        var admitted = await TryContextAsync("update_attempts", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var attempts = await db.ClientUpdateAttempts.AsNoTracking()
                .Where(attempt => attempt.TenantId == tenantId && attempt.AgentId == agentId)
                .OrderByDescending(attempt => attempt.UpdatedAtUtc)
                .Take(limit ?? 50)
                .Select(attempt => new McpOperatorClientUpdateAttempt(
                    attempt.PublicId,
                    attempt.ReleaseId,
                    attempt.RuntimeId,
                    attempt.FromVersion,
                    attempt.TargetVersion,
                    attempt.State.ToString(),
                    attempt.FailureCode,
                    attempt.CreatedAtUtc,
                    attempt.UpdatedAtUtc,
                    attempt.ReadmittedAtUtc,
                    attempt.ConfirmedAtUtc))
                .ToArrayAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientUpdateAttempts(tenantId, agentId, attempts, context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> UpdateMetadataAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] OrchestratorDbContext db,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("update_metadata", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var tenant = await db.Tenants.AsNoTracking()
                .Where(candidate => candidate.Id == tenantId)
                .Select(candidate => new { candidate.AutoUpdate, candidate.AutoUpdateChannel, candidate.AutoUpdateTargetVersion })
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (tenant is null) return Failure("target_not_found", context);

            var state = await db.AgentClientUpdateStates.AsNoTracking()
                .Where(candidate => candidate.TenantId == tenantId && candidate.AgentId == agentId)
                .Select(candidate => new McpOperatorClientUpdateState(
                    candidate.SuspendedAtUtc,
                    candidate.SuspensionReason,
                    candidate.SuppressedReleaseId,
                    candidate.PolicyRevision,
                    candidate.ResumedAtUtc,
                    candidate.ResumedBy))
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientUpdateMetadata(
                tenantId,
                agentId,
                tenant.AutoUpdate,
                tenant.AutoUpdateChannel,
                tenant.AutoUpdateTargetVersion,
                state,
                context.CorrelationId));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewPingAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        [FromServices] NetRatelAkkaMigrationOptions options,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var admitted = await TryPingContextAsync(
            tenantId, agentId, "preview_ping", http, environment, localAgents, admission, presence, options, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, PingPayloadHash(tenantId, agentId)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientPingPreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmPingAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientPingConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        [FromServices] NetRatelAkkaMigrationOptions options,
        [FromServices] IAgentControlSessionRegistry controlSessions,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var admitted = await TryPingContextAsync(
            tenantId, agentId, "ping", http, environment, localAgents, admission, presence, options, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!IsOpaque(request.PlanToken) || !IsOpaque(request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, PingPayloadHash(tenantId, agentId), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmation.IsReplay) return ReplayPing(confirmation, context);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var ping = await controlSessions.RequestPingAsync(new ClientKey(tenantId, agentId), TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            var response = ToPingResult(ping, replayed: false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, PingResultReference(response), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (AgentControlSessionUnavailableException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "agent_control_session_unavailable", CancellationToken.None).ConfigureAwait(false);
            return Failure("agent_control_session_unavailable", context);
        }
        catch (TimeoutException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "ping_timeout", CancellationToken.None).ConfigureAwait(false);
            return Failure("ping_timeout", context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "ping_dispatch_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("ping_dispatch_failed", context);
        }
    }

    private static async Task<IResult> PreviewSoftwareUpdateAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientUpdateOperatorAuthority updates,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("software_update", tenantId, agentId, http, environment, localAgents, admission, cancellationToken, "preview_software_update").ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        var eligibility = await updates.GetResumeEligibilityAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (!eligibility.IsEligible) return Failure(eligibility.FailureCode ?? "client_update_not_suspended", context);

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, SoftwareUpdatePayloadHash(tenantId, agentId)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientSoftwareUpdatePreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                tenantId,
                agentId,
                eligibility.PolicyRevision,
                context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmSoftwareUpdateAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientSoftwareUpdateConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IClientUpdateOperatorAuthority updates,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("software_update", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!IsOpaque(request.PlanToken) || !IsOpaque(request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken, request.IdempotencyKey, SoftwareUpdatePayloadHash(tenantId, agentId), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmation.IsReplay) return ReplaySoftwareUpdate(confirmation, context);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            var eligibility = await updates.GetResumeEligibilityAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
            if (!eligibility.IsEligible)
            {
                var failureCode = eligibility.FailureCode ?? "client_update_not_suspended";
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, failureCode, CancellationToken.None).ConfigureAwait(false);
                return Failure(failureCode, context);
            }

            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var resumed = await updates.ResumeAutomaticUpdatesAsync(tenantId, agentId, context.Request.Principal.Subject, cancellationToken).ConfigureAwait(false);
            if (!resumed.Resumed)
            {
                var failureCode = resumed.FailureCode ?? "client_software_update_failed";
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, failureCode, CancellationToken.None).ConfigureAwait(false);
                return Failure(failureCode, context);
            }

            var response = new McpOperatorClientSoftwareUpdateResult(tenantId, agentId, resumed.PolicyRevision, false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, SoftwareUpdateResultReference(response), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "client_software_update_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("client_software_update_failed", context);
        }
    }

    private static Task<IResult> PreviewDisableAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientDisablePreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        PreviewLifecycleAsync("disable", request.Reason, "preview_disable", tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static Task<IResult> ConfirmDisableAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientDisableConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        ConfirmLifecycleAsync("disable", request.Reason, request.PlanToken, request.IdempotencyKey, tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static Task<IResult> PreviewEnableAsync(
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        PreviewLifecycleAsync("enable", null, "preview_enable", tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static Task<IResult> ConfirmEnableAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientEnableConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        ConfirmLifecycleAsync("enable", null, request.PlanToken, request.IdempotencyKey, tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static Task<IResult> PreviewDeleteAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientDeletePreviewRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        PreviewLifecycleAsync("delete", request.Reason, "preview_delete", tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static Task<IResult> ConfirmDeleteAsync(
        int tenantId,
        Guid agentId,
        McpOperatorClientDeleteConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] McpOperatorLocalAgentOptions localAgents,
        [FromServices] IMcpOperatorRouteAdmission admission,
        [FromServices] IAgentManagementService agents,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken) =>
        ConfirmLifecycleAsync("delete", request.Reason, request.PlanToken, request.IdempotencyKey, tenantId, agentId, http, environment, localAgents, admission, agents, confirmations, cancellationToken);

    private static async Task<IResult> PreviewLifecycleAsync(
        string action,
        string? reason,
        string delegatedOperation,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorRouteAdmission admission,
        IAgentManagementService agents,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var fallback = new McpOperatorClientContext(null!, null!, "netratel.mcp.admin", action, http.TraceIdentifier, tenantId, agentId);
        if (RequiresLifecycleReason(action) && !IsLifecycleReason(reason)) return Failure("client_lifecycle_reason_invalid", fallback);

        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, localAgents, admission, cancellationToken, delegatedOperation).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        var stateFailure = await LifecycleStateFailureAsync(action, tenantId, agentId, agents, context, cancellationToken).ConfigureAwait(false);
        if (stateFailure is not null) return stateFailure;

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, LifecyclePayloadHash(action, reason)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorClientLifecyclePreview(
                plan.PlanToken,
                plan.IdempotencyKey,
                plan.ExpiresAtUtc,
                plan.ConfirmationClass,
                action,
                tenantId,
                agentId,
                context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmLifecycleAsync(
        string action,
        string? reason,
        string? planToken,
        string? idempotencyKey,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorRouteAdmission admission,
        IAgentManagementService agents,
        IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var fallback = new McpOperatorClientContext(null!, null!, "netratel.mcp.admin", action, http.TraceIdentifier, tenantId, agentId);
        if (RequiresLifecycleReason(action) && !IsLifecycleReason(reason)) return Failure("client_lifecycle_reason_invalid", fallback);

        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!IsOpaque(planToken) || !IsOpaque(idempotencyKey)) return Failure("confirmation_plan_invalid", context);

        var confirmation = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(planToken!, idempotencyKey!, LifecyclePayloadHash(action, reason), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmation.IsReplay) return ReplayLifecycle(confirmation, context);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        var stateFailure = await LifecycleStateFailureAsync(action, tenantId, agentId, agents, context, cancellationToken).ConfigureAwait(false);
        if (stateFailure is not null)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "client_lifecycle_state_invalid", CancellationToken.None).ConfigureAwait(false);
            return stateFailure;
        }

        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            if (action == "disable")
                await agents.DisableAsync(tenantId, agentId, reason!.Trim(), context.Request.Principal.Subject, cancellationToken).ConfigureAwait(false);
            else if (action == "enable")
                await agents.EnableAsync(tenantId, agentId, context.Request.Principal.Subject, cancellationToken).ConfigureAwait(false);
            else
                await agents.DeleteAsync(tenantId, agentId, reason!.Trim(), context.Request.Principal.Subject, cancellationToken).ConfigureAwait(false);

            var response = new McpOperatorClientLifecycleResult(tenantId, agentId, action, action == "enable", false, context.CorrelationId);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, LifecycleResultReference(response), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }
        catch (AgentAuthException exception) when (string.Equals(exception.Code, "agent_not_found", StringComparison.Ordinal))
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "target_not_found", CancellationToken.None).ConfigureAwait(false);
            return Failure("target_not_found", context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "client_lifecycle_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("client_lifecycle_failed", context);
        }
    }

    private static async Task<IResult?> LifecycleStateFailureAsync(
        string action,
        int tenantId,
        Guid agentId,
        IAgentManagementService agents,
        McpOperatorClientContext context,
        CancellationToken cancellationToken)
    {
        var agent = await agents.GetAsync(tenantId, agentId, cancellationToken).ConfigureAwait(false);
        if (agent is null) return Failure("target_not_found", context);
        return action switch
        {
            "disable" when !agent.IsEnabled => Failure("client_already_disabled", context),
            "enable" when agent.IsEnabled => Failure("client_already_enabled", context),
            _ => null
        };
    }

    private static async Task<McpOperatorClientContextResult> TryContextAsync(
        string operation,
        int tenantId,
        Guid agentId,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorRouteAdmission admission,
        CancellationToken cancellationToken,
        string? delegatedOperation = null,
        bool targetOnline = true,
        bool capabilityAvailable = true)
    {
        var fallback = new McpOperatorClientContext(null!, null!, string.Empty, operation, http.TraceIdentifier, tenantId, agentId);
        var expectedDelegatedOperation = delegatedOperation ?? operation;
        var hasDelegation = http.TryGetMcpOperatorDelegation(out var delegation) && delegation is not null;
        if (!hasDelegation && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool, expectedDelegatedOperation, tenantId, agentId, out delegation))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));

        var effective = delegation!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            tenantId <= 0 || agentId == Guid.Empty || access is null ||
            !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(effective.Operation, expectedDelegatedOperation, StringComparison.Ordinal) ||
            effective.TenantId != tenantId || effective.AgentId != agentId ||
            string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));

        var request = new McpOperatorRouteAccessRequest(
            operatorEnvironment,
            new McpOperatorPrincipal(
                effective.Identity.Subject,
                effective.Identity.ClientId,
                effective.Identity.AuthorizedParty,
                effective.Identity.Groups.ToHashSet(StringComparer.Ordinal),
                effective.Identity.Roles.ToHashSet(StringComparer.Ordinal),
                effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal),
                effective.ServicePrincipal),
            effective.ServicePrincipal,
            effective.Resource!,
            effective.Instance!,
            Tool,
            operation,
            tenantId,
            agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            effective.CorrelationId!,
            effective.RequestId,
            TargetOnline: targetOnline,
            CapabilityAvailable: capabilityAvailable);
        var evaluated = await admission.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorClientContext(request, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, effective.CorrelationId!, tenantId, agentId);
        return evaluated.Decision.IsAllowed ? new(context, null) : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static async Task<McpOperatorClientContextResult> TryPingContextAsync(
        int tenantId,
        Guid agentId,
        string delegatedOperation,
        HttpContext http,
        IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents,
        IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        NetRatelAkkaMigrationOptions options,
        CancellationToken cancellationToken)
    {
        var snapshot = await presence.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken).ConfigureAwait(false);
        return await TryContextAsync(
            "ping",
            tenantId,
            agentId,
            http,
            environment,
            localAgents,
            admission,
            cancellationToken,
            delegatedOperation,
            snapshot.Status == ShadowPresenceStatus.Online,
            options.IsPingAuthorityActive).ConfigureAwait(false);
    }

    private static IResult Failure(string code, McpOperatorClientContext context)
    {
        var status = code switch
        {
            "invalid_update_attempt_limit" or "client_lifecycle_reason_invalid" => StatusCodes.Status400BadRequest,
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "target_not_found" or "client_binding_not_found" => StatusCodes.Status404NotFound,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" or "client_already_disabled" or "client_already_enabled" or "client_update_not_suspended" or "client_auto_update_disabled" or "target_disabled" => StatusCodes.Status409Conflict,
            "target_offline" or "capability_unavailable" or "agent_control_session_unavailable" or "ping_dispatch_failed" or "client_lifecycle_failed" or "client_software_update_failed" => StatusCodes.Status503ServiceUnavailable,
            "ping_timeout" => StatusCodes.Status504GatewayTimeout,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "invalid_update_attempt_limit" or "client_lifecycle_reason_invalid" => "constraint",
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "target_not_found" or "target_disabled" or "target_offline" or "client_binding_not_found" or "agent_control_session_unavailable" => "target",
            "capability_unavailable" => "capability",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            "client_already_disabled" or "client_already_enabled" or "client_update_not_suspended" or "client_auto_update_disabled" => "target",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator client access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator client access was not admitted.",
            ["failure"] = new
            {
                code,
                retryable = false,
                requiredScopes = new[] { context.RequiredScope },
                requiredOperation = $"{Tool}/{context.Operation}",
                target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId },
                safeDetails = "The requested client operation requires an exact signed delegation and an active ClientAdministration policy.",
                remediation = "Use the exact delegated tenant and agent target with the required OAuth scope, or ask a policy administrator to review the target policy."
            },
            ["correlationId"] = context.CorrelationId
        });
    }

    private static IResult ReplayPing(McpOperatorConfirmationAdmission admission, McpOperatorClientContext context)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        return admission.Outcome == McpOperatorIdempotencyOutcome.Succeeded && TryReadPingResult(admission.ResultReference, out var result)
            ? Results.Ok(result with { Replayed = true, CorrelationId = context.CorrelationId })
            : Failure("idempotency_replay_unavailable", context);
    }

    private static IResult ReplayLifecycle(McpOperatorConfirmationAdmission admission, McpOperatorClientContext context)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        return admission.Outcome == McpOperatorIdempotencyOutcome.Succeeded && TryReadLifecycleResult(admission.ResultReference, out var result) &&
               result.TenantId == context.TenantId && result.AgentId == context.AgentId && string.Equals(result.Action, context.Operation, StringComparison.Ordinal)
            ? Results.Ok(result with { Replayed = true, CorrelationId = context.CorrelationId })
            : Failure("idempotency_replay_unavailable", context);
    }

    private static IResult ReplaySoftwareUpdate(McpOperatorConfirmationAdmission admission, McpOperatorClientContext context)
    {
        if (admission.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        return admission.Outcome == McpOperatorIdempotencyOutcome.Succeeded && TryReadSoftwareUpdateResult(admission.ResultReference, out var result) &&
               result.TenantId == context.TenantId && result.AgentId == context.AgentId
            ? Results.Ok(result with { Replayed = true, CorrelationId = context.CorrelationId })
            : Failure("idempotency_replay_unavailable", context);
    }

    private static McpOperatorClientPingResult ToPingResult(AgentControlPingResult result, bool replayed, string correlationId) =>
        new(result.Client.TenantId, result.Client.AgentId, result.RequestId, result.SentAtUtc, result.ReceivedAtUtc, result.AgentRespondedAtUtc,
            Math.Max(0, result.RoundTripTime.TotalMilliseconds), "akka", replayed, correlationId);

    private static string PingPayloadHash(int tenantId, Guid agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"ping:{tenantId}:{agentId:D}")));

    private static string LifecyclePayloadHash(string action, string? reason) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"client-lifecycle:{action}:{reason?.Trim() ?? string.Empty}")));

    private static string SoftwareUpdatePayloadHash(int tenantId, Guid agentId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"client-software-update-resume:{tenantId}:{agentId:D}")));

    private static string PingResultReference(McpOperatorClientPingResult response) => JsonSerializer.Serialize(response with { Replayed = false, CorrelationId = string.Empty });

    private static bool TryReadPingResult(string? reference, out McpOperatorClientPingResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 1024) return false;
        try
        {
            result = JsonSerializer.Deserialize<McpOperatorClientPingResult>(reference)!;
            return result is { TenantId: > 0, AgentId: var agentId } && agentId != Guid.Empty && result.RequestId != Guid.Empty &&
                result.SentAtUtc <= result.ReceivedAtUtc && result.AgentRespondedAtUtc != default && result.RoundTripMilliseconds >= 0 &&
                string.Equals(result.Authority, "akka", StringComparison.Ordinal);
        }
        catch (JsonException) { return false; }
    }

    private static string LifecycleResultReference(McpOperatorClientLifecycleResult response) =>
        JsonSerializer.Serialize(response with { Replayed = false, CorrelationId = string.Empty });

    private static bool TryReadLifecycleResult(string? reference, out McpOperatorClientLifecycleResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 1024) return false;
        try
        {
            result = JsonSerializer.Deserialize<McpOperatorClientLifecycleResult>(reference)!;
            return result is { TenantId: > 0, AgentId: var agentId } && agentId != Guid.Empty &&
                result.Action is "disable" or "enable" or "delete" && result.Enabled == (result.Action == "enable");
        }
        catch (JsonException) { return false; }
    }

    private static string SoftwareUpdateResultReference(McpOperatorClientSoftwareUpdateResult response) =>
        JsonSerializer.Serialize(response with { Replayed = false, CorrelationId = string.Empty });

    private static bool TryReadSoftwareUpdateResult(string? reference, out McpOperatorClientSoftwareUpdateResult result)
    {
        result = default!;
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > 1024) return false;
        try
        {
            result = JsonSerializer.Deserialize<McpOperatorClientSoftwareUpdateResult>(reference)!;
            return result is { TenantId: > 0, AgentId: var agentId, PolicyRevision: > 0 } && agentId != Guid.Empty;
        }
        catch (JsonException) { return false; }
    }

    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static bool RequiresLifecycleReason(string action) => action is "disable" or "delete";
    private static bool IsLifecycleReason(string? value) => value is { Length: > 0 and <= 256 } && !string.IsNullOrWhiteSpace(value) && value.All(character => !char.IsControl(character));

    private sealed record McpOperatorClientContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, string Operation, string CorrelationId, int TenantId, Guid AgentId);
    private sealed record McpOperatorClientContextResult(McpOperatorClientContext? Context, IResult? Failure);
}

public sealed record McpOperatorClientDetails(AgentDetailDto Client, string CorrelationId)
{
    public McpOperatorClientIdentity? Identity { get; init; }
}

public sealed record McpOperatorClientIdentity(string DisplayName, string? HostName, string? OperatingSystem, string? Architecture)
{
    private const int MaximumIdentityFieldLength = 256;

    internal static McpOperatorClientIdentity Create(AgentDetailDto agent, string? deviceInfoJson)
    {
        var presentation = AgentDirectoryPresentation.Create(
            agent.TenantId,
            agent.AgentId,
            agent.DisplayName,
            agent.IsEnabled,
            deviceInfoJson,
            string.Empty);
        return new(
            Bounded(presentation.DisplayName) ?? $"Agent-{agent.AgentId:N}"[..14],
            Bounded(presentation.HostName),
            Bounded(presentation.OperatingSystem),
            Bounded(presentation.Architecture));
    }

    private static string? Bounded(string? value) => value is { Length: <= MaximumIdentityFieldLength } ? value : null;
}

public sealed record McpOperatorClientPresence(int TenantId, Guid AgentId, string? Status, DateTimeOffset? LastReceivedAtUtc, string? AgentVersion, IReadOnlyList<string> Capabilities, long Revision, string CorrelationId);
public sealed record McpOperatorClientCapabilities(int TenantId, Guid AgentId, IReadOnlyList<string> Capabilities, DateTimeOffset? LastReceivedAtUtc, long Revision, string CorrelationId, GatewayFileCapabilityDto? File = null);
public sealed record McpOperatorClientBinding(PrimaryClientAgentBindingDto? Binding, string CorrelationId)
{
    public int TenantId { get; init; }
    public Guid AgentId { get; init; }
    public bool HasBinding => Binding is not null;
}
public sealed record McpOperatorClientUpdateAttempt(Guid AttemptId, int ReleaseId, string RuntimeId, string FromVersion, string TargetVersion, string State, string? FailureCode, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? ReadmittedAtUtc, DateTimeOffset? ConfirmedAtUtc);
public sealed record McpOperatorClientUpdateAttempts(int TenantId, Guid AgentId, IReadOnlyList<McpOperatorClientUpdateAttempt> Items, string CorrelationId);
public sealed record McpOperatorClientUpdateState(DateTimeOffset? SuspendedAtUtc, string? SuspensionReason, Guid? SuppressedReleaseId, long PolicyRevision, DateTimeOffset? ResumedAtUtc, string? ResumedBy);
public sealed record McpOperatorClientUpdateMetadata(int TenantId, Guid AgentId, bool AutoUpdate, string AutoUpdateChannel, string? AutoUpdateTargetVersion, McpOperatorClientUpdateState? State, string CorrelationId);
public sealed record McpOperatorClientPingConfirmRequest(string PlanToken, string IdempotencyKey);
public sealed record McpOperatorClientPingPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, int TenantId, Guid AgentId, string CorrelationId);
public sealed record McpOperatorClientPingResult(int TenantId, Guid AgentId, Guid RequestId, DateTimeOffset SentAtUtc, DateTimeOffset ReceivedAtUtc, DateTimeOffset AgentRespondedAtUtc, double RoundTripMilliseconds, string Authority, bool Replayed, string CorrelationId);
public sealed record McpOperatorClientSoftwareUpdateConfirmRequest(string PlanToken, string IdempotencyKey);
public sealed record McpOperatorClientSoftwareUpdatePreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, int TenantId, Guid AgentId, long? PolicyRevision, string CorrelationId);
public sealed record McpOperatorClientSoftwareUpdateResult(int TenantId, Guid AgentId, long? PolicyRevision, bool Replayed, string CorrelationId);
public sealed record McpOperatorClientDisablePreviewRequest(string Reason);
public sealed record McpOperatorClientDisableConfirmRequest(string Reason, string PlanToken, string IdempotencyKey);
public sealed record McpOperatorClientDeletePreviewRequest(string Reason);
public sealed record McpOperatorClientDeleteConfirmRequest(string Reason, string PlanToken, string IdempotencyKey);
public sealed record McpOperatorClientEnableConfirmRequest(string PlanToken, string IdempotencyKey);
public sealed record McpOperatorClientLifecyclePreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, int TenantId, Guid AgentId, string CorrelationId);
public sealed record McpOperatorClientLifecycleResult(int TenantId, Guid AgentId, string Action, bool Enabled, bool Replayed, string CorrelationId);
