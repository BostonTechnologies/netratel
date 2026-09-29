using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NetRatel.API.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive" }))
            .RequireAuthorization("HealthRead")
            .WithTags("Health")
            .WithSummary("Liveness probe");

        app.MapGet("/health/ready", async (HealthCheckService healthChecks, CancellationToken cancellationToken) =>
        {
            var report = await healthChecks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);
            return report.Status == HealthStatus.Healthy
                ? Results.Ok(new { status = "ready" })
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "A required NetRatel dependency is unhealthy.");
        })
            .RequireAuthorization("HealthRead")
            .WithTags("Health")
            .WithSummary("Readiness probe for required NetRatel dependencies");

        return app;
    }
}
