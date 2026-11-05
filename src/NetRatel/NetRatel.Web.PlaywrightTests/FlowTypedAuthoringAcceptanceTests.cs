using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class FlowTypedAuthoringAcceptanceTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;

    [Fact]
    public async Task Near_Limit_Graph_Mount_Fit_Pan_Zoom_And_Route_Are_Measured_Without_Execution()
    {
        var fixture = _fixture!;
        var flow = Assert.Single(await fixture.FlowsData.ListAsync(17));
        var graph = NearLimitGraph();
        Assert.Equal(FlowGraphV2Limits.MaximumNodes, graph.Nodes.Count); Assert.Equal(61, graph.Edges.Count);
        Assert.True(FlowGraphV2Validator.ValidateDraft(graph).Valid);
        await fixture.FlowsData.SaveAsync(17, flow.Id, new(flow.Revision, "Synthetic near-limit graph", graph));
        var seedWrites = fixture.FlowsData.SaveCalls;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1000 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await OpenAsync(page, fixture);
        var clock = Stopwatch.StartNew();
        await page.GetByTestId("flow-edit").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(32);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(61);
        var mountMilliseconds = clock.Elapsed.TotalMilliseconds;
        clock.Restart(); await page.GetByTestId("flow-fit").ClickAsync();
        await page.GetByTestId("flow-canvas").ScrollIntoViewIfNeededAsync();
        await page.WaitForFunctionAsync("""
            () => {
                const panel=document.querySelector('[data-testid=flow-canvas]'),p=panel?.getBoundingClientRect();
                const nodes=[...(panel?.querySelectorAll('.flow-node')??[])];
                return nodes.length===32&&nodes.every(node=>{
                    const r=node.getBoundingClientRect();
                    return r.width>0&&r.height>0&&r.left>=p.left-1&&r.right<=p.right+1&&r.top>=p.top-1&&r.bottom<=p.bottom+1;
                });
            }
            """);
        var fitMilliseconds = clock.Elapsed.TotalMilliseconds;
        Assert.True(await page.GetByTestId("flow-canvas").EvaluateAsync<bool>("""
            panel => [...panel.querySelectorAll('[data-node-kind=RunLibraryScript]')].every(node=>{
                const r=node.getBoundingClientRect(),ports=[...node.querySelectorAll('.flow-port.output')];
                return ports.length===7&&ports.every(port=>{const p=port.getBoundingClientRect();return p.top>=r.top&&p.bottom<=r.bottom+1});
            })
            """), "Every named outcome must fit its actual adaptive card height.");
        var fitted = await MeasurementsAsync(page);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-fit.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });

        var panel = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        var before = (await page.Locator(".flow-node").First.BoundingBoxAsync())!;
        clock.Restart();
        await page.Mouse.MoveAsync(panel.X + panel.Width - 12, panel.Y + panel.Height - 12);
        await page.Mouse.DownAsync(new() { Button = MouseButton.Middle });
        await page.Mouse.MoveAsync(panel.X + panel.Width - 72, panel.Y + panel.Height - 52, new() { Steps = 12 });
        await page.Mouse.UpAsync(new() { Button = MouseButton.Middle });
        await page.WaitForFunctionAsync("previous => {const r=document.querySelector('.flow-node').getBoundingClientRect();return Math.abs(r.x-previous.x)>1||Math.abs(r.y-previous.y)>1}", new { x = before.X, y = before.Y });
        await page.GetByTestId("flow-zoom-in").ClickAsync();
        await page.WaitForFunctionAsync("previous => document.querySelector('.flow-node').getBoundingClientRect().width>previous+1", before.Width);
        var panZoomMilliseconds = clock.Elapsed.TotalMilliseconds;
        var moved = await MeasurementsAsync(page);

        clock.Restart();
        var edge = page.Locator($".flow-connection[data-flow-edge-id='{graph.Edges[0].Id}'] [data-testid=flow-link-hit]");
        await edge.FocusAsync();
        await page.GetByTestId("flow-route-add").ClickAsync();
        var handle = page.GetByTestId("flow-route-handle");
        await Assertions.Expect(handle).ToHaveCountAsync(1);
        var initialX = double.Parse((await handle.GetAttributeAsync("data-world-x"))!, System.Globalization.CultureInfo.InvariantCulture);
        await handle.FocusAsync(); await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(handle).ToHaveAttributeAsync("data-world-x", (initialX + 1).ToString("0.######", System.Globalization.CultureInfo.InvariantCulture));
        await page.Keyboard.PressAsync("Tab"); await Assertions.Expect(handle).Not.ToBeFocusedAsync();
        var routeMilliseconds = clock.Elapsed.TotalMilliseconds;
        var routed = await MeasurementsAsync(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-route.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = graph.SchemaVersion, nodeCount = graph.Nodes.Count, edgeCount = graph.Edges.Count,
            mountMilliseconds, fitMilliseconds, panZoomMilliseconds, routeMilliseconds,
            fitted, moved, routed, assertions = "32 native nodes;61 curves;all cards fit;7 outcome ports fit;actual pan and zoom;route edit and Tab exit",
            noExecutionWrites = fixture.FlowsData.SaveCalls == seedWrites && fixture.FlowsData.PublishCalls == 0 && fixture.FlowsData.DryRunCalls == 0
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(seedWrites, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Graph_History_Retains_Native_Text_Undo_And_Current_Viewport_Without_Dispatch()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await OpenAsync(page, fixture); await page.GetByTestId("flow-edit").First.ClickAsync();
        var name = page.GetByTestId("flow-name");
        await Assertions.Expect(name).ToHaveValueAsync("Incident from alert");
        await name.FillAsync("Synthetic local history");
        await page.GetByTestId("flow-canvas").FocusAsync(); await name.FocusAsync();
        await name.PressAsync("End"); await page.Keyboard.TypeAsync(" edited");
        await name.PressAsync("Control+z");
        await Assertions.Expect(name).ToHaveValueAsync("Synthetic local history");
        await page.GetByTestId("flow-zoom-in").ClickAsync();
        var before = await MeasurementsAsync(page);
        var width = (await page.Locator(".flow-node").First.BoundingBoxAsync())!.Width;
        await page.GetByTestId("flow-undo").ClickAsync();
        await Assertions.Expect(name).ToHaveValueAsync("Incident from alert");
        Assert.InRange((await page.Locator(".flow-node").First.BoundingBoxAsync())!.Width, width - .01f, width + .01f);
        await page.GetByTestId("flow-canvas").FocusAsync();
        await page.Keyboard.PressAsync("Control+Shift+z");
        await Assertions.Expect(name).ToHaveValueAsync("Synthetic local history");
        var after = await MeasurementsAsync(page);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-local-history.json"), JsonSerializer.Serialize(new { before, after }));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-local-history.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private static FlowGraphDto NearLimitGraph()
    {
        var kinds = new[] { FlowNodeKind.ExecutionEvent }.Concat(Enumerable.Repeat(FlowNodeKind.RunLibraryScript, 5))
            .Concat(Enumerable.Repeat(FlowNodeKind.MapIncident, 25)).Append(FlowNodeKind.Finish).ToArray();
        var nodes = kinds.Select((kind, index) => new FlowNodeDto(Guid.NewGuid(), kind, $"Synthetic {kind} {index + 1}", new(30 + index % 4 * 380, 70 + index / 4 * 350))
        {
            ExecutionTrigger = kind == FlowNodeKind.ExecutionEvent ? new([FlowExecutionSourceKind.Script], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) : null,
            Mapping = kind == FlowNodeKind.MapIncident ? new("Synthetic {definitionName}", "Original outcome: {outcome}") : null
        }).ToArray();
        var edges = new List<FlowEdgeDto>();
        for (var index = 0; index < nodes.Length - 1; index++)
            foreach (var port in FlowNodeCatalogue.Find(nodes[index].Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output))
                edges.Add(new(nodes[index].Id, port.Id, nodes[index + 1].Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() });
        return new(FlowGraphV2Limits.SchemaVersion, nodes, edges, new());
    }
    private static Task<JsonElement> MeasurementsAsync(IPage page) => page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("""
        panel => ({
            viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},
            panel:panel.getBoundingClientRect(),
            scroller:(()=>{const e=panel.querySelector('[id^=flow-scroll-]');return e?{x:e.scrollLeft,y:e.scrollTop,width:e.scrollWidth,height:e.scrollHeight}:null})(),
            nodes:[...panel.querySelectorAll('.flow-node')].map(node=>({id:node.dataset.nodeId,kind:node.dataset.nodeKind,rect:node.getBoundingClientRect(),ports:node.querySelectorAll('.flow-port').length})),
            curves:panel.querySelectorAll('[data-testid=flow-link]').length,
            routePoints:[...panel.querySelectorAll('.flow-route-handle')].map(handle=>({x:Number(handle.dataset.worldX),y:Number(handle.dataset.worldY)}))
        })
        """);
    private static async Task OpenAsync(IPage page, ClientsManagementFixtureHost fixture)
    {
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("flow-row").WaitForAsync(new() { Timeout = 90_000 });
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
