using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Playwright;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "ManualAcceptance")]
public sealed class XtermAssetAcceptanceTests(ClientsManagementBrowserFixture browserFixture)
    : IClassFixture<ClientsManagementBrowserFixture>
{
    [Fact]
    public async Task Shipped_assets_deliver_keyboard_output_fit_and_disposal_without_a_transport()
    {
        await using var host = await ClientsManagementFixtureHost.StartAsync();
        await using var context = await browserFixture.Browser.NewContextAsync(new() { ViewportSize = new() { Width = 1366, Height = 900 } });
        var page = await context.NewPageAsync();
        var errors = new List<string>();
        page.PageError += (_, error) => errors.Add(error);
        // A clean asset-only document preserves App.razor's fit-before-Monaco
        // load order; an existing AMD loader would change the UMD export mode.
        var document = await page.GotoAsync(host.BaseAddress + "/__fixtures/terminal-assets");
        Assert.Equal(200, document!.Status);
        var assets = new List<object>();
        foreach (var path in new[] { "/_content/XtermBlazor/XtermBlazor.min.css", "/js/xterm/addon-fit-0.11.0.min.js", "/_content/XtermBlazor/XtermBlazor.min.js" })
        {
            var response = await context.APIRequest.GetAsync(host.BaseAddress + path);
            Assert.Equal(200, response.Status);
            var bytes = await response.BodyAsync();
            Assert.NotEmpty(bytes);
            assets.Add(new { path, bytes = bytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() });
            if (path.EndsWith(".css", StringComparison.Ordinal))
                await page.AddStyleTagAsync(new() { Url = host.BaseAddress + path });
            else
                await page.AddScriptTagAsync(new() { Url = host.BaseAddress + path });
        }
        await page.EvaluateAsync("""
            () => {
                const id = 'asset-terminal';
                const calls = [];
                const originalDotNet = globalThis.DotNet;
                const observe = args => {
                    if (args[0] !== 'XtermBlazor') throw new Error('Unexpected callback assembly.');
                    if (args[2] !== id) throw new Error('Unexpected terminal callback identity.');
                    calls.push({ method: args[1], id: args[2], data: args[1] === 'OnData' ? args[3] : null });
                };
                // Observe this vendor instance only; no input is sent to a
                // managed terminal service or native gateway session.
                globalThis.DotNet = {
                    invokeMethodAsync: (...args) => { observe(args); return Promise.resolve(); },
                    invokeMethod: (...args) => { observe(args); return true; }
                };
                const element = document.createElement('div');
                element.id = id;
                element.style.cssText = 'position:fixed;left:20px;top:20px;width:640px;height:240px;z-index:10000;background:#111827';
                document.body.append(element);
                XtermBlazor.registerAddons({ 'addon-fit': new FitAddon.FitAddon() });
                XtermBlazor.registerTerminal(id, element,
                    { cols: 12, rows: 4, convertEol: true, fontFamily: 'monospace', fontSize: 14 }, ['addon-fit']);
                XtermBlazor.invokeAddonFunction(id, 'addon-fit', 'fit', []);
                window.xtermAssetProbe = {
                    id, calls, firstFit: { cols: XtermBlazor.getCols(id), rows: XtermBlazor.getRows(id) },
                    restore: () => {
                        if (originalDotNet === undefined) delete globalThis.DotNet;
                        else globalThis.DotNet = originalDotNet;
                    }
                };
                XtermBlazor.focus(id);
            }
            """);
        Assert.True(await page.EvaluateAsync<bool>("() => document.activeElement === document.querySelector('#asset-terminal .xterm-helper-textarea')"));
        await page.Keyboard.TypeAsync("probe");
        await page.Keyboard.PressAsync("Enter");
        await page.WaitForFunctionAsync("() => xtermAssetProbe.calls.filter(c => c.method === 'OnData').map(c => c.data).join('') === 'probe\\r'");
        await page.EvaluateAsync("() => XtermBlazor.write(xtermAssetProbe.id, 'OUTPUT: ✓\\r\\n')");
        await Assertions.Expect(page.Locator("#asset-terminal .xterm-rows")).ToContainTextAsync("OUTPUT: ✓");
        var dimensions = await page.EvaluateAsync<JsonElement>("""
            () => {
                const probe = xtermAssetProbe;
                document.getElementById(probe.id).style.width = '320px';
                document.getElementById(probe.id).style.height = '180px';
                XtermBlazor.invokeAddonFunction(probe.id, 'addon-fit', 'fit', []);
                return { first: probe.firstFit, second: { cols: XtermBlazor.getCols(probe.id), rows: XtermBlazor.getRows(probe.id) } };
            }
            """);
        Assert.True(dimensions.GetProperty("first").GetProperty("cols").GetInt32() > 12);
        Assert.True(dimensions.GetProperty("first").GetProperty("rows").GetInt32() > 4);
        Assert.True(dimensions.GetProperty("second").GetProperty("cols").GetInt32() < dimensions.GetProperty("first").GetProperty("cols").GetInt32());
        Assert.True(dimensions.GetProperty("second").GetProperty("rows").GetInt32() < dimensions.GetProperty("first").GetProperty("rows").GetInt32());
        var artifacts = Environment.GetEnvironmentVariable("NETRATEL_PLAYWRIGHT_ARTIFACT_ROOT") ?? Path.GetFullPath("TestResults/playwright");
        Directory.CreateDirectory(artifacts);
        await page.Locator("#asset-terminal").ScreenshotAsync(new() { Path = Path.Combine(artifacts, "xterm-assets-output.png"), Animations = ScreenshotAnimations.Disabled });
        var callbacks = await page.EvaluateAsync<JsonElement>("() => xtermAssetProbe.calls");
        var disposed = await page.EvaluateAsync<bool>("""
            () => {
                const id = xtermAssetProbe.id;
                XtermBlazor.disposeTerminal(id);
                xtermAssetProbe.restore();
                try { XtermBlazor.getTerminalById(id); return false; }
                catch (error) { return error.message === `Terminal with ID "${id}" not found.`; }
            }
            """);
        Assert.True(disposed);
        await Assertions.Expect(page.Locator("#asset-terminal .xterm-helper-textarea")).ToHaveCountAsync(0);
        Assert.Empty(errors);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "xterm-assets.json"), JsonSerializer.Serialize(new
        {
            scope = "Shipped XtermBlazor assets and fit addon; no managed terminal, gateway, native PTY or auth transport exercised",
            xtermAssemblyVersion = typeof(XtermBlazor.Xterm).Assembly.GetName().Version?.ToString(),
            browser = browserFixture.Browser.Version, assets, keyboardData = "probe\r", renderedOutput = "OUTPUT: ✓", dimensions, disposed, callbacks
        }, new JsonSerializerOptions { WriteIndented = true }));
    }
}
