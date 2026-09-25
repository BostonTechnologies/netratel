using System.Diagnostics;
using NetRatel.Application.Events;

namespace NetRatel.API.Middleware;

public sealed class CorrelationLoggingMiddleware(RequestDelegate next, ILogger<CorrelationLoggingMiddleware> logger)
{
    public async Task Invoke(HttpContext context)
    {
        var corr = context.Items.TryGetValue(CorrelationConstants.HttpContextItemKey, out var value)
            ? value as string
            : null;
        if (string.IsNullOrWhiteSpace(corr))
            corr = context.Request.Headers[CorrelationConstants.HeaderName].FirstOrDefault() ?? context.TraceIdentifier;

        var tenant = context.Request.Headers["X-Tenant"].FirstOrDefault() ?? "(none)";
        var traceId = Activity.Current?.TraceId.ToString() ?? context.TraceIdentifier;
        var spanId = Activity.Current?.SpanId.ToString() ?? string.Empty;
        var path = SafeLogPath(context.Request.Path);
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["correlation_id"] = corr!,
            ["trace_id"] = traceId,
            ["span_id"] = spanId,
            ["tenant_id"] = tenant,
            ["request_path"] = path
        });

        var sw = Stopwatch.StartNew();
        logger.LogInformation("HTTP {Method} {Path} started (tenant={Tenant}, corr={CorrelationId})",
            context.Request.Method, path, tenant, corr);
        try
        {
            await next(context);
        }
        finally
        {
            sw.Stop();
            using var completionScope = logger.BeginScope(new Dictionary<string, object>
            {
                ["status_code"] = context.Response.StatusCode,
                ["elapsed_ms"] = sw.ElapsedMilliseconds
            });

            logger.LogInformation("HTTP {Method} {Path} -> {Status} in {Elapsed} ms (corr={CorrelationId})",
                context.Request.Method, path, context.Response.StatusCode, sw.ElapsedMilliseconds, corr);
        }
    }

    internal static string SafeLogPath(PathString path) =>
        path.StartsWithSegments("/clients/install", StringComparison.OrdinalIgnoreCase)
            ? "/clients/install/{capability}"
            : path.Value ?? string.Empty;
}
