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
    public MonitoringQueryDto Query { get; set; } = new(0, View: MonitoringView.Firing, MaximumCount: 20);
    public Guid? ClientFilter { get => Query.ClientId; set => Query = Query with { ClientId = value }; }
    public MonitoringCursorHistory SeriesPager { get; } = new();
    public MonitoringCursorHistory HistoryPager { get; } = new();
    private MonitoringQueryDto SeriesQuery => (Query with { TenantId = TenantId, Sort = MonitoringSort.SeriesIdentity }).Normalize();
    private MonitoringQueryDto HistoryQuery => (Query with { TenantId = TenantId, View = MonitoringView.All, Sort = MonitoringSort.EventTimeDescending }).Normalize(history: true);
    public async Task SetQueryAsync(MonitoringQueryDto query)
    {
        Query = (query with { TenantId = TenantId }).Normalize();
        await RefreshAsync();
    }
    public int TenantId { get; private set; }
    public bool Loading { get; private set; }
    public bool PagingSeries { get; private set; }
    public bool PagingHistory { get; private set; }
    public bool PagingClients { get; private set; }
    public string? Error { get; private set; }
    public MonitoringPageSnapshot? Snapshot { get; private set; }
    private readonly Dictionary<Guid, MonitoringClientIdentityDto> _identities = [];
    public MonitoringClientIdentityDto? ClientIdentity(Guid agentId) => _identities.GetValueOrDefault(agentId);
    private void RememberIdentities(MonitoringPageSnapshot snapshot)
    {
        foreach (var client in snapshot.Clients.Items) _identities[client.AgentId] = client.Identity ?? new(client.AgentId, client.DisplayName ?? "Client metadata unavailable");
        foreach (var identity in snapshot.Series.ClientIdentities.IsDefault ? [] : snapshot.Series.ClientIdentities) _identities[identity.AgentId] = identity;
        foreach (var identity in snapshot.History.ClientIdentities.IsDefault ? [] : snapshot.History.ClientIdentities) _identities[identity.AgentId] = identity;
    }
    public CancellationToken Token => _scope?.Token ?? CancellationToken.None;
    public long Generation => _generation;
    public bool IsCurrent(int tenantId, long generation) => !_disposed && TenantId == tenantId && _generation == generation;

    public void ClearScope(int tenantId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenewScope(); ++_generation;
        TenantId = tenantId; Query = Query with { TenantId = tenantId };
        Snapshot = null; _identities.Clear(); Error = null; Loading = false;
        SeriesPager.Reset(); HistoryPager.Reset();
    }
    public async Task SelectTenantAsync(int tenantId)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        RenewScope();
        TenantId = tenantId;
        Query = Query with { TenantId = tenantId };
        SeriesPager.Reset(); HistoryPager.Reset();
        var generation = ++_generation;
        Snapshot = null;
        _identities.Clear();
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
            var series = api.GetSeriesAsync(SeriesQuery, token: token);
            var history = api.GetEventsAsync(HistoryQuery, token: token);
            var flows = api.GetPublishedFlowsAsync(tenantId, token);
            var clients = api.GetClientsAsync(tenantId, token: token);
            var selectedClient = Query.ClientId;
            var selectedIdentities = selectedClient is { } client ? api.GetClientIdentitiesAsync(tenantId, [client], token) : Task.FromResult<IReadOnlyList<MonitoringClientIdentityDto>>([]);
            await Task.WhenAll(configuration, summary, series, history, flows, clients, selectedIdentities);
            if (!IsCurrent(tenantId, generation)) return;
            var config = await configuration;
            var counts = await summary;
            var rows = await series;
            var events = await history;
            if (config.TenantId != tenantId || counts.TenantId != tenantId ||
                rows.Items.Any(item => item.Series.TenantId != tenantId) || events.Items.Any(item => item.Series.TenantId != tenantId))
                throw new HttpRequestException("The monitoring response did not match the selected tenant.");
            Snapshot = new(permissions, config, counts, rows, events, await flows, await clients);
            SeriesPager.Reset(); HistoryPager.Reset();
            RememberIdentities(Snapshot);
            foreach (var identity in await selectedIdentities)
                if (identity.AgentId == selectedClient) _identities[identity.AgentId] = identity;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
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
    public Task PreviousSeriesPageAsync() => SeriesPager.CanPrevious ? ReadPageAsync("series", SeriesPager.PreviousCursor) : Task.CompletedTask;
    public Task PreviousHistoryPageAsync() => HistoryPager.CanPrevious ? ReadPageAsync("history", HistoryPager.PreviousCursor) : Task.CompletedTask;
    public Task NextClientsPageAsync(string? cursor) => ReadPageAsync("clients", cursor);
    private async Task ReadPageAsync(string kind, string? cursor)
    {
        if (Snapshot is null || _scope is null || _disposed) return;
        var tenantId = TenantId;
        var generation = _generation;
        var seriesQuery = SeriesQuery;
        var historyQuery = HistoryQuery;
        var pager = kind == "series" ? SeriesPager : HistoryPager;
        var oldCount = kind == "series" ? Snapshot.Series.Items.Length : Snapshot.History.Items.Length + (Snapshot.History.Audits.IsDefault ? 0 : Snapshot.History.Audits.Length);
        Error = null;
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
                var page = await api.GetSeriesAsync(seriesQuery, cursor, read.Token);
                if (!Current()) return;
                if (page.Items.Any(item => item.Series.TenantId != tenantId)) throw new HttpRequestException("The monitoring page did not match the selected tenant.");
                pager.Accept(cursor, oldCount);
                Snapshot = Snapshot! with { Series = page };
            }
            else if (kind == "history")
            {
                var page = await api.GetEventsAsync(historyQuery, cursor, read.Token);
                if (!Current()) return;
                if (page.Items.Any(item => item.Series.TenantId != tenantId)) throw new HttpRequestException("The monitoring history did not match the selected tenant.");
                pager.Accept(cursor, oldCount);
                Snapshot = Snapshot! with { History = page };
            }
            else
            {
                var page = await api.GetClientsAsync(tenantId, cursor, read.Token);
                if (!Current()) return;
                Snapshot = Snapshot! with { Clients = page };
            }
            RememberIdentities(Snapshot!);
        }
        catch (OperationCanceledException) when (read.IsCancellationRequested) { return; }
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

/// <summary>Bounded opaque cursor history. Offsets count returned items, including shortened byte-budget pages.</summary>
public sealed class MonitoringCursorHistory
{
    private const int MaximumHistory = 128;
    private readonly List<(string? Cursor, int Offset)> _pages = [(null, 0)];
    private int _index;
    public int Offset => _pages[_index].Offset;
    public bool CanFirst => _pages[_index].Cursor is not null;
    public bool CanPrevious => _index > 0;
    public string? PreviousCursor => CanPrevious ? _pages[_index - 1].Cursor : null;
    public void Reset() { _pages.Clear(); _pages.Add((null, 0)); _index = 0; }
    public void Accept(string? cursor, int previousCount)
    {
        if (cursor is null) { Reset(); return; }
        var existing = _pages.FindIndex(page => page.Cursor == cursor);
        if (existing >= 0) { _index = existing; return; }
        var offset = Offset + previousCount;
        if (_index + 1 < _pages.Count) _pages.RemoveRange(_index + 1, _pages.Count - _index - 1);
        _pages.Add((cursor, offset));
        if (_pages.Count > MaximumHistory) _pages.RemoveAt(0);
        _index = _pages.Count - 1;
    }
    public string Range(int count, bool hasMore) => count == 0 ? "0 matching items" : $"{Offset + 1}–{Offset + count} matching items" + (hasMore ? " · more available" : " · end of results");
}
