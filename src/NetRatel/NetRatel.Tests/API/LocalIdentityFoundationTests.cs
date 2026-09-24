using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
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
    [InlineData("local", "https://issuer.example.test", "Local")]
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
    public async Task Validated_oidc_token_projects_a_stable_application_principal()
    {
        using var keyMaterial = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(keyMaterial);
        var issuer = "https://issuer.example.test";
        var subject = "oidc-subject";
        var token = new JwtSecurityToken(
            issuer,
            "netratel-api",
            [new Claim("sub", subject), new Claim(ClaimTypes.Email, "untrusted-email@example.test")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256));
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var validated = handler.ValidateToken(handler.WriteToken(token), new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = "netratel-api",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = signingKey,
            AuthenticationType = "Oidc"
        }, out _);
        var options = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        await using var db = new NetRatelIdentityDbContext(options);

        var transformed = await new LocalPrincipalClaimsTransformation(new ApplicationPrincipalResolver(db))
            .TransformAsync(validated);

        transformed.Identity!.AuthenticationType.Should().Be("Oidc");
        var principalId = transformed.FindFirstValue(LocalPrincipalClaimsTransformation.PrincipalIdClaimType);
        principalId.Should().NotBeNullOrWhiteSpace();
        (await db.ApplicationPrincipals.SingleAsync()).Should().Match<ApplicationPrincipal>(principal =>
            principal.Id == principalId && principal.ExternalIssuer == issuer && principal.ExternalSubject == subject);
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
