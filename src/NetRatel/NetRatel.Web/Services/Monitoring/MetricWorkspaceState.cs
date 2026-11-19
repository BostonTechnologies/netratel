using System.Net;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Web.Services.Monitoring;

public sealed record MetricWorkspaceScope(int Tenant, Guid? Agent, string? Interface, long? Disk, long? Service, string? AlertResource,
    DateTimeOffset From, DateTimeOffset To);

/// <summary>Only the mounted Metrics tab owns these requests and retained results.</summary>
public sealed class MetricWorkspaceState(IMonitoringApiService api) : IDisposable
{
    private CancellationTokenSource? _request;
    private long _generation;
    private (int Tenant, Guid? Agent) _client;
    private string? _soleInterface;
    public bool Loading { get; private set; }
    public bool CanManage { get; private set; }
    public bool PermissionDenied { get; private set; }
    public string? Error { get; private set; }
    public MetricCatalogue? Catalogue { get; private set; }
    public MetricHistoryResponse? History { get; private set; }
    public string? SelectedInterface { get; private set; }
    public long? SelectedDisk { get; private set; }
    public long? SelectedService { get; private set; }

    public async Task RefreshAsync(MetricWorkspaceScope scope)
    {
        Cancel();
        _request = new();
        var token = _request.Token;
        var generation = _generation;
        if (_client != (scope.Tenant, scope.Agent))
        {
            _client = (scope.Tenant, scope.Agent);
            _soleInterface = null;
            Catalogue = null;
            CanManage = false;
        }
        History = null;
        Loading = true;
        Error = null;
        PermissionDenied = false;
        try
        {
            var permissions = await api.GetPermissionsAsync(scope.Tenant, token);
            if (!Current()) return;
            if (permissions.TenantId != scope.Tenant || !permissions.CanRead)
            {
                LoseRead(permissions.TenantId == scope.Tenant && permissions.CanManage);
                return;
            }
            CanManage = permissions.CanManage;
            if (scope.Agent is not { } agent) return;
            var catalogue = await api.GetMetricCatalogueAsync(scope.Tenant, agent, token: token);
            if (!Current()) return;
            if (catalogue.NextCursor is { } cursor)
            {
                var next = await api.GetMetricCatalogueAsync(scope.Tenant, agent, cursor, token);
                if (!Current()) return;
                catalogue = catalogue with { Series = catalogue.Series.Concat(next.Series).ToArray(), NextCursor = next.NextCursor };
            }
            if (catalogue.Series.Any(row => row.AgentId != agent))
                throw new HttpRequestException("The catalogue returned a different computer.");
            Catalogue = catalogue;
            var ids = ResolveSelection(scope, catalogue.Series);
            if (ids.Length == 0) return;
            var history = await api.QueryMetricsAsync(scope.Tenant, agent,
                new(ids, scope.From, scope.To, MaximumPointsPerSeries: 500), token);
            if (!Current()) return;
            if (history.Series.Any(row => row.Series.AgentId != agent || !ids.Contains(row.Series.Id)))
                throw new HttpRequestException("The history response returned a different scope.");
            History = history;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        { if (Current()) LoseRead(false); }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            if (Current())
            {
                Catalogue = null;
                Error = error is HttpRequestException { StatusCode: HttpStatusCode.NotFound }
                    ? "This computer or a selected metric is no longer available." : "Could not load historical metrics. Refresh to try again.";
            }
        }
        finally { if (Current()) Loading = false; }
        return;

        bool Current() => generation == _generation && !token.IsCancellationRequested;
    }

    private long[] ResolveSelection(MetricWorkspaceScope scope, IReadOnlyList<MetricSeriesDescription> series)
    {
        var interfaces = series.Where(item => item.Kind is MetricHistoryKind.NetworkRx or MetricHistoryKind.NetworkTx)
            .Select(item => item.ResourceKey).Distinct(StringComparer.Ordinal).ToArray();
        if (scope.Interface is null && interfaces.Length == 1) _soleInterface ??= interfaces[0];
        SelectedInterface = interfaces.Contains(scope.Interface ?? _soleInterface, StringComparer.Ordinal) ? scope.Interface ?? _soleInterface : null;

        var disks = series.Where(item => item.Kind == MetricHistoryKind.DiskUsedPercent).OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        var services = series.Where(item => item.Kind == MetricHistoryKind.ServiceState).OrderBy(item => item.DisplayName, StringComparer.Ordinal).ToArray();
        SelectedDisk = Choose(scope.Disk, disks, item => MonitoringObservationFactory.TryDiskKey(item.DisplayName) == scope.AlertResource);
        SelectedService = Choose(scope.Service, services, item => ServiceKey(item) == scope.AlertResource);

        return series.Where(item => item.Kind is MetricHistoryKind.CpuPercent or MetricHistoryKind.MemoryPercent or
                MetricHistoryKind.MemoryUsedBytes or MetricHistoryKind.MemoryTotalBytes ||
            (item.Kind is MetricHistoryKind.NetworkRx or MetricHistoryKind.NetworkTx && item.ResourceKey == SelectedInterface) ||
            item.Id == SelectedDisk || item.Id == SelectedService).Select(item => item.Id).ToArray();
    }

    private static long? Choose(long? id, MetricSeriesDescription[] series, Func<MetricSeriesDescription, bool> matches) =>
        id.HasValue ? series.FirstOrDefault(item => item.Id == id)?.Id :
            (series.FirstOrDefault(matches) ?? series.FirstOrDefault())?.Id;

    private static string? ServiceKey(MetricSeriesDescription item)
    {
        if (item.ResourceKey.StartsWith("1:", StringComparison.Ordinal))
            return "service:windows:" + item.ResourceKey[2..];
        return item.ResourceKey.StartsWith("2:", StringComparison.Ordinal) ? "service:systemd:" + item.ResourceKey[2..] : null;
    }

    private void LoseRead(bool manage)
    {
        PermissionDenied = true;
        CanManage = manage;
        Catalogue = null;
        History = null;
        SelectedDisk = SelectedService = null;
        SelectedInterface = null;
    }

    public void Cancel()
    {
        ++_generation;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        Loading = false;
    }

    public void Dispose() => Cancel();
}
