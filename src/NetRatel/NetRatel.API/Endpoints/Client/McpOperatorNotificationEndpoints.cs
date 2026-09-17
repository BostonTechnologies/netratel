using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Middleware;
using NetRatel.Application.Notifications;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Delegated-operator notification access. Notification state is deliberately
/// keyed to the signed human subject, never to the M2M service principal that
/// transports the request. The control-plane policy selector prevents an
/// agent-target policy from granting global notification authority.
/// </summary>
public static class McpOperatorNotificationEndpoints
{
    private const string Tool = "netratel_notifications";
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private const int MaximumMarkReadIds = 200;
    private static readonly string ControlPlaneDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("netratel-mcp-notifications-control-plane-v1")));

    public static IEndpointRouteBuilder MapMcpOperatorNotificationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/notifications")
            .WithTags("MCP Operator Notifications")
            .RequireAuthorization("M2MOnly");

        group.MapGet(string.Empty, ListAsync);
        group.MapGet("/{id:guid}", GetAsync);
        group.MapGet("/summary", SummaryAsync);
        group.MapGet("/unread-errors", UnreadErrorsAsync);
        group.MapPost("/preview/mark-read", PreviewMarkReadAsync);
        group.MapPost("/confirm/mark-read", ConfirmMarkReadAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int? page,
        int? pageSize,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] INetRatelNotificationService notifications,
        CancellationToken cancellationToken)
    {
        if (page is < 1 || pageSize is < 1 or > MaximumPageSize)
            return Failure("notification_query_invalid", MinimalContext("list", http.TraceIdentifier));
        var admitted = await TryContextAsync("list", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var result = await notifications.GetPageAsync(context.Subject, page ?? 1, pageSize ?? DefaultPageSize,
                null, null, null, null, null, null, null, null, null, cancellationToken).ConfigureAwait(false);
            return Results.Ok(result);
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] INetRatelNotificationService notifications,
        CancellationToken cancellationToken)
    {
        if (id == Guid.Empty) return Failure("notification_invalid", MinimalContext("get", http.TraceIdentifier));
        var admitted = await TryContextAsync("get", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var result = await notifications.GetByIdAsync(id, context.Subject, cancellationToken).ConfigureAwait(false);
            return result is null ? Failure("notification_not_found", context) : Results.Ok(result);
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> SummaryAsync(
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] INetRatelNotificationService notifications,
        CancellationToken cancellationToken)
    {
        var admitted = await TryContextAsync("summary", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await notifications.GetSummaryAsync(context.Subject, cancellationToken).ConfigureAwait(false));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> UnreadErrorsAsync(
        int? take,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] INetRatelNotificationService notifications,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > MaximumPageSize)
            return Failure("notification_query_invalid", MinimalContext("unread_errors", http.TraceIdentifier));
        var admitted = await TryContextAsync("unread_errors", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await notifications.GetUnreadErrorsAsync(context.Subject, take ?? DefaultPageSize, cancellationToken).ConfigureAwait(false));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewMarkReadAsync(
        McpOperatorNotificationMarkReadRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        if (!HasValidIds(request.Ids)) return Failure("notification_invalid", MinimalContext("mark_read", http.TraceIdentifier));
        var admitted = await TryContextAsync("mark_read", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, PayloadHash(request.Ids!)), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorNotificationMarkReadPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, request.Ids!.Count, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmMarkReadAsync(
        McpOperatorNotificationMarkReadRequest request,
        HttpContext http,
        IHostEnvironment environment,
        [FromServices] IMcpOperatorAuthorization authorization,
        [FromServices] IMcpOperatorConfirmationService confirmations,
        [FromServices] INetRatelNotificationService notifications,
        CancellationToken cancellationToken)
    {
        if (!HasValidIds(request.Ids)) return Failure("notification_invalid", MinimalContext("mark_read", http.TraceIdentifier));
        var admitted = await TryContextAsync("mark_read", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.Failure is { } failure) return failure;
        var context = admitted.Context!;
        if (!IsOpaque(request.PlanToken) || !IsOpaque(request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);

        var confirmed = await confirmations.ConfirmAsync(new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, PayloadHash(request.Ids!), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmed.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmed.IsReplay)
            return Results.Ok(new McpOperatorNotificationMarkReadResult(ParseCount(confirmed.ResultReference), true, context.CorrelationId));
        if (!confirmed.IsNewDispatch || confirmed.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var updated = await notifications.MarkReadAsync(context.Subject, request.Ids!, cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, updated.ToString(System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorNotificationMarkReadResult(updated, false, context.CorrelationId));
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
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "notification_mark_read_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("notification_mark_read_failed", context);
        }
    }

    private static async Task<McpOperatorNotificationContextResult> TryContextAsync(string operation, HttpContext http, IHostEnvironment environment, IMcpOperatorAuthorization authorization, CancellationToken cancellationToken)
    {
        var fallback = MinimalContext(operation, http.TraceIdentifier);
        if (!http.TryGetMcpOperatorDelegation(out var effective) || effective is null)
            return new(null, Failure("delegated_identity_required", fallback));
        var access = McpOperationAccessCatalog.Find(Tool, operation);
        var descriptor = McpOperatorOperationCatalog.Find(Tool, operation);
        if (!McpOperatorRuntimeEnvironment.TryResolve(environment, out var operatorEnvironment, out var expectedInstance) ||
            access is null || descriptor is null ||
            !string.Equals(effective.Instance, expectedInstance, StringComparison.Ordinal) ||
            !string.Equals(effective.Tool, Tool, StringComparison.Ordinal) ||
            !string.Equals(effective.Operation, operation, StringComparison.Ordinal) ||
            effective.TenantId is not null || effective.AgentId is not null ||
            string.IsNullOrWhiteSpace(effective.Resource) || string.IsNullOrWhiteSpace(effective.CorrelationId))
            return new(null, Failure("delegated_identity_invalid", fallback));

        var principal = new McpOperatorPrincipal(effective.Identity.Subject, effective.Identity.ClientId, effective.Identity.AuthorizedParty,
            effective.Identity.Groups.ToHashSet(StringComparer.Ordinal), effective.Identity.Roles.ToHashSet(StringComparer.Ordinal), effective.Identity.Scopes.ToHashSet(StringComparer.Ordinal), effective.ServicePrincipal);
        var request = new McpOperatorAccessRequest(
            operatorEnvironment,
            principal,
            0,
            null,
            null,
            descriptor.OperationFamily,
            $"{Tool}/{operation}",
            new HashSet<string>([McpOperationAccessScopeNames.Canonical(access.RequiredScope)], StringComparer.Ordinal),
            descriptor.ConfirmationClass,
            effective.CorrelationId!,
            effective.RequestId,
            ControlPlaneDigest,
            null,
            effective.Resource,
            effective.Instance,
            Tool,
            true,
            true,
            true,
            true,
            true);
        var decision = await authorization.EvaluateAsync(request, cancellationToken).ConfigureAwait(false);
        var context = new McpOperatorNotificationContext(decision, effective.ServicePrincipal, effective.Identity.Subject, McpOperationAccessScopeNames.Canonical(access.RequiredScope), operation, effective.CorrelationId!);
        return decision.IsAllowed ? new(context, null) : new(context, Failure(decision.FailureCode ?? "target_policy_missing", context));
    }

    private static McpOperatorNotificationContext MinimalContext(string operation, string correlationId) =>
        new(null!, string.Empty, string.Empty, McpOperationAccessScopeNames.Canonical(McpOperationAccessCatalog.Find(Tool, operation)!.RequiredScope), operation, correlationId);

    private static bool HasValidIds(IReadOnlyList<Guid>? ids) => ids is { Count: > 0 and <= MaximumMarkReadIds } && ids.All(id => id != Guid.Empty) && ids.Distinct().Count() == ids.Count;
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
    private static int ParseCount(string? value) => int.TryParse(value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var count) && count >= 0 ? count : 0;
    private static string PayloadHash(IReadOnlyList<Guid> ids) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(',', ids.Order().Select(id => id.ToString("D"))))));

    private static IResult Failure(string code, McpOperatorNotificationContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "notification_invalid" or "notification_query_invalid" => StatusCodes.Status400BadRequest,
            "notification_not_found" => StatusCodes.Status404NotFound,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "notification_mark_read_failed" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "notification_invalid" or "notification_query_invalid" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            "oauth_scope_missing" => "oauth_scope",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator notification access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator notification access was not admitted.",
            ["failure"] = new { code, layer, retryable = code == "notification_mark_read_failed", requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{Tool}/{context.Operation}", target = (object?)null, remediation = "Use a signed delegated operator identity with the required scope and an active Notifications ControlPlane policy." },
            ["correlationId"] = context.CorrelationId
        });
    }

    private sealed record McpOperatorNotificationContext(McpOperatorDecision Decision, string ServicePrincipal, string Subject, string RequiredScope, string Operation, string CorrelationId);
    private sealed record McpOperatorNotificationContextResult(McpOperatorNotificationContext? Context, IResult? Failure);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorNotificationMarkReadRequest(IReadOnlyList<Guid>? Ids, string? PlanToken = null, string? IdempotencyKey = null);
public sealed record McpOperatorNotificationMarkReadPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, int NotificationCount, string CorrelationId);
public sealed record McpOperatorNotificationMarkReadResult(int UpdatedCount, bool Replayed, string CorrelationId);
