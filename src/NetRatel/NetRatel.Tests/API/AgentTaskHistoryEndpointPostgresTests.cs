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
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTaskHistoryEndpointPostgresTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private DbContextOptions<OrchestratorDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(_postgres.GetConnectionString())
            .Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task History_ProjectsOnlySummaryFieldsAndKeepsPersistedResultsOutOfRows()
    {
        await SeedAsync();
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync("/api/v2/tasks/history?page=0&pageSize=10&search=task-host");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<TaskHistoryPageDto>();
        page.Should().NotBeNull();
        page!.Total.Should().Be(1);
        var item = page.Items.Should().ContainSingle().Subject;
        item.ClientHostName.Should().Be("task-host");
        item.StatusMessage.Should().Be("completed");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("result-secret");
    }

    private async Task SeedAsync()
    {
        const int tenantId = 91;
        await using var db = new OrchestratorDbContext(_options);
        var agentId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = tenantId, Name = "Task tenant" });
        db.Agents.Add(new Agent
        {
            Id = agentId,
            TenantId = tenantId,
            Name = "Task agent",
            DeviceInfoJson = """{"hostName":"task-host"}"""
        });
        db.JobTaskActivities.Add(new JobTaskActivityRecord
        {
            RequestId = "task-history-request",
            TenantId = tenantId,
            AgentId = agentId,
            ClientIdentity = "client",
            TaskType = "exec-library-script",
            Status = "Completed",
            Error = "completed",
            ResultJson = """{"stdout":["result-secret"],"exitCode":0}""",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
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
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(_postgres.GetConnectionString()));
                services.AddScoped<IJobRunService, JobRunService>();
                services.AddSingleton<IScriptService>(_ => throw new NotSupportedException());
                services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
                services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
                services.AddSingleton<ICorrelationContext>(_ => throw new NotSupportedException());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAgentTaskEndpoints());
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
