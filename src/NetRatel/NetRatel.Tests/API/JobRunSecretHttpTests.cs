using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobRunSecretHttpTests
{
    [Fact]
    public async Task Ordinary_standalone_task_detail_history_and_logs_withhold_legacy_output_and_preserve_verified_nonsecret_lines()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IEffectiveAccessService, FixtureAccess>();
        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IJobRunService, JobRunService>();
        builder.Services.AddSingleton<IScriptService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ICorrelationContext>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        const string useful = "  Useful 雨 {\"zero\":0,\"flag\":false,\"empty\":\"\"}  ";
        ulong taskId;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Tenants.Add(new() { Id = 7, Name = "Owned fixture tenant" });
            await db.SaveChangesAsync();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            var now = DateTimeOffset.UtcNow;
            var policy = TaskOutputPolicy.Create([("vault", "secret_reference")]);
            var task = await runs.CreateTaskActivityAsync(new("standalone-owned", null, null, "", 7,
                TaskKinds.ExecLibraryScript, "Pending", null, now, null, OutputPolicy: policy));
            taskId = task.Id;
            var native = new TaskOutputRedactor(policy, new Dictionary<string, string> { ["vault"] = "FIXTURE_VALUE" },
                resolveEnvironment: _ => "garden rain Ω 908");
            var result = JsonNode.Parse(native.RedactEnvelope(JsonSerializer.Serialize(new
                { stdout = new[] { useful, "garden rain Ω 908" }, stderr = new[] { "garden rain Ω 908" }, exitCode = 0 }))!)!.AsObject();
            result[TaskOutputRedactor.ReceiptProperty] = policy.Binding;
            var safe = await runs.ProtectTaskOutputAsync(task.RequestId, result.ToJsonString());
            await runs.UpdateTaskActivityStatusAsync(new(task.RequestId, "Completed", null, now, safe, ExitCode: 0));
            await runs.AppendTaskLogAsync(new(task.RequestId, task.Id, "", 7, "stdout", useful, 1, now, policy.Binding));
            await runs.AppendTaskLogAsync(new(task.RequestId, task.Id, "", 7, "stderr", native.Redact("garden rain Ω 908")!, 2, now, policy.Binding));
            foreach (var id in new[] { 71L, 72L })
            {
                db.JobTaskActivities.Add(new() { Id = id, RequestId = "historical-" + id, TenantId = 7,
                    TaskType = TaskKinds.ExecLibraryScript, Status = "Completed", Error = "garden rain Ω 908",
                    ResultJson = "{\"stdout\":[\"garden rain Ω 908\"],\"exitCode\":0}", CommandLabel = "garden rain Ω 908",
                    OutputProtectionJson = id == 72 ? "corrupt-classification" : null, CreatedAtUtc = now, CompletedAtUtc = now });
                db.JobTaskLogs.Add(new() { JobTaskActivityId = id, RequestId = "historical-" + id, TenantId = 7,
                    Stream = "stdout", Message = "garden rain Ω 908", Sequence = 1, TimestampUtc = now });
            }
            await db.SaveChangesAsync();
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapAgentTaskEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var path in new[] { "/api/v2/tasks/recent", "/api/v2/tasks/history", $"/api/v2/tasks/{taskId}",
            $"/api/v2/tasks/{taskId}/logs?sinceId=0", "/api/v2/tasks/71", "/api/v2/tasks/71/logs?sinceId=0",
            "/api/v2/tasks/72", "/api/v2/tasks/72/logs?sinceId=0", "/api/v2/tasks/logs?requestId=standalone-owned&sinceId=0" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            (await response.Content.ReadAsStringAsync()).Should().NotContain("garden rain").And.NotContain("ProtectedLegacyOutputJson");
        }
        using var logs = JsonDocument.Parse(await client.GetStringAsync($"/api/v2/tasks/{taskId}/logs?sinceId=0"));
        logs.RootElement[0].GetProperty("message").GetString().Should().Be(useful);
        logs.RootElement[1].GetProperty("message").GetString().Should().Be(TaskOutputRedactor.Mask);
        client.DefaultRequestHeaders.Add("X-Fixture-Tenant", "8");
        foreach (var path in new[] { $"/api/v2/tasks/{taskId}", $"/api/v2/tasks/{taskId}/logs?sinceId=0" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }
        (await client.GetStringAsync("/api/v2/tasks/recent")).Should().Be("[]");
    }

    [Fact]
    public async Task Ordinary_http_list_detail_stream_and_logs_mask_at_server_and_denied_readers_receive_no_target()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthentication>("Fixture", _ => { });
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        builder.Services.AddSingleton<IEffectiveAccessService, FixtureAccess>();
        builder.Services.AddSingleton<ExecutionActivityBroker>();
        builder.Services.AddSingleton(TimeProvider.System);
        var databaseName = Guid.NewGuid().ToString("N");
        builder.Services.AddDbContext<OrchestratorDbContext>(o => o.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IJobRunService, JobRunService>();
        builder.Services.AddScoped<IJobDefinitionService, JobDefinitionService>();
        builder.Services.AddScoped<ITenantService, TenantService>();
        builder.Services.AddSingleton<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<ICorrelationContext>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Owned inert fixture" });
            db.JobParameters.Add(new() { Id = 42, JobId = 41, Name = "phrase", Type = "secret" });
            db.Tenants.Add(new() { Id = 7, Name = "Owned fixture tenant" });
            await db.SaveChangesAsync();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            var now = DateTimeOffset.UtcNow;
            await runs.UpsertRunAsync(new(51, 41, 7, "", "fixture", JobRunState.Failed, 1, now, now, now,
                "garden rain Ω 908", "{\"phrase\":\"garden rain Ω 908\",\"normal\":\"useful\",\"flag\":false}", null));
            await runs.UpsertStepRunAsync(new(52, 51, 1, JobStepRunState.Failed, 1, "owned-task", "garden rain Ω 908", now, now));
            var activity = await runs.CreateTaskActivityAsync(new("owned-task", 51, 1, "", 7, "inert", "Failed", "garden rain Ω 908", now, now));
            await runs.AppendTaskLogAsync(new("owned-task", activity.Id, "", 7, "stdout", "garden rain Ω 908", 1, now));
        }
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapJobRunEndpoints();
        await app.StartAsync();
        using var client = app.GetTestClient();
        foreach (var path in new[] { "/api/v1/jobruns/", "/api/v1/jobruns/query", "/api/v1/jobruns/51", "/api/v1/jobruns/51/steps", "/api/v1/jobruns/51/steps/1/logs", "/api/v1/jobruns/51/stream" })
        {
            using var response = await client.GetAsync(path);
            response.StatusCode.Should().Be(HttpStatusCode.OK, path);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("garden rain").And.NotContain("ProtectedExecutionJson");
            if (path == "/api/v1/jobruns/51") body.Should().Contain("useful").And.Contain("[REDACTED]").And.Contain("submitted-definition-v1");
        }
        foreach (var deniedTenant in new[] { "8", "denied" })
        {
            client.DefaultRequestHeaders.Remove("X-Fixture-Tenant");
            client.DefaultRequestHeaders.Add("X-Fixture-Tenant", deniedTenant);
            foreach (var path in new[] { "/api/v1/jobruns/51", "/api/v1/jobruns/51/stream", "/api/v1/jobruns/51/steps/1/logs" })
            {
                using var response = await client.GetAsync(path);
                response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
                (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
            }
            (await client.GetStringAsync("/api/v1/jobruns/")).Should().Be("[]");
        }
    }

    private sealed class FixtureAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 7 && principal.FindFirstValue("fixture-tenant") == "7" &&
                permission is NetRatelPermissions.JobManagement or NetRatelPermissions.ScriptExecute);
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission, CancellationToken cancellationToken = default) => Task.FromResult<int[]?>([7]);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("fixture", false, false, new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FixtureAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(ClaimTypes.NameIdentifier, "owned-reader"), new Claim("fixture-tenant", Request.Headers["X-Fixture-Tenant"].FirstOrDefault() ?? "7")], "Fixture")), Scheme.Name)));
    }
}
