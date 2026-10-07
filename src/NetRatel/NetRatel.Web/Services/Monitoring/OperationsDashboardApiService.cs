using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public interface IOperationsDashboardApiService
{
    Task<OperationsDashboardDto> GetAsync(int tenantId, int page = 0, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OperationsRecentJobDto>> GetJobsAsync(int tenantId, CancellationToken cancellationToken = default);
}

public sealed class OperationsDashboardApiService(IHttpClientFactory clients) : IOperationsDashboardApiService
{
    public async Task<IReadOnlyList<OperationsRecentJobDto>> GetJobsAsync(int tenantId, CancellationToken cancellationToken = default)
    {
        var jobs = await ReadAsync<OperationsRecentJobDto[]>($"/api/v2/tenants/{tenantId}/monitoring/dashboard/jobs", cancellationToken);
        return jobs.Length <= 6 ? jobs : throw new HttpRequestException("The dashboard jobs response could not be verified.");
    }

    public async Task<OperationsDashboardDto> GetAsync(int tenantId, int page = 0, CancellationToken cancellationToken = default)
    {
        var result = await ReadAsync<OperationsDashboardDto>($"/api/v2/tenants/{tenantId}/monitoring/dashboard?page={page}&pageSize=25", cancellationToken);
        if (result.TenantId != tenantId || result.Page != page || result.Clients.Length > 25)
            throw new HttpRequestException("The dashboard response could not be verified. Retry to refresh it.");
        return result;
    }

    private async Task<T> ReadAsync<T>(string path, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await clients.CreateClient(MonitoringApiService.ClientName).GetAsync(
                path,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden
                    ? "You do not have permission to view this tenant's dashboard." : "Dashboard data is unavailable. Retry to refresh it.", null, response.StatusCode);
            await response.Content.LoadIntoBufferAsync(256 * 1024, timeout.Token).ConfigureAwait(false);
            var result = await response.Content.ReadFromJsonAsync<T>(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (result is null)
                throw new HttpRequestException("The dashboard response could not be verified. Retry to refresh it.");
            return result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new HttpRequestException("Dashboard data timed out. Retry to refresh it."); }
        catch (JsonException exception)
        { throw new HttpRequestException("The dashboard response could not be verified. Retry to refresh it.", exception); }
    }
}
