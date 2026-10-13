using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.Services;

public interface IPromptLibraryApiService
{
    Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default);
    Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default);
    Task<AiPromptDetailDto> GetAsync(int tenantId, Guid id, long? revision = null, CancellationToken ct = default);
    Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default);
    Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid id, AiPromptSaveRequest request, CancellationToken ct = default);
    Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid id, AiPromptArchiveRequest request, CancellationToken ct = default);
    Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default);
}

public sealed class PromptLibraryApiService(IHttpClientFactory factory) : IPromptLibraryApiService
{
    private readonly HttpClient http = factory.CreateClient(FlowApiService.ClientName);
    private static string Root(int tenantId) => $"/api/v1/tenants/{tenantId}/ai/prompts";
    public Task<IReadOnlyList<AiPromptTenantDto>> TenantsAsync(CancellationToken ct = default) => Send<IReadOnlyList<AiPromptTenantDto>>(HttpMethod.Get, "/api/v1/ai/prompt-tenants", null, ct);
    public Task<AiPromptListDto> ListAsync(int tenantId, string? search = null, bool includeArchived = false, CancellationToken ct = default) =>
        Send<AiPromptListDto>(HttpMethod.Get, Root(tenantId) + $"?includeArchived={includeArchived}&search={Uri.EscapeDataString(search ?? "")}", null, ct);
    public Task<AiPromptDetailDto> GetAsync(int tenantId, Guid id, long? revision = null, CancellationToken ct = default) =>
        Send<AiPromptDetailDto>(HttpMethod.Get, Root(tenantId) + $"/{id:D}" + (revision is null ? "" : $"?revision={revision}"), null, ct);
    public Task<AiPromptDetailDto> CreateAsync(int tenantId, AiPromptSaveRequest request, CancellationToken ct = default) => Send<AiPromptDetailDto>(HttpMethod.Post, Root(tenantId), request, ct);
    public Task<AiPromptDetailDto> UpdateAsync(int tenantId, Guid id, AiPromptSaveRequest request, CancellationToken ct = default) => Send<AiPromptDetailDto>(HttpMethod.Put, Root(tenantId) + $"/{id:D}", request, ct);
    public Task<AiPromptDetailDto> ArchiveAsync(int tenantId, Guid id, AiPromptArchiveRequest request, CancellationToken ct = default) => Send<AiPromptDetailDto>(HttpMethod.Post, Root(tenantId) + $"/{id:D}/archive", request, ct);
    public Task<AiPromptDefaultDto> SetDefaultAsync(int tenantId, AiPromptSetDefaultRequest request, CancellationToken ct = default) => Send<AiPromptDefaultDto>(HttpMethod.Put, Root(tenantId) + "/default", request, ct);
    private async Task<T> Send<T>(HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await http.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var message = response.StatusCode switch { HttpStatusCode.Forbidden => "Your current permissions do not allow this prompt operation in this tenant.",
                HttpStatusCode.NotFound => "This prompt or revision is unavailable in this tenant.", _ => "The prompt request failed. Refresh before trying again." };
            if (response.Content.Headers.ContentLength is <= 8192)
                try { var error = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token); if (error.TryGetProperty("message", out var text) && text.GetString() is { Length: > 0 and < 1024 } detail) message = detail; }
                catch (JsonException) { }
            throw new HttpRequestException(message, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<T>(timeout.Token) ?? throw new HttpRequestException("The API returned no prompt state.");
    }
}
