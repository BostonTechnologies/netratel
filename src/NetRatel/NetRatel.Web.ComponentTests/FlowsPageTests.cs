using System.Net;
using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowsPageTests : AsyncBunitContext
{
    private readonly FakeFlowApi _api = new();
    public FlowsPageTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/VeloxDev.Razor/veloxdev.workflow.js").Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./js/flows-editor.js"); module.Mode = JSRuntimeMode.Loose;
        module.Setup<double[]>("geometry", _ => true).SetResult([0, 0, 1000, 800, 1200, 1000]);
        Services.AddSingleton<IFlowApiService>(_api);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task New_Result_Query_Cancels_And_Fences_The_Prior_Same_Tenant_Response(bool oldFails)
    {
        var old = Version(1, "Old result"); var latest = Version(2, "Latest result");
        // Deliberately ignore cancellation so the old HTTP completion still exercises admission.
        var pending = new TaskCompletionSource<FlowVersionDto>(); CancellationToken oldToken = default;
        _api.VersionLookup = (_, id, token) => id == old.Id ? CaptureOld(token) : Task.FromResult(latest);
        Task<FlowVersionDto> CaptureOld(CancellationToken token) { oldToken = token; return pending.Task; }
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo($"/flows?tenantId=17&version={old.Id:D}");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => oldToken.CanBeCanceled.Should().BeTrue());
        await cut.InvokeAsync(() => navigation.NavigateTo($"/flows?tenantId=17&version={latest.Id:D}"));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Immutable version 2"));
        oldToken.IsCancellationRequested.Should().BeTrue();
        await cut.InvokeAsync(() =>
        {
            if (oldFails) pending.SetException(new FlowApiException(HttpStatusCode.NotFound)); else pending.SetResult(old);
        });
        cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Immutable version 2");
        cut.Markup.Should().Contain("Latest result").And.NotContain("Old result");
        cut.FindAll("[data-testid=flows-error]").Should().BeEmpty();
    }

    [Fact]
    public async Task Unauthorized_Result_Query_Clears_The_Previous_Tenant_Viewer()
    {
        var version = Version(1, "Authorized graph"); var lookups = 0;
        _api.VersionLookup = (_, _, _) => { lookups++; return Task.FromResult(version); };
        var navigation = Services.GetRequiredService<NavigationManager>(); navigation.NavigateTo($"/flows?tenantId=17&version={version.Id:D}");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-dirty]").TextContent.Should().Contain("Immutable version 1"));
        await cut.InvokeAsync(() => navigation.NavigateTo($"/flows?tenantId=91&version={version.Id:D}"));
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flows-error]").TextContent.Should().Contain("does not grant Flows access"));
        cut.FindAll("[data-testid=flow-editor]").Should().BeEmpty();
        cut.Find("[data-testid=flow-tenant]").GetAttribute("value").Should().Be("0");
        lookups.Should().Be(1);
    }

    [Theory]
    [InlineData("NR-2026-000123", "https://fixture.invalid/incidents/123", true)]
    [InlineData(null, "javascript:alert(1)", false)]
    public async Task Run_Receipt_Separates_Code_Identity_Tracking_And_The_Optional_Safe_Link(string? tracking, string link, bool hasLink)
    {
        var version = Version(1, "Receipt graph"); var definition = FlowEditorTests.Definition(); var runId = Guid.NewGuid();
        var run = new FlowRunDetailDto(new(runId, definition.Id, version.Id, Guid.NewGuid(), Guid.NewGuid(), FlowRunStatus.Succeeded, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "incident-created"),
            new(Guid.NewGuid(), Guid.NewGuid(), "CPU rule", "SQL server", "cpu", "cpu.usage.percent", "warning", 96, null, DateTimeOffset.UtcNow),
            [new(Guid.NewGuid(), "action", FlowActionStatus.Succeeded, 1, 1, "incident-created", new("123", tracking, link))]);
        _api.RunLookup = (tenant, id, _) => { tenant.Should().Be(17); id.Should().Be(runId); return Task.FromResult(run); };
        _api.VersionLookup = (_, _, _) => Task.FromResult(version);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/flows?tenantId=17&run={runId:D}");
        var cut = Render<FlowsPage>();
        await cut.WaitForAssertionAsync(() => cut.Find("[data-testid=flow-action-result]").TextContent.Should().Contain("Status: Succeeded · Attempts: 1"));
        var result = cut.Find("[data-testid=flow-action-result]");
        result.QuerySelector("[data-testid=flow-action-code]")!.TextContent.Should().Be("incident-created");
        result.QuerySelector("[data-testid=flow-incident-id]")!.TextContent.Should().Be("123");
        result.QuerySelectorAll("dt").Select(e => e.TextContent).Should().Contain("Incident ID");
        if (tracking is not null)
        {
            result.QuerySelectorAll("dt").Select(e => e.TextContent).Should().Contain("Tracking number");
            result.QuerySelector("[data-testid=flow-incident-tracking]")!.TextContent.Should().Be(tracking);
        }
        else result.QuerySelector("[data-testid=flow-incident-tracking]").Should().BeNull();
        var anchor = result.QuerySelector("a");
        if (hasLink) { anchor!.GetAttribute("href").Should().Be(link); anchor.GetAttribute("rel").Should().Be("noopener noreferrer"); }
        else anchor.Should().BeNull();
        _api.SaveCalls.Should().Be(0); _api.PublishCalls.Should().Be(0);
    }

    private static FlowVersionDto Version(int number, string triggerName)
    {
        var definition = FlowEditorTests.Definition();
        var graph = definition.Draft with { Nodes = definition.Draft.Nodes.Select(n => n.Kind == FlowNodeKind.AlertRaised ? n with { Name = triggerName } : n).ToArray() };
        return new(Guid.NewGuid(), definition.Id, 17, number, graph, "component-proof", "Operator", DateTimeOffset.UtcNow);
    }
}
