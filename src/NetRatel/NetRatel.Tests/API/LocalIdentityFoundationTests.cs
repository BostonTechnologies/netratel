using System.Security.Claims;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class LocalIdentityFoundationTests
{
    [Theory]
    [InlineData(null, null, "Local")]
    [InlineData("Oidc", null, "Oidc")]
    [InlineData("Hybrid", "https://issuer.example.test", "Hybrid")]
    public void Authentication_mode_resolves_without_making_oidc_a_local_mode_requirement(string? configuredMode, string? authority, string expectedMode)
    {
        var values = new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = configuredMode,
            ["Authentication:Oidc:Authority"] = authority
        };

        var options = LocalAuthenticationOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        options.Mode.Should().Be(expectedMode);
        options.SupportsLocalAccounts.Should().Be(expectedMode is "Local" or "Hybrid");
    }

    [Fact]
    public async Task External_principal_resolution_is_stable_and_never_uses_email()
    {
        var options = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new NetRatelIdentityDbContext(options);
        var resolver = new ApplicationPrincipalResolver(db);
        var first = Principal("https://issuer.example.test", "subject-1", "one@example.test");
        var sameExternalIdentityWithNewEmail = Principal("https://issuer.example.test", "subject-1", "two@example.test");

        var firstId = await resolver.ResolveExternalAsync(first);
        var secondId = await resolver.ResolveExternalAsync(sameExternalIdentityWithNewEmail);

        firstId.Should().NotBeNullOrWhiteSpace();
        secondId.Should().Be(firstId);
        (await db.ApplicationPrincipals.CountAsync()).Should().Be(1);
    }

    [Fact]
    public void Local_session_revalidation_rejects_stale_security_or_authorization_facts()
    {
        var user = new LocalUser
        {
            Id = "local-user",
            PrincipalId = "principal-1",
            SecurityStamp = "stamp-1",
            AuthorizationRevision = 3,
            IsEnabled = true
        };
        var current = LocalPrincipal(user, "stamp-1", 3);

        LocalSessionValidator.IsValid(current, user).Should().BeTrue();
        LocalSessionValidator.IsValid(LocalPrincipal(user, "stale", 3), user).Should().BeFalse();
        LocalSessionValidator.IsValid(LocalPrincipal(user, "stamp-1", 2), user).Should().BeFalse();
        user.IsEnabled = false;
        LocalSessionValidator.IsValid(current, user).Should().BeFalse();
    }

    private static ClaimsPrincipal Principal(string issuer, string subject, string email) => new(new ClaimsIdentity(
    [
        new Claim("iss", issuer),
        new Claim("sub", subject),
        new Claim(ClaimTypes.Email, email)
    ], "Oidc"));

    private static ClaimsPrincipal LocalPrincipal(LocalUser user, string stamp, long revision) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id),
        new Claim("security_stamp", stamp),
        new Claim("authorization_revision", revision.ToString()),
        new Claim(LocalPrincipalClaimsTransformation.PrincipalIdClaimType, user.PrincipalId)
    ], LocalAuthenticationOptions.Scheme));
}
