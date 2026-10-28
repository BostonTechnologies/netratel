using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.ComputerUse;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.ComputerUse;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Bounded, owner-scoped desktop API over the enrolled agent gateway.</summary>
public static class McpOperatorComputerUseEndpoints
{
    public static IEndpointRouteBuilder MapMcpOperatorComputerUseEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/computer-use")
            .WithTags("MCP Operator Computer Use").RequireAuthorization("M2MOnly")
            .WithMetadata(new RequestSizeLimitAttribute(ComputerUseContract.MaximumRequestBytes));
        group.MapPost("/capabilities", (int tenantId, Guid agentId, ComputerUseTargetRequest request, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.ReadTargetAsync(tenantId, agentId, "capabilities", request, http, ct));
        group.MapPost("/windows", (int tenantId, Guid agentId, ComputerUseTargetRequest request, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.ReadTargetAsync(tenantId, agentId, "list_windows", request, http, ct));
        group.MapPost("/sessions/preview", (int tenantId, Guid agentId, ComputerUseOpenRequest request, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.PreviewOpenAsync(tenantId, agentId, request, http, ct));
        group.MapPost("/sessions/confirm", (int tenantId, Guid agentId, ComputerUseOpenConfirmRequest request, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.OpenAsync(tenantId, agentId, request, http, ct));
        group.MapPost("/sessions/view", (int tenantId, Guid agentId, ComputerUseOpenViewRequest request, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.OpenViewAsync(tenantId, agentId, request, http, ct));
        group.MapGet("/sessions/{sessionId:guid}", (int tenantId, Guid agentId, Guid sessionId, HttpContext http,
            ComputerUseOperatorService service, CancellationToken ct) => service.GetAsync(tenantId, agentId, sessionId, http, ct));
        group.MapPost("/sessions/{sessionId:guid}/observe", (int tenantId, Guid agentId, Guid sessionId, ComputerUseObserveRequest request,
            HttpContext http, ComputerUseOperatorService service, CancellationToken ct) => service.ObserveAsync(tenantId, agentId, sessionId, request, http, ct));
        group.MapPost("/sessions/{sessionId:guid}/actions/preview", (int tenantId, Guid agentId, Guid sessionId, ComputerUseActRequest request,
            HttpContext http, ComputerUseOperatorService service, CancellationToken ct) => service.PreviewActAsync(tenantId, agentId, sessionId, request, http, ct));
        group.MapPost("/sessions/{sessionId:guid}/actions/confirm", (int tenantId, Guid agentId, Guid sessionId, ComputerUseActConfirmRequest request,
            HttpContext http, ComputerUseOperatorService service, CancellationToken ct) => service.ActAsync(tenantId, agentId, sessionId, request, http, ct));
        foreach (var operation in new[] { "pause", "resume", "close", "close_view" })
        {
            var route = operation == "close_view" ? "close-view" : operation;
            group.MapPost($"/sessions/{{sessionId:guid}}/{route}", (int tenantId, Guid agentId, Guid sessionId, ComputerUseControlRequest request,
                HttpContext http, ComputerUseOperatorService service, CancellationToken ct) => service.ControlAsync(tenantId, agentId, sessionId, operation, request, http, ct));
        }
        return app;
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseTargetRequest(ComputerUseTarget Target, string Provider);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ComputerUseOpenRequest(ComputerUseTarget Target, string Provider, int LifetimeSeconds = 300, int IdleTimeoutSeconds = 60);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseOpenConfirmRequest(ComputerUseTarget Target, string Provider, string PlanToken, string IdempotencyKey,
    int LifetimeSeconds = 300, int IdleTimeoutSeconds = 60) : ComputerUseOpenRequest(Target, Provider, LifetimeSeconds, IdleTimeoutSeconds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseOpenViewRequest(ComputerUseTarget Target, string Provider, string IdempotencyKey,
    int LifetimeSeconds = 300, int IdleTimeoutSeconds = 60) : ComputerUseOpenRequest(Target, Provider, LifetimeSeconds, IdleTimeoutSeconds);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseObserveRequest(bool IncludeScreenshot = false);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public record ComputerUseActRequest(string ObservationId, ComputerUseAction Action);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseActConfirmRequest(string ObservationId, ComputerUseAction Action, string PlanToken, string IdempotencyKey)
    : ComputerUseActRequest(ObservationId, Action);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ComputerUseControlRequest(string IdempotencyKey);

public sealed class ComputerUseOperatorService(
    IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations, IComputerUseSessionStore sessions,
    IComputerUseGateway gateway, IClientPresenceRouter presence, IAgentControlSessionRegistry controls,
    IRemoteSupportV2PreparationRegistry preparation, IHostEnvironment environment, TimeProvider time,
    IOptionsMonitor<ComputerUseOptions> options)
{
    private const string Tool = "netratel_computer_use";

    public async Task<IResult> ReadTargetAsync(int tenantId, Guid agentId, string operation, ComputerUseTargetRequest request,
        HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, operation, http, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        if (ValidateTarget(request.Target, request.Provider, tenantId, agentId, false) is { } targetFailure)
            return await RefuseAsync(targetFailure, context, null, ct);
        var audit = await AcceptAsync(context, ct);
        if (audit.Failure is not null) return audit.Failure;
        var command = new ComputerUseCommand(ComputerUseContract.Version, Guid.NewGuid(), Guid.Empty,
            operation, request.Target, request.Provider, false, time.GetUtcNow().AddSeconds(30));
        await sessions.RecordAuditAsync(Audit(context, null, command.OperationId, "admitted", null, audit.Id, Hash(request)), ct);
        var result = await gateway.ExecuteAsync(new(tenantId, agentId), context.ConnectionId, context.ConnectionEpoch, command, ct);
        await sessions.RecordAuditAsync(Audit(context, null, command.OperationId, result.Status, result.Code, audit.Id, Hash(request)), CancellationToken.None);
        return Result(result, null, context.CorrelationId, audit.Id);
    }

    public async Task<IResult> PreviewOpenAsync(int tenantId, Guid agentId, ComputerUseOpenRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "open", http, ct, "preview_open");
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        if (ValidateOpen(request, tenantId, agentId) is { } failure) return await RefuseAsync(failure, context, null, ct);
        var plan = await confirmations.CreatePlanAsync(new(context.Decision, OpenHash(request)), ct);
        return Results.Ok(new { plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass,
            request.Target, request.Provider, request.LifetimeSeconds, request.IdleTimeoutSeconds, context.CorrelationId });
    }

    public async Task<IResult> OpenAsync(int tenantId, Guid agentId, ComputerUseOpenConfirmRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "open", http, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        if (ValidateOpen(request, tenantId, agentId) is { } failure) return await RefuseAsync(failure, context, null, ct);
        if (!Opaque(request.PlanToken) || !Opaque(request.IdempotencyKey)) return await RefuseAsync("confirmation_plan_invalid", context, null, ct);
        var confirmed = await confirmations.ConfirmAsync(new(request.PlanToken, request.IdempotencyKey, OpenHash(request), context.Decision), ct);
        if (confirmed.FailureCode is { } code) return await RefuseAsync(code, context, null, ct);
        if (confirmed.IdempotencyId is not { } id || !confirmed.IsNewDispatch && !confirmed.IsReplay)
            return await RefuseAsync("confirmation_plan_invalid", context, null, ct);
        return await OpenCoreAsync(context, request, id, true, confirmed.IsNewDispatch, ct, confirmed.IsReplay);
    }

    public async Task<IResult> OpenViewAsync(int tenantId, Guid agentId, ComputerUseOpenViewRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "open_view", http, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        if (ValidateOpen(request, tenantId, agentId) is { } failure) return await RefuseAsync(failure, context, null, ct);
        if (!Opaque(request.IdempotencyKey)) return await RefuseAsync("idempotency_key_invalid", context, null, ct);
        return await OpenCoreAsync(context, request, OperationId(context, Guid.Empty, request.IdempotencyKey), false, false, ct);
    }

    private async Task<IResult> OpenCoreAsync(Context context, ComputerUseOpenRequest request, Guid id, bool control, bool confirmed, CancellationToken ct, bool replayOnly = false)
    {
        var audit = await AcceptAsync(context, ct);
        if (audit.Failure is not null) return audit.Failure;
        var started = await sessions.CreateAsync(new(id, id, context.TenantId, context.AgentId, context.ConnectionId, context.ConnectionEpoch,
            context.Owner, request.Target, request.Provider, control, request.LifetimeSeconds, request.IdleTimeoutSeconds,
            OpenHash(request), audit.Id, context.CorrelationId, time.GetUtcNow(), ReplayOnly: replayOnly), ct);
        return await FinishAsync(context, started, id, ComputerUseOperations.Open, null, null, false, audit.Id, confirmed, ct);
    }

    public async Task<IResult> GetAsync(int tenantId, Guid agentId, Guid sessionId, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "get", http, ct, requireOnline: false);
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        var lease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Owner, ct);
        if (lease is null) return await RefuseAsync("computer_use_session_not_found", context, sessionId, ct);
        var audit = await AcceptAsync(context, ct);
        if (audit.Failure is not null) return audit.Failure;
        return Results.Ok(new { session = PublicSession(lease), context.CorrelationId, auditId = audit.Id });
    }

    public async Task<IResult> ObserveAsync(int tenantId, Guid agentId, Guid sessionId, ComputerUseObserveRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "observe", http, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        return await DispatchOwnedAsync(resolved.Context!, sessionId, Guid.NewGuid(), Hash(request), null, null, request.IncludeScreenshot, false, ct);
    }

    public async Task<IResult> PreviewActAsync(int tenantId, Guid agentId, Guid sessionId, ComputerUseActRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "act", http, ct, "preview_act");
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        var lease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Owner, ct);
        if (ValidateAct(lease, request, context) is { } failure) return await RefuseAsync(failure, context, sessionId, ct);
        var plan = await confirmations.CreatePlanAsync(new(context.Decision, ActHash(sessionId, request)), ct);
        return Results.Ok(new { plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, sessionId,
            request.ObservationId, actionKind = request.Action.Kind, context.CorrelationId });
    }

    public async Task<IResult> ActAsync(int tenantId, Guid agentId, Guid sessionId, ComputerUseActConfirmRequest request, HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, "act", http, ct);
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        var lease = await sessions.GetOwnedAsync(sessionId, tenantId, agentId, context.Owner, ct);
        if (ValidateAct(lease, request, context) is { } failure) return await RefuseAsync(failure, context, sessionId, ct);
        if (!Opaque(request.PlanToken) || !Opaque(request.IdempotencyKey)) return await RefuseAsync("confirmation_plan_invalid", context, sessionId, ct);
        var confirmed = await confirmations.ConfirmAsync(new(request.PlanToken, request.IdempotencyKey, ActHash(sessionId, request), context.Decision), ct);
        if (confirmed.FailureCode is { } code) return await RefuseAsync(code, context, sessionId, ct);
        if (confirmed.IdempotencyId is not { } id || !confirmed.IsNewDispatch && !confirmed.IsReplay)
            return await RefuseAsync("confirmation_plan_invalid", context, sessionId, ct);
        return await DispatchOwnedAsync(context, sessionId, id, ActHash(sessionId, request), request.ObservationId, request.Action, false,
            confirmed.IsNewDispatch, ct, confirmed.IsReplay);
    }

    public async Task<IResult> ControlAsync(int tenantId, Guid agentId, Guid sessionId, string operation, ComputerUseControlRequest request,
        HttpContext http, CancellationToken ct)
    {
        var resolved = await ContextAsync(tenantId, agentId, operation, http, ct,
            requireOnline: operation is not ("close" or "close_view" or "pause"));
        if (resolved.Failure is not null) return resolved.Failure;
        var context = resolved.Context!;
        if (!Opaque(request.IdempotencyKey)) return await RefuseAsync("idempotency_key_invalid", context, sessionId, ct);
        return await DispatchOwnedAsync(context, sessionId, OperationId(context, sessionId, request.IdempotencyKey),
            Hash(new { sessionId, operation }), null, null, false, false, ct);
    }

    private async Task<IResult> DispatchOwnedAsync(Context context, Guid sessionId, Guid id, string hash,
        string? observationId, ComputerUseAction? action, bool screenshot, bool confirmed, CancellationToken ct, bool replayOnly = false)
    {
        var lease = await sessions.GetOwnedAsync(sessionId, context.TenantId, context.AgentId, context.Owner, ct);
        if (lease is null) return await RefuseAsync("computer_use_session_not_found", context, sessionId, ct);
        if (context.Operation is not ("pause" or "close" or "close_view") &&
            (lease.ConnectionId != context.ConnectionId || lease.ConnectionEpoch != context.ConnectionEpoch))
            return await RefuseAsync("computer_use_presence_changed", context, sessionId, ct);
        if (context.Operation is "observe" or "act" or "resume" &&
            ValidateTarget(lease.Target, lease.Provider, context.TenantId, context.AgentId, true) is { } targetFailure)
            return await RefuseAsync(targetFailure, context, sessionId, ct);
        var audit = await AcceptAsync(context, ct);
        if (audit.Failure is not null) return audit.Failure;
        var started = await sessions.BeginAsync(new(sessionId, id, context.TenantId, context.AgentId, context.Owner,
            context.Operation, hash, audit.Id, context.CorrelationId, time.GetUtcNow(), ReplayOnly: replayOnly), ct);
        return await FinishAsync(context, started, id, context.Operation == "close_view" ? "close" : context.Operation,
            observationId, action, screenshot, audit.Id, confirmed, ct);
    }

    private async Task<IResult> FinishAsync(Context context, ComputerUseOperationAdmission started, Guid id, string operation,
        string? observationId, ComputerUseAction? action, bool screenshot, Guid? auditId, bool confirmed, CancellationToken ct)
    {
        if (started.FailureCode is { } failure)
        {
            if (confirmed && failure != "computer_use_operation_pending")
                await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Failed, failure, CancellationToken.None);
            return await RefuseAsync(failure, context, started.Session?.SessionId, ct);
        }
        if (!started.Dispatch && started.PreviousResult is { } replay)
            return Result(replay, started.Session, context.CorrelationId, auditId, true);
        var lease = started.Session!;
        var command = Command(lease, id, operation, observationId, action, screenshot);
        ComputerUseResult result;
        try { result = await gateway.ExecuteAsync(new(lease.TenantId, lease.AgentId), lease.ConnectionId, lease.ConnectionEpoch, command, ct); }
        catch (OperationCanceledException)
        {
            result = new(ComputerUseContract.Version, id, lease.SessionId, ComputerUseStatuses.Unknown, "execution_interrupted",
                Provider: lease.Provider, MayHaveApplied: operation is "open" or "act" or "resume");
        }
        await sessions.CompleteAsync(id, result, time.GetUtcNow(), CancellationToken.None);
        if (confirmed)
            await confirmations.CompleteAsync(id, IsCompleted(result) ? McpOperatorIdempotencyOutcome.Succeeded : McpOperatorIdempotencyOutcome.Failed,
                IsCompleted(result) ? lease.SessionId.ToString("D") : result.Code ?? result.Status, CancellationToken.None);
        var updated = await sessions.GetOwnedAsync(lease.SessionId, lease.TenantId, lease.AgentId, lease.Owner, CancellationToken.None);
        return Result(result, updated ?? lease, context.CorrelationId, auditId);
    }

    private async Task<(Context? Context, IResult? Failure)> ContextAsync(int tenantId, Guid agentId, string operation,
        HttpContext http, CancellationToken ct, string? delegatedOperation = null, bool requireOnline = true)
    {
        if (!http.TryGetMcpOperatorDelegation(out var delegated) || delegated is null)
        {
            await sessions.RecordAuditAsync(new(Guid.NewGuid(), tenantId, agentId, null, null,
                new("unattributed", string.Empty, "unattributed", string.Empty, string.Empty), operation,
                "refused", "delegated_identity_required", http.TraceIdentifier, null, null, time.GetUtcNow()), ct);
            return (null, Failure("delegated_identity_required", http.TraceIdentifier, operation));
        }
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var targetEnvironment, out var instance) ||
            tenantId <= 0 || agentId == Guid.Empty || delegated.Instance != instance || delegated.Tool != Tool ||
            delegated.Operation != (delegatedOperation ?? operation) || delegated.TenantId != tenantId || delegated.AgentId != agentId ||
            access is null || string.IsNullOrWhiteSpace(delegated.Resource) || string.IsNullOrWhiteSpace(delegated.CorrelationId))
        {
            await sessions.RecordAuditAsync(new(Guid.NewGuid(), tenantId, agentId, null, null,
                new(delegated.Identity.Subject, delegated.Identity.ClientId ?? delegated.Identity.Subject,
                    delegated.ServicePrincipal, delegated.Resource ?? string.Empty,
                    delegated.Instance is { Length: <= 32 } auditInstance ? auditInstance : "invalid_instance"),
                operation, "refused", "delegated_identity_invalid", http.TraceIdentifier, null, null, time.GetUtcNow()), ct);
            return (null, Failure("delegated_identity_invalid", http.TraceIdentifier, operation));
        }
        var identity = delegated.Identity;
        var owner = new ComputerUseOwner(identity.Subject, identity.ClientId ?? identity.AuthorizedParty ?? identity.Subject,
            delegated.ServicePrincipal, delegated.Resource, delegated.Instance);
        var client = new ClientKey(tenantId, agentId);
        var snapshot = await presence.GetSnapshotAsync(client, ct);
        var available = controls.GetAvailability(client);
        var request = new McpOperatorRouteAccessRequest(targetEnvironment,
            new(identity.Subject, identity.ClientId, identity.AuthorizedParty, identity.Groups.ToHashSet(StringComparer.Ordinal),
                identity.Roles.ToHashSet(StringComparer.Ordinal), identity.Scopes.ToHashSet(StringComparer.Ordinal), delegated.ServicePrincipal),
            delegated.ServicePrincipal, delegated.Resource, delegated.Instance, Tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            delegated.CorrelationId, delegated.RequestId,
            TargetOnline: !requireOnline || snapshot.Status == ClientPresenceStatus.Online,
            CapabilityAvailable: !requireOnline || available is not null && available.ConnectionId == snapshot.ConnectionId &&
                available.ConnectionEpoch == (ulong?)snapshot.ConnectionEpoch && available.Capabilities.Contains(ComputerUseContract.Capability, StringComparer.Ordinal));
        var evaluated = await admission.EvaluateAsync(request, ct);
        var context = new Context(request, evaluated.Decision, owner, snapshot.ConnectionId ?? Guid.Empty,
            snapshot.ConnectionEpoch > 0 ? checked((ulong)snapshot.ConnectionEpoch.Value) : 0);
        if (!evaluated.Decision.IsAllowed)
            return (null, await RefuseAsync(evaluated.Decision.FailureCode ?? "target_policy_missing", context, null, ct));
        if (!options.CurrentValue.Enabled && operation is not ("get" or "pause" or "close" or "close_view"))
            return (null, await RefuseAsync("computer_use_disabled", context, null, ct));
        return (context, null);
    }

    private async Task<(Guid? Id, IResult? Failure)> AcceptAsync(Context context, CancellationToken ct)
    {
        if (!options.CurrentValue.Enabled && context.Operation is not ("get" or "pause" or "close" or "close_view"))
            return (null, await RefuseAsync("computer_use_disabled", context, null, ct));
        try { return ((await admission.RecordAcceptedAsync(context.Request, ct)).AuditId, null); }
        catch (McpOperatorAdmissionRejectedException rejection) { return (null, await RefuseAsync(rejection.FailureCode, context, null, ct)); }
    }

    private string? ValidateOpen(ComputerUseOpenRequest request, int tenantId, Guid agentId) =>
        request.LifetimeSeconds is < 30 or > 300 || request.IdleTimeoutSeconds is < 15 or > 120 || request.IdleTimeoutSeconds > request.LifetimeSeconds
            ? "computer_use_lease_limits_invalid" : ValidateTarget(request.Target, request.Provider, tenantId, agentId, true);

    private string? ValidateTarget(ComputerUseTarget? target, string provider, int tenantId, Guid agentId, bool window)
    {
        if (target is null || !target.IsSessionTarget || window && !target.IsWindowTarget || !ComputerUseProviders.IsKnown(provider))
            return "computer_use_target_invalid";
        var inventory = preparation.GetInventory(new(tenantId, agentId));
        if (inventory is null || !inventory.IsFresh(time.GetUtcNow()) || inventory.Snapshot.ExpiresAtUtc <= time.GetUtcNow())
            return "computer_use_inventory_stale";
        var selected = inventory.Snapshot.Entries.SingleOrDefault(entry => entry.WindowsSessionId == target.WindowsSessionId && entry.UserSidHash == target.UserSidHash);
        return selected is { IsConnected: true, IsLocked: false, IsWinlogon: false }
            ? null : "computer_use_session_unavailable";
    }

    private string? ValidateAct(ComputerUseSessionLease? lease, ComputerUseActRequest request, Context context)
    {
        if (lease is null) return "computer_use_session_not_found";
        if (!lease.ControlAllowed) return "computer_use_control_not_allowed";
        if (lease.State != "active") return "computer_use_session_paused";
        if (lease.IdleExpiresAtUtc <= time.GetUtcNow() || lease.ExpiresAtUtc <= time.GetUtcNow()) return "computer_use_lease_expired";
        if (lease.ConnectionId != context.ConnectionId || lease.ConnectionEpoch != context.ConnectionEpoch) return "computer_use_presence_changed";
        if (string.IsNullOrWhiteSpace(request.ObservationId) || request.ObservationId.Length > 256) return "observation_required";
        return ComputerUseValidation.ValidateAction(request.Action) ?? ValidateTarget(lease.Target, lease.Provider, lease.TenantId, lease.AgentId, true);
    }

    private async Task<IResult> RefuseAsync(string code, Context context, Guid? sessionId, CancellationToken ct)
    {
        await sessions.RecordAuditAsync(Audit(context, sessionId, null, "refused", code, null, null), ct);
        return Failure(code, context.CorrelationId, context.Operation);
    }

    private ComputerUseAudit Audit(Context context, Guid? sessionId, Guid? operationId, string outcome, string? code, Guid? acceptedId, string? hash) =>
        new(Guid.NewGuid(), context.TenantId, context.AgentId, sessionId, operationId, context.Owner,
            context.Operation, outcome, code, context.CorrelationId, acceptedId, hash, time.GetUtcNow());

    internal static ComputerUseCommand Command(ComputerUseSessionLease lease, Guid id, string operation,
        string? observationId = null, ComputerUseAction? action = null, bool screenshot = false) =>
        new(ComputerUseContract.Version, id, lease.SessionId, operation, lease.Target, lease.Provider,
            lease.ControlAllowed, lease.IdleExpiresAtUtc < lease.ExpiresAtUtc ? lease.IdleExpiresAtUtc : lease.ExpiresAtUtc,
            observationId, action, screenshot, lease.Revision);

    internal static object PublicSession(ComputerUseSessionLease lease) => new
    {
        lease.SessionId, lease.TenantId, lease.AgentId, lease.Target, lease.Provider, lease.ControlAllowed, lease.State,
        lease.CreatedAtUtc, lease.IdleExpiresAtUtc, lease.ExpiresAtUtc, lease.Revision
    };
    internal static IResult Result(ComputerUseResult result, ComputerUseSessionLease? lease, string correlationId, Guid? auditId, bool replayed = false) =>
        Results.Ok(new { session = lease is null ? null : PublicSession(lease), result, correlationId, auditId, replayed });
    private static IResult Failure(string code, string correlationId, string operation) => Results.Problem(
        statusCode: code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => 401,
            "computer_use_session_not_found" or "target_not_found" => 404,
            "computer_use_target_invalid" or "computer_use_lease_limits_invalid" or "idempotency_key_invalid" or "observation_required" or
                "unsupported_action" or "action_out_of_bounds" or "unsupported_key" or "invalid_action" or "element_required" or
                "coordinates_required" or "text_required" or "scroll_axis_required" => 400,
            "target_offline" or "capability_unavailable" or "computer_use_disabled" => 503,
            var value when value.StartsWith("computer_use_", StringComparison.Ordinal) || value.StartsWith("confirmation_", StringComparison.Ordinal) || value.StartsWith("idempotency_", StringComparison.Ordinal) => 409,
            _ => 403
        }, title: "Computer use operation was refused.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false, ["failure"] = new { code, layer = "computer_use", retryable = false,
                requiredOperation = $"{Tool}/{operation}", requiredScopes = new[] { McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope) } },
            ["correlationId"] = correlationId
        });
    private static bool IsCompleted(ComputerUseResult result) => result.Status is ComputerUseStatuses.Completed or ComputerUseStatuses.Observed or ComputerUseStatuses.Verified or ComputerUseStatuses.AppliedUnverified;
    private static bool Opaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string Hash<T>(T value) => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(value, ComputerUseContract.JsonOptions)));
    private static string OpenHash(ComputerUseOpenRequest request) => Hash(new ComputerUseOpenRequest(request.Target, request.Provider, request.LifetimeSeconds, request.IdleTimeoutSeconds));
    private static string ActHash(Guid sessionId, ComputerUseActRequest request) => Hash(new { sessionId, request.ObservationId, request.Action });
    private static Guid OperationId(Context context, Guid sessionId, string key) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { context.Owner, context.TenantId, context.AgentId, context.Operation, sessionId, key }, ComputerUseContract.JsonOptions))).AsSpan(0, 16));
    private sealed record Context(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, ComputerUseOwner Owner, Guid ConnectionId, ulong ConnectionEpoch)
    {
        public int TenantId => Request.TenantId;
        public Guid AgentId => Request.AgentId;
        public string Operation => Request.Operation;
        public string CorrelationId => Request.CorrelationId;
    }
}
