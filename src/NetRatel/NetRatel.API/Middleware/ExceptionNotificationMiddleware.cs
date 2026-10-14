using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.Application.Events;

namespace NetRatel.API.Middleware;

public sealed class ExceptionNotificationMiddleware(RequestDelegate next, ILogger<ExceptionNotificationMiddleware> logger)
{
    private readonly RequestDelegate _next = next;
    private readonly ILogger<ExceptionNotificationMiddleware> _logger = logger;

    public async Task InvokeAsync(HttpContext context, IEventRecorder events, ICorrelationContext correlation)
    {
        try
        {
            await _next(context);
        }
        catch (Exception ex) when (
            (ex is OperationCanceledException || ex is TaskCanceledException) &&
            context.RequestAborted.IsCancellationRequested)
        {
            var correlationId = correlation.GetOrCreate();
            _logger.LogInformation(
                "Request canceled by client at {Method} {Path} (corr={CorrelationId})",
                context.Request.Method,
                context.Request.Path,
                correlationId);
            return;
        }
        catch (IOException ex) when (context.RequestAborted.IsCancellationRequested)
        {
            var correlationId = correlation.GetOrCreate();
            _logger.LogInformation(
                ex,
                "Connection aborted by client at {Method} {Path} (corr={CorrelationId})",
                context.Request.Method,
                context.Request.Path,
                correlationId);
            return;
        }
        catch (Exception ex)
        {
            var correlationId = correlation.GetOrCreate();
            _logger.LogError(
                ex,
                "Unhandled exception at {Method} {Path} (corr={CorrelationId})",
                context.Request.Method,
                context.Request.Path,
                correlationId);

            try
            {
                var stack = ex.StackTrace;
                if (!string.IsNullOrEmpty(stack) && stack.Length > 4000)
                    stack = stack[..4000];
                var innerError = ex.InnerException?.ToString();
                if (innerError is { Length: > 4000 }) innerError = innerError[..4000];

                var tenantId = context.User.FindFirstValue("tenant_id");
                var entityId = context.User.FindFirstValue("agent_id") ?? context.User.FindFirstValue("sub") ??
                    context.User.FindFirstValue(ClaimTypes.NameIdentifier);
                var attemptId = context.Request.RouteValues.TryGetValue("attemptId", out var attempt) ? attempt?.ToString() : null;
                var exceptionType = ex.GetType().FullName ?? ex.GetType().Name;
                // Retry requests have new trace IDs. Scope the identity to the actual tenant,
                // agent/actor, attempt and fault, while retaining those individual trace links.
                var errorIdentity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    tenantId, entityId, attemptId, path = context.Request.Path.Value, context.Request.Method,
                    // Include the inner exception chain and provider details: a common
                    // wrapper/callsite must not merge two different underlying faults.
                    exceptionType, fault = ex.ToString()
                }))));
                var now = DateTimeOffset.UtcNow;
                var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;

                await events.RecordAsync(new DomainEvent
                {
                    OccurredUtc = now,
                    EventType = NetRatelEventTypes.System.UnhandledException,
                    Source = nameof(ExceptionNotificationMiddleware),
                    CorrelationId = correlationId,
                    TenantId = tenantId,
                    EntityId = entityId,
                    Severity = "Critical",
                    Message = $"Unhandled exception at '{context.Request.Path}' ({context.Request.Method}).",
                    Payload = new ExceptionPayload(
                        Path: context.Request.Path,
                        Method: context.Request.Method,
                        CorrelationId: correlationId,
                        Error: ex.Message,
                        StackTrace: stack,
                        ExceptionType: exceptionType,
                        AttemptId: attemptId,
                        Occurrence: attemptId is not null &&
                            context.Request.Path.StartsWithSegments("/api/v2/agent-updates/attempts")
                            ? new(errorIdentity, 1, now, now, correlationId, correlationId, traceId, traceId) : null,
                        InnerExceptionType: ex.InnerException?.GetType().FullName,
                        InnerError: innerError)
                }, context.RequestAborted);
            }
            catch
            {
                // keep pipeline stable; original exception is rethrown below
            }

            throw;
        }
    }
}
