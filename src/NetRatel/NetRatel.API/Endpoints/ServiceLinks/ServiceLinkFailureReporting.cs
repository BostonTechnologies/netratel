using System.Security.Claims;
using NetRatel.Application.Events;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.API.Endpoints.ServiceLinks;

public static partial class ServiceLinkEndpoints
{
    private static async Task<IResult> FailureAsync(HttpContext http, int statusCode, string code, string? existingAttemptId = null)
    {
        // This ID is generated locally, never copied from an untrusted request header.
        var correlation = Guid.NewGuid().ToString("N");
        var stage = HttpMethods.IsGet(http.Request.Method) ? "status" :
            ServiceLinkFailure.NormalizeStage(http.Request.Path.Value?.TrimEnd('/').Split('/').LastOrDefault());
        var failure = ServiceLinkFailure.From(code, stage, correlation, statusCode, existingAttemptId);
        http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ServiceLinkFailure").LogWarning(
            "Service-link operation failed. Code={Code} Stage={Stage} CorrelationId={CorrelationId}",
            failure.Code, failure.Stage, failure.CorrelationId);

        // Only interactive commands produce personal notifications. Reads, peer protocol traffic
        // and the background reconciliation worker cannot flood the notification centre.
        var actor = http.User.FindFirstValue("netratel_principal_id") ?? http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub");
        if (HttpMethods.IsPost(http.Request.Method) &&
            http.Request.Path.StartsWithSegments("/api/v1/admin/service-links") &&
            http.User.Identity?.IsAuthenticated == true &&
            http.User.FindFirst("service_principal_id") is null &&
            http.User.FindFirst("netratel_service_principal_id") is null &&
            http.User.FindFirst("netratel_integration_credential_id") is null &&
            http.User.FindFirst("integration_credential_id") is null && !string.IsNullOrWhiteSpace(actor))
        {
            try
            {
                await using var reportingScope = http.RequestServices.CreateAsyncScope();
                var events = reportingScope.ServiceProvider.GetService<IEventRecorder>();
                if (events is not null) await events.RecordAsync(new DomainEvent
                {
                    EventType = ServiceLinkFailure.NotificationEventType, Source = "ServiceLink",
                    CorrelationId = correlation, EntityId = actor, Severity = "Warning",
                    Message = failure.Message,
                    Payload = new { failure.Code, failure.Stage, failure.CorrelationId, Link = "/account/integration-credentials" }
                }, http.RequestAborted);
            }
            catch (Exception) when (!http.RequestAborted.IsCancellationRequested)
            {
                // Reporting must not replace the bounded original failure or expose exception text.
                http.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("ServiceLinkFailure").LogWarning(
                    "Service-link failure notification could not be saved. CorrelationId={CorrelationId}", correlation);
            }
        }
        return Results.Problem(statusCode: statusCode, title: failure.Message,
            extensions: new Dictionary<string, object?>
            {
                ["code"] = ServiceLinkFailure.ProtocolCode(code, statusCode), ["stage"] = failure.Stage, ["correlationId"] = failure.CorrelationId,
                ["existingAttemptId"] = failure.ExistingAttemptId
            });
    }
}

internal static class ServiceLinkFailureReporting
{
    public static bool IsNetworkPolicyFailure(Exception exception)
    {
        // The policy is local code; use its local marker, never classify by peer/error text.
        for (Exception? current = exception; current is not null; current = current.InnerException)
            if (current.Data.Contains("ServiceLink.NetworkPolicyRejected")) return true;
        return false;
    }
}
