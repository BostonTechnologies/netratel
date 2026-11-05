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
        clock.Restart(); await FitAsync(page);
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
        await page.SetViewportSizeAsync(195, 422);
        await FitAsync(page);
        for (var point = 1; point < FlowGraphV2Limits.MaximumRoutePoints; point++)
            await page.GetByTestId("flow-route-add").ClickAsync();
        await Assertions.Expect(handle).ToHaveCountAsync(FlowGraphV2Limits.MaximumRoutePoints);
        var lastRemove = page.GetByRole(AriaRole.Button, new() { Name = "Remove route point 8", Exact = true });
        await lastRemove.ScrollIntoViewIfNeededAsync();
        Assert.True(await lastRemove.EvaluateAsync<bool>("""
            button => {
                const tools=button.closest('.flow-edge-tools'),panel=tools.closest('.flow-canvas-panel');
                const t=tools.getBoundingClientRect(),p=panel.getBoundingClientRect(),r=button.getBoundingClientRect();
                const x=r.left+r.width/2,y=r.top+r.height/2,hit=document.elementFromPoint(x,y);
                return t.height>0&&t.top>=p.top&&t.bottom<=p.bottom+1&&
                    x>=p.left&&x<=p.right&&y>=p.top&&y<=p.bottom&&(hit===button||button.contains(hit));
            }
            """), "The bounded inspector and its last route control must remain inside the narrow canvas and accept an ordinary pointer.");
        await lastRemove.ClickAsync();
        await Assertions.Expect(handle).ToHaveCountAsync(7);
        var lastY = page.GetByRole(AriaRole.Spinbutton, new() { Name = "Point 7 Y", Exact = true });
        await lastY.FocusAsync(); await page.Keyboard.PressAsync("Tab");
        var keyboardRemove = page.GetByRole(AriaRole.Button, new() { Name = "Remove route point 7", Exact = true });
        await Assertions.Expect(keyboardRemove).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Enter"); await Assertions.Expect(handle).ToHaveCountAsync(6);
        var narrowRoute = await MeasurementsAsync(page);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-route-narrow.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = graph.SchemaVersion, nodeCount = graph.Nodes.Count, edgeCount = graph.Edges.Count,
            mountMilliseconds, fitMilliseconds, panZoomMilliseconds, routeMilliseconds,
            fitted, moved, routed, narrowRoute, assertions = "32 native nodes;61 curves;all cards fit;7 outcome ports fit;actual pan and zoom;route edit and Tab exit;narrow bounded inspector with pointer and keyboard reachability",
            noExecutionWrites = fixture.FlowsData.SaveCalls == seedWrites && fixture.FlowsData.PublishCalls == 0 && fixture.FlowsData.DryRunCalls == 0
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(seedWrites, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
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
            panel:panel.getBoundingClientRect(),fitState:panel.dataset.fitState,fitRevision:Number(panel.dataset.fitRevision),
            scroller:(()=>{const e=panel.querySelector('[id^=flow-scroll-]');return e?{x:e.scrollLeft,y:e.scrollTop,width:e.scrollWidth,height:e.scrollHeight}:null})(),
            nodes:[...panel.querySelectorAll('.flow-node')].map(node=>({id:node.dataset.nodeId,kind:node.dataset.nodeKind,rect:node.getBoundingClientRect(),ports:node.querySelectorAll('.flow-port').length})),
            curves:panel.querySelectorAll('[data-testid=flow-link]').length,
            routePoints:[...panel.querySelectorAll('.flow-route-handle')].map(handle=>({x:Number(handle.dataset.worldX),y:Number(handle.dataset.worldY)}))
        })
        """);
    private static async Task FitAsync(IPage page)
    {
        var canvas = page.GetByTestId("flow-canvas");
        var previous = long.Parse((await canvas.GetAttributeAsync("data-fit-revision"))!, System.Globalization.CultureInfo.InvariantCulture);
        await page.GetByTestId("flow-fit").ClickAsync();
        await page.WaitForFunctionAsync("""
            previous => {
                const panel=document.querySelector('[data-testid=flow-canvas]');
                return panel?.dataset.fitState==='complete'&&Number(panel.dataset.fitRevision)>previous;
            }
            """, previous);
    }
    private static async Task OpenAsync(IPage page, ClientsManagementFixtureHost fixture)
    {
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("flow-row").WaitForAsync(new() { Timeout = 90_000 });
    }
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
