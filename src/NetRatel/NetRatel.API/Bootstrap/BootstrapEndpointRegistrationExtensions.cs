using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace NetRatel.API.Bootstrap;

/// <summary>Minimal anonymous surface available before the operational runtime is safe to start.</summary>
public static class BootstrapEndpointRegistrationExtensions
{
    /// <summary>
    /// Keeps the setup status contract available after the restricted bootstrap host has been
    /// replaced by the operational host. The mutation endpoints deliberately remain absent.
    /// </summary>
    public static WebApplication MapReadyBootstrapStatus(this WebApplication app, BootstrapDescriptor descriptor)
    {
        if (descriptor.State != BootstrapState.Ready)
        {
            throw new InvalidOperationException("The operational setup status can only be mapped for a ready installation.");
        }

        app.MapGet("/api/v2/setup/status", () => Results.Ok(BootstrapLifecycleService.ToStatus(descriptor)))
            .AllowAnonymous()
            .WithTags("Bootstrap");

        return app;
    }

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
            BootstrapSetupSessionService setupSession,
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

            if (result.Descriptor.OperationId is { } operationId)
            {
                setupSession.Issue(context, operationId);
            }

            return Results.Accepted("/api/v2/setup/status", BootstrapLifecycleService.ToStatus(result.Descriptor));
        })
            .AllowAnonymous()
            .RequireRateLimiting(BootstrapApplicationExtensions.ClaimRateLimitPolicy)
            .WithTags("Bootstrap");

        app.MapPost("/api/v2/setup/initialize", async (
            HttpContext context,
            [FromBody] BootstrapInitializeRequest request,
            BootstrapInitializationService initialization,
            BootstrapLifecycleService lifecycle,
            BootstrapSetupSessionService setupSession,
            BootstrapOptions options,
            IHostApplicationLifetime applicationLifetime,
            CancellationToken cancellationToken) =>
        {
            if (!IsAllowedOrigin(context, options))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The request origin is not allowed for setup.");
            }

            var descriptor = await lifecycle.InitializeAsync(cancellationToken).ConfigureAwait(false);
            if (descriptor.State != BootstrapState.Configuring || descriptor.OperationId is not { } operationId)
            {
                return Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup is not available for this installation.");
            }

            if (!setupSession.IsCurrent(context, operationId))
            {
                return Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "The setup session is not valid for this installation.");
            }

            var result = await initialization.InitializeAsync(
                operationId,
                new BootstrapInitializationRequest(request.DisplayName, request.Email, request.Password, request.TenantName),
                cancellationToken).ConfigureAwait(false);
            if (result.Succeeded)
            {
                setupSession.Clear(context);
                // The restricted bootstrap host cannot mutate itself into the operational graph.
                // Compose restarts this process; other supervisors should apply the documented
                // normal process restart after this successful, durable transition.
                context.Response.OnCompleted(() =>
                {
                    applicationLifetime.StopApplication();
                    return Task.CompletedTask;
                });
                return Results.Accepted("/api/v2/setup/status", new { status = "ready", result.TenantId, result.UserId });
            }

            if (result.IsUnavailable)
            {
                return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable, title: "The selected storage is unavailable.");
            }

            return string.IsNullOrWhiteSpace(result.Error)
                ? Results.Problem(statusCode: StatusCodes.Status409Conflict, title: "Setup could not be completed.")
                : Results.ValidationProblem(new Dictionary<string, string[]> { ["setup"] = [result.Error] });
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
    public sealed record BootstrapInitializeRequest(string? DisplayName, string? Email, string? Password, string? TenantName);
}
