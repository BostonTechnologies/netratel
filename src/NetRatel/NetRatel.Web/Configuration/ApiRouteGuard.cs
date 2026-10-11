using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace NetRatel.Web.Configuration;

public static class ApiRouteGuard
{
    public static IApplicationBuilder UseApiRouteGuard(this IApplicationBuilder app) =>
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value;
            if (string.Equals(path, "/api", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "/api/", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.Redirect("/api/docs/");
                return;
            }

            if (ctx.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/docs", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/openapi", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.Equals("/api/pairing/v1/metadata", StringComparison.OrdinalIgnoreCase) &&
                // The CLI's tenant-scoped telemetry command is intentionally served
                // by the same local Web gateway as the existing API surface. The API
                // remains the authorization boundary: its TelemetryReader policy and
                // effective credential-grant evaluation decide every request.
                !ctx.Request.Path.StartsWithSegments("/api/v2/agents", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/v2/agent-updates", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/v2/setup", StringComparison.OrdinalIgnoreCase) &&
                !ctx.Request.Path.StartsWithSegments("/api/v2/local-auth", StringComparison.OrdinalIgnoreCase))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });
}
