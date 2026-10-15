using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Infrastructure.Identity;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

public static class HttpHostSetupEndpoints
{
    public static IEndpointRouteBuilder MapHttpHostSetupEndpoints(this IEndpointRouteBuilder app)
    {
        var publicApi = app.MapGroup(HttpHostSetupProtocol.Route).WithTags("HTTP MCP deployment setup").AllowAnonymous();
        publicApi.MapGet("/capabilities", (HttpHostSetupService setup, HttpContext http, CancellationToken ct) =>
            GuardAsync(http, () => setup.CapabilitiesAsync(ct)));
        publicApi.MapPost("/start", ([FromBody] HttpHostSetupStartRequest request, HttpHostSetupService setup, HttpContext http, CancellationToken ct) =>
            GuardAsync(http, () => setup.StartAsync(request, ct))).RequireRateLimiting("integration-pairing-start");
        publicApi.MapPost("/poll", ([FromBody] HttpHostSetupDeviceRequest request, HttpHostSetupService setup, HttpContext http, CancellationToken ct) =>
            GuardAsync(http, () => setup.PollAsync(request.DeviceCapability, ct: ct))).RequireRateLimiting("integration-pairing-poll");
        publicApi.MapPost("/cancel", ([FromBody] HttpHostSetupDeviceRequest request, HttpHostSetupService setup, HttpContext http, CancellationToken ct) =>
            GuardAsync(http, () => setup.PollAsync(request.DeviceCapability, cancel: true, ct))).RequireRateLimiting("integration-pairing-poll");

        var account = app.MapGroup(HttpHostSetupProtocol.AccountRoute).WithTags("HTTP MCP deployment setup")
            .RequireAuthorization("InteractiveAccount").RequireRateLimiting("integration-pairing-account");
        account.MapPost("/lookup", ([FromBody] HttpHostSetupCodeRequest request, HttpHostSetupService setup, ClaimsPrincipal actor, HttpContext http, CancellationToken ct) =>
            AccountGuardAsync(http, () => setup.ReviewAsync(actor, request.UserCode, ct)));
        account.MapPost("/approve", ([FromBody] HttpHostSetupCodeRequest request, HttpHostSetupService setup, ClaimsPrincipal actor, HttpContext http, CancellationToken ct) =>
            AccountGuardAsync(http, () => setup.DecideAsync(actor, request.UserCode, approve: true, ct)));
        account.MapPost("/deny", ([FromBody] HttpHostSetupCodeRequest request, HttpHostSetupService setup, ClaimsPrincipal actor, HttpContext http, CancellationToken ct) =>
            AccountGuardAsync(http, () => setup.DecideAsync(actor, request.UserCode, approve: false, ct)));
        return app;
    }

    private static Task<IResult> AccountGuardAsync<T>(HttpContext http, Func<Task<T>> action)
    {
        if (http.User.FindFirstValue("netratel_principal_id") is null ||
            (http.User.HasClaim("auth_mode", "local") && http.Request.Headers["X-NetRatel-Account-Request"] != "1"))
            return Task.FromResult<IResult>(Results.Forbid());
        return GuardAsync(http, action);
    }
    private static async Task<IResult> GuardAsync<T>(HttpContext http, Func<Task<T>> action)
    {
        http.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action().ConfigureAwait(false)); }
        catch (HttpHostSetupUnavailableException) { return Results.BadRequest(new { status = "unavailable" }); }
        catch (HttpHostSetupAdministratorException) { return Results.Forbid(); }
    }
}
