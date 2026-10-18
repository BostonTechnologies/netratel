using NetRatel.API.Middleware;
using NetRatel.API.Endpoints.Auth;
using NetRatel.Infrastructure.Identity;
using NetRatel.Shared.Connectivity;
using NetRatel.Application.Operations;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

/// <summary>Caller-bound safe requests and receipts. Approval and secret delivery use separate existing authorities.</summary>
public static class McpReviewedAccessEndpoints
{
    public static IEndpointRouteBuilder MapMcpReviewedAccessEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v2/mcp/operator/access").RequireAuthorization("M2MOnly").WithTags("MCP Operator");
        api.MapPost("/requests", (ReviewedMcpAccessProposal proposal, HttpContext http, IHostEnvironment environment,
            IntegrationPairingService pairing, CancellationToken ct) => Guard(http, environment, "request", async delegation =>
                await pairing.RequestReviewedAsync(proposal, delegation.Identity.Subject, delegation.IngressCredentialId!,
                    Origin(delegation.Resource), delegation.Resource, OperatorEnvironment(environment), ct).ConfigureAwait(false)));
        api.MapGet("/requests", (HttpContext http, IHostEnvironment environment, IntegrationPairingService pairing, CancellationToken ct) =>
            Guard(http, environment, "requests", async delegation => await pairing.ListReviewedAsync(delegation.Identity.Subject, delegation.IngressCredentialId!, ct, delegation.Resource, OperatorEnvironment(environment)).ConfigureAwait(false)));
        api.MapPost("/receipt", (ReviewedMcpAccessReference request, HttpContext http, IHostEnvironment environment, IntegrationPairingService pairing, CancellationToken ct) =>
            Guard(http, environment, "receipt", delegation => pairing.InspectReviewedAsync(request.RequestId, delegation.Identity.Subject, delegation.IngressCredentialId!, ct: ct, currentResource: delegation.Resource, currentEnvironment: OperatorEnvironment(environment))));
        api.MapPost("/resume", (ReviewedMcpAccessReference request, HttpContext http, IHostEnvironment environment, IntegrationPairingService pairing, CancellationToken ct) =>
            Guard(http, environment, "resume", delegation => pairing.InspectReviewedAsync(request.RequestId, delegation.Identity.Subject, delegation.IngressCredentialId!, resume: true, ct: ct, currentResource: delegation.Resource, currentEnvironment: OperatorEnvironment(environment))));
        api.MapPost("/cancel", (ReviewedMcpAccessReference request, HttpContext http, IHostEnvironment environment, IntegrationPairingService pairing, CancellationToken ct) =>
            Guard(http, environment, "cancel", delegation => pairing.CancelReviewedAsync(request.RequestId, delegation.Identity.Subject, delegation.IngressCredentialId!, ct, delegation.Resource, OperatorEnvironment(environment))));
        api.MapGet("/credentials/{credentialId}", (string credentialId, HttpContext http, IHostEnvironment environment, IntegrationPairingService pairing, TimeProvider clock, CancellationToken ct) =>
            Guard(http, environment, "credential", async delegation =>
            {
                var credential = await pairing.InspectOwnedCredentialAsync(delegation.Identity.Subject, credentialId, ct).ConfigureAwait(false);
                if (credential is null || !McpResourceUri.Equivalent(credential.Resource, delegation.Resource)) throw new IntegrationPairingUnavailableException();
                var admission = await pairing.InspectCredentialAdmissionAsync(delegation.Identity.Subject, credentialId,
                    OperatorEnvironment(environment), ct: ct).ConfigureAwait(false);
                return new { credential, lifecycleState = credential.RevokedAtUtc is not null ? "revoked" : credential.ExpiresAtUtc <= clock.GetUtcNow() ? "expired" : "active", admission };
            }));
        return app;
    }

    private static async Task<IResult> Guard<T>(HttpContext http, IHostEnvironment environment, string operation,
        Func<McpOperatorDelegation, Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        if (!http.TryGetMcpOperatorDelegation(out var caller) || caller is null || caller.Tool != "netratel_access" || caller.Operation != operation ||
            caller.TenantId is not null || caller.AgentId is not null || string.IsNullOrWhiteSpace(caller.IngressCredentialId) ||
            caller.Identity.ClientId != caller.IngressCredentialId || string.IsNullOrWhiteSpace(caller.Identity.Subject) ||
            caller.Instance != (environment.IsProduction() ? "prod" : "dev") || (!environment.IsDevelopment() && !environment.IsProduction()))
            return Results.Unauthorized();
        try
        {
            object? receipt = await action(caller).ConfigureAwait(false);
            if (receipt is ReviewedMcpAccessReceipt { ReviewFailure: { } failure } reviewed)
                receipt = reviewed with { ReviewFailure = failure with { CorrelationId = caller.CorrelationId } };
            return Results.Ok(new { receipt, currentDelegation = new { caller.Identity.Subject, caller.Identity.ClientId, caller.Identity.Roles, caller.Identity.Scopes, caller.Tool, caller.Operation }, correlationId = caller.CorrelationId });
        }
        catch (IntegrationPairingUnavailableException exception)
        {
            return Results.BadRequest((exception.ReviewFailure ?? IntegrationPairingFailure.ForCode("reviewed_request_unavailable", operation)) with
            { CorrelationId = caller.CorrelationId });
        }
        catch (IntegrationCredentialGrantValidationException exception)
        {
            if (exception.ReviewFailure is { } failure)
                return Results.Json(failure with { CorrelationId = caller.CorrelationId }, statusCode: exception.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest);
            return Results.Json(new { code = exception.Field, nextAction = exception.Field == "protectedSetupRequired" ?
                "Prepare this request with the protected setup client, retain its device capability outside the model, then inspect and resume by requestId." :
                "Review exact grants, targets, UTC expiry and revisions with an independent authorized operator; submit a new requestId when the proposal changes.", correlationId = caller.CorrelationId }, statusCode: exception.Forbidden ? 403 : 400);
        }
        catch (Exception exception) when (IntegrationPairingFailureResponses.IsTransient(exception))
        {
            return IntegrationPairingFailureResponses.Unavailable(operation, caller.CorrelationId);
        }
    }
    private static McpOperatorEnvironment OperatorEnvironment(IHostEnvironment environment) => environment.IsProduction() ? McpOperatorEnvironment.Production : McpOperatorEnvironment.Development;
    private static string Origin(string resource) => new Uri(resource).GetLeftPart(UriPartial.Authority);
}
