using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Shared.Monitoring;
using NetRatel.Web.Services.Monitoring;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringTargetPickerTests : AsyncBunitContext
{
    private readonly PickerApi _api = new();
    public MonitoringTargetPickerTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IMonitoringApiService>(_api);
    }

    [Fact]
    public async Task SelectionAndIdentitySurvivePagesSearchAndGroupUnion()
    {
        var selected = new HashSet<Guid>();
        var groupId = Guid.NewGuid();
        var groups = new HashSet<Guid> { groupId };
        var cut = Render<MonitoringTargetPicker>(parameters => parameters
            .Add(component => component.TenantId, 1)
            .Add(component => component.SelectedAgents, selected)
            .Add(component => component.SelectedGroups, groups)
            .Add(component => component.Groups, [new(1, groupId, 1, "Database servers", [_api.First])])
            .Add(component => component.Metric, MonitoringMetricKind.CpuUsagePercent));
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll("[data-testid='monitoring-client-choice']")));
        cut.Find("[data-testid='monitoring-client-choice']").Change(true);
        cut.Find("[data-testid='monitoring-next-clients']").Click();
        cut.WaitForAssertion(() => Assert.Contains("Second workstation", cut.Markup));
        Assert.Contains(_api.First, selected);
        Assert.Contains("First workstation", cut.Markup);
        Assert.Contains("1 effective clients", cut.Markup);
        Assert.DoesNotContain("Services unsupported", cut.Markup);
        var search = cut.FindComponent<MudTextField<string>>();
        await cut.InvokeAsync(() => search.Instance.ValueChanged.InvokeAsync("10.0.0.2"));
        cut.WaitForAssertion(() => Assert.Equal("10.0.0.2", _api.LastSearch));
        Assert.Contains(_api.First, selected);
        Assert.Contains("First workstation", cut.Markup);
        Assert.Contains("10.0.0.1", cut.Markup);
    }

    [Fact]
    public async Task ExistingOffPageSelectionUsesBatchedIdentityAndCanBeRemoved()
    {
        var selected = new HashSet<Guid> { _api.Second };
        var cut = Render<MonitoringTargetPicker>(parameters => parameters
            .Add(component => component.TenantId, 1)
            .Add(component => component.SelectedAgents, selected));
        cut.WaitForAssertion(() => Assert.Contains("Second workstation", cut.Markup));
        Assert.Equal(1, _api.IdentityReads);
        var chip = cut.FindComponents<MudChip<Guid>>().Single();
        await cut.InvokeAsync(() => chip.Instance.OnClose.InvokeAsync(chip.Instance));
        Assert.Empty(selected);
    }

    private sealed class PickerApi : MonitoringTestApi, IMonitoringApiService
    {
        public Guid First { get; } = Guid.Parse("11111111-1111-1111-1111-111111111111");
        public Guid Second { get; } = Guid.Parse("22222222-2222-2222-2222-222222222222");
        public string? LastSearch { get; private set; }
        public int IdentityReads { get; private set; }
        public Task<MonitoringClientPageDto> SearchClientsAsync(int tenantId, string? search, string? cursor = null, CancellationToken token = default)
        {
            LastSearch = search;
            var second = cursor is not null || search is not null;
            var id = second ? Second : First;
            var identity = new MonitoringClientIdentityDto(id, second ? "Second workstation" : "First workstation", second ? "host-b" : "host-a", second ? "10.0.0.2" : "10.0.0.1");
            return Task.FromResult(new MonitoringClientPageDto([new(id, identity.DisplayName, null, MonitoringTargetSupport.Unsupported, "services-unsupported", identity)], second ? null : "next", 300));
        }
        public Task<IReadOnlyList<MonitoringClientIdentityDto>> GetClientIdentitiesAsync(int tenantId, IReadOnlyCollection<Guid> ids, CancellationToken token = default)
        {
            IdentityReads++;
            Assert.Equal(new[] { Second }, ids);
            return Task.FromResult<IReadOnlyList<MonitoringClientIdentityDto>>([new(Second, "Second workstation", "host-b", "10.0.0.2")]);
        }
    }
}
