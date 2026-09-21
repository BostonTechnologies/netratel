using System.Net.Http.Headers;
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
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Context.RequestAborted);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await clients.CreateClient(ApiHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Fail(McpLocalCredentialExchangeFailure.FromResponse(response));

            var result = await McpLocalCredentialExchangeResponse
                .ReadJsonAsync<McpLocalAuthenticationResponse>(response.Content, deadline.Token).ConfigureAwait(false);
            if (result is null || string.IsNullOrWhiteSpace(result.OwnerPrincipalId) || string.IsNullOrWhiteSpace(result.CredentialId))
                return Fail(McpLocalCredentialExchangeFailure.ProtocolError);

            var identity = new ClaimsIdentity(
            [
                new Claim("netratel_principal_id", result.OwnerPrincipalId),
                new Claim("netratel_integration_credential_id", result.CredentialId),
                new Claim("auth_mode", "local_http_mcp")
            ], SchemeName);
            return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
        }
        catch (OperationCanceledException) when (Context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(McpLocalCredentialExchangeFailure.TimedOut);
        }
        catch (HttpRequestException)
        {
            return Fail(McpLocalCredentialExchangeFailure.DependencyUnavailable);
        }
        catch (IOException)
        {
            return Fail(McpLocalCredentialExchangeFailure.DependencyUnavailable);
        }
    }

    private AuthenticateResult Fail(McpLocalCredentialExchangeFailure failure)
    {
        return failure.StatusCode == System.Net.HttpStatusCode.Unauthorized
            ? AuthenticateResult.Fail(failure.Code)
            : throw new McpLocalCredentialExchangeException(failure);
    }

    private sealed record McpLocalAuthenticationResponse(string OwnerPrincipalId, string CredentialId);
}
