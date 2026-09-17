using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Signed operator adapter over the existing V2 presence and preparation projections.</summary>
public static class McpOperatorRemoteSupportEndpoints
{
    private const string Tool = "netratel_remote_support_v2";

    public static IEndpointRouteBuilder MapMcpOperatorRemoteSupportEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/remote-support")
            .WithTags("MCP Operator Remote Support").RequireAuthorization("M2MOnly");
        group.MapGet("/{operation}", ReadAsync);
        group.MapPost("/refresh-inventory/preview", PreviewRefreshAsync);
        group.MapPost("/refresh-inventory/confirm", ConfirmRefreshAsync);
        return app;
    }

    private static async Task<IResult> ReadAsync(int tenantId, Guid agentId, string operation,
        HttpContext http, IHostEnvironment environment, IMcpOperatorRouteAdmission admission,
        [FromServices] IClientPresenceRouter presence,
        [FromServices] NetRatelAkkaMigrationOptions options, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        if (operation is not ("presence" or "capabilities" or "inventory")) return Results.NotFound();
        var (context, failure) = await AdmitAsync(operation, tenantId, agentId, http, environment, admission, cancellationToken);
        if (failure is not null) return failure;
        try
        {
            await admission.RecordAcceptedAsync(context!.Request, cancellationToken);
            var target = new ClientKey(tenantId, agentId);
            if (operation == "presence")
            {
                var snapshot = await presence.GetSnapshotAsync(target, cancellationToken);
                return Results.Ok(new { tenantId, agentId, status = snapshot.Status.ToString(), context.CorrelationId });
            }
            var preparation = http.RequestServices.GetService<IRemoteSupportV2PreparationRegistry>();
            var enabled = options.IsRemoteSupportV2InventoryActive;
            var now = timeProvider.GetUtcNow();
            if (operation == "capabilities")
            {
                var snapshot = enabled ? preparation?.GetCapabilities(target) : null;
                return Results.Ok(new { tenantId, agentId, enabled, hasSnapshot = snapshot is not null,
                    transportAvailable = snapshot?.IsFresh(now) == true,
                    fresh = snapshot?.IsFresh(now) == true, snapshot, context.CorrelationId });
            }
            var inventory = enabled ? preparation?.GetInventory(target) : null;
            return Results.Ok(new { tenantId, agentId, enabled, hasSnapshot = inventory is not null,
                transportAvailable = enabled && preparation?.GetCapabilities(target)?.IsFresh(now) == true,
                fresh = inventory?.IsFresh(now) == true, receivedAtUtc = inventory?.ReceivedAtUtc,
                projectionExpiresAtUtc = inventory?.ExpiresAtUtc, snapshot = inventory?.Snapshot, context.CorrelationId });
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context!); }
    }

    private static async Task<IResult> PreviewRefreshAsync(int tenantId, Guid agentId, HttpContext http,
        IHostEnvironment environment, IMcpOperatorRouteAdmission admission, IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var (context, failure) = await AdmitAsync("refresh_inventory", tenantId, agentId, http, environment, admission, cancellationToken);
        if (failure is not null) return failure;
        var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context!.Decision, PayloadHash(tenantId, agentId)), cancellationToken);
        return Results.Ok(new { plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass,
            tenantId, agentId, context.CorrelationId });
    }

    private static async Task<IResult> ConfirmRefreshAsync(int tenantId, Guid agentId, McpOperatorRemoteSupportConfirmRequest request,
        HttpContext http, IHostEnvironment environment, IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations,
        [FromServices] NetRatelAkkaMigrationOptions options, CancellationToken cancellationToken)
    {
        var (context, failure) = await AdmitAsync("refresh_inventory", tenantId, agentId, http, environment, admission, cancellationToken);
        if (failure is not null) return failure;
        if (!Opaque(request.PlanToken) || !Opaque(request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context!);
        var confirmed = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!,
            PayloadHash(tenantId, agentId), context!.Decision), cancellationToken);
        if (confirmed.FailureCode is { } code) return Failure(code, context);
        if (confirmed.IsReplay)
            return confirmed.Outcome == McpOperatorIdempotencyOutcome.Succeeded
                ? Results.Ok(new { tenantId, agentId, refreshRequested = true, replayed = true, context.CorrelationId })
                : Failure(confirmed.ResultReference ?? "remote_support_refresh_failed", context);
        if (!confirmed.IsNewDispatch || confirmed.IdempotencyId is not { } id) return Failure("confirmation_plan_invalid", context);
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken);
            var preparation = http.RequestServices.GetService<IRemoteSupportV2PreparationRegistry>();
            if (!options.IsRemoteSupportV2InventoryActive || preparation is null)
                throw new RemoteSupportV2InventoryUnavailableException(new ClientKey(tenantId, agentId));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await preparation.RequestInventoryRefreshAsync(new ClientKey(tenantId, agentId), timeout.Token);
            await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Succeeded, "refresh_requested", CancellationToken.None);
            return Results.Ok(new { tenantId, agentId, refreshRequested = true, replayed = false, context.CorrelationId });
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None);
            return Failure(rejection.FailureCode, context);
        }
        catch (RemoteSupportV2InventoryUnavailableException)
        {
            await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Failed, "remote_support_unavailable", CancellationToken.None);
            return Failure("remote_support_unavailable", context);
        }
        catch (Exception exception) when (exception is OperationCanceledException or ChannelClosedException)
        {
            // Cancellation stops the waiter; delivery may already have occurred.
            await confirmations.CompleteAsync(id, McpOperatorIdempotencyOutcome.Failed, "remote_support_refresh_outcome_unknown", CancellationToken.None);
            return Failure("remote_support_refresh_outcome_unknown", context);
        }
    }

    private static async Task<(Context? Context, IResult? Failure)> AdmitAsync(string operation, int tenantId, Guid agentId,
        HttpContext http, IHostEnvironment environment, IMcpOperatorRouteAdmission admission, CancellationToken cancellationToken)
    {
        var scope = operation == "refresh_inventory" ? "netratel.mcp.execute" : "netratel.mcp.observe";
        var fallback = new Context(null!, null!, operation, scope, http.TraceIdentifier, tenantId, agentId);
        if (!http.TryGetMcpOperatorDelegation(out var delegated) || delegated is null)
            return (null, Failure("delegated_identity_required", fallback));
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var targetEnvironment, out var instance) ||
            tenantId <= 0 || agentId == Guid.Empty || delegated.Instance != instance || delegated.Tool != Tool ||
            delegated.Operation != operation || delegated.TenantId != tenantId || delegated.AgentId != agentId ||
            string.IsNullOrWhiteSpace(delegated.Resource) || string.IsNullOrWhiteSpace(delegated.CorrelationId))
            return (null, Failure("delegated_identity_invalid", fallback));
        var identity = delegated.Identity;
        var request = new McpOperatorRouteAccessRequest(targetEnvironment,
            new McpOperatorPrincipal(identity.Subject, identity.ClientId, identity.AuthorizedParty,
                identity.Groups.ToHashSet(StringComparer.Ordinal), identity.Roles.ToHashSet(StringComparer.Ordinal),
                identity.Scopes.ToHashSet(StringComparer.Ordinal), delegated.ServicePrincipal),
            delegated.ServicePrincipal, delegated.Resource, delegated.Instance, Tool, operation, tenantId, agentId,
            new HashSet<string>([scope], StringComparer.Ordinal), delegated.CorrelationId, delegated.RequestId,
            TargetOnline: true, CapabilityAvailable: true);
        if (operation == "refresh_inventory")
        {
            var target = new ClientKey(tenantId, agentId);
            var presence = await http.RequestServices.GetRequiredService<IClientPresenceRouter>().GetSnapshotAsync(target, cancellationToken);
            var options = http.RequestServices.GetRequiredService<NetRatelAkkaMigrationOptions>();
            var capabilities = http.RequestServices.GetService<IRemoteSupportV2PreparationRegistry>()?.GetCapabilities(target);
            request = request with
            {
                TargetOnline = presence.Status == ShadowPresenceStatus.Online,
                CapabilityAvailable = options.IsRemoteSupportV2InventoryActive &&
                    capabilities?.IsFresh(http.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()) == true
            };
        }
        var evaluated = await admission.EvaluateAsync(request, cancellationToken);
        var context = new Context(request, evaluated.Decision, operation, scope, delegated.CorrelationId, tenantId, agentId);
        return evaluated.Decision.IsAllowed ? (context, null) : (null, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static IResult Failure(string code, Context context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => 401,
            "remote_support_unavailable" or "target_offline" or "capability_unavailable" => 503,
            "remote_support_refresh_outcome_unknown" => 504,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" => 409,
            _ => 403
        };
        return Results.Problem(statusCode: status, title: "Remote Support operation did not complete.",
            extensions: new Dictionary<string, object?>
            {
                ["success"] = false,
                ["failure"] = new { code, layer = status == 401 ? "delegation" : status == 403 ? "policy" : "upstream",
                    retryable = code == "remote_support_unavailable", requiredScopes = new[] { context.Scope },
                    requiredOperation = $"{Tool}/{context.Operation}",
                    remediation = code == "remote_support_refresh_outcome_unknown"
                        ? "Inspect inventory freshness before requesting another refresh; dispatch may have occurred."
                        : "Use signed operator delegation with the required scope and current target policy; inspect presence and Remote Support availability." },
                ["correlationId"] = context.CorrelationId
            });
    }

    private static bool Opaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
    private static string PayloadHash(int tenantId, Guid agentId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"remote-support-refresh:{tenantId.ToString(CultureInfo.InvariantCulture)}:{agentId:D}"))).ToLowerInvariant();
    private sealed record Context(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string Operation, string Scope, string CorrelationId, int TenantId, Guid AgentId);
}

public sealed record McpOperatorRemoteSupportConfirmRequest(string? PlanToken, string? IdempotencyKey);
