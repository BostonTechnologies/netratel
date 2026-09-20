using System.Net;
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
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientUpdateHistoryEndpointPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async ValueTask InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public ValueTask DisposeAsync() => _postgres.DisposeAsync();

    [Fact]
    public async Task History_UsesPostgresPaginationFiltersAndHostnamePresentation()
    {
        var releaseId = await SeedAsync();
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync(
            $"/api/v1/client-updates/history?page=0&pageSize=10&search=canary-host&status=RolledBack&releaseId={releaseId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<ClientUpdateHistoryPageDto>();
        page.Should().NotBeNull();
        page!.Total.Should().Be(1);
        page.Items.Should().ContainSingle();
        page.Items[0].ClientHostName.Should().Be("canary-host");
        page.Items[0].ClientDisplayName.Should().Be("Canary agent");
        page.Items[0].Status.Should().Be("RolledBack");
        page.Items[0].FailureCode.Should().Be("activation_timeout");
    }

    [Fact]
    public async Task History_RejectsUnboundedPageSizes()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync("/api/v1/client-updates/history?page=0&pageSize=501");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AgentStates_UseTheSameFriendlyHostnamePresentation()
    {
        await SeedAsync();
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync("/api/v1/client-updates/agent-states");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var states = await response.Content.ReadFromJsonAsync<List<AgentClientUpdateStateDto>>();
        var state = states.Should().ContainSingle().Subject;
        state.ClientHostName.Should().Be("canary-host");
        state.ClientDisplayName.Should().Be("Canary agent");
    }

    [Fact]
    public async Task ManagementPages_AreBoundedFilteredAndRetainFriendlyClientPresentation()
    {
        await SeedAsync();
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var releases = await client.GetFromJsonAsync<ClientUpdateReleasePageDto>(
            "/api/v1/client-updates/management/releases?page=0&pageSize=10&runtimeId=win-x64&enabled=true");
        releases.Should().NotBeNull();
        releases!.Total.Should().Be(1);
        releases.Items.Should().ContainSingle(release => release.Version == "0.4.102");

        var attempts = await client.GetFromJsonAsync<ClientUpdateHistoryPageDto>(
            "/api/v1/client-updates/history?page=0&pageSize=10&tenantId=87&version=0.4.102");
        attempts.Should().NotBeNull();
        attempts!.Items.Should().ContainSingle(attempt => attempt.ClientHostName == "canary-host");

        var suspended = await client.GetFromJsonAsync<AgentClientUpdateStatePageDto>(
            "/api/v1/client-updates/management/suspended?page=0&pageSize=10&tenantId=87&search=canary-host");
        suspended.Should().NotBeNull();
        suspended!.Total.Should().Be(1);
        suspended.Items.Should().ContainSingle(state => state.ClientHostName == "canary-host");

        var invalid = await client.GetAsync("/api/v1/client-updates/management/releases?page=0&pageSize=25");
        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private async Task<int> SeedAsync()
    {
        const int tenantId = 87;
        var agentId = Guid.NewGuid();
        await using var db = new OrchestratorDbContext(_options);
        var release = new ClientUpdateReleaseRecord
        {
            PublicId = Guid.NewGuid(),
            Revision = 1,
            RuntimeId = "win-x64",
            Version = "0.4.102",
            ArtifactKey = "win-x64/0.4.102.zip",
            Sha256 = new string('a', 64),
            SizeBytes = 42,
            ManifestJson = "{}",
            PublishedAtUtc = DateTimeOffset.UtcNow
        };
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Canary" });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = tenantId,
            Name = "Canary agent",
            DeviceInfoJson = """{"hostName":"canary-host"}"""
        });
        db.ClientUpdateReleases.Add(release);
        await db.SaveChangesAsync();
        db.ClientUpdateAttempts.Add(new ClientUpdateAttemptRecord
        {
            PublicId = Guid.NewGuid(),
            ReleaseId = release.Id,
            TenantId = tenantId,
            AgentId = agentId,
            FromVersion = "0.4.101",
            TargetVersion = release.Version,
            RuntimeId = release.RuntimeId,
            State = ClientUpdateAttemptState.RolledBack,
            AdmissionNonceHash = new string('b', 64),
            FailureCode = "activation_timeout",
            CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1),
            UpdatedAtUtc = DateTimeOffset.UtcNow
        });
        db.AgentClientUpdateStates.Add(new AgentClientUpdateStateRecord
        {
            AgentId = agentId,
            TenantId = tenantId,
            SuspendedAtUtc = DateTimeOffset.UtcNow,
            SuspensionReason = "operator-review",
            PolicyRevision = 1
        });
        await db.SaveChangesAsync();
        return release.Id;
    }

    private async Task<IHost> BuildAppAsync()
    {
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
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser());
                    options.AddPolicy("ArtifactPublisher", policy => policy.RequireAuthenticatedUser());
                });
                services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
                services.AddSingleton<IClientArtifactsService>(_ => throw new NotSupportedException());
                services.AddSingleton<IClientUpdatePublisher>(_ => throw new NotSupportedException());
                services.AddScoped<ClientUpdateAuthorityService>();
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapClientUpdatesEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey("Authorization"))
            {
                return Task.FromResult(AuthenticateResult.Fail("Missing authorization header."));
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-admin")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
