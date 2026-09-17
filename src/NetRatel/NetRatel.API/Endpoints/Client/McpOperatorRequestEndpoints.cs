using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NetRatel.API.Middleware;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;
using NetRatel.Shared.Security;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Production V2 boundary for caller-owned requests. It deliberately does
/// not invoke the unbounded ExternalService request API: every request is linked to
/// one owned job and target, contains only bounded redacted summaries, and
/// records immutable policy-backed audit evidence for each mutation.
/// </summary>
public static class McpOperatorRequestEndpoints
{
    private const string Tool = "netratel_requests";

    public static IEndpointRouteBuilder MapMcpOperatorRequestEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/requests")
            .WithTags("MCP Operator Requests")
            .RequireAuthorization("M2MOnly");
        group.MapGet("", ListAsync);
        group.MapGet("/{requestId:int}", GetAsync);
        group.MapPost("/preview/{action}", PreviewAsync);
        group.MapPost("/confirm/{action}", ConfirmAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(int tenantId, Guid agentId, string? state, long? jobId, DateTimeOffset? sinceUtc, int? limit,
        HttpContext http, IHostEnvironment environment, McpOperatorLocalAgentOptions localAgents, IMcpOperatorRouteAdmission admission,
        IMcpOperatorRequestStore requests, CancellationToken cancellationToken)
    {
        if (!IsState(state) || jobId is < 1 || limit is < 1 or > 100 || (sinceUtc.HasValue && sinceUtc.Value > DateTimeOffset.UtcNow.AddMinutes(5)))
            return Results.BadRequest(new { code = "request_query_invalid" });
        var admitted = await TryContextAsync("list", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var results = await requests.ListOwnedAsync(tenantId, agentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, state, jobId, sinceUtc, limit ?? 25, cancellationToken).ConfigureAwait(false);
            return Results.Ok(results.Select(ToSummary));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(int tenantId, Guid agentId, int requestId,
        HttpContext http, IHostEnvironment environment, McpOperatorLocalAgentOptions localAgents, IMcpOperatorRouteAdmission admission,
        IMcpOperatorRequestStore requests, CancellationToken cancellationToken)
    {
        if (requestId <= 0) return Results.BadRequest(new { code = "request_invalid" });
        var admitted = await TryContextAsync("get", tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
            var request = await GetOwnedAsync(requestId, requests, context, cancellationToken).ConfigureAwait(false);
            if (request is null) return Failure("request_not_found", context);
            http.Response.Headers.ETag = $"\"{request.Version}\"";
            return Results.Ok(ToDetails(request));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewAsync(int tenantId, Guid agentId, string action, McpOperatorRequestMutationRequest request,
        HttpContext http, IHostEnvironment environment, McpOperatorLocalAgentOptions localAgents, IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorRequestStore requests, CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        var resolved = await ResolveAsync(action, request, requests, context, cancellationToken).ConfigureAwait(false);
        if (resolved.FailureCode is { } code) return Failure(code, context);
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, MutationHash(action, request)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorRequestPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, action, Decimal(resolved.Request?.RequestId), context.Decision.Request.TargetSetDigest!, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmAsync(int tenantId, Guid agentId, string action, McpOperatorRequestMutationRequest request,
        HttpContext http, IHostEnvironment environment, McpOperatorLocalAgentOptions localAgents, IMcpOperatorRouteAdmission admission,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorRequestStore requests, CancellationToken cancellationToken)
    {
        if (!IsMutation(action)) return Results.NotFound();
        var admitted = await TryContextAsync(action, tenantId, agentId, http, environment, localAgents, admission, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!HasPlan(request.PlanToken, request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);
        var resolved = await ResolveAsync(action, request, requests, context, cancellationToken).ConfigureAwait(false);
        if (resolved.FailureCode is { } code) return Failure(code, context);
        var confirmation = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, MutationHash(action, request), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmation.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmation.IsReplay) return await ReplayAsync(confirmation, requests, context, cancellationToken).ConfigureAwait(false);
        if (!confirmation.IsNewDispatch || confirmation.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        McpOperatorAcceptedAudit audit;
        try
        {
            audit = await admission.RecordAcceptedAsync(context.Request, cancellationToken).ConfigureAwait(false);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, rejection.FailureCode, CancellationToken.None).ConfigureAwait(false);
            return Failure(rejection.FailureCode, context);
        }

        try
        {
            McpOperatorRequestLease? result = action == "create"
                ? await requests.CreateOrGetAsync(new McpOperatorRequestCreateRequest(request.JobId!.Value, request.Summary!, context.Decision, audit, idempotencyId, context.CorrelationId, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false)
                : await requests.MutateAsync(new McpOperatorRequestMutation(request.RequestId!.Value, request.ExpectedVersion!.Value, action, request.Summary, request.ResultSummary, request.ClaimReference, context.Decision, audit, DateTimeOffset.UtcNow), cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "request_not_found", CancellationToken.None).ConfigureAwait(false);
                return Failure("request_not_found", context);
            }
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, result.RequestId.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            return Results.Accepted(RequestLocation(tenantId, agentId, result.RequestId), new McpOperatorRequestMutationResult(Decimal(result.RequestId), result.State, false, context.CorrelationId));
        }
        catch (McpOperatorRequestLimitException exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, exception.Code, CancellationToken.None).ConfigureAwait(false);
            return Failure(exception.Code, context);
        }
        catch (ArgumentException)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "request_invalid", CancellationToken.None).ConfigureAwait(false);
            return Failure("request_invalid", context);
        }
        catch (Exception)
        {
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "request_mutation_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("request_mutation_failed", context);
        }
    }

    private static async Task<RequestResolution> ResolveAsync(string action, McpOperatorRequestMutationRequest request, IMcpOperatorRequestStore requests, McpOperatorRequestContext context, CancellationToken cancellationToken)
    {
        if (action == "create")
            return request.RequestId is null && request.ExpectedVersion is null && request.JobId is > 0 && IsText(request.Summary, 4096) &&
                   request.ResultSummary is null && request.ClaimReference is null
                ? RequestResolution.Valid
                : RequestResolution.Invalid;

        if (request.RequestId is not > 0 || request.ExpectedVersion is not > 0 || request.JobId is not null)
            return RequestResolution.Invalid;
        var existing = await GetOwnedAsync(request.RequestId.Value, requests, context, cancellationToken).ConfigureAwait(false);
        if (existing is null) return RequestResolution.NotFound;
        var valid = action switch
        {
            "update" => IsText(request.Summary, 4096) && request.ResultSummary is null && request.ClaimReference is null,
            "claim" => request.Summary is null && request.ResultSummary is null && IsText(request.ClaimReference, 128),
            "complete" or "fail" => request.Summary is null && request.ClaimReference is null && IsText(request.ResultSummary, 48 * 1024),
            "cancel" => request.Summary is null && request.ClaimReference is null && (request.ResultSummary is null || IsText(request.ResultSummary, 48 * 1024)),
            _ => false
        };
        return valid ? new(existing, null) : RequestResolution.Invalid;
    }

    private static async Task<IResult> ReplayAsync(McpOperatorConfirmationAdmission confirmation, IMcpOperatorRequestStore requests, McpOperatorRequestContext context, CancellationToken cancellationToken)
    {
        if (confirmation.Outcome == McpOperatorIdempotencyOutcome.Pending) return Failure("idempotency_pending", context);
        if (confirmation.Outcome != McpOperatorIdempotencyOutcome.Succeeded || !int.TryParse(confirmation.ResultReference, out var requestId)) return Failure("idempotency_replay_unavailable", context);
        var request = await GetOwnedAsync(requestId, requests, context, cancellationToken).ConfigureAwait(false);
        return request is null ? Failure("idempotency_replay_unavailable", context) : Results.Ok(new McpOperatorRequestMutationResult(Decimal(request.RequestId), request.State, true, context.CorrelationId));
    }

    private static Task<McpOperatorRequestLease?> GetOwnedAsync(int requestId, IMcpOperatorRequestStore requests, McpOperatorRequestContext context, CancellationToken cancellationToken) =>
        requests.GetOwnedAsync(requestId, context.TenantId, context.AgentId, context.Request.Principal, context.Request.McpResource!, context.Request.McpInstance!, cancellationToken);

    private static async Task<McpOperatorRequestContextResult> TryContextAsync(string operation, int tenantId, Guid agentId, HttpContext http, IHostEnvironment environment,
        McpOperatorLocalAgentOptions localAgents, IMcpOperatorRouteAdmission admission, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(operation, tenantId, agentId, http.TraceIdentifier);
        var delegated = http.TryGetMcpOperatorDelegation(out var assertion) && assertion is not null;
        if (!delegated && !McpOperatorLocalAgentDelegation.TryCreate(http, environment, localAgents, Tool, operation, tenantId, agentId, out assertion))
            return new(null, Failure(environment.IsProduction() && localAgents.Enabled ? "local_operator_identity_not_allowed" : "delegated_identity_required", fallback));
        var effective = assertion!;
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) || !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId != tenantId || effective.AgentId != agentId || string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));
        var route = new McpOperatorRouteAccessRequest(operatorEnvironment,
            new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
                effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal)),
            effective.ServicePrincipal, effective.Resource!, effective.Instance!, Tool, operation, tenantId, agentId,
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal), effective.CorrelationId!, effective.RequestId, true, true);
        var evaluated = await admission.EvaluateAsync(route, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorRequestContext(route, evaluated.Decision, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, tenantId, agentId, effective.CorrelationId!);
        return evaluated.Decision.IsAllowed ? new(context, null) : new(context, Failure(evaluated.Decision.FailureCode ?? "target_policy_missing", context));
    }

    private static string MutationHash(string action, McpOperatorRequestMutationRequest request) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { action, request.RequestId, request.JobId, request.ExpectedVersion, request.Summary, request.ResultSummary, request.ClaimReference }))));
    private static bool IsMutation(string action) => action is "create" or "update" or "claim" or "complete" or "fail" or "cancel";
    private static bool IsState(string? state) => state is null or "Pending" or "Claimed" or "Completed" or "Failed" or "Cancelled";
    private static bool IsText(string? value, int maximumBytes) => value is { Length: > 0 } && Encoding.UTF8.GetByteCount(value) <= maximumBytes && !value.Contains('\0');
    private static bool HasPlan(string? plan, string? key) => IsOpaque(plan) && IsOpaque(key);
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static string Decimal(long? value) => value?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    private static string Decimal(long value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string Decimal(int value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    private static string RequestLocation(int tenantId, Guid agentId, int requestId) => $"/api/v2/mcp/operator/agents/{tenantId}/{agentId:D}/requests/{requestId}";
    private static McpOperatorRequestContext MinimalContext(string operation, int tenantId, Guid agentId, string correlationId) => new(null!, null!, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope), operation, tenantId, agentId, correlationId);
    private static McpOperatorRequestSummary ToSummary(McpOperatorRequestLease request) => new(Decimal(request.RequestId), Decimal(request.JobId), request.TenantId, request.AgentId, request.State, request.Summary, request.CreatedAtUtc, request.UpdatedAtUtc, request.CompletedAtUtc, request.CorrelationId, Decimal(request.Version));
    private static McpOperatorRequestDetails ToDetails(McpOperatorRequestLease request) => new(ToSummary(request), request.ResultSummary is null ? null : OperatorOutputRedactor.Redact(request.ResultSummary), request.ClaimReferenceHash, request.PolicyId, Decimal(request.PolicyVersion), request.TargetSetDigest);

    private static IResult Failure(string code, McpOperatorRequestContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "request_not_found" => StatusCodes.Status404NotFound,
            "request_invalid" or "request_query_invalid" => StatusCodes.Status400BadRequest,
            "request_terminal_conflict" or "request_version_conflict" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "oauth_scope_missing" => "oauth_scope",
            "tenant_not_authorized" => "tenant",
            "request_invalid" or "request_query_invalid" or "request_terminal_conflict" or "request_version_conflict" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator request access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator request access was not admitted.",
            ["failure"] = new { code, layer, retryable = false, requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{Tool}/{context.Operation}", target = code == "tenant_not_authorized" ? null : new { context.TenantId, context.AgentId }, remediation = "Review request ownership, the owned job relation, ETag version, active target policy, confirmation credentials, and idempotency key." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorRequestContext(McpOperatorRouteAccessRequest Request, McpOperatorDecision Decision, string RequiredScope, string Operation, int TenantId, Guid AgentId, string CorrelationId);
    private sealed record McpOperatorRequestContextResult(McpOperatorRequestContext? Context, IResult? Failure);
    private sealed record RequestResolution(McpOperatorRequestLease? Request, string? FailureCode)
    {
        public static RequestResolution Valid { get; } = new(null, null);
        public static RequestResolution Invalid { get; } = new(null, "request_invalid");
        public static RequestResolution NotFound { get; } = new(null, "request_not_found");
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
public sealed record McpOperatorRequestMutationRequest(int? RequestId = null, long? JobId = null, long? ExpectedVersion = null, string? Summary = null, string? ResultSummary = null, string? ClaimReference = null, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorRequestPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string Action, string RequestId, string TargetSetDigest, string CorrelationId);
public sealed record McpOperatorRequestMutationResult(string RequestId, string State, bool Replayed, string CorrelationId);
public sealed record McpOperatorRequestSummary(string RequestId, string JobId, int TenantId, Guid AgentId, string State, string Summary, DateTimeOffset CreatedAtUtc, DateTimeOffset UpdatedAtUtc, DateTimeOffset? CompletedAtUtc, string CorrelationId, string Version);
public sealed record McpOperatorRequestDetails(McpOperatorRequestSummary Request, string? ResultSummary, string? ClaimReferenceHash, Guid PolicyId, string PolicyVersion, string TargetSetDigest);
