using System.Net;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Application.Events;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientDownloadEndpointTests
{
    [Fact]
    public async Task ConcurrentUploadConflictDoesNotDeleteTheWinningArtifact()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        var storageRoot = Path.Combine(root, "store");
        Directory.CreateDirectory(root);
        try
        {
            using var app = await BuildAppAsync(storageRoot);
            await using var scope = app.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IClientArtifactsService>();
            var bytes = BuildBaseZip();
            var winner = new CallbackFormFile(bytes);
            var loser = new CallbackFormFile(bytes, async () =>
                await service.UploadAsync(winner, "win-x64", "0.4.6", "winner", "test", CancellationToken.None));

            await FluentActions.Invoking(() => service.UploadAsync(loser, "win-x64", "0.4.6", "loser", "test", CancellationToken.None))
                .Should().ThrowAsync<ClientArtifactConflictException>();

            var metadata = await service.GetMetadataAsync("win-x64", "0.4.6", CancellationToken.None);
            metadata.Should().NotBeNull();
            metadata!.Notes.Should().Be("winner");
            File.Exists(Path.Combine(storageRoot, "win-x64", "0.4.6", "metadata.json")).Should().BeTrue();
            var download = await service.DownloadRawAsync("win-x64", "0.4.6", CancellationToken.None);
            await using var stream = download.Content;
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.ToArray().Should().Equal(bytes);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task PostClientArtifactUpload_StoresVersionedMetadata_AndAllowsIdenticalRepair()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storageRoot = Path.Combine(root, "store");

        using var app = await BuildAppAsync(storageRoot);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var archive = BuildBaseZip();
        using var first = BuildUploadContent("win-x64", "0.4.6", "Uploaded by test", archive);
        var response = await client.PostAsync("/api/v1/client-artifacts/upload", first);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var metadataPath = Path.Combine(storageRoot, "win-x64", "0.4.6", "metadata.json");
        File.Exists(metadataPath).Should().BeTrue();

        var metadataJson = await File.ReadAllTextAsync(metadataPath);
        using var metadata = JsonDocument.Parse(metadataJson);
        metadata.RootElement.GetProperty("rid").GetString().Should().Be("win-x64");
        metadata.RootElement.GetProperty("version").GetString().Should().Be("0.4.6");
        metadata.RootElement.GetProperty("notes").GetString().Should().Be("Uploaded by test");

        using var duplicate = BuildUploadContent("win-x64", "0.4.6", "Duplicate", archive);
        var duplicateResponse = await client.PostAsync("/api/v1/client-artifacts/upload", duplicate);

        duplicateResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var duplicateResult = await duplicateResponse.Content.ReadFromJsonAsync<ClientArtifactUploadResultDto>();
        duplicateResult!.Created.Should().BeFalse();
    }

    [Fact]
    public async Task PostClientDownload_WithInjection_ReturnsZipWithEnrollmentFile_AndPersistsCode()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storageRoot = Path.Combine(root, "store");
        var rid = "win-x64";
        var version = "0.0.1";
        var versionDir = Path.Combine(storageRoot, rid, version);
        Directory.CreateDirectory(versionDir);

        await File.WriteAllBytesAsync(Path.Combine(versionDir, "NetRatel.Client-win-x64-0.0.1.zip"), BuildBaseZip());
        var metadata = new
        {
            rid,
            version,
            fileName = "NetRatel.Client-win-x64-0.0.1.zip",
            size = 123,
            sha256 = "abc",
            uploadedAt = DateTimeOffset.UtcNow,
            uploadedBy = "test",
            notes = "n",
            contentType = "application/zip"
        };
        await File.WriteAllTextAsync(Path.Combine(versionDir, "metadata.json"), JsonSerializer.Serialize(metadata), Encoding.UTF8);

        using var app = await BuildAppAsync(storageRoot);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.PostAsJsonAsync("/api/v1/client/download", new
        {
            tenantId = 4098,
            environment = 0,
            runtimeId = rid,
            version = "latest",
            injectEnrollment = true,
            validForMinutes = 60,
            maxUses = 1
        });

        response.IsSuccessStatusCode.Should().BeTrue();
        response.Content.Headers.ContentDisposition.Should().NotBeNull();
        response.Content.Headers.ContentDisposition!.FileName.Should().Contain("netratel-client-4098-win-x64");

        var zipBytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.GetEntry("netratel.enroll.json").Should().NotBeNull();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var codes = await db.EnrollmentCodes.ToListAsync();
        codes.Should().ContainSingle();
        codes[0].Uses.Should().Be(0);
    }

    [Fact]
    public async Task GetRawDownload_RequiresAuthentication()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storageRoot = Path.Combine(root, "store");

        using var app = await BuildAppAsync(storageRoot);
        var client = app.GetTestClient();

        var response = await client.GetAsync("/api/v1/client-artifacts/win-x64/latest/raw-download");

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRawDownload_WithAuth_ReturnsStoredZipWithoutTokenInjection()
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storageRoot = Path.Combine(root, "store");
        var rid = "win-x64";
        var version = "0.4.6";
        var versionDir = Path.Combine(storageRoot, rid, version);
        Directory.CreateDirectory(versionDir);

        await File.WriteAllBytesAsync(Path.Combine(versionDir, "NetRatel.Client-win-x64-0.4.6.zip"), BuildBaseZip());
        var metadata = new
        {
            rid,
            version,
            fileName = "NetRatel.Client-win-x64-0.4.6.zip",
            size = 123,
            sha256 = "abc",
            uploadedAt = DateTimeOffset.UtcNow,
            uploadedBy = "test",
            notes = "n",
            contentType = "application/zip"
        };
        await File.WriteAllTextAsync(Path.Combine(versionDir, "metadata.json"), JsonSerializer.Serialize(metadata), Encoding.UTF8);

        using var app = await BuildAppAsync(storageRoot);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync("/api/v1/client-artifacts/win-x64/latest/raw-download");

        response.IsSuccessStatusCode.Should().BeTrue();
        var zipBytes = await response.Content.ReadAsByteArrayAsync();
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        archive.GetEntry("NetRatel.Client.exe").Should().NotBeNull();
        archive.GetEntry("spacetime.token").Should().BeNull();
        archive.GetEntry("netratel.enroll.json").Should().BeNull();
    }

    [Theory]
    [InlineData("v1")]
    [InlineData("v2")]
    public async Task GetOnboardingDownload_WithEnrollmentCode_DoesNotConsumeCode(string apiVersion)
    {
        var root = Path.Combine(Path.GetTempPath(), "netratel-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var storageRoot = Path.Combine(root, "store");
        var rid = "win-x64";
        var version = "0.4.6";
        var versionDir = Path.Combine(storageRoot, rid, version);
        Directory.CreateDirectory(versionDir);

        await File.WriteAllBytesAsync(Path.Combine(versionDir, "NetRatel.Client-win-x64-0.4.6.zip"), BuildBaseZip());
        var metadata = new
        {
            rid,
            version,
            fileName = "NetRatel.Client-win-x64-0.4.6.zip",
            size = 123,
            sha256 = "abc",
            uploadedAt = DateTimeOffset.UtcNow,
            uploadedBy = "test",
            notes = "n",
            contentType = "application/zip"
        };
        await File.WriteAllTextAsync(Path.Combine(versionDir, "metadata.json"), JsonSerializer.Serialize(metadata), Encoding.UTF8);

        using var app = await BuildAppAsync(storageRoot);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.EnrollmentCodes.Add(new EnrollmentCode
            {
                Id = Guid.NewGuid(),
                TenantId = 4098,
                Code = "ENR-TEST1",
                ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
                ValidToUtc = DateTimeOffset.UtcNow.AddMinutes(30),
                Uses = 0,
                MaxUses = 1,
                CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/{apiVersion}/client-artifacts/win-x64/0.4.6/onboarding-download");
        request.Headers.Add("X-NetRatel-Tenant-Id", "4098");
        request.Headers.Add("X-NetRatel-Enrollment-Code", "ENR-TEST1");
        var response = await client.SendAsync(request);

        response.IsSuccessStatusCode.Should().BeTrue();
        await using var verifyScope = app.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var code = await verifyDb.EnrollmentCodes.SingleAsync();
        code.Uses.Should().Be(0);
        var path = $"/api/{apiVersion}/client-artifacts/win-x64/0.4.6/onboarding-download";
        (await client.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var wrongTenant = new HttpRequestMessage(HttpMethod.Get, path);
        wrongTenant.Headers.Add("X-NetRatel-Tenant-Id", "4099");
        wrongTenant.Headers.Add("X-NetRatel-Enrollment-Code", "ENR-TEST1");
        (await client.SendAsync(wrongTenant)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        code.ValidToUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await verifyDb.SaveChangesAsync();
        using var expired = new HttpRequestMessage(HttpMethod.Get, path);
        expired.Headers.Add("X-NetRatel-Tenant-Id", "4098");
        expired.Headers.Add("X-NetRatel-Enrollment-Code", "ENR-TEST1");
        (await client.SendAsync(expired)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        code.ValidToUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        code.RevokedAtUtc = DateTimeOffset.UtcNow;
        await verifyDb.SaveChangesAsync();
        using var revoked = new HttpRequestMessage(HttpMethod.Get, path);
        revoked.Headers.Add("X-NetRatel-Tenant-Id", "4098");
        revoked.Headers.Add("X-NetRatel-Enrollment-Code", "ENR-TEST1");
        (await client.SendAsync(revoked)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<IHost> BuildAppAsync(string storageRoot)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            var dbName = Guid.NewGuid().ToString("N");
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("ClientArtifactsWrite", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ClientArtifactsUpload", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ClientArtifactsDownload", policy => policy.RequireAuthenticatedUser());
                });

                services.AddDbContext<OrchestratorDbContext>(opts => opts.UseInMemoryDatabase(dbName));
                services.AddScoped<IEventRecorder, NoopEventRecorder>();
                services.AddScoped<ICorrelationContext, TestCorrelationContext>();
                services.AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>();
                services.AddScoped<IArtifactZipInjectionService, ZipInjectionService>();
                services.AddScoped<ITenantLookupService, AlwaysTenantLookupService>();
                services.Configure<ClientArtifactsOptions>(opts =>
                {
                    opts.StorageRoot = storageRoot;
                    opts.LegacyRoot = Path.Combine(storageRoot, "legacy");
                    opts.EnableFallbackScan = false;
                });
                services.Configure<AgentAuthOptions>(opts => opts.Issuer = "https://netratel.example.invalid");
                services.AddScoped<IClientArtifactsService, ClientArtifactsService>();
                services.AddScoped<IClientUpdatePublisher, TestClientUpdatePublisher>();
                services.AddScoped<IClientScriptService, NoopClientScriptService>();
            });

            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapClientArtifactsEndpoints());
            });
        });

        var host = await builder.StartAsync();
        return host;
    }

    private sealed class NoopEventRecorder : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class TestClientUpdatePublisher : IClientUpdatePublisher
    {
        public Task<ClientUpdateReleaseRecord> PublishArtifactAsync(ClientArtifactSummaryDto artifact, string manifestJson,
            string? publishedBy, CancellationToken cancellationToken) => Task.FromResult(new ClientUpdateReleaseRecord());
        public Task<bool> IsArtifactPublishedAsync(string runtimeId, string version, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class TestCorrelationContext : ICorrelationContext
    {
        public string? Current => "corr-test";

        public string GetOrCreate() => Current!;
    }

    private static byte[] BuildBaseZip()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("NetRatel.Client.exe");
            using (var writer = new StreamWriter(entry.Open(), Encoding.UTF8)) writer.Write("bin");
            var manifestEntry = archive.CreateEntry("netratel-client-manifest.json");
            using var manifest = new StreamWriter(manifestEntry.Open(), Encoding.UTF8);
            manifest.Write("{\"schema\":\"netratel.client.manifest.v1\",\"product\":\"NetRatel.Client\",\"version\":\"0.4.6\",\"runtimeId\":\"win-x64\",\"commitSha\":\"0123456789abcdef0123456789abcdef01234567\",\"executable\":\"NetRatel.Client.exe\"}");
        }

        return stream.ToArray();
    }

    private static MultipartFormDataContent BuildUploadContent(string rid, string version, string notes, byte[]? archive = null)
    {
        var content = new MultipartFormDataContent();
        content.Add(new StringContent(rid), "rid");
        content.Add(new StringContent(version), "version");
        content.Add(new StringContent(notes), "notes");

        var fileContent = new ByteArrayContent(archive ?? BuildBaseZip());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
        content.Add(fileContent, "file", $"NetRatel.Client-{rid}-{version}.zip");
        return content;
    }

    private sealed class AlwaysTenantLookupService : ITenantLookupService
    {
        public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId > 0);
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test-admin") }, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }

    private sealed class NoopClientScriptService : IClientScriptService
    {
        public Task<ClientScriptResult> GenerateAsync(NetRatel.API.Models.ClientScriptRequest request, CancellationToken ct)
            => Task.FromResult(new ClientScriptResult(Array.Empty<byte>(), "text/plain", "noop.ps1"));
    }

    private sealed class CallbackFormFile(byte[] bytes, Func<Task>? beforeCopy = null) : IFormFile
    {
        public string ContentType => "application/zip";
        public string ContentDisposition => "form-data; name=\"file\"; filename=\"client.zip\"";
        public IHeaderDictionary Headers { get; } = new HeaderDictionary();
        public long Length => bytes.Length;
        public string Name => "file";
        public string FileName => "client.zip";
        public Stream OpenReadStream() => new MemoryStream(bytes, writable: false);
        public void CopyTo(Stream target) => target.Write(bytes);
        public async Task CopyToAsync(Stream target, CancellationToken cancellationToken = default)
        {
            if (beforeCopy is not null) await beforeCopy();
            await target.WriteAsync(bytes, cancellationToken);
        }
    }
}
