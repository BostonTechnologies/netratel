using Microsoft.AspNetCore.Authorization;

namespace NetRatel.API.Security.M2M;

public sealed class AllowedClientRequirement : IAuthorizationRequirement
{
    public AllowedClientRequirement(IEnumerable<string> allowedClientIds)
        => AllowedClientIds = allowedClientIds?.ToHashSet(StringComparer.Ordinal) ?? new();
    public HashSet<string> AllowedClientIds { get; }
}

public sealed class AllowedClientHandler : AuthorizationHandler<AllowedClientRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, AllowedClientRequirement requirement)
    {
        var candidates = new[]
        {
            context.User.FindFirst("client_id")?.Value,
            context.User.FindFirst("azp")?.Value,
            context.User.FindFirst("sub")?.Value
        }.Where(v => !string.IsNullOrWhiteSpace(v));

        if (candidates.Any(v => requirement.AllowedClientIds.Contains(v!)))
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}
