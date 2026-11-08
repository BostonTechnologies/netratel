using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Web.Components.Dialogs.FileBrowser;
using NetRatel.Web.Services.FileSystem;

namespace NetRatel.Web.PlaywrightTests;

[Collection(PlaywrightCollection.Name)]
[Trait("Category", "manual-integration")]
public sealed class FullExplorerAcceptanceTests(ClientsManagementBrowserFixture browser) : IClassFixture<ClientsManagementBrowserFixture>
{
    [Theory]
    [InlineData(1440, 1000)]
    [InlineData(390, 844)]
    public async Task CardChoiceUsesTheRemoteAdapterAndGovernedViewer(int width, int height)
    {
        ExplorerFixtureFactory? remote = null;
        // This is UI/Blazor lifecycle evidence. Native filesystem/range behavior
        // is separately exercised by the existing admitted-gateway test server.
        await using var fixture = await ClientsManagementFixtureHost.StartAsync(services =>
        {
            MudBlazor.Extensions.ServiceCollectionExtensions.AddMudExtensions(services, configuration => configuration.WithoutAutomaticCssLoading(), typeof(RemoteDataRenderer).Assembly);
            services.AddSingleton<IHttpClientFactory>(provider => remote = new ExplorerFixtureFactory(provider.GetRequiredService<ClientDirectoryFixtureData>()));
            services.AddScoped<GatewayFileSystemApiService>();
            services.AddScoped<FileSystemApiService>();
            services.AddScoped<ExplorerPreferenceService>();
            services.AddScoped<FileBrowserContentService>();
            services.AddScoped<FileBrowserCircuitLifetime>();
            services.AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler>(provider => provider.GetRequiredService<FileBrowserCircuitLifetime>());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<FileBrowserContentRegistry>();
            services.AddOptions<FileBrowserOptions>();
        });
        await using var context = await browser.Browser.NewContextAsync(new() { ViewportSize = new() { Width = width, Height = height } });
        await context.RouteAsync("https://**/*", route => route.AbortAsync());
        var page = await context.NewPageAsync();
        page.PageError += (_, error) => Console.WriteLine("Explorer browser error: " + error);
        page.Response += (_, response) => { if (response.Status >= 400) Console.WriteLine($"Explorer HTTP {response.Status}: {response.Url}"); };
        var externalContent = new List<string>();
        context.Request += (_, request) => { if (request.Url.StartsWith("http", StringComparison.Ordinal) && !request.Url.StartsWith(fixture.BaseAddress + "/", StringComparison.Ordinal)) externalContent.Add(request.Url); };
        await page.GotoAsync(fixture.BaseAddress + "/clients");
        await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
        await page.GetByRole(AriaRole.Button, new() { Name = "Choose file explorer", Exact = true }).First.ClickAsync();
        await page.GetByText("Full Explorer", new() { Exact = true }).ClickAsync();
        var workspace = page.GetByTestId("full-explorer");
        try { await workspace.GetByText("notes.txt", new() { Exact = true }).WaitForAsync(new() { Timeout = 60_000 }); }
        catch
        {
            Console.WriteLine("Explorer state: " + await workspace.InnerTextAsync());
            Console.WriteLine(string.Join("\n", fixture.StartupServerDiagnostics));
            var failureDirectory = Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");
            Directory.CreateDirectory(failureDirectory);
            await page.ScreenshotAsync(new() { Path = Path.Combine(failureDirectory, $"full-explorer-{width}-failure.png"), FullPage = true });
            await File.WriteAllTextAsync(Path.Combine(failureDirectory, $"full-explorer-{width}-failure.html"), await page.ContentAsync());
            throw;
        }
        if (width > 1000)
        {
            await page.GetByRole(AriaRole.Button, new() { Name = "Close Full Explorer", Exact = true }).ClickAsync();
            await page.ReloadAsync();
            await page.GetByTestId("clients-page-interactive").WaitForAsync(new() { State = WaitForSelectorState.Attached });
            await page.GetByRole(AriaRole.Button, new() { Name = "Open file browser", Exact = true }).First.ClickAsync();
            await workspace.GetByText("notes.txt", new() { Exact = true }).WaitForAsync();
        }
        await workspace.GetByText("notes.txt", new() { Exact = true }).ClickAsync();
        await workspace.GetByText("Synthetic remote text", new() { Exact = true }).First.WaitForAsync();
        Assert.Empty(externalContent);

        if (width > 1000)
        {
            var popup = await page.RunAndWaitForPopupAsync(() => page.GetByRole(AriaRole.Button, new() { Name = "Pop out / rejoin", Exact = true }).ClickAsync());
            await popup.GetByText("Synthetic remote text", new() { Exact = true }).WaitForAsync();
            // A real click in the adopted document must reach the original
            // Interactive Server circuit through the pre-start event bridge.
            await popup.GetByRole(AriaRole.Button, new() { Name = "Open fullscreen", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Return to Explorer", Exact = true }).WaitForAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Return to Explorer", Exact = true }).ClickAsync();
            await popup.WaitForFunctionAsync("document.activeElement?.textContent.includes('Open fullscreen')");
            await page.GetByRole(AriaRole.Button, new() { Name = "Pop out / rejoin", Exact = true }).ClickAsync();
            await workspace.GetByText("Synthetic remote text", new() { Exact = true }).First.WaitForAsync();
            Assert.True(popup.IsClosed);
            await page.GetByRole(AriaRole.Button, new() { Name = "Save layout", Exact = true }).ClickAsync();
            await page.GetByRole(AriaRole.Button, new() { Name = "Reset layout", Exact = true }).ClickAsync();
        }
        else
        {
            await workspace.GetByRole(AriaRole.Tab, new() { Name = "Preview", Exact = true }).ClickAsync();
            Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= innerWidth + 8"));
        }
        await workspace.GetByText("slow.txt", new() { Exact = true }).ClickAsync();
        try
        {
            await remote!.SlowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await workspace.GetByText("notes.txt", new() { Exact = true }).ClickAsync();
        }
        finally { remote!.ReleaseSlow.TrySetResult(); }
        await workspace.GetByText("Synthetic remote text", new() { Exact = true }).First.WaitForAsync();
        Assert.DoesNotContain("Stale selection must never appear", await workspace.InnerTextAsync());
        await workspace.GetByText("document.docx", new() { Exact = true }).ClickAsync();
        await page.FrameLocator("iframe[title='Isolated local DOCX preview']:visible").GetByText("Synthetic DOCX", new() { Exact = true }).WaitForAsync();
        await workspace.GetByText("document.pdf", new() { Exact = true }).ClickAsync();
        try { await workspace.Locator("span[role='status']:visible").Filter(new() { HasText = "Page 1 / 1 · 100%" }).First.WaitForAsync(); }
        catch { Console.WriteLine("PDF workspace state: " + await workspace.InnerTextAsync()); throw; }
        Assert.True(await workspace.Locator("canvas[aria-label='PDF page']").First.EvaluateAsync<bool>("canvas => canvas.width > 0 && canvas.height > 0"));
        await workspace.GetByRole(AriaRole.Button, new() { Name = "Zoom in", Exact = true }).First.ClickAsync();
        await workspace.Locator("span[role='status']:visible").Filter(new() { HasText = "Page 1 / 1 · 125%" }).First.WaitForAsync();
        await workspace.GetByText("notes.txt", new() { Exact = true }).ClickAsync();
        await workspace.GetByText("Synthetic remote text", new() { Exact = true }).First.WaitForAsync();
        Assert.Empty(externalContent);
        Assert.Equal(0, await workspace.Locator("canvas[aria-label='PDF page']").CountAsync());
        await workspace.GetByText("active.html", new() { Exact = true }).ClickAsync();
        await workspace.GetByText(ActiveMarkup, new() { Exact = true }).First.WaitForAsync();
        Assert.Empty(externalContent);
        var evidence = Path.Combine(AppContext.BaseDirectory, "TestResults", "playwright");
        Directory.CreateDirectory(evidence);
        await page.ScreenshotAsync(new() { Path = Path.Combine(evidence, $"full-explorer-{width}.png"), FullPage = true });
        Assert.False(await page.Locator("#blazor-error-ui").IsVisibleAsync());
        await page.GetByRole(AriaRole.Button, new() { Name = "Open Basic", Exact = true }).ClickAsync();
        await page.GetByText("File Browser · gateway-agent-01", new() { Exact = true }).WaitForAsync();
        Assert.DoesNotContain(fixture.StartupServerDiagnostics, message => message.Contains("Unhandled exception", StringComparison.Ordinal));
    }

    private const string ActiveMarkup = "<script>fetch('https://preview-leak.invalid/')</script><img src='https://preview-leak.invalid/pixel'>";

    private static byte[] Docx()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
        {
            Add("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/></Types>
                """);
            Add("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/></Relationships>
                """);
            Add("word/document.xml", """
                <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:body><w:p><w:r><w:t>Synthetic DOCX</w:t></w:r></w:p></w:body></w:document>
                """);
            void Add(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(text); }
        }
        return stream.ToArray();
    }

    private static byte[] Pdf()
    {
        const string text = "BT /F1 24 Tf 40 100 Td (Synthetic PDF) Tj ET";
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 300 200] /Resources << /Font << /F1 4 0 R >> >> /Contents 5 0 R >>",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>", $"<< /Length {text.Length} >>\nstream\n{text}\nendstream"];
        var pdf = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (var i = 0; i < objects.Length; i++) { offsets.Add(pdf.Length); pdf.Append($"{i + 1} 0 obj\n{objects[i]}\nendobj\n"); }
        var xref = pdf.Length;
        pdf.Append($"xref\n0 {objects.Length + 1}\n0000000000 65535 f \n");
        foreach (var offset in offsets) pdf.Append($"{offset:D10} 00000 n \n");
        pdf.Append($"trailer << /Size {objects.Length + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(pdf.ToString());
    }

    private sealed class ExplorerFixtureFactory(ClientDirectoryFixtureData directory) : IHttpClientFactory
    {
        public TaskCompletionSource SlowStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseSlow { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpClient CreateClient(string name) => new(new Handler(this, directory)) { BaseAddress = new("https://netratel.test") };
        private sealed class Handler(ExplorerFixtureFactory owner, ClientDirectoryFixtureData directory) : HttpMessageHandler
        {
            private static readonly DateTimeOffset Modified = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
            private static readonly Dictionary<string, byte[]> Corpus = new()
            {
                ["/notes.txt"] = Encoding.UTF8.GetBytes("Synthetic remote text"),
                ["/active.html"] = Encoding.UTF8.GetBytes(ActiveMarkup),
                ["/slow.txt"] = Encoding.UTF8.GetBytes("Stale selection must never appear"),
                ["/document.docx"] = Docx(), ["/document.pdf"] = Pdf()
            };
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var uri = request.RequestUri!;
                if (!uri.AbsolutePath.Contains("/filesystem", StringComparison.Ordinal)) return Task.FromResult(directory.Respond(request));
                var segments = uri.AbsolutePath.Split('/');
                var tenant = int.Parse(segments[4]);
                var agent = Guid.Parse(segments[5]);
                var query = QueryHelpers.ParseQuery(uri.Query);
                var path = query.TryGetValue("path", out var selectedPath) ? selectedPath.ToString() : "";
                var content = Corpus.GetValueOrDefault(path) ?? [];
                object body;
                if (uri.AbsolutePath.EndsWith("/roots", StringComparison.Ordinal))
                    body = new GatewayFileSystemListResponse(tenant, agent, "", "akka", [new("/", "/", true, 0)]);
                else if (uri.AbsolutePath.EndsWith("/stat", StringComparison.Ordinal))
                    body = new GatewayFileSystemStatResponse(tenant, agent, path, false, content.Length, Modified, Modified, "v1:fixture", false);
                else if (uri.AbsolutePath.EndsWith("/file", StringComparison.Ordinal))
                {
                    if (path == "/slow.txt") { owner.SlowStarted.TrySetResult(); return LateResponseAsync(); }
                    return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
                }
                else
                    body = new GatewayFileSystemListResponse(tenant, agent, path, "akka",
                        Corpus.Select(item => new GatewayFileSystemEntryDto(item.Key[1..], item.Key, false, item.Value.Length) { LastModifiedUtc = Modified }).ToArray())
                    {
                        CanCreateDirectory = true, CanDeleteDirectory = true, CanWriteFiles = true,
                        Capabilities = new() { Stat = true, CreateFile = true, MoveEntry = true, DeleteFile = true, DeleteRecursive = true, ConditionalUpload = true }
                    };
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
                async Task<HttpResponseMessage> LateResponseAsync()
                {
                    // Deliberately ignore cancellation to prove that a late
                    // physical completion cannot replace a newer selection.
                    await owner.ReleaseSlow.Task;
                    return new(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };
                }
            }
        }
    }
}
