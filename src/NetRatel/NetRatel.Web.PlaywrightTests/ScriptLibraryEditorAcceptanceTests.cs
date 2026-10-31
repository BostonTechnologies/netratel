using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "BrowserAcceptance")]
public sealed class ScriptLibraryEditorAcceptanceTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1366, 768, "light")]
    [InlineData(390, 844, "dark")]
    public async Task Keyboard_selection_and_full_screen_preserve_an_editable_name_and_Monaco_draft(int width, int height, string theme)
    {
        var scripts = new ScriptLibraryWorkspaceFixture();
        await using var host = await ClientsManagementFixtureHost.StartAsync(scripts.Register);
        await using var context = await browserFixture.Browser.NewContextAsync(new()
        {
            ViewportSize = new() { Width = width, Height = height },
            ColorScheme = theme == "dark" ? ColorScheme.Dark : ColorScheme.Light
        });
        await context.AddInitScriptAsync($"localStorage.setItem('netratel.theme.preference', '{theme}');");
        var page = await context.NewPageAsync();
        page.SetDefaultTimeout(15_000);
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        var evidence = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(evidence);
        var suffix = $"{theme}-{width}x{height}";
        try
        {
            var response = await page.GotoAsync(host.BaseAddress + "/scriptlibrary", new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = 30_000 });
            Assert.True(response?.Ok == true, $"Script-library fixture returned {response?.Status}.");
            await page.GetByTestId("app-main-content").WaitForAsync(new() { Timeout = 90_000 });
            await page.Locator($"html[data-netratel-theme='{theme}']").WaitForAsync();
            var drawer = page.GetByTestId("app-navigation-drawer");
            if (width < 1280 && !await drawer.EvaluateAsync<bool>("e => e.classList.contains('mud-drawer--closed')"))
                await page.GetByTestId("navigation-toggle").ClickAsync();
            if (width < 1280)
                await Assertions.Expect(drawer).ToHaveClassAsync(new Regex("mud-drawer--closed"));

            var tree = page.GetByTestId("script-library-tree");
            var folder = tree.GetByRole(AriaRole.Button, new() { Name = "Folder: operations", Exact = true });
            await folder.ClickAsync();
            await Assertions.Expect(folder.Locator(".script-library-folder-count")).ToHaveTextAsync("1");
            var script = tree.GetByRole(AriaRole.Button, new() { Name = "Select script: Inventory.ps1", Exact = true });
            await Assertions.Expect(script).ToBeVisibleAsync();
            var folderBounds = (await folder.BoundingBoxAsync())!;
            var scriptBounds = (await script.BoundingBoxAsync())!;
            Assert.InRange(scriptBounds.X - folderBounds.X, 12, 30);
            Assert.True(await script.Locator(".script-library-node-name").EvaluateAsync<bool>("""
                element => {
                    const style = getComputedStyle(element);
                    return style.whiteSpace === 'nowrap' && style.textOverflow === 'ellipsis'
                        && parseFloat(style.fontSize) <= 14;
                }
                """), "Tree labels must remain compact single-line names.");
            await script.FocusAsync();
            await script.PressAsync("Enter");
            var editor = page.GetByTestId("script-library-editor");
            await editor.Locator(".monaco-editor").WaitForAsync(new() { Timeout = 90_000 });
            await ReplaceContentAsync(editor, "# inline_draft_marker");
            await Assertions.Expect(page.GetByTestId("script-editor-save")).ToBeEnabledAsync();
            await page.GetByRole(AriaRole.Textbox, new() { Name = "Script name", Exact = true }).FillAsync("Inline draft.ps1");
            await CaptureAsync(page, evidence, $"script-editor-inline-{suffix}");

            await page.GetByRole(AriaRole.Button, new() { Name = "Edit script full screen", Exact = true }).ClickAsync();
            var dialog = page.Locator(".script-editor-dialog");
            await Assertions.Expect(dialog).ToBeVisibleAsync();
            var fullEditor = dialog.GetByTestId("script-library-editor");
            await Assertions.Expect(fullEditor.Locator(".view-lines")).ToContainTextAsync("inline_draft_marker");
            await Assertions.Expect(dialog.GetByRole(AriaRole.Textbox, new() { Name = "Script name", Exact = true })).ToHaveValueAsync("Inline draft.ps1");
            // Monaco can initialize before MudBlazor's scale entrance animation finishes.
            // Measure settled geometry, without changing the editor/viewport requirements.
            await dialog.EvaluateAsync("""
                async element => {
                    const finite = element.getAnimations().filter(animation =>
                        Number.isFinite(animation.effect?.getComputedTiming().endTime));
                    await Promise.all(finite.map(animation => animation.finished));
                }
                """);
            var geometry = await dialog.EvaluateAsync<JsonElement>("""
                element => {
                    const describe = node => {
                        if (!node) return null;
                        const rect = node.getBoundingClientRect(), style = getComputedStyle(node);
                        return { rect: rect.toJSON(), width: style.width, height: style.height,
                            maxHeight: style.maxHeight, minHeight: style.minHeight, flex: style.flex,
                            boxSizing: style.boxSizing, padding: style.padding, overflow: style.overflow,
                            transform: style.transform };
                    };
                    const frameNode = element.querySelector('[data-testid="script-library-editor-frame"]');
                    const editorNode = frameNode?.querySelector('.monaco-editor');
                    const frame = frameNode?.getBoundingClientRect(), editor = editorNode?.getBoundingClientRect();
                    const checks = {
                        frameWidth: !!frame && frame.width >= innerWidth * .85,
                        frameHeight: !!frame && frame.height >= innerHeight * .65,
                        contained: !!frame && frame.x >= -1 && frame.y >= -1 && frame.right <= innerWidth + 1 && frame.bottom <= innerHeight + 1,
                        editorHeight: !!editor && editor.height >= frame.height - 4,
                        editorWidth: !!editor && editor.width >= frame.width - 4
                    };
                    return { fits: Object.values(checks).every(Boolean), checks,
                        viewport: { width: innerWidth, height: innerHeight, dpr: devicePixelRatio },
                        dialog: describe(element), content: describe(element.querySelector('.mud-dialog-content')),
                        pane: describe(element.querySelector('[data-testid="script-editor-pane"]')),
                        toolbar: describe(element.querySelector('.script-editor-toolbar')),
                        frame: describe(frameNode), editor: describe(editorNode),
                        editorContainer: describe(element.querySelector('.monaco-editor-container')),
                        animations: element.getAnimations().map(animation => ({ name: animation.animationName,
                            state: animation.playState, time: animation.currentTime,
                            endTime: animation.effect?.getComputedTiming().endTime })) };
                }
                """);
            await File.WriteAllTextAsync(Path.Combine(evidence, $"script-editor-geometry-{suffix}.json"), geometry.GetRawText());
            Assert.True(geometry.GetProperty("fits").GetBoolean(),
                $"Full-screen Monaco must fill the available viewport without clipping or collapsing. {geometry.GetRawText()}");
            foreach (var action in new[] { "Delete selected script", "Save", "Exit full screen" })
                await Assertions.Expect(dialog.GetByRole(AriaRole.Button, new() { Name = action, Exact = true })).ToBeInViewportAsync(new() { Ratio = 1 });
            await ReplaceContentAsync(fullEditor, "# fullscreen_draft_marker");
            await dialog.GetByRole(AriaRole.Textbox, new() { Name = "Script name", Exact = true }).FillAsync("Full screen draft.ps1");
            await CaptureAsync(page, evidence, $"script-editor-fullscreen-{suffix}");
            await dialog.GetByRole(AriaRole.Button, new() { Name = "Exit full screen", Exact = true }).ClickAsync();

            await Assertions.Expect(dialog).ToHaveCountAsync(0);
            await Assertions.Expect(page.GetByRole(AriaRole.Textbox, new() { Name = "Script name", Exact = true })).ToHaveValueAsync("Full screen draft.ps1");
            await Assertions.Expect(editor.Locator(".view-lines")).ToContainTextAsync("fullscreen_draft_marker");
            var restoredInput = EditorInput(editor);
            await restoredInput.PressAsync("ControlOrMeta+End");
            await restoredInput.PressSequentiallyAsync(" editable_again");
            await Assertions.Expect(editor.Locator(".view-lines")).ToContainTextAsync("editable_again");
            await Assertions.Expect(page.GetByTestId("script-editor-save")).ToBeEnabledAsync();
            await CaptureAsync(page, evidence, $"script-editor-returned-{suffix}");
            Assert.Empty(errors);
            Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
            Assert.False(await page.EvaluateAsync<bool>("() => document.documentElement.scrollWidth > innerWidth + 1"), "Script workspace must not overflow horizontally.");
        }
        catch
        {
            await CaptureAsync(page, evidence, $"script-editor-failure-{suffix}");
            throw;
        }
    }

    private static ILocator EditorInput(ILocator editor) => editor.GetByRole(AriaRole.Textbox, new() { Name = "Editor content", Exact = true });

    private static async Task ReplaceContentAsync(ILocator editor, string content)
    {
        var input = EditorInput(editor);
        await input.PressAsync("ControlOrMeta+A");
        await input.PressAsync("Backspace");
        await input.PressSequentiallyAsync(content);
        await Assertions.Expect(editor.Locator(".view-lines")).ToContainTextAsync(content);
    }

    private static Task CaptureAsync(IPage page, string root, string name) => page.ScreenshotAsync(new()
    {
        Path = Path.Combine(root, name + ".png"), FullPage = true, Animations = ScreenshotAnimations.Disabled
    });
}
