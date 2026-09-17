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

                await events.RecordAsync(new DomainEvent
                {
                    EventType = NetRatelEventTypes.System.UnhandledException,
                    Source = nameof(ExceptionNotificationMiddleware),
                    CorrelationId = correlationId,
                    Severity = "Critical",
                    Message = $"Unhandled exception at '{context.Request.Path}' ({context.Request.Method}).",
                    Payload = new ExceptionPayload(
                        Path: context.Request.Path,
                        Method: context.Request.Method,
                        CorrelationId: correlationId,
                        Error: ex.Message,
                        StackTrace: stack)
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
