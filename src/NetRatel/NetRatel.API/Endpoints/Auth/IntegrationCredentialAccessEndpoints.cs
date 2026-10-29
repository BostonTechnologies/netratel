using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

public static partial class IntegrationCredentialEndpoints
{
    private static void MapAccessEditing(RouteGroupBuilder credentials)
    {
        var access = credentials.MapGroup("/{credentialId}/access-requests")
            .RequireRateLimiting("integration-pairing-account");
        access.MapPost("/", (string credentialId, [FromBody] CredentialAccessRequest request,
            [FromServices] IntegrationPairingService pairing, HttpContext http, CancellationToken ct) =>
            AccessGuardAsync(http, "request", true, context => pairing.RequestAccountAccessAsync(context.Owner,
                credentialId, request.RequestId, request.Grants, request.InstancePermissions,
                context.Origin, context.Resource, context.Environment, ct)));
        access.MapGet("/", (string credentialId, [FromServices] IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => AccessGuardAsync(http, "list", false,
            context => pairing.ListAccountAccessAsync(context.Owner, credentialId, context.Resource, context.Environment, ct)));
        access.MapGet("/{requestId}", (string credentialId, string requestId, [FromServices] IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => AccessGuardAsync(http, "inspect", false,
            context => pairing.InspectAccountAccessAsync(context.Owner, credentialId, requestId, context.Resource, context.Environment, ct)));
        access.MapPost("/{requestId}/apply", (string credentialId, string requestId, [FromBody] CredentialAccessDecision request,
            [FromServices] IntegrationPairingService pairing, HttpContext http, CancellationToken ct) =>
            AccessGuardAsync(http, "apply", true, context => pairing.ApplyAccountAccessAsync(context.Owner,
                credentialId, requestId, request.ProposalHash, request.Confirm, context.Resource, context.Environment, ct)));
        access.MapPost("/{requestId}/cancel", (string credentialId, string requestId, [FromServices] IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => AccessGuardAsync(http, "cancel", true,
            context => pairing.CancelAccountAccessAsync(context.Owner, credentialId, requestId, context.Resource, context.Environment, ct)));
    }

    private static async Task<IResult> AccessGuardAsync<T>(HttpContext http, string stage, bool mutation,
        Func<CredentialAccessContext, Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Correlation-ID"] = http.TraceIdentifier;
        var logger = http.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("NetRatel.CredentialAccess");
        var owner = PrincipalId(http.User);
        if (owner is null || mutation && !IsCookieMutationValidated(http.User, http)) return Results.Forbid();
        var environment = http.RequestServices.GetRequiredService<IHostEnvironment>();
        if (!environment.IsDevelopment() && !environment.IsProduction())
            return Results.BadRequest(IntegrationPairingFailure.ForCode("review_binding_changed", stage) with
            { CorrelationId = http.TraceIdentifier });
        try
        {
            var branding = http.RequestServices.GetRequiredService<IDeploymentBrandingService>();
            var configuration = http.RequestServices.GetRequiredService<IConfiguration>();
            string origin;
            try
            {
                origin = HttpHostSetupProtocol.NormalizeWebAddress((await branding.GetEffectiveAsync(http.RequestAborted)
                    .ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/');
            }
            catch (ArgumentException) { throw new IntegrationPairingUnavailableException
                { ReviewFailure = IntegrationPairingFailure.ForCode("review_destination_unconfigured", stage) }; }
            if (!McpResourceUri.TryNormalize(configuration["Integration:HttpMcp:PublicResourceUri"] ?? origin + "/mcp", out var resource))
                throw new IntegrationPairingUnavailableException
                { ReviewFailure = IntegrationPairingFailure.ForCode("review_destination_unconfigured", stage) };
            return Results.Ok(await action(new(owner, origin, resource!, environment.IsProduction()
                ? McpOperatorEnvironment.Production : McpOperatorEnvironment.Development)).ConfigureAwait(false));
        }
        catch (IntegrationPairingUnavailableException exception)
        {
            logger.LogWarning("Credential access failed. Stage {Stage}, HTTP {HttpStatus}, code {FailureCode}, reference {CorrelationId}",
                stage, 400, exception.ReviewFailure?.Code ?? "review_request_unavailable", http.TraceIdentifier);
            return Results.BadRequest((exception.ReviewFailure ?? IntegrationPairingFailure.ForCode("review_request_unavailable", stage)) with
            { Stage = stage, CorrelationId = http.TraceIdentifier });
        }
        catch (IntegrationCredentialGrantValidationException exception)
        {
            var failure = exception.ReviewFailure ?? new IntegrationPairingFailure(
                exception.Forbidden ? "review_owner_authority_missing" : "review_proposal_changed", stage,
                exception.Forbidden
                    ? "Your account no longer has every selected permission. Review your current tenant and server access, then retry with your retained draft."
                    : "Review the selected tenant and server permissions. Keep at least one permission and remove unavailable or duplicate selections, then retry.");
            logger.LogWarning("Credential access failed. Stage {Stage}, HTTP {HttpStatus}, code {FailureCode}, reference {CorrelationId}",
                stage, exception.Forbidden ? 403 : 400, failure.Code, http.TraceIdentifier);
            return Results.Json(failure with { Stage = stage, CorrelationId = http.TraceIdentifier },
                statusCode: exception.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest);
        }
        catch (Exception exception) when (IntegrationPairingFailureResponses.IsTransient(exception))
        {
            logger.LogWarning("Credential access failed. Stage {Stage}, HTTP {HttpStatus}, code {FailureCode}, exception type {ExceptionType}, reference {CorrelationId}",
                stage, 503, "review_service_unavailable", exception.GetType().Name, http.TraceIdentifier);
            return IntegrationPairingFailureResponses.Unavailable(stage, http.TraceIdentifier);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError("Credential access failed. Stage {Stage}, HTTP {HttpStatus}, code {FailureCode}, exception type {ExceptionType}, reference {CorrelationId}",
                stage, 500, "unexpected_failure", exception.GetType().Name, http.TraceIdentifier);
            throw;
        }
    }

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CredentialAccessRequest(string RequestId, IReadOnlyList<IntegrationPairingGrant> Grants,
        IReadOnlyList<string> InstancePermissions);
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    public sealed record CredentialAccessDecision(string ProposalHash, bool Confirm);
    private sealed record CredentialAccessContext(string Owner, string Origin, string Resource, McpOperatorEnvironment Environment);
}
