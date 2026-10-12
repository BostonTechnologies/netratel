using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts;

namespace NetRatel.API.Endpoints.Client;

public static class ClientOffboardingEndpoints
{
    public static IEndpointRouteBuilder MapClientOffboardingEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/agents/{tenantId:int}/{agentId:guid}/offboarding")
            .WithTags("Client Offboarding").RequireAuthorization("ClientManager");
        group.MapGet("", ReadAsync);
        group.MapPost("/uninstall", UninstallAsync);
        group.MapDelete("", RemoveAsync);
        return app;
    }

    private static Task<IResult> ReadAsync(int tenantId, Guid agentId, HttpContext http,
        IEffectiveAccessService access, ClientOffboardingService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, http, access, () => service.ReadAsync(new(tenantId, agentId), ct), ct);

    private static Task<IResult> UninstallAsync(int tenantId, Guid agentId, ClientUninstallRequest request, HttpContext http,
        IEffectiveAccessService access, ClientOffboardingService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, http, access, () => service.RequestUninstallAsync(new(tenantId, agentId), request, Actor(http), ct), ct);

    private static Task<IResult> RemoveAsync(int tenantId, Guid agentId, [FromBody] ClientRemovalRequest request, HttpContext http,
        IEffectiveAccessService access, ClientOffboardingService service, CancellationToken ct) =>
        ExecuteAsync(tenantId, http, access, () => service.RemoveAsync(new(tenantId, agentId), request.Reason, Actor(http), ct), ct);

    private static async Task<IResult> ExecuteAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        Func<Task<ClientOffboardingDetailsDto>> operation, CancellationToken ct)
    {
        if (!await access.AuthorizeAsync(http.User, NetRatelPermissions.ClientManagement, tenantId, ct).ConfigureAwait(false)) return Results.Forbid();
        try { return Results.Ok(await operation().ConfigureAwait(false)); }
        catch (ClientOffboardingException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code, extensions: new Dictionary<string, object?> { ["code"] = error.Code }); }
        catch (AgentAuthException error) { return Results.Problem(statusCode: error.StatusCode, title: error.Code); }
    }

    private static string Actor(HttpContext http) => http.User.FindFirstValue(ClaimTypes.NameIdentifier) ?? http.User.FindFirstValue("sub") ?? http.User.Identity?.Name ?? "operator";
}
