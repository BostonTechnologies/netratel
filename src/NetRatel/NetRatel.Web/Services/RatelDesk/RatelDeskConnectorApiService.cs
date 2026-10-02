using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Web.Services.RatelDesk;

public sealed class RatelDeskConnectorApiService(IHttpClientFactory clients) : IRatelDeskConnectorApiService
{
    public const string ClientName = "RatelDeskConnectorApi";
    public const int MaximumResponseBytes = 1_048_576; // Includes a bounded tenant list of at most 256 connectors.
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public Task<IReadOnlyList<RatelDeskConnectorTenantDto>> GetTenantsAsync(CancellationToken ct) =>
        ReadListAsync<RatelDeskConnectorTenantDto>("/api/v2/connectors/rateldesk/tenants", ct);
    public Task<IReadOnlyList<RatelDeskConnectorDto>> ListAsync(int tenantId, CancellationToken ct) =>
        ReadListAsync<RatelDeskConnectorDto>(Path(tenantId), ct);
    public Task<RatelDeskConnectorDto> SaveAsync(int tenantId, Guid id, SaveRatelDeskConnectorRequest request, CancellationToken ct) =>
        SendAsync<RatelDeskConnectorDto>(HttpMethod.Put, Path(tenantId, id), request, ct);
    public Task<RatelDeskConnectorDto> RotateAsync(int tenantId, Guid id, RotateRatelDeskConnectorCredentialRequest request, CancellationToken ct) =>
        SendAsync<RatelDeskConnectorDto>(HttpMethod.Post, Path(tenantId, id) + "/credential", request, ct);
    public Task<RatelDeskConnectionTestResult> TestAsync(int tenantId, Guid id, CancellationToken ct) =>
        SendAsync<RatelDeskConnectionTestResult>(HttpMethod.Post, Path(tenantId, id) + "/connection-test", null, ct);
    public Task<RatelDeskDryRunResult> DryRunAsync(int tenantId, Guid id, RatelDeskDryRunRequest request, CancellationToken ct) =>
        SendAsync<RatelDeskDryRunResult>(HttpMethod.Post, Path(tenantId, id) + "/dry-run", request, ct);

    private async Task<IReadOnlyList<T>> ReadListAsync<T>(string path, CancellationToken ct) =>
        await SendAsync<T[]>(HttpMethod.Get, path, null, ct).ConfigureAwait(false);
    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var token = deadline.Token;
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) request.Headers.Add("X-NetRatel-Account-Request", "1");
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType());
        using var client = clients.CreateClient(ClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var code = "connector-busy";
            try
            {
                using var error = JsonDocument.Parse(await ReadBoundedAsync(response, token).ConfigureAwait(false));
                if (error.RootElement.ValueKind == JsonValueKind.Object && error.RootElement.TryGetProperty("code", out var value) &&
                    value.ValueKind == JsonValueKind.String && value.GetString() == "connector-capacity-exhausted")
                    code = "connector-capacity-exhausted";
            }
            catch (JsonException) { }
            throw new RatelDeskConnectorApiException(code);
        }
        if (!response.IsSuccessStatusCode)
            throw new RatelDeskConnectorApiException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "access-denied",
                HttpStatusCode.Conflict => "connector-conflict",
                HttpStatusCode.BadRequest => "invalid-connector-request",
                HttpStatusCode.NotFound => "connector-not-found",
                HttpStatusCode.TooManyRequests => "connector-busy",
                _ => "connector-unavailable"
            });
        try { return JsonSerializer.Deserialize<T>(await ReadBoundedAsync(response, token).ConfigureAwait(false), JsonOptions) ?? throw new RatelDeskConnectorApiException("invalid-response"); }
        catch (JsonException) { throw new RatelDeskConnectorApiException("invalid-response"); }
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes) throw new RatelDeskConnectorApiException("response-too-large");
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var count = await stream.ReadAsync(buffer, token).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaximumResponseBytes) throw new RatelDeskConnectorApiException("response-too-large");
            bytes.Write(buffer, 0, count);
        }
        return bytes.ToArray();
    }
    private static string Path(int tenantId, Guid? id = null)
    {
        if (tenantId <= 0 || id == Guid.Empty) throw new ArgumentException("invalid-connector-scope");
        return $"/api/v2/tenants/{tenantId}/connectors/rateldesk" + (id is { } value ? $"/{value:D}" : "");
    }
}
