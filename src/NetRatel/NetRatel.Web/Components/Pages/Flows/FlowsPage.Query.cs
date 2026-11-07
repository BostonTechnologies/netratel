using Microsoft.AspNetCore.Components;
using MudBlazor;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Services.Flows;

namespace NetRatel.Web.Components.Pages.Flows;

public partial class FlowsPage
{
    [SupplyParameterFromQuery(Name = "enabled")] public string? RequestedEnabled { get; set; }
    [SupplyParameterFromQuery(Name = "publication")] public string? RequestedPublication { get; set; }
    [SupplyParameterFromQuery(Name = "sort")] public string? RequestedSort { get; set; }
    [SupplyParameterFromQuery(Name = "descending")] public bool? RequestedDescending { get; set; }
    [SupplyParameterFromQuery(Name = "page")] public int? RequestedPage { get; set; }
    [SupplyParameterFromQuery(Name = "pageSize")] public int? RequestedPageSize { get; set; }
    [SupplyParameterFromQuery(Name = "flowId")] public Guid? RequestedHistoryFlowId { get; set; }
    private FlowQueryDto? _appliedListParameters;
    private int _tableEpoch;

    private static T ParseQueryEnum<T>(string? text, T fallback) where T : struct, Enum =>
        text is null ? fallback : Enum.TryParse<T>(text, out var value) ? value : (T)Enum.ToObject(typeof(T), -1);

    private async Task ReadListParametersAsync()
    {
        var restored = new FlowQueryDto(RequestedSearch?.Trim(), ParseQueryEnum(RequestedEnabled, FlowEnabledFilter.All),
            ParseQueryEnum(RequestedPublication, FlowPublicationFilter.All), ParseQueryEnum(RequestedSort, FlowSort.Name),
            RequestedDescending ?? false, RequestedPage ?? 0, RequestedPageSize ?? 20);
        if (restored == _appliedListParameters) return;
        if (!restored.IsValid || restored.PageSize is not (20 or 50 or 100))
        { _error = "The linked Flows query is invalid. Showing the default query."; restored = new(); }
        _appliedListParameters = restored; _query = restored; _search = restored.Search ?? "";
        _readScope?.Cancel(); ++_readIdentity;
        ++_tableEpoch; _table = null;
        await Task.CompletedTask;
    }

    private SortDirection InitialSort(FlowSort sort) => _query.Sort != sort ? SortDirection.None :
        _query.Descending ? SortDirection.Descending : SortDirection.Ascending;

    private Task SearchChangedAsync(string? value) => ChangeQueryAsync(_query with { Search = value?.Trim() ?? "", PageIndex = 0 });
    private Task EnabledFilterChangedAsync(ChangeEventArgs args) => Enum.TryParse<FlowEnabledFilter>(args.Value?.ToString(), out var value)
        ? ChangeQueryAsync(_query with { Enabled = value, PageIndex = 0 }) : Task.CompletedTask;
    private Task PublicationChangedAsync(ChangeEventArgs args) => Enum.TryParse<FlowPublicationFilter>(args.Value?.ToString(), out var value)
        ? ChangeQueryAsync(_query with { Publication = value, PageIndex = 0 }) : Task.CompletedTask;
    private Task ClearQueryAsync() => ChangeQueryAsync(new(PageSize: _query.PageSize));

    private async Task ChangeQueryAsync(FlowQueryDto query)
    {
        query = query with { Search = string.IsNullOrWhiteSpace(query.Search) ? null : query.Search.Trim() };
        var resetTable = query.Sort != _query.Sort || query.Descending != _query.Descending;
        _query = query; _search = query.Search ?? ""; _queryError = null;
        _readScope?.Cancel(); ++_readIdentity;
        PersistQuery();
        if (resetTable) { ++_tableEpoch; _table = null; return; }
        if (_table is not null)
        {
            _table.CurrentPage = 0;
            await _table.ReloadServerData();
        }
    }

    private void PersistQuery()
    {
        _appliedListParameters = _query;
        var uri = Navigation.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["tenantId"] = _tenantId, ["search"] = string.IsNullOrEmpty(_query.Search) ? null : _query.Search,
            ["enabled"] = _query.Enabled == FlowEnabledFilter.All ? null : _query.Enabled.ToString(),
            ["publication"] = _query.Publication == FlowPublicationFilter.All ? null : _query.Publication.ToString(),
            ["sort"] = _query.Sort == FlowSort.Name ? null : _query.Sort.ToString(),
            ["descending"] = _query.Descending ? true : null, ["page"] = _query.PageIndex == 0 ? null : _query.PageIndex,
            ["pageSize"] = _query.PageSize == 20 ? null : _query.PageSize
        });
        if (uri != Navigation.Uri) Navigation.NavigateTo(uri, replace: true);
    }

    private async Task<TableData<FlowSummaryDto>> LoadPageAsync(TableState state, CancellationToken tableCancellation)
    {
        var tenant = _tenantId;
        if (_disposed || tenant <= 0 || Access is null) return new() { Items = [], TotalItems = 0 };
        var sort = Enum.TryParse<FlowSort>(state.SortLabel, out var selected) ? selected : _query.Sort;
        var descending = state.SortLabel is null ? _query.Descending : state.SortDirection == SortDirection.Descending;
        var changedOrder = sort != _query.Sort || descending != _query.Descending || state.PageSize != _query.PageSize;
        var query = _query with { Sort = sort, Descending = descending, PageSize = state.PageSize,
            PageIndex = changedOrder ? 0 : state.Page };
        if (!query.IsValid || query.PageSize is not (20 or 50 or 100)) throw new InvalidOperationException("Invalid Flows table query.");
        _readScope?.Cancel(); _readScope?.Dispose();
        var readScope = CancellationTokenSource.CreateLinkedTokenSource(tableCancellation, _scope.Token);
        _readScope = readScope;
        var identity = ++_readIdentity;
        _query = query;
        if (changedOrder && _table is not null) _table.CurrentPage = 0;
        PersistQuery();
        _queryError = null;
        bool CurrentRead() => !_disposed && identity == _readIdentity && tenant == _tenantId && _query == query;
        try
        {
            var page = await Api.QueryAsync(tenant, query, readScope.Token);
            if (!CurrentRead()) return _pageData;
            var last = Math.Max(0, (page.Total - 1) / query.PageSize);
            if (query.PageIndex > last)
            {
                query = query with { PageIndex = last }; _query = query;
                if (_table is not null) _table.CurrentPage = last;
                PersistQuery();
                page = await Api.QueryAsync(tenant, query, readScope.Token);
                if (!CurrentRead()) return _pageData;
            }
            if (page.Items.Any(row => row.TenantId != tenant || row.DeletedAtUtc is not null) ||
                page.Items.Count > query.PageSize || page.Total < page.Items.Count || page.PageIndex != query.PageIndex || page.PageSize != query.PageSize)
                throw new HttpRequestException("The Flows page response could not be verified.");
            foreach (var row in page.Items) _uncertain.Remove(row.Id);
            _pageData = new() { Items = page.Items, TotalItems = page.Total };
            return _pageData;
        }
        catch (OperationCanceledException) when (readScope.IsCancellationRequested) { return _pageData; }
        catch (HttpRequestException exception)
        {
            if (!CurrentRead()) return _pageData;
            _queryError = exception is FlowApiException ? exception.Message : "Flows could not be loaded.";
            _error = _queryError;
            _pageData = new() { Items = [], TotalItems = 0 };
            return _pageData;
        }
    }

    private static FlowDefinitionDto HistoricalDefinition(FlowSummaryDto metadata, FlowGraphDto immutableGraph) =>
        new(metadata.Id, metadata.TenantId, metadata.DeletedAtUtc is null ? metadata.Name : metadata.Name + " (Deleted flow)",
            metadata.Revision, metadata.Enabled, immutableGraph, metadata.PublishedVersionId, metadata.PublishedVersionNumber,
            metadata.UpdatedAtUtc, metadata.LatestRun);
}
