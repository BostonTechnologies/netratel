using System.Text.Json;
using System.Security.Claims;
using Akka.Actor;
using Akka.Hosting;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.API.Services.RemoteSupport;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.RemoteSupport;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.RemoteSupport;

namespace NetRatel.API.Endpoints;

/// <summary>
/// V2 agent-ID remote-support browser adapter. It is intentionally additive:
/// existing primary-card remote support and terminal routes are untouched.
/// </summary>
public static class AgentRemoteSupportGatewayEndpoints
{
    public static IEndpointRouteBuilder MapAgentRemoteSupportGatewayEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/remote-support")
            .WithTags("Gateway Remote Support")
            .RequireAuthorization("Operator");
        group.MapPost("/sessions", OpenAsync);
        group.MapGet("/v2/capabilities", GetV2Capabilities);
        group.MapGet("/v2/inventory", GetV2Inventory);
        group.MapPost("/v2/inventory/refresh", RefreshV2InventoryAsync);
        group.MapPost("/v2/prepare", PrepareV2TargetAsync);
        group.MapPost("/v2/lifecycle/sessions", OpenV2LifecycleAsync);
        group.MapGet("/v2/lifecycle/sessions/{sessionId:guid}", GetV2LifecycleAsync);
        group.MapGet("/v2/lifecycle/sessions/{sessionId:guid}/ice-configuration", GetV2IceConfigurationAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/close", CloseV2LifecycleAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/resume", ResumeV2LifecycleAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/sas", SendV2SasAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/repair", RepairV2HelperAsync);
        group.MapGet("/v2/lifecycle/sessions/{sessionId:guid}/transition-target", GetV2TransitionTargetAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/transition-target", SelectV2TransitionTargetAsync);
        group.MapGet("/v2/lifecycle/sessions/{sessionId:guid}/events", StreamV2LifecycleEventsAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/prepare", PrepareV2MediaAsync);
        group.MapPost("/v2/lifecycle/sessions/{sessionId:guid}/negotiation", SendV2NegotiationAsync);
        group.MapGet("/v2/lifecycle/sessions/{sessionId:guid}/negotiation", StreamV2NegotiationAsync);

        var sessions = app.MapGroup("/api/v2/gateway-remote-support/{sessionId}")
            .WithTags("Gateway Remote Support")
            .RequireAuthorization("Operator");
        sessions.MapGet("", GetAsync);
        sessions.MapGet("/signals", StreamSignalsAsync);
        sessions.MapPost("/signals", SendSignalAsync);
        sessions.MapPost("/close", CloseAsync);
        return app;
    }

    private static async Task<IResult> OpenAsync(
        int tenantId,
        Guid agentId,
        OpenRemoteSupportRequest request,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsLegacyRemoteSupportGatewayActive)
        {
            return Results.NotFound();
        }

        if (!RemoteSupportTargetResolver.TryResolve(request, out _, out var error))
        {
            return Results.BadRequest(error);
        }

        try
        {
            var sessions = services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
            var session = await sessions.OpenAsync(new ClientKey(tenantId, agentId), request, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-remote-support/{session.SessionId}", ToResponse(session));
        }
        catch (GatewayRemoteSupportSessionUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static IResult GetV2Inventory(
        int tenantId,
        Guid agentId,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider)
    {
        if (!options.IsRemoteSupportV2InventoryActive)
        {
            return Results.NotFound();
        }

        var client = new ClientKey(tenantId, agentId);
        var projection = services.GetRequiredService<IRemoteSupportV2PreparationRegistry>().GetInventory(client);
        return projection switch
        {
            null => Results.NotFound(),
            { } when !projection.IsFresh(timeProvider.GetUtcNow()) => Results.Problem(
                "The agent inventory projection is stale.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Ok(projection.Snapshot)
        };
    }

    private static IResult GetV2Capabilities(
        int tenantId,
        Guid agentId,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider)
    {
        if (!options.IsRemoteSupportV2InventoryActive)
        {
            return Results.NotFound();
        }

        var projection = services.GetRequiredService<IRemoteSupportV2PreparationRegistry>()
            .GetCapabilities(new ClientKey(tenantId, agentId));
        return projection switch
        {
            null => Results.NotFound(),
            { } when !projection.IsFresh(timeProvider.GetUtcNow()) => Results.Problem(
                "The agent Remote Support capability snapshot is stale.", statusCode: StatusCodes.Status409Conflict),
            _ => Results.Ok(projection)
        };
    }

    private static async Task<IResult> RefreshV2InventoryAsync(
        int tenantId,
        Guid agentId,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2InventoryActive)
        {
            return Results.NotFound();
        }

        try
        {
            await services.GetRequiredService<IRemoteSupportV2PreparationRegistry>()
                .RequestInventoryRefreshAsync(new ClientKey(tenantId, agentId), cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted();
        }
        catch (RemoteSupportV2InventoryUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static async Task<IResult> PrepareV2TargetAsync(
        int tenantId,
        Guid agentId,
        RemoteSupportPrepareTargetRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2InventoryActive)
        {
            return Results.NotFound();
        }

        var operatorId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub")
            ?? http.User.Identity?.Name;
        if (string.IsNullOrWhiteSpace(operatorId))
        {
            return Results.Forbid();
        }

        try
        {
            var result = await services.GetRequiredService<IRemoteSupportV2PreparationRegistry>()
                .PrepareAsync(
                    new ClientKey(tenantId, agentId),
                    new RemoteSupportOperatorBinding(operatorId),
                    request.Target,
                    cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (RemoteSupportV2InventoryUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (RemoteSupportV2InventoryStaleException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static async Task<IResult> OpenV2LifecycleAsync(
        int tenantId,
        Guid agentId,
        RemoteSupportV2LifecycleOpenRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        Microsoft.Extensions.Options.IOptions<RemoteSupportIceOptions> iceOptions,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        var command = new RemoteSupportOpenSessionCommand(
            RemoteSupportV2ContractVersions.Current,
            tenantId,
            agentId,
            request.RequestId,
            operatorBinding,
            request.Target,
            request.RequestedCapabilities,
            timeProvider.GetUtcNow(),
            timeProvider.GetUtcNow().Add(iceOptions.Value.SessionLifetime));
        try
        {
            var snapshot = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
                .OpenAsync(command, cancellationToken)
                .ConfigureAwait(false);
            return Results.Accepted(V2LifecycleUri(snapshot.Session), snapshot);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception.Message);
        }
        catch (InvalidOperationException exception)
        {
            return Results.Conflict(new { code = "remote_support_request_conflict", detail = exception.Message });
        }
    }

    private static async Task<IResult> GetV2LifecycleAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        var snapshot = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
            .GetAsync(new(tenantId, agentId, sessionId), operatorBinding, cancellationToken)
            .ConfigureAwait(false);
        return snapshot is null ? Results.NotFound() : Results.Ok(snapshot);
    }

    private static async Task<IResult> GetV2IceConfigurationAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        long? generation,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        var requestedGeneration = generation.GetValueOrDefault();
        if (operatorBinding is null || requestedGeneration <= 0)
        {
            return Results.Forbid();
        }

        var session = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        var snapshot = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
            .GetAsync(session, operatorBinding, cancellationToken).ConfigureAwait(false);
        if (snapshot is not
            {
                State: RemoteSupportV2SessionStates.ReadyForOffer,
                ExpiresAtUtc: { } expiresAtUtc
            } || expiresAtUtc <= timeProvider.GetUtcNow())
        {
            return Results.NotFound();
        }

        return Results.Ok(services.GetRequiredService<IRemoteSupportIceConfigurationProvider>().GetForSession(snapshot, requestedGeneration));
    }

    private static async Task<IResult> CloseV2LifecycleAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        RemoteSupportV2LifecycleControlRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        return await DispatchV2ControlAsync(
            tenantId, agentId, sessionId, request, operatorBinding, RemoteSupportV2ControlTypes.Close,
            services, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> SendV2SasAsync(
        int tenantId, Guid agentId, Guid sessionId, RemoteSupportV2LifecycleControlRequest request,
        HttpContext http, NetRatelAkkaMigrationOptions options, IServiceProvider services,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive) return Results.NotFound();
        var binding = TryGetOperator(http);
        return binding is null ? Results.Forbid() : await DispatchV2ControlAsync(
            tenantId, agentId, sessionId, request, binding, RemoteSupportV2ControlTypes.RequestSas,
            services, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> RepairV2HelperAsync(
        int tenantId, Guid agentId, Guid sessionId, RemoteSupportV2LifecycleControlRequest request,
        HttpContext http, NetRatelAkkaMigrationOptions options, IServiceProvider services,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive) return Results.NotFound();
        var binding = TryGetOperator(http);
        return binding is null ? Results.Forbid() : await DispatchV2ControlAsync(
            tenantId, agentId, sessionId, request, binding, RemoteSupportV2ControlTypes.RepairHelper,
            services, timeProvider, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<IResult> DispatchV2ControlAsync(
        int tenantId, Guid agentId, Guid sessionId, RemoteSupportV2LifecycleControlRequest request,
        RemoteSupportOperatorBinding operatorBinding, string controlType, IServiceProvider services,
        TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        var command = new RemoteSupportControlCommand(
            RemoteSupportV2ContractVersions.Current,
            new(tenantId, agentId, sessionId),
            operatorBinding,
            request.RequestId,
            controlType,
            request.ExpectedLifecycleRevision,
            timeProvider.GetUtcNow());
        var result = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
            .ControlAsync(command, cancellationToken)
            .ConfigureAwait(false);
        return result.Disposition switch
        {
            RemoteSupportLifecycleTransitionDisposition.Applied or RemoteSupportLifecycleTransitionDisposition.Duplicate when result.Snapshot is not null =>
                Results.Accepted(V2LifecycleUri(result.Snapshot.Session), result.Snapshot),
            RemoteSupportLifecycleTransitionDisposition.StaleRevision when result.Snapshot is not null =>
                Results.Conflict(result.Snapshot),
            RemoteSupportLifecycleTransitionDisposition.Missing => Results.NotFound(),
            _ => Results.Conflict(new { code = "remote_support_control_rejected" })
        };
    }

    private static async Task<IResult> ResumeV2LifecycleAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        RemoteSupportV2LifecycleResumeRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        var result = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
            .ResumeAsync(new(
                    RemoteSupportV2ContractVersions.Current,
                    new(tenantId, agentId, sessionId),
                    operatorBinding,
                    request.AfterAuditSequence,
                    request.RequestId,
                    timeProvider.GetUtcNow()),
                cancellationToken)
            .ConfigureAwait(false);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }

    private static async Task<IResult> SelectV2TransitionTargetAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        RemoteSupportTransitionTargetSelectionRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        if (request.RequestId == Guid.Empty || request.TransitionId == Guid.Empty || request.PresenceEpoch == 0 ||
            request.InventorySequence == 0 || request.WindowsSessionId <= 0 || string.IsNullOrWhiteSpace(request.IdentityReference) ||
            request.ExpectedLifecycleRevision <= 0)
        {
            return Results.BadRequest(new { code = "transition_target_invalid" });
        }

        var authorityRegion = services.GetService<IRequiredActor<RemoteSupportSessionAuthorityRegion>>();
        if (authorityRegion is null)
        {
            return Results.NotFound();
        }

        var session = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        var authority = await authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
        var decision = await authority.Ask<RemoteSupportTransitionDecision>(
                new SelectRemoteSupportTransitionTarget(
                    session,
                    operatorBinding,
                    request.RequestId,
                    request.TransitionId,
                    request.PresenceEpoch,
                    request.InventorySequence,
                    request.WindowsSessionId,
                    request.IdentityReference,
                    request.ExpectedLifecycleRevision),
                options.AskTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return decision.Accepted
            ? Results.Accepted(V2LifecycleUri(session), decision)
            : Results.Conflict(new { code = decision.Code });
    }

    private static async Task<IResult> GetV2TransitionTargetAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2LifecycleAuthorityActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        var authorityRegion = services.GetService<IRequiredActor<RemoteSupportSessionAuthorityRegion>>();
        if (operatorBinding is null || authorityRegion is null)
        {
            return Results.Forbid();
        }

        var session = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        var authority = await authorityRegion.GetAsync(cancellationToken).ConfigureAwait(false);
        var selection = await authority.Ask<RemoteSupportTransitionSelection?>(
                new GetRemoteSupportTransitionSelectionByKey(session, operatorBinding),
                options.AskTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        return selection is null
            ? Results.NotFound()
            : Results.Ok(new RemoteSupportTransitionTargetSelectionResponse(
                selection.Fence.TransitionId,
                selection.Fence.PresenceEpoch,
                selection.Fence.LastInventorySequence,
                selection.Candidates.Select(candidate => new RemoteSupportTransitionTargetOption(
                    candidate.WindowsSessionId,
                    candidate.IdentityReference,
                    candidate.State,
                    candidate.IsConsole,
                    candidate.IsAssistable)).ToArray()));
    }

    private static async Task StreamV2LifecycleEventsAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        long? after,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2ReplicaSafeEdgeActive)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var cursor = TryGetSseCursor(http, after);
        if (cursor is null)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var session = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        var router = services.GetRequiredService<IRemoteSupportLifecycleRouter>();
        var subscription = await router.SubscribeAsync(session, operatorBinding, cursor.Value, cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (subscription.ConfigureAwait(false))
        {
            var resume = await router.ResumeAsync(new(
                    RemoteSupportV2ContractVersions.Current,
                    session,
                    operatorBinding,
                    cursor.Value,
                    Guid.NewGuid(),
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            if (resume is null)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Append("X-Accel-Buffering", "no");

            var emittedCursor = cursor.Value;
            foreach (var audit in resume.AuditEvents.OrderBy(item => item.AuditSequence))
            {
                await WriteV2LifecycleEventAsync(http.Response, audit.AuditSequence, audit.EventType, audit, cancellationToken)
                    .ConfigureAwait(false);
                emittedCursor = audit.AuditSequence;
            }

            await foreach (var lifecycleEvent in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                if (lifecycleEvent.AuditSequence <= emittedCursor)
                {
                    continue;
                }

                await WriteV2LifecycleEventAsync(
                        http.Response,
                        lifecycleEvent.AuditSequence,
                        lifecycleEvent.EventType,
                        lifecycleEvent,
                        cancellationToken)
                    .ConfigureAwait(false);
                emittedCursor = lifecycleEvent.AuditSequence;
            }
        }
    }

    private static async Task<IResult> PrepareV2MediaAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        RemoteSupportV2MediaPrepareRequest request,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            return Results.Forbid();
        }

        var session = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        var router = services.GetRequiredService<IRemoteSupportLifecycleRouter>();
        var snapshot = await router.GetAsync(session, operatorBinding, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Results.NotFound();
        }

        if (snapshot.Target != request.Target)
        {
            return Results.BadRequest("The prepared target must exactly match the lifecycle target.");
        }

        try
        {
            var prepared = await services.GetRequiredService<IRemoteSupportV2PreparationRegistry>()
                .PrepareMediaAsync(session, operatorBinding, request.Target, cancellationToken)
                .ConfigureAwait(false);
            var result = await router.PrepareMediaAsync(new PrepareRemoteSupportMedia(
                    session,
                    operatorBinding,
                    prepared,
                    request.ExpectedLifecycleRevision,
                    request.RequestId,
                    timeProvider.GetUtcNow()),
                cancellationToken).ConfigureAwait(false);
            return result.Disposition switch
            {
                RemoteSupportLifecycleTransitionDisposition.Applied or RemoteSupportLifecycleTransitionDisposition.Duplicate when result.Snapshot is not null =>
                    Results.Accepted(V2LifecycleUri(session), result.Snapshot),
                RemoteSupportLifecycleTransitionDisposition.StaleRevision when result.Snapshot is not null => Results.Conflict(result.Snapshot),
                _ => Results.Conflict(new { code = "remote_support_media_preparation_rejected" })
            };
        }
        catch (RemoteSupportV2InventoryUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (RemoteSupportV2InventoryStaleException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static async Task<IResult> SendV2NegotiationAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        RemoteSupportV2NegotiationEnvelope envelope,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive)
        {
            return Results.NotFound();
        }

        var operatorBinding = TryGetOperator(http);
        var expectedSession = new RemoteSupportSessionKey(tenantId, agentId, sessionId);
        if (operatorBinding is null || envelope.Session != expectedSession || envelope.Direction != RemoteSupportV2NegotiationDirections.Browser)
        {
            return Results.Forbid();
        }

        var router = services.GetRequiredService<IRemoteSupportLifecycleRouter>();
        var snapshot = await router.GetAsync(expectedSession, operatorBinding, cancellationToken).ConfigureAwait(false);
        if (snapshot is null)
        {
            return Results.NotFound();
        }

        var agentEnvelope = envelope.SignalType == RemoteSupportV2NegotiationSignalTypes.Offer
            ? envelope with { IceConfiguration = services.GetRequiredService<IRemoteSupportIceConfigurationProvider>().GetForSession(snapshot, envelope.Generation) }
            : envelope;
        var result = await router
            .NegotiateAsync(new RemoteSupportNegotiationIngress(agentEnvelope, operatorBinding), cancellationToken)
            .ConfigureAwait(false);
        return result.Accepted
            ? Results.Accepted(V2LifecycleUri(expectedSession), new { result.Generation })
            : Results.Conflict(new { code = result.FailureCode, result.Generation });
    }

    private static async Task StreamV2NegotiationAsync(
        int tenantId,
        Guid agentId,
        Guid sessionId,
        HttpContext http,
        NetRatelAkkaMigrationOptions options,
        IServiceProvider services,
        CancellationToken cancellationToken)
    {
        if (!options.IsRemoteSupportV2MediaActive)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var operatorBinding = TryGetOperator(http);
        if (operatorBinding is null)
        {
            http.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var subscription = await services.GetRequiredService<IRemoteSupportLifecycleRouter>()
            .SubscribeNegotiationAsync(new RemoteSupportSessionKey(tenantId, agentId, sessionId), operatorBinding, cancellationToken)
            .ConfigureAwait(false);
        if (subscription is null)
        {
            http.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await using (subscription.ConfigureAwait(false))
        {
            http.Response.StatusCode = StatusCodes.Status200OK;
            http.Response.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Append("X-Accel-Buffering", "no");
            await foreach (var envelope in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                var json = JsonSerializer.Serialize(envelope, RemoteSupportV2JsonContext.Default.RemoteSupportV2NegotiationEnvelope);
                await http.Response.WriteAsync("event: negotiation\n", cancellationToken).ConfigureAwait(false);
                await http.Response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
                await http.Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static long? TryGetSseCursor(HttpContext http, long? after)
    {
        if (after is < 0)
        {
            return null;
        }

        var requested = after;
        if (http.Request.Headers.TryGetValue("Last-Event-ID", out var lastEventId) &&
            !string.IsNullOrWhiteSpace(lastEventId))
        {
            if (!long.TryParse(lastEventId.ToString(), out var parsed) || parsed < 0)
            {
                return null;
            }

            requested = Math.Max(requested ?? 0, parsed);
        }

        return requested ?? 0;
    }

    private static async Task WriteV2LifecycleEventAsync(
        HttpResponse response,
        long cursor,
        string eventType,
        object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await response.WriteAsync($"id: {cursor}\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"event: {eventType}\n", cancellationToken).ConfigureAwait(false);
        await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
        await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static IResult GetAsync(string sessionId, NetRatelAkkaMigrationOptions options, IServiceProvider services) =>
        !options.IsLegacyRemoteSupportGatewayActive
            ? Results.NotFound()
            : services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>().Get(sessionId) is { } session
                ? Results.Ok(ToResponse(session))
                : Results.NotFound();

    private static async Task StreamSignalsAsync(string sessionId, HttpResponse response, NetRatelAkkaMigrationOptions options, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.IsLegacyRemoteSupportGatewayActive)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var sessions = services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
        if (sessions.Get(sessionId) is null)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        using var subscription = sessions.Subscribe(sessionId);
        await foreach (var signal in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var json = JsonSerializer.Serialize(signal, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await response.WriteAsync("event: signal\n", cancellationToken).ConfigureAwait(false);
            await response.WriteAsync($"data: {json}\n\n", cancellationToken).ConfigureAwait(false);
            await response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IResult> SendSignalAsync(string sessionId, RemoteSupportSignalRequest request, NetRatelAkkaMigrationOptions options, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.IsLegacyRemoteSupportGatewayActive)
        {
            return Results.NotFound();
        }

        try
        {
            var sessions = services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
            await sessions.SendBrowserSignalAsync(sessionId, request, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-remote-support/{sessionId}");
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (GatewayRemoteSupportSessionUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(exception.Message);
        }
    }

    private static async Task<IResult> CloseAsync(string sessionId, RemoteSupportCloseRequest request, NetRatelAkkaMigrationOptions options, IServiceProvider services, CancellationToken cancellationToken)
    {
        if (!options.IsLegacyRemoteSupportGatewayActive)
        {
            return Results.NotFound();
        }

        try
        {
            var sessions = services.GetRequiredService<IGatewayRemoteSupportSessionRegistry>();
            await sessions.CloseAsync(sessionId, request.Reason, cancellationToken).ConfigureAwait(false);
            return Results.Accepted($"/api/v2/gateway-remote-support/{sessionId}");
        }
        catch (KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (GatewayRemoteSupportSessionUnavailableException exception)
        {
            return Results.Problem(exception.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }

    private static GatewayRemoteSupportOpenResponse ToResponse(GatewayRemoteSupportSession session) =>
        new(session.SessionId, session.TenantId, session.AgentId, session.Authority, session.State, session.CreatedAtUtc);

    private static RemoteSupportOperatorBinding? TryGetOperator(HttpContext http)
    {
        var operatorId = http.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? http.User.FindFirstValue("sub")
            ?? http.User.Identity?.Name;
        return string.IsNullOrWhiteSpace(operatorId) ? null : new(operatorId);
    }

    private static string V2LifecycleUri(RemoteSupportSessionKey session) =>
        $"/api/v2/agents/{session.TenantId}/{session.AgentId:D}/remote-support/v2/lifecycle/sessions/{session.RemoteSupportSessionId:D}";
}

public sealed record RemoteSupportTransitionTargetSelectionRequest(
    Guid RequestId,
    Guid TransitionId,
    ulong PresenceEpoch,
    ulong InventorySequence,
    int WindowsSessionId,
    string IdentityReference,
    long ExpectedLifecycleRevision);

public sealed record RemoteSupportTransitionTargetSelectionResponse(
    Guid TransitionId,
    ulong PresenceEpoch,
    ulong InventorySequence,
    IReadOnlyList<RemoteSupportTransitionTargetOption> Candidates);

public sealed record RemoteSupportTransitionTargetOption(
    int WindowsSessionId,
    string IdentityReference,
    string State,
    bool IsConsole,
    bool IsAssistable);

public sealed record RemoteSupportV2LifecycleOpenRequest(
    Guid RequestId,
    RemoteSupportTargetDescriptor Target,
    IReadOnlyList<string> RequestedCapabilities,
    DateTimeOffset? ExpiresAtUtc = null);

public sealed record RemoteSupportV2LifecycleControlRequest(
    Guid RequestId,
    long? ExpectedLifecycleRevision = null);

public sealed record RemoteSupportV2LifecycleResumeRequest(
    Guid RequestId,
    long AfterAuditSequence = 0);

public sealed record RemoteSupportV2MediaPrepareRequest(
    Guid RequestId,
    RemoteSupportTargetDescriptor Target,
    long? ExpectedLifecycleRevision = null);
