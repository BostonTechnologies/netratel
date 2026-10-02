using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
public sealed class InstallScriptPreviewGeometryTests(ClientsManagementBrowserFixture fixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1280, 800)]
    [InlineData(390, 480)]
    public async Task ProductionStylesKeepTheEditorViewportAndScrollBottomInsideCompactPreview(int width, int height)
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Combine(repository.FullName, "AGENTS.md"))) repository = repository.Parent;
        Assert.NotNull(repository);
        var global = await File.ReadAllTextAsync(Path.Combine(repository.FullName, "src", "NetRatel", "NetRatel.Web", "wwwroot", "app-site.css"));
        var scoped = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "NetRatel.Web.styles.css"));
        var scope = Regex.Match(scoped, @"\.generated-install-script-preview\[(b-[a-z0-9]+)\]").Groups[1].Value;
        Assert.NotEmpty(scope);
        await using var context = await fixture.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = width, Height = height }
        });
        var page = await context.NewPageAsync();
        // Exercise the actual global/scoped cascade against Monaco's container
        // shape. This is viewport geometry evidence, not editor execution coverage.
        await page.SetContentAsync($"""
            <style>{global}</style><style>{scoped}</style>
            <div class="generated-install-script-preview" {scope}>
                <div class="monaco-editor-container" style="overflow:auto;box-sizing:border-box">
                    <div style="height:2000px">Long script</div><div id="script-end">End of script</div>
                </div>
            </div>
            """);
        var container = page.Locator(".monaco-editor-container");
        var fits = await container.EvaluateAsync<bool>("""
            element => {
                const parent = element.parentElement.getBoundingClientRect();
                const editor = element.getBoundingClientRect();
                return editor.height > 200 && editor.height < 400 && editor.bottom <= parent.bottom;
            }
            """);
        Assert.True(fits, "The global 600px important rule must not clip the compact editor viewport.");
        await container.EvaluateAsync("element => element.scrollTop = element.scrollHeight");
        var reachable = await container.EvaluateAsync<bool>("""
            element => document.getElementById('script-end').getBoundingClientRect().bottom <= element.getBoundingClientRect().bottom
            """);
        Assert.True(reachable, "The end of a long script must remain reachable by scrolling inside the preview.");
    }
}
