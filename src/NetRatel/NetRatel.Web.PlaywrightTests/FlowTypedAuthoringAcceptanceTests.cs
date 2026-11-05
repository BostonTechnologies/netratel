using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class FlowTypedAuthoringAcceptanceTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;
    private readonly ConcurrentQueue<string> _outgoingWrites = new();

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

        var panTarget = await page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("""
            panel => {
                const scroller=panel.querySelector('[id^=flow-scroll-]'),r=scroller.getBoundingClientRect();
                const x=r.left+scroller.clientWidth*.8,y=r.top+scroller.clientHeight*.5;
                const hit=document.elementFromPoint(x,y);
                return {id:scroller.id,x,y,bounds:r,clientWidth:scroller.clientWidth,clientHeight:scroller.clientHeight,
                    scrollLeft:scroller.scrollLeft,scrollTop:scroller.scrollTop,
                    maxScrollLeft:scroller.scrollWidth-scroller.clientWidth,maxScrollTop:scroller.scrollHeight-scroller.clientHeight,
                    hit:{tag:hit?.tagName,id:hit?.id,classes:hit?.getAttribute('class')},
                    blank:!!hit&&scroller.contains(hit)&&!hit.closest('.flow-node,.veloxdev-wf-node-drag,.veloxdev-wf-slot,.flow-canvas-tools,.flow-edge-tools,.veloxdev-wf-minimap,.flow-route-handle,input,select,button,[data-testid=flow-link-hit]')};
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-pan-start.json"),
            JsonSerializer.Serialize(new { fitted, panTarget }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(panTarget.GetProperty("blank").GetBoolean(), "The native pan must start on an ordinary blank scroller surface.");
        var panX = panTarget.GetProperty("x").GetSingle(); var panY = panTarget.GetProperty("y").GetSingle();
        var before = (await page.Locator(".flow-node").First.BoundingBoxAsync())!;
        clock.Restart();
        await page.Mouse.MoveAsync(panX, panY);
        await page.Mouse.DownAsync(new() { Button = MouseButton.Middle });
        var panPhase = "native acceptance";
        try
        {
            try
            {
                // Velox 10.0.1 waits for its .NET press verdict; an early mouseup cancels the pending gesture.
                await page.WaitForFunctionAsync("id => document.getElementById(id)?.style.cursor==='grabbing'", panTarget.GetProperty("id").GetString());
                await page.Mouse.MoveAsync(panX - 60, panY - 40, new() { Steps = 12 });
                var held = await MeasurementsAsync(page);
                await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-pan-held.json"), held.GetRawText());
                panPhase = "displacement while held";
                await page.WaitForFunctionAsync("previous => {const r=document.querySelector('.flow-node').getBoundingClientRect();return Math.abs(r.x-previous.x)>1||Math.abs(r.y-previous.y)>1}", new { x = (double)before.X, y = (double)before.Y });
            }
            finally
            {
                await page.Mouse.UpAsync(new() { Button = MouseButton.Middle });
                var released = await MeasurementsAsync(page);
                await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-pan-released.json"), released.GetRawText());
            }
            panPhase = "displacement after release";
            await page.WaitForFunctionAsync("previous => {const r=document.querySelector('.flow-node').getBoundingClientRect();return Math.abs(r.x-previous.x)>1||Math.abs(r.y-previous.y)>1}", new { x = (double)before.X, y = (double)before.Y });
        }
        catch (Exception error)
        {
            try
            {
                var failed = await MeasurementsAsync(page);
                await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-pan-failure.json"),
                    JsonSerializer.Serialize(new { panPhase, error = error.GetType().Name, failed }, new JsonSerializerOptions { WriteIndented = true }));
                await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-pan-failure.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
            }
            catch (Exception diagnosticError) when (diagnosticError is PlaywrightException or IOException or UnauthorizedAccessException)
            { Console.WriteLine($"Pan failure evidence could not be retained: {diagnosticError.GetType().Name}"); }
            throw;
        }
        var panned = await MeasurementsAsync(page);
        await page.GetByTestId("flow-zoom-in").ClickAsync();
        await page.WaitForFunctionAsync("previous => document.querySelector('.flow-node').getBoundingClientRect().width>previous+1", (double)before.Width);
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

        var edgeId = graph.Edges[0].Id!.Value;
        var routedGroup = page.Locator($".flow-connection[data-flow-edge-id='{edgeId}']");
        var routedCurve = routedGroup.Locator("[data-testid=flow-link]");
        var nodeIds = await page.Locator(".flow-node[data-node-id]").EvaluateAllAsync<string[]>("nodes => nodes.map(node=>node.dataset.nodeId).sort()");
        var edgeIds = await page.Locator(".flow-connection[data-flow-edge-id]").EvaluateAllAsync<string[]>("edges => edges.map(edge=>edge.dataset.flowEdgeId).sort()");
        var beforeSaveGeometry = await RouteGeometryAsync(page, edgeId);
        await page.GetByTestId("flow-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-result")).ToContainTextAsync("Draft saved");
        var savedGraph = fixture.FlowsData.SavedGraph!;
        Assert.Equal(32, savedGraph.Nodes.Count); Assert.Equal(61, savedGraph.Edges.Count);
        Assert.Equal(graph.Nodes.Select(node => node.Id).Order(), savedGraph.Nodes.Select(node => node.Id).Order());
        Assert.Equal(graph.Edges.Select(value => value.Id).Order(), savedGraph.Edges.Select(value => value.Id).Order());
        var savedRoute = Assert.Single(savedGraph.Edges, value => value.Id == edgeId).Route!;
        Assert.Equal(6, savedRoute.Points.Count);
        var savedRouteJson = (await routedGroup.GetAttributeAsync("data-flow-route"))!;
        var savedPath = (await routedCurve.GetAttributeAsync("d"))!;
        Assert.Contains(" C ", savedPath);
        var beforeReload = await MeasurementsAsync(page);
        var beforeReloadGeometry = await RouteGeometryAsync(page, edgeId);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-route-saved.json"), JsonSerializer.Serialize(savedGraph));
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-route-before-reload.json"),
            JsonSerializer.Serialize(new { beforeSaveGeometry, beforeReloadGeometry, savedPath, savedRoute.Points, savedGraph.Viewport }, new JsonSerializerOptions { WriteIndented = true }));

        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("flow-row").Filter(new() { HasText = "Synthetic near-limit graph" }).GetByTestId("flow-edit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(32);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(61);
        Assert.Equal(nodeIds, await page.Locator(".flow-node[data-node-id]").EvaluateAllAsync<string[]>("nodes => nodes.map(node=>node.dataset.nodeId).sort()"));
        Assert.Equal(edgeIds, await page.Locator(".flow-connection[data-flow-edge-id]").EvaluateAllAsync<string[]>("edges => edges.map(edge=>edge.dataset.flowEdgeId).sort()"));
        await Assertions.Expect(routedGroup).ToHaveAttributeAsync("data-flow-route", savedRouteJson);
        await Assertions.Expect(routedGroup).ToHaveAttributeAsync("data-flow-source-node", graph.Edges[0].SourceNodeId.ToString());
        await Assertions.Expect(routedGroup).ToHaveAttributeAsync("data-flow-target-node", graph.Edges[0].TargetNodeId.ToString());
        var afterReloadGeometry = await RouteGeometryAsync(page, edgeId);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-route-after-reload.json"), afterReloadGeometry.GetRawText());
        try { await Assertions.Expect(routedCurve).ToHaveAttributeAsync("d", savedPath); }
        catch (Exception error)
        {
            try
            {
                var failedGeometry = await RouteGeometryAsync(page, edgeId);
                await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-route-reload-failure.json"),
                    JsonSerializer.Serialize(new { error = error.GetType().Name, beforeSaveGeometry, beforeReloadGeometry, afterReloadGeometry, failedGeometry, savedPath }, new JsonSerializerOptions { WriteIndented = true }));
                await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-route-reload-failure.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
            }
            catch (Exception diagnosticError) when (diagnosticError is PlaywrightException or IOException or UnauthorizedAccessException)
            { Console.WriteLine($"Route reload evidence could not be retained: {diagnosticError.GetType().Name}"); }
            throw;
        }
        await routedGroup.GetByTestId("flow-link-hit").FocusAsync();
        await Assertions.Expect(handle).ToHaveCountAsync(6);
        var reloadedRoute = await MeasurementsAsync(page);
        var displayedPointsBeforeReload = narrowRoute.GetProperty("routePoints").Deserialize<FlowPositionDto[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var restoredPoints = reloadedRoute.GetProperty("routePoints").Deserialize<FlowPositionDto[]>(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit-route-roundtrip.json"), JsonSerializer.Serialize(new
        {
            fullPrecisionPoints = savedRoute.Points, savedRouteJson,
            restoredRouteJson = await routedGroup.GetAttributeAsync("data-flow-route"), savedPath,
            restoredPath = await routedCurve.GetAttributeAsync("d"), displayedPointsBeforeReload, restoredPoints
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(displayedPointsBeforeReload!, restoredPoints!);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-near-limit-route-reloaded.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-route-reset").ClickAsync();
        await Assertions.Expect(handle).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".flow-connection[data-flow-route]")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(61);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-near-limit.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = graph.SchemaVersion, nodeCount = graph.Nodes.Count, edgeCount = graph.Edges.Count,
            mountMilliseconds, fitMilliseconds, panZoomMilliseconds, routeMilliseconds,
            fitted, panTarget, panned, moved, routed, narrowRoute, beforeReload, reloadedRoute, savedRouteJson, savedPath,
            draftSaves = fixture.FlowsData.SaveCalls - seedWrites,
            assertions = "32 native nodes;61 curves;all cards fit;7 outcome ports fit;actual pan and zoom;route edit and Tab exit;narrow bounded inspector with pointer and keyboard reachability;save/reload preserves IDs, six route points and exact curve geometry;reset retains all edges",
            noExecutionWrites = fixture.FlowsData.PublishCalls == 0 && fixture.FlowsData.DryRunCalls == 0 && _outgoingWrites.IsEmpty
        }, new JsonSerializerOptions { WriteIndented = true }));
        Assert.Equal(seedWrites + 1, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.Empty(_outgoingWrites);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Explicit_Upgrade_Named_Definitions_Bound_Job_Target_And_Typed_Mapping_Save_And_Reload_Without_Dispatch()
    {
        var fixture = _fixture!;
        var metadata = fixture.FlowsData.SeedExecutionAuthoringCatalogue();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 1050 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await OpenAsync(page, fixture);
        await page.GetByTestId("flows-new").ClickAsync();
        await page.GetByTestId("flow-create-name").FillAsync("Synthetic typed authoring");
        await page.GetByTestId("flow-create-blank").ClickAsync();
        await page.GetByTestId("flow-upgrade-draft").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-palette-RerunOriginalExecution")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-palette-InvestigateWithAi")).ToContainTextAsync("Investigate failure");
        var palette = await page.Locator(".flow-palette-label").EvaluateAllAsync<string[]>("items => items.map(item => item.getAttribute('data-testid'))");
        Assert.Equal(FlowNodeCatalogue.Nodes.Select(node => $"flow-palette-{node.Kind}").ToArray(), palette);
        await page.GetByTestId("flow-focus").ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('[data-testid=flow-editor]')?.classList.contains('focused')");

        await page.GetByTestId("flow-add-ExecutionEvent").ClickAsync();
        await PositionSelectedAsync(page, FlowNodeKind.ExecutionEvent, 40, 90);
        await page.GetByTestId("flow-trigger-preset").SelectOptionAsync(nameof(FlowExecutionSourceKind.Job));
        await page.GetByTestId("flow-trigger-scope").SelectOptionAsync("2");
        await ChooseDefinitionAsync(page, "source", metadata.SourceJob.Definition.Id, metadata.SourceJob.Definition.Name!);
        await Assertions.Expect(page.GetByTestId("flow-trigger-definition")).ToContainTextAsync("Synthetic source job");

        await page.GetByTestId("flow-add-RunJob").ClickAsync();
        await PositionSelectedAsync(page, FlowNodeKind.RunJob, 410, 90);
        await ChooseDefinitionAsync(page, "maintenance", metadata.Job.Definition.Id, metadata.Job.Definition.Name!);
        await Assertions.Expect(page.GetByTestId("flow-job-bound-target")).ToContainTextAsync("Bound synthetic repair endpoint");
        await Assertions.Expect(page.GetByTestId("flow-script-target-mode")).ToHaveCountAsync(0);
        await MapEventInputAsync(page, "event.definition.name");
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-job-bound-target.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });

        await page.GetByTestId("flow-add-RunLibraryScript").ClickAsync();
        await PositionSelectedAsync(page, FlowNodeKind.RunLibraryScript, 780, 90);
        await ChooseDefinitionAsync(page, "diagnostic", metadata.Script.Definition.Id, metadata.Script.Definition.Name!);
        await Assertions.Expect(page.GetByTestId("flow-script-target-mode")).ToHaveValueAsync("event");
        await MapEventInputAsync(page, "event.reason");

        await page.GetByTestId("flow-add-MapIncident").ClickAsync();
        await PositionSelectedAsync(page, FlowNodeKind.MapIncident, 410, 500);
        await page.GetByTestId("flow-mapping-alias").FillAsync("failureReason");
        await page.GetByTestId("flow-mapping-alias").PressAsync("Tab");
        await page.GetByTestId("flow-mapping-add-binding").ClickAsync();
        await MapEventInputAsync(page, "event.reason");
        await page.GetByTestId("flow-mapping-title").FillAsync("Synthetic {input.failureReason}");
        await page.GetByTestId("flow-mapping-title").PressAsync("Tab");
        await page.GetByTestId("flow-mapping-description").FillAsync("Original definition: {definitionName}");
        await page.GetByTestId("flow-mapping-description").PressAsync("Tab");

        await page.GetByTestId("flow-add-RerunOriginalExecution").ClickAsync();
        await PositionSelectedAsync(page, FlowNodeKind.RerunOriginalExecution, 780, 500);
        await Assertions.Expect(page.GetByTestId("flow-rerun-budget")).ToHaveValueAsync("1");
        await page.GetByTestId("flow-properties-close").ClickAsync();
        await FitAsync(page);
        await ConnectAsync(page, FlowNodeKind.ExecutionEvent, "event", FlowNodeKind.RunJob, 1);
        await ConnectAsync(page, FlowNodeKind.RunJob, "succeeded", FlowNodeKind.RunLibraryScript, 2);
        await ConnectAsync(page, FlowNodeKind.RunLibraryScript, "succeeded", FlowNodeKind.MapIncident, 3);
        await ConnectAsync(page, FlowNodeKind.MapIncident, "result", FlowNodeKind.RerunOriginalExecution, 4);
        var eventCategory = FlowNodeCatalogue.Find(FlowNodeKind.ExecutionEvent)!.Category;
        var actionCategory = FlowNodeCatalogue.Find(FlowNodeKind.RunJob)!.Category;
        Assert.NotEqual(eventCategory, actionCategory);
        await Assertions.Expect(page.Locator("[data-node-kind=ExecutionEvent] .flow-node-header-kind")).ToContainTextAsync(eventCategory);
        await Assertions.Expect(page.Locator("[data-node-kind=RunJob] .flow-node-header-kind")).ToContainTextAsync(actionCategory);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-typed-authoring.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-save").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Saved draft");

        var saved = Assert.Single((await fixture.FlowsData.ListAsync(17)).Where(flow => flow.Name == "Synthetic typed authoring"));
        Assert.False(saved.Enabled); Assert.Null(saved.PublishedVersionId); Assert.Equal(0, saved.PublishedVersionNumber);
        Assert.Equal(2, saved.Draft.SchemaVersion); Assert.Equal(5, saved.Draft.Nodes.Count); Assert.Equal(4, saved.Draft.Edges.Count);
        Assert.True(FlowGraphV2Validator.ValidateDraft(saved.Draft).Valid);
        Assert.Equal(new FlowPositionDto(40, 90), saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.ExecutionEvent).Position);
        Assert.Equal(new FlowPositionDto(410, 500), saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Position);
        var trigger = saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.ExecutionEvent);
        var job = saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.RunJob);
        var script = saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.RunLibraryScript);
        var mapping = saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident);
        Assert.Equal("42", Assert.Single(trigger.ExecutionTrigger!.DefinitionReferences!).Id);
        Assert.Equal("9007199254740993", job.ExecutionAction!.Definition.Id);
        Assert.Equal(metadata.Job.Target, job.ExecutionAction.Target);
        Assert.NotEqual(metadata.SourceJob.Target!.AgentId, job.ExecutionAction.Target!.AgentId);
        Assert.Null(job.ExecutionAction.PinnedSnapshotReference);
        Assert.Equal("event.definition.name", job.Inputs!["message"].Path);
        Assert.Equal("9007199254740995", script.ExecutionAction!.Definition.Id); Assert.Null(script.ExecutionAction.Target);
        Assert.Equal("event.reason", mapping.Inputs!["failureReason"].Path);
        Assert.Equal("Synthetic {input.failureReason}", mapping.Mapping!.TitleTemplate);
        Assert.Equal(1, saved.Draft.Nodes.Single(node => node.Kind == FlowNodeKind.RerunOriginalExecution).Rerun!.MaximumAttempts);

        await page.GetByTestId("flow-close").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-editor")).ToHaveCountAsync(0);
        var row = page.GetByTestId("flow-row").Filter(new() { HasText = "Synthetic typed authoring" });
        await Assertions.Expect(row).ToContainTextAsync("Disabled"); await Assertions.Expect(row).ToContainTextAsync("Not published");
        await row.GetByTestId("flow-edit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(5);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(4);
        await page.GetByTestId("flow-select-node").SelectOptionAsync(job.Id.ToString());
        await page.GetByTestId("flow-properties-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-job-bound-target")).ToContainTextAsync("Bound synthetic repair endpoint");
        await Assertions.Expect(page.GetByTestId("flow-binding-event")).ToHaveValueAsync("event.definition.name");
        await page.GetByTestId("flow-select-node").SelectOptionAsync(mapping.Id.ToString());
        await Assertions.Expect(page.GetByTestId("flow-mapping-title")).ToHaveValueAsync("Synthetic {input.failureReason}");
        await Assertions.Expect(page.GetByTestId("flow-binding-event")).ToHaveValueAsync("event.reason");
        Assert.True(fixture.FlowsData.CapabilityReads >= 2);
        Assert.Contains(fixture.FlowsData.ExecutionSearches, request => request.Source == FlowExecutionSourceKind.Job && request.Search == "maintenance");
        Assert.Contains(fixture.FlowsData.ExecutionSearches, request => request.Source == FlowExecutionSourceKind.Script && request.Search == "diagnostic");
        Assert.Equal(2, fixture.FlowsData.ExecutionDefinitionReads);
        Assert.Equal(1, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.Empty(await fixture.FlowsData.GetRunsAsync(17, saved.Id)); Assert.Empty(_outgoingWrites);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-v2-typed-authoring.json"), JsonSerializer.Serialize(new
        {
            scenario = "inert authoring through actual UI controls; no execution evidence is implied",
            saved.Id, saved.Revision, saved.Enabled, saved.PublishedVersionNumber,
            saved.Draft.SchemaVersion, nodeCount = saved.Draft.Nodes.Count, edgeCount = saved.Draft.Edges.Count,
            sourceJobId = metadata.SourceJob.Definition.Id, repairJobId = job.ExecutionAction.Definition.Id,
            retainedJobTarget = job.ExecutionAction.Target.DisplayName, scriptId = script.ExecutionAction.Definition.Id,
            searches = fixture.FlowsData.ExecutionSearches.Select(request => new { source = request.Source.ToString(), request.Search }),
            fixture.FlowsData.ExecutionDefinitionReads, fixture.FlowsData.CapabilityReads,
            fixture.FlowsData.SaveCalls, fixture.FlowsData.PublishCalls, fixture.FlowsData.DryRunCalls,
            outgoingWriteAttempts = _outgoingWrites.ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
    private static async Task ChooseDefinitionAsync(IPage page, string search, string id, string name)
    {
        var input = page.GetByTestId("flow-definition-search");
        await input.FillAsync(search); await input.PressAsync("Tab");
        var choice = page.Locator($"[data-testid=flow-definition-choice][data-definition-id='{id}']");
        await Assertions.Expect(choice).ToContainTextAsync(name);
        await choice.ClickAsync();
    }
    private static async Task PositionSelectedAsync(IPage page, FlowNodeKind kind, int x, int y)
    {
        await Assertions.Expect(page.Locator($".flow-node.selected[data-node-kind='{kind}']")).ToHaveCountAsync(1);
        await page.GetByTestId("flow-node-x").FillAsync(x.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByTestId("flow-node-x").PressAsync("Tab");
        await Assertions.Expect(page.Locator(".flow-node.selected")).ToHaveAttributeAsync("data-world-x", x.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByTestId("flow-node-y").FillAsync(y.ToString(System.Globalization.CultureInfo.InvariantCulture));
        await page.GetByTestId("flow-node-y").PressAsync("Tab");
        await Assertions.Expect(page.Locator(".flow-node.selected")).ToHaveAttributeAsync("data-world-y", y.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }
    private static async Task MapEventInputAsync(IPage page, string path)
    {
        await page.GetByTestId("flow-binding-source").SelectOptionAsync(nameof(FlowInputSource.Event));
        await page.GetByTestId("flow-binding-event").SelectOptionAsync(path);
    }
    private static async Task ConnectAsync(IPage page, FlowNodeKind source, string outcome, FlowNodeKind target, int expectedEdges)
    {
        await page.Locator($"[data-node-kind='{source}'] .flow-port.output[data-port-id='{outcome}'] button").ClickAsync();
        await page.Locator($"[data-node-kind='{target}'] .flow-port.input button").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(expectedEdges);
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
            document:{x:scrollX,y:scrollY,active:document.activeElement?.getAttribute('class')},
            panel:panel.getBoundingClientRect(),fitState:panel.dataset.fitState,fitRevision:Number(panel.dataset.fitRevision),
            scroller:(()=>{const e=panel.querySelector('[id^=flow-scroll-]'),content=e?.querySelector('.veloxdev-wf-canvas-content');
                return e?{id:e.id,connected:e.isConnected,cursor:e.style.cursor,x:e.scrollLeft,y:e.scrollTop,width:e.scrollWidth,height:e.scrollHeight,
                    clientWidth:e.clientWidth,clientHeight:e.clientHeight,rect:e.getBoundingClientRect(),
                    canvas:e.querySelector('.veloxdev-wf-canvas')?.getBoundingClientRect(),content:content?.getBoundingClientRect(),contentTransform:content?.style.transform}:null})(),
            nodes:[...panel.querySelectorAll('.flow-node')].map(node=>({id:node.dataset.nodeId,kind:node.dataset.nodeKind,rect:node.getBoundingClientRect(),ports:node.querySelectorAll('.flow-port').length})),
            curves:panel.querySelectorAll('[data-testid=flow-link]').length,
            routePoints:[...panel.querySelectorAll('.flow-route-handle')].map(handle=>({x:Number(handle.dataset.worldX),y:Number(handle.dataset.worldY)}))
        })
        """);
    private static Task<JsonElement> RouteGeometryAsync(IPage page, Guid edgeId) => page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("""
        (panel,edgeId) => {
            const group=panel.querySelector('.flow-connection[data-flow-edge-id="'+edgeId+'"]');
            const scroller=panel.querySelector('.veloxdev-wf-scroll'),canvas=scroller?.querySelector('.veloxdev-wf-canvas');
            const content=canvas?.querySelector('.veloxdev-wf-canvas-content');
            const box=element=>{
                if(!element)return null;
                const style=getComputedStyle(element);
                return{rect:element.getBoundingClientRect(),style:element.getAttribute('style'),computed:{
                    left:style.left,top:style.top,width:style.width,height:style.height,transform:style.transform,
                    transformOrigin:style.transformOrigin,paddingLeft:style.paddingLeft,paddingTop:style.paddingTop,
                    borderLeft:style.borderLeftWidth,borderTop:style.borderTopWidth}};
            };
            const matrix=element=>{const m=element?.getScreenCTM?.();return m?{a:m.a,b:m.b,c:m.c,d:m.d,e:m.e,f:m.f}:null};
            const endpoint=attribute=>{
                const id=group?.getAttribute(attribute),slot=id&&panel.querySelector('[data-veloxdev-slot-id="'+id+'"]');
                const glyph=slot?.querySelector('.veloxdev-wf-slot-svg')??slot,node=slot?.closest('.flow-node');
                const native=node?.closest('[data-veloxdev-node-id]');
                const rect=glyph?.getBoundingClientRect();
                return{slotId:id,slot:box(slot),glyph:box(glyph),glyphCentre:rect?{x:rect.left+rect.width/2,y:rect.top+rect.height/2}:null,node:box(node),native:box(native),
                    worldX:node?.dataset.worldX,worldY:node?.dataset.worldY,designWidth:node?.dataset.designWidth};
            };
            return{edgeId,viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio,scrollX,scrollY},panel:box(panel),
                fullscreen:{nativeRoot:document.fullscreenElement?.id,editorClasses:panel.closest('[data-testid=flow-editor]')?.className},
                inverseScale:panel.dataset.inverseScale,routeScale:group?.dataset.routeScale,route:group?.dataset.flowRoute,
                path:group?.querySelector('[data-testid=flow-link]')?.getAttribute('d'),
                hitPath:group?.querySelector('[data-testid=flow-link-hit]')?.getAttribute('d'),
                svg:{...box(group?.closest('svg')),viewBox:group?.closest('svg')?.getAttribute('viewBox'),matrix:matrix(group?.closest('svg'))},groupMatrix:matrix(group),
                scroller:{...box(scroller),x:scroller?.scrollLeft,y:scroller?.scrollTop,clientWidth:scroller?.clientWidth,clientHeight:scroller?.clientHeight,scrollWidth:scroller?.scrollWidth,scrollHeight:scroller?.scrollHeight},canvas:box(canvas),
                content:{...box(content),offsetLeft:content?.offsetLeft,offsetTop:content?.offsetTop,computedLeft:content&&getComputedStyle(content).left,computedTransform:content&&getComputedStyle(content).transform},
                source:endpoint('data-veloxdev-sender-slot'),target:endpoint('data-veloxdev-receiver-slot')};
        }
        """, edgeId.ToString());
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
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync(services =>
        services.AddSingleton<IHttpClientFactory>(provider => new ReadOnlyFixtureHttpClientFactory(
            provider.GetRequiredService<ClientDirectoryFixtureData>(), _outgoingWrites)));
    private sealed class ReadOnlyFixtureHttpClientFactory(ClientDirectoryFixtureData directory, ConcurrentQueue<string> writes) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(directory, writes)) { BaseAddress = new("https://netratel.test") };
        private sealed class Handler(ClientDirectoryFixtureData directory, ConcurrentQueue<string> writes) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                if (request.Method != HttpMethod.Get && request.Method != HttpMethod.Head && request.Method != HttpMethod.Options)
                {
                    writes.Enqueue($"{request.Method} {request.RequestUri?.AbsolutePath}");
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                }
                return Task.FromResult(directory.Respond(request));
            }
        }
    }
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
