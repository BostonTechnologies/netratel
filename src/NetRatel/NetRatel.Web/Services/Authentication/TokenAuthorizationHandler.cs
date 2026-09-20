using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Linq;

namespace NetRatel.Web.Services.Authentication;

public class TokenAuthorizationHandler : DelegatingHandler
{
    private readonly ITokenService _tokenService;
    private readonly ILogger<TokenAuthorizationHandler> _logger;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly string _localCookieName;
    private static readonly JwtSecurityTokenHandler TokenReader = new();

    public TokenAuthorizationHandler(
        ITokenService tokenService,
        ILogger<TokenAuthorizationHandler> logger,
        IHttpContextAccessor httpContextAccessor,
        IConfiguration configuration)
    {
        _tokenService = tokenService;
        _logger = logger;
        _httpContextAccessor = httpContextAccessor;
        _localCookieName = configuration["Authentication:Local:CookieName"]?.Trim() is { Length: > 0 } configuredCookieName
            ? configuredCookieName
            : "NetRatel.Local";
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            var context = _httpContextAccessor.HttpContext;
            var localCookie = context?.User.HasClaim("auth_mode", "local") == true
                ? context.Request.Cookies[_localCookieName]
                : null;
            if (!string.IsNullOrWhiteSpace(localCookie))
            {
                // Internal BFF hop only. The API validates this protected
                // ticket and its current local-account state on every call.
                request.Headers.TryAddWithoutValidation("Cookie", $"{_localCookieName}={localCookie}");
                return await base.SendAsync(request, ct);
            }

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
