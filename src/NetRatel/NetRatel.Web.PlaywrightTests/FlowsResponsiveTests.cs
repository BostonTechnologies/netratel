using System.Text;
using System.Text.Json;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class FlowsResponsiveTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;

    [Theory]
    [InlineData(1366, 768, "light", 1d)]
    [InlineData(1366, 768, "dark", 1d)]
    [InlineData(390, 844, "system", 1d)]
    [InlineData(195, 422, "light", 2d)] // physical390×844 at200% browser zoom equivalent.
    [InlineData(640, 400, "dark", 2d)] // physical1280×800 at200% browser zoom equivalent.
    public async Task Editor_Reflows_With_Production_Drawer_Theme_And_All_Controls_Reachable(int width, int height, string theme, double dpr)
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)dpr, ColorScheme = ColorScheme.Light });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await OpenPageAsync(page, fixture);
        await SetThemeAsync(page, theme);
        var drawer = page.GetByTestId("app-navigation-drawer");
        var closed = await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')");
        if ((width >= 1280 && closed) || (width < 1280 && !closed)) await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width >= 1280) await Assertions.Expect(drawer).ToBeVisibleAsync();
        var opener = page.GetByTestId("flow-edit").First;
        await opener.ClickAsync();
        await page.GetByTestId("flow-editor").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await Assertions.Expect(page.GetByTestId("flow-close")).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByTestId("flow-overview-toggle")).ToHaveAttributeAsync("aria-expanded", "false");
        if (width == 640)
        {
            await page.GetByTestId("flow-close").FocusAsync();
            await Assertions.Expect(page.Locator(".mud-popover-open.mud-tooltip").Filter(new() { HasText = "Close editor" })).ToBeVisibleAsync();
        }
        await page.GetByTestId("flow-overview-toggle").ClickAsync();
        await page.Locator(".veloxdev-wf-minimap").WaitForAsync();
        await page.GetByTestId("flow-fit").ClickAsync();
        await AssertNativeNodesFitAsync(page);
        await AssertNoOverflowAsync(page);
        var canvasEvidence = EvidenceRoot(); Directory.CreateDirectory(canvasEvidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(canvasEvidence, $"flows-{width}x{height}-{theme}-workspace.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        foreach (var id in new[] { "flow-close", "flow-name", "flow-save", "flow-validate", "flow-dry-run-toggle", "flow-publish", "flow-reload", "flow-properties-toggle", "flow-fit", "flow-overview-toggle" })
            await AssertControlReachableAsync(page.GetByTestId(id), width, height);
        var actionId = await page.Locator("[data-node-kind=CreateIncident]").GetAttributeAsync("data-node-id");
        await Assertions.Expect(page.GetByTestId("flow-properties")).ToHaveCountAsync(0);
        await page.Locator($"[data-node-id='{actionId}'] [data-testid=flow-node-settings]").ClickAsync();
        await AssertControlReachableAsync(page.GetByTestId("flow-connector"), width, height);
        if (width > 1100)
        {
            var available = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!.Width;
            var properties = (await page.GetByTestId("flow-properties").BoundingBoxAsync())!;
            Assert.InRange(properties.Width, 360, 440);
            await page.GetByTestId("flow-properties-pin").ClickAsync();
            await Assertions.Expect(page.GetByTestId("flow-properties-pin")).ToHaveAttributeAsync("aria-pressed", "true");
            var docked = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!.Width;
            Assert.True(docked < available - 350);
            await page.GetByTestId("flow-properties-close").ClickAsync();
            await Assertions.Expect(page.GetByTestId("flow-properties")).ToHaveCountAsync(0);
            Assert.InRange((await page.GetByTestId("flow-canvas").BoundingBoxAsync())!.Width, available - 1, available + 1);
            await page.Locator($"[data-node-id='{actionId}'] [data-testid=flow-node-settings]").ClickAsync();
        }
        await Assertions.Expect(page.GetByTestId("flow-connector-status")).ToContainTextAsync("No owned connector");
        Assert.True(await page.EvaluateAsync<bool>("""
            () => {
                const drawer=document.querySelector('[data-testid=flow-properties]'),map=document.querySelector('.veloxdev-wf-minimap');
                if(!drawer||!map)return true;
                const d=drawer.getBoundingClientRect(),m=map.getBoundingClientRect(),x=m.x+m.width/2,y=m.y+m.height/2;
                return x<d.left||x>=d.right||y<d.top||y>=d.bottom||!!document.elementFromPoint(x,y)?.closest('[data-testid=flow-properties]');
            }
            """), "Canvas overview must remain behind overlay properties.");
        await page.ScreenshotAsync(new() { Path = Path.Combine(canvasEvidence, $"flows-{width}x{height}-{theme}-properties.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-validate").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-validation-issues")).ToContainTextAsync("connector reference");
        await page.GetByTestId("flow-dry-run-toggle").ClickAsync();
        await AssertControlReachableAsync(page.GetByTestId("flow-dry-run"), width, height);
        await page.GetByTestId("flow-dry-run").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-preview")).ToContainTextAsync("Preview could not validate");
        Assert.Equal(0, fixture.FlowsData.PublishCalls);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        await AssertNoOverflowAsync(page);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        var name = $"flows-{width}x{height}-{theme}-dpr{dpr}";
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        var geometry = await page.EvaluateAsync<JsonElement>("""
            () => ({ cssWidth:innerWidth,cssHeight:innerHeight,dpr:devicePixelRatio,rootFont:getComputedStyle(document.documentElement).fontSize,
              documentWidth:document.documentElement.scrollWidth,canvas:(()=>{const r=document.querySelector('[data-testid=flow-canvas]').getBoundingClientRect();return{x:r.x,y:r.y,width:r.width,height:r.height}})() })
            """);
        await File.WriteAllTextAsync(Path.Combine(evidence, name + ".json"), geometry.GetRawText());
        Assert.Equal(width, geometry.GetProperty("cssWidth").GetInt32()); Assert.Equal(dpr, geometry.GetProperty("dpr").GetDouble());
        Assert.Equal("16px", geometry.GetProperty("rootFont").GetString());
        await page.GetByTestId("flow-preview-close").ClickAsync();
        await page.GetByTestId("flow-canvas").ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, name + "-canvas.png"), Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-properties-close").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-properties")).ToHaveCountAsync(0);
        await page.GetByTestId("flow-overview-toggle").ClickAsync();
        await Assertions.Expect(page.Locator(".veloxdev-wf-minimap")).ToHaveCountAsync(0);
        if (theme == "system")
        {
            await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Light });
            await page.Locator("html[data-netratel-theme=light]").WaitForAsync();
            await AssertNoOverflowAsync(page);
        }
        await CloseEditorAsync(page);
        await Assertions.Expect(opener).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Fullscreen_Editor_And_Viewer_Keep_Preview_Modal_And_Preserve_Draft_On_Escape()
    {
        var fixture = _fixture!;
        fixture.FlowsData.SeedRun();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await OpenPageAsync(page, fixture);
        await page.GetByTestId("flow-edit").First.ClickAsync();
        await page.GetByTestId("flow-name").FillAsync("Fullscreen retained draft");
        var selected = await page.Locator(".flow-node.selected").GetAttributeAsync("data-node-id");
        await page.GetByTestId("flow-focus").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-focus")).ToHaveAttributeAsync("aria-pressed", "true");
        var shell = (await page.GetByTestId("flow-editor").BoundingBoxAsync())!;
        Assert.InRange(shell.X, -1, 1); Assert.InRange(shell.Y, -1, 1);
        Assert.InRange(shell.Width, 1278, 1282); Assert.InRange(shell.Height, 798, 802);
        var canvas = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await page.GetByTestId("flow-dry-run-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-preview-dialog")).ToBeVisibleAsync();
        Assert.True(await page.GetByTestId("flow-preview-dialog").EvaluateAsync<bool>("e => e.matches(':modal')"));
        var after = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        Assert.InRange(after.Y, canvas.Y - 1, canvas.Y + 1); Assert.InRange(after.Height, canvas.Height - 1, canvas.Height + 1);
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("flow-preview-dialog")).ToHaveCountAsync(0);
        await Assertions.Expect(page.GetByTestId("flow-dry-run-toggle")).ToBeFocusedAsync();
        await Assertions.Expect(page.GetByTestId("flow-name")).ToHaveValueAsync("Fullscreen retained draft");
        Assert.Equal(selected, await page.Locator(".flow-node.selected").GetAttributeAsync("data-node-id"));
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-fullscreen-editor.png"), Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-close").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-unsaved")).ToBeVisibleAsync();
        Assert.True(await page.GetByTestId("flow-unsaved").EvaluateAsync<bool>("e => !document.fullscreenElement || document.fullscreenElement.contains(e)"));
        await page.GetByTestId("flow-discard").ClickAsync();
        await page.GotoAsync($"{fixture.BaseAddress}/flows?tenantId=17&version={fixture.FlowsData.SeededVersionId:D}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        await page.GetByTestId("flow-focus").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-focus")).ToHaveAttributeAsync("aria-pressed", "true");
        var viewer = (await page.GetByTestId("flow-editor").BoundingBoxAsync())!;
        Assert.InRange(viewer.Y, -1, 1); Assert.InRange(viewer.Height, 798, 802);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-fullscreen-viewer.png"), Animations = ScreenshotAnimations.Disabled });
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("flow-editor")).ToHaveCountAsync(1);
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Actual_Native_Drag_And_Pointer_Connections_Save_Reload_Stable_Properties_And_Viewport()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 }, HasTouch = true });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        // Test the cancellable full-window fallback explicitly; native fullscreen Escape is browser-owned.
        await page.AddInitScriptAsync("""
            Object.defineProperty(document, 'fullscreenEnabled', { configurable: true, get: () => false });
            const captures=new Set(),add=window.addEventListener,remove=window.removeEventListener;
            window.addEventListener=function(type,listener,options){
                if(type==='pointermove'&&(options===true||options?.capture))captures.add(listener);
                return add.call(this,type,listener,options);
            };
            window.removeEventListener=function(type,listener,options){
                if(type==='pointermove'&&(options===true||options?.capture))captures.delete(listener);
                return remove.call(this,type,listener,options);
            };
            window.flowMoveCaptureCount=()=>captures.size;
            window.flowTouchPortEvents=[];
            window.addEventListener('pointerdown',event=>{
                const port=event.target.closest?.('.flow-port');
                if(event.pointerType==='touch'&&port)window.flowTouchPortEvents.push({
                    pointerType:event.pointerType,nodeId:port.dataset.portNode,direction:port.dataset.portDirection});
            },true);
            window.flowPortReleases=[];
            window.addEventListener('pointerup',event=>{
                if(event.pointerType!=='mouse')return;
                const hit=document.elementFromPoint(event.clientX,event.clientY),port=hit?.closest('.flow-port');
                window.flowPortReleases.push({trusted:event.isTrusted,x:event.clientX,y:event.clientY,
                    hit:{tag:hit?.tagName,id:hit?.id,classes:hit?.getAttribute('class')},
                    nodeId:port?.dataset.portNode,direction:port?.dataset.portDirection,portId:port?.dataset.portId});
            },true);
            """);
        var frames = new TransientMoveFrames(page);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        foreach (var (path, mime) in new[] { ("/_content/VeloxDev.Razor/veloxdev.workflow.css", "text/css"), ("/_content/VeloxDev.Razor/veloxdev.workflow.js", "text/javascript"), ("/js/flows-editor.js", "text/javascript") })
        {
            var asset = await page.APIRequest.GetAsync(fixture.BaseAddress + path);
            Assert.Equal(200, asset.Status); Assert.Contains(mime, asset.Headers["content-type"]);
        }
        await OpenPageAsync(page, fixture);
        var captureBaseline = await page.EvaluateAsync<int>("() => window.flowMoveCaptureCount()");
        await page.GetByTestId("flows-new").ClickAsync(); await page.GetByTestId("flow-create-name").FillAsync("Native canvas roundtrip");
        await page.GetByTestId("flow-create-blank").ClickAsync();
        await page.GetByTestId("flow-editor").WaitForAsync();
        foreach (var kind in new[] { "AlertRaised", "MapIncident", "CreateIncident" })
        {
            var drop = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
            var index = Array.IndexOf(new[] { "AlertRaised", "MapIncident", "CreateIncident" }, kind);
            await page.GetByTestId("flow-palette-" + kind).ClickAsync();
            await Assertions.Expect(page.Locator("[data-node-kind=" + kind + "]")).ToHaveCountAsync(0);
            await page.GetByTestId("flow-drag-" + kind).DragToAsync(page.GetByTestId("flow-canvas"), new() { TargetPosition = new() { X = 70 + index * 290, Y = 140 } });
            await page.Locator("[data-node-kind=" + kind + "]").WaitForAsync();
            await Assertions.Expect(page.GetByTestId("flow-palette-" + kind)).ToHaveCountAsync(1);
            await Assertions.Expect(page.GetByTestId("flow-add-" + kind)).ToBeDisabledAsync();
        }
        var trigger = await page.Locator("[data-node-kind=AlertRaised]").GetAttributeAsync("data-node-id");
        var mapping = await page.Locator("[data-node-kind=MapIncident]").GetAttributeAsync("data-node-id");
        var action = await page.Locator("[data-node-kind=CreateIncident]").GetAttributeAsync("data-node-id");
        await page.GetByTestId("flow-focus").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-focus")).ToHaveAttributeAsync("aria-pressed", "true");
        await page.GetByTestId("flow-canvas").FocusAsync();
        var blank = await page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("""
            panel=>{const scroller=panel.querySelector('.veloxdev-wf-scroll'),r=scroller.getBoundingClientRect();
                const x=r.right-100,y=r.bottom-100,hit=document.elementFromPoint(x,y);
                return{x,y,blank:!!hit&&scroller.contains(hit)&&!hit.closest('.flow-node,.flow-connection,.flow-canvas-tools,.flow-edge-tools,.veloxdev-wf-minimap')};}
            """);
        Assert.True(blank.GetProperty("blank").GetBoolean());
        var blankX = blank.GetProperty("x").GetSingle(); var blankY = blank.GetProperty("y").GetSingle();
        await frames.PrimeAsync(page, () => page.Mouse.MoveAsync(blankX, blankY));
        var idleMoves = await ObserveTransientMovesAsync(page, frames,
            () => page.Mouse.MoveAsync(blankX - 60, blankY - 40, new() { Steps = 16 }));
        var cancelPort = (await page.Locator($"[data-port-node='{trigger}'][data-port-direction=output] svg").BoundingBoxAsync())!;
        await frames.PrimeAsync(page, () => page.Mouse.MoveAsync(cancelPort.X + cancelPort.Width / 2, cancelPort.Y + cancelPort.Height / 2));
        await page.Mouse.DownAsync();
        var ghostMoves = await ObserveTransientMovesAsync(page, frames,
            () => page.Mouse.MoveAsync(cancelPort.X + 100, cancelPort.Y + 40, new() { Steps = 16 }));
        await page.WaitForFunctionAsync("() => !document.querySelector('[data-testid=flow-virtual-link]').hasAttribute('hidden')");
        var transientEvidence = EvidenceRoot(); Directory.CreateDirectory(transientEvidence);
        await File.WriteAllTextAsync(Path.Combine(transientEvidence, "flows-native-transient-moves.json"),
            JsonSerializer.Serialize(new { idleMoves, ghostMoves, frames.ObservedSockets, frames.ObservedRenderBatches }));
        foreach (var observation in new[] { idleMoves, ghostMoves })
        {
            Assert.True(observation.Dom.GetProperty("physical").GetInt32() > 1, "The probe must execute multiple actual pointer moves.");
            Assert.Equal(0, observation.Dom.GetProperty("delegated").GetInt32());
            Assert.Equal(0, observation.PointerMoveEvents); Assert.Equal(0, observation.RenderBatches);
        }
        Assert.True(frames.ObservedSockets > 0); Assert.True(frames.ObservedRenderBatches > 0);
        Assert.True(await page.EvaluateAsync<int>("() => window.flowMoveCaptureCount()") > captureBaseline);
        await page.EvaluateAsync("document.dispatchEvent(new PointerEvent('pointerup', { pointerId: 999999, pointerType: 'touch', bubbles: true }))");
        Assert.False(await page.GetByTestId("flow-virtual-link").EvaluateAsync<bool>("path => path.hasAttribute('hidden')"));
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("flow-virtual-link")).ToHaveAttributeAsync("hidden", "hidden");
        await Assertions.Expect(page.GetByTestId("flow-focus")).ToHaveAttributeAsync("aria-pressed", "true");
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(0);
        Assert.Equal(0, await page.Locator(".flow-port.compatible,.flow-port.incompatible,.flow-port.pending").CountAsync());
        await page.Mouse.UpAsync();

        await page.EvaluateAsync("window.flowPortReleases=[]");
        await GestureAsync(page, page.Locator($"[data-port-node='{trigger}'][data-port-direction=output] svg"),
            page.Locator($"[data-port-node='{mapping}'][data-port-direction=output] svg"), "flows-invalid-output-drop.png");
        var invalidReleases = await page.EvaluateAsync<JsonElement>("() => window.flowPortReleases");
        await File.WriteAllTextAsync(Path.Combine(transientEvidence, "flows-native-invalid-release.json"), invalidReleases.GetRawText());
        var invalidRelease = Assert.Single(invalidReleases.EnumerateArray());
        Assert.True(invalidRelease.GetProperty("trusted").GetBoolean());
        Assert.Equal(mapping, invalidRelease.GetProperty("nodeId").GetString());
        Assert.Equal("output", invalidRelease.GetProperty("direction").GetString());
        Assert.Equal("out", invalidRelease.GetProperty("portId").GetString());
        await Assertions.Expect(page.GetByTestId("flow-connection-message")).ToContainTextAsync("Choose an output and an input port.");
        await Assertions.Expect(page.GetByTestId("flow-connection-message")).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(0);
        Assert.Equal(0, await page.Locator(".flow-port.compatible,.flow-port.incompatible,.flow-port.pending").CountAsync());
        var invalidDrop = new { invalidRelease, message = await page.GetByTestId("flow-connection-message").InnerTextAsync(),
            edges = await page.GetByTestId("flow-link").CountAsync(), ghostHidden = await page.GetByTestId("flow-virtual-link").EvaluateAsync<bool>("path => path.hasAttribute('hidden')") };
        Assert.True(invalidDrop.ghostHidden);

        var touchSource = page.Locator($"[data-port-node='{trigger}'][data-port-direction=output] svg");
        var touchTarget = page.Locator($"[data-port-node='{mapping}'][data-port-direction=input] svg");
        var touchSourceBounds = (await touchSource.BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(touchSourceBounds.X + touchSourceBounds.Width / 2, touchSourceBounds.Y + touchSourceBounds.Height / 2);
        await Assertions.Expect(page.GetByTestId("flow-canvas")).ToHaveAttributeAsync("data-connection-start", trigger!);
        var touchTargetBounds = (await touchTarget.BoundingBoxAsync())!;
        await page.Touchscreen.TapAsync(touchTargetBounds.X + touchTargetBounds.Width / 2, touchTargetBounds.Y + touchTargetBounds.Height / 2);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("flow-virtual-link")).ToHaveAttributeAsync("hidden", "hidden");
        Assert.Equal(0, await page.Locator(".flow-port.compatible,.flow-port.incompatible,.flow-port.pending").CountAsync());
        var touchEvidence = await page.EvaluateAsync<JsonElement>("() => ({maxTouchPoints:navigator.maxTouchPoints,events:window.flowTouchPortEvents})");
        Assert.True(touchEvidence.GetProperty("maxTouchPoints").GetInt32() > 0);
        var touchEvents = touchEvidence.GetProperty("events").EnumerateArray().ToArray();
        Assert.Equal(2, touchEvents.Length);
        Assert.All(touchEvents, value => Assert.Equal("touch", value.GetProperty("pointerType").GetString()));
        Assert.Equal(trigger, touchEvents[0].GetProperty("nodeId").GetString());
        Assert.Equal("output", touchEvents[0].GetProperty("direction").GetString());
        Assert.Equal(mapping, touchEvents[1].GetProperty("nodeId").GetString());
        Assert.Equal("input", touchEvents[1].GetProperty("direction").GetString());
        await File.WriteAllTextAsync(Path.Combine(transientEvidence, "flows-native-invalid-and-touch.json"), JsonSerializer.Serialize(new { invalidDrop, touchEvidence }));
        await page.ScreenshotAsync(new() { Path = Path.Combine(transientEvidence, "flows-native-touch-connected.png"), Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-link-hit").FocusAsync();
        await page.GetByTestId("flow-edge-remove").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(0);

        await GestureAsync(page, page.Locator($"[data-port-node='{trigger}'][data-port-direction=output] svg"), page.Locator($"[data-port-node='{mapping}'][data-port-direction=input] svg"));
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(1);
        await GestureAsync(page, page.Locator($"[data-port-node='{mapping}'][data-port-direction=output] svg"), page.Locator($"[data-port-node='{action}'][data-port-direction=input] svg"));
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(2);
        var title = page.Locator("[data-node-kind=MapIncident] [data-testid=flow-node-title]");
        var box = (await title.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(box.X + 70, box.Y + 15); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(box.X + 115, box.Y + 110, new() { Steps = 15 }); await page.Mouse.UpAsync();
        await page.Locator($"[data-node-id='{mapping}'] [data-testid=flow-node-settings]").ClickAsync();
        var nodeBeforeSettings = (await page.Locator("[data-node-kind=MapIncident]").BoundingBoxAsync())!;
        await page.GetByTestId("flow-mapping-title").FillAsync("Incident: {ruleName} / {resource}");
        await page.GetByTestId("flow-mapping-title").PressAsync("End"); await page.GetByTestId("flow-mapping-title").PressAsync("Delete");
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await page.GetByTestId("flow-mapping-title").PressAsync("Tab");
        var nodeAfterSettings = (await page.Locator("[data-node-kind=MapIncident]").BoundingBoxAsync())!;
        Assert.InRange(nodeAfterSettings.X, nodeBeforeSettings.X - 1, nodeBeforeSettings.X + 1);
        Assert.InRange(nodeAfterSettings.Y, nodeBeforeSettings.Y - 1, nodeBeforeSettings.Y + 1);
        await page.GetByTestId("flow-properties-close").ClickAsync();
        var canvas = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(canvas.X + 200, canvas.Y + 420); await page.Mouse.DownAsync(new() { Button = MouseButton.Middle });
        await page.Mouse.MoveAsync(canvas.X + 130, canvas.Y + 360, new() { Steps = 10 }); await page.Mouse.UpAsync(new() { Button = MouseButton.Middle });
        await page.GetByTestId("flow-drag-Condition").DragToAsync(page.GetByTestId("flow-name"));
        await Assertions.Expect(page.Locator("[data-node-kind=Condition]")).ToHaveCountAsync(0);
        await page.GetByTestId("flow-zoom-in").ClickAsync();
        await page.WaitForFunctionAsync("() => !document.querySelector('.flow-canvas-tools').innerText.includes('100%')");
        canvas = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        var dropX = canvas.Width * .4f; var dropY = canvas.Height * .6f;
        await page.GetByTestId("flow-drag-Condition").DragToAsync(page.GetByTestId("flow-canvas"), new() { TargetPosition = new() { X = dropX, Y = dropY } });
        await Assertions.Expect(page.Locator("[data-node-kind=Condition]")).ToHaveCountAsync(1);
        var inserted = (await page.Locator("[data-node-kind=Condition]").BoundingBoxAsync())!;
        Assert.InRange(inserted.X, canvas.X + dropX - 2, canvas.X + dropX + 2);
        Assert.InRange(inserted.Y, canvas.Y + dropY - 2, canvas.Y + dropY + 2);
        await Assertions.Expect(page.GetByTestId("flow-save")).ToBeEnabledAsync();
        await page.GetByTestId("flow-save").ClickAsync(); await Assertions.Expect(page.GetByTestId("flow-result")).ToContainTextAsync("Draft saved");
        var saved = fixture.FlowsData.SavedGraph!;
        Assert.Equal(4, saved.Nodes.Count); Assert.Equal(2, saved.Edges.Count);
        var mapped = saved.Nodes.Single(n => n.Kind == FlowNodeKind.MapIncident);
        var earlyEvidence = EvidenceRoot(); Directory.CreateDirectory(earlyEvidence);
        await File.WriteAllTextAsync(Path.Combine(earlyEvidence, "flows-native-saved.json"), JsonSerializer.Serialize(saved));
        await page.ScreenshotAsync(new() { Path = Path.Combine(earlyEvidence, "flows-native-saved.png"), FullPage = true });
        Assert.True(mapped.Position.X > 330, $"Native drag X was {mapped.Position.X}"); Assert.True(mapped.Position.Y > 140, $"Native drag Y was {mapped.Position.Y}");
        Assert.Equal("Incident: {ruleName} / {resource}", mapped.Mapping!.TitleTemplate);
        Assert.NotEqual(1, saved.Viewport.Scale); Assert.True(saved.Viewport.ScrollX > 0 || saved.Viewport.ScrollY > 0);
        var canonical = JsonSerializer.Serialize(saved);
        await page.ReloadAsync(new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(".flows-table .flow-row").Filter(new() { HasText = "Native canvas roundtrip" }).GetByTestId("flow-edit").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(4); await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(2);
        await page.GetByTestId("flow-name").FillAsync("Native canvas roundtrip restored");
        await page.GetByTestId("flow-save").ClickAsync(); await Assertions.Expect(page.GetByTestId("flow-result")).ToContainTextAsync("Draft saved");
        Assert.Equal(canonical, JsonSerializer.Serialize(fixture.FlowsData.SavedGraph));
        Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Empty(errors);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-native-roundtrip.json"), canonical);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-native-roundtrip.png"), FullPage = true });
        var actionBefore = (await page.Locator("[data-node-kind=CreateIncident]").BoundingBoxAsync())!;
        await page.Locator("[data-node-kind=CreateIncident] [data-testid=flow-node-settings]").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-connector")).ToBeVisibleAsync();
        await page.GetByTestId("flow-properties-close").ClickAsync();
        var actionAfter = (await page.Locator("[data-node-kind=CreateIncident]").BoundingBoxAsync())!;
        Assert.InRange(actionAfter.X, actionBefore.X - 1, actionBefore.X + 1); Assert.InRange(actionAfter.Y, actionBefore.Y - 1, actionBefore.Y + 1);
        await page.Locator("[data-node-kind=MapIncident] [data-testid=flow-node-delete]").ClickAsync();
        await page.GetByTestId("flow-delete-confirm").ClickAsync();
        try { await Assertions.Expect(page.Locator("[data-node-kind=MapIncident]")).ToHaveCountAsync(0); }
        catch
        {
            await File.WriteAllTextAsync(Path.Combine(evidence, "flows-delete-failure.txt"), await page.GetByTestId("flow-editor").InnerTextAsync());
            await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-delete-failure.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
            throw;
        }
        await Assertions.Expect(page.Locator("[data-node-kind=CreateIncident].selected")).ToHaveCountAsync(1);
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(0);
        Assert.Empty(errors);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        await CloseEditorAsync(page);
        await page.WaitForFunctionAsync("expected => window.flowMoveCaptureCount()===expected", captureBaseline);
    }

    [Fact]
    public async Task Dirty_Conflict_And_Tenant_Change_Are_Explicit_And_Return_Focus()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000); await OpenPageAsync(page, fixture);
        var opener = page.GetByTestId("flow-edit").First; await opener.ClickAsync(); await page.GetByTestId("flow-name").WaitForAsync();
        await page.GetByTestId("flow-name").FillAsync("My preserved changes"); fixture.FlowsData.ConflictNextSave = true;
        await page.GetByTestId("flow-save").ClickAsync(); await Assertions.Expect(page.GetByTestId("flow-error")).ToContainTextAsync("changed in another session");
        await Assertions.Expect(page.GetByTestId("flow-name")).ToHaveValueAsync("My preserved changes");
        // Leaving is explicit; Escape only dismisses the inner overlay or fullscreen.
        await page.GetByTestId("flow-close").ClickAsync(); await page.GetByTestId("flow-unsaved").WaitForAsync();
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-dirty-dialog.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.GetByRole(AriaRole.Button, new() { Name = "Keep editing", Exact = true }).ClickAsync();
        await page.GetByTestId("flow-tenant").SelectOptionAsync("23"); await page.GetByTestId("flow-unsaved").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("flow-tenant")).ToHaveValueAsync("17");
        await Assertions.Expect(page.GetByTestId("flow-name")).ToHaveValueAsync("My preserved changes");
        await page.GetByTestId("flow-discard").ClickAsync(); await Assertions.Expect(page.GetByTestId("flows-empty")).ToContainTextAsync("No flows");
        await page.GetByTestId("flow-tenant").SelectOptionAsync("17"); await page.GetByTestId("flow-edit").First.ClickAsync();
        await CloseEditorAsync(page);
        await Assertions.Expect(opener).ToBeFocusedAsync();
    }

    [Fact]
    public async Task Schema_Two_Route_Keyboard_Arrows_Edit_Coordinates_And_Tab_Leaves_The_Handle()
    {
        var fixture = _fixture!;
        var flow = Assert.Single(await fixture.FlowsData.ListAsync(17));
        var graph = FlowDraftMigration.UpgradeToSchemaTwo(flow.Draft);
        graph = graph with { Edges = graph.Edges.Select((edge, index) => index == 0
            ? edge with { Route = new([new(300, 200)]) } : edge).ToArray() };
        await fixture.FlowsData.SaveAsync(17, flow.Id, new(flow.Revision, flow.Name, graph));
        var setupWrites = fixture.FlowsData.SaveCalls;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await OpenPageAsync(page, fixture);
        await page.GetByTestId("flow-edit").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await page.GetByTestId("flow-fit").ClickAsync();
        await page.GetByTestId("flow-link-hit").First.FocusAsync();
        var handle = page.GetByTestId("flow-route-handle");
        await Assertions.Expect(handle).ToHaveCountAsync(1);
        await handle.FocusAsync();
        await page.Keyboard.PressAsync("ArrowRight");
        await Assertions.Expect(handle).ToHaveAttributeAsync("data-world-x", "301");
        await page.Keyboard.PressAsync("Shift+ArrowDown");
        await Assertions.Expect(handle).ToHaveAttributeAsync("data-world-y", "210");
        await page.Keyboard.PressAsync("Tab");
        await Assertions.Expect(handle).Not.ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Shift+Tab");
        await Assertions.Expect(handle).ToBeFocusedAsync();
        var coordinate = page.GetByRole(AriaRole.Spinbutton, new() { Name = "Point 1 X", Exact = true });
        await coordinate.FillAsync("410"); await coordinate.PressAsync("Tab");
        await Assertions.Expect(handle).ToHaveAttributeAsync("data-world-x", "410");
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-v2-route-keyboard.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await page.GetByTestId("flow-route-reset").ClickAsync();
        await Assertions.Expect(handle).ToHaveCountAsync(0);
        await Assertions.Expect(page.Locator(".flow-connection[data-flow-route]")).ToHaveCountAsync(0);
        Assert.Equal(setupWrites, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
        Assert.Equal(0, fixture.FlowsData.DryRunCalls); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    [Fact]
    public async Task Native_Spaces_Text_Undo_And_Port_Activation_Preserve_Graph_History_And_Viewport()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await OpenPageAsync(page, fixture); await page.GetByTestId("flow-edit").First.ClickAsync();
        var name = page.GetByTestId("flow-name");
        await Assertions.Expect(name).ToHaveValueAsync("Incident from alert");
        await name.FillAsync("Synthetic local history");
        await page.GetByTestId("flow-canvas").FocusAsync(); await name.FocusAsync();
        await name.PressAsync("End"); await page.Keyboard.TypeAsync(" edited");
        await Assertions.Expect(name).ToHaveValueAsync("Synthetic local history edited");
        await name.PressAsync("Control+z");
        await Assertions.Expect(name).ToHaveValueAsync("Synthetic local history");
        await page.GetByTestId("flow-zoom-in").ClickAsync();
        var before = await page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("e => e.getBoundingClientRect()");
        var width = (await page.Locator(".flow-node").First.BoundingBoxAsync())!.Width;
        await page.GetByTestId("flow-undo").ClickAsync();
        await Assertions.Expect(name).ToHaveValueAsync("Incident from alert");
        Assert.InRange((await page.Locator(".flow-node").First.BoundingBoxAsync())!.Width, width - .01f, width + .01f);
        await page.GetByTestId("flow-canvas").FocusAsync();
        await page.Keyboard.PressAsync("Control+Shift+z");
        await Assertions.Expect(name).ToHaveValueAsync("Synthetic local history");
        var after = await page.GetByTestId("flow-canvas").EvaluateAsync<JsonElement>("e => e.getBoundingClientRect()");
        var firstLink = page.GetByTestId("flow-link-hit").First;
        await firstLink.FocusAsync(); await page.GetByTestId("flow-edge-remove").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(1);
        var source = page.Locator("[data-node-kind=AlertRaised] .flow-port.output button");
        var target = page.Locator("[data-node-kind=MapIncident] .flow-port.input button");
        await source.FocusAsync(); await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(source).ToHaveAttributeAsync("aria-pressed", "true");
        await target.FocusAsync(); await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(page.GetByTestId("flow-link")).ToHaveCountAsync(2);
        await page.Locator("[data-node-kind=MapIncident] [data-testid=flow-node-title]").FocusAsync();
        await page.Keyboard.PressAsync("Space");
        await Assertions.Expect(page.Locator("[data-node-kind=MapIncident].selected")).ToHaveCountAsync(1);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-local-history.json"), JsonSerializer.Serialize(new { before, after }));
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-local-history.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls); Assert.Equal(0, fixture.FlowsData.DryRunCalls);
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private static async Task CloseEditorAsync(IPage page)
    {
        await page.GetByTestId("flow-close").ClickAsync();
        await page.WaitForFunctionAsync("""
            () => {
                if(!document.querySelector('[data-testid=flow-editor]'))return true;
                const dialog=document.querySelector('[data-testid=flow-unsaved]');
                return dialog && dialog.getBoundingClientRect().height>0 && getComputedStyle(dialog).visibility!=='hidden';
            }
            """);
        if (await page.GetByTestId("flow-unsaved").IsVisibleAsync()) await page.GetByTestId("flow-discard").ClickAsync();
        await page.GetByTestId("flow-editor").WaitForAsync(new() { State = WaitForSelectorState.Hidden });
    }

    private static async Task OpenPageAsync(IPage page, ClientsManagementFixtureHost fixture)
    {
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.Locator(".flows-table .flow-row").WaitForAsync(new() { Timeout = 90_000 });
    }
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(390, 844)]
    public async Task History_Opens_Immutable_Published_Graph_And_Clone_Creates_Independent_Disabled_Draft(int width, int height)
    {
        var fixture = _fixture!; fixture.FlowsData.SeedPublishedVersion();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await OpenPageAsync(page, fixture);
        var historyOpener = page.GetByTestId("flow-history");
        await historyOpener.ClickAsync(); await page.GetByTestId("flow-history-panel").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("flow-history-dismiss")).ToBeFocusedAsync();
        var bounds = (await page.GetByRole(AriaRole.Dialog).BoundingBoxAsync())!;
        Assert.InRange(bounds.X, 0, width); Assert.True(bounds.X + bounds.Width <= width);
        Assert.InRange(bounds.Y, 0, height); Assert.True(bounds.Y + bounds.Height <= height);
        await AssertNoOverflowAsync(page);
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"flow-history-{width}x{height}.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        var versionOpener = page.GetByTestId("flow-view-version"); await versionOpener.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        Assert.Equal(0, await page.GetByTestId("flow-save").CountAsync());
        Assert.Equal(0, await page.GetByTestId("flow-publish").CountAsync());
        await Assertions.Expect(page.GetByTestId("flow-name")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-delete").First).ToBeDisabledAsync();
        await page.GetByTestId("flow-close").ClickAsync();
        await Assertions.Expect(versionOpener).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(page.GetByTestId("flow-history-panel")).ToHaveCountAsync(0);
        await Assertions.Expect(historyOpener).ToBeFocusedAsync();
        await page.GetByTestId("flow-clone").ClickAsync(); await page.GetByTestId("flow-clone-name").FillAsync("Independent copy");
        await page.GetByTestId("flow-clone-confirm").ClickAsync(); await page.GetByTestId("flow-name").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("flow-name")).ToHaveValueAsync("Independent copy");
        await CloseEditorAsync(page);
        var copy = page.Locator(".flows-table .flow-row").Filter(new() { HasText = "Independent copy" });
        await Assertions.Expect(copy).ToContainTextAsync("Disabled"); await Assertions.Expect(copy).ToContainTextAsync("Not published");
        await Assertions.Expect(page.Locator(".flows-table .flow-row")).ToHaveCountAsync(2);
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }
    private static async Task SetThemeAsync(IPage page, string theme)
    {
        var mobile = await page.GetByTestId("mobile-overflow").IsVisibleAsync();
        await page.GetByTestId(mobile ? "mobile-overflow" : "theme-preference-menu").ClickAsync();
        await page.GetByTestId($"{(mobile ? "mobile-theme-option" : "theme-option")}-{theme}").ClickAsync();
        if (theme == "system") await page.EmulateMediaAsync(new() { ColorScheme = ColorScheme.Dark });
        await page.Locator($"html[data-netratel-theme='{(theme == "system" ? "dark" : theme)}']").WaitForAsync();
    }
    [Fact]
    public async Task Monitoring_Deep_Links_Open_The_Paired_Run_Receipt_Or_Immutable_Version_Without_History_Fanout()
    {
        var fixture = _fixture!; fixture.FlowsData.SeedRun();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        await page.GotoAsync($"{fixture.BaseAddress}/flows?tenantId=17&run={fixture.FlowsData.SeededRunId:D}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync("Succeeded", new() { Timeout = 90_000 });
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync("Fixture client");
        await Assertions.Expect(page.GetByTestId("flow-run-result").GetByRole(AriaRole.Link, new() { Name = "Open incident" })).ToHaveAttributeAsync("href", "https://fixture.invalid/incidents/123");
        Assert.Equal(1, fixture.FlowsData.RunLookups); Assert.Equal(1, fixture.FlowsData.VersionLookups); Assert.Equal(0, fixture.FlowsData.HistoryReads);
        await page.GotoAsync($"{fixture.BaseAddress}/flows?tenantId=23&version={fixture.FlowsData.SeededVersionId:D}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("flows-error")).ToContainTextAsync("unavailable in the selected tenant");
        Assert.Equal(0, await page.GetByTestId("flow-editor").CountAsync()); Assert.Equal(0, await page.GetByTestId("flow-run-result").CountAsync());
        await page.GotoAsync($"{fixture.BaseAddress}/flows?tenantId=17&version={fixture.FlowsData.SeededVersionId:D}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        Assert.Equal(0, await page.GetByTestId("flow-save").CountAsync()); Assert.Equal(0, fixture.FlowsData.HistoryReads);
    }
    private sealed record TransientMoveObservation(JsonElement Dom, int PointerMoveEvents, int RenderBatches);
    private static async Task<TransientMoveObservation> ObserveTransientMovesAsync(IPage page, TransientMoveFrames frames, Func<Task> move)
    {
        await using var observer = await page.EvaluateHandleAsync("""
            () => {
                const panel=document.querySelector('[data-testid=flow-canvas]');let physical=0,delegated=0;
                const countPhysical=e=>{if(panel.contains(e.target))physical++},countDelegated=e=>{if(panel.contains(e.target))delegated++};
                window.addEventListener('pointermove',countPhysical,true);document.addEventListener('pointermove',countDelegated);
                return{snapshot:()=>({physical,delegated}),dispose:()=>{
                    window.removeEventListener('pointermove',countPhysical,true);document.removeEventListener('pointermove',countDelegated);
                }};
            }
            """);
        frames.Start();
        try
        {
            await move();
            // The ghost's scheduled SVG draw must execute before taking the browser-side observation.
            await page.EvaluateAsync("() => new Promise(resolve=>requestAnimationFrame(()=>requestAnimationFrame(resolve)))");
            var dom = await observer.EvaluateAsync<JsonElement>("observer => observer.snapshot()");
            var counts = frames.Stop();
            return new(dom, counts.Moves, counts.Renders);
        }
        finally { frames.Stop(); await observer.EvaluateAsync("observer => observer.dispose()"); }
    }
    private sealed class TransientMoveFrames
    {
        private int _pending, _sawMove, _recording, _moves, _renders, _sockets, _allRenders;
        private TaskCompletionSource _settled = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ObservedSockets => Volatile.Read(ref _sockets);
        public int ObservedRenderBatches => Volatile.Read(ref _allRenders);
        public TransientMoveFrames(IPage page) => page.WebSocket += (_, socket) =>
        {
            if (!new Uri(socket.Url).AbsolutePath.EndsWith("/_blazor", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref _sockets);
            socket.FrameSent += (_, frame) =>
            {
                var payload = Payload(frame);
                Interlocked.Add(ref _pending, Occurrences(payload, "BeginInvokeDotNetFromJS"));
                if (payload.Contains("DispatchEventAsync", StringComparison.Ordinal) && payload.Contains("pointermove", StringComparison.Ordinal))
                {
                    Volatile.Write(ref _sawMove, 1);
                    if (Volatile.Read(ref _recording) != 0) Interlocked.Increment(ref _moves);
                }
            };
            socket.FrameReceived += (_, frame) =>
            {
                var payload = Payload(frame); var renders = Occurrences(payload, "JS.RenderBatch");
                Interlocked.Add(ref _allRenders, renders);
                if (Volatile.Read(ref _recording) != 0) Interlocked.Add(ref _renders, renders);
                var ended = Occurrences(payload, "JS.EndInvokeDotNet");
                if (ended > 0 && Interlocked.Add(ref _pending, -ended) == 0 && Volatile.Read(ref _sawMove) != 0) _settled.TrySetResult();
            };
        };
        public async Task PrimeAsync(IPage page, Func<Task> move)
        {
            // Leaving the canvas clears its idle target; re-entry must forward a real native transition.
            await page.Mouse.MoveAsync(1, 1);
            _settled = new(TaskCreationOptions.RunContinuationsAsynchronously); Volatile.Write(ref _sawMove, 0);
            await move();
            // The actual native dispatch and all preceding circuit calls must complete before measurement.
            try { await _settled.Task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException)
            {
                Console.WriteLine($"Native pointer fence: sockets={ObservedSockets}, renders={ObservedRenderBatches}, pending={Volatile.Read(ref _pending)}, sawMove={Volatile.Read(ref _sawMove)}");
                throw;
            }
        }
        public void Start() { Interlocked.Exchange(ref _moves, 0); Interlocked.Exchange(ref _renders, 0); Volatile.Write(ref _recording, 1); }
        public (int Moves, int Renders) Stop() { Volatile.Write(ref _recording, 0); return (Volatile.Read(ref _moves), Volatile.Read(ref _renders)); }
        private static string Payload(IWebSocketFrame frame) => frame.Binary is { } bytes ? Encoding.UTF8.GetString(bytes) : frame.Text ?? "";
        private static int Occurrences(string value, string marker)
        {
            var count = 0;
            for (var position = 0; (position = value.IndexOf(marker, position, StringComparison.Ordinal)) >= 0; position += marker.Length) count++;
            return count;
        }
    }
    private static async Task GestureAsync(IPage page, ILocator from, ILocator to, string screenshotName = "flows-connection-before-drop.png")
    {
        var a = (await from.BoundingBoxAsync())!; var b = (await to.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(a.X + a.Width / 2, a.Y + a.Height / 2); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(b.X + b.Width / 2, b.Y + b.Height / 2, new() { Steps = 18 });
        await page.WaitForFunctionAsync("() => {const p=document.querySelector('[data-testid=flow-virtual-link]');return p&&!p.hasAttribute('hidden')&&p.getAttribute('d')?.includes(' C ')}");
        await Assertions.Expect(page.GetByTestId("flow-virtual-link")).ToHaveAttributeAsync("stroke-dasharray", "7 5");
        var evidence = EvidenceRoot(); Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, screenshotName), Animations = ScreenshotAnimations.Disabled });
        await page.Mouse.UpAsync();
        await Assertions.Expect(page.GetByTestId("flow-virtual-link")).ToHaveAttributeAsync("hidden", "hidden");
    }
    private static async Task AssertNativeNodesFitAsync(IPage page)
    {
        try
        {
        await page.GetByTestId("flow-canvas").EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})");
        await page.WaitForFunctionAsync("""
            () => {
                const panel=document.querySelector('[data-testid=flow-canvas]'); if(!panel)return false;
                const p=panel.getBoundingClientRect(); const nodes=[...panel.querySelectorAll('.flow-node')];
                const reachable=e=>{const r=e.getBoundingClientRect();const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return hit===e||e.contains(hit)};
                return nodes.length===3 && nodes.every(n=>{const r=n.getBoundingClientRect();return r.left>=p.left-1&&r.right<=p.right+1&&r.top>=p.top-1&&r.bottom<=p.bottom+1&&[...n.querySelectorAll('.flow-node-title,.flow-port svg')].every(reachable)});
            }
            """, null, new() { Timeout = 5_000 });
        }
        catch
        {
            var path = EvidenceRoot(); Directory.CreateDirectory(path);
            var state = await page.EvaluateAsync<JsonElement>("""
                () => ({viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},panel:document.querySelector('[data-testid=flow-canvas]').getBoundingClientRect(),
                  nodes:[...document.querySelectorAll('.flow-node')].map(n=>({name:n.innerText,rect:n.getBoundingClientRect(),style:n.getAttribute('style'),parentStyle:n.parentElement.getAttribute('style')})),
                  scroller:(()=>{const e=document.querySelector('[id^=flow-scroll-]');return{rect:e.getBoundingClientRect(),scrollX:e.scrollLeft,scrollY:e.scrollTop,scrollWidth:e.scrollWidth,scrollHeight:e.scrollHeight,style:e.getAttribute('style')}})(),
                  tools:document.querySelector('.flow-canvas-tools').innerText})
                """);
            await File.WriteAllTextAsync(Path.Combine(path, "flows-fit-failure.json"), state.GetRawText());
            await page.ScreenshotAsync(new() { Path = Path.Combine(path, "flows-fit-failure.png"), FullPage = true });
            throw;
        }
    }
    private static async Task AssertControlReachableAsync(ILocator control, int width, int height)
    {
        await control.EvaluateAsync("e => e.scrollIntoView({block:'center',inline:'nearest'})"); var bounds = (await control.BoundingBoxAsync())!;
        var id = await control.GetAttributeAsync("data-testid") ?? await control.GetAttributeAsync("aria-label") ?? "control";
        Assert.InRange(bounds.X, -1, width);
        Assert.True(bounds.X + bounds.Width <= width + 1, $"{id} horizontal bounds X={bounds.X}, width={bounds.Width}, viewport={width}.");
        Assert.InRange(bounds.Y, -1, height);
        Assert.True(bounds.Y + bounds.Height <= height + 1, $"{id} vertical bounds Y={bounds.Y}, height={bounds.Height}, viewport={height}.");
        Assert.True(await control.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return hit===e || e.contains(hit) || (e.matches(':disabled') && hit?.contains(e) && hit.closest('.mud-tooltip-root')===e.closest('.mud-tooltip-root'))}"), $"{await control.GetAttributeAsync("data-testid")} must not be covered by the app bar, drawer, or another panel.");
    }
    private static async Task AssertNoOverflowAsync(IPage page) => Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
