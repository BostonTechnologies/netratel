using System.Security.Claims;
using FluentAssertions;
using NetRatel.API.Realtime.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class OperationsLogTenantAuthorizerTests
{
    [Fact]
    public void TenantClaimsCannotBeUsedToJoinAnotherTenantsLogGroup()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator-1"), new Claim("tenant_id", "17")], "test"));
        var authorizer = new OperationsLogTenantAuthorizer();

        authorizer.IsAuthorized(principal, 17).Should().BeTrue();
        authorizer.IsAuthorized(principal, 18).Should().BeFalse();
    }

    [Fact]
    public void OperatorRetainsExplicitOperationsScope()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, "operator-2"), new Claim(ClaimTypes.Role, "Operator")], "test"));

        new OperationsLogTenantAuthorizer().IsAuthorized(principal, 19).Should().BeTrue();
    }
}
