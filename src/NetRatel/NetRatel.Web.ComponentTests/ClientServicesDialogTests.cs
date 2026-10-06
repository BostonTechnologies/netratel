using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using MudBlazor;
using NetRatel.Shared.Contracts.Services;
using NetRatel.Web.Components.Dialogs;
using NetRatel.Web.Services.Services;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class ClientServicesDialogTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("66e174db-af23-4e44-a8dc-34fc8f734f7b");
    private readonly FakeApi _api = new();
    private readonly FakeLive _live = new();

    public ClientServicesDialogTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IClientServicesApiService>(_api);
        Services.AddSingleton<IClientServicesLiveStreamService>(_live);
    }

    [Fact]
    public void Keeps_Offline_Complete_Inventory_And_Failed_Attempt_Distinct()
    {
        _api.Model = Model(connected: false) with
        {
            SupportsServices = false,
            LatestAttempt = new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Partial,
                1, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0, "inventory_limit")
        };
        var cut = RenderDialog();
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='services-summary']").TextContent.Should().Contain("Cached offline inventory").And.Contain("Stale inventory");
            cut.Find("[data-testid='services-attempt']").TextContent.Should().Contain("Partial").And.Contain("does not prove a service is missing");
            cut.Find("[data-testid='services-refresh']").HasAttribute("disabled").Should().BeTrue();
            cut.FindAll("[data-testid='service-row']").Should().HaveCount(3);
            cut.FindAll("[data-testid='services-unsupported']").Should().BeEmpty();
            _api.RefreshCount.Should().Be(0);
        });
    }

    [Fact]
    public void Uses_Selected_Watch_Evidence_Without_Auto_Monitoring_Stopped_Inventory()
    {
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='service-row']").Should().HaveCount(3));
        var watched = cut.Find("[data-service-name='worker.service']");
        watched.TextContent.Should().Contain("Failed").And.Contain("Monitored").And.Contain("Selected watch");
        cut.Find("[data-service-name='disabled.service']").TextContent.Should().Contain("Not monitored").And.Contain("Enablement: disabled");
        cut.Find("[data-service-name='oneshot.service']").TextContent.Should().Contain("Running").And.Contain("Sub: exited");
        cut.Find("[data-testid='services-filter-stopped']").Click();
        cut.Find("[data-testid='services-filter-stopped']").GetAttribute("aria-pressed").Should().Be("true");
        cut.Find("[data-testid='services-filter-all']").GetAttribute("aria-pressed").Should().Be("false");
        cut.FindAll("[data-testid='service-row']").Should().ContainSingle();
        cut.Find("[data-testid='services-filter-monitored']").Click();
        cut.FindAll("[data-testid='service-row']").Should().ContainSingle();
        cut.Find("[data-testid='services-filter-all']").Click();
        cut.Find("[data-testid='services-search']").Input("One Shot");
        cut.FindAll("[data-testid='service-row']").Should().ContainSingle();
        cut.Find("[data-testid='service-row']").TextContent.Should().Contain("oneshot.service");
    }

    [Theory]
    [InlineData(ClientServicesRefreshStatus.Requested, "Refresh requested")]
    [InlineData(ClientServicesRefreshStatus.Offline, "No refresh was requested")]
    [InlineData(ClientServicesRefreshStatus.Unsupported, "does not support")]
    [InlineData(ClientServicesRefreshStatus.Throttled, "Try again after")]
    public void Reports_Actual_Refresh_Admission_Without_Claiming_Fresh_Collection(ClientServicesRefreshStatus status, string message)
    {
        _api.RefreshResponse = new(status, status == ClientServicesRefreshStatus.Requested ? Guid.NewGuid() : null, DateTimeOffset.UtcNow.AddSeconds(15));
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("[data-testid='services-refresh']").HasAttribute("disabled").Should().BeFalse());
        cut.Find("[data-testid='services-refresh']").Click();
        cut.WaitForAssertion(() => cut.Find("[data-testid='services-refresh-result']").TextContent.Should().Contain(message));
        _api.RefreshCount.Should().Be(1);
        cut.FindAll("[data-testid='service-row']").Should().HaveCount(3);
    }

    [Fact]
    public async Task Live_Refresh_Replaces_Complete_Cache_And_Ignores_Wrong_Client_And_Older_Read_Model()
    {
        var cut = RenderDialog();
        cut.WaitForAssertion(() => _live.Subscriptions.Should().Be(1));
        cut.Find("[data-testid='services-refresh']").Click();
        var newModel = Model() with
        {
            GeneratedAtUtc = DateTimeOffset.UtcNow.AddSeconds(1),
            LastCompleteInventory = new(Guid.NewGuid(), 1, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
                [Service("new.service", "New", ClientServiceState.Running)]),
            WatchedServices = [], MonitoredServiceNames = []
        };
        await _live.Events.Writer.WriteAsync(new("Live", newModel with { TenantId = 99 }));
        await _live.Events.Writer.WriteAsync(new("Live", newModel));
        await _live.Events.Writer.WriteAsync(new("Live", Model()));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='service-row']").Should().ContainSingle();
            cut.Find("[data-testid='service-row']").TextContent.Should().Contain("new.service");
            cut.Find("[data-testid='services-refresh-result']").TextContent.Should().Contain("Complete inventory received");
        });
    }

    [Fact]
    public async Task Close_Cancels_Owned_Stream_And_Fences_A_Late_Refresh_Response()
    {
        _api.PendingRefresh = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => _live.Subscriptions.Should().Be(1));
        cut.Find("[data-testid='services-refresh']").Click();
        cut.WaitForAssertion(() => _api.RefreshCount.Should().Be(1));
        cut.Find("[data-testid='services-refresh']").HasAttribute("disabled").Should().BeTrue();
        cut.Find("[data-testid='close-services']").Click();
        await _live.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        _api.PendingRefresh.SetResult(new(ClientServicesRefreshStatus.Requested, Guid.NewGuid()));
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.FindAll("[data-testid='services-refresh-result']").Should().BeEmpty();
    }

    [Fact]
    public void Unsupported_And_Empty_Are_Explicit_And_Do_Not_Request_Collection()
    {
        _api.Model = Model() with { SupportsServices = false, LastCompleteInventory = null, WatchedServices = [], MonitoredServiceNames = [] };
        var cut = RenderDialog();
        cut.WaitForAssertion(() =>
        {
            cut.Find("[data-testid='services-unsupported']").TextContent.Should().Contain("unsupported");
            cut.Find("[data-testid='services-empty']").TextContent.Should().Contain("No service inventory");
            cut.Find("[data-testid='services-refresh']").HasAttribute("disabled").Should().BeTrue();
        });
        _api.RefreshCount.Should().Be(0);
    }

    [Fact]
    public void Read_Error_Exposes_No_Exception_Or_Routing_Details()
    {
        _api.FailRead = true;
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.Find("[data-testid='services-error']").TextContent.Should().Contain("Could not load services"));
        cut.Markup.Should().NotContain("secret-debug-route");
        _live.Subscriptions.Should().Be(0);
    }

    [Fact]
    public async Task Tenant_And_Client_Change_Cancels_Read_And_Fences_Its_Late_Response()
    {
        _api.PendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var cut = RenderDialog();
        cut.WaitForAssertion(() => _api.ReadCount.Should().Be(1));
        var nextAgent = Guid.NewGuid();
        cut.Render(parameters => parameters.Add(component => component.TenantId, 7).Add(component => component.AgentId, nextAgent));
        cut.WaitForAssertion(() =>
        {
            _api.ReadCount.Should().Be(2);
            _api.FirstReadToken.IsCancellationRequested.Should().BeTrue();
            cut.FindAll("[data-testid='service-row']").Should().HaveCount(3);
            cut.Find(".client-services-identity").TextContent.Should().Contain("Tenant 7");
        });
        _api.PendingRead.SetResult(Model() with
        {
            LastCompleteInventory = Model().LastCompleteInventory! with { Services = [Service("old-tenant.service", "Old tenant", ClientServiceState.Running)] }
        });
        await cut.InvokeAsync(() => Task.CompletedTask);
        cut.Markup.Should().NotContain("old-tenant.service");
    }

    [Fact]
    public async Task Durable_Revision_Fences_Late_Events_Even_When_Server_Clock_Moves_Backward()
    {
        var initial = Model() with { Revision = 10, GeneratedAtUtc = DateTimeOffset.UtcNow };
        _api.Model = initial;
        var cut = RenderDialog();
        cut.WaitForAssertion(() => _live.Subscriptions.Should().Be(1));
        var accepted = initial with
        {
            Revision = 11, GeneratedAtUtc = initial.GeneratedAtUtc.AddMinutes(-1),
            LastCompleteInventory = initial.LastCompleteInventory! with { Services = [Service("newer-revision.service", "Newer revision", ClientServiceState.Running)] },
            WatchedServices = [], MonitoredServiceNames = []
        };
        await _live.Events.Writer.WriteAsync(new("Live", accepted));
        await _live.Events.Writer.WriteAsync(new("Live", initial with { GeneratedAtUtc = initial.GeneratedAtUtc.AddHours(1) }));
        cut.WaitForAssertion(() =>
        {
            cut.FindAll("[data-testid='service-row']").Should().ContainSingle();
            cut.Find("[data-testid='service-row']").TextContent.Should().Contain("newer-revision.service");
        });
    }

    [Fact]
    public void Windows_Name_Merges_Case_Insensitive_Watch_While_Systemd_Names_Remain_Distinct()
    {
        var windows = Service("Spooler", "Print Spooler", ClientServiceState.Stopped) with { Platform = ClientServicePlatform.Windows, StartMode = "Automatic" };
        _api.Model = Model() with
        {
            LastCompleteInventory = Model().LastCompleteInventory! with
            {
                Services = [windows, Service("Case.service", "Upper", ClientServiceState.Stopped), Service("case.service", "Lower", ClientServiceState.Running)]
            },
            WatchedServices = [windows with { Name = "spooler", State = ClientServiceState.Running }],
            MonitoredServiceNames = ["SPOOLER", "case.service"]
        };
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='service-row']").Should().HaveCount(3));
        cut.Find("[data-service-name='spooler']").TextContent.Should().Contain("Monitored").And.Contain("Running");
        cut.Find("[data-testid='services-filter-monitored']").Click();
        cut.FindAll("[data-testid='service-row']").Should().HaveCount(2);
    }

    [Fact]
    public void Selected_Name_Without_Evidence_Is_Unknown_And_Future_Observation_Reports_Clock_Skew()
    {
        _api.Model = Model() with
        {
            MonitoredServiceNames = ["not-observed.service"], WatchedServices = [],
            LastCompleteInventory = Model().LastCompleteInventory! with
            { Services = [Service("future.service", "Future", ClientServiceState.Running) with { ObservedAtUtc = DateTimeOffset.UtcNow.AddHours(1) }] }
        };
        var cut = RenderDialog();
        cut.WaitForAssertion(() => cut.FindAll("[data-testid='service-row']").Should().HaveCount(2));
        cut.Find("[data-service-name='future.service']").TextContent.Should().Contain("Client clock is ahead");
        cut.Find("[data-testid='services-filter-monitored']").Click();
        var row = cut.Find("[data-testid='service-row']");
        row.TextContent.Should().Contain("Unknown").And.Contain("Awaiting selected watch").And.Contain("No watch evidence yet").And.NotContain("Missing");
        row.QuerySelectorAll("time").Should().BeEmpty();
    }

    [Fact]
    public async Task Launcher_Routing_Key_Change_Closes_Viewer_And_Cancels_Its_Stream()
    {
        var provider = Render<MudDialogProvider>();
        var launcher = Render<ClientServicesLauncher>(parameters => parameters
            .Add(component => component.TenantId, 3).Add(component => component.AgentId, AgentId));
        launcher.Find("[data-testid='client-services-launcher']").Click();
        provider.WaitForAssertion(() => _live.Subscriptions.Should().Be(1));
        launcher.Render(parameters => parameters.Add(component => component.TenantId, 7).Add(component => component.AgentId, Guid.NewGuid()));
        await _live.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(2));
        provider.WaitForAssertion(() => provider.FindAll("[data-testid='client-services-dialog']").Should().BeEmpty());
    }

    private IRenderedComponent<ClientServicesDialog> RenderDialog() => Render<ClientServicesDialog>(parameters => parameters
        .Add(component => component.TenantId, 3).Add(component => component.AgentId, AgentId)
        .Add(component => component.HostLabel, "fixture-client").Add(component => component.Embedded, true));

    private static ClientServicesReadModelDto Model(bool connected = true) => new(3, AgentId,
        new(Guid.Parse("c1e5bfc7-3938-47ed-80a0-6f7c6f54e443"), 1, 1, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1),
            [Service("worker.service", "Worker", ClientServiceState.Running), Service("disabled.service", "Disabled", ClientServiceState.Stopped),
             Service("oneshot.service", "One Shot", ClientServiceState.Running, subState: "exited")]), null,
        [Service("worker.service", "Worker", ClientServiceState.Failed)], ["worker.service"], 1, connected, true, DateTimeOffset.UtcNow);

    private static ClientServiceObservation Service(string name, string displayName, ClientServiceState state, string subState = "dead") =>
        new(name, displayName, ClientServicePlatform.LinuxSystemd, state, state.ToString().ToLowerInvariant(), null, "loaded",
            state == ClientServiceState.Running ? "active" : "inactive", subState, "disabled", DateTimeOffset.UtcNow.AddHours(-1));

    private sealed class FakeApi : IClientServicesApiService
    {
        public ClientServicesReadModelDto Model { get; set; } = ClientServicesDialogTests.Model();
        public bool FailRead { get; set; }
        public int RefreshCount { get; private set; }
        public int ReadCount { get; private set; }
        public CancellationToken FirstReadToken { get; private set; }
        public TaskCompletionSource<ClientServicesReadModelDto?>? PendingRead { get; set; }
        public TaskCompletionSource<ClientServicesRefreshResponse>? PendingRefresh { get; set; }
        public ClientServicesRefreshResponse RefreshResponse { get; set; } = new(ClientServicesRefreshStatus.Requested, Guid.NewGuid());
        public Task<ClientServicesReadModelDto?> GetAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
        {
            ReadCount++;
            if (ReadCount == 1)
            {
                FirstReadToken = cancellationToken;
                if (PendingRead is not null) return PendingRead.Task;
            }
            return FailRead ? Task.FromException<ClientServicesReadModelDto?>(new HttpRequestException("secret-debug-route")) :
                Task.FromResult<ClientServicesReadModelDto?>(Model with { TenantId = tenantId, AgentId = agentId });
        }
        public Task<ClientServicesRefreshResponse> RefreshAsync(int tenantId, Guid agentId, CancellationToken cancellationToken = default)
        {
            RefreshCount++;
            return PendingRefresh?.Task ?? Task.FromResult(RefreshResponse);
        }
    }

    private sealed class FakeLive : IClientServicesLiveStreamService
    {
        public Channel<ClientServicesLiveEvent> Events { get; } = Channel.CreateUnbounded<ClientServicesLiveEvent>();
        public int Subscriptions { get; private set; }
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ClientServicesLiveEvent> SubscribeAsync(int tenantId, Guid agentId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Subscriptions++;
            try { await foreach (var item in Events.Reader.ReadAllAsync(cancellationToken)) yield return item; }
            finally { Cancelled.TrySetResult(); }
        }
    }
}
