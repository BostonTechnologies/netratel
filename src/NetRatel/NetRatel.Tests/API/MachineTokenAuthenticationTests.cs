using FluentAssertions;
using Microsoft.IdentityModel.Tokens;
using NetRatel.API.Security;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class MachineTokenAuthenticationTests
{
    private const string Issuer = "https://issuer.example.test";
    private const string MachineAudience = "netratel-machine";
    private const string HumanAudience = "netratel-api";

    [Fact]
    public void Disabled_machine_configuration_never_routes_a_retained_machine_token_to_the_machine_scheme()
    {
        var token = ReadToken(CreateSignedToken(MachineAudience, SecurityAlgorithms.RsaSha256));

        MachineTokenAuthentication.IsCandidate(token, Options(enabled: false)).Should().BeFalse();
    }

    [Fact]
    public void Shared_issuer_routes_only_the_machine_audience_to_machine_authentication()
    {
        var machineToken = ReadToken(CreateSignedToken(MachineAudience, SecurityAlgorithms.RsaSha256));
        var humanToken = ReadToken(CreateSignedToken(HumanAudience, SecurityAlgorithms.RsaSha256));

        MachineTokenAuthentication.IsCandidate(machineToken, Options(enabled: true)).Should().BeTrue();
        MachineTokenAuthentication.IsCandidate(humanToken, Options(enabled: true)).Should().BeFalse();
    }

    [Fact]
    public void Signed_token_using_an_algorithm_outside_the_configured_allowlist_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var handler = new JwtSecurityTokenHandler();
        var token = CreateSignedToken(MachineAudience, SecurityAlgorithms.RsaSha512, key);
        var parameters = MachineTokenAuthentication.CreateValidationParameters(Options(enabled: true));
        parameters.IssuerSigningKey = key;

        Action validate = () => handler.ValidateToken(token, parameters, out _);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void Correctly_signed_machine_token_with_required_claims_is_accepted()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa);
        var handler = new JwtSecurityTokenHandler();
        var token = CreateSignedToken(MachineAudience, SecurityAlgorithms.RsaSha256, key);
        var parameters = MachineTokenAuthentication.CreateValidationParameters(Options(enabled: true));
        parameters.IssuerSigningKey = key;

        var principal = handler.ValidateToken(token, parameters, out _);

        principal.Identity?.IsAuthenticated.Should().BeTrue();
    }

    private static MachineTokenAuthenticationOptions Options(bool enabled) => new()
    {
        Enabled = enabled,
        Authority = Issuer,
        Audience = MachineAudience,
        RequiredGroups = ["operators"],
        AllowedSigningAlgorithms = [SecurityAlgorithms.RsaSha256]
    };

    private static string CreateSignedToken(string audience, string algorithm, SecurityKey? key = null)
    {
        using var rsa = key is null ? RSA.Create(2048) : null;
        var signingKey = key ?? new RsaSecurityKey(rsa!);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Subject = new ClaimsIdentity([new Claim("groups", "operators")]),
            Expires = DateTime.UtcNow.AddMinutes(5),
            SigningCredentials = new SigningCredentials(signingKey, algorithm)
        };
        return new JwtSecurityTokenHandler().CreateEncodedJwt(descriptor);
    }

    private static JwtSecurityToken ReadToken(string encoded) => new JwtSecurityTokenHandler().ReadJwtToken(encoded);
}
