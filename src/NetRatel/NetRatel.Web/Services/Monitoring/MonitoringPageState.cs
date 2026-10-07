using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Web.Services.Monitoring;

public sealed record MonitoringPageSnapshot(MonitoringPermissionsDto Permissions,
    MonitoringConfigurationDto Configuration, MonitoringSummaryDto Summary,
    MonitoringSeriesPageDto Series, MonitoringEventPageDto History,
    IReadOnlyList<MonitoringPublishedFlowDto> PublishedFlows, MonitoringClientPageDto Clients);

/// <summary>Owns cancellable tenant reads and fences clients that finish despite cancellation.</summary>
public sealed class MonitoringPageState(IMonitoringApiService api) : IDisposable
{
    private CancellationTokenSource? _scope;
    private long _generation;
    private long _seriesPageGeneration, _historyPageGeneration, _clientsPageGeneration;
    private CancellationTokenSource? _seriesPage, _historyPage, _clientsPage;
    private bool _disposed;
    public int TenantId { get; private set; }
    public bool Loading { get; private set; }
    public bool PagingSeries { get; private set; }
    public bool PagingHistory { get; private set; }
    public bool PagingClients { get; private set; }
    public string? Error { get; private set; }
    public MonitoringPageSnapshot? Snapshot { get; private set; }
    public CancellationToken Token => _scope?.Token ?? CancellationToken.None;
    public long Generation => _generation;
    public bool IsCurrent(int tenantId, long generation) => !_disposed && TenantId == tenantId && _generation == generation;

    public async Task SelectTenantAsync(int tenantId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenewScope();
        TenantId = tenantId;
        var generation = ++_generation;
        Snapshot = null;
        Error = null;
        Loading = true;
        await ReadAsync(tenantId, generation, _scope!.Token);
    }
    public async Task RefreshAsync()
    {
        if (_disposed || TenantId <= 0 || _scope is null) return;
        RenewScope();
        var tenantId = TenantId;
        var generation = ++_generation;
        Error = null;
        Loading = true;
        await ReadAsync(tenantId, generation, _scope!.Token);
    }
    private async Task ReadAsync(int tenantId, long generation, CancellationToken token)
    {
        try
        {
            var permissions = await api.GetPermissionsAsync(tenantId, token);
            if (!IsCurrent(tenantId, generation)) return;
            if (permissions.TenantId != tenantId || !permissions.CanRead)
            { Snapshot = null; Error = "You do not have permission to read monitoring for this tenant."; return; }
            var configuration = api.GetConfigurationAsync(tenantId, token);
            var summary = api.GetSummaryAsync(tenantId, token);
            var series = api.GetSeriesAsync(tenantId, token: token);
            var history = api.GetEventsAsync(tenantId, token: token);
            var flows = api.GetPublishedFlowsAsync(tenantId, token);
            var clients = api.GetClientsAsync(tenantId, token: token);
            await Task.WhenAll(configuration, summary, series, history, flows, clients);
            if (!IsCurrent(tenantId, generation)) return;
            var config = await configuration;
            var counts = await summary;
            var rows = await series;
            var events = await history;
            if (config.TenantId != tenantId || counts.TenantId != tenantId ||
                rows.Items.Any(item => item.Series.TenantId != tenantId) || events.Items.Any(item => item.Series.TenantId != tenantId))
                throw new HttpRequestException("The monitoring response did not match the selected tenant.");
            Snapshot = new(permissions, config, counts, rows, events, await flows, await clients);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        {
            if (IsCurrent(tenantId, generation))
            {
                if (error is HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.Unauthorized }) Snapshot = null;
                Error = error is HttpRequestException ? error.Message : "Could not read monitoring. Refresh to try again.";
            }
        }
        finally { if (IsCurrent(tenantId, generation)) Loading = false; }
    }
    public Task NextSeriesPageAsync(string? cursor) => ReadPageAsync("series", cursor);
    public Task NextHistoryPageAsync(string? cursor) => ReadPageAsync("history", cursor);
    public Task NextClientsPageAsync(string? cursor) => ReadPageAsync("clients", cursor);
    private async Task ReadPageAsync(string kind, string? cursor)
    {
        if (Snapshot is null || _scope is null || _disposed) return;
        var tenantId = TenantId;
        var generation = _generation;
        var pageGeneration = kind switch { "series" => ++_seriesPageGeneration, "history" => ++_historyPageGeneration, _ => ++_clientsPageGeneration };
        var previous = kind switch { "series" => _seriesPage, "history" => _historyPage, _ => _clientsPage };
        previous?.Cancel(); previous?.Dispose();
        var read = CancellationTokenSource.CreateLinkedTokenSource(_scope.Token);
        switch (kind) { case "series": _seriesPage = read; PagingSeries = true; break; case "history": _historyPage = read; PagingHistory = true; break; default: _clientsPage = read; PagingClients = true; break; }
        bool Current() => IsCurrent(tenantId, generation) && pageGeneration == (kind switch { "series" => _seriesPageGeneration, "history" => _historyPageGeneration, _ => _clientsPageGeneration });
        try
        {
            if (kind == "series")
            {
                var page = await api.GetSeriesAsync(tenantId, cursor, read.Token);
                if (!Current()) return;
                if (page.Items.Any(item => item.Series.TenantId != tenantId)) throw new HttpRequestException("The monitoring page did not match the selected tenant.");
                Snapshot = Snapshot! with { Series = page };
            }
            else if (kind == "history")
            {
                var page = await api.GetEventsAsync(tenantId, cursor, read.Token);
                if (!Current()) return;
                if (page.Items.Any(item => item.Series.TenantId != tenantId)) throw new HttpRequestException("The monitoring history did not match the selected tenant.");
                Snapshot = Snapshot! with { History = page };
            }
            else
            {
                var page = await api.GetClientsAsync(tenantId, cursor, read.Token);
                if (Current()) Snapshot = Snapshot! with { Clients = page };
            }
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException)
        { if (Current()) Error = error is HttpRequestException ? error.Message : "Could not read this monitoring page. Try again."; }
        finally
        {
            if (Current()) switch (kind) { case "series": PagingSeries = false; break; case "history": PagingHistory = false; break; default: PagingClients = false; break; }
        }
    }
    private void RenewScope()
    {
        _scope?.Cancel(); _scope?.Dispose();
        _seriesPage?.Dispose(); _historyPage?.Dispose(); _clientsPage?.Dispose();
        _seriesPage = _historyPage = _clientsPage = null;
        PagingSeries = PagingHistory = PagingClients = false;
        _scope = new CancellationTokenSource();
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ++_generation;
        _scope?.Cancel();
        _scope?.Dispose();
        _seriesPage?.Dispose(); _historyPage?.Dispose(); _clientsPage?.Dispose();
    }
}
