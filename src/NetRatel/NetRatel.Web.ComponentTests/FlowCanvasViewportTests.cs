using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Web.Components.Pages.Flows;
using NetRatel.Web.Services.Flows;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FlowCanvasViewportTests : AsyncBunitContext
{
    [Fact]
    public async Task Capture_Waits_For_Active_Fit_And_Completion_Reports_The_Final_Viewport()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./_content/VeloxDev.Razor/veloxdev.workflow.js").Mode = JSRuntimeMode.Loose;
        JSInterop.SetupModule("./js/flows-connections.js").Mode = JSRuntimeMode.Loose;
        var module = JSInterop.SetupModule("./js/flows-editor.js"); module.Mode = JSRuntimeMode.Loose;
        module.Setup<double[]>("geometry", _ => true).SetResult([23, 45, 1000, 800, 1200, 1000]);
        module.Setup<double>("canvasToolsInset", _ => true).SetResult(50);
        var positioning = module.SetupVoid("fitPosition", _ => true);
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        await using var adapter = new FlowCanvasAdapter();
        await adapter.RestoreAsync(FlowGraphTemplates.IncidentFromAlert());
        var cut = Render<FlowCanvas>(p => p.Add(component => component.Adapter, adapter));
        await cut.WaitForAssertionAsync(() => Assert.True(cut.Instance.IsReady));
        var fitting = cut.InvokeAsync(() => cut.Find("[data-testid=flow-fit]").ClickAsync(new()));
        await cut.WaitForAssertionAsync(() => Assert.Equal("fitting", cut.Find("[data-testid=flow-canvas]").GetAttribute("data-fit-state")));
        await cut.WaitForAssertionAsync(() => Assert.Single(module.Invocations["fitPosition"]));
        Task<FlowGraphDto>? captured = null;
        await cut.InvokeAsync(() => { captured = cut.Instance.CaptureAsync(); });
        Assert.NotNull(captured); Assert.False(captured.IsCompleted);
        positioning.SetVoidResult();
        await fitting;
        var graph = await captured;
        Assert.Equal(23, graph.Viewport.ScrollX); Assert.Equal(45, graph.Viewport.ScrollY);
        await cut.WaitForAssertionAsync(() =>
        {
            Assert.Equal("complete", cut.Find("[data-testid=flow-canvas]").GetAttribute("data-fit-state"));
            Assert.Equal("1", cut.Find("[data-testid=flow-canvas]").GetAttribute("data-fit-revision"));
        });
        Assert.Equal(0, adapter.ExecutionCalls);
    }
}
