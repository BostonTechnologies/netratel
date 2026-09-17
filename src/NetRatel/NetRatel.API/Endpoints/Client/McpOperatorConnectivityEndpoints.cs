using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>
/// Control-plane access to a server-owned, time-bounded ExternalService M2M probe.
/// No route here accepts a network target or connectivity override.
/// </summary>
public static class McpOperatorConnectivityEndpoints
{
    private const string Tool = "netratel_connectivity";
    private static readonly string ControlPlaneDigest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("netratel-mcp-connectivity-control-plane-v1")));

    public static IEndpointRouteBuilder MapMcpOperatorConnectivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/mcp/operator/connectivity")
            .WithTags("MCP Operator Connectivity")
            .RequireAuthorization("M2MOnly");

        group.MapGet("/settings", SettingsAsync);
        group.MapGet("/netratel", NetRatelAsync);
        group.MapPost("/test/preview", PreviewTestAsync);
        group.MapPost("/test/confirm", ConfirmTestAsync);
        return app;
    }

    private static async Task<IResult> SettingsAsync(HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConnectivityAuthority connectivity,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync("settings", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure) return Failure(failure, admitted.Context ?? admitted.Fallback);
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(await connectivity.GetSettingsAsync(cancellationToken).ConfigureAwait(false));
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> NetRatelAsync(HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConnectivityAuthority connectivity,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync("netratel", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure) return Failure(failure, admitted.Context ?? admitted.Fallback);
        var context = admitted.Context!;
        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            return Results.Ok(connectivity.GetNetRatel());
        }
        catch (McpOperatorAdmissionRejectedException rejection) { return Failure(rejection.FailureCode, context); }
    }

    private static async Task<IResult> PreviewTestAsync(HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, IMcpOperatorConfirmationService confirmations,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync("test", http, environment, authorization, cancellationToken, "preview_test").ConfigureAwait(false);
        if (admitted.FailureCode is { } failure) return Failure(failure, admitted.Context ?? admitted.Fallback);
        var context = admitted.Context!;
        try
        {
            var plan = await confirmations.CreatePlanAsync(new McpOperatorConfirmationPlanRequest(context.Decision, PayloadHash()), cancellationToken).ConfigureAwait(false);
            return Results.Ok(new McpOperatorConnectivityTestPreview(plan.PlanToken, plan.IdempotencyKey, plan.ExpiresAtUtc, plan.ConfirmationClass, context.CorrelationId));
        }
        catch (ArgumentException) { return Failure("confirmation_plan_invalid", context); }
    }

    private static async Task<IResult> ConfirmTestAsync(McpOperatorConnectivityTestConfirmRequest request, HttpContext http,
        IHostEnvironment environment, IMcpOperatorAuthorization authorization,
        IMcpOperatorConfirmationService confirmations, IMcpOperatorConnectivityAuthority connectivity,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitAsync("test", http, environment, authorization, cancellationToken).ConfigureAwait(false);
        if (admitted.FailureCode is { } failure) return Failure(failure, admitted.Context ?? admitted.Fallback);
        var context = admitted.Context!;
        if (!IsOpaque(request.PlanToken) || !IsOpaque(request.IdempotencyKey)) return Failure("confirmation_plan_invalid", context);

        var confirmed = await confirmations.ConfirmAsync(
            new McpOperatorConfirmationRequest(request.PlanToken!, request.IdempotencyKey!, PayloadHash(), context.Decision), cancellationToken).ConfigureAwait(false);
        if (confirmed.FailureCode is { } confirmationFailure) return Failure(confirmationFailure, context);
        if (confirmed.IsReplay)
        {
            return Results.Ok(new McpOperatorConnectivityTestConfirmed(null, true, context.CorrelationId));
        }
        if (!confirmed.IsNewDispatch || confirmed.IdempotencyId is not { } idempotencyId) return Failure("confirmation_plan_invalid", context);

        try
        {
            await authorization.RecordAcceptedAsync(context.Decision, context.ServicePrincipal, cancellationToken).ConfigureAwait(false);
            var result = await connectivity.TestAsync(cancellationToken).ConfigureAwait(false);
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Succeeded, "connectivity-test", CancellationToken.None).ConfigureAwait(false);
            return Results.Ok(new McpOperatorConnectivityTestConfirmed(result, false, context.CorrelationId));
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
            await confirmations.CompleteAsync(idempotencyId, McpOperatorIdempotencyOutcome.Failed, "connectivity_test_failed", CancellationToken.None).ConfigureAwait(false);
            return Failure("connectivity_test_failed", context);
        }
    }

    private static Task<McpOperatorControlPlaneAdmissionResult> AdmitAsync(string operation, HttpContext http, IHostEnvironment environment,
        IMcpOperatorAuthorization authorization, CancellationToken cancellationToken, string? delegatedOperation = null) =>
        McpOperatorControlPlaneAdmission.TryAuthorizeAsync(Tool, operation, ControlPlaneDigest, http, environment, authorization, cancellationToken, delegatedOperation);

    private static string PayloadHash() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("server-owned-external-service-m2m-probe-v1")));
    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static IResult Failure(string code, McpOperatorControlPlaneContext context)
    {
        var status = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => StatusCodes.Status401Unauthorized,
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" or "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => StatusCodes.Status409Conflict,
            "connectivity_test_failed" => StatusCodes.Status503ServiceUnavailable,
            _ => StatusCodes.Status403Forbidden
        };
        var layer = code switch
        {
            "delegated_identity_required" or "delegated_identity_invalid" => "delegation",
            "confirmation_plan_invalid" or "confirmation_plan_stale" or "confirmation_plan_expired" => "confirmation",
            "idempotency_conflict" or "idempotency_pending" or "idempotency_replay_unavailable" => "idempotency",
            "oauth_scope_missing" => "oauth_scope",
            _ => "policy"
        };
        return Results.Problem(statusCode: status, title: "MCP operator connectivity access was not admitted.", extensions: new Dictionary<string, object?>
        {
            ["success"] = false,
            ["summary"] = "MCP operator connectivity access was not admitted.",
            ["failure"] = new { code, layer, retryable = code == "connectivity_test_failed", requiredScopes = new[] { context.RequiredScope }, requiredOperation = $"{context.Tool}/{context.Operation}", target = (object?)null, remediation = "Use a signed delegated operator identity with the required scope and an active Connectivity ControlPlane policy." },
            ["correlationId"] = context.CorrelationId
        });
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record McpOperatorConnectivityTestConfirmRequest(string? PlanToken, string? IdempotencyKey);
public sealed record McpOperatorConnectivityTestPreview(string PlanToken, string IdempotencyKey, DateTimeOffset ExpiresAtUtc, McpOperatorConfirmationClass ConfirmationClass, string CorrelationId);
public sealed record McpOperatorConnectivityTestConfirmed(McpOperatorConnectivityTestResult? Result, bool Replayed, string CorrelationId);
