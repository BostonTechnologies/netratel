using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NetRatel.API.Bootstrap;

/// <summary>Minimal anonymous surface available before the operational runtime is safe to start.</summary>
public static class BootstrapEndpointRegistrationExtensions
{
    public static WebApplication MapBootstrapEndpoints(this WebApplication app)
    {
        app.MapGet("/health/live", () => Results.Ok(new { status = "alive", lifecycle = "bootstrap" }))
            .AllowAnonymous()
            .WithTags("Bootstrap");

        app.MapGet("/health/ready", async (BootstrapLifecycleService lifecycle, CancellationToken cancellationToken) =>
        {
            var status = BootstrapLifecycleService.ToStatus(await lifecycle.InitializeAsync(cancellationToken).ConfigureAwait(false));
            return status.IsReady
                ? Results.Ok(new { status = "ready" })
                : Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "NetRatel setup has not completed.");
        })
            .AllowAnonymous()
            .WithTags("Bootstrap");

        app.MapGet("/api/v2/setup/status", async (BootstrapLifecycleService lifecycle, CancellationToken cancellationToken) =>
        {
            var descriptor = await lifecycle.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return Results.Ok(BootstrapLifecycleService.ToStatus(descriptor));
        })
            .AllowAnonymous()
            .WithTags("Bootstrap");

        app.MapPost("/api/v2/setup/claim", async (
            HttpContext context,
            [FromBody] BootstrapClaimRequest request,
            BootstrapLifecycleService lifecycle,
            BootstrapOptions options,
            CancellationToken cancellationToken) =>
        {
            if (!IsAllowedOrigin(context, options))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The request origin is not allowed for setup.");
            }

            var result = await lifecycle.ClaimSetupAsync(request.Proof ?? string.Empty, cancellationToken).ConfigureAwait(false);
            if (!result.Succeeded || result.Descriptor is null)
            {
                // Do not distinguish replay, expiry, or bad proof. This endpoint never creates a browser session.
                return Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The setup proof is invalid, expired, or already used.");
            }

            return Results.Accepted("/api/v2/setup/status", BootstrapLifecycleService.ToStatus(result.Descriptor));
        })
            .AllowAnonymous()
            .RequireRateLimiting(BootstrapApplicationExtensions.ClaimRateLimitPolicy)
            .WithTags("Bootstrap");

        return app;
    }

    private static bool IsAllowedOrigin(HttpContext context, BootstrapOptions options)
    {
        var origin = context.Request.Headers.Origin.ToString();
        if (string.IsNullOrWhiteSpace(origin))
        {
            // CLI and deployment automation submit the proof directly; browser requests must pass origin validation.
            return true;
        }

        return options.AllowedOrigins.Contains(origin, StringComparer.Ordinal);
    }

    public sealed record BootstrapClaimRequest(string? Proof);
}
