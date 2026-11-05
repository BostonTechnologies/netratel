using System.Text.Json;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class FlowPreviewPlaybackTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>, IAsyncLifetime
{
    private ClientsManagementFixtureHost? _fixture;

    [Fact]
    public async Task Pure_preview_dialog_keeps_canvas_geometry_and_client_playback_inspects_values_without_writes()
    {
        var fixture = _fixture!; fixture.FlowsData.SeedPreviewGraph();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("flow-row").WaitForAsync(new() { Timeout = 90_000 });
        await page.GetByTestId("flow-edit").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await page.GetByTestId("flow-fit").ClickAsync();
        var before = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await page.GetByTestId("flow-dry-run-toggle").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-preview-dialog")).ToBeVisibleAsync();
        var dialog = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        Assert.InRange(dialog.Y, before.Y - 1, before.Y + 1); Assert.InRange(dialog.Height, before.Height - 1, before.Height + 1);
        await page.GetByTestId("flow-dry-run").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-preview-dialog")).ToHaveCountAsync(0);
        var player = page.GetByTestId("flow-trace-player");
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player).ToContainTextAsync("Simulated playback");
        await Assertions.Expect(player.Locator("[data-trace-position]")).ToHaveTextAsync("Step 1 of 3");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("Sample client");
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("mapped");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("No external action was executed");
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("Data unavailable");
        await player.Locator("[data-trace-action=previous]").ClickAsync();
        await player.Locator("[data-trace-action=restart]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-position]")).ToHaveTextAsync("Step 1 of 3");
        await player.Locator("[data-trace-speed]").SelectOptionAsync("4");
        await player.Locator("[data-trace-action=play]").ClickAsync();
        await page.WaitForFunctionAsync("() => document.querySelector('[data-trace-position]')?.textContent !== 'Step 1 of 3'");
        if (await player.Locator("[data-trace-action=play]").GetAttributeAsync("aria-pressed") == "true")
            await player.Locator("[data-trace-action=play]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-action=play]")).ToHaveAttributeAsync("aria-pressed", "false");
        await page.EmulateMediaAsync(new() { ReducedMotion = ReducedMotion.Reduce });
        Assert.True(await page.EvaluateAsync<bool>("() => matchMedia('(prefers-reduced-motion: reduce)').matches"));
        Assert.Equal(1, fixture.FlowsData.DryRunCalls); Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
        var after = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        Assert.InRange(after.Y, before.Y - 1, before.Y + 1); Assert.InRange(after.Height, before.Height - 1, before.Height + 1);
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, "flows-preview-playback.png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
        await File.WriteAllTextAsync(Path.Combine(evidence, "flows-preview-playback.json"), JsonSerializer.Serialize(new
        { before, dialog, after, previewCalls = fixture.FlowsData.DryRunCalls, saveCalls = fixture.FlowsData.SaveCalls, publishCalls = fixture.FlowsData.PublishCalls }));
        await page.GetByTestId("flow-name").FillAsync("Changed synthetic draft");
        await Assertions.Expect(page.GetByTestId("flow-trace-player")).ToHaveCountAsync(0);
        Assert.Empty(errors); Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }


    [Theory]
    [InlineData("root")]
    [InlineData("scope")]
    [InlineData("context")]
    public async Task Detached_playback_disposes_client_resources_without_a_server_callback(string removal)
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var result = await page.EvaluateAsync<JsonElement>("""
            async removal => {
                const { attach } = await import("/js/flows-playback.js");
                const scope = document.createElement("section");
                scope.id = "playback-cleanup-scope-" + removal;
                const node = document.createElement("article");
                node.className = "flow-node"; node.dataset.nodeId = "synthetic-source";
                const root = document.createElement("section");
                root.id = "playback-cleanup-player-" + removal;
                root.dataset.scopeId = scope.id; root.dataset.context = "synthetic-context";
                root.innerHTML = '<button data-trace-action="play">Play</button><button data-trace-action="previous">Previous</button><button data-trace-action="next">Next</button><output data-trace-position></output><article data-trace-panel="0" data-node-id="synthetic-source"></article><article data-trace-panel="1" data-node-id="synthetic-target"></article>';
                scope.append(node, root); document.body.append(scope);
                const visibility = new Set(), motion = new Set();
                const originalAdd = document.addEventListener, originalRemove = document.removeEventListener;
                const originalMatch = window.matchMedia;
                const media = originalMatch.call(window, "(prefers-reduced-motion: reduce)");
                let handle;
                try {
                    document.addEventListener = function(type, callback, options) {
                        if (type === "visibilitychange") visibility.add(callback);
                        return originalAdd.call(this, type, callback, options);
                    };
                    document.removeEventListener = function(type, callback, options) {
                        if (type === "visibilitychange") visibility.delete(callback);
                        return originalRemove.call(this, type, callback, options);
                    };
                    window.matchMedia = query => query === "(prefers-reduced-motion: reduce)" ? {
                        get matches() { return media.matches; },
                        addEventListener(type, callback, options) { motion.add(callback); media.addEventListener(type, callback, options); },
                        removeEventListener(type, callback, options) { motion.delete(callback); media.removeEventListener(type, callback, options); }
                    } : originalMatch.call(window, query);
                    handle = attach(root.id, "synthetic-context");
                    document.addEventListener = originalAdd; window.matchMedia = originalMatch;
                    root.querySelector("[data-trace-action=play]").click();
                    const styles = () => document.head.querySelectorAll('style[data-flow-playback-owner="' + root.id + '"]').length;
                    const initial = { style: styles(), marked: node.hasAttribute("data-flow-playback-node"), visibility: visibility.size, motion: motion.size };
                    if (removal === "root") root.remove();
                    else if (removal === "scope") scope.remove();
                    else root.dataset.context = "replaced-context";
                    // Await native mutation-observer delivery; no server disposal or timer delay is used.
                    await Promise.resolve(); await Promise.resolve();
                    const after = { style: styles(), marked: node.hasAttribute("data-flow-playback-node"), visibility: visibility.size, motion: motion.size };
                    handle.dispose(); handle.dispose();
                    return { initial, after };
                } finally {
                    handle?.dispose(); scope.remove();
                    document.addEventListener = originalAdd; document.removeEventListener = originalRemove;
                    window.matchMedia = originalMatch;
                }
            }
            """, removal);
        var initial = result.GetProperty("initial");
        Assert.Equal(1, initial.GetProperty("style").GetInt32()); Assert.True(initial.GetProperty("marked").GetBoolean());
        Assert.Equal(1, initial.GetProperty("visibility").GetInt32()); Assert.Equal(1, initial.GetProperty("motion").GetInt32());
        var after = result.GetProperty("after");
        Assert.Equal(0, after.GetProperty("style").GetInt32()); Assert.False(after.GetProperty("marked").GetBoolean());
        Assert.Equal(0, after.GetProperty("visibility").GetInt32()); Assert.Equal(0, after.GetProperty("motion").GetInt32());
        Assert.Equal(0, fixture.FlowsData.DryRunCalls); Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
    }

    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
