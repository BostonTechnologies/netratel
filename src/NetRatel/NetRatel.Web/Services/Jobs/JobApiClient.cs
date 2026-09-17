using System.Net.Http;
using System.Net.Http.Json;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Web.Services.Jobs;

public interface IJobApiClient
{
    Task<IReadOnlyList<JobDto>> GetJobsAsync(
        string? folder,
        string? search = null,
        ClientEnvironment? environment = null,
        CancellationToken cancellationToken = default);
    Task<JobDto?> GetJobAsync(ulong id, CancellationToken cancellationToken = default);
    Task<JobWithDetailsDto?> GetJobWithDetailsAsync(ulong id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobParamDto>> GetJobParamsAsync(ulong jobId, CancellationToken cancellationToken = default);
    Task<JobParamDto> AddJobParamAsync(ulong jobId, AddJobParamRequest request, CancellationToken cancellationToken = default);
    Task<JobParamDto> UpdateJobParamAsync(ulong paramId, UpdateJobParamRequest request, CancellationToken cancellationToken = default);
    Task DeleteJobParamAsync(ulong paramId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<JobStepDto>> GetJobStepsAsync(ulong jobId, CancellationToken cancellationToken = default);
    Task<JobStepDto> AddJobStepAsync(ulong jobId, AddJobStepRequest request, CancellationToken cancellationToken = default);
    Task<JobStepDto> UpdateJobStepAsync(ulong stepId, UpdateJobStepRequest request, CancellationToken cancellationToken = default);
    Task ReorderJobStepAsync(ulong stepId, int newOrdinal, CancellationToken cancellationToken = default);
    Task DeleteJobStepAsync(ulong stepId, CancellationToken cancellationToken = default);

    Task<JobDto> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default);
    Task<JobDto> UpdateJobAsync(ulong id, UpdateJobRequest request, CancellationToken cancellationToken = default);
    Task DeleteJobAsync(ulong id, CancellationToken cancellationToken = default);

    Task<JobRunDto> RunJobAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobRunDto>> GetJobRunsAsync(
        ulong? jobId = null,
        int? take = null,
        JobRunStatusDto? status = null,
        int? tenantId = null,
        CancellationToken cancellationToken = default);
    Task<JobRunDto?> GetJobRunAsync(ulong id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<JobStepRunDto>> GetJobRunStepsAsync(ulong id, CancellationToken cancellationToken = default);
    Task<PagedJobRunsDto> QueryJobRunsAsync(
        ulong? jobId = null,
        int? tenantId = null,
        string? clientIdentity = null,
        string? folder = null,
        string? search = null,
        JobRunStatusDto? status = null,
        ClientEnvironment? environment = null,
        int page = 0,
        int pageSize = 20,
        CancellationToken cancellationToken = default);
    Task<ExecutionLogDto?> GetJobStepLogsAsync(ulong runId, int ordinal, CancellationToken cancellationToken = default);
    Task<JobRunDto?> StartJobRunAsync(ulong jobId, CancellationToken cancellationToken = default);
    Task CancelJobRunAsync(ulong runId, CancellationToken cancellationToken = default);
    Task DeleteJobRunAsync(ulong runId, CancellationToken cancellationToken = default);
}

public sealed class JobApiClient : IJobApiClient
{
    private readonly HttpClient _http;

    public JobApiClient(IHttpClientFactory factory)
    {
        _http = factory.CreateClient("OrchestratorApi");
    }

    public async Task<IReadOnlyList<JobDto>> GetJobsAsync(
        string? folder,
        string? search = null,
        ClientEnvironment? environment = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(folder)) query.Add($"folder={Uri.EscapeDataString(folder)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (environment.HasValue) query.Add($"environment={Uri.EscapeDataString(environment.Value.ToString())}");

        var url = "/api/v1/jobs";
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        var result = await _http.GetFromJsonAsync<List<JobDto>>(url, cancellationToken);
        return result ?? [];
    }

    public Task<JobDto?> GetJobAsync(ulong id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<JobDto>($"/api/v1/jobs/{id}", cancellationToken);

    public Task<JobWithDetailsDto?> GetJobWithDetailsAsync(ulong id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<JobWithDetailsDto>($"/api/v1/jobs/{id}/details", cancellationToken);

    public async Task<IReadOnlyList<JobParamDto>> GetJobParamsAsync(ulong jobId, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<JobParamDto>>($"/api/v1/jobs/{jobId}/params", cancellationToken);
        return result ?? [];
    }

    public async Task<JobParamDto> AddJobParamAsync(ulong jobId, AddJobParamRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/v1/jobs/{jobId}/params", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobParamDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<JobParamDto> UpdateJobParamAsync(ulong paramId, UpdateJobParamRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/v1/jobs/params/{paramId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobParamDto>(cancellationToken: cancellationToken))!;
    }

    public async Task DeleteJobParamAsync(ulong paramId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/jobs/params/{paramId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<IReadOnlyList<JobStepDto>> GetJobStepsAsync(ulong jobId, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<JobStepDto>>($"/api/v1/jobs/{jobId}/steps", cancellationToken);
        return result ?? [];
    }

    public async Task<JobStepDto> AddJobStepAsync(ulong jobId, AddJobStepRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/v1/jobs/{jobId}/steps", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobStepDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<JobStepDto> UpdateJobStepAsync(ulong stepId, UpdateJobStepRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/v1/jobs/steps/{stepId}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobStepDto>(cancellationToken: cancellationToken))!;
    }

    public async Task ReorderJobStepAsync(ulong stepId, int newOrdinal, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync($"/api/v1/jobs/steps/{stepId}/reorder", new ReorderJobStepRequest(newOrdinal), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteJobStepAsync(ulong stepId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/jobs/steps/{stepId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JobDto> CreateJobAsync(CreateJobRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsJsonAsync("/api/v1/jobs", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDto>(cancellationToken: cancellationToken))!;
    }

    public async Task<JobDto> UpdateJobAsync(ulong id, UpdateJobRequest request, CancellationToken cancellationToken = default)
    {
        var response = await _http.PutAsJsonAsync($"/api/v1/jobs/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<JobDto>(cancellationToken: cancellationToken))!;
    }

    public async Task DeleteJobAsync(ulong id, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/jobs/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<JobRunDto> RunJobAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken = default)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            var response = await _http.PostAsJsonAsync($"/api/v1/jobruns/start/{jobId}", request, cts.Token);
            response.EnsureSuccessStatusCode();
            return (await response.Content.ReadFromJsonAsync<JobRunDto>(cancellationToken: cts.Token))!;
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException("The job start request was canceled or timed out.", ex);
        }
    }

    public async Task<IReadOnlyList<JobRunDto>> GetJobRunsAsync(
        ulong? jobId = null,
        int? take = null,
        JobRunStatusDto? status = null,
        int? tenantId = null,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (status.HasValue) query.Add($"status={status.Value}");
        if (jobId.HasValue) query.Add($"jobId={jobId.Value}");
        if (take.HasValue) query.Add($"take={take.Value}");
        if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");

        var url = "/api/v1/jobruns";
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        var result = await _http.GetFromJsonAsync<List<JobRunDto>>(url, cancellationToken);
        return result ?? [];
    }

    public Task<JobRunDto?> GetJobRunAsync(ulong id, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<JobRunDto>($"/api/v1/jobruns/{id}", cancellationToken);

    public async Task<IReadOnlyList<JobStepRunDto>> GetJobRunStepsAsync(ulong id, CancellationToken cancellationToken = default)
    {
        var result = await _http.GetFromJsonAsync<List<JobStepRunDto>>($"/api/v1/jobruns/{id}/steps", cancellationToken);
        return result ?? [];
    }

    public async Task<PagedJobRunsDto> QueryJobRunsAsync(
        ulong? jobId = null,
        int? tenantId = null,
        string? clientIdentity = null,
        string? folder = null,
        string? search = null,
        JobRunStatusDto? status = null,
        ClientEnvironment? environment = null,
        int page = 0,
        int pageSize = 20,
        CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (jobId.HasValue) query.Add($"jobId={jobId.Value}");
        if (tenantId.HasValue) query.Add($"tenantId={tenantId.Value}");
        if (!string.IsNullOrWhiteSpace(clientIdentity)) query.Add($"clientIdentity={Uri.EscapeDataString(clientIdentity)}");
        if (!string.IsNullOrWhiteSpace(folder)) query.Add($"folder={Uri.EscapeDataString(folder)}");
        if (!string.IsNullOrWhiteSpace(search)) query.Add($"search={Uri.EscapeDataString(search)}");
        if (status.HasValue) query.Add($"status={Uri.EscapeDataString(status.Value.ToString())}");
        if (environment.HasValue) query.Add($"environment={Uri.EscapeDataString(environment.Value.ToString())}");
        if (page > 0) query.Add($"page={page}");
        if (pageSize > 0) query.Add($"pageSize={pageSize}");

        var url = "/api/v1/jobruns/query";
        if (query.Count > 0)
            url += "?" + string.Join("&", query);

        var result = await _http.GetFromJsonAsync<PagedJobRunsDto>(url, cancellationToken);
        return result ?? new PagedJobRunsDto([], 0);
    }

    public Task<ExecutionLogDto?> GetJobStepLogsAsync(ulong runId, int ordinal, CancellationToken cancellationToken = default) =>
        _http.GetFromJsonAsync<ExecutionLogDto>($"/api/v1/jobruns/{runId}/steps/{ordinal}/logs", cancellationToken);

    public async Task<JobRunDto?> StartJobRunAsync(ulong jobId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"/api/v1/jobruns/start/{jobId}", content: null, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JobRunDto>(cancellationToken: cancellationToken);
    }

    public async Task CancelJobRunAsync(ulong runId, CancellationToken cancellationToken = default)
    {
        var response = await _http.PostAsync($"/api/v1/jobruns/{runId}/cancel", content: null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task DeleteJobRunAsync(ulong runId, CancellationToken cancellationToken = default)
    {
        var response = await _http.DeleteAsync($"/api/v1/jobruns/{runId}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }
}
