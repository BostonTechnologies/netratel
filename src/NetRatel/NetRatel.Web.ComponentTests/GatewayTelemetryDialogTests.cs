using System.Net;
using System.Net.Http.Json;
using Bunit;
using AwesomeAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Components.Shared.Monitoring;
using NetRatel.Web.Services.Monitoring;
using NetRatel.Web.Services.Telemetry;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class GatewayTelemetryDialogTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("9a0ce4b0-7f3d-4b22-a059-d33b2b3f9ec5");
    private readonly TelemetryHttpClientFactory _transport = new();
    private readonly HistoryApi _history = new();

    public GatewayTelemetryDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory>(_transport);
        Services.AddSingleton<IMonitoringApiService>(_history);
        Services.AddScoped<GatewayTelemetryApiService>();
        Services.AddScoped<GatewayTelemetryLiveStreamService>();
    }

    [Fact]
    public async Task Tab_switches_release_only_the_dialog_stream_and_fence_late_history()
    {
        var provider = Render<MudDialogProvider>();
        var held = new TaskCompletionSource<MetricHistoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _history.HoldFirstQuery = held;
        await OpenDialogAsync();
        await provider.WaitForAssertionAsync(() => Assert.Equal(1, _transport.ActiveStreams));
        Assert.Empty(_history.Queries);
        var url = Services.GetRequiredService<NavigationManager>().Uri;

        await Tab(provider, "History").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Single(_history.Queries));
        Assert.Equal(0, _transport.ActiveStreams);
        Assert.Empty(provider.FindAll(".telemetry-stream-state"));
        Assert.Empty(provider.FindAll("[data-testid=monitoring-client-filter]"));
        await Tab(provider, "Live").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Equal(2, _transport.StreamRequests));
        Assert.True(_history.Queries[0].Token.IsCancellationRequested);
        Assert.Equal(1, _transport.ActiveStreams);
        Assert.Contains("Last received", provider.Markup);

        await Tab(provider, "History").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Contains("125 KiB", provider.Find("[data-testid=metrics-received]").TextContent));
        await provider.InvokeAsync(() => held.SetResult(HistoryApi.Response(_history.Queries[0].Range, "1024")));
        Assert.Contains("125 KiB", provider.Find("[data-testid=metrics-received]").TextContent);
        Assert.Contains("Collection disabled", provider.Markup);
        Assert.Contains("This computer is offline", provider.Markup);
        Assert.Equal(1, _transport.MaximumStreams);
        Assert.Equal(url, Services.GetRequiredService<NavigationManager>().Uri);

        await provider.Find("[data-testid=close-telemetry-dashboard]").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Empty(provider.FindComponents<GatewayTelemetryDialog>()));
        Assert.Equal(0, _transport.ActiveStreams);
        Assert.Equal(2, _history.Queries.Count);
    }

    [Fact]
    public async Task A_fixed_client_range_matches_Monitoring_and_keeps_filters_without_polling()
    {
        var provider = Render<MudDialogProvider>();
        await OpenDialogAsync();
        await Tab(provider, "History").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Single(_history.Queries));
        await provider.Find("section[data-testid=monitoring-metrics] select").ChangeAsync("custom");
        var workspace = provider.FindComponent<MetricHistoryWorkspace>();
        await workspace.FindAll("input[type=date]")[0].ChangeAsync("2026-11-15");
        await workspace.FindAll("input[type=date]")[1].ChangeAsync("2026-11-16");
        await workspace.FindAll("button").Single(button => button.TextContent.Trim() == "Apply dates").ClickAsync(new());
        var fixedQuery = _history.Queries.Last().Range;
        var selection = workspace.Instance.Selection;
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 0, 0, 0, TimeSpan.Zero), fixedQuery.FromUtc);
        var expected = workspace.Find("[data-testid=metrics-received]").TextContent;
        var monitoring = Render<MonitoringMetricsWorkspace>(parameters => parameters
            .Add(item => item.TenantId, 9).Add(item => item.AgentId, AgentId)
            .Add(item => item.Range, "custom").Add(item => item.From, selection.From).Add(item => item.To, selection.To));
        var monitoringQuery = _history.Queries.Last().Range;
        Assert.Equal(fixedQuery.FromUtc, monitoringQuery.FromUtc);
        Assert.Equal(fixedQuery.ToExclusiveUtc, monitoringQuery.ToExclusiveUtc);
        Assert.Equal(fixedQuery.SeriesIds, monitoringQuery.SeriesIds);
        Assert.Equal(500, monitoringQuery.MaximumPointsPerSeries);
        Assert.Equal(expected, monitoring.Find("[data-testid=metrics-received]").TextContent);

        var reads = _history.Queries.Count;
        await Tab(provider, "Live").ClickAsync(new());
        await workspace.Instance.RefreshVisibleMetrics();
        await Tab(provider, "History").ClickAsync(new());
        await workspace.Instance.RefreshVisibleMetrics();
        Assert.Equal(reads, _history.Queries.Count);
        Assert.Equal(selection, workspace.Instance.Selection);
        Assert.Equal(expected, workspace.Find("[data-testid=metrics-received]").TextContent);
        await Tab(provider, "Live").ClickAsync(new());
        _history.CanRead = false;
        await Tab(provider, "History").ClickAsync(new());
        Assert.Empty(workspace.FindAll("[data-testid=metrics-received]"));
        Assert.Contains("permission to read this tenant", workspace.Markup);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, true)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public async Task Historical_and_live_rights_are_independent(HttpStatusCode snapshotStatus, bool historyAllowed)
    {
        _transport.SnapshotStatus = snapshotStatus;
        _history.CanRead = historyAllowed;
        var provider = Render<MudDialogProvider>();
        await OpenDialogAsync(CreateSnapshot());

        if (historyAllowed)
        {
            await provider.WaitForAssertionAsync(() => Assert.Equal("true", Tab(provider, "History").GetAttribute("aria-selected")));
            Assert.Single(_history.Queries);
            Assert.Equal(0, _transport.StreamRequests);
            await Tab(provider, "Live").ClickAsync(new());
            await provider.WaitForAssertionAsync(() => Assert.Contains("permission to read live telemetry", provider.Markup));
            Assert.DoesNotContain("telemetry-health-strip", provider.Markup);
        }
        else
        {
            await provider.WaitForAssertionAsync(() => Assert.Equal(1, _transport.ActiveStreams));
            Assert.Equal("true", Tab(provider, "Live").GetAttribute("aria-selected"));
            await Tab(provider, "History").ClickAsync(new());
            await provider.WaitForAssertionAsync(() => Assert.Contains("permission to read this tenant", provider.Markup));
            Assert.Empty(_history.Queries);
            Assert.Equal(0, _transport.ActiveStreams);
        }
    }

    [Fact]
    public async Task Closing_during_history_load_cancels_the_request()
    {
        var provider = Render<MudDialogProvider>();
        var held = new TaskCompletionSource<MetricHistoryResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        _history.HoldFirstQuery = held;
        await OpenDialogAsync();
        await Tab(provider, "History").ClickAsync(new());
        await provider.WaitForAssertionAsync(() => Assert.Single(_history.Queries));
        await provider.Find("[data-testid=close-telemetry-dashboard]").ClickAsync(new());
        Assert.True(_history.Queries[0].Token.IsCancellationRequested);
        held.SetResult(HistoryApi.Response(_history.Queries[0].Range, "1024"));
        Assert.Equal(0, _transport.ActiveStreams);
    }

    private Task<IDialogReference> OpenDialogAsync(GatewayTelemetrySummary? snapshot = null) =>
        Services.GetRequiredService<IDialogService>().ShowAsync<GatewayTelemetryDialog>("Telemetry",
            new DialogParameters<GatewayTelemetryDialog>
            {
                { dialog => dialog.TenantId, 9 }, { dialog => dialog.AgentId, AgentId },
                { dialog => dialog.HostLabel, "Example workstation" }, { dialog => dialog.InitialSnapshot, snapshot }
            }, new DialogOptions { FullScreen = true, CloseOnEscapeKey = true });

    private static AngleSharp.Dom.IElement Tab(IRenderedComponent<MudDialogProvider> provider, string name) =>
        provider.FindAll("[role=tab]").Single(tab => tab.TextContent.Trim() == name);

    [Fact]
    public async Task UsesOneOffGatewayFallbackWhenNoInitialSnapshotWasSupplied()
    {
        var provider = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<GatewayTelemetryDialog>("Telemetry",
            new DialogParameters<GatewayTelemetryDialog>
            {
                { dialog => dialog.TenantId, 9 },
                { dialog => dialog.AgentId, AgentId },
                { dialog => dialog.HostLabel, "fallback-host" },
                { dialog => dialog.TenantLabel, "Fallback tenant" }
            },
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });

        provider.WaitForAssertion(() =>
        {
            provider.Markup.Should().Contain("Fallback authority");
            provider.Markup.Should().Contain("fallback-host");
            ((TelemetryHttpClientFactory)Services.GetRequiredService<IHttpClientFactory>()).SnapshotRequests.Should().Be(1);
        });
    }

    [Fact]
    public async Task KeepsTheCloseControlAndCadenceStateIndependentlyAddressable()
    {
        var provider = Render<MudDialogProvider>();
        await Services.GetRequiredService<IDialogService>().ShowAsync<GatewayTelemetryDialog>("Telemetry",
            new DialogParameters<GatewayTelemetryDialog>
            {
                { dialog => dialog.TenantId, 9 },
                { dialog => dialog.AgentId, AgentId },
                { dialog => dialog.HostLabel, "responsive-host" },
                { dialog => dialog.TenantLabel, "Responsive tenant" },
                { dialog => dialog.InitialSnapshot, CreateSnapshot() }
            },
            new DialogOptions { FullScreen = true, CloseOnEscapeKey = true, CloseButton = false });

        provider.WaitForAssertion(() =>
        {
            provider.Find(".telemetry-close-button [data-testid='close-telemetry-dashboard']").Should().NotBeNull();
            provider.Find(".telemetry-stream-state").TextContent.Should().Contain("Connecting");
        });
    }

    private static GatewayTelemetrySummary CreateSnapshot() => new(
        9, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
        new GatewayTelemetryCpu(17, 0.3, 20), new GatewayTelemetryMemory(100, 40, 60, 40),
        [], [], new GatewayTelemetryTransportHealth(60, "0.4.131-rc.1+long-build-metadata-for-small-viewports", "Linux", DateTimeOffset.UtcNow),
        "Akka", true);

    private sealed class TelemetryHttpClientFactory : IHttpClientFactory
    {
        private readonly Handler _handler = new();
        public int SnapshotRequests => _handler.SnapshotRequests;
        public int StreamRequests => _handler.StreamRequests;
        public int ActiveStreams => _handler.ActiveStreams;
        public int MaximumStreams => _handler.MaximumStreams;
        public HttpStatusCode SnapshotStatus { set => _handler.SnapshotStatus = value; }

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("https://netratel.test")
        };
    }

    private sealed class Handler : HttpMessageHandler
    {
        public int SnapshotRequests { get; private set; }
        public int StreamRequests { get; private set; }
        public int MaximumStreams { get; private set; }
        public int ActiveStreams;
        public HttpStatusCode SnapshotStatus = HttpStatusCode.OK;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/stream", StringComparison.Ordinal))
            {
                StreamRequests++;
                MaximumStreams = Math.Max(MaximumStreams, Interlocked.Increment(ref ActiveStreams));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    // Keep the transport open while the dialog asserts its initial lifecycle state.
                    // A completed response correctly transitions the production stream to Stale,
                    // which would make this test timing-dependent.
                    Content = new StreamContent(new WaitingStream(() => Interlocked.Decrement(ref ActiveStreams)))
                });
            }

            SnapshotRequests++;
            if (SnapshotStatus != HttpStatusCode.OK) return Task.FromResult(new HttpResponseMessage(SnapshotStatus));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new GatewayTelemetrySummary(
                    9, AgentId, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                    new GatewayTelemetryCpu(17, 0.3, 20), new GatewayTelemetryMemory(100, 40, 60, 40),
                    [], [], new GatewayTelemetryTransportHealth(60, "0.4.130-rc.1", "Linux", DateTimeOffset.UtcNow),
                    "Fallback authority", true))
            });
        }
    }

    private sealed class HistoryApi : MonitoringTestApi, IMonitoringApiService
    {
        private static readonly MetricCollectionStatus Status =
            new(false, null, null, null, null, null, 2, 1000, 10000, 0, 0, null, 0, 0, 0, null, null);
        private static readonly MetricSeriesDescription Received = new(1, AgentId, MetricHistoryKind.NetworkRx, "eth0", "eth0",
            "B/s", DateTimeOffset.UtcNow.AddDays(-14), DateTimeOffset.UtcNow, MetricHistoryQuality.None, false);

        public List<(MetricHistoryRange Range, CancellationToken Token)> Queries { get; } = [];
        public TaskCompletionSource<MetricHistoryResponse>? HoldFirstQuery { get; set; }

        public Task<MetricCatalogue> GetMetricCatalogueAsync(int tenantId, Guid agentId, string? cursor = null, CancellationToken token = default)
        {
            Assert.Equal(9, tenantId);
            Assert.Equal(AgentId, agentId);
            return Task.FromResult(new MetricCatalogue([Received], null, Status, ClientOnline: false));
        }

        public Task<MetricHistoryResponse> QueryMetricsAsync(int tenantId, Guid agentId, MetricHistoryRange range, CancellationToken token = default)
        {
            Assert.Equal(9, tenantId);
            Assert.Equal(AgentId, agentId);
            Queries.Add((range, token));
            return Queries.Count == 1 && HoldFirstQuery is { } held ? held.Task : Task.FromResult(Response(range, "128000"));
        }

        public static MetricHistoryResponse Response(MetricHistoryRange range, string bytes) =>
            new(range.FromUtc, range.ToExclusiveUtc, 60, range.FromUtc.AddDays(-1), range.FromUtc.AddDays(-1), "available", Status,
                [new(Received, new(1, 512, range.FromUtc, 512, 512, 512, 60_000_000, bytes, null, null,
                    MetricHistoryQuality.None, null), [], [], false)]);
    }

    private sealed class WaitingStream(Action closed) : Stream
    {
        private readonly TaskCompletionSource<int> _readCancelled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposed;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set { } }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => 0;
        public override long Seek(long offset, SeekOrigin origin) => 0;
        public override void SetLength(long value) { }
        public override void Write(byte[] buffer, int offset, int count) { }

        protected override void Dispose(bool disposing)
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) closed();
            base.Dispose(disposing);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            new(WaitForCancellationAsync(cancellationToken));

        private async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(
                static state => ((TaskCompletionSource<int>)state!).TrySetCanceled(),
                _readCancelled);
            return await _readCancelled.Task;
        }
    }
}
