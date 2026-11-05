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

    public async ValueTask InitializeAsync() => _fixture = await ClientsManagementFixtureHost.StartAsync();
    public async ValueTask DisposeAsync() { if (_fixture is not null) await _fixture.DisposeAsync(); }
}
