using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

internal static class IntegrationPairingFailureResponses
{
    public static bool IsTransient(Exception exception) => exception is TimeoutException or DbUpdateConcurrencyException or
        DbException { IsTransient: true } or DbUpdateException { InnerException: DbException { IsTransient: true } };

    public static IResult Unavailable(string stage, string correlationId) => Results.Json(
        IntegrationPairingFailure.ForCode("review_service_unavailable", stage) with { CorrelationId = correlationId },
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
