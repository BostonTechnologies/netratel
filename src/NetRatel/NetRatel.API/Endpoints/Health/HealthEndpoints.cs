using Microsoft.Extensions.Diagnostics.HealthChecks;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        foreach (var path in new[] { "/health/live", McpApiHealthRoutes.Liveness })
        {
            app.MapGet(path, () => Results.Ok(new { status = "alive" }))
                .RequireAuthorization("HealthRead")
                .WithTags("Health")
                .WithSummary("Liveness probe");
        }

        foreach (var path in new[] { "/health/ready", McpApiHealthRoutes.Readiness })
        {
            app.MapGet(path, async (HealthCheckService healthChecks, CancellationToken cancellationToken) =>
            {
                var report = await healthChecks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
                return report.Status == HealthStatus.Healthy
                    ? Results.Ok(new { status = "ready" })
                    : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                        title: "A required NetRatel dependency is unhealthy.",
                        extensions: new Dictionary<string, object?> { ["code"] = "api_readiness_unhealthy" });
            })
                .RequireAuthorization("HealthRead")
                .WithTags("Health")
                .WithSummary("Readiness probe for required NetRatel dependencies");
        }

        return app;
    }
}
