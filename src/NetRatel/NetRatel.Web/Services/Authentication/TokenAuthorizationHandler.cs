using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace NetRatel.Web.Services.Authentication;

public class TokenAuthorizationHandler : DelegatingHandler
{
    private readonly ITokenService _tokenService;
    private readonly ILogger<TokenAuthorizationHandler> _logger;
    private static readonly JwtSecurityTokenHandler TokenReader = new();

    public TokenAuthorizationHandler(ITokenService tokenService, ILogger<TokenAuthorizationHandler> logger)
    {
        _tokenService = tokenService;
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var token = await _tokenService.GetValidAccessTokenAsync();

            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ReauthRequiredException("No bearer token available for API call.");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            _logger.LogInformation("Attached bearer token for {Method} {Uri}", request.Method, request.RequestUri);

            return await base.SendAsync(request, ct);
        }
        catch (TaskCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Timed out calling {Method} {Uri}", request.Method, request.RequestUri);
            return new HttpResponseMessage(System.Net.HttpStatusCode.GatewayTimeout)
            {
                RequestMessage = request,
                ReasonPhrase = "Upstream API timeout"
            };
        }
    }
}
