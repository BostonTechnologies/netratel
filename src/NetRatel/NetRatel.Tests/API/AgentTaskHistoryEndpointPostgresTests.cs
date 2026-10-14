using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
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
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared;
using NetRatel.Shared.Contracts.Tasks;
using Testcontainers.PostgreSql;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTaskHistoryEndpointPostgresTests : IAsyncLifetime
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
    public async Task History_ProjectsOnlySummaryFieldsAndKeepsPersistedResultsOutOfRows()
    {
        await SeedAsync(includeTargetCases: false);
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

    [Fact]
    public async Task Target_reads_share_authorized_tenant_scoped_presentation_and_preserve_frozen_execution_context()
    {
        await SeedAsync();
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var requestTasks = await client.GetFromJsonAsync<List<TaskDto>>("/api/v2/tasks?requestId=task-history-request");
        var task = requestTasks.Should().ContainSingle().Subject;
        var numericTask = await client.GetFromJsonAsync<TaskDto>("/api/v2/tasks/2391");
        numericTask.Should().Be(task);
        task.ClientDisplayName.Should().Be("task-host");
        task.ClientName.Should().Be("Task friendly name");
        task.TenantName.Should().Be("Task tenant");
        task.Environment.Should().BeNull();
        task.StartedAt.Should().BeNull();

        var recent = await client.GetFromJsonAsync<List<TaskDto>>("/api/v2/tasks/recent");
        recent!.Single(x => x.Id == task.Id).Should().Be(task);
        recent.Should().NotContain(x => x.TenantId == 92);
        var mismatched = recent.Single(x => x.Id == 2392);
        mismatched.ClientDisplayName.Should().BeNull();
        mismatched.ClientHostName.Should().BeNull();
        mismatched.ClientName.Should().BeNull();

        var mismatchedDetail = await client.GetFromJsonAsync<List<TaskDto>>("/api/v2/tasks?requestId=wrong-tenant-target");
        mismatchedDetail.Should().ContainSingle().Which.ClientDisplayName.Should().BeNull();
        var denied = await client.GetAsync("/api/v2/tasks?requestId=private-task");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var history = await client.GetFromJsonAsync<TaskHistoryPageDto>("/api/v2/tasks/history?page=0&pageSize=10");
        history!.Total.Should().Be(3);
        history.Items.Single(x => x.Id == task.Id).ClientDisplayName.Should().Be(task.ClientDisplayName);
        history.Items.Single(x => x.Id == 2392).ClientDisplayName.Should().BeNull();
        history.Items.Should().NotContain(x => x.TenantId == 92);
        var privateSearch = await client.GetFromJsonAsync<TaskHistoryPageDto>("/api/v2/tasks/history?search=private-host&page=0&pageSize=10");
        privateSearch!.Total.Should().Be(0, "a wrong-tenant agent must not influence authorized activity searches");

        // A stored name is historical context, independent of the live directory.
        var snapshot = recent.Single(x => x.Id == 2394);
        snapshot.ClientDisplayName.Should().Be("historical-host");
        snapshot.ClientName.Should().BeNull();
        snapshot.Environment.Should().Be(ClientEnvironment.Prod);
        snapshot.StartedAt.Should().Be(snapshot.Created.AddSeconds(1));
        snapshot.ExitCode.Should().Be(7);
        snapshot.ScriptName.Should().Be("Directory listing");
        snapshot.ScriptType.Should().Be("PowerShell");
        var snapshotHistory = history.Items.Single(x => x.Id == snapshot.Id);
        snapshotHistory.ClientDisplayName.Should().Be(snapshot.ClientDisplayName);
        snapshotHistory.ClientName.Should().BeNull();
        snapshotHistory.Environment.Should().Be(snapshot.Environment);
        snapshotHistory.StartedAt.Should().Be(snapshot.StartedAt);
        snapshotHistory.ExitCode.Should().Be(snapshot.ExitCode);
        snapshotHistory.ScriptName.Should().Be(snapshot.ScriptName);
    }

    private async Task SeedAsync(bool includeTargetCases = true)
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
            FriendlyName = "Task friendly name",
            DeviceInfoJson = """{"hostName":"task-host"}"""
        });
        db.JobTaskActivities.Add(new JobTaskActivityRecord
        {
            Id = 2391,
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
        if (!includeTargetCases)
        {
            await db.SaveChangesAsync();
            return;
        }
        var privateAgentId = Guid.NewGuid();
        db.Tenants.Add(new Tenant { Id = 92, Name = "Private tenant" });
        db.Agents.Add(new Agent { Id = privateAgentId, TenantId = 92, Name = "Private agent", DeviceInfoJson = """{"hostName":"private-host"}""" });
        db.JobTaskActivities.AddRange(
            new JobTaskActivityRecord
            {
                Id = 2392, RequestId = "wrong-tenant-target", TenantId = tenantId, AgentId = privateAgentId,
                TaskType = "exec-library-script", Status = "Completed", CreatedAtUtc = DateTimeOffset.UtcNow
            },
            new JobTaskActivityRecord
            {
                Id = 2393, RequestId = "private-task", TenantId = 92, AgentId = privateAgentId,
                TaskType = "exec-library-script", Status = "Completed", CreatedAtUtc = DateTimeOffset.UtcNow,
                TargetDisplayName = "private-snapshot"
            });
        var created = DateTimeOffset.UtcNow;
        db.JobTaskActivities.Add(new JobTaskActivityRecord
        {
            Id = 2394, RequestId = "historical-snapshot", TenantId = tenantId, AgentId = agentId,
            TaskType = "exec-library-script", Status = "Failed", CreatedAtUtc = created,
            StartedAtUtc = created.AddSeconds(1), CompletedAtUtc = created.AddSeconds(2),
            Environment = ClientEnvironment.Prod, ExitCode = 7,
            TargetDisplayName = "historical-host", TargetHostName = "historical-host",
            ScriptName = "Directory listing", ScriptType = "PowerShell"
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
                services.AddSingleton<IEffectiveAccessService, TaskTenantAccess>();
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

    private sealed class TaskTenantAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) => Task.FromResult(tenantId == 91);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("test", false, true, NetRatelPermissions.All));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
