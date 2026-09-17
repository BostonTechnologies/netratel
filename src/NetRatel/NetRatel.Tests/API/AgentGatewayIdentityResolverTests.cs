using System.Security.Claims;
using FluentAssertions;
using NetRatel.API.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayIdentityResolverTests
{
    [Fact]
    public void TryResolve_RequiresMatchingAgentClaimsTenantRoleAndScope()
    {
        var agentId = Guid.NewGuid();
        var principal = CreatePrincipal(agentId, agentId, tenantId: 12, includeScope: true);

        var result = AgentGatewayIdentityResolver.TryResolve(principal, out var identity, out var error);

        result.Should().BeTrue(error);
        identity.Should().Be(new AuthenticatedAgentIdentity(12, agentId));
    }

    [Fact]
    public void TryResolve_RejectsMismatchedSubjectAndAgentId()
    {
        var principal = CreatePrincipal(Guid.NewGuid(), Guid.NewGuid(), tenantId: 12, includeScope: true);

        var result = AgentGatewayIdentityResolver.TryResolve(principal, out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("sub and agent_id");
    }

    [Fact]
    public void TryResolve_RejectsMissingConnectScope()
    {
        var agentId = Guid.NewGuid();
        var principal = CreatePrincipal(agentId, agentId, tenantId: 12, includeScope: false);

        var result = AgentGatewayIdentityResolver.TryResolve(principal, out _, out var error);

        result.Should().BeFalse();
        error.Should().Contain("netratel:connect");
    }

    private static ClaimsPrincipal CreatePrincipal(
        Guid subject,
        Guid agentId,
        int tenantId,
        bool includeScope)
    {
        var claims = new List<Claim>
        {
            new("sub", subject.ToString("D")),
            new("agent_id", agentId.ToString("D")),
            new("tenant_id", tenantId.ToString()),
            new("role", "agent")
        };
        if (includeScope)
        {
            claims.Add(new Claim("scope", "netratel:connect"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }
}
