using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using NetRatel.Client.Service.Auth;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AccessTokenAuditMetadataTests
{
    [Fact]
    public void Describe_ReportsOnlyIssuerAudienceAndSigningKeyId()
    {
        var token = new JwtSecurityToken(
            issuer: "https://netratel.example.invalid",
            audience: "netratel-agent");
        token.Header["kid"] = "netratel-agent-es256";
        token.Payload["sub"] = "must-not-be-reported";
        var serialized = new JwtSecurityTokenHandler().WriteToken(token);

        var result = AccessTokenAuditMetadata.Describe(serialized);

        result.Should().Be(
            "issuer=https://netratel.example.invalid, audience=netratel-agent, keyId=netratel-agent-es256");
        result.Should().NotContain("must-not-be-reported");
        result.Should().NotContain(serialized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    public void Describe_WithMissingOrMalformedToken_ReturnsUnavailable(string? accessToken) =>
        AccessTokenAuditMetadata.Describe(accessToken).Should().Be("unavailable");
}
