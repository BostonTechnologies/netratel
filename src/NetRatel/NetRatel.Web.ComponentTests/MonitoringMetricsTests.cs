using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Shared.Monitoring;
using NetRatel.Web.Services.Monitoring;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringMetricsTests : AsyncBunitContext
{
    private static readonly DateTimeOffset From = new(2026, 11, 18, 11, 0, 0, TimeSpan.Zero);
    private static readonly Guid Agent = Guid.Parse("11111111-1111-4111-8111-111111111111");

    [Fact]
    public async Task Delayed_old_interface_and_permission_loss_cannot_replace_or_retain_scoped_data()
    {
        var api = new MetricsApi();
        using var state = new MetricWorkspaceState(api);
        var old = state.RefreshAsync(Scope("eth0"));
        var oldToken = await api.FirstQuery.Task;
        await state.RefreshAsync(Scope("eth1"));
        Assert.True(oldToken.IsCancellationRequested);
        Assert.Equal("eth1", state.SelectedInterface);
        Assert.Equal(2, state.History!.Series[0].Series.Id);
        api.SlowReply.SetResult(Response(Description(1, "eth0")));
        await old;
        Assert.Equal(2, state.History.Series[0].Series.Id);

        api.CanRead = false;
        await state.RefreshAsync(Scope("eth1"));
        Assert.True(state.PermissionDenied);
        Assert.Null(state.History);
        Assert.Null(state.Catalogue);

        static MetricWorkspaceScope Scope(string key) => new(1, Agent, key, null, null, null, From, From.AddHours(1));
    }

    [Fact]
    public async Task Pending_and_failed_resource_selection_never_relabels_a_retained_chart()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var api = new MetricsApi();
        Services.AddSingleton<IMonitoringApiService>(api);
        var cut = Render<MetricHistoryWorkspace>(parameters => parameters.Add(item => item.TenantId, 1)
            .Add(item => item.AgentId, Agent).Add(item => item.Selection, new MetricHistorySelection(Interface: "eth1")));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid=metrics-received]")));
        var before = cut.FindComponents<MetricHistoryChart>().Single(chart => chart.Instance.CardId == "metrics-network-card").Instance;

        cut.Render(parameters => parameters.Add(item => item.Selection, new MetricHistorySelection(Interface: "eth0")));
        await api.FirstQuery.Task;
        Assert.Same(before, cut.FindComponents<MetricHistoryChart>().Single(chart => chart.Instance.CardId == "metrics-network-card").Instance);
        Assert.Single(cut.FindAll("[data-testid=metrics-network-card] select"));
        Assert.Empty(cut.FindAll("[data-testid=metrics-received]"));

        api.SlowReply.SetException(new HttpRequestException("Synthetic metrics outage"));
        await cut.WaitForAssertionAsync(() => Assert.Contains("Last successfully loaded", cut.Markup));
        Assert.Empty(cut.FindAll("[data-testid=metrics-received]"));
        Assert.False(cut.Find("[data-testid=metrics-interface]").HasAttribute("disabled"));
    }

    [Fact]
    public void Network_cards_display_server_summary_bytes_and_distinguish_zero_from_unavailable()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        var received = Description(1, "eth0");
        var sent = received with { Id = 2, Kind = MetricHistoryKind.NetworkTx };
        var response = Response(received);
        var chart = Render<MetricHistoryChart>(component => component.Add(parameters => parameters.Title, "Network RX/TX")
            .Add(parameters => parameters.History, response)
            .Add(parameters => parameters.Series,
            [
                response.Series[0] with { Summary = Totals("0", 0) },
                new(sent, Totals(null, null) with { Quality = MetricHistoryQuality.Legacy }, [], [], false)
            ]));
        Assert.Contains("0 B", chart.Find("[data-testid=metrics-received]").TextContent);
        Assert.Contains("Unavailable", chart.Find("[data-testid=metrics-sent]").TextContent);
        Assert.Contains("RX · Receive", chart.Markup);
        Assert.Contains("TX · Transmit", chart.Markup);
        Assert.Equal("1 KiB/s", MetricHistoryPresentation.Value(1024, "B/s"));
    }

    private static MetricSeriesDescription Description(long id, string name) =>
        new(id, Agent, MetricHistoryKind.NetworkRx, name, name, "B/s", From, From.AddMinutes(1), MetricHistoryQuality.None, true);
    private static MetricHistoryStatistics Totals(string? bytes, double? rate) =>
        new(1, rate, From, rate, rate, rate, bytes is null ? 0 : 60_000_000, bytes, null, null, MetricHistoryQuality.None, null);
    private static MetricCollectionStatus Status() =>
        new(true, From, From, null, null, null, 2, 1000, 10000, 0, 0, null, 0, 0, 0, null, null);
    private static MetricHistoryResponse Response(MetricSeriesDescription series) =>
        new(From, From.AddHours(1), 60, From.AddDays(-14), From.AddDays(-14), "available",
            Status(), [new(series, Totals("128000", 256000), [], [], false)]);

    private sealed class MetricsApi : MonitoringTestApi, IMonitoringApiService
    {
        public TaskCompletionSource<CancellationToken> FirstQuery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<MetricHistoryResponse> SlowReply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<MetricCatalogue> GetMetricCatalogueAsync(int tenantId, Guid agentId, string? cursor = null, CancellationToken token = default) =>
            Task.FromResult(new MetricCatalogue([Description(1, "eth0"), Description(2, "eth1") with { RecentlyObserved = false }], null, Status()));

        public Task<MetricHistoryResponse> QueryMetricsAsync(int tenantId, Guid agentId, MetricHistoryRange range, CancellationToken token = default)
        {
            if (range.SeriesIds.SequenceEqual([1L]))
            {
                FirstQuery.TrySetResult(token);
                return SlowReply.Task;
            }
            return Task.FromResult(Response(Description(2, "eth1")));
        }
    }
}
