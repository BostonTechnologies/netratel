using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Web.Services.Jobs;

/// <summary>Typed transport for the canonical tenant directory, groups and read-only target resolver.</summary>
public sealed class JobTargetsApiClient(IHttpClientFactory factory)
{
    private readonly HttpClient _http = factory.CreateClient("OrchestratorApi");

    public Task<ExecutionGroupPageDto> GetGroupsAsync(int tenantId, string? search, int page, CancellationToken ct) =>
        ReadAsync<ExecutionGroupPageDto>($"/api/v2/tenants/{tenantId}/execution-groups?search={Uri.EscapeDataString(search ?? "")}&page={page}&pageSize=25", ct);

    public Task<ExecutionGroupDetailsDto> GetGroupAsync(int tenantId, Guid id, CancellationToken ct) =>
        ReadAsync<ExecutionGroupDetailsDto>($"/api/v2/tenants/{tenantId}/execution-groups/{id}", ct);

    public async Task<IReadOnlyList<JobNodeFilterPropertyDto>> GetPropertiesAsync(int tenantId, CancellationToken ct) =>
        await ReadAsync<JobNodeFilterPropertyDto[]>($"/api/v2/tenants/{tenantId}/job-targets/properties", ct);

    public async Task<ExecutionGroupDetailsDto> SaveGroupAsync(int tenantId, Guid? id, ExecutionGroupWriteDto request, CancellationToken ct)
    {
        var path = $"/api/v2/tenants/{tenantId}/execution-groups" + (id is null ? "" : $"/{id}");
        using var response = id is null ? await _http.PostAsJsonAsync(path, request, ct) : await _http.PutAsJsonAsync(path, request, ct);
        return await ReadResponseAsync<ExecutionGroupDetailsDto>(response, ct);
    }

    public async Task DeleteGroupAsync(int tenantId, Guid id, long revision, CancellationToken ct)
    {
        using var response = await _http.DeleteAsync($"/api/v2/tenants/{tenantId}/execution-groups/{id}?expectedRevision={revision}", ct);
        await RequireSuccessAsync(response, ct);
    }

    public Task<JobNodePageDto> GetNodesAsync(int tenantId, string? search, int page, ClientEnvironment? environment, CancellationToken ct) =>
        ReadAsync<JobNodePageDto>($"/api/v2/tenants/{tenantId}/job-targets/nodes?search={Uri.EscapeDataString(search ?? "")}&page={page}&pageSize=25" +
            (environment is null ? "" : $"&environment={Uri.EscapeDataString(environment.Value.ToString())}"), ct);

    public async Task<IReadOnlyList<JobNodeInventoryDto>> GetNodeIdentitiesAsync(int tenantId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/tenants/{tenantId}/job-targets/nodes/identities", ids, ct);
        return await ReadResponseAsync<List<JobNodeInventoryDto>>(response, ct);
    }

    public async Task<JobTargetPreviewDto> PreviewAsync(int tenantId, JobTargetPreviewRequest request, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v2/tenants/{tenantId}/job-targets/resolve-preview", request, ct);
        return await ReadResponseAsync<JobTargetPreviewDto>(response, ct);
    }

    public async Task<JobLaunchPreviewDto> PreviewRunAsync(ulong jobId, RunJobRequest request, int page, int pageSize, CancellationToken ct)
    {
        using var response = await _http.PostAsJsonAsync($"/api/v1/jobruns/preview/{jobId}?page={page}&pageSize={pageSize}", request, ct);
        return await ReadResponseAsync<JobLaunchPreviewDto>(response, ct);
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct);
        return await ReadResponseAsync<T>(response, ct);
    }

    private static async Task<T> ReadResponseAsync<T>(HttpResponseMessage response, CancellationToken ct)
    {
        await RequireSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("The server returned an empty response. Retry to reload the saved data.");
    }

    private static async Task RequireSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "You do not have permission for this tenant or target.",
            HttpStatusCode.Conflict => "The saved data changed. Your draft is retained. Reload the current revision before saving again.",
            _ => "The request could not be completed. Your draft is retained; retry when the service is available."
        };
        try
        {
            using var problem = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            if (problem.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var name in new[] { "detail", "message", "error" })
                    if (problem.RootElement.TryGetProperty(name, out var detail) && detail.ValueKind == JsonValueKind.String && detail.GetString() is { Length: > 0 and <= 1024 } text)
                    {
                        message = text;
                        break;
                    }
                if (problem.RootElement.TryGetProperty("references", out var references) && references.ValueKind == JsonValueKind.Array)
                {
                    var names = references.EnumerateArray().Where(reference => reference.ValueKind == JsonValueKind.Object)
                        .Take(8).Select(reference => reference.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() : null)
                        .Where(name => name is { Length: > 0 and <= 128 }).ToArray();
                    if (names.Length > 0) message += " References: " + string.Join(", ", names) + ".";
                }
            }
        }
        catch (JsonException) { throw new HttpRequestException(message, null, response.StatusCode); }
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
