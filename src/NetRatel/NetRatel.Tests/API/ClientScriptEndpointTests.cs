using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Models;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Artifacts;
using NetRatel.Infrastructure.Artifacts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientScriptEndpointTests
{
    [Fact]
    public async Task PostClientScript_ReturnsScript_AndCreatesEnrollmentCode()
    {
        var app = await BuildAppAsync();
        using (app)
        {
            var client = app.GetTestClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

            var response = await client.PostAsJsonAsync("/api/v1/client/script", new
            {
                tenantId = 4098,
                runtimeId = "win-x64",
                validForMinutes = 60,
                maxUses = 1,
                installAsService = true,
                silentInstall = true
            });

            response.IsSuccessStatusCode.Should().BeTrue();
            response.Content.Headers.ContentType!.MediaType.Should().Be("text/plain");
            var script = await response.Content.ReadAsStringAsync();
            script.Should().Contain("ENR-");
            script.Should().Contain("netratel.enroll.json");
            script.Should().Contain("NetRatel.Update");
            script.Should().Contain("win-x64");
            script.Should().Contain("/api/v1/client-artifacts/");
            script.Should().Contain("0.4.10");
            script.Should().Contain("abc123");
            script.Should().Contain("/onboarding-download");
            script.Should().Contain("X-NetRatel-Enrollment-Code");
            script.Should().NotContain("/latest");
            script.Should().NotContain("/api/v1/client/download");
            script.Should().NotContain("eyJ"); // heuristic JWT prefix

            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var row = await db.EnrollmentCodes.SingleAsync();
            row.TenantId.Should().Be(4098);
            row.Uses.Should().Be(0);
        }
    }

    [Fact]
    public async Task PostClientScript_WithArtifactVersion_UsesTheRequestedImmutableArtifact()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.PostAsJsonAsync("/api/v1/client/script", new
        {
            tenantId = 4098,
            runtimeId = "linux-x64",
            artifactVersion = "0.4.131-rc.1",
            validForMinutes = 60,
            maxUses = 1
        });

        response.IsSuccessStatusCode.Should().BeTrue();
        var script = await response.Content.ReadAsStringAsync();
        script.Should().Contain("VERSION=\"0.4.131-rc.1\"");
        script.Should().Contain("EXPECTED_SHA=\"abc123\"");
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var dbName = Guid.NewGuid().ToString("N");
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("ClientArtifactsWrite", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ClientArtifactsDownload", policy => policy.RequireAuthenticatedUser());
                });

                services.AddDbContext<OrchestratorDbContext>(opts => opts.UseInMemoryDatabase(dbName));
                services.AddScoped<IEnrollmentCodeIssueService, EnrollmentCodeIssueService>();
                services.AddScoped<IScriptTemplateService, ScriptTemplateService>();
                services.AddScoped<IClientScriptService, ClientScriptService>();
                services.AddScoped<ITenantLookupService, AlwaysTenantLookupService>();
                services.AddScoped<IClientArtifactsService, NoopArtifactsService>();
                services.Configure<AgentAuthOptions>(o => o.Issuer = "https://netratel.example.invalid");
                services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AgentAuth:Issuer"] = "https://netratel.example.invalid"
                }).Build());
            });

            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapClientArtifactsEndpoints());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class AlwaysTenantLookupService : ITenantLookupService
    {
        public Task<bool> TenantExistsAsync(int tenantId, CancellationToken ct = default) =>
            Task.FromResult(tenantId > 0);
    }

    private sealed class NoopArtifactsService : IClientArtifactsService
    {
        public Task DeleteAsync(string rid, string version, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactDownloadResult> DownloadAsync(string rid, string versionOrLatest, bool allowFallback, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactDownloadResult> DownloadRawAsync(string rid, string versionOrLatest, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactDownloadResult> DownloadForClientAsync(NetRatel.API.Models.ClientDownloadRequest request, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactSummaryDto?> GetLatestAsync(string rid, CancellationToken ct) =>
            Task.FromResult<ClientArtifactSummaryDto?>(new()
            {
                Rid = rid,
                Version = "0.4.10",
                FileName = "NetRatel.Client-win-x64-0.4.10.zip",
                Size = 123,
                Sha256 = "abc123",
                UploadedAt = DateTimeOffset.UtcNow,
                Notes = null
            });
        public Task<ClientArtifactSummaryDto?> GetMetadataAsync(string rid, string version, CancellationToken ct) =>
            Task.FromResult<ClientArtifactSummaryDto?>(new()
            {
                Rid = rid,
                Version = version,
                FileName = $"NetRatel.Client-{rid}-{version}.zip",
                Size = 123,
                Sha256 = "abc123",
                UploadedAt = DateTimeOffset.UtcNow,
                Notes = null
            });
        public Task<ClientArtifactListDto> ListAsync(string? rid, int skip, int take, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactDownloadResult> RunFallbackScanAsync(string rid, string? version, CancellationToken ct) => throw new NotImplementedException();
        public Task<ClientArtifactUploadResultDto> UploadAsync(Microsoft.AspNetCore.Http.IFormFile file, string rid, string version, string? notes, string? uploadedBy, CancellationToken ct) => throw new NotImplementedException();
    }

    private sealed class TestAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, "test-admin") }, Scheme.Name);
            var principal = new ClaimsPrincipal(identity);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
        }
    }
}
