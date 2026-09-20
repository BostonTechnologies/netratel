using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

public sealed class AgentClientValidationException(string message) : Exception(message);
public sealed class AgentClientRemoteException(string code, string message, int statusCode, string? responseBody, string? remoteCode = null) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
    public string? ResponseBody { get; } = responseBody;
    public string? RemoteCode { get; } = remoteCode;
}

internal static class AgentClientRemoteError
{
    public static string? ExtractSafeCode(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) || responseBody.Length > 4096)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            return SafeCode(document.RootElement) ??
                   (document.RootElement.TryGetProperty("failure", out var failure) && failure.ValueKind == JsonValueKind.Object
                       ? SafeCode(failure)
                       : null);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? SafeCode(JsonElement value)
    {
        if (!value.TryGetProperty("code", out var codeElement) || codeElement.ValueKind is not JsonValueKind.String)
            return null;

        var code = codeElement.GetString();
        return code is { Length: > 0 and <= 100 } &&
            code.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? code
            : null;
    }
}

public interface INetRatelAgentClient
{
    AgentClientConfiguration Configuration { get; }
    Task<string> GetAccessTokenAsync(CancellationToken ct = default);
    Task<JsonNode?> GetAsync(string path, bool authenticated = true, CancellationToken ct = default);
    Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, bool authenticated = true, CancellationToken ct = default);
    Task<JsonArray> GetHealthAsync(CancellationToken ct = default);
    Task<JsonNode?> GetClientsAsync(JsonObject? filters = null, CancellationToken ct = default);
    Task<JsonNode?> GetJobsAsync(JsonObject? filters = null, CancellationToken ct = default);
    Task<JsonNode?> GetJobRunsAsync(JsonObject? filters = null, CancellationToken ct = default);
    Task<JsonNode?> GetTerminalStreamAsync(string sessionId, int waitSeconds, int maxEvents, CancellationToken ct = default)
        => throw new NotSupportedException("Terminal streaming is not implemented by this agent client.");
}

public sealed class NetRatelAgentClient(AgentClientConfiguration configuration, Func<HttpMessageHandler>? handlerFactory = null) : INetRatelAgentClient
{
    private readonly Func<HttpMessageHandler>? _handlerFactory = handlerFactory;
    private readonly SemaphoreSlim _accessTokenGate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _accessTokenExpiresAt;
    public AgentClientConfiguration Configuration { get; } = configuration;

    public async Task<JsonNode?> GetAsync(string path, bool authenticated = true, CancellationToken ct = default)
        => await SendAsync(HttpMethod.Get, path, null, authenticated, ct).ConfigureAwait(false);

    public async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body = null, bool authenticated = true, CancellationToken ct = default)
    {
        var config = Configuration.Resolve();
        using var client = await CreateClientAsync(config, authenticated, ct).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json");
        using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
        return await ReadJsonAsync(response, ct).ConfigureAwait(false);
    }

    public async Task<JsonArray> GetHealthAsync(CancellationToken ct = default)
    {
        var config = Configuration.Resolve();
        var results = new JsonArray();
        // Public-dev applies the fallback authorization policy to health
        // routes. Use the same AI-agent bearer token for every probe so MCP
        // health reflects availability rather than an avoidable 401.
        foreach (var (name, path, authenticated) in new[] { ("live", "/health/live", true), ("ready", "/health/ready", true), ("auth", "/api/v1/auth/ai-agent/status", true) })
        {
            try { results.Add(new JsonObject { ["name"] = name, ["success"] = true, ["data"] = await GetAsync(path, authenticated, ct).ConfigureAwait(false) }); }
            catch (AgentClientRemoteException ex) { results.Add(new JsonObject { ["name"] = name, ["success"] = false, ["statusCode"] = ex.StatusCode, ["error"] = ex.Code }); }
        }
        return results;
    }

    public Task<JsonNode?> GetClientsAsync(JsonObject? filters = null, CancellationToken ct = default) => GetAsync(WithQuery("/api/v1/clients/", filters), true, ct);
    public Task<JsonNode?> GetJobsAsync(JsonObject? filters = null, CancellationToken ct = default) => GetAsync(WithQuery("/api/v1/jobs/", filters), true, ct);
    public Task<JsonNode?> GetJobRunsAsync(JsonObject? filters = null, CancellationToken ct = default) => GetAsync(WithQuery("/api/v1/jobruns/", filters), true, ct);

    public async Task<JsonNode?> GetTerminalStreamAsync(string sessionId, int waitSeconds, int maxEvents, CancellationToken ct = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(waitSeconds));
        var config = Configuration.Resolve();
        using var client = await CreateClientAsync(config, authenticated: true, timeout.Token).ConfigureAwait(false);
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/terminal/{Uri.EscapeDataString(sessionId)}/stream");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            throw new AgentClientRemoteException("remote_request_failed", $"NetRatel API request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, responseBody, AgentClientRemoteError.ExtractSafeCode(responseBody));
        }

        var messages = new JsonArray();
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(stream);
            while (!timeout.IsCancellationRequested && messages.Count < maxEvents)
            {
                var line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (line is null) break;
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                var payload = line[6..];
                messages.Add(JsonNode.Parse(payload));
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // A bounded stream read completes normally when its requested window elapses.
        }

        return new JsonObject
        {
            ["sessionId"] = sessionId,
            ["events"] = messages,
            ["timedOut"] = !ct.IsCancellationRequested && timeout.IsCancellationRequested,
            ["nextAction"] = "Call netratel_terminal operation=stream again to receive later terminal output."
        };
    }

    private async Task<HttpClient> CreateClientAsync(ResolvedAgentClientConfiguration config, bool authenticated, CancellationToken ct)
    {
        var client = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory(), disposeHandler: true);
        client.BaseAddress = config.ApiBaseUrl;
        client.Timeout = TimeSpan.FromSeconds(60);
        if (authenticated) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await GetAccessTokenAsync(ct).ConfigureAwait(false));
        return client;
    }

    public Task<string> GetAccessTokenAsync(CancellationToken ct = default) => GetAccessTokenAsync(Configuration.Resolve(), ct);

    private async Task<string> GetAccessTokenAsync(ResolvedAgentClientConfiguration config, CancellationToken ct)
    {
        if (config.AuthenticationMode is AgentClientAuthenticationMode.IntegrationCredential)
        {
            return config.IntegrationCredential
                ?? throw new AgentClientValidationException("An integration credential is required for integration credential mode.");
        }

        // Do not reuse an expired access token. Keep a small safety margin so a
        // request cannot begin with a token that expires while it is in flight.
        if (HasUsableAccessToken()) return _accessToken!;

        await _accessTokenGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (HasUsableAccessToken()) return _accessToken!;

            using var client = _handlerFactory is null ? new HttpClient() : new HttpClient(_handlerFactory(), disposeHandler: true);
            client.Timeout = TimeSpan.FromSeconds(30);
            var tokenUrl = config.AuthenticationMode is AgentClientAuthenticationMode.ApiM2MClientSecret
                ? config.ApiM2MTokenUrl
                : config.OidcTokenUrl;
            var tokenRequest = config.AuthenticationMode is AgentClientAuthenticationMode.ApiM2MClientSecret
                ? new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = config.ApiM2MClientId!,
                    ["client_secret"] = config.ApiM2MClientSecret!,
                    ["scope"] = config.ApiM2MScope!
                }
                : new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials",
                    ["client_id"] = config.OidcClientId,
                    ["client_secret"] = config.OidcAppPassword,
                    ["username"] = config.OidcUsername,
                    ["password"] = config.OidcAppPassword,
                    ["scope"] = config.OidcScope
                };
            using var response = await client.PostAsync(tokenUrl, new FormUrlEncodedContent(tokenRequest), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new AgentClientRemoteException("auth_token_failed", $"Oidc token mint failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, body);
            using var document = JsonDocument.Parse(body);
            _accessToken = document.RootElement.GetProperty("access_token").GetString() ?? throw new AgentClientValidationException("Oidc token response did not include access_token.");
            var expiresIn = document.RootElement.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds)
                ? Math.Max(seconds, 60)
                : 300;
            _accessTokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
            return _accessToken;
        }
        finally
        {
            _accessTokenGate.Release();
        }
    }

    private bool HasUsableAccessToken()
        => !string.IsNullOrWhiteSpace(_accessToken) && _accessTokenExpiresAt > DateTimeOffset.UtcNow.AddSeconds(30);

    private static async Task<JsonNode?> ReadJsonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new AgentClientRemoteException("remote_request_failed", $"NetRatel API request failed with HTTP {(int)response.StatusCode}.", (int)response.StatusCode, body, AgentClientRemoteError.ExtractSafeCode(body));
        return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
    }

    private static string WithQuery(string path, JsonObject? values)
    {
        if (values is null || values.Count == 0) return path;
        var query = values.Where(x => x.Value is not null).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!.ToString())}");
        return string.Join('?', path, string.Join('&', query));
    }
}
