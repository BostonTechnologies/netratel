using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using NetRatel.Shared.Contracts.RatelDesk;

namespace NetRatel.Web.Services.ServiceLinks;

/// <summary>Human-authorized service administration; mutations have no automatic replay.</summary>
public sealed class HelpdeskM2MApiClient(IHttpClientFactory clients)
{
    private const string ClientRoot = "api/v2/account/service-clients";
    private const string LinkRoot = "api/v1/admin/service-links";
    public Task<ServiceClientManagementAuthority> GetAuthorityAsync(CancellationToken ct = default) =>
        GetAsync<ServiceClientManagementAuthority>(ClientRoot + "/authority", ct);
    public Task<ServicePublicSettingsResponse> GetPublicSettingsAsync(CancellationToken ct = default) => GetAsync<ServicePublicSettingsResponse>(ClientRoot + "/settings", ct);
    public async Task<ServicePublicSettingsResponse> UpdatePublicSettingsAsync(ServicePublicSettingsUpdate request, CancellationToken ct = default)
    {
        using var api = clients.CreateClient("ServiceLinkApi");
        using var budget = RequestBudget(api, ct);
        using var response = await SendJsonAsync(api, HttpMethod.Put, ClientRoot + "/settings", request, budget.Token);
        await CheckResponseAsync(response, budget.Token);
        return await ReadAsync<ServicePublicSettingsResponse>(response, budget.Token);
    }
    public Task<ServiceClientMetadata[]> ListClientsAsync(CancellationToken ct = default) =>
        GetAsync<ServiceClientMetadata[]>(ClientRoot, ct);
    public Task<ServiceClientDeploymentMetadata[]> ListDeploymentClientsAsync(CancellationToken ct = default) =>
        GetAsync<ServiceClientDeploymentMetadata[]>(ClientRoot + "/deployment", ct);
    public Task<ServiceClientReveal> CreateAsync(ServiceClientCreateRequest request, CancellationToken ct = default) =>
        PostAsync<ServiceClientReveal>(ClientRoot, request, ct);
    public Task<ServiceClientReveal> RotateAsync(ServiceClientMetadata client, CancellationToken ct = default) =>
        PostAsync<ServiceClientReveal>($"{ClientRoot}/{client.Id:D}/rotate", new ServiceClientRotateRequest(client.CredentialRevision), ct);
    public async Task RevokeAsync(ServiceClientMetadata client, CancellationToken ct = default)
    {
        using var api = clients.CreateClient("ServiceLinkApi");
        using var budget = RequestBudget(api, ct);
        using var response = await SendJsonAsync(api, HttpMethod.Post, $"{ClientRoot}/{client.Id:D}/revoke", new ServiceClientRevokeRequest(client.Revision), budget.Token);
        await CheckResponseAsync(response, budget.Token);
    }
    public Task<ServiceLinkAdminStatus[]> ListLinksAsync(CancellationToken ct = default) => GetAsync<ServiceLinkAdminStatus[]>(LinkRoot, ct);
    public Task<ServiceLinkAdminStatus> GetAttemptAsync(string attempt, CancellationToken ct = default) =>
        GetAsync<ServiceLinkAdminStatus>(LinkRoot + "/attempts/" + Uri.EscapeDataString(attempt), ct);
    public Task<ServiceLinkIdentityDto> GetIdentityAsync(CancellationToken ct = default) => GetAsync<ServiceLinkIdentityDto>(LinkRoot + "/identity", ct);
    public Task<ServiceLinkIdentityDto> AdoptSourceAsync(ServiceLinkAdoptSourceRequest request, CancellationToken ct = default) =>
        PostAsync<ServiceLinkIdentityDto>(LinkRoot + "/identity/source", request, ct);
    public Task<ServiceLinkIdentityDto> PrepareFlowSourceAsync(CancellationToken ct = default) =>
        PostAsync<ServiceLinkIdentityDto>(LinkRoot + "/identity/source/flow", new { }, ct);
    public Task<RatelDeskConnectionCompletionDto> CompleteConnectionAsync(int tenantId, string linkId, CancellationToken ct = default) =>
        PostAsync<RatelDeskConnectionCompletionDto>($"api/v2/tenants/{tenantId}/connectors/rateldesk/setup/complete", new CompleteRatelDeskConnectionRequest(linkId), ct);
    public Task<ServiceLinkTestResult> TestLinkAsync(string linkId, CancellationToken ct = default) =>
        PostAsync<ServiceLinkTestResult>(LinkRoot + "/links/" + Uri.EscapeDataString(linkId) + "/test", new ServiceLinkAdminAction(), ct);
    public async Task ActOnLinkAsync(ServiceLinkAdminStatus link, string action, string? directionId = null, CancellationToken ct = default)
    {
        if (action is not ("resume" or "cancel" or "rotate" or "revoke")) throw new ArgumentOutOfRangeException(nameof(action));
        var resource = link.LinkId is null ? "attempts" : "links";
        var id = Uri.EscapeDataString(link.LinkId ?? link.AttemptId);
        using var api = clients.CreateClient("ServiceLinkApi");
        using var budget = RequestBudget(api, ct);
        using var response = await SendJsonAsync(api, HttpMethod.Post, $"{LinkRoot}/{resource}/{id}/{action}", new ServiceLinkAdminAction("administrator-request", directionId), budget.Token);
        await CheckResponseAsync(response, budget.Token);
    }
    private async Task<T> GetAsync<T>(string path, CancellationToken ct)
    {
        using var api = clients.CreateClient("ServiceLinkApi");
        using var budget = RequestBudget(api, ct);
        using var response = await api.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, budget.Token);
        await CheckResponseAsync(response, budget.Token);
        return await ReadAsync<T>(response, budget.Token);
    }
    private async Task<T> PostAsync<T>(string path, object request, CancellationToken ct)
    {
        using var api = clients.CreateClient("ServiceLinkApi");
        using var budget = RequestBudget(api, ct);
        using var response = await SendJsonAsync(api, HttpMethod.Post, path, request, budget.Token);
        await CheckResponseAsync(response, budget.Token);
        return await ReadAsync<T>(response, budget.Token);
    }
    private static CancellationTokenSource RequestBudget(HttpClient api, CancellationToken ct)
    {
        var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(api.Timeout);
        return budget;
    }
    private static async Task<HttpResponseMessage> SendJsonAsync(HttpClient api, HttpMethod method, string path, object body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body, body.GetType()) };
        return await api.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }
    private static async Task CheckResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var failure = ServiceLinkFailure.From(null, statusCode: (int)response.StatusCode);
        try
        {
            using var problem = await ReadAsync<JsonDocument>(response, ct);
            if (problem.RootElement.ValueKind == JsonValueKind.Object)
            {
                string? Field(string name) => problem.RootElement.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
                    ? field.GetString() : null;
                failure = ServiceLinkFailure.From(Field("code"), Field("stage"), Field("correlationId"), (int)response.StatusCode, Field("existingAttemptId"));
            }
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException)
        { throw new ServiceAdministrationException(response.StatusCode, failure); }
        throw new ServiceAdministrationException(response.StatusCode, failure);
    }
    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        const int bound = 131072;
        if (response.Content.Headers.ContentLength > bound) throw new InvalidDataException("Service administration response exceeds its bound.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var bytes = new byte[bound + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var count = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (count == 0) break;
            length += count;
        }
        if (length > bound) throw new InvalidDataException("Service administration response exceeds its bound.");
        return JsonSerializer.Deserialize<T>(bytes.AsSpan(0, length), new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new JsonException("Service administration returned no result.");
    }
}

public sealed class ServiceAdministrationException : Exception
{
    public ServiceAdministrationException(HttpStatusCode statusCode, string? code)
        : this(statusCode, ServiceLinkFailure.From(code, statusCode: (int)statusCode)) { }
    public ServiceAdministrationException(HttpStatusCode statusCode, ServiceLinkFailure failure) : base(failure.Message)
    { StatusCode = statusCode; Failure = failure; }
    public HttpStatusCode StatusCode { get; }
    public ServiceLinkFailure Failure { get; }
    public string Code => Failure.Code;
    public string Stage => Failure.Stage;
    public string? CorrelationId => Failure.CorrelationId;
}
