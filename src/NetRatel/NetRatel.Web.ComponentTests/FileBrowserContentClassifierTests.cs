using System.Net;
using System.Text;
using Bunit;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Web.Components.Dialogs.FileBrowser;
using NetRatel.Web.Services.FileSystem;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FileBrowserContentClassifierTests : AsyncBunitContext
{
    private static readonly Guid AgentId = Guid.Parse("3c01f7c1-0e53-4fe5-9e95-ffbc8dd4fd1d");

    public FileBrowserContentClassifierTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddMudServices(options => options.PopoverOptions.CheckForPopoverProvider = false);
        Services.AddSingleton<IHttpClientFactory, FileViewerHttpClientFactory>();
        Services.AddScoped<FileSystemApiService>();
        Services.AddScoped<GatewayFileSystemApiService>();
        Services.AddScoped<FileBrowserContentClassifier>();
        Services.AddOptions<FileBrowserOptions>();
    }

    [Theory]
    [InlineData("settings.json", "application/json", "{\"enabled\":true}", true, "json")]
    [InlineData("Dockerfile", "text/plain", "FROM alpine", true, "dockerfile")]
    [InlineData("archive.zip", "application/octet-stream", "PK\u0003\u0004", false, "plaintext")]
    public void Classifies_RemoteContent_Using_Name_And_BoundedProbe(string name, string contentType, string content, bool isText, string language)
    {
        var result = new FileBrowserContentClassifier().Classify(name, contentType, Encoding.UTF8.GetBytes(content));

        result.IsText.Should().Be(isText);
        result.Language.Should().Be(language);
    }

    [Theory]
    [InlineData("photo.png", "image/png")]
    [InlineData("photo.JPG", "image/jpeg")]
    public void Classifies_SafeImageNames_ForNativeStreamingPreview(string name, string expectedContentType)
    {
        var result = new FileBrowserContentClassifier().Classify(name, "application/octet-stream", []);

        result.IsText.Should().BeFalse();
        result.ViewerKind.Should().Be("image");
        result.ContentType.Should().Be(expectedContentType);
    }

    [Theory]
    [InlineData("notes.txt", "plaintext")]
    [InlineData("service.log", "plaintext")]
    [InlineData("readme.md", "markdown")]
    [InlineData("settings.jsonc", "json")]
    [InlineData("settings.xml", "xml")]
    [InlineData("settings.yaml", "yaml")]
    [InlineData("settings.toml", "ini")]
    [InlineData("settings.ini", "plaintext")]
    [InlineData("app.config", "xml")]
    [InlineData("worker.service", "ini")]
    [InlineData("worker.timer", "ini")]
    [InlineData("setup.sh", "shell")]
    [InlineData("setup.ps1", "powershell")]
    [InlineData("setup.cmd", "bat")]
    [InlineData("app.cs", "csharp")]
    [InlineData("app.razor", "html")]
    [InlineData("app.css", "css")]
    [InlineData("app.js", "javascript")]
    [InlineData("app.ts", "typescript")]
    [InlineData("query.sql", "sql")]
    [InlineData("script.py", "python")]
    [InlineData("Makefile", "makefile")]
    [InlineData("hosts", "plaintext")]
    [InlineData(".gitignore", "plaintext")]
    public void Maps_OperationalTextNames_To_MonacoLanguages(string name, string language)
        => FileBrowserContentClassifier.ResolveLanguage(name).Should().Be(language);

    [Fact]
    public async Task Does_Not_Read_A_KnownOversizedFile_Into_TheViewer()
    {
        var factory = Services.GetRequiredService<IHttpClientFactory>();
        var dialogProvider = Render<MudBlazor.MudDialogProvider>();
        await Services.GetRequiredService<MudBlazor.IDialogService>()
            .ShowAsync<ClientFileViewer>("large.log", new MudBlazor.DialogParameters<ClientFileViewer>
            {
                { viewer => viewer.File, new FileSystemEntryDto("/var/log", "large.log", "/var/log/large.log", false, 8 * 1024 * 1024 + 1) },
                { viewer => viewer.TenantId, 3 },
                { viewer => viewer.AgentId, AgentId }
            });

        dialogProvider.WaitForAssertion(() =>
        {
            dialogProvider.FindAll("[data-testid='client-file-viewer-too-large']").Should().ContainSingle();
            ((FileViewerHttpClientFactory)factory).RequestCount.Should().Be(0);
        });
    }

    private sealed class FileViewerHttpClientFactory : IHttpClientFactory
    {
        public int RequestCount { get; private set; }

        public HttpClient CreateClient(string name) => new(new Handler(this)) { BaseAddress = new Uri("https://netratel.test") };

        private sealed class Handler(FileViewerHttpClientFactory owner) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                owner.RequestCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
            }
        }
    }
}
