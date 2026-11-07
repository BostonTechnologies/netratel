using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Services.Monitoring;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringPageStateTests
{
    [Fact]
    public async Task LateTenantResponseCannotReplaceCurrentTenantAndOldReadIsCancelled()
    {
        var api = new MonitoringTestApi(); var late = new TaskCompletionSource<MonitoringConfigurationDto>();
        CancellationToken old = default;
        api.ConfigurationRead = (tenant, token) => { if (tenant == 1) { old = token; return late.Task; } return Task.FromResult(api.Configuration(tenant)); };
        using var state = new MonitoringPageState(api);
        var first = state.SelectTenantAsync(1);
        await state.SelectTenantAsync(2);
        Assert.True(old.IsCancellationRequested);
        late.SetResult(api.Configuration(1)); await first;
        Assert.Equal(2, state.Snapshot!.Configuration.TenantId);
        Assert.False(state.Loading);
    }
    [Fact]
    public async Task RefreshCancelsOldReadAndLateFailureCannotOverwriteSuccess()
    {
        var api = new MonitoringTestApi(); using var state = new MonitoringPageState(api); await state.SelectTenantAsync(1);
        var late = new TaskCompletionSource<MonitoringConfigurationDto>(); var calls = 0; CancellationToken old = default;
        api.ConfigurationRead = (tenant, token) => { if (++calls == 1) { old = token; return late.Task; } return Task.FromResult(api.Configuration(tenant) with { Revision = 2 }); };
        var first = state.RefreshAsync(); await state.RefreshAsync();
        Assert.True(old.IsCancellationRequested);
        late.SetException(new HttpRequestException("old failure")); await first;
        Assert.Equal((ulong)2, state.Snapshot!.Configuration.Revision);
        Assert.Null(state.Error);
    }
    [Fact]
    public async Task ReversedSeriesPagesAndScopeChangesCannotReplaceCurrentRows()
    {
        var api = new MonitoringTestApi(); using var state = new MonitoringPageState(api); await state.SelectTenantAsync(1);
        var late = new TaskCompletionSource<MonitoringSeriesPageDto>(); CancellationToken old = default;
        api.SeriesRead = (tenant, cursor, token) => { if (cursor == "old") { old = token; return late.Task; } return Task.FromResult(new MonitoringSeriesPageDto([], "new-page")); };
        var first = state.NextSeriesPageAsync("old"); await state.NextSeriesPageAsync("new");
        Assert.True(old.IsCancellationRequested);
        late.SetResult(new([], "old-page")); await first;
        Assert.Equal("new-page", state.Snapshot!.Series.NextCursor);
        Assert.False(state.PagingSeries);
    }
    [Fact]
    public async Task HistoryPagingIsFencedIndependentlyAndDisposedReadsCannotPublish()
    {
        var api = new MonitoringTestApi(); using var state = new MonitoringPageState(api); await state.SelectTenantAsync(1);
        var late = new TaskCompletionSource<MonitoringEventPageDto>(); CancellationToken old = default;
        api.EventsRead = (_, _, token) => { old = token; return late.Task; };
        var first = state.NextHistoryPageAsync("next"); state.Dispose();
        Assert.True(old.IsCancellationRequested);
        late.SetResult(new([], "late")); await first;
        Assert.Null(state.Snapshot!.History.NextCursor);
    }
    [Fact]
    public async Task PermissionDenialAndForeignRowsNeverBecomeCurrentEvidence()
    {
        var api = new MonitoringTestApi { CanRead = false }; using var state = new MonitoringPageState(api);
        await state.SelectTenantAsync(1); Assert.Null(state.Snapshot); Assert.Contains("permission", state.Error);
        api.CanRead = true;
        api.ConfigurationRead = (_, _) => Task.FromResult(api.Configuration(2));
        await state.RefreshAsync(); Assert.Null(state.Snapshot); Assert.Contains("selected tenant", state.Error);
    }
    [Fact]
    public async Task RevokedReadPermissionClearsCachedScopeOnRefresh()
    {
        var api = new MonitoringTestApi(); using var state = new MonitoringPageState(api); await state.SelectTenantAsync(1);
        Assert.NotNull(state.Snapshot);
        api.ConfigurationRead = (_, _) => throw new HttpRequestException("Permission changed", null, System.Net.HttpStatusCode.Forbidden);
        await state.RefreshAsync();
        Assert.Null(state.Snapshot);
        Assert.Contains("Permission", state.Error);
    }
    [Fact]
    public async Task PagesKeepTheQueryAndUseActualReturnedCountForPreviousAndSizeResets()
    {
        var api = new MonitoringTestApi(); var calls = new List<(MonitoringQueryDto Query, string? Cursor)>();
        var draft = MonitoringRuleDraft.Create(); draft.Name = "Backup capacity"; var rule = draft.Build(1);
        var now = DateTimeOffset.UtcNow;
        var evidence = new MonitoringEvidenceDto(new(1, 1), Guid.NewGuid(), now, now, MonitoringEvidenceQuality.Unknown, MonitoringClassification.Unknown, null, 0, null);
        var row = new MonitoringSeriesState(new(1, rule.RuleId, Guid.NewGuid(), "disk:/backup"), 1, 1, MonitoringPhase.Firing, MonitoringEvidenceQuality.Unknown,
            Occurrence: new(Guid.NewGuid(), Guid.NewGuid(), now, now, rule, evidence, MonitoringFlowDispatchDisposition.NoFlowSelected));
        api.QuerySeriesRead = (query, cursor, _) =>
        {
            calls.Add((query, cursor));
            return Task.FromResult(new MonitoringSeriesPageDto(Enumerable.Repeat(row, cursor == "seven" ? 3 : 7).ToImmutableArray(), cursor is null ? "seven" : null));
        };
        using var state = new MonitoringPageState(api)
        {
            Query = new(1, MonitoringView.Firing, row.Series.AgentId, MonitoringSeverity.Warning, "backup", now.AddDays(-1), now.AddDays(1), MaximumCount: 20)
        };
        await state.SelectTenantAsync(1);
        Assert.Equal("1–7 matching items · more available", state.SeriesPager.Range(7, true));
        await state.NextSeriesPageAsync("seven");
        Assert.Equal(7, state.SeriesPager.Offset); Assert.True(state.SeriesPager.CanPrevious);
        await state.PreviousSeriesPageAsync(); Assert.Equal(0, state.SeriesPager.Offset);
        Assert.All(calls, call => { Assert.Equal(20, call.Query.MaximumCount); Assert.Equal(row.Series.AgentId, call.Query.ClientId); Assert.Equal("backup", call.Query.Text); Assert.Equal(MonitoringView.Firing, call.Query.View); });
        await state.SetQueryAsync(state.Query with { MaximumCount = 100, Text = "archive" });
        Assert.False(state.SeriesPager.CanPrevious); Assert.False(state.SeriesPager.CanFirst);
        Assert.Equal(100, calls[^1].Query.MaximumCount); Assert.Equal("archive", calls[^1].Query.Text); Assert.Null(calls[^1].Cursor);
    }
    [Fact]
    public async Task ChangedFiltersFenceLateRowsAndErrorsAndPagingFailureIsExplicit()
    {
        var api = new MonitoringTestApi(); using var state = new MonitoringPageState(api); await state.SelectTenantAsync(1);
        var delayed = new TaskCompletionSource<MonitoringSeriesPageDto>(); CancellationToken old = default;
        api.QuerySeriesRead = (query, _, token) => query.Text == "old" ? Capture(token) : Task.FromResult(new MonitoringSeriesPageDto([], "current"));
        Task<MonitoringSeriesPageDto> Capture(CancellationToken token) { old = token; return delayed.Task; }
        var previous = state.SetQueryAsync(state.Query with { Text = "old" });
        await state.SetQueryAsync(state.Query with { Text = "new" });
        Assert.True(old.IsCancellationRequested);
        delayed.SetException(new HttpRequestException("late filter failure")); await previous;
        Assert.Null(state.Error); Assert.Equal("current", state.Snapshot!.Series.NextCursor);
        api.QuerySeriesRead = (_, _, _) => throw new HttpRequestException("Invalid continuation. Restart pagination.");
        await state.NextSeriesPageAsync("foreign");
        Assert.Contains("Restart pagination", state.Error); Assert.False(state.SeriesPager.CanFirst);
    }
    [Theory]
    [InlineData("2026-03-08", 23)]
    [InlineData("2026-11-01", 25)]
    public void OperatorLocalInclusiveEndUsesTheFollowingLocalDayAcrossDst(string day, int hours)
    {
        var range = MonitoringDateRange.ToUtc(day, day, TimeZoneInfo.FindSystemTimeZoneById("America/New_York"));
        Assert.Equal(TimeSpan.FromHours(hours), range.ToExclusiveUtc - range.FromUtc);
        Assert.Equal(TimeSpan.Zero, range.FromUtc!.Value.Offset);
        Assert.Throws<ArgumentException>(() => MonitoringDateRange.ToUtc("2026-11-02", "2026-11-01", TimeZoneInfo.Utc));
    }
    [Fact]
    public void DetachedRuleDraftPreservesErrorsAndRequiresExplicitReset()
    {
        var draft = MonitoringRuleDraft.Create(); draft.Name = "CPU"; draft.Enabled = true; draft.AgentIds.Add(Guid.NewGuid()); draft.Reason = "first rule";
        var permissions = new MonitoringPermissionsDto(1, true, true, true, true, true, true);
        Assert.Null(draft.Validate(1, permissions, []));
        var saved = draft.Build(1); var editor = MonitoringRuleDraft.Create(saved);
        editor.Name = "renamed"; editor.Reason = "rename";
        Assert.True(editor.Dirty); Assert.Equal(saved.EvaluationRevision, editor.Build(1).EvaluationRevision);
        editor.BreachThreshold = 95;
        Assert.Contains("reset", editor.Validate(1, permissions, [])!);
        Assert.Equal("renamed", editor.Name); Assert.Equal(95, editor.BreachThreshold);
        editor.ResetConfirmed = true;
        Assert.Null(editor.Validate(1, permissions, []));
        Assert.Equal(saved.EvaluationRevision + 1, editor.Build(1).EvaluationRevision);
    }
    [Fact]
    public void DraftSupportsExactServiceStatesAndRealPublishedChoicesOnly()
    {
        var draft = MonitoringRuleDraft.Create(); draft.Name = "SQL service"; draft.Reason = "selected service"; draft.AgentIds.Add(Guid.NewGuid());
        draft.SelectMetric(MonitoringMetricKind.ServiceExpectedState); draft.ResourceName = "MSSQLSERVER";
        draft.ExpectedStates.Clear();
        var permissions = new MonitoringPermissionsDto(1, true, true, true, true, true, false);
        Assert.NotNull(draft.Validate(1, permissions, []));
        draft.ExpectedStates.Add(NetRatel.Shared.Contracts.Services.ClientServiceState.Stopped);
        Assert.Null(draft.Validate(1, permissions, []));
        draft.PublishedFlowVersionId = Guid.NewGuid();
        Assert.Contains("published", draft.Validate(1, permissions, [])!);
        Assert.Null(draft.Build(1).ExecutionPrincipalId);
    }
}

internal class MonitoringTestApi : IMonitoringApiService
{
    public Func<int, Guid, CancellationToken, Task<NetRatel.Shared.Contracts.MonitoringAlertDetailDto>>? AlertRead { get; set; }
    public Task<NetRatel.Shared.Contracts.MonitoringAlertDetailDto> GetAlertAsync(int tenantId, Guid occurrenceId, CancellationToken token = default) =>
        AlertRead?.Invoke(tenantId, occurrenceId, token) ?? Task.FromException<NetRatel.Shared.Contracts.MonitoringAlertDetailDto>(new HttpRequestException("Alert unavailable"));
    public int PreviewCalls { get; private set; }
    public IReadOnlyList<MonitoringPublishedFlowDto> PublishedFlows { get; set; } = [];
    public bool CanRead { get; set; } = true;
    public Func<int, CancellationToken, Task<MonitoringConfigurationDto>>? ConfigurationRead { get; set; }
    public Func<int, CancellationToken, Task<MonitoringSummaryDto>>? SummaryRead { get; set; }
    public Func<int, string?, CancellationToken, Task<MonitoringSeriesPageDto>>? SeriesRead { get; set; }
    public Func<MonitoringQueryDto, string?, CancellationToken, Task<MonitoringSeriesPageDto>>? QuerySeriesRead { get; set; }
    public Func<MonitoringQueryDto, string?, CancellationToken, Task<MonitoringEventPageDto>>? QueryEventsRead { get; set; }
    public Func<int, string?, CancellationToken, Task<MonitoringEventPageDto>>? EventsRead { get; set; }
    public Func<int, string?, string?, CancellationToken, Task<MonitoringClientPageDto>>? ClientSearch { get; set; }
    public Func<int, MonitoringRuleWriteDto, CancellationToken, Task<MonitoringConfigurationDto>>? RuleSave { get; set; }
    public MonitoringConfigurationDto Configuration(int tenant) => new(tenant, 1, [], [], [], DateTimeOffset.UtcNow);
    public Task<IReadOnlyList<MonitoringTenantDto>> GetTenantsAsync(CancellationToken token = default) => Task.FromResult<IReadOnlyList<MonitoringTenantDto>>([new(1, "SQL tenant"), new(2, "Other tenant")]);
    public Task<MonitoringPermissionsDto> GetPermissionsAsync(int tenantId, CancellationToken token = default) => Task.FromResult(new MonitoringPermissionsDto(tenantId, CanRead, true, true, true, true, true));
    public Task<MonitoringConfigurationDto> GetConfigurationAsync(int tenantId, CancellationToken token = default) => ConfigurationRead?.Invoke(tenantId, token) ?? Task.FromResult(Configuration(tenantId));
    public Task<MonitoringSummaryDto> GetSummaryAsync(int tenantId, CancellationToken token = default) => SummaryRead?.Invoke(tenantId, token) ?? Task.FromResult(new MonitoringSummaryDto(tenantId, 0, 0, 0, 0, 0, 0, DateTimeOffset.UtcNow));
    public Task<MonitoringSeriesPageDto> GetSeriesAsync(int tenantId, string? cursor = null, CancellationToken token = default) => SeriesRead?.Invoke(tenantId, cursor, token) ?? Task.FromResult(new MonitoringSeriesPageDto([], null));
    public Task<MonitoringEventPageDto> GetEventsAsync(int tenantId, string? cursor = null, CancellationToken token = default) => EventsRead?.Invoke(tenantId, cursor, token) ?? Task.FromResult(new MonitoringEventPageDto([], null));
    public async Task<MonitoringSeriesPageDto> GetSeriesAsync(MonitoringQueryDto query, string? cursor = null, CancellationToken token = default)
    {
        if (QuerySeriesRead is not null) return await QuerySeriesRead(query, cursor, token);
        var page = await GetSeriesAsync(query.TenantId, cursor, token);
        return page with { Items = page.Items.Where(row => Matches(row, query)).ToImmutableArray() };
    }
    internal static bool Matches(MonitoringSeriesState row, MonitoringQueryDto query)
    {
        var active = row.Occurrence is { EndedAtUtc: null };
        var unknown = row.EvidenceQuality == MonitoringEvidenceQuality.Unknown || row.LatestEvidence is null;
        var view = query.View switch
        {
            MonitoringView.Firing => active && row.Phase == MonitoringPhase.Firing,
            MonitoringView.Active => active || row.Phase == MonitoringPhase.Pending || unknown,
            MonitoringView.Pending => row.Phase == MonitoringPhase.Pending,
            MonitoringView.Unknown => unknown,
            MonitoringView.Suppressed => row.Suppressed,
            MonitoringView.Offline => row.LatestEvidence?.OfflineSummary?.OfflineCount > 0,
            _ => true
        };
        return view && (query.ClientId is null || row.Series.AgentId == query.ClientId || !row.OfflineAgentIds.IsDefault && row.OfflineAgentIds.Contains(query.ClientId.Value)) &&
            (query.Severity is null || row.Occurrence?.PinnedRule.Severity == query.Severity) &&
            (query.FromUtc is null || row.Occurrence?.RaisedAtUtc >= query.FromUtc) && (query.ToExclusiveUtc is null || row.Occurrence?.RaisedAtUtc < query.ToExclusiveUtc);
    }
    public Task<MonitoringEventPageDto> GetEventsAsync(MonitoringQueryDto query, string? cursor = null, CancellationToken token = default) =>
        QueryEventsRead?.Invoke(query, cursor, token) ?? GetEventsAsync(query.TenantId, cursor, token);
    public Task<MonitoringClientPageDto> GetClientsAsync(int tenantId, string? cursor = null, CancellationToken token = default) => Task.FromResult(new MonitoringClientPageDto([new(Guid.Parse("11111111-1111-1111-1111-111111111111"), "SQL Server", NetRatel.Shared.Contracts.Services.ClientServicePlatform.Windows, MonitoringTargetSupport.Supported, "cached")], null, 1));
    public Task<MonitoringClientPageDto> SearchClientsAsync(int tenantId, string? search, string? cursor = null, CancellationToken token = default) => ClientSearch?.Invoke(tenantId, search, cursor, token) ?? GetClientsAsync(tenantId, cursor, token);
    public Task<IReadOnlyList<MonitoringPublishedFlowDto>> GetPublishedFlowsAsync(int tenantId, CancellationToken token = default) => Task.FromResult(PublishedFlows);
    public Task<MonitoringTargetPreviewDto> PreviewTargetsAsync(int tenantId, MonitoringTargetPreviewRequest request, CancellationToken token = default) { PreviewCalls++; return Task.FromResult(new MonitoringTargetPreviewDto(request.Targets.AgentIds, 1, [], request.Targets.AgentIds.Length)); }
    public Task<MonitoringConfigurationDto> SaveRuleAsync(int tenantId, MonitoringRuleWriteDto request, CancellationToken token = default) =>
        RuleSave?.Invoke(tenantId, request, token) ?? Task.FromResult(Configuration(tenantId) with { Revision = request.ExpectedConfigurationRevision + 1 });
    public Task<MonitoringConfigurationDto> SaveGroupAsync(int tenantId, MonitoringGroupWriteDto request, CancellationToken token = default) => Task.FromResult(Configuration(tenantId) with { Revision = request.ExpectedConfigurationRevision + 1 });
    public Task<MonitoringConfigurationDto> SaveBypassAsync(int tenantId, Guid bypassId, MonitoringBypassWriteDto request, CancellationToken token = default) => Task.FromResult(Configuration(tenantId) with { Revision = request.ExpectedConfigurationRevision + 1 });
    public Task<MonitoringConfigurationDto> DeleteAsync(int tenantId, string collection, Guid entityId, MonitoringDeleteDto request, CancellationToken token = default) => Task.FromResult(Configuration(tenantId) with { Revision = request.ExpectedConfigurationRevision + 1 });
    public Task AcknowledgeAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default) => Task.CompletedTask;
    public Task ClearAsync(int tenantId, MonitoringSeriesState series, string reason, CancellationToken token = default) => Task.CompletedTask;
}
