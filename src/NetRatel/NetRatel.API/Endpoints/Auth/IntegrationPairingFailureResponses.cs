using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

internal static class IntegrationPairingFailureResponses
{
    public static bool IsTransient(Exception exception) => exception is TimeoutException or DbUpdateConcurrencyException or
        DbException { IsTransient: true } or DbUpdateException { InnerException: DbException { IsTransient: true } };

    public static IntegrationPairingFailure Validation(IntegrationCredentialGrantValidationException exception, string stage) =>
        (exception.ReviewFailure ?? IntegrationPairingFailure.ForCode(
            exception.Forbidden ? "review_owner_authority_missing" : "review_proposal_changed", stage)) with
        { Stage = stage, Field = exception.ReviewFailure?.Field ?? exception.Field };

    public static int ValidationStatus(IntegrationCredentialGrantValidationException exception, IntegrationPairingFailure failure) =>
        failure.Code switch
        {
            "review_credential_changed" or "review_policy_changed" => StatusCodes.Status409Conflict,
            _ when failure.Code.StartsWith("integration_", StringComparison.Ordinal) => StatusCodes.Status400BadRequest,
            _ => exception.Forbidden ? StatusCodes.Status403Forbidden : StatusCodes.Status400BadRequest
        };

    public static IResult CredentialFailure(HttpContext http, IntegrationPairingFailure failure, int status)
    {
        http.Response.Headers.CacheControl = "no-store";
        http.Response.Headers["X-Correlation-ID"] = http.TraceIdentifier;
        failure = failure with { CorrelationId = http.TraceIdentifier };
        if (status == StatusCodes.Status400BadRequest && failure.Field is { } field)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { [field] = [failure.Describe()] }, extensions:
                new Dictionary<string, object?>
                {
                    ["code"] = failure.Code, ["stage"] = failure.Stage, ["field"] = field, ["correlationId"] = failure.CorrelationId,
                    ["remediation"] = failure.Remediation,
                    ["tenantId"] = failure.TenantId, ["requestedCount"] = failure.RequestedCount, ["limit"] = failure.Limit
                });
        }
        return Results.Json(failure, statusCode: status);
    }

    public static IResult Unavailable(string stage, string correlationId) => Results.Json(
        IntegrationPairingFailure.ForCode("review_service_unavailable", stage) with { CorrelationId = correlationId },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
