using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;

namespace NetRatel.API.Security.Integration;

/// <summary>Authenticates only API-purpose integration credentials.</summary>
public sealed class IntegrationCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIntegrationCredentialService credentials)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationCredential";
    public const string CredentialIdClaimType = "netratel_integration_credential_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var verified = await credentials.VerifyAsync(header["Bearer ".Length..].Trim(), IntegrationCredentialPurpose.Api, Context.RequestAborted)
            .ConfigureAwait(false);
        if (verified is null)
        {
            return AuthenticateResult.Fail("Invalid integration credential.");
        }

        var claims = new[]
        {
            new Claim(LocalPrincipalClaimsTransformation.PrincipalIdClaimType, verified.OwnerPrincipalId),
            new Claim(CredentialIdClaimType, verified.CredentialId),
            new Claim("auth_mode", "integration_credential"),
            new Claim("integration_credential_purpose", "api")
        };
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
