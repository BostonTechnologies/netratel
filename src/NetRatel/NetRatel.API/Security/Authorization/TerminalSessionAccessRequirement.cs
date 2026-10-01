using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using NetRatel.API.Gateway;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Security.Authorization;

public sealed class TerminalSessionAccessRequirement : IAuthorizationRequirement
{
}

/// <summary>
/// Resolves the tenant from the actual terminal session before evaluating the
/// caller's effective TerminalAccess grant. An unknown session has no tenant
/// scope, so tenant-only grants cannot probe opaque session identifiers.
/// </summary>
public sealed class TerminalSessionAccessHandler(
    IAgentTerminalSessionRegistry terminals,
    IEffectiveAccessService access)
    : AuthorizationHandler<TerminalSessionAccessRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        TerminalSessionAccessRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true || context.Resource is not HttpContext http)
        {
            return;
        }

        if (!http.Request.RouteValues.TryGetValue("sessionId", out var routeValue) ||
            routeValue is not string sessionId ||
            string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var tenantId = terminals.Get(sessionId)?.TenantId;
        if (await access.AuthorizeAsync(
                context.User,
                NetRatelPermissions.TerminalAccess,
                tenantId,
                http.RequestAborted).ConfigureAwait(false))
        {
            context.Succeed(requirement);
        }
    }
}
