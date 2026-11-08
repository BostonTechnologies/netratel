using System.Net;
using System.Text;
using Bunit;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Web.Components.Dialogs.FileBrowser;
using NetRatel.Web.Services.FileSystem;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class FileBrowserContentClassifierTests : AsyncBunitContext
{
    [Theory]
    [InlineData("document.html")]
    [InlineData("drawing.svg")]
    [InlineData("message.eml")]
    [InlineData("readme.md")]
    [InlineData("change.patch")]
    public void GovernedMarkupAndUnknownTextRemainInertSource(string name)
    {
        const string active = "<script>fetch('https://preview-leak.invalid')</script><img src='https://preview-leak.invalid/pixel'>";
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(active));
        var result = RemotePreviewPolicy.Inspect(name, source, CancellationToken.None);
        result.Kind.Should().Be("text");
        result.Text.Should().Be(active);
    }

    [Fact]
    public void GovernedArchiveRefusesExpansionAndDtdBeforeRendering()
    {
        using var source = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var output = archive.CreateEntry("word/document.xml").Open();
            using var writer = new StreamWriter(output);
            writer.Write("<!DOCTYPE x [<!ENTITY e SYSTEM 'https://preview-leak.invalid'>]><x>&e;</x>");
        }
        source.Position = 0;
        var inspect = () => RemotePreviewPolicy.Inspect("document.docx", source, CancellationToken.None);
        inspect.Should().Throw<System.Xml.XmlException>();
    }

    [Fact]
    public void GovernedRasterRejectsHugeDecodedDimensionsDespiteTinyFile()
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }.CopyTo(bytes, 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16), 100_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(20), 100_000);
        using var stream = new MemoryStream(bytes);
        var inspect = () => RemotePreviewPolicy.Inspect("image.png", stream, CancellationToken.None);
        inspect.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void GovernedCsvHonorsQuotedFieldsAndGpxNeverNeedsMapTiles()
    {
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("name,value\n\"a,b\",42"));
        RemotePreviewPolicy.Inspect("table.csv", csv, CancellationToken.None).Rows![1].Should().Equal("a,b", "42");
        using var gpx = new MemoryStream(Encoding.UTF8.GetBytes("<gpx><trk><trkseg><trkpt lat='12.5' lon='25.25'/></trkseg></trk></gpx>"));
        var map = RemotePreviewPolicy.Inspect("route.gpx", gpx, CancellationToken.None);
        map.Kind.Should().Be("map");
        map.Text.Should().Be("205.25000,77.50000");
    }

    [Theory]
    [InlineData("document.docx", "word/document.xml", "docx")]
    [InlineData("slides.pptx", "ppt/slides/slide1.xml", "text")]
    [InlineData("book.epub", "chapter.xhtml", "text")]
    [InlineData("document.odt", "content.xml", "text")]
    public void LocalDocumentVariantsDecodeTextWithoutExternalResolution(string name, string part, string kind)
    {
        using var source = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var output = archive.CreateEntry(part).Open();
            using var writer = new StreamWriter(output);
            writer.Write("<document><p>Synthetic document content</p></document>");
        }
        var document = RemotePreviewPolicy.Inspect(name, source, CancellationToken.None);
        document.Kind.Should().Be(kind);
        document.Text.Should().Contain("Synthetic document content");
    }

    [Fact]
    public void ZipListsEntriesWithoutExtractingTraversalPathsAndRejectsExpansion()
    {
        using var source = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var output = archive.CreateEntry("../../outside.txt").Open();
            output.Write("inspection only"u8);
        }
        var directory = RemotePreviewPolicy.Inspect("archive.zip", source, CancellationToken.None);
        directory.Rows![0][0].Should().Be("../../outside.txt");
        using var expanded = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(expanded, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using var output = archive.CreateEntry("large.txt").Open();
            var buffer = new byte[64 * 1024];
            for (var i = 0; i <= RemotePreviewPolicy.MaximumExpandedBytes / buffer.Length; i++) output.Write(buffer);
        }
        var inspect = () => RemotePreviewPolicy.Inspect("archive.zip", expanded, CancellationToken.None);
        inspect.Should().Throw<InvalidDataException>().WithMessage("*expanded-size*");
    }

    [Fact]
    public void LocalCertificateAndDatabaseInspectionStayReadOnly()
    {
        using var key = System.Security.Cryptography.RSA.Create(2048);
        var request = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=synthetic.example", key,
            System.Security.Cryptography.HashAlgorithmName.SHA256, System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var encoded = new MemoryStream(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert));
        RemotePreviewPolicy.Inspect("public.cer", encoded, CancellationToken.None).Rows.Should().Contain(row => row[0] == "Subject" && row[1] == "CN=synthetic.example");
        var path = Path.Combine(Path.GetTempPath(), $"netratel-corpus-{Guid.NewGuid():N}.sqlite");
        try
        {
            using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
            {
                connection.Open();
                using var command = connection.CreateCommand();
                command.CommandText = "CREATE TABLE notes (message TEXT); INSERT INTO notes VALUES ('Synthetic database content');";
                command.ExecuteNonQuery();
            }
            using var source = new MemoryStream(File.ReadAllBytes(path));
            var before = System.Security.Cryptography.SHA256.HashData(source.ToArray());
            var document = RemotePreviewPolicy.Inspect("notes.sqlite", source, CancellationToken.None);
            document.Rows.Should().Contain(row => row.Contains("Synthetic database content"));
            System.Security.Cryptography.SHA256.HashData(source.ToArray()).Should().Equal(before);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void XlsxUsesStoredValuesWithoutEvaluatingFormulas()
    {
        using var source = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(source, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            Add("[Content_Types].xml", """
                <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>
                """);
            Add("_rels/.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>
                """);
            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Synthetic" sheetId="1" r:id="rId1"/></sheets></workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1"><c r="A1" t="inlineStr"><is><t>Synthetic spreadsheet</t></is></c><c r="B1"><f>1+1</f><v>42</v></c></row></sheetData></worksheet>
                """);
            void Add(string name, string text) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(text); }
        }
        var document = RemotePreviewPolicy.Inspect("stored.xlsx", source, CancellationToken.None);
        document.Rows![0].Should().Equal("Synthetic spreadsheet", "42");
    }

    [Theory]
    [InlineData("trace.har", "{\"log\":{\"entries\":[{\"request\":{\"method\":\"GET\",\"url\":\"https://preview-leak.invalid/\"},\"response\":{\"status\":200}}]}}", "table")]
    [InlineData("mesh.obj", "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3", "mesh")]
    [InlineData("contact.vcf", "BEGIN:VCARD\nFN:Synthetic Contact\nEND:VCARD", "table")]
    [InlineData("event.ics", "BEGIN:VCALENDAR\nSUMMARY:Synthetic Event\nEND:VCALENDAR", "table")]
    public void RemainingLocalFamiliesAreInspectedAsData(string name, string content, string kind)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        RemotePreviewPolicy.Inspect(name, source, CancellationToken.None).Kind.Should().Be(kind);
    }

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
