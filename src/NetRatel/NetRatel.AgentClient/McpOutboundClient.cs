using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;

namespace NetRatel.AgentClient;

/// <summary>
/// Selects the isolated outbound credential contract used by an MCP host.
/// </summary>
public enum NetRatelMcpOutboundTokenKind
{
    LegacyOidcApplicationPassword,
    ApiM2MClientSecret
}

/// <summary>
/// Non-secret configuration for one MCP host's outbound API identity plus the
/// credentials it obtains from the host's secret provider. The type does not
/// expose credential values through <see cref="ToString"/>.
/// </summary>
public sealed class NetRatelMcpOutboundOptions
{
    public NetRatelMcpOutboundOptions(
        NetRatelMcpTarget target,
        Uri tokenEndpoint,
        string clientId,
        string username,
        string appPassword,
        string scope,
        TimeSpan? apiTimeout = null,
        TimeSpan? tokenTimeout = null)
        : this(
            target,
            tokenEndpoint,
            clientId,
            scope,
            NetRatelMcpOutboundTokenKind.LegacyOidcApplicationPassword,
            username,
            appPassword,
            null,
            apiTimeout,
            tokenTimeout)
    {
    }

    private NetRatelMcpOutboundOptions(
        NetRatelMcpTarget target,
        Uri tokenEndpoint,
        string clientId,
        string scope,
        NetRatelMcpOutboundTokenKind tokenKind,
        string? username,
        string? appPassword,
        string? clientSecret,
        TimeSpan? apiTimeout,
        TimeSpan? tokenTimeout)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(tokenEndpoint);
        if (!tokenEndpoint.IsAbsoluteUri || (tokenEndpoint.Scheme != Uri.UriSchemeHttp && tokenEndpoint.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("MCP token endpoint must be an absolute HTTP(S) URI.", nameof(tokenEndpoint));
        }

        Target = target;
        TokenEndpoint = tokenEndpoint;
        ClientId = Require(clientId, nameof(clientId));
        Scope = Require(scope, nameof(scope));
        TokenKind = tokenKind;
        Username = tokenKind == NetRatelMcpOutboundTokenKind.LegacyOidcApplicationPassword
            ? Require(username, nameof(username))
            : string.Empty;
        AppPassword = tokenKind == NetRatelMcpOutboundTokenKind.LegacyOidcApplicationPassword
            ? Require(appPassword, nameof(appPassword))
            : string.Empty;
        ClientSecret = tokenKind == NetRatelMcpOutboundTokenKind.ApiM2MClientSecret
            ? Require(clientSecret, nameof(clientSecret))
            : null;
        ApiTimeout = ValidateTimeout(apiTimeout ?? TimeSpan.FromSeconds(60), nameof(apiTimeout));
        TokenTimeout = ValidateTimeout(tokenTimeout ?? TimeSpan.FromSeconds(30), nameof(tokenTimeout));
    }

    public NetRatelMcpTarget Target { get; }
    public Uri TokenEndpoint { get; }
    public string ClientId { get; }
    public string Username { get; }
    public string AppPassword { get; }
    public string? ClientSecret { get; }
    public string Scope { get; }
    public NetRatelMcpOutboundTokenKind TokenKind { get; }
    public TimeSpan ApiTimeout { get; }
    public TimeSpan TokenTimeout { get; }

    public override string ToString() => $"{nameof(NetRatelMcpOutboundOptions)} {{ Target = {Target.Instance}, TokenEndpoint = {TokenEndpoint} }}";

    /// <summary>
    /// Creates the API-owned service credential contract used by the V2 MCP
    /// operator routes. The caller must still ensure the token endpoint is
    /// pinned to the selected immutable API target.
    /// </summary>
    public static NetRatelMcpOutboundOptions CreateApiM2M(
        NetRatelMcpTarget target,
        Uri tokenEndpoint,
        string clientId,
        string clientSecret,
        string scope,
        TimeSpan? apiTimeout = null,
        TimeSpan? tokenTimeout = null)
        => new(
            target,
            tokenEndpoint,
            clientId,
            scope,
            NetRatelMcpOutboundTokenKind.ApiM2MClientSecret,
            null,
            null,
            clientSecret,
            apiTimeout,
            tokenTimeout);

    /// <summary>
    /// Converts credentials from the one isolated MCP configuration file while
    /// proving that its API base URI is the immutable target selected at startup.
    /// </summary>
    public static NetRatelMcpOutboundOptions FromIsolatedConfiguration(NetRatelMcpTarget target, AgentClientConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!Uri.TryCreate(configuration.ApiBaseUrl, UriKind.Absolute, out var configuredApiBaseUrl) || !Equivalent(target.ApiBaseUri, configuredApiBaseUrl))
        {
            throw new AgentClientValidationException("The isolated MCP configuration apiBaseUrl must match the selected target.");
        }

        if (!Uri.TryCreate(configuration.OidcTokenUrl, UriKind.Absolute, out var tokenEndpoint) || tokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            if (HasApiM2MConfiguration(configuration))
            {
                return CreateApiM2MFromIsolatedConfiguration(target, configuration);
            }

            throw new AgentClientValidationException("The isolated MCP configuration oidcTokenUrl must be an absolute HTTPS URI.");
        }

        if (HasApiM2MConfiguration(configuration))
        {
            return CreateApiM2MFromIsolatedConfiguration(target, configuration);
        }

        return new NetRatelMcpOutboundOptions(
            target,
            tokenEndpoint,
            Required(configuration.OidcClientId, "oidcClientId"),
            Required(configuration.OidcUsername, "oidcUsername"),
            Required(configuration.OidcAppPassword, "oidcAppPassword"),
            Required(configuration.OidcScope, "oidcScope"));
    }

    private static NetRatelMcpOutboundOptions CreateApiM2MFromIsolatedConfiguration(NetRatelMcpTarget target, AgentClientConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration.ApiM2MTokenUrl, UriKind.Absolute, out var tokenEndpoint) || tokenEndpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new AgentClientValidationException("The isolated MCP configuration apiM2MTokenUrl must be an absolute HTTPS URI.");
        }

        var expectedTokenEndpoint = new Uri(target.ApiBaseUri, "/connect/token");
        if (!Equivalent(expectedTokenEndpoint, tokenEndpoint))
        {
            throw new AgentClientValidationException("The isolated MCP configuration apiM2MTokenUrl must be the selected API target's /connect/token endpoint.");
        }

        return CreateApiM2M(
            target,
            tokenEndpoint,
            Required(configuration.ApiM2MClientId, "apiM2MClientId"),
            Required(configuration.ApiM2MClientSecret, "apiM2MClientSecret"),
            Required(configuration.ApiM2MScope, "apiM2MScope"));
    }

    private static bool HasApiM2MConfiguration(AgentClientConfiguration configuration)
        => !string.IsNullOrWhiteSpace(configuration.ApiM2MTokenUrl) ||
           !string.IsNullOrWhiteSpace(configuration.ApiM2MClientId) ||
           !string.IsNullOrWhiteSpace(configuration.ApiM2MClientSecret) ||
           !string.IsNullOrWhiteSpace(configuration.ApiM2MScope);

    private static string Require(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new ArgumentException("A non-empty value is required.", name);

    private static string Required(string? value, string name) => !string.IsNullOrWhiteSpace(value)
        ? value
        : throw new AgentClientValidationException($"The isolated MCP configuration {name} is required.");

    private static bool Equivalent(Uri left, Uri right)
    {
        var normalizedLeft = new UriBuilder(left) { Scheme = left.Scheme.ToLowerInvariant(), Host = left.Host.ToLowerInvariant(), Path = left.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri.TrimEnd('/');
        var normalizedRight = new UriBuilder(right) { Scheme = right.Scheme.ToLowerInvariant(), Host = right.Host.ToLowerInvariant(), Path = right.AbsolutePath.TrimEnd('/') }.Uri.AbsoluteUri.TrimEnd('/');
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.Ordinal);
    }

    private static TimeSpan ValidateTimeout(TimeSpan timeout, string name) => timeout is { TotalSeconds: >= 1 and <= 120 }
        ? timeout
        : throw new ArgumentOutOfRangeException(name, "Timeout must be between one and 120 seconds.");
}

public interface INetRatelMcpOutboundClient
{
    NetRatelMcpTarget Target { get; }
    Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default);
    Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// Per-host app-token cache. A cache is registered as a singleton only inside the
/// host service provider, preventing tokens from crossing configured MCP targets.
/// </summary>
public sealed class NetRatelMcpAccessTokenCache
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public async Task<string> GetAsync(Func<CancellationToken, Task<NetRatelMcpAccessToken>> mintAsync, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(mintAsync);
        if (IsUsable()) return _accessToken!;

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsUsable()) return _accessToken!;
            var token = await mintAsync(cancellationToken).ConfigureAwait(false);
            _accessToken = token.Value;
            _expiresAt = token.ExpiresAt;
            return token.Value;
        }
        finally
        {
            _gate.Release();
        }
    }

    private bool IsUsable() => !string.IsNullOrWhiteSpace(_accessToken) && _expiresAt > DateTimeOffset.UtcNow.AddSeconds(30);
}

public sealed record NetRatelMcpAccessToken(string Value, DateTimeOffset ExpiresAt);

public sealed class NetRatelMcpOutboundClient(
    IHttpClientFactory httpClientFactory,
    NetRatelMcpOutboundOptions options,
    NetRatelMcpAccessTokenCache accessTokenCache,
    IMcpOperatorDelegationContext delegationContext) : INetRatelMcpOutboundClient
{
    public const string ApiHttpClientName = "NetRatel.Mcp.Api";
    public const string TokenHttpClientName = "NetRatel.Mcp.Token";

    private readonly IHttpClientFactory _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
    private readonly NetRatelMcpOutboundOptions _options = options ?? throw new ArgumentNullException(nameof(options));
    private readonly NetRatelMcpAccessTokenCache _accessTokenCache = accessTokenCache ?? throw new ArgumentNullException(nameof(accessTokenCache));
    private readonly IMcpOperatorDelegationContext _delegationContext = delegationContext ?? throw new ArgumentNullException(nameof(delegationContext));

    public NetRatelMcpTarget Target => _options.Target;

    public Task<JsonNode?> GetAsync(string path, CancellationToken cancellationToken = default)
        => SendAsync(HttpMethod.Get, path, null, cancellationToken);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(method);
        ValidateRelativeApiPath(path);

        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(cancellationToken).ConfigureAwait(false));
        if (_delegationContext.CurrentAssertion is { Length: > 0 } delegationAssertion)
            request.Headers.TryAddWithoutValidation(McpOperatorDelegationOptions.HeaderName, delegationAssertion);

        var client = _httpClientFactory.CreateClient(ApiHttpClientName);
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new AgentClientRemoteException(
                "remote_request_failed",
                $"NetRatel API request failed with HTTP {(int)response.StatusCode}.",
                (int)response.StatusCode,
                null,
                AgentClientRemoteError.ExtractSafeCode(errorBody));
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(responseBody)) return null;
        try
        {
            return JsonNode.Parse(responseBody);
        }
        catch (JsonException)
        {
            throw new AgentClientRemoteException("remote_response_invalid", "NetRatel API returned an invalid JSON response.", (int)response.StatusCode, null);
        }
    }

    private Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
        => _accessTokenCache.GetAsync(MintAccessTokenAsync, cancellationToken);

    private async Task<NetRatelMcpAccessToken> MintAccessTokenAsync(CancellationToken cancellationToken)
    {
        var client = _httpClientFactory.CreateClient(TokenHttpClientName);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = _options.ClientId,
            ["scope"] = _options.Scope
        };
        if (_options.TokenKind == NetRatelMcpOutboundTokenKind.ApiM2MClientSecret)
        {
            form["client_secret"] = _options.ClientSecret!;
        }
        else
        {
            form["username"] = _options.Username;
            form["password"] = _options.AppPassword;
        }

        using var content = new FormUrlEncodedContent(form);
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint) { Content = content };
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AgentClientRemoteException("auth_token_failed", $"NetRatel app-token request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, null);
        }

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var value = document.RootElement.GetProperty("access_token").GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new AgentClientValidationException("NetRatel app-token response did not include an access token.");
            }

            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? Math.Max(seconds, 60)
                : 300;
            return new NetRatelMcpAccessToken(value, DateTimeOffset.UtcNow.AddSeconds(expiresIn));
        }
        catch (JsonException)
        {
            throw new AgentClientRemoteException("auth_token_invalid", "NetRatel app-token response was invalid.", (int)response.StatusCode, null);
        }
    }

    private static void ValidateRelativeApiPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !path.StartsWith("/", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
        {
            throw new AgentClientValidationException("MCP outbound API paths must be root-relative paths.");
        }
    }
}

public static class NetRatelMcpOutboundServiceCollectionExtensions
{
    public static IServiceCollection AddNetRatelMcpOutboundClient(this IServiceCollection services, NetRatelMcpOutboundOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(INetRatelMcpOutboundClient)))
        {
            throw new InvalidOperationException("Only one NetRatel MCP outbound client may be registered in a service provider.");
        }

        services.AddSingleton(options);
        services.AddSingleton<NetRatelMcpAccessTokenCache>();
        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IMcpOperatorDelegationContext)))
            services.AddSingleton<IMcpOperatorDelegationContext, McpOperatorDelegationContext>();
        services.AddHttpClient(NetRatelMcpOutboundClient.ApiHttpClientName, client =>
        {
            client.BaseAddress = options.Target.ApiBaseUri;
            client.Timeout = options.ApiTimeout;
        });
        services.AddHttpClient(NetRatelMcpOutboundClient.TokenHttpClientName, client => client.Timeout = options.TokenTimeout);
        services.AddSingleton<INetRatelMcpOutboundClient, NetRatelMcpOutboundClient>();
        services.AddSingleton<IMcpOperatorTaskV2Client, McpOperatorTaskV2Client>();
        services.AddSingleton<IMcpOperatorRequestV2Client, McpOperatorRequestV2Client>();
        services.AddSingleton<IMcpOperatorAccessV2Client, McpOperatorAccessV2Client>();
        services.AddSingleton<IMcpOperatorPolicyV2Client, McpOperatorPolicyV2Client>();
        services.AddSingleton<IMcpOperatorTenantV2Client, McpOperatorTenantV2Client>();
        services.AddSingleton<IMcpOperatorOnboardingV2Client, McpOperatorOnboardingV2Client>();
        services.AddSingleton<IMcpOperatorClientV2Client, McpOperatorClientV2Client>();
        services.AddSingleton<IMcpOperatorCommandV2Client, McpOperatorCommandV2Client>();
        services.AddSingleton<IMcpOperatorJobV2Client, McpOperatorJobV2Client>();
        services.AddSingleton<IMcpOperatorObservabilityV2Client, McpOperatorObservabilityV2Client>();
        services.AddSingleton<IMcpOperatorScriptV2Client, McpOperatorScriptV2Client>();
        services.AddSingleton<IMcpOperatorFileV2Client, McpOperatorFileV2Client>();
        services.AddSingleton<IMcpOperatorTerminalV2Client, McpOperatorTerminalV2Client>();
        services.AddSingleton<IMcpOperatorNotificationV2Client, McpOperatorNotificationV2Client>();
        services.AddSingleton<IMcpOperatorEventV2Client, McpOperatorEventV2Client>();
        services.AddSingleton<IMcpOperatorConnectivityV2Client, McpOperatorConnectivityV2Client>();
        return services;
    }
}
