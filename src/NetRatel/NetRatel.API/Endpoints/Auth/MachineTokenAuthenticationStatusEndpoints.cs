using System.Security.Claims;

namespace NetRatel.API.Endpoints;

public static class MachineTokenAuthenticationStatusEndpoints
{
    public static IEndpointRouteBuilder MapMachineTokenAuthenticationStatusEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/auth/machine-token/status", (ClaimsPrincipal user) => Results.Ok(new
        {
            authenticated = user.Identity?.IsAuthenticated == true,
            name = user.Identity?.Name,
            authMode = user.FindFirst("auth_mode")?.Value,
            identityProvider = user.FindFirst("identity_provider")?.Value,
            groups = user.FindAll("groups").Select(claim => claim.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray(),
            roles = user.FindAll(ClaimTypes.Role).Concat(user.FindAll("roles")).Select(claim => claim.Value).Distinct(StringComparer.OrdinalIgnoreCase).Order().ToArray()
        }))
        .RequireAuthorization("MachineTokenApi")
        .WithTags("Authentication");

        return app;
    }
}
