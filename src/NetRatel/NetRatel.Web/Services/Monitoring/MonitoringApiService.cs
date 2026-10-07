using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public interface IMonitoringApiService
{
    Task<IReadOnlyList<MonitoringTenantDto>> GetTenantsAsync(CancellationToken token = default);
    Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    Task<MonitoringClientPageDto> SearchClientsAsync(int tenantId, string? search, string? cursor = null, CancellationToken token = default) => GetClientsAsync(tenantId, cursor, token);
    Task<IReadOnlyList<MonitoringClientIdentityDto>> GetClientIdentitiesAsync(int tenantId, IReadOnlyCollection<Guid> agentIds, CancellationToken token = default) => Task.FromResult<IReadOnlyList<MonitoringClientIdentityDto>>([]);
    Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    async Task<MonitoringSeriesPageDto> GetClientSeriesAsync(int tenantId, Guid agentId, CancellationToken token = default)
    {
        var page = await GetSeriesAsync(tenantId, token: token);
        return page with { Items = page.Items.Where(item => item.Series.AgentId == agentId).ToImmutableArray(), NextCursor = null };
    }
    Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, string? cursor = null, CancellationToken token = default);
    Task<IReadOnlyList<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, CancellationToken token = default);
    Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, MonitoringRuleWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, MonitoringGroupWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, CancellationToken token = default);
    Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, string collection, Guid entityId, MonitoringDeleteDto request, CancellationToken token = default);
    Task AcknowledgeAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default);
    Task ClearAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default);
    Task AcknowledgeOccurrenceAsync(int tenantId, MonitoringSeriesState series, ulong configurationRevision, string reason, CancellationToken token = default) => AcknowledgeAsync(tenantId, series, reason, token);
    Task ClearOccurrenceAsync(int tenantId, MonitoringSeriesState series, ulong configurationRevision, string reason, CancellationToken token = default) => ClearAsync(tenantId, series, reason, token);
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
    public Task<MonitoringClientPageDto> SearchClientsAsync(int tenantId, string? search, string? cursor = null, CancellationToken token = default) =>
        GetAsync<MonitoringClientPageDto>(Root(tenantId) + "/clients?maximumCount=25" +
            (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)) +
            (string.IsNullOrWhiteSpace(search) ? "" : "&search=" + Uri.EscapeDataString(search.Trim())), token);
    public async Task<IReadOnlyList<MonitoringClientIdentityDto>> GetClientIdentitiesAsync(int tenantId, IReadOnlyCollection<Guid> agentIds, CancellationToken token = default)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, Root(tenantId) + "/clients/identities") { Content = JsonContent.Create(agentIds) };
        return await SendReadAsync<MonitoringClientIdentityDto[]>(message, token);
    }
    public Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringPermissionsDto>(Root(tenantId) + "/permissions", token);
    public Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringConfigurationDto>(Root(tenantId) + "/configuration", token);
    public Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default) =>
        GetAsync<MonitoringSummaryDto>(Root(tenantId) + "/summary", token);
    public Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default) =>
        GetAsync<MonitoringSeriesPageDto>(Root(tenantId) + "/series" + PageQuery(cursor), token);
    public async Task<MonitoringSeriesPageDto> GetClientSeriesAsync(int tenantId, Guid agentId, CancellationToken token = default)
    {
        var rows = await GetAsync<MonitoringSeriesState[]>(Root(tenantId) + $"/agents/{agentId:D}/series", token);
        var identities = await GetClientIdentitiesAsync(tenantId, [agentId], token);
        return new(rows.ToImmutableArray(), null, identities.ToImmutableArray());
    }
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

    public Task AcknowledgeOccurrenceAsync(int tenantId, MonitoringSeriesState series, ulong configurationRevision, string reason, CancellationToken token = default) =>
        OperatorAsync(tenantId, series, "ack", reason, token, configurationRevision);
    public Task ClearOccurrenceAsync(int tenantId, MonitoringSeriesState series, ulong configurationRevision, string reason, CancellationToken token = default) =>
        OperatorAsync(tenantId, series, "clear", reason, token, configurationRevision);

    private Task OperatorAsync(int tenantId, MonitoringSeriesState series, string operation, string reason, CancellationToken token, ulong? configurationRevision = null)
    {
        if (series.Series.TenantId != tenantId || series.Occurrence is not { } occurrence) throw new ArgumentException("A matching alert occurrence is required.");
        var path = Root(tenantId) + $"/agents/{series.Series.AgentId:D}/rules/{series.Series.RuleId:D}/{operation}?resourceKey=" + Uri.EscapeDataString(series.Series.ResourceKey);
        return WriteAsync(HttpMethod.Post, path, new MonitoringOperatorActionDto(occurrence.OccurrenceId, reason, ExpectedConfigurationRevision: configurationRevision, ExpectedOperatorRevision: series.OperatorRevision), token);
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
            await EnsureSuccessAsync(response, timeout.Token);
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
            await EnsureSuccessAsync(response, timeout.Token);
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
            await EnsureSuccessAsync(response, timeout.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new HttpRequestException("The operation could not be confirmed. Refresh to check its result before submitting again."); }
    }
    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken token)
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
        try
        {
            await response.Content.LoadIntoBufferAsync(4096, token);
            using var problem = await System.Text.Json.JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            var code = problem.RootElement.TryGetProperty("title", out var title) ? title.GetString() : null;
            message = code switch
            {
                "monitoring_occurrence_replaced" => "A newer occurrence has replaced this alert. Refresh and review it; your reason is retained.",
                "monitoring_occurrence_closed" => "This alert occurrence has ended or was suspended. Refresh and review its history; your reason is retained.",
                "monitoring_operator_conflict" => "Another operator changed this alert. Refresh and review the action; your reason is retained.",
                "monitoring_rule_revision_conflict" or "monitoring_configuration_conflict" => "The rule or configuration changed. Refresh and review it; your edits are retained.",
                _ => message
            };
        }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException or InvalidOperationException) { }
        throw new HttpRequestException(message, null, response.StatusCode);
    }
}
