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
        await FitAndWaitAsync(page);
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
    [InlineData(1280, 800)]
    [InlineData(195, 422)]
    public async Task Execution_preview_requires_explicit_external_samples_and_shows_real_pure_values(int width, int height)
    {
        var fixture = _fixture!; fixture.FlowsData.SeedExecutionPreviewGraph();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("flow-row").WaitForAsync(new() { Timeout = 90_000 });
        await page.GetByTestId("flow-edit").First.ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(6);
        await FitAndWaitAsync(page);
        var documentScroll = await page.EvaluateAsync<double[]>("() => [window.scrollX, window.scrollY]");
        var before = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await CaptureExecutionPreviewEvidenceAsync(page, width, height, "before-open", new { before, documentScroll });
        await page.GetByTestId("flow-dry-run-toggle").ClickAsync();
        var dialog = page.GetByTestId("flow-preview-dialog");
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        var during = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await CaptureExecutionPreviewEvidenceAsync(page, width, height, "dialog-open", new { before, during });
        Assert.InRange(during.Y, before.Y - 1, before.Y + 1);
        Assert.InRange(during.Height, before.Height - 1, before.Height + 1);
        await dialog.GetByTestId("flow-sample-execution-id").FillAsync("9223372036854775805");
        await dialog.GetByTestId("flow-execution-preview").ClickAsync();
        await ExpectExecutionPreviewHiddenAsync(page, dialog, width, height, "first-submit");
        var player = page.GetByTestId("flow-trace-player");
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player.Locator("[data-trace-position]")).ToHaveTextAsync("Step 1 of 2");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("sample-result-unavailable");
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("No external action was executed");
        await player.Locator("[data-trace-action=exit]").ClickAsync();

        await page.GetByTestId("flow-dry-run-toggle").ClickAsync();
        await Assertions.Expect(dialog).ToBeVisibleAsync();
        await Assertions.Expect(dialog.GetByTestId("flow-sample-execution-id")).ToHaveValueAsync("9223372036854775805");
        await dialog.Locator(".flow-sample-fixtures summary").ClickAsync();
        await dialog.GetByTestId("flow-sample-fixture-enabled").CheckAsync();
        await dialog.GetByTestId("flow-execution-preview").ClickAsync();
        await Assertions.Expect(dialog.GetByTestId("flow-sample-error")).ToContainTextAsync("Choose the simulated outcome");
        Assert.Equal(1, fixture.FlowsData.DryRunCalls);
        await dialog.GetByTestId("flow-sample-fixture-outcome").SelectOptionAsync("succeeded");
        await dialog.GetByTestId("flow-sample-fixture-output").FillAsync(JsonSerializer.Serialize(new
        { outcome = "succeeded", executionId = "9223372036854775807", exitCode = 0, summary = "Explicit synthetic repair sample" }));
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"flows-execution-sample-{width}.png"), Animations = ScreenshotAnimations.Disabled });
        await dialog.GetByTestId("flow-execution-preview").ClickAsync();
        await ExpectExecutionPreviewHiddenAsync(page, dialog, width, height, "fixture-submit");
        await Assertions.Expect(player).ToBeVisibleAsync();
        await Assertions.Expect(player.Locator("[data-trace-position]")).ToHaveTextAsync("Step 1 of 5");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("9223372036854775807");
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("Explicit synthetic repair sample");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("true");
        await player.Locator("[data-trace-action=next]").ClickAsync();
        var mapped = player.Locator("[data-trace-panel]:not([hidden])");
        await Assertions.Expect(mapped).ToContainTextAsync("Map explicit sample result");
        await Assertions.Expect(mapped).ToContainTextAsync("Sample job: 9223372036854775807");
        await mapped.Locator("details").Last.Locator("pre").ScrollIntoViewIfNeededAsync();
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"flows-execution-mapped-{width}.png"), Animations = ScreenshotAnimations.Disabled });
        await player.Locator("[data-trace-action=previous]").ClickAsync();
        await player.Locator("[data-trace-action=restart]").ClickAsync();
        Assert.Equal(2, fixture.FlowsData.DryRunCalls);
        Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
        await CaptureExecutionPreviewEvidenceAsync(page, width, height, "after-inspection", new { before, documentScroll });
        // Inspecting output and clicking playback controls can scroll the document. Compare at the original document viewport.
        await page.EvaluateAsync("position => window.scrollTo({ left: position[0], top: position[1], behavior: 'instant' })", documentScroll);
        await page.WaitForFunctionAsync("position => window.scrollX === position[0] && window.scrollY === position[1]", documentScroll);
        var after = (await page.GetByTestId("flow-canvas").BoundingBoxAsync())!;
        await CaptureExecutionPreviewEvidenceAsync(page, width, height, "after-restore", new { before, after, documentScroll });
        Assert.InRange(after.Y, before.Y - 1, before.Y + 1); Assert.InRange(after.Height, before.Height - 1, before.Height + 1);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"flows-execution-preview-{width}.json"), JsonSerializer.Serialize(new
        {
            width, height, before, during, after, evaluations = fixture.FlowsData.DryRunCalls,
            saves = fixture.FlowsData.SaveCalls, publications = fixture.FlowsData.PublishCalls,
            sourceExecutionId = "9223372036854775805", simulatedExecutionId = "9223372036854775807",
            expectedBranch = new[] { "event", "succeeded", "true", "result", "finished" }
        }));
        Assert.Empty(errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Historical_replay_uses_immutable_graph_and_recorded_snapshots_with_no_evaluation_or_writes(bool captured)
    {
        var fixture = _fixture!; var detail = fixture.FlowsData.SeedRecordedRun(captured);
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1440, Height = 900 } });
        var page = await context.NewPageAsync(); page.SetDefaultTimeout(30_000);
        var errors = new List<string>(); page.PageError += (_, error) => errors.Add(error);
        await page.GotoAsync($"{fixture.BaseAddress}/flows?tenantId=17&run={detail.Run.Id:D}", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Assertions.Expect(page.GetByTestId("flow-run-result")).ToContainTextAsync("Fixture client", new() { Timeout = 90_000 });
        await page.GetByTestId("flow-run-replay").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-dirty")).ToContainTextAsync("Immutable version 1");
        await Assertions.Expect(page.GetByTestId("flow-node-title")).ToHaveCountAsync(3);
        await Assertions.Expect(page.Locator("[data-node-kind=MapIncident] [data-testid=flow-node-title]")).Not.ToHaveTextAsync("Current mutable draft mapping");
        var player = page.GetByTestId("flow-trace-player");
        await Assertions.Expect(player).ToBeVisibleAsync();
        if (captured)
        {
            await Assertions.Expect(player).ToContainTextAsync("Recorded playback");
            await player.Locator("[data-trace-action=next]").ClickAsync();
            await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("Recorded synthetic incident");
            await player.Locator("[data-trace-action=next]").ClickAsync();
            await Assertions.Expect(player.Locator("[data-trace-panel]:not([hidden])")).ToContainTextAsync("NR-2026-000123");
            await player.Locator("[data-trace-action=previous]").ClickAsync();
            await player.Locator("[data-trace-action=restart]").ClickAsync();
            await Assertions.Expect(player.Locator("[data-trace-position]")).ToHaveTextAsync("Step 1 of 3");
        }
        else
        {
            await Assertions.Expect(player).ToContainTextAsync("no captured node snapshots");
            await Assertions.Expect(player.Locator("[data-trace-action]")).ToHaveCountAsync(0);
        }
        await page.GetByTestId("flow-focus").ClickAsync();
        await Assertions.Expect(page.GetByTestId("flow-focus")).ToHaveAttributeAsync("aria-pressed", "true");
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, captured ? "flows-recorded-playback.png" : "flows-recorded-missing.png"), Animations = ScreenshotAnimations.Disabled });
        Assert.Equal(1, fixture.FlowsData.RunLookups); Assert.Equal(2, fixture.FlowsData.VersionLookups); Assert.Equal(0, fixture.FlowsData.HistoryReads);
        Assert.Equal(0, fixture.FlowsData.DryRunCalls); Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
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


    [Fact]
    public async Task Client_playback_copies_visible_captured_data_and_marks_only_the_recorded_route()
    {
        var fixture = _fixture!;
        await using var context = await browserFixture.Browser.NewContextAsync();
        var page = await context.NewPageAsync();
        await page.GotoAsync(fixture.BaseAddress + "/flows", new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        var result = await page.EvaluateAsync<JsonElement>("""
            async () => {
                const { attach } = await import("/js/flows-playback.js");
                const scope = document.createElement("section");
                scope.id = "playback-inspection-scope";
                scope.innerHTML = '<article class="flow-node" data-node-id="captured-node"></article><article class="flow-node" data-node-id="target-node"></article><article class="flow-node" data-node-id="uncaptured-node"></article><svg><g data-flow-edge-id="chosen-edge" data-flow-source-node="captured-node" data-flow-target-node="target-node"><path data-flow-edge-id="chosen-edge" data-flow-source-node="captured-node" data-flow-target-node="target-node"></path></g><path data-flow-edge-id="other-edge" data-flow-source-node="captured-node" data-flow-target-node="target-node"></path></svg>';
                const root = document.createElement("section");
                root.id = "playback-inspection-player";
                root.dataset.scopeId = scope.id; root.dataset.context = "captured-inspection";
                root.innerHTML = '<button data-trace-action="play">Play</button><button data-trace-action="previous">Previous</button><button data-trace-action="next">Next</button><button data-trace-action="restart">Restart</button><output data-trace-position></output><p hidden data-trace-unvisited>No captured step</p><article data-trace-panel="0" data-node-id="captured-node" data-next-node-id="target-node" data-edge-id="chosen-edge"><div data-trace-data><pre data-trace-value>null</pre><button data-trace-copy>Copy captured JSON</button><span data-trace-copy-status></span></div></article><article hidden data-trace-panel="1" data-node-id="target-node"><div data-trace-data><pre data-trace-value>visible excerpt</pre><button data-trace-copy>Copy visible excerpt</button><span data-trace-copy-status></span><code>protected-reference-not-fetched</code></div></article>';
                scope.append(root); document.body.append(scope);
                const clipboardDescriptor = Object.getOwnPropertyDescriptor(navigator, "clipboard");
                const copied = [];
                let handle;
                try {
                    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async text => copied.push(text) } });
                    handle = attach(root.id, "captured-inspection");
                    const chosenPath = scope.querySelector('path[data-flow-edge-id="chosen-edge"]');
                    const route = {
                        path: chosenPath.hasAttribute("data-flow-playback-edge"),
                        group: scope.querySelector("svg g").hasAttribute("data-flow-playback-edge"),
                        other: scope.querySelector('path[data-flow-edge-id="other-edge"]').hasAttribute("data-flow-playback-edge")
                    };
                    root.querySelector('[data-trace-panel="0"] [data-trace-copy]').click();
                    await Promise.resolve();
                    const nullStatus = root.querySelector('[data-trace-panel="0"] [data-trace-copy-status]').textContent;
                    root.querySelector("[data-trace-action=next]").click();
                    root.querySelector('[data-trace-panel="1"] [data-trace-copy]').click();
                    await Promise.resolve();
                    const excerptStatus = root.querySelector('[data-trace-panel="1"] [data-trace-copy-status]').textContent;
                    scope.querySelector('[data-node-id="uncaptured-node"]').click();
                    const uncaptured = {
                        message: !root.querySelector("[data-trace-unvisited]").hidden,
                        visiblePanels: root.querySelectorAll("[data-trace-panel]:not([hidden])").length,
                        marked: scope.querySelector('[data-node-id="uncaptured-node"]').hasAttribute("data-flow-playback-node"),
                        edges: scope.querySelectorAll("[data-flow-playback-edge]").length,
                        position: root.querySelector("[data-trace-position]").textContent
                    };
                    root.querySelector("[data-trace-action=restart]").click();
                    const restarted = { position: root.querySelector("[data-trace-position]").textContent, message: root.querySelector("[data-trace-unvisited]").hidden };
                    Object.defineProperty(navigator, "clipboard", { configurable: true, value: { writeText: async () => { throw new Error("denied"); } } });
                    root.querySelector('[data-trace-panel="0"] [data-trace-copy]').click();
                    await Promise.resolve(); await Promise.resolve();
                    const deniedStatus = root.querySelector('[data-trace-panel="0"] [data-trace-copy-status]').textContent;
                    return { route, copied, nullStatus, excerptStatus, uncaptured, restarted, deniedStatus };
                } finally {
                    handle?.dispose(); scope.remove();
                    if (clipboardDescriptor) Object.defineProperty(navigator, "clipboard", clipboardDescriptor);
                    else delete navigator.clipboard;
                }
            }
            """);
        var route = result.GetProperty("route");
        Assert.True(route.GetProperty("path").GetBoolean()); Assert.False(route.GetProperty("group").GetBoolean()); Assert.False(route.GetProperty("other").GetBoolean());
        Assert.Equal(new[] { "null", "visible excerpt" }, result.GetProperty("copied").EnumerateArray().Select(item => item.GetString()).ToArray());
        Assert.Equal("Copied visible data.", result.GetProperty("nullStatus").GetString()); Assert.Equal("Copied visible data.", result.GetProperty("excerptStatus").GetString());
        var uncaptured = result.GetProperty("uncaptured");
        Assert.True(uncaptured.GetProperty("message").GetBoolean()); Assert.True(uncaptured.GetProperty("marked").GetBoolean());
        Assert.Equal(0, uncaptured.GetProperty("visiblePanels").GetInt32()); Assert.Equal(0, uncaptured.GetProperty("edges").GetInt32());
        Assert.Equal("Selected node has no captured step", uncaptured.GetProperty("position").GetString());
        Assert.Equal("Step 1 of 2", result.GetProperty("restarted").GetProperty("position").GetString()); Assert.True(result.GetProperty("restarted").GetProperty("message").GetBoolean());
        Assert.Equal("Copy was not permitted. Select the visible text to copy it.", result.GetProperty("deniedStatus").GetString());
        Assert.Equal(0, fixture.FlowsData.DryRunCalls); Assert.Equal(0, fixture.FlowsData.SaveCalls); Assert.Equal(0, fixture.FlowsData.PublishCalls);
    }


    private static async Task FitAndWaitAsync(IPage page)
    {
        var canvas = page.GetByTestId("flow-canvas");
        var previous = long.Parse((await canvas.GetAttributeAsync("data-fit-revision"))!, System.Globalization.CultureInfo.InvariantCulture);
        await page.GetByTestId("flow-fit").ClickAsync();
        await page.WaitForFunctionAsync("""
            previous => {
                const panel = document.querySelector('[data-testid=flow-canvas]');
                return panel?.dataset.fitState === 'complete' && Number(panel.dataset.fitRevision) > previous;
            }
            """, previous);
    }

    private async Task ExpectExecutionPreviewHiddenAsync(IPage page, ILocator dialog, int width, int height, string stage)
    {
        await CaptureExecutionPreviewEvidenceAsync(page, width, height, stage);
        try { await Assertions.Expect(dialog).ToBeHiddenAsync(); }
        catch
        {
            await CaptureExecutionPreviewEvidenceAsync(page, width, height, stage + "-failed");
            throw;
        }
    }

    private async Task CaptureExecutionPreviewEvidenceAsync(IPage page, int width, int height, string stage, object? measured = null)
    {
        var dom = await page.EvaluateAsync<JsonElement>("""
            () => {
                const rect = element => {
                    if (!element) return null;
                    const r = element.getBoundingClientRect(), style = getComputedStyle(element);
                    return { x:r.x, y:r.y, width:r.width, height:r.height, scrollLeft:element.scrollLeft,
                        scrollTop:element.scrollTop, clientWidth:element.clientWidth, clientHeight:element.clientHeight,
                        display:style.display, position:style.position, overflowX:style.overflowX, overflowY:style.overflowY };
                };
                const one = selector => document.querySelector(selector);
                const text = selector => one(selector)?.textContent?.slice(0,2000) ?? null;
                const dialog = one('[data-testid="flow-preview-dialog"]');
                const canvas = one('[data-testid="flow-canvas"]');
                return {
                    viewport:{ width:innerWidth, height:innerHeight, scrollX, scrollY, dpr:devicePixelRatio },
                    active:{ tag:document.activeElement?.tagName, testId:document.activeElement?.getAttribute('data-testid') },
                    editor:rect(one('[data-testid="flow-editor"]')), canvas:rect(canvas),
                    inverseScale:canvas?.getAttribute('data-inverse-scale'), fit:{state:canvas?.dataset.fitState, revision:canvas?.dataset.fitRevision}, scroll:rect(one('[id^="flow-scroll-"]')),
                    toolbar:rect(one('.flow-toolbar')), canvasTools:rect(one('.flow-canvas-tools')),
                    zoom:text('.flow-canvas-tools>span'), dirty:text('[data-testid="flow-dirty"]'),
                    dialog:{ open:dialog?.open ?? false, box:rect(dialog), children:dialog?.childElementCount ?? 0,
                        fieldset:rect(dialog?.querySelector('fieldset')), text:dialog?.textContent?.slice(0,3000) ?? null },
                    error:text('[data-testid="flow-error"]'), issues:text('[data-testid="flow-validation-issues"]'),
                    result:text('[data-testid="flow-preview"]'), sampleError:text('[data-testid="flow-sample-error"]'),
                    fields:[...(dialog?.querySelectorAll('input,select,textarea,button') ?? [])].slice(0,40).map(element => ({
                        tag:element.tagName, type:element.type, testId:element.getAttribute('data-testid'),
                        disabled:element.disabled, value:String(element.value ?? '').slice(0,160), box:rect(element)
                    }))
                };
            }
            """);
        var api = _fixture!.FlowsData;
        var receipt = JsonSerializer.Serialize(new { stage, width, height, measured, evaluations = api.DryRunCalls,
            saves = api.SaveCalls, publications = api.PublishCalls, dom });
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ??
            Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(evidence);
        await File.WriteAllTextAsync(Path.Combine(evidence, $"flows-execution-dialog-{width}-{stage}.json"), receipt);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"flows-execution-dialog-{width}-{stage}.png"), Animations = ScreenshotAnimations.Disabled });
        Console.WriteLine($"Flow execution preview evidence {width}/{stage}: {receipt}");
    }

    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
