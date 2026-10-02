using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public interface IMonitoringApiService
{
    Task<IReadOnlyList<MonitoringTenantDto>> GetTenantsAsync(CancellationToken token = default);
    Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    Task<IReadOnlyList<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, MonitoringRuleWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, MonitoringGroupWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, string collection, Guid entityId, MonitoringDeleteDto request, CancellationToken token = default);
    Task AcknowledgeAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default);
    Task ClearAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default);
}

/// <summary>Tenant-scoped reads and explicit operator mutations. Reads never change alert state.</summary>
public sealed class MonitoringApiService(IHttpClientFactory clients) : IMonitoringApiService
{
    public const string ClientName = "MonitoringApi";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private readonly HttpClient _http = clients.CreateClient(ClientName);
    private static string Root(int tenantId) => $"/api/v2/tenants/{tenantId}/monitoring";
    private static string PageQuery(string? cursor) => "?maximumCount=50" +
        (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor));

    public async Task<IReadOnlyList<MonitoringTenantDto>> GetTenantsAsync(CancellationToken token = default) =>
        await GetAsync<MonitoringTenantDto[]>("/api/v2/monitoring/tenants", token);
    public Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, string? cursor = null, CancellationToken token = default) =>
        GetAsync<MonitoringClientPageDto>(Root(tenantId) + "/clients" + PageQuery(cursor), token);
    public Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringPermissionsDto>(Root(tenantId) + "/permissions", token);
    public Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringConfigurationDto>(Root(tenantId) + "/configuration", token);
    public Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringSummaryDto>(Root(tenantId) + "/summary", token);
    public Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default) =>
        GetAsync<MonitoringSeriesPageDto>(Root(tenantId) + "/series" + PageQuery(cursor), token);
    public Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, string? cursor = null, CancellationToken token = default) =>
        GetAsync<MonitoringEventPageDto>(Root(tenantId) + "/events" + PageQuery(cursor), token);
    public async Task<IReadOnlyList<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, CancellationToken token = default) =>
        await GetAsync<MonitoringPublishedFlowDto[]>(Root(tenantId) + "/published-flows", token);
    public async Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, CancellationToken token = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Root(tenantId) + "/targets/preview") { Content = JsonContent.Create(request) };
        return await SendReadAsync<MonitoringTargetPreviewDto>(message, token);
    }
    public Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, MonitoringRuleWriteDto request, CancellationToken token = default) =>
        WriteConfigurationAsync(HttpMethod.Put, Root(tenantId) + $"/rules/{request.Rule.RuleId:D}", tenantId, request.ExpectedConfigurationRevision, request, token);
    public Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, MonitoringGroupWriteDto request, CancellationToken token = default) =>
        WriteConfigurationAsync(HttpMethod.Put, Root(tenantId) + $"/groups/{request.Group.GroupId:D}", tenantId, request.ExpectedConfigurationRevision, request, token);
    public Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, CancellationToken token = default) =>
        WriteConfigurationAsync(HttpMethod.Put, Root(tenantId) + $"/bypasses/{bypassId:D}", tenantId, request.ExpectedConfigurationRevision, request, token);
    public Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, string collection, Guid entityId, MonitoringDeleteDto request, CancellationToken token = default)
    {
        if (collection is not ("rules" or "groups" or "bypasses")) throw new ArgumentException("Invalid monitoring collection.", nameof(collection));
        return WriteConfigurationAsync(HttpMethod.Delete, Root(tenantId) + $"/{collection}/{entityId:D}", tenantId, request.ExpectedConfigurationRevision, request, token);
    }
    public Task AcknowledgeAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default) =>
        OperatorAsync(tenantId, series, "ack", reason, token);
    public Task ClearAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default) =>
        OperatorAsync(tenantId, series, "clear", reason, token);

    private Task OperatorAsync(int tenantId, MonitoringSeriesState series, string operation, string reason, CancellationToken token)
    {
        if (series.Series.TenantId != tenantId || series.Occurrence is not { } occurrence) throw new ArgumentException("A matching alert occurrence is required.");
        var path = Root(tenantId) + $"/agents/{series.Series.AgentId:D}/rules/{series.Series.RuleId:D}/{operation}?resourceKey=" + Uri.EscapeDataString(series.Series.ResourceKey);
        return WriteAsync(HttpMethod.Post, path, new MonitoringOperatorActionDto(occurrence.OccurrenceId, reason, series.StateRevision), token);
    }
    private async Task<T> GetAsync<T>(string path, CancellationToken token)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, path);
        return await SendReadAsync<T>(message, token);
    }
    private async Task<T> SendReadAsync<T>(HttpRequestMessage message, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            return await response.Content.ReadFromJsonAsync<T>(cancellationToken: timeout.Token).ConfigureAwait(false)
                ?? throw new HttpRequestException("The monitoring response was empty.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The monitoring read timed out. Refresh to try again."); }
    }
    private async Task<MonitoringConfigurationDto> WriteConfigurationAsync<T>(HttpMethod method, string path, int tenantId, ulong expectedRevision, T body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
            await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
            var saved = await response.Content.ReadFromJsonAsync<MonitoringConfigurationDto>(cancellationToken: timeout.Token).ConfigureAwait(false);
            if (saved is null || saved.TenantId != tenantId || expectedRevision == ulong.MaxValue || saved.Revision != expectedRevision + 1)
                throw new HttpRequestException("The saved configuration could not be verified. Refresh to check its result before submitting again.");
            return saved;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The operation could not be confirmed. Refresh to check its result before submitting again."); }
    }
    private async Task WriteAsync<T>(HttpMethod method, string path, T body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var message = new HttpRequestMessage(method, path) { Content = JsonContent.Create(body) };
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            EnsureSuccess(response);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The operation could not be confirmed. Refresh to check its result before submitting again."); }
    }
    private static void EnsureSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        var message = response.StatusCode switch
        {
            HttpStatusCode.Conflict => "The configuration or alert changed. Refresh before saving; your edits are retained.",
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized => "You do not have permission for this monitoring operation.",
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => "The configuration was rejected. Check targets, thresholds, reset policy and published flow; your edits are retained.",
            HttpStatusCode.NotFound => "This monitoring item is no longer available.",
            _ => "Monitoring is unavailable. Your edits are retained."
        };
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
