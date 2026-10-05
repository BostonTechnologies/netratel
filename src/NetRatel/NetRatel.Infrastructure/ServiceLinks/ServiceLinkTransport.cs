using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using NetRatel.Infrastructure.ServiceLinks.Network;
using NetRatel.Shared.ServiceLinks;
using Microsoft.Extensions.Options;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed class ServiceLinkProtocolException(int statusCode, string code, string message) : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

public sealed record ServiceLinkAccessToken(string AccessToken, int ExpiresIn);

/// <summary>Every exchange uses the approved immutable destination and the existing connection-time DNS policy.</summary>
public sealed class ServiceLinkTransport(HttpClient client, IOptions<ServiceLinkOptions> options, IOptionsMonitor<ServiceLinkOptions>? currentOptions = null)
{
    private ServiceLinkOptions Settings => currentOptions?.CurrentValue ?? options.Value;

    public async Task<T> GetAsync<T>(string endpoint, CancellationToken ct, string? bearer = null, string? sourceInstanceId = null)
    {
        using var message = Message(HttpMethod.Get, endpoint, bearer);
        if (sourceInstanceId is not null)
        {
            if (!Guid.TryParseExact(sourceInstanceId, "D", out var source) || source.ToString("D") != sourceInstanceId)
                throw new ServiceLinkProtocolException(400, "invalid-source-identity", "The producer identity must be canonical.");
            message.Headers.Add("X-NetRatel-Source-Instance", sourceInstanceId);
        }
        return await SendAsync<T>(message, ct);
    }

    public async Task<T> PostAsync<T>(string endpoint, object body, CancellationToken ct, string? bearer = null)
    {
        using var message = Message(HttpMethod.Post, endpoint, bearer);
        message.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        return await SendAsync<T>(message, ct);
    }

    public async Task<int> PostStatusAsync(string endpoint, object body, string correlationId, string bearer, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(correlationId) || correlationId.Length > 256 || correlationId.Any(char.IsControl))
            throw new ServiceLinkProtocolException(400, "invalid-correlation", "A bounded correlation identifier is required.");
        using var message = Message(HttpMethod.Post, endpoint, bearer);
        message.Headers.Add("X-Correlation-Id", correlationId);
        message.Content = new StringContent(JsonSerializer.Serialize(body, new JsonSerializerOptions(JsonSerializerDefaults.Web)), Encoding.UTF8, "application/json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400)
            throw new ServiceLinkProtocolException(422, "peer-redirect-rejected", "The peer redirected an approved callback endpoint.");
        if (response.Content.Headers.ContentLength > Settings.MaximumPayloadBytes)
            throw new ServiceLinkProtocolException(422, "peer-response-too-large", "The peer callback response exceeded its configured limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var buffer = new byte[4096];
        var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer, timeout.Token);
            if (count == 0) break;
            total = checked(total + count);
            if (total > Settings.MaximumPayloadBytes)
                throw new ServiceLinkProtocolException(422, "peer-response-too-large", "The peer callback response exceeded its configured limit.");
        }
        return (int)response.StatusCode;
    }

    public async Task<string> TokenAsync(ServiceDirectionalCredential credential, string scopes, CancellationToken ct) =>
        (await AcquireTokenAsync(credential, scopes, ct)).AccessToken;

    public async Task<ServiceLinkAccessToken> AcquireTokenAsync(ServiceDirectionalCredential credential, string scopes, CancellationToken ct)
    {
        if (credential.TokenEndpointAuthMethod != "client_secret_post") throw new ServiceLinkProtocolException(422, "unsupported-client-authentication", "The approved peer authentication method is unsupported.");
        using var message = Message(HttpMethod.Post, credential.TokenEndpoint);
        message.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials", ["client_id"] = credential.ClientId,
            ["client_secret"] = credential.ClientSecret, ["scope"] = scopes
        });
        using var result = await ReadTokenResponseAsync(message, ct);
        var root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("access_token", out var tokenValue) || tokenValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("token_type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String ||
            !root.TryGetProperty("expires_in", out var expiryValue) || expiryValue.ValueKind != JsonValueKind.Number ||
            !expiryValue.TryGetInt32(out var expiresIn) || expiresIn is < 1 or > 900)
            throw new ServiceLinkProtocolException(422, "invalid-token-response", "The peer returned an invalid service token response.");
        var token = tokenValue.GetString();
        if (string.IsNullOrEmpty(token) || token.Length > 32768 || typeValue.GetString()?.Equals("Bearer", StringComparison.OrdinalIgnoreCase) != true)
            throw new ServiceLinkProtocolException(422, "invalid-token-response", "The peer returned an invalid service token response.");
        return new(token, expiresIn);
    }

    private async Task<JsonDocument> ReadTokenResponseAsync(HttpRequestMessage message, CancellationToken ct)
    {
        try { return await SendAsync<JsonDocument>(message, ct); }
        catch (Exception error) when (error is JsonException or DecoderFallbackException)
        {
            throw new ServiceLinkProtocolException(422, "invalid-token-response", "The peer returned an invalid service token response.");
        }
    }

    private HttpRequestMessage Message(HttpMethod method, string endpoint, string? bearer = null)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri)) throw new ServiceLinkProtocolException(400, "invalid-endpoint", "The endpoint is not absolute.");
        // The central typed-options boundary normalizes both deployment flags.
        // Read its current snapshot for each request, including opt-in removal.
        var allowPrivateHttp = Settings.AllowPrivateHttp;
        ServiceLinkEndpointPolicy.Validate(uri, "Service link endpoint", allowPrivateHttp);
        var message = new HttpRequestMessage(method, uri);
        message.Options.Set(ServiceLinkSafeHttpMessageHandler.AllowPrivateHttpOption, allowPrivateHttp);
        if (bearer is not null) message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return message;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if ((int)response.StatusCode is >= 300 and < 400) throw new ServiceLinkProtocolException(422, "peer-redirect-rejected", "The peer redirected an approved endpoint.");
        if (!response.IsSuccessStatusCode)
        {
            if (message.Method == HttpMethod.Get && message.RequestUri!.AbsolutePath.EndsWith(ServiceLinkContract.MetadataPath, StringComparison.Ordinal) && (int)response.StatusCode is 404 or 405)
                throw new ServiceLinkProtocolException(422, "upgrade-required", "The peer does not serve the required service link discovery contract. Manual configuration remains available.");
            throw new ServiceLinkProtocolException(502, "peer-operation-failed", $"The approved peer operation failed with status {(int)response.StatusCode}.");
        }
        if (response.Content.Headers.ContentLength > Settings.MaximumPayloadBytes) throw new ServiceLinkProtocolException(422, "peer-response-too-large", "The peer response exceeded its configured limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var memory = new MemoryStream();
        var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (memory.Length + count > Settings.MaximumPayloadBytes) throw new ServiceLinkProtocolException(422, "peer-response-too-large", "The peer response exceeded its configured limit.");
            memory.Write(buffer, 0, count);
        }
        var json = new UTF8Encoding(false, true).GetString(memory.ToArray());
        if (typeof(T) == typeof(JsonDocument)) return (T)(object)JsonDocument.Parse(ServiceLinkCanonicalJson.Canonicalize(json));
        return ServiceLinkCanonicalJson.Deserialize<T>(json);
    }
}
