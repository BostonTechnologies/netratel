using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Control-plane operator access to redacted outbox event state.</summary>
public static class McpOperatorEventEndpoints
{
    private const string Tool = "netratel_events";
    private const int DefaultPageSize = 20;
    private const int MaximumPageSize = 100;
    private static readonly string ControlPlaneDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("netratel-mcp-events-control-plane-v1")));

    public static IEndpointRouteBuilder MapMcpOperatorEventEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/events")
            .WithTags("MCP Operator Events")
            .RequireAuthorization("M2MOnly");

        group.MapGet(string.Empty, ListAsync);
        group.MapGet("/{eventId:guid}", GetAsync);
        group.MapPost("/{eventId:guid}/retry/preview", PreviewRetryAsync);
        group.MapPost("/{eventId:guid}/retry/confirm", ConfirmRetryAsync);
        group.MapPost("/{eventId:guid}/disable/preview", PreviewDisableAsync);
        group.MapPost("/{eventId:guid}/disable/confirm", ConfirmDisableAsync);
        return app;
    }

    private static async Task<IResult> ListAsync(
        int? page,
        int? pageSize,
        HttpContext http,
        IHostEnvironment environment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken)
    {
        if (page is < 1 || pageSize is < 1 or > MaximumPageSize)
        {
            return Failure("event_query_invalid", McpOperatorControlPlaneAdmission.MinimalContext(Tool, "list", http.TraceIdentifier));
        }

        var admitted = await AdmitAsync("list", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure)
        {
            return Failure(failure, admitted.Context ?? admitted.Fallback);
        }

        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await events.ListAsync(page ?? 1, pageSize ?? DefaultPageSize, cancellationToken).ConfigureAwait(false));
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, context);
        }
    }

    private static async Task<IResult> GetAsync(
        Guid eventId,
        HttpContext http,
        IHostEnvironment environment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync("get", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure)
        {
            return Failure(failure, admitted.Context ?? admitted.Fallback);
        }

        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var result = await events.GetAsync(eventId, cancellationToken).ConfigureAwait(false);
            return result is null ? Failure("event_not_found", context) : Results.Ok(result);
        }
        catch (McpOperatorAdmissionRejectedException rejection)
        {
            return Failure(rejection.FailureCode, context);
        }
    }

    private static Task<IResult> PreviewRetryAsync(Guid eventId, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations,
        IMcpOperatorEventAuthority events, CancellationToken cancellationToken) =>
        PreviewMutationAsync("retry", eventId, http, environment, authorization, confirmations, events, cancellationToken);

    private static Task<IResult> PreviewDisableAsync(Guid eventId, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations,
        IMcpOperatorEventAuthority events, CancellationToken cancellationToken) =>
        PreviewMutationAsync("disable", eventId, http, environment, authorization, confirmations, events, cancellationToken);

    private static Task<IResult> ConfirmRetryAsync(Guid eventId, McpOperatorEventConfirmRequest request, HttpContext http,
        IHostEnvironment environment, IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken) =>
        ConfirmMutationAsync("retry", eventId, request, http, environment, authorization, confirmations, events, cancellationToken);

    private static Task<IResult> ConfirmDisableAsync(Guid eventId, McpOperatorEventConfirmRequest request, HttpContext http,
        IHostEnvironment environment, IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken) =>
        ConfirmMutationAsync("disable", eventId, request, http, environment, authorization, confirmations, events, cancellationToken);

    private static async Task<IResult> PreviewMutationAsync(
        string operation,
        Guid eventId,
        HttpContext http,
        IHostEnvironment environment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync(operation, http, environment, authorization, cancellationToken, $"preview_{operation}").ConfigureAwait(false);
        if (admitted.FailureCode is { } failure)
        {
            return Failure(failure, admitted.Context ?? admitted.Fallback);
        }

        var context = admitted.Context!;
        if (await events.GetAsync(eventId, cancellationToken).ConfigureAwait(false) is null)
        {
            return Failure("event_not_found", context);
        }

        try
        {
            var plan = await confirmations.CreatePlanAsync(
                new McpOperatorConfirmationPlanRequest(context.Decision, PayloadHash(operation, eventId)), cancellationToken)
                .ConfigureAwait(false);
            return Results.Ok(new McpOperatorEventPreview(eventId, operation, plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, context.CorrelationId));
        }
        catch (ArgumentException)
        {
            return Failure("confirmation_plan_invalid", context);
        }
    }

    private static async Task<IResult> ConfirmMutationAsync(
        string operation,
        Guid eventId,
        McpOperatorEventConfirmRequest request,
        HttpContext http,
        IHostEnvironment environment,
        IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations,
        IMcpOperatorEventAuthority events,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync(operation, http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure)
        {
            return Failure(failure, admitted.Context ?? admitted.Fallback);
        }

        var context = admitted.Context!;
        if (!IsOpaque(request.PlanToken) || !IsOpaque(request.IdempotencyKey))
        {
            return Failure("confirmation_plan_invalid", context);
        }

        var confirmed = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, PayloadHash(operation, eventId), context.Decision),
            cancellationToken).ConfigureAwait(false);
        if (confirmed.FailureCode is { } confirmationFailure)
        {
            return Failure(confirmationFailure, context);
        }

        if (confirmed.IsReplay)
        {
            return Results.Ok(new McpOperatorEventMutationResult(eventId, operation, true, context.CorrelationId));
        }

        if (!confirmed.IsNewDispatch || confirmed.IdempotencyId is not { } idempotencyId)
        {
            return Failure("confirmation_plan_invalid", context);
        }

        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var outcome = operation == "retry"
                ? await events.RetryAsync(eventId, cancellationToken).ConfigureAwait(false)
                : await events.DisableAsync(eventId, cancellationToken).ConfigureAwait(false);
            if (!outcome.Succeeded)
            {
                await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, outcome.FailureCode, CancellationToken.None).ConfigureAwait(false);
                return Failure(outcome.FailureCode ?? "event_mutation_failed", context);
            }

            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, operation, CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorEventMutationResult(eventId, operation, false, context.CorrelationId));
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
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "event_mutation_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("event_mutation_failed", context);
        }
    }

    private static Task<McpOperatorControlPlaneAdmissionResult> AdmitAsync(string operation, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, CancellationToken cancellationToken, string? delegatedOperation = null) =>
        McpOperatorControlPlaneAdmission.TryAuthorizeAsync(Tool, operation, ControlPlaneDigest, http, environment, authorization, cancellationToken, delegatedOperation);

    private static string PayloadHash(string operation, Guid eventId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{operation}:{eventId:D}")));

    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IResult Failure(string code, McpOperatorControlPlaneContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "event_query_invalid" => StatusCodes.Status400BadRequest,
            "event_not_found" => StatusCodes.Status404NotFound,
            "event_retry_not_allowed" or "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "event_mutation_failed" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "event_query_invalid" => "constraint",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            "oauth_scope_missing" => "oauth_scope",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator event access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator event access was not admitted.",
            ["failure"] = new { code, layer, retryable = code == "event_mutation_failed", requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{context.Tool}/{context.Operation}", target = (object?)null, remediation = "Use a signed delegated operator identity with the required scope and an active Events ControlPlane policy." },
            ["correlationId"] = context.CorrelationId
        });
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorEventConfirmRequest(string? PlanToken, string? IdempotencyKey);
public sealed record McpOperatorEventPreview(Guid EventId, string Operation, string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string CorrelationId);
public sealed record McpOperatorEventMutationResult(Guid EventId, string Operation, bool Replayed, string CorrelationId);
