using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NetRatel.API.Models;
using NetRatel.API.Endpoints;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Testcontainers.PostgreSql;
using System.Threading.RateLimiting;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientInstallLinkTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"client-install-link-{Guid.NewGuid():N}");
    private string Keys => Path.Combine(_root, "keys");

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(Keys);
        await _postgres.StartAsync();
        await using var services = BuildServices(Keys);
        await using var scope = services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.MigrateAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _postgres.DisposeAsync();
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task ProtectedGrantFetchDoesNotSpendUsesAndIdempotencyReturnsTheSameCapability()
    {
        await using var services = BuildServices(Keys);
        await using var scope = services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var request = Request();

        var created = await links.CreateAsync(request, "fixture-admin", TestContext.Current.CancellationToken);
        var replay = await links.CreateAsync(request, "fixture-admin", TestContext.Current.CancellationToken);
        Assert.True(replay.Replay);
        Assert.Equal(created.PublicUrl, replay.PublicUrl);
        Assert.Equal(created.Script, replay.Script);
        Assert.Equal(1, await db.ClientInstallGrants.CountAsync());

        var grant = await db.ClientInstallGrants.AsNoTracking().Include(x => x.EnrollmentCode).SingleAsync();
        Assert.StartsWith("PROTECTED-", grant.EnrollmentCode.Code);
        Assert.DoesNotContain(grant.EnrollmentCode.Code, created.Script);
        Assert.DoesNotContain(created.Script, grant.ProtectedScript);
        Assert.DoesNotContain(created.PublicUrl.Split('/').Last().Split('.')[0], grant.ProtectedToken);
        var code = Regex.Match(created.Script, "ENR-[A-F0-9]{32}").Value;
        Assert.NotEmpty(code);
        Assert.NotEqual(code, grant.EnrollmentCode.Code);
        Assert.Equal(EnrollmentCodeLookup.Hash(code), grant.EnrollmentCode.CodeHash);

        var token = created.PublicUrl.Split('/').Last().Split('.')[0];
        Assert.Equal(created.Script, (await links.GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken))?.Script);
        Assert.Equal(created.Script, (await links.GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken))?.Script);
        Assert.Null(await links.GetPublicScriptAsync(token, "ps1", TestContext.Current.CancellationToken));
        Assert.Equal(0, (await db.EnrollmentCodes.AsNoTracking().SingleAsync()).Uses);

        var enrollmentCodes = new EnrollmentCodeIssueService(db);
        var validated = await enrollmentCodes.ValidateActiveCodeAsync(code, request.TenantId,
            TestContext.Current.CancellationToken);
        Assert.Equal(code, validated.Code);
        await Assert.ThrowsAsync<InvalidOperationException>(() => links.CreateAsync(
            request with { MaxUses = 3 }, "fixture-admin", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ConcurrentCreationWithOneRequestKeyReturnsOneGrant()
    {
        await using var services = BuildServices(Keys);
        var request = Request();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(async _ =>
        {
            await using var scope = services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>()
                .CreateAsync(request, "fixture-admin", TestContext.Current.CancellationToken);
        }));

        Assert.Single(results.Select(x => x.Id).Distinct());
        Assert.Single(results.Select(x => x.PublicUrl).Distinct());
        Assert.Single(results.Select(x => x.Script).Distinct());
        await using var inspect = services.CreateAsyncScope();
        var db = inspect.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(1, await db.EnrollmentCodes.CountAsync());
        Assert.Equal(1, await db.ClientInstallGrants.CountAsync());
    }

    [Fact]
    public async Task ExpiryAndRevocationStopFetchAndRedeemingAnAlreadyFetchedScript()
    {
        await using var services = BuildServices(Keys);
        await using var scope = services.CreateAsyncScope();
        var links = scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var created = await links.CreateAsync(Request(), "fixture-admin", TestContext.Current.CancellationToken);
        var token = created.PublicUrl.Split('/').Last().Split('.')[0];
        var code = Regex.Match(created.Script, "ENR-[A-F0-9]{32}").Value;
        Assert.NotNull(await links.GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken));

        var grant = await db.ClientInstallGrants.Include(x => x.EnrollmentCode).SingleAsync();
        grant.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        Assert.Null(await links.GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken));
        grant.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();

        Assert.True(await links.RevokeAsync(created.Id, "fixture-admin", TestContext.Current.CancellationToken));
        Assert.Null(await links.GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<AgentAuthException>(() => new EnrollmentCodeIssueService(db)
            .ValidateActiveCodeAsync(code, created.TenantId, TestContext.Current.CancellationToken));
        Assert.False((await links.GetAsync(created.Id, TestContext.Current.CancellationToken))!.IsActive);
    }

    [Fact]
    public async Task MissingProtectionKeyFailsClosedAndBadPublicOriginsIssueNoEnrollmentCode()
    {
        await using (var services = BuildServices(Keys))
        {
            await using var scope = services.CreateAsyncScope();
            var links = scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>();
            var created = await links.CreateAsync(Request(), "fixture-admin", TestContext.Current.CancellationToken);
            var token = created.PublicUrl.Split('/').Last().Split('.')[0];
            var otherKeys = Path.Combine(_root, "other-keys");
            Directory.CreateDirectory(otherKeys);
            await using var wrongServices = BuildServices(otherKeys);
            await using var wrongScope = wrongServices.CreateAsyncScope();
            await Assert.ThrowsAsync<CryptographicException>(() => wrongScope.ServiceProvider
                .GetRequiredService<ClientInstallLinkService>()
                .GetPublicScriptAsync(token, "sh", TestContext.Current.CancellationToken));
        }

        await using var badServices = BuildServices(Keys, "http://localhost:5000");
        await using var badScope = badServices.CreateAsyncScope();
        await Assert.ThrowsAsync<InvalidOperationException>(() => badScope.ServiceProvider
            .GetRequiredService<ClientInstallLinkService>()
            .CreateAsync(Request(), "fixture-admin", TestContext.Current.CancellationToken));
        Assert.Equal(1, await badScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .EnrollmentCodes.CountAsync());
    }

    [Fact]
    public async Task AnonymousGetAndHeadReturnRawScriptWithoutSpendingEnrollmentUses()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicUrls:WebBaseUrl"] = "https://netratel.example",
            ["PublicUrls:ApiBaseUrl"] = "https://netratel.example",
            ["DataProtection:KeysDirectory"] = Keys
        });
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
        builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
        builder.Services.AddSingleton<ITenantLookupService>(new FixtureTenantLookup());
        builder.Services.AddSingleton<IClientArtifactsService>(new FixtureArtifacts(MakeArchive()));
        builder.Services.AddSingleton<IScriptTemplateService, ScriptTemplateService>();
        builder.Services.AddScoped<ClientInstallLinkService>();
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Keys))
            .SetApplicationName("NetRatel-Link-Test");
        builder.Services.AddAuthorization(options => options.AddPolicy("ClientArtifactsWrite",
            policy => policy.RequireAuthenticatedUser()));
        builder.Services.AddRateLimiter(options => options.AddPolicy("public-client-install", _ =>
            RateLimitPartition.GetFixedWindowLimiter("fixture", _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            })));
        await using var app = builder.Build();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthorization();
        app.MapClientInstallLinkEndpoints();
        await app.StartAsync();

        await using var scope = app.Services.CreateAsyncScope();
        var created = await scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>()
            .CreateAsync(Request(), "fixture-admin", TestContext.Current.CancellationToken);
        var path = new Uri(created.PublicUrl).PathAndQuery;
        var client = app.GetTestClient();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("text/x-shellscript", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store, max-age=0", response.Headers.CacheControl?.ToString());
        Assert.Equal(created.Script, await response.Content.ReadAsStringAsync());
        Assert.StartsWith("#!/usr/bin/env bash", created.Script);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.OK, head.StatusCode);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path + "?tenantId=999")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path[..^2] + "ps1")).StatusCode);
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, (await db.EnrollmentCodes.AsNoTracking().SingleAsync()).Uses);

        await scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>()
            .RevokeAsync(created.Id, "fixture-admin", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task ConcurrentEnrollmentCannotExceedTheGrantUseLimit()
    {
        await using var services = BuildServices(Keys);
        await using (var scope = services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.Add(new Tenant { Id = 21, Name = "Fixture", CreatedAtUtc = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        ClientInstallLinkResult created;
        await using (var scope = services.CreateAsyncScope())
        {
            created = await scope.ServiceProvider.GetRequiredService<ClientInstallLinkService>()
                .CreateAsync(Request() with { MaxUses = 1 }, "fixture-admin", TestContext.Current.CancellationToken);
        }
        var code = Regex.Match(created.Script, "ENR-[A-F0-9]{32}").Value;
        var attempts = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
            await using var scope = services.CreateAsyncScope();
            try
            {
                await scope.ServiceProvider.GetRequiredService<EnrollmentService>()
                    .EnrollAsync(new AgentEnrollRequest(code, publicKey), TestContext.Current.CancellationToken);
                return true;
            }
            catch (AgentAuthException exception) when (exception.Message.Contains("maximum uses", StringComparison.Ordinal))
            {
                return false;
            }
        });
        var outcomes = await Task.WhenAll(attempts);
        Assert.Single(outcomes.Where(x => x));
        await using var finalScope = services.CreateAsyncScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(1, (await finalDb.EnrollmentCodes.AsNoTracking().SingleAsync()).Uses);
        Assert.Equal(1, await finalDb.Agents.CountAsync());
    }

    private ServiceProvider BuildServices(string keys, string publicWebBase = "https://netratel.example")
    {
        var archive = MakeArchive();
        var artifacts = new FixtureArtifacts(archive);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PublicUrls:WebBaseUrl"] = publicWebBase,
            ["PublicUrls:ApiBaseUrl"] = "https://netratel.example",
            ["DataProtection:KeysDirectory"] = keys
        }).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddOptions()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()))
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<TimeProvider>(TimeProvider.System)
            .AddSingleton<ITenantLookupService>(new FixtureTenantLookup())
            .AddSingleton<IClientArtifactsService>(artifacts)
            .AddSingleton<IScriptTemplateService, ScriptTemplateService>()
            .AddScoped<IPrimaryClientAgentBindingService, PrimaryClientAgentBindingService>()
            .AddScoped<EnrollmentService>()
            .AddScoped<ClientInstallLinkService>();
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keys))
            .SetApplicationName("NetRatel-Link-Test");
        return services.BuildServiceProvider();
    }

    private static ClientInstallLinkCreateRequest Request() =>
        new(21, "linux-x64", "1.2.3", 60, 2, false, true, Guid.NewGuid().ToString("D"));

    private static byte[] MakeArchive()
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = zip.CreateEntry("netratel-client-manifest.json");
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write("{\"schema\":\"netratel.client.manifest.v1\",\"product\":\"NetRatel.Client\",\"version\":\"1.2.3\",\"runtimeId\":\"linux-x64\",\"commitSha\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"executable\":\"NetRatel.Client\"}");
            var executable = zip.CreateEntry("NetRatel.Client");
            using var content = new StreamWriter(executable.Open());
            content.Write("fixture executable");
        }
        return output.ToArray();
    }

    private sealed class FixtureTenantLookup : ITenantLookupService
    {
        public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId == 21);
    }

    private sealed class FixtureArtifacts(byte[] archive) : IClientArtifactsService
    {
        private readonly ClientArtifactSummaryDto _summary = new()
        {
            Rid = "linux-x64", Version = "1.2.3", FileName = "fixture.zip",
            Size = archive.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()
        };
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) =>
            Task.FromResult<ClientArtifactSummaryDto?>(rid == _summary.Rid ? _summary : null);
        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct) =>
            Task.FromResult<ClientArtifactSummaryDto?>(rid == _summary.Rid && version == _summary.Version ? _summary : null);
        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct) =>
            Task.FromResult(new ClientArtifactDownloadResult(new MemoryStream(archive, writable: false),
                "application/zip", _summary.FileName, false, _summary));
        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(ClientDownloadRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotSupportedException();
    }
}
