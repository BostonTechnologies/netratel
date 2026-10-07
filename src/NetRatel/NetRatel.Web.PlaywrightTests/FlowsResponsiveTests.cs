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
        await page.GetByTestId("flow-overview-toggle").ClickAsync();
        await page.Locator(".veloxdev-wf-minimap").WaitForAsync();
        await page.GetByTestId("flow-fit").ClickAsync();
        await AssertNativeNodesFitAsync(page);
        await AssertNoOverflowAsync(page);
        foreach (var id in new[] { "flow-close", "flow-name", "flow-save", "flow-validate", "flow-dry-run-toggle", "flow-publish", "flow-reload", "flow-properties-toggle", "flow-fit", "flow-overview-toggle" })
            await AssertControlReachableAsync(page.GetByTestId(id), width, height);
        var actionId = await page.Locator("[data-node-kind=CreateIncident]").GetAttributeAsync("data-node-id");
        var canvasEvidence = EvidenceRoot(); Directory.CreateDirectory(canvasEvidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(canvasEvidence, $"flows-{width}x{height}-{theme}-workspace.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
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
    public async Task Actual_Native_Drag_And_Pointer_Connections_Save_Reload_Stable_Properties_And_Viewport()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1600, Height = 1000 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        foreach (var (path, mime) in new[] { ("/_content/VeloxDev.Razor/veloxdev.workflow.css", "text/css"), ("/_content/VeloxDev.Razor/veloxdev.workflow.js", "text/javascript"), ("/js/flows-editor.js", "text/javascript") })
        {
            var asset = await page.APIRequest.GetAsync(fixture.BaseAddress + path);
            Assert.Equal(200, asset.Status); Assert.Contains(mime, asset.Headers["content-type"]);
        }
        await OpenPageAsync(page, fixture);
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
        await page.GetByTestId("flow-row").Filter(new() { HasText = "Native canvas roundtrip" }).GetByTestId("flow-edit").ClickAsync();
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
        // Disabling Save during the request may move browser focus to the document.
        // Escape is an editor keyboard action, so focus a real editor control first.
        await page.GetByTestId("flow-close").FocusAsync();
        await Assertions.Expect(page.GetByTestId("flow-close")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Escape"); await page.GetByTestId("flow-unsaved").WaitForAsync();
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
        await page.GetByTestId("flow-row").WaitForAsync(new() { Timeout = 90_000 });
    }
    [Fact]
    public async Task History_Opens_Immutable_Published_Graph_And_Clone_Creates_Independent_Disabled_Draft()
    {
        var fixture = _fixture!; fixture.FlowsData.SeedPublishedVersion();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1280, Height = 800 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000); await OpenPageAsync(page, fixture);
        await page.GetByTestId("flow-history").ClickAsync(); await page.GetByTestId("flow-history-panel").WaitForAsync();
        var versionOpener = page.GetByTestId("flow-view-version"); await versionOpener.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        Assert.Equal(0, await page.GetByTestId("flow-save").CountAsync());
        Assert.Equal(0, await page.GetByTestId("flow-publish").CountAsync());
        await Assertions.Expect(page.GetByTestId("flow-name")).ToBeDisabledAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-delete").First).ToBeDisabledAsync();
        await page.GetByTestId("flow-close").ClickAsync();
        await Assertions.Expect(versionOpener).ToBeFocusedAsync();
        await page.GetByTestId("flow-clone").ClickAsync(); await page.GetByTestId("flow-clone-name").FillAsync("Independent copy");
        await page.GetByTestId("flow-clone-confirm").ClickAsync(); await page.GetByTestId("flow-name").WaitForAsync();
        await Assertions.Expect(page.GetByTestId("flow-name")).ToHaveValueAsync("Independent copy");
        await CloseEditorAsync(page);
        var copy = page.GetByTestId("flow-row").Filter(new() { HasText = "Independent copy" });
        await Assertions.Expect(copy).ToContainTextAsync("Disabled"); await Assertions.Expect(copy).ToContainTextAsync("Not published");
        await Assertions.Expect(page.GetByTestId("flow-row")).ToHaveCountAsync(2);
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
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
    private static async Task GestureAsync(IPage page, ILocator from, ILocator to)
    {
        var a = (await from.BoundingBoxAsync())!; var b = (await to.BoundingBoxAsync())!;
        await page.Mouse.MoveAsync(a.X + a.Width / 2, a.Y + a.Height / 2); await page.Mouse.DownAsync();
        await page.Mouse.MoveAsync(b.X + b.Width / 2, b.Y + b.Height / 2, new() { Steps = 18 }); await page.Mouse.UpAsync();
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
        Assert.InRange(bounds.X, -1, width); Assert.True(bounds.X + bounds.Width <= width + 1);
        Assert.InRange(bounds.Y, -1, height); Assert.True(bounds.Y + bounds.Height <= height + 1);
        Assert.True(await control.EvaluateAsync<bool>("e => {const r=e.getBoundingClientRect();const hit=document.elementFromPoint(r.x+r.width/2,r.y+r.height/2);return hit===e || e.contains(hit) || (e.matches(':disabled') && hit?.contains(e) && hit.closest('.mud-tooltip-root')===e.closest('.mud-tooltip-root'))}"), $"{await control.GetAttributeAsync("data-testid")} must not be covered by the app bar, drawer, or another panel.");
    }
    private static async Task AssertNoOverflowAsync(IPage page) => Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
    private static string EvidenceRoot() => Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
