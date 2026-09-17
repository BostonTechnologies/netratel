using System.Collections.Concurrent;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace NetRatel.Web.Services.Authentication;

/// <summary>
/// Keeps OIDC token state in the encrypted authentication ticket. A short-lived
/// access-token cache is retained only for an already-authenticated Blazor circuit
/// with no request context; refresh tokens are never process-local.
/// </summary>
public sealed class TokenService : ITokenService
{
    private const string RefreshedAccessTokenItemKey = "NetRatel.RefreshedAccessToken";
    public const string SessionRefreshedItemKey = "NetRatel.SessionRefreshed";
    private const string ReauthMessage = "Your sign-in session could not be renewed.";
    private static readonly TimeSpan TokenRefreshBuffer = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromHours(8);
    private static readonly TimeSpan RecentRefreshLifetime = TimeSpan.FromSeconds(30);
    private const int RefreshLockStripeCount = 64;
    private const int MaxRecentRefreshResults = 1024;
    private static readonly SemaphoreSlim[] RefreshLocks = Enumerable.Range(0, RefreshLockStripeCount)
        .Select(_ => new SemaphoreSlim(1, 1))
        .ToArray();
    private static readonly ConcurrentDictionary<string, CachedRefreshResult> RecentRefreshResults = new(StringComparer.Ordinal);

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ISystemTokenService _systemTokenService;
    private readonly ILogger<TokenService> _logger;
    private readonly object _circuitTokenLock = new();
    private string? _circuitAccessToken;
    private DateTimeOffset? _circuitAccessTokenExpiresAt;

    public TokenService(
        IHttpContextAccessor httpContextAccessor,
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ISystemTokenService systemTokenService,
        ILogger<TokenService> logger)
    {
        _httpContextAccessor = httpContextAccessor;
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _systemTokenService = systemTokenService;
        _logger = logger;
    }

    public async Task<string> GetValidAccessTokenAsync()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            var circuitToken = GetCircuitAccessToken();
            if (!string.IsNullOrWhiteSpace(circuitToken))
            {
                return circuitToken;
            }

            throw new ReauthRequiredException(ReauthMessage);
        }

        if (context.Items.TryGetValue(RefreshedAccessTokenItemKey, out var refreshedAccessToken)
            && refreshedAccessToken is string refreshedAccessTokenValue
            && !string.IsNullOrWhiteSpace(refreshedAccessTokenValue))
        {
            return refreshedAccessTokenValue;
        }

        var authentication = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme).ConfigureAwait(false);
        if (!authentication.Succeeded || authentication.Principal?.Identity?.IsAuthenticated != true)
        {
            throw new ReauthRequiredException(ReauthMessage);
        }

        if (!await TryRefreshSessionAsync(context, authentication, context.RequestAborted).ConfigureAwait(false))
        {
            throw new ReauthRequiredException(ReauthMessage);
        }

        if (context.Items.TryGetValue(RefreshedAccessTokenItemKey, out var refreshed)
            && refreshed is string refreshedTokenValue
            && !string.IsNullOrWhiteSpace(refreshedTokenValue))
        {
            return refreshedTokenValue;
        }

        var accessToken = authentication.Properties?.GetTokenValue("access_token");
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            throw new ReauthRequiredException(ReauthMessage);
        }

        CacheCircuitAccessToken(accessToken, ParseExpiration(authentication.Properties?.GetTokenValue("expires_at")));
        return accessToken;
    }

    public async Task<bool> TryRefreshSessionAsync(
        HttpContext context,
        AuthenticateResult authentication,
        CancellationToken cancellationToken = default)
    {
        if (authentication.Principal is not { Identity.IsAuthenticated: true } principal)
        {
            return false;
        }

        var properties = authentication.Properties ?? new AuthenticationProperties();
        if (authentication.Properties is null)
        {
            authentication = AuthenticateResult.Success(new AuthenticationTicket(
                authentication.Principal,
                properties,
                CookieAuthenticationDefaults.AuthenticationScheme));
        }

        if (IsDevelopmentSession(principal))
        {
            return await RefreshDevelopmentSessionAsync(context, authentication).ConfigureAwait(false);
        }

        if (IsAiAgentSession(principal))
        {
            return ValidateNonRefreshableSession(properties);
        }

        var accessToken = properties.GetTokenValue("access_token");
        var expiresAt = ParseExpiration(properties.GetTokenValue("expires_at"));
        if (string.IsNullOrWhiteSpace(accessToken) || !expiresAt.HasValue)
        {
            _logger.LogWarning(
                "OIDC session is missing valid token state for {User}. AccessTokenPresent={AccessTokenPresent} ExpiresAtPresent={ExpiresAtPresent}",
                GetUserLabel(principal),
                !string.IsNullOrWhiteSpace(accessToken),
                expiresAt.HasValue);
            return false;
        }

        if (DateTimeOffset.UtcNow < expiresAt.Value.Subtract(TokenRefreshBuffer))
        {
            CacheCircuitAccessToken(accessToken, expiresAt);
            return true;
        }

        var refreshToken = properties.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            _logger.LogWarning(
                "OIDC session cannot refresh because refresh_token is missing for {User}. TokenExpiresAt={TokenExpiresAt}",
                GetUserLabel(principal),
                expiresAt);
            return false;
        }

        var refreshed = await RefreshOidcWithGuardAsync(
            context,
            authentication,
            accessToken,
            refreshToken,
            expiresAt.Value,
            cancellationToken).ConfigureAwait(false);
        if (refreshed is null)
        {
            return false;
        }

        context.Items[RefreshedAccessTokenItemKey] = refreshed.AccessToken;
        return true;
    }

    private async Task<RefreshResult?> RefreshOidcWithGuardAsync(
        HttpContext context,
        AuthenticateResult authentication,
        string accessToken,
        string refreshToken,
        DateTimeOffset currentAccessTokenExpiresAt,
        CancellationToken cancellationToken)
    {
        var key = BuildRefreshKey(authentication.Principal!, accessToken, refreshToken);
        PruneExpiredRefreshResults();
        var gate = RefreshLocks[(StringComparer.Ordinal.GetHashCode(key) & int.MaxValue) % RefreshLocks.Length];

        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (TryGetRecentRefreshResult(key, out var recent))
            {
                await UpdateCookieTokensAsync(context, authentication, recent).ConfigureAwait(false);
                return recent;
            }

            var result = await RefreshOidcAsync(refreshToken, currentAccessTokenExpiresAt, cancellationToken).ConfigureAwait(false);
            if (result is null)
            {
                if (TryGetRecentRefreshResult(key, out recent))
                {
                    await UpdateCookieTokensAsync(context, authentication, recent).ConfigureAwait(false);
                    return recent;
                }

                return null;
            }

            RecentRefreshResults[key] = new CachedRefreshResult(result, DateTimeOffset.UtcNow.Add(RecentRefreshLifetime));
            PruneExpiredRefreshResults();
            await UpdateCookieTokensAsync(context, authentication, result).ConfigureAwait(false);
            return result;
        }
        finally
        {
            gate.Release();
            PruneExpiredRefreshResults();
        }
    }

    private async Task<RefreshResult?> RefreshOidcAsync(
        string refreshToken,
        DateTimeOffset currentAccessTokenExpiresAt,
        CancellationToken cancellationToken)
    {
        var oidcSection = _configuration.GetSection("Authentication:Oidc");
        if (!oidcSection.Exists())
            oidcSection = _configuration.GetSection("Authentication:Azure");
        if (!oidcSection.Exists())
            oidcSection = _configuration.GetSection("AzureAd");

        var authority = oidcSection["Authority"];
        var clientId = oidcSection["ClientId"];
        var clientSecret = _configuration["OIDC_CLIENT_SECRET"] ?? _configuration["AZURE_CLIENT_SECRET"] ?? oidcSection["ClientSecret"];
        var apiScope = oidcSection["ApiScope"];
        var tokenEndpoint = oidcSection["TokenEndpoint"] ?? BuildLegacyAzureTokenEndpoint(authority);
        var scope = string.Join(' ', new[] { "openid", "profile", "email", "offline_access", apiScope }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.Ordinal));

        var body = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId ?? string.Empty,
            ["refresh_token"] = refreshToken,
            ["scope"] = scope
        };
        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            body["client_secret"] = clientSecret;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(body)
        };
        using var response = await _httpClientFactory.CreateClient("TokenClient")
            .SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "OIDC refresh failed. StatusCode={StatusCode} TokenExpiresAt={TokenExpiresAt}",
                response.StatusCode,
                currentAccessTokenExpiresAt);
            return null;
        }

        var tokenResponse = await response.Content.ReadFromJsonAsync<TokenResponse>(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(tokenResponse?.AccessToken) || tokenResponse.ExpiresIn <= 0)
        {
            _logger.LogWarning("OIDC refresh returned an incomplete token response.");
            return null;
        }

        return new RefreshResult(
            tokenResponse.AccessToken,
            string.IsNullOrWhiteSpace(tokenResponse.RefreshToken) ? refreshToken : tokenResponse.RefreshToken,
            DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn));
    }

    private async Task UpdateCookieTokensAsync(
        HttpContext context,
        AuthenticateResult authentication,
        RefreshResult result)
    {
        var properties = authentication.Properties ?? new AuthenticationProperties();
        properties.UpdateTokenValue("access_token", result.AccessToken);
        properties.UpdateTokenValue("refresh_token", result.RefreshToken);
        properties.UpdateTokenValue("expires_at", result.ExpiresAt.UtcDateTime.ToString("o", CultureInfo.InvariantCulture));
        PreserveOrRestoreSessionLifetime(properties, result.ExpiresAt);

        await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                authentication.Principal!,
                properties)
            .ConfigureAwait(false);
        context.Items[SessionRefreshedItemKey] = true;
        CacheCircuitAccessToken(result.AccessToken, result.ExpiresAt);
    }

    private async Task<bool> RefreshDevelopmentSessionAsync(HttpContext context, AuthenticateResult authentication)
    {
        var accessToken = await _systemTokenService.GetTokenAsync().ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var expiresAt = TryGetTokenExpiration(accessToken) ?? DateTimeOffset.UtcNow.AddMinutes(30);
        await UpdateCookieTokensAsync(context, authentication, new RefreshResult(accessToken, null, expiresAt)).ConfigureAwait(false);
        context.Items[RefreshedAccessTokenItemKey] = accessToken;
        return true;
    }

    private static bool ValidateNonRefreshableSession(AuthenticationProperties properties) =>
        ParseExpiration(properties.GetTokenValue("expires_at")) is { } expiresAt && DateTimeOffset.UtcNow < expiresAt;

    private void PreserveOrRestoreSessionLifetime(AuthenticationProperties properties, DateTimeOffset accessTokenExpiresAt)
    {
        var now = DateTimeOffset.UtcNow;
        if (properties.ExpiresUtc is { } ticketExpiresAt
            && ticketExpiresAt > now
            && ticketExpiresAt > accessTokenExpiresAt)
        {
            return;
        }

        var configured = _configuration.GetValue<TimeSpan?>("Authentication:Cookie:ExpireTimeSpan");
        properties.ExpiresUtc = now.Add(configured.GetValueOrDefault(SessionLifetime));
        properties.IsPersistent = true;
    }

    private void CacheCircuitAccessToken(string accessToken, DateTimeOffset? expiresAt)
    {
        lock (_circuitTokenLock)
        {
            _circuitAccessToken = accessToken;
            _circuitAccessTokenExpiresAt = expiresAt;
        }
    }

    private string? GetCircuitAccessToken()
    {
        lock (_circuitTokenLock)
        {
            if (string.IsNullOrWhiteSpace(_circuitAccessToken)
                || !_circuitAccessTokenExpiresAt.HasValue
                || DateTimeOffset.UtcNow >= _circuitAccessTokenExpiresAt.Value.Subtract(TokenRefreshBuffer))
            {
                return null;
            }

            return _circuitAccessToken;
        }
    }

    private static bool IsDevelopmentSession(ClaimsPrincipal principal) =>
        principal.Claims.Any(claim =>
            string.Equals(claim.Type, "auth_mode", StringComparison.OrdinalIgnoreCase)
            && string.Equals(claim.Value, "development", StringComparison.OrdinalIgnoreCase));

    private static bool IsAiAgentSession(ClaimsPrincipal principal) =>
        principal.Claims.Any(claim =>
            (string.Equals(claim.Type, "auth_mode", StringComparison.OrdinalIgnoreCase)
             && string.Equals(claim.Value, "ai_agent", StringComparison.OrdinalIgnoreCase))
            || (string.Equals(claim.Type, "identity_provider", StringComparison.OrdinalIgnoreCase)
                && string.Equals(claim.Value, "oidc_ai_agent", StringComparison.OrdinalIgnoreCase)));

    private static DateTimeOffset? ParseExpiration(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var expiresAt)
            ? expiresAt
            : null;

    private static DateTimeOffset? TryGetTokenExpiration(string token)
    {
        try
        {
            var parsed = new JwtSecurityTokenHandler().ReadJwtToken(token);
            return parsed.ValidTo == DateTime.MinValue ? null : new DateTimeOffset(parsed.ValidTo, TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string BuildLegacyAzureTokenEndpoint(string? authority)
    {
        var trimmed = string.IsNullOrWhiteSpace(authority)
            ? "https://login.microsoftonline.com/common"
            : authority.TrimEnd('/');
        if (trimmed.EndsWith("/v2.0", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^"/v2.0".Length];
        }

        return $"{trimmed}/oauth2/v2.0/token";
    }

    private static bool TryGetRecentRefreshResult(string key, out RefreshResult result)
    {
        if (RecentRefreshResults.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
        {
            result = cached.Result;
            return true;
        }

        RecentRefreshResults.TryRemove(key, out _);
        result = default!;
        return false;
    }

    private static void PruneExpiredRefreshResults()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in RecentRefreshResults)
        {
            if (item.Value.ExpiresAt <= now)
            {
                RecentRefreshResults.TryRemove(item.Key, out _);
            }
        }

        foreach (var item in RecentRefreshResults.OrderBy(item => item.Value.ExpiresAt).Take(Math.Max(0, RecentRefreshResults.Count - MaxRecentRefreshResults)))
        {
            RecentRefreshResults.TryRemove(item.Key, out _);
        }
    }

    private static string BuildRefreshKey(ClaimsPrincipal principal, string accessToken, string refreshToken)
    {
        var subject = principal.FindFirst("sub")?.Value
            ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.Identity?.Name
            ?? "anonymous";
        var tokenHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{accessToken}:{refreshToken}")));
        return $"{subject}:{tokenHash}";
    }

    private static string GetUserLabel(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value
        ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? principal.Identity?.Name
        ?? "unknown";

    private sealed record RefreshResult(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

    private sealed record CachedRefreshResult(RefreshResult Result, DateTimeOffset ExpiresAt);
}
