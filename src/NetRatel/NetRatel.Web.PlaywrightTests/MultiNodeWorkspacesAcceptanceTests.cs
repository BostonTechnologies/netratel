using Microsoft.Playwright;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class MultiNodeWorkspacesAcceptanceTests(ClientsManagementBrowserFixture browser) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 900, "light", 1d)]
    [InlineData(1366, 900, "dark", 1d)]
    [InlineData(1920, 1080, "light", 1d)]
    [InlineData(768, 1024, "light", 1d)]
    [InlineData(390, 844, "dark", 1d)]
    [InlineData(320, 740, "light", 1d)]
    [InlineData(1366, 900, "light", 2d)]
    public async Task Group_job_and_hostname_run_workspaces_retain_readable_sections_and_isolated_output(
        int width, int height, string theme, double scale)
    {
        ExecutionGroupWriteDto? saved = null;
        await using var fixture = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            NewTaskWorkspaceFixture.AddServices(services);
            new ScriptLibraryWorkspaceFixture().Register(services);
            JobsWorkspaceFixture.Configure(services, 0, multiNode: true, groupSaved: group => saved = group);
        });
        await using var context = await browser.Browser.NewContextAsync(new()
        { ViewportSize = new() { Width = width, Height = height }, HasTouch = width <= 768 });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        if (scale == 2d) await context.AddInitScriptAsync("document.addEventListener('DOMContentLoaded', () => document.documentElement.style.zoom = '2');");
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(20_000);

        await OpenAsync(page, fixture, "/tenants", width);
        await page.GetByRole(AriaRole.Button, new() { Name = "Manage Visual fixture", Exact = true }).ClickAsync();
        await page.GetByRole(AriaRole.Menuitem, new() { Name = "Edit Tenant", Exact = true }).ClickAsync();
        var tenant = page.Locator(".tenant-editor-dialog");
        await tenant.GetByLabel("Tenant name", new() { Exact = true }).WaitForAsync();
        await SectionAsync(tenant, "Tenant section", "Execution groups");
        await tenant.GetByRole(AriaRole.Button, new() { Name = "Edit execution group Five-node group", Exact = true }).ClickAsync();
        await tenant.GetByRole(AriaRole.Button, new() { Name = "Selected members (5)", Exact = true }).ClickAsync();
        await Assertions.Expect(tenant.Locator(".node-picker-row")).ToHaveCountAsync(5);
        await Assertions.Expect(tenant.GetByText("Offline", new() { Exact = false }).First).ToBeVisibleAsync();
        await tenant.GetByLabel("Group name", new() { Exact = true }).FillAsync("Reviewed five-node group");
        await page.Keyboard.PressAsync("Escape");
        await Assertions.Expect(tenant).ToBeVisibleAsync();
        if (width >= 768) await page.Mouse.ClickAsync(2, 2);
        await Assertions.Expect(tenant.GetByLabel("Group name", new() { Exact = true })).ToHaveValueAsync("Reviewed five-node group");
        await tenant.GetByRole(AriaRole.Button, new() { Name = "Save group", Exact = true }).ScrollIntoViewIfNeededAsync();
        await tenant.GetByRole(AriaRole.Button, new() { Name = "Save group", Exact = true }).ClickAsync();
        await Assertions.Expect(tenant.GetByText("Group saved · 5 members", new() { Exact = false })).ToBeVisibleAsync();
        Assert.NotNull(saved);
        Assert.Equal(5, saved.AgentIds.Distinct().Count());
        await Assertions.Expect(tenant.GetByRole(AriaRole.Button, new() { Name = "Cancel tenant changes", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        await Assertions.Expect(tenant.GetByRole(AriaRole.Button, new() { Name = "Save tenant changes", Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
        if (width <= 390)
        {
            var bounds = (await tenant.BoundingBoxAsync())!;
            Assert.InRange(bounds.Y, -1, 1);
            Assert.InRange(bounds.Height, height - 2, height + 2);
        }
        await CaptureAsync(page, $"groups-{width}-{theme}-scale{scale}");
        await tenant.GetByRole(AriaRole.Button, new() { Name = "Cancel tenant changes", Exact = true }).ClickAsync();

        await OpenAsync(page, fixture, "/jobs", width);
        await page.GetByRole(AriaRole.Button, new() { Name = "Edit Nightly backup", Exact = true }).ClickAsync();
        var editor = page.Locator(".jobs-edit-dialog");
        await editor.GetByLabel("Job Name", new() { Exact = false }).WaitForAsync();
        await editor.EvaluateAsync("async element => { for (let current = element; current; current = current.parentElement) await Promise.all(current.getAnimations().map(animation => animation.finished)); }");
        var editorBounds = (await editor.BoundingBoxAsync())!;
        Assert.InRange(editorBounds.Width, width - 32 * scale - 4, width + 2);
        Assert.InRange(editorBounds.Height, height - 32 * scale - 4, height + 2);
        foreach (var section in new[] { "Definition", "Targets", "Execution", "Steps", "Parameters", "Schedule" })
        {
            await SectionAsync(editor, "Job section", section);
            var sectionBounds = (await editor.BoundingBoxAsync())!;
            Assert.InRange(Math.Abs(sectionBounds.Width - editorBounds.Width), 0, 2);
            Assert.InRange(Math.Abs(sectionBounds.Height - editorBounds.Height), 0, 2);
        }
        await SectionAsync(editor, "Job section", "Targets");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Preview targets", Exact = true }).ClickAsync();
        await Assertions.Expect(editor.GetByText("5 targets", new() { Exact = false }).First).ToBeVisibleAsync();
        await editor.GetByRole(AriaRole.Combobox, new() { Name = "Target source", Exact = true }).ClickAsync();
        await page.Locator(".mud-popover-open").GetByText("Node filter", new() { Exact = true }).ClickAsync();
        await editor.GetByLabel("Node filter expression", new() { Exact = true }).FillAsync("tags: TestSystem");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Preview targets", Exact = true }).ClickAsync();
        await Assertions.Expect(editor.Locator(".job-target-preview-row")).ToHaveCountAsync(5);
        await SectionAsync(editor, "Job section", "Execution");
        await editor.GetByLabel("Thread Count", new() { Exact = true }).FillAsync("100");
        await editor.GetByText("Continue on remaining nodes, then fail the run", new() { Exact = true }).ClickAsync();
        await Assertions.Expect(editor.GetByRole(AriaRole.Tabpanel, new() { Name = "Execution", Exact = true }).GetByText("5 targets · configured 100 · up to 5 active", new() { Exact = false })).ToBeVisibleAsync();
        await editor.GetByLabel("Thread Count", new() { Exact = true }).FocusAsync();
        await page.Keyboard.PressAsync("Tab");
        Assert.True(await editor.EvaluateAsync<bool>("e => e.contains(document.activeElement)"));
        await CaptureAsync(page, $"jobs-execution-{width}-{theme}-scale{scale}");
        await editor.GetByRole(AriaRole.Button, new() { Name = "Save", Exact = true }).ClickAsync();
        await Assertions.Expect(editor).ToHaveCountAsync(0);

        await OpenAsync(page, fixture, "/jobs/42/runs/9", width);
        await Assertions.Expect(page.Locator(".job-run-node-panel")).ToHaveCountAsync(5);
        var panels = page.Locator(".job-run-node-panel");
        await panels.Nth(0).Locator(":scope > .mud-expand-panel-header").ClickAsync();
        await Assertions.Expect(panels.Nth(0).GetByText("lab-node-01: isolated output", new() { Exact = false })).ToBeVisibleAsync();
        await panels.Nth(1).Locator(":scope > .mud-expand-panel-header").ClickAsync();
        await Assertions.Expect(panels.Nth(1).GetByText("lab-node-02: isolated output", new() { Exact = false })).ToBeVisibleAsync();
        Assert.DoesNotContain("lab-node-02: isolated output", await panels.Nth(0).InnerTextAsync());
        Assert.DoesNotContain("lab-node-01: isolated output", await panels.Nth(1).InnerTextAsync());
        await Assertions.Expect(page.GetByText("Follow selected node", new() { Exact = true })).ToBeVisibleAsync();
        await CaptureAsync(page, $"runs-hostnames-{width}-{theme}-scale{scale}");
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
    }

    private static async Task SectionAsync(ILocator dialog, string switcherLabel, string name)
    {
        await dialog.WaitForAsync();
        var switcher = dialog.GetByLabel(switcherLabel, new() { Exact = true });
        await switcher.WaitForAsync(new() { State = WaitForSelectorState.Attached });
        if (await switcher.IsVisibleAsync())
        {
            await Assertions.Expect(dialog.GetByRole(AriaRole.Tab)).ToHaveCountAsync(0);
            await switcher.SelectOptionAsync(new SelectOptionValue { Label = name });
        }
        else await dialog.GetByRole(AriaRole.Tab, new() { Name = name, Exact = true }).ClickAsync();
    }
    private static async Task OpenAsync(IPage page, ClientsManagementFixtureHost fixture, string route, int width)
    {
        await page.GotoAsync(fixture.BaseAddress + route, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByTestId("app-main-content").WaitForAsync(new() { Timeout = 90_000 });
        await page.Locator("html[data-netratel-theme]").WaitForAsync();
        var drawer = page.GetByTestId("app-navigation-drawer");
        // Each full navigation creates a new drawer. Its viewport subscription
        // closes it on small screens; toggling before that initialization races
        // the automatic close and can reopen it over the workspace.
        await Assertions.Expect(drawer).ToHaveClassAsync(new System.Text.RegularExpressions.Regex(
            width < 1280 ? "mud-drawer--closed" : "mud-drawer--open"));
    }
    private static async Task CaptureAsync(IPage page, string name)
    {
        Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > document.documentElement.clientWidth + 1"));
        var root = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.Combine(Directory.GetCurrentDirectory(), "TestResults", "playwright");
        Directory.CreateDirectory(root);
        await page.ScreenshotAsync(new() { Path = Path.Combine(root, name + ".png"), FullPage = await page.Locator(".mud-dialog:visible").CountAsync() == 0, Animations = ScreenshotAnimations.Disabled });
    }
}
