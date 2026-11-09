using System.Collections.Immutable;
using Bunit;
using Xunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Web.Components.Shared.Monitoring;
using NetRatel.Web.Services.Monitoring;

namespace NetRatel.Web.ComponentTests;

public sealed class MonitoringManagementTests : AsyncBunitContext
{
    public MonitoringManagementTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
    }

    [Fact]
    public async Task Complete_snapshot_is_searched_before_paging_with_independent_sections_and_real_totals()
    {
        var groups = Enumerable.Range(0, 107).Select(index => new MonitoringGroupDto(7, Guid.NewGuid(), 1, $"Group {index:000}", [Guid.NewGuid()])).ToImmutableArray();
        var config = new MonitoringConfigurationDto(7, 40, [], groups, [], DateTimeOffset.UtcNow);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/monitoring?tenantId=7&view=manage&manageTab=groups&search=unrelated-alert-filter&pageSize=100");
        var cut = Render<MonitoringManagement>(parameters => parameters.Add(x => x.Configuration, config)
            .Add(x => x.Permissions, new(7, true, false, false, false, false, false))
            .Add(x => x.QueryUri, navigation.Uri));
        Assert.Equal(20, cut.FindAll("tbody tr").Count);
        Assert.Contains("1–20 of 107 matching", cut.Find("[data-testid=manage-pager]").TextContent);
        await cut.Find("[data-testid=manage-next]").ClickAsync(new());
        Assert.Contains("21–40 of 107 matching", cut.Markup);
        await cut.Find("[data-testid=manage-search]").InputAsync(new() { Value = "Group 106" });
        Assert.Contains("Group 106", cut.Find("tbody").TextContent);
        Assert.Contains("1–1 of 1 matching", cut.Markup);
        Assert.Single(cut.FindAll("tbody tr"));
        await cut.Find("[data-testid=manage-tab-rules]").ClickAsync(new());
        Assert.Equal("", cut.Find("[data-testid=manage-search]").GetAttribute("value"));
        await cut.Find("[data-testid=manage-tab-groups]").ClickAsync(new());
        Assert.Equal("Group 106", cut.Find("[data-testid=manage-search]").GetAttribute("value"));
        Assert.All(cut.FindAll("tbody button"), button => Assert.True(button.HasAttribute("disabled")));
        Assert.Contains("search=unrelated-alert-filter", navigation.Uri);
        await cut.Find("[data-testid=manage-search]").InputAsync(new() { Value = "" });
        await cut.Find("[data-testid=manage-page-size]").ChangeAsync(new() { Value = "50" });
        Assert.Equal(50, cut.FindAll("tbody tr").Count);
        Assert.Contains("1–50 of 107 matching", cut.Markup);
        await cut.Find("[data-testid=manage-next]").ClickAsync(new());
        Assert.Contains("51–100 of 107 matching", cut.Markup);
        await cut.Find("[data-testid=manage-page-size]").ChangeAsync(new() { Value = "100" });
        Assert.Equal(100, cut.FindAll("tbody tr").Count);
        await cut.Find("[data-testid=manage-next]").ClickAsync(new());
        Assert.Equal(7, cut.FindAll("tbody tr").Count);
        Assert.Contains("101–107 of 107 matching", cut.Markup);
        cut.Render(parameters => parameters.Add(x => x.Configuration, config with { Groups = groups.Take(100).ToImmutableArray() }).Add(x => x.QueryUri, navigation.Uri));
        Assert.Contains("1–100 of 100 matching", cut.Markup);
    }

    [Fact]
    public void Projection_has_stable_duplicate_name_order_and_clamps_after_last_row_removal()
    {
        var first = Guid.Parse("11111111-1111-4111-8111-111111111111");
        var last = Guid.Parse("ffffffff-ffff-4fff-8fff-ffffffffffff");
        var query = new MonitoringManagementQuery { Page = 2 };
        var rows = new[] { new MonitoringGroupDto(7, last, 1, "Same", []), new MonitoringGroupDto(7, first, 1, "Same", []) };
        var page = query.Project(rows, row => row.Name, row => row.GroupId);
        Assert.Equal(first, page.Items[0].GroupId); Assert.Equal(1, page.Page);
        Assert.Equal("1–2 of 2 matching", page.Range);
    }
}
