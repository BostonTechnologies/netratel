using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Validates only manual HTTP-MCP credentials through the paired API exchange.
/// The opaque bearer is used for this request alone and is not retained in the
/// principal, singleton state, or outbound business API client.
/// </summary>
public sealed class McpLocalCredentialAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IHttpClientFactory clients,
    McpLocalCredentialPairingService pairing)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "NetRatelMcpLocalCredential";
    public const string ApiHttpClientName = "NetRatel.Mcp.LocalCredentialExchange";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer nrt_ic_", StringComparison.Ordinal))
            return AuthenticateResult.NoResult();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/mcp/local-delegation/authenticate");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        request.Headers.TryAddWithoutValidation("X-NetRatel-Mcp-Pairing", pairing.CreateAuthenticationProof());
        try
        {
            using var response = await clients.CreateClient(ApiHttpClientName).SendAsync(request, Context.RequestAborted).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return AuthenticateResult.Fail("Invalid local HTTP MCP credential.");

            var result = await response.Content.ReadFromJsonAsync<McpLocalAuthenticationResponse>(cancellationToken: Context.RequestAborted).ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(result.OwnerPrincipalId) || string.IsNullOrWhiteSpace(result.CredentialId))
                return AuthenticateResult.Fail("Local HTTP MCP credential exchange returned an invalid response.");

            var identity = new ClaimsIdentity(
            [
                new Claim("netratel_principal_id", result.OwnerPrincipalId),
                new Claim("netratel_integration_credential_id", result.CredentialId),
                new Claim("auth_mode", "local_http_mcp")
            ], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (HttpRequestException exception)
        {
            return AuthenticateResult.Fail($"Local HTTP MCP credential exchange is unavailable: {exception.Message}");
        }
        catch (TaskCanceledException) when (!Context.RequestAborted.IsCancellationRequested)
        {
            return AuthenticateResult.Fail("Local HTTP MCP credential exchange timed out.");
        }
    }

    private sealed record McpLocalAuthenticationResponse(string OwnerPrincipalId, string CredentialId);
}
