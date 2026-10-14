using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Shared.Client;
using NetRatel.Shared.Authentication;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

public static class IntegrationPairingEndpoints
{
    public static IServiceCollection AddIntegrationPairing(this IServiceCollection services)
    {
        services.AddHostedService<IntegrationPairingCleanupWorker>();
        services.AddRateLimiter(options =>
        {
            options.AddPolicy("integration-pairing-start", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(10), QueueLimit = 0, AutoReplenishment = true }));
            options.AddPolicy("integration-pairing-poll", http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 60, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
            options.AddPolicy("integration-pairing-account", http => RateLimitPartition.GetFixedWindowLimiter(
                http.User.FindFirstValue("netratel_principal_id") ?? "unknown", _ => new FixedWindowRateLimiterOptions
                { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0, AutoReplenishment = true }));
        });
        return services;
    }

    public static IEndpointRouteBuilder MapIntegrationPairingEndpoints(this IEndpointRouteBuilder app)
    {
        var publicApi = app.MapGroup(IntegrationPairingProtocol.Route).WithTags("Integration credential pairing").AllowAnonymous();
        publicApi.MapGet("/capabilities", async (IDeploymentBrandingService branding, IConfiguration configuration, HttpContext http, CancellationToken ct) =>
        {
            NoStore(http);
            var origin = await PublicOriginAsync(branding, ct).ConfigureAwait(false);
            if (origin is null) return Results.Json(new { status = "not_configured" }, statusCode: 503);
            try { return Results.Ok(Capabilities(origin, configuration)); }
            catch (IntegrationPairingUnavailableException) { return Results.Json(new { status = "not_configured" }, statusCode: 503); }
        });
        publicApi.MapPost("/start", async ([FromBody] IntegrationPairingStartRequest request, IntegrationPairingService pairing,
            IDeploymentBrandingService branding, IConfiguration configuration, HttpContext http, CancellationToken ct) =>
        {
            var origin = await PublicOriginAsync(branding, ct).ConfigureAwait(false);
            if (origin is null) return Results.Json(new { status = "not_configured" }, statusCode: 503);
            return await GuardAsync(http, () => pairing.StartAsync(request, origin, HttpMcpResource(origin, configuration), ct)).ConfigureAwait(false);
        }).RequireRateLimiting("integration-pairing-start");
        publicApi.MapPost("/poll", ([FromBody] IntegrationPairingDeviceRequest request, IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => GuardAsync(http, () => pairing.PollAsync(request.DeviceCapability, ct)))
            .RequireRateLimiting("integration-pairing-poll");
        publicApi.MapPost("/cancel", ([FromBody] IntegrationPairingDeviceRequest request, IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => GuardAsync(http, () => pairing.CancelAsync(request.DeviceCapability, ct)))
            .RequireRateLimiting("integration-pairing-poll");
        publicApi.MapPost("/acknowledge", ([FromBody] IntegrationPairingAcknowledgeRequest request, IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => GuardAsync(http, () => pairing.AcknowledgeAsync(request.DeviceCapability, request.CredentialId, ct)))
            .RequireRateLimiting("integration-pairing-poll");

        var account = app.MapGroup(IntegrationPairingProtocol.AccountRoute).WithTags("Integration credential pairing")
            .RequireAuthorization("InteractiveAccount").RequireRateLimiting("integration-pairing-account");
        account.MapPost("/lookup", ([FromBody] IntegrationPairingCodeRequest request, IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => AccountGuardAsync(http, () => pairing.ReviewAsync(request.UserCode, ct)));
        account.MapPost("/approve", ([FromBody] IntegrationPairingApprovalRequest request, IntegrationPairingService pairing,
            ClaimsPrincipal principal, HttpContext http, CancellationToken ct) => AccountGuardAsync(http, () => pairing.ApproveAsync(principal, request, ct)));
        account.MapPost("/deny", ([FromBody] IntegrationPairingCodeRequest request, IntegrationPairingService pairing,
            HttpContext http, CancellationToken ct) => AccountGuardAsync(http, () => pairing.DenyAsync(request.UserCode, ct)));
        return app;
    }

    private static Task<IResult> AccountGuardAsync<T>(HttpContext http, Func<Task<T>> action)
    {
        // Same cookie-mutation boundary as account credential create/revoke. Browser UI uses its protected interactive circuit.
        if (http.User.FindFirstValue("netratel_principal_id") is null ||
            (http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1"))
            return Task.FromResult<IResult>(Results.Forbid());
        return GuardAsync(http, action);
    }

    private static async Task<IResult> GuardAsync<T>(HttpContext http, Func<Task<T>> action)
    {
        NoStore(http);
        try { return Results.Ok(await action().ConfigureAwait(false)); }
        catch (IntegrationPairingUnavailableException) { return Results.BadRequest(new { status = "unavailable" }); }
        catch (IntegrationCredentialGrantValidationException exception)
        {
            return exception.Forbidden ? Results.Forbid() : Results.ValidationProblem(
                new Dictionary<string, string[]> { [exception.Field] = [exception.Field == "durableAuthority" ?
                    IntegrationPairingProtocol.DurableAuthorityGuidance : "Review the connection and selected access, then retry."] });
        }
    }

    private static void NoStore(HttpContext http) => http.Response.Headers.CacheControl = "no-store";
    private static async Task<string?> PublicOriginAsync(IDeploymentBrandingService branding, CancellationToken ct)
    {
        try { return ClientEndpointAddress.NormalizePublicOrigin((await branding.GetEffectiveAsync(ct).ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/'); }
        catch (ArgumentException) { return null; }
    }
    private static string HttpMcpResource(string origin, IConfiguration configuration) =>
        McpResourceUri.TryNormalize(configuration["Integration:HttpMcp:PublicResourceUri"] ?? origin + "/mcp", out var resource)
            ? resource! : throw new IntegrationPairingUnavailableException();
    private static IntegrationPairingCapabilities Capabilities(string origin, IConfiguration configuration) => new(IntegrationPairingProtocol.Version, origin, true,
        origin + "/account/integration-credentials?tab=credentials", origin + IntegrationPairingProtocol.Route + "/start",
        origin + IntegrationPairingProtocol.Route + "/poll", origin + IntegrationPairingProtocol.Route + "/cancel",
        origin + IntegrationPairingProtocol.Route + "/acknowledge", HttpMcpResource(origin, configuration))
    {
        AuthenticationMethods = AuthenticationModeConfiguration.Resolve(configuration) switch
        { "Hybrid" => ["local-browser", "oidc-browser"], "Oidc" => ["oidc-browser"], _ => ["local-browser"] }
    };
}

public sealed class IntegrationPairingCleanupWorker(IServiceScopeFactory scopes, TimeProvider clock,
    ILogger<IntegrationPairingCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IntegrationPairingService>().CleanupAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception) { logger.LogWarning("Integration pairing cleanup will resume on the next interval."); }
        } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
