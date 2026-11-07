using System.Net;
using AwesomeAssertions;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed partial class FlowsPageTests
{
    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task History_link_metadata_errors_are_visible_without_opening_an_unauthorized_viewer(HttpStatusCode status)
    {
        var id = Guid.NewGuid();
        _api.MetadataLookup = (_, _, _) => Task.FromException<FlowSummaryDto>(new FlowApiException(status));
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/flows?tenantId=17&flowId={id:D}");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flows-error]").TextContent.Should().Contain(
            status == HttpStatusCode.NotFound ? "unavailable" : "permission"));
        _dialogs.FindAll("[data-testid=flow-history-panel]").Should().BeEmpty();
        _api.MetadataLookup = (_, _, _) => Task.FromResult(FakeFlowApi.Summary(FlowEditorTests.Definition()) with { Id = id });
        await cut.Find("[data-testid=flows-retry]").ClickAsync(new());
        await _dialogs.WaitForAssertionAsync(() => _dialogs.FindAll("[data-testid=flow-history-panel]").Should().HaveCount(1));
        cut.FindAll("[data-testid=flows-error]").Should().BeEmpty();
    }

    [Fact]
    public async Task Replaced_history_link_fences_a_late_metadata_response_and_retains_deleted_history()
    {
        var old = FakeFlowApi.Summary(FlowEditorTests.Definition()) with { Name = "Old linked history" };
        var current = old with { Id = Guid.NewGuid(), Name = "New linked history", Enabled = false, DeletedAtUtc = DateTimeOffset.UtcNow };
        var pending = new TaskCompletionSource<FlowSummaryDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        _api.MetadataLookup = (_, id, token) =>
        {
            if (id != old.Id) return Task.FromResult(current);
            oldToken = token; return pending.Task;
        };
        _api.VersionsLookup = (_, _, _) => Task.FromResult<IReadOnlyList<FlowVersionDto>>([]);
        _api.RunsLookup = (_, _, _) => Task.FromResult<IReadOnlyList<FlowRunSummaryDto>>([]);
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/flows?tenantId=17&flowId={old.Id:D}");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => oldToken.CanBeCanceled.Should().BeTrue());
        await cut.InvokeAsync(() => navigation.NavigateTo($"/flows?tenantId=17&flowId={current.Id:D}"));
        await _dialogs.WaitForAssertionAsync(() => _dialogs.Markup.Should().Contain("New linked history").And.Contain("Deleted flow"));
        oldToken.IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() => pending.SetResult(old));
        _dialogs.Markup.Should().Contain("New linked history").And.NotContain("Old linked history");
        cut.FindAll("[data-testid=flows-error]").Should().BeEmpty();
    }

    [Fact]
    public async Task Delete_capability_is_independent_of_edit_and_cancel_performs_no_mutation()
    {
        _api.TenantAccess = [new(17, "Tenant 17", false, false, false) { CanDelete = true }];
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.FindAll("[data-testid=flow-delete]").Should().HaveCount(1));
        cut.FindAll("[data-testid=flow-clone]").Should().BeEmpty();
        cut.Find("[data-testid=flow-edit]").GetAttribute("aria-label").Should().StartWith("View draft");
        cut.Find("[data-testid=flow-delete]").Click();
        await _dialogs.WaitForAssertionAsync(() => _dialogs.Markup.Should().Contain("90 days").And.Contain(FlowEditorTests.Definition().Name));
        _dialogs.Find("[data-testid=flow-delete-cancel]").Click();
        _api.DeleteCalls.Should().Be(0);
    }

    [Fact]
    public async Task Actual_toggle_remains_red_and_unpressed_during_delayed_and_failed_write()
    {
        var flow = FlowEditorTests.Definition() with { Enabled = false };
        _api.QueryLookup = (tenant, query, _) => Task.FromResult(new FlowPageDto([FakeFlowApi.Summary(flow)], 1, query.PageIndex, query.PageSize));
        var pending = new TaskCompletionSource<FlowDefinitionDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        _api.EnabledLookup = (tenant, id, request, _) =>
        {
            tenant.Should().Be(17); id.Should().Be(flow.Id); request.Should().Be(new FlowEnabledRequest(flow.Revision, true));
            return pending.Task;
        };
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-enabled]").GetAttribute("aria-pressed").Should().Be("false"));
        var clicking = cut.Find("[data-testid=flow-enabled]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() =>
        {
            var button = cut.Find("[data-testid=flow-enabled]");
            button.HasAttribute("disabled").Should().BeTrue(); button.GetAttribute("aria-pressed").Should().Be("false");
            cut.FindComponent<MudToggleIconButton>().Instance.Toggled.Should().BeFalse();
        });
        await cut.InvokeAsync(() => pending.SetException(new HttpRequestException("Synthetic lost response")));
        await clicking;
        cut.Find("[data-testid=flow-enabled]").GetAttribute("aria-pressed").Should().Be("false");
        cut.Find("[data-testid=flows-error]").TextContent.Should().Contain("not confirmed");
        _api.EnabledCalls.Should().Be(1); _api.Queries.Should().HaveCountGreaterThan(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_old_tenant_or_query_page_cannot_replace_the_current_result(bool sameTenant)
    {
        _api.TenantAccess = [new(17, "Tenant 17", true, true, true), new(23, "Tenant 23", true, false, false)];
        var old = new TaskCompletionSource<FlowPageDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var flow = FakeFlowApi.Summary(FlowEditorTests.Definition());
        _api.QueryLookup = (tenant, query, token) =>
        {
            if (tenant == 17 && (!sameTenant || string.IsNullOrEmpty(query.Search))) { oldToken = token; return old.Task; }
            return Task.FromResult(new FlowPageDto([flow with { TenantId = sameTenant ? 17 : 23, Name = "New current result" }], 1, query.PageIndex, query.PageSize));
        };
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => oldToken.CanBeCanceled.Should().BeTrue());
        if (sameTenant) await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo("/flows?tenantId=17&search=current"));
        else await cut.Find("[data-testid=flow-tenant]").ChangeAsync(new() { Value = "23" });
        await cut.WaitForAssertionAsync(() => cut.Markup.Should().Contain("New current result"));
        oldToken.IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() => old.SetResult(new([flow with { Name = "Late old result" }], 1, 0, 20)));
        cut.Markup.Should().Contain("New current result").And.NotContain("Late old result");
    }

    [Fact]
    public async Task Real_page_sizes_and_delete_last_row_reload_and_clamp_the_authoritative_query()
    {
        var flow = FakeFlowApi.Summary(FlowEditorTests.Definition()); var deleted = false;
        _api.QueryLookup = (tenant, query, _) =>
        {
            var total = deleted ? 20 : 21;
            var items = query.PageIndex == 1 && deleted ? Array.Empty<FlowSummaryDto>() : new[] { flow };
            return Task.FromResult(new FlowPageDto(items, total, query.PageIndex, query.PageSize));
        };
        _api.DeleteLookup = (tenant, id, request, _) =>
        {
            request.ExpectedRevision.Should().Be(flow.Revision); deleted = true;
            return Task.FromResult(new FlowDeleteResultDto(tenant, id, flow.Revision + 1, FlowDeleteDisposition.Deleted, "flow-deleted"));
        };
        Services.GetRequiredService<NavigationManager>().NavigateTo("/flows?tenantId=17&page=1");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => _api.Queries.Should().Contain(query => query.PageIndex == 1));
        cut.Find("[data-testid=flow-delete]").Click();
        await _dialogs.Find("[data-testid=flow-delete-confirm]").ClickAsync(new());
        await cut.WaitForAssertionAsync(() => _api.Queries.Last().PageIndex.Should().Be(0));
        var table = cut.FindComponent<MudTable<FlowSummaryDto>>();
        foreach (var size in new[] { 50, 100 })
        {
            await cut.InvokeAsync(() => table.Instance.SetRowsPerPage(size));
            await cut.WaitForAssertionAsync(() => _api.Queries.Last().PageSize.Should().Be(size));
        }
        _api.Queries.Should().Contain(query => query.PageSize == 20);
    }

    [Fact]
    public async Task Failed_read_displays_error_instead_of_successful_empty_state()
    {
        _api.QueryLookup = (_, _, _) => Task.FromException<FlowPageDto>(new FlowApiException(HttpStatusCode.Forbidden));
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flows-error]").TextContent.Should().Contain("permission"));
        cut.FindAll("[data-testid=flows-empty]").Should().BeEmpty();
    }
}
