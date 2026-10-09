using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.Web.Services.Pairing;

public interface IPairingApiClient
{
    Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default);
    Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default);
    Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default);
    Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default);
    Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default);
    Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default);
    Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default);
}

/// <summary>Human authenticated administration. Secrets stay in the backend and writes are never replayed by HTTP middleware.</summary>
public sealed class PairingApiClient(IHttpClientFactory clients) : IPairingApiClient
{
    private const string Root = PairingProtocol.AdminRoute;
    private const int ResponseLimit = 131072;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<PairingConnectionDto[]> ListAsync(CancellationToken ct = default) =>
        SendAsync<PairingConnectionDto[]>(HttpMethod.Get, Root, null, ct);
    public Task<PairingCodeResponse> GenerateCodeAsync(CancellationToken ct = default) =>
        SendAsync<PairingCodeResponse>(HttpMethod.Post, Root + "/code", new { }, ct);
    public Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, CancellationToken ct = default) =>
        SendAsync<PairingConnectionDto>(HttpMethod.Post, Root + "/pair", request, ct);
    public Task<PairingDirectories> DirectoryAsync(string pairId, CancellationToken ct = default) =>
        SendAsync<PairingDirectories>(HttpMethod.Get, PairPath(pairId) + "/directory", null, ct);
    public Task<PairingConnectionDto> SaveAsync(PairingMapping mapping, CancellationToken ct = default) =>
        SendAsync<PairingConnectionDto>(HttpMethod.Put, PairPath(mapping.PairId) + $"/mappings/{mapping.Id:D}", mapping, ct);
    public Task<PairingTestResult> TestAsync(PairingConnectionDto connection, CancellationToken ct = default) =>
        SendAsync<PairingTestResult>(HttpMethod.Post, MappingPath(connection), new { }, ct, "/test");

    public async Task DeleteAsync(PairingConnectionDto connection, CancellationToken ct = default)
    {
        using var api = clients.CreateClient("PairingApi");
        using var budget = Budget(ct);
        using var request = new HttpRequestMessage(HttpMethod.Delete,
            connection.Mapping is null ? PairPath(connection.PairId) : MappingPath(connection));
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        if (response.StatusCode != HttpStatusCode.NotFound) await CheckAsync(response, budget.Token);
    }

    private async Task<T> SendAsync<T>(HttpMethod method, string path, object? body, CancellationToken ct, string suffix = "")
    {
        using var api = clients.CreateClient("PairingApi");
        using var budget = Budget(ct);
        using var request = new HttpRequestMessage(method, path + suffix);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: Json);
        using var response = await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        await CheckAsync(response, budget.Token);
        return await ReadAsync<T>(response, budget.Token);
    }

    private static string PairPath(string pairId) => Root + "/" + Uri.EscapeDataString(pairId);
    private static string MappingPath(PairingConnectionDto connection) => connection.Mapping is { } mapping
        ? PairPath(connection.PairId) + $"/mappings/{mapping.Id:D}"
        : throw new PairingApiException("mapping-required", "Save the tenant mapping before testing this connection.");
    private static CancellationTokenSource Budget(CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(45));
        return budget;
    }

    private static async Task CheckAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        string? code = null, reference = null;
        try
        {
            using var problem = await ReadAsync<JsonDocument>(response, ct);
            if (problem.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? Field(string name) => problem.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
                code = Field("code");
                reference = Field("correlationId") ?? Field("traceId");
                if (code is null && problem.RootElement.TryGetProperty("extensions", out var extensions) && extensions.ValueKind == JsonValueKind.Object &&
                    extensions.TryGetProperty("code", out var extensionCode) && extensionCode.ValueKind == JsonValueKind.String)
                    code = extensionCode.GetString();
            }
        }
        catch (Exception e) when (e is JsonException or InvalidDataException) { }
        throw new PairingApiException(code ?? $"http-{(int)response.StatusCode}", FailureText(code, response.StatusCode), reference);
    }

    private static string FailureText(string? code, HttpStatusCode status) => code switch
    {
        "invalid-code" or "pairing-code-invalid" or "code-expired" => "The pairing code is invalid, expired or already used. Generate a new code on the other system.",
        "invalid-address" or "address-invalid" or "invalid-origin" => "Enter a valid HTTP or HTTPS system address, including its port when needed.",
        "destination-blocked" or "unsafe-address" or "target-blocked" or "address-blocked" => "The address resolves to a blocked destination. Use the intended NetRatel or RatelDesk system address.",
        "metadata-mismatch" or "invalid-peer" or "peer-identity-mismatch" or "peer-origin-mismatch" or "peer-alias-unverified" or "peer-proof-invalid" or "caller-proof-invalid" or "caller-identity-mismatch" => "The system identity could not be verified. Check the address and its Web/API configuration.",
        "peer-incompatible" or "same-installation" => "Enter the other product's compatible installation address. NetRatel must pair with RatelDesk.",
        "peer-identity-changed" => "The other system's signing identity changed. Restore its original protected signing keys before pairing again.",
        "public-address-unavailable" => "Configure this installation's public Web and API addresses before pairing.",
        "producer-unavailable" or "producer-identity-drift" => "Restore the installation's preserved Flow producer identity before pairing.",
        "installation-identity-invalid" or "installation-identity-drift" => "Restore this installation's immutable installation identity before pairing.",
        "operation-required" or "exchange-invalid" or "pairing-retry-unavailable" => "This pairing operation can no longer be retried. Generate a fresh code on the other system and start Create Connection again.",
        "exchange-response-invalid" or "peer-response-invalid" or "peer-response-too-large" => "The other system returned an invalid response. Check both systems run compatible pairing releases and retry this operation.",
        "pair-incomplete" => "Pairing has not completed. Retry Pair & connect with the same address and code before choosing the mapping.",
        "pairing-authentication-required" or "pairing-revoked" => "System pairing access has been revoked. Generate a fresh code and pair the systems again.",
        "administrator-required" or "integration-management-required" => "Sign in with a current administrator account permitted to manage integrations.",
        "administrator-unavailable" => "The administrator who authorized this pairing is no longer active. Pair again with a current authorized administrator.",
        "automation-catalog-unavailable" => "Run automation needs current job definitions with active agents owned by the selected NetRatel tenant. Add the required jobs or leave Run automation disabled.",
        "tls-failed" => "The remote certificate could not be verified. Correct the certificate or use its valid DNS name.",
        "mapping-required" => "Save the tenant mapping before testing this connection.",
        "invalid-mapping" or "mapping-invalid" or "invalid-configuration" => "Choose an authorized NetRatel tenant and RatelDesk organization, a customer for incidents, a name and at least one capability.",
        "customer-required" or "invalid-customer" => "Choose an authorized RatelDesk customer belonging to the selected organization.",
        "mapping-conflict" or "revision-conflict" => "This connection changed. Reopen its current configuration before saving your changes.",
        "mapping-deleted" or "pair-deleted" or "pair-not-found" or "connection-not-found" or "pair-unavailable" => "This connection was removed. Refresh the list before proceeding.",
        "authority-changed" or "access-denied" or "not-authorized" or "mapping-not-authorized" => "Your current access no longer permits this operation. Review the selected tenant, organization and customer with an administrator.",
        "peer-unavailable" or "connection-unavailable" => "The other system could not be reached. Check its address and availability, then retry this operation.",
        _ => status switch
        {
            HttpStatusCode.BadRequest => "The request was rejected. Review the address, code or selected configuration and try again.",
            HttpStatusCode.Unauthorized => "Your sign-in is no longer valid. Sign in again to continue.",
            HttpStatusCode.Forbidden => "Your current access does not permit this operation. Ask an administrator to review the selected resources.",
            HttpStatusCode.TooManyRequests => "Too many pairing requests. Wait a moment and try again.",
            HttpStatusCode.NotFound => "This connection or selected resource is no longer available. Refresh its current configuration.",
            HttpStatusCode.Conflict => "This connection changed while the request was running. Reopen its current configuration and retry.",
            HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout => "The other system could not complete the request. Check its availability and retry this operation.",
            _ => $"The operation failed (HTTP {(int)status}). Your connection and draft remain available."
        }
    };

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > ResponseLimit) throw new InvalidDataException("Pairing response exceeded its size limit.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[ResponseLimit + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > ResponseLimit) throw new InvalidDataException("Pairing response exceeded its size limit.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), Json) ?? throw new JsonException("Pairing returned an empty response.");
    }
}

public sealed class PairingApiException(string code, string message, string? reference = null) : Exception(message)
{
    public string Code { get; } = Regex.IsMatch(code, "^[A-Za-z0-9_-]{1,80}$") ? code : "request-failed";
    public string Reference { get; } = reference is not null && Regex.IsMatch(reference, "^[A-Za-z0-9_.:-]{1,100}$")
        ? reference : Guid.NewGuid().ToString("N");
}
