using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class JobsDialogsAcceptanceTests(ClientsManagementBrowserFixture browserFixture) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768, "light", 1d)]
    [InlineData(1366, 768, "dark", 1d)]
    [InlineData(1600, 900, "light", 1d)]
    [InlineData(390, 844, "dark", 1d)]
    [InlineData(640, 400, "light", 2d)]
    public async Task Eight_inputs_and_parameter_editor_keep_actions_visible_and_keyboard_focus_inside(int width, int height, string theme, double dpr)
    {
        await using var fixture = await StartAsync(8);
        await using var context = await ContextAsync(width, height, theme, dpr);
        var page = await context.NewPageAsync();
        await OpenAsync(page, fixture, "/jobs", width);
        await page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true }).ClickAsync();
        var form = page.Locator(".run-job-dialog");
        await form.GetByLabel("Input 8", new() { Exact = true }).WaitForAsync();
        var dialog = page.Locator(".mud-dialog").Last;
        var start = dialog.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true });
        await Assertions.Expect(start).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        if (width >= 1366)
        {
            await WaitForDialogAnimationAsync(dialog);
            var bounds = (await dialog.BoundingBoxAsync())!;
            Assert.InRange(bounds.Width, 1040, 1200);
            await Assertions.Expect(form.GetByLabel("Input 1", new() { Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
            await Assertions.Expect(form.GetByLabel("Input 8", new() { Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
            Assert.True(await dialog.Locator(".mud-dialog-content").EvaluateAsync<bool>("e => e.scrollHeight <= e.clientHeight + 2"), "Eight short inputs fit without scrolling at desktop size.");
        }
        else
        {
            await form.GetByLabel("Input 8", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(start).ToBeInViewportAsync(new() { Ratio = 1 });
        }
        await form.GetByLabel("Input 1", new() { Exact = true }).FocusAsync();
        for (var index = 0; index < 12; index++)
        {
            await page.Keyboard.PressAsync("Tab");
            Assert.True(await dialog.EvaluateAsync<bool>("e => e.contains(document.activeElement)"), "Keyboard traversal stays in the dialog.");
        }
        await CaptureAsync(page, $"jobs-run-eight-{width}x{height}-{theme}-dpr{dpr}");
        await dialog.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Nightly backup", Exact = true }).ClickAsync();
        var parent = page.Locator(".jobs-edit-dialog");
        await parent.GetByLabel("Job Name", new() { Exact = true }).FillAsync("Unsaved parent title");
        await parent.GetByRole(AriaRole.Tab, new() { Name = "Parameters", Exact = false }).ClickAsync();
        await parent.GetByRole(AriaRole.Button, new() { Name = "Add Param", Exact = true }).ClickAsync();
        var editor = page.Locator(".param-editor");
        await editor.GetByLabel("Name", new() { Exact = true }).FillAsync("New input");
        var child = page.Locator(".mud-dialog").Last;
        if (width >= 1366)
        {
            await WaitForDialogAnimationAsync(child);
            Assert.InRange((await child.BoundingBoxAsync())!.Width, 600, 720);
        }
        await Assertions.Expect(child.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        await CaptureAsync(page, $"jobs-add-parameter-{width}x{height}-{theme}-dpr{dpr}");
        await child.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(editor).ToHaveCountAsync(0);
        await Assertions.Expect(parent.GetByLabel("Job Name", new() { Exact = true })).ToHaveValueAsync("Unsaved parent title");
        await Assertions.Expect(parent.GetByText("New input", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(parent.GetByRole(AriaRole.Button, new() { Name = "Add Param", Exact = true })).ToBeFocusedAsync();
        await parent.GetByRole(AriaRole.Button, new() { Name = "Edit parameter Input 1", Exact = true }).ClickAsync();
        await editor.GetByLabel("Name", new() { Exact = true }).FillAsync("Cancelled edit");
        await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(parent.GetByRole(AriaRole.Button, new() { Name = "Edit parameter Input 1", Exact = true })).ToBeFocusedAsync();
        await Assertions.Expect(parent.GetByText("Cancelled edit", new() { Exact = true })).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task Eight_immediate_parameter_saves_edit_and_cancel_keep_parent_draft_and_reach_run_form()
    {
        await using var fixture = await StartAsync(0);
        await using var context = await ContextAsync(1366, 768, "light");
        var page = await context.NewPageAsync();
        await OpenAsync(page, fixture, "/jobs", 1366);
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Nightly backup", Exact = true }).ClickAsync();
        var parent = page.Locator(".jobs-edit-dialog");
        await parent.GetByLabel("Job Name", new() { Exact = true }).FillAsync("Unsaved definition stays");
        await parent.GetByRole(AriaRole.Tab, new() { Name = "Parameters", Exact = false }).ClickAsync();
        var types = new[] { "Text", "Whole number", "True / false", "JSON", "Choice", "Secret", "Text", "Text" };
        for (var index = 0; index < types.Length; index++)
        {
            await parent.GetByRole(AriaRole.Button, new() { Name = "Add Param", Exact = true }).ClickAsync();
            var editor = page.Locator(".param-editor");
            await editor.GetByLabel("Name", new() { Exact = true }).FillAsync($"Created {index + 1}");
            if (types[index] != "Text")
            {
                await editor.GetByRole(AriaRole.Combobox, new() { Name = "Type", Exact = true }).ClickAsync();
                await page.Locator(".mud-popover-open").GetByText(types[index], new() { Exact = true }).ClickAsync();
            }
            if (types[index] == "Choice") await editor.GetByLabel("Options", new() { Exact = true }).FillAsync("safe\npreview");
            if (types[index] == "Whole number") await editor.GetByLabel("Default value", new() { Exact = true }).FillAsync("0");
            if (types[index] == "JSON") await editor.GetByLabel("Default value", new() { Exact = true }).FillAsync("{}");
            await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
            await Assertions.Expect(editor).ToHaveCountAsync(0);
            await Assertions.Expect(parent.GetByLabel("Job Name", new() { Exact = true })).ToHaveValueAsync("Unsaved definition stays");
        }
        await parent.GetByRole(AriaRole.Button, new() { Name = "Edit parameter Created 1", Exact = true }).ClickAsync();
        await page.Locator(".param-editor").GetByLabel("Name", new() { Exact = true }).FillAsync("Edited input");
        await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(parent.GetByText("Edited input", new() { Exact = true })).ToBeVisibleAsync();
        await parent.GetByRole(AriaRole.Button, new() { Name = "Edit parameter Created 2", Exact = true }).ClickAsync();
        await page.Locator(".param-editor").GetByLabel("Name", new() { Exact = true }).FillAsync("Discarded child name");
        await page.Locator(".mud-dialog").Last.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await Assertions.Expect(parent.GetByText("Created 2", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, "jobs-eight-saved-parameters-parent-draft");
        await parent.GetByRole(AriaRole.Button, new() { Name = "Cancel", Exact = true }).ClickAsync();
        await page.Locator(".mud-message-box").GetByRole(AriaRole.Button, new() { Name = "Discard", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true }).ClickAsync();
        await Assertions.Expect(page.Locator(".run-job-field")).ToHaveCountAsync(8);
        await Assertions.Expect(page.Locator(".run-job-dialog").GetByLabel("Edited input", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, "jobs-eight-created-run-inputs");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(24)]
    public async Task Empty_small_and_large_forms_choose_a_bounded_size(int count)
    {
        await using var fixture = await StartAsync(count);
        await using var context = await ContextAsync(1366, 768, "light");
        var page = await context.NewPageAsync();
        await OpenAsync(page, fixture, "/jobs", 1366);
        await page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true }).ClickAsync();
        var dialog = page.Locator(".mud-dialog").Last;
        await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true })).ToBeEnabledAsync();
        var content = dialog.Locator(".mud-dialog-content");
        await WaitForDialogAnimationAsync(dialog);
        if (count <= 1) Assert.InRange((await dialog.BoundingBoxAsync())!.Width, 300, 600);
        if (count > 20)
        {
            Assert.True(await content.EvaluateAsync<bool>("e => e.scrollHeight > e.clientHeight + 20"));
            await page.GetByLabel("Input 24", new() { Exact = true }).ScrollIntoViewIfNeededAsync();
            await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        }
        await CaptureAsync(page, $"jobs-run-count-{count}");
    }

    [Fact]
    public async Task Mixed_controls_validate_in_place_mask_secrets_and_preserve_zero_false()
    {
        await using var fixture = await StartAsync(6, mixed: true);
        await using var context = await ContextAsync(1366, 768, "dark");
        var page = await context.NewPageAsync();
        await OpenAsync(page, fixture, "/jobs", 1366);
        await page.GetByRole(AriaRole.Button, new() { Name = "Run Nightly backup", Exact = true }).ClickAsync();
        var form = page.Locator(".run-job-dialog");
        await Assertions.Expect(form.GetByLabel("Count", new() { Exact = true })).ToHaveValueAsync("0");
        await Assertions.Expect(form.Locator("input[type=checkbox]")).Not.ToBeCheckedAsync();
        await Assertions.Expect(form.GetByLabel("Token", new() { Exact = true })).ToHaveAttributeAsync("type", "password");
        await form.GetByLabel("Settings", new() { Exact = true }).FillAsync("{invalid");
        await form.GetByLabel("Count", new() { Exact = true }).FocusAsync();
        await Assertions.Expect(form.GetByText("Enter valid JSON.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true })).ToBeDisabledAsync();
        await form.GetByLabel("Settings", new() { Exact = true }).FillAsync("{\"enabled\":false}");
        await form.GetByLabel("Count", new() { Exact = true }).FocusAsync();
        await Assertions.Expect(page.GetByRole(AriaRole.Button, new() { Name = "Start Run", Exact = true })).ToBeEnabledAsync();
        await CaptureAsync(page, "jobs-run-mixed-dark");
    }

    [Fact]
    public async Task Delayed_three_step_stream_follows_panels_pauses_for_manual_inspection_and_keeps_final_output()
    {
        var stream = new JobsRunFixtureStream();
        await using var fixture = await StartAsync(8, stream: stream);
        await using var context = await ContextAsync(1366, 768, "light");
        var page = await context.NewPageAsync();
        await OpenAsync(page, fixture, "/jobs/42/runs/9", 1366);
        var panels = page.Locator(".job-run-step-panel");
        await Assertions.Expect(panels).ToHaveCountAsync(3);
        await Assertions.Expect(panels.Nth(0)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await stream.PublishAsync(1);
        await Assertions.Expect(panels.Nth(0).GetByText("Delayed output for step 1", new() { Exact = true })).ToBeVisibleAsync();
        await stream.PublishAsync(2);
        await Assertions.Expect(panels.Nth(0)).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await Assertions.Expect(panels.Nth(1)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await Assertions.Expect(panels.Nth(0).GetByText("Delayed output for step 1", new() { Exact = true })).Not.ToBeVisibleAsync();
        await Assertions.Expect(panels.Nth(1).GetByText("Delayed output for step 2", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, "jobs-follow-step-two");
        await panels.Nth(0).Locator(".mud-expand-panel-header").ClickAsync();
        await Assertions.Expect(page.GetByText("Follow paused", new() { Exact = true })).ToBeVisibleAsync();
        await stream.PublishAsync(3);
        await Assertions.Expect(panels.Nth(2).GetByText("Running", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(panels.Nth(0)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await Assertions.Expect(panels.Nth(2)).Not.ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await page.GetByRole(AriaRole.Button, new() { Name = "Resume", Exact = true }).ClickAsync();
        await Assertions.Expect(panels.Nth(2)).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-panel-expanded"));
        await Assertions.Expect(panels.Nth(2).GetByText("Delayed output for step 3", new() { Exact = true })).ToBeVisibleAsync();
        await stream.PublishAsync(3, terminal: true);
        await Assertions.Expect(page.GetByText("Execution completed successfully.", new() { Exact = true })).ToBeVisibleAsync();
        await Assertions.Expect(panels.Nth(2).GetByText("Delayed output for step 3", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, "jobs-follow-completed");
    }

    private static async Task<ClientsManagementFixtureHost> StartAsync(int count, bool mixed = false, JobsRunFixtureStream? stream = null) =>
        await ClientsManagementFixtureHost.StartAsync(services =>
        {
            NewTaskWorkspaceFixture.AddServices(services);
            new ScriptLibraryWorkspaceFixture().Register(services);
            JobsWorkspaceFixture.Configure(services, count, mixed, stream);
        });
    private async Task<IBrowserContext> ContextAsync(int width, int height, string theme, double dpr = 1d)
    {
        var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height }, DeviceScaleFactor = (float)dpr });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        return context;
    }
    private static async Task OpenAsync(IPage page, ClientsManagementFixtureHost fixture, string route, int width)
    {
        page.SetDefaultTimeout(20_000);
        await page.GotoAsync(fixture.BaseAddress + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("app-main-content").WaitForAsync(new() { Timeout = 90_000 });
        await page.Locator("html[data-netratel-theme]").WaitForAsync();
        var drawer = page.GetByTestId("app-navigation-drawer");
        var closed = await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')");
        if ((width >= 1280 && closed) || (width < 1280 && !closed)) await page.GetByTestId("navigation-toggle").ClickAsync();
        if (width < 1280) await Assertions.Expect(drawer).ToHaveClassAsync(new System.Text.RegularExpressions.Regex("mud-drawer--closed"));
    }
    private static async Task WaitForDialogAnimationAsync(ILocator dialog) =>
        await dialog.EvaluateAsync("e => Promise.all(e.getAnimations().map(animation => animation.finished.catch(() => {})))");
    private static async Task CaptureAsync(IPage page, string name)
    {
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"));
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled });
    }
}
