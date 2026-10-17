using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NetRatel.API.Security.Local;
using NetRatel.API.Endpoints.Auth;
using NetRatel.Infrastructure.Identity;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Security.Integration;

/// <summary>
/// Authenticates API-purpose credentials, plus HTTP-MCP credentials on the two
/// dedicated exchange routes. The latter are deliberately never accepted by a
/// normal business API endpoint.
/// </summary>
public sealed class IntegrationCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IIntegrationCredentialService credentials,
    McpOperatorDelegationTokenService? localDelegationTokens = null)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "IntegrationCredential";
    public const string CredentialIdClaimType = "netratel_integration_credential_id";
    private string? _failureCode;
    private string? _correlationId;

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        var purpose = IsLocalDelegationExchangeRoute(Request.Path)
            ? IntegrationCredentialPurpose.HttpMcp
            : IntegrationCredentialPurpose.Api;
        VerifiedIntegrationCredential? verified;
        if (purpose == IntegrationCredentialPurpose.HttpMcp)
        {
            if (!TryPairedResource(out var resource))
            {
                _failureCode = "local_credential_invalid";
                return AuthenticateResult.Fail("Invalid integration credential.");
            }
            var result = await credentials.VerifyWithStatusAsync(header["Bearer ".Length..].Trim(), purpose, resource, Context.RequestAborted).ConfigureAwait(false);
            verified = result.Credential;
            _failureCode = result.FailureCode is "local_credential_expired" or "local_credential_revoked" ? result.FailureCode : "local_credential_invalid";
        }
        else
            verified = await credentials.VerifyAsync(header["Bearer ".Length..].Trim(), purpose, Context.RequestAborted).ConfigureAwait(false);
        if (verified is null)
        {
            return AuthenticateResult.Fail("Invalid integration credential.");
        }

        var claims = new[]
        {
            new Claim(LocalPrincipalClaimsTransformation.PrincipalIdClaimType, verified.OwnerPrincipalId),
            new Claim(CredentialIdClaimType, verified.CredentialId),
            new Claim("auth_mode", "integration_credential"),
            new Claim("integration_credential_purpose", purpose == IntegrationCredentialPurpose.HttpMcp ? "http_mcp" : "api")
        };
        if (!string.IsNullOrWhiteSpace(verified.Resource))
            claims = [.. claims, new Claim("integration_credential_resource", verified.Resource)];
        var identity = new ClaimsIdentity(claims, SchemeName, ClaimTypes.Name, ClaimTypes.Role);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        if (!IsLocalDelegationExchangeRoute(Request.Path)) return base.HandleChallengeAsync(properties);
        var code = _failureCode ?? "local_credential_invalid";
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        return Response.WriteAsJsonAsync(new
        {
            success = false,
            code,
            failure = McpDelegationFailure.ForCode(code),
            correlationId = _correlationId ?? Context.TraceIdentifier
        }, Context.RequestAborted);
    }

    private bool TryPairedResource(out string? resource)
    {
        resource = null;
        var values = Request.Headers[McpLocalDelegationEndpoints.PairingHeaderName];
        if (localDelegationTokens is null || values.Count != 1 || !localDelegationTokens.TryValidate(values[0], out var proof) || proof is null ||
            proof.Identity.ClientId != "netratel-local-gateway" || string.IsNullOrWhiteSpace(proof.Resource)) return false;
        if ((Request.Path.Equals(new PathString(McpLocalDelegationEndpoints.AuthenticationPath)) || Request.Path.Equals(new PathString(McpLocalDelegationEndpoints.AccessStartPath))) &&
            (proof.Tool != "netratel_gateway" || proof.Operation != "authenticate")) return false;
        resource = proof.Resource;
        _correlationId = proof.CorrelationId is { Length: > 0 and <= 128 } value &&
            value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.' or ':') ? value : null;
        return true;
    }

    private static bool IsLocalDelegationExchangeRoute(PathString path) =>
        path.Equals(new PathString(McpLocalDelegationEndpoints.AccessStartPath)) ||
        path.StartsWithSegments(McpLocalDelegationEndpoints.AuthenticationPath, StringComparison.Ordinal) ||
        path.StartsWithSegments(McpLocalDelegationEndpoints.ExchangePath, StringComparison.Ordinal);
}
