using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorJobStartEndpointTests
{
    private const string Resource = "https://mcp.example.test/mcp";
    private const string Subject = "job-operator";
    private const string ClientId = "job-client";
    private const string Content = "printf 'owned-job-marker'";
    private const long JobId = 901;
    private const long ScriptId = 801;
    private static readonly Guid AgentId = Guid.Parse("965d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static string Root => $"/api/v2/mcp/operator/agents/42/{AgentId:D}/jobs/{JobId}/runs";

    [Fact]
    public async Task Execute_only_start_previews_confirms_and_replays_one_owned_pinned_job()
    {
        await using var app = await BuildAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(Root + "/preview", new McpOperatorJobRunRequest());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await response.Content.ReadFromJsonAsync<McpOperatorJobRunPreview>())!;
        var request = new McpOperatorJobRunRequest(PlanToken: preview.PlanToken, IdempotencyKey: preview.IdempotencyKey);
        var confirmed = await client.PostAsJsonAsync(Root + "/confirm", request);
        confirmed.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var result = (await confirmed.Content.ReadFromJsonAsync<McpOperatorJobRunResult>())!;
        result.State.Should().Be(nameof(JobRunState.Succeeded));
        result.Replayed.Should().BeFalse();
        var replayed = await client.PostAsJsonAsync(Root + "/confirm", request);
        replayed.StatusCode.Should().Be(HttpStatusCode.OK);
        (await replayed.Content.ReadFromJsonAsync<McpOperatorJobRunResult>())!.Replayed.Should().BeTrue();
        app.Services.GetRequiredService<DispatchCounter>().Starts.Should().Be(1);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorAcceptedAudits.SingleAsync()).ScopesJson.Should().Contain("netratel.mcp.execute").And.NotContain("netratel.mcp.read");
        (await db.McpOperatorJobRuns.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("script-owner")]
    [InlineData("script-tenant")]
    [InlineData("script-revision")]
    [InlineData("script-content")]
    [InlineData("script-deleted")]
    public async Task Invalid_pinned_script_cannot_create_a_start_plan_or_run(string change)
    {
        await using var app = await BuildAsync();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var script = await db.McpOperatorScripts.SingleAsync();
            switch (change)
            {
                case "script-owner": script.Subject = "another-operator"; break;
                case "script-tenant": script.TenantId = 43; break;
                case "script-revision": script.Version++; break;
                case "script-content": (await db.Scripts.SingleAsync()).Content = "printf 'unreviewed'"; break;
                case "script-deleted": (await db.Scripts.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow; break;
            }
            await db.SaveChangesAsync();
        }
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(Root + "/preview", new McpOperatorJobRunRequest());

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("job_run_invalid");
        app.Services.GetRequiredService<DispatchCounter>().Starts.Should().Be(0);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorConfirmationPlans.CountAsync()).Should().Be(0);
        (await verificationDb.JobRuns.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("owner-version")]
    [InlineData("legacy-definition")]
    [InlineData("legacy-step")]
    [InlineData("script-parameters")]
    public async Task Changed_execution_material_invalidates_an_existing_start_plan_before_dispatch(string change)
    {
        await using var app = await BuildAsync();
        using var client = app.GetTestClient();
        var previewResponse = await client.PostAsJsonAsync(Root + "/preview", new McpOperatorJobRunRequest());
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<McpOperatorJobRunPreview>())!;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            switch (change)
            {
                case "owner-version": (await db.McpOperatorJobs.SingleAsync()).Version++; break;
                case "legacy-definition": (await db.Jobs.SingleAsync()).Name = "revised-owned-job"; break;
                case "legacy-step": (await db.JobSteps.SingleAsync()).Ordinal++; break;
                case "script-parameters": db.ScriptParameters.Add(new ScriptParameterDefinition
                    { ScriptId = ScriptId, Name = "credential", Type = "secret", Required = false }); break;
            }
            await db.SaveChangesAsync();
        }

        var confirmed = await client.PostAsJsonAsync(Root + "/confirm",
            new McpOperatorJobRunRequest(PlanToken: preview.PlanToken, IdempotencyKey: preview.IdempotencyKey));

        confirmed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await confirmed.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        app.Services.GetRequiredService<DispatchCounter>().Starts.Should().Be(0);
    }

    [Fact]
    public async Task Start_assertion_cannot_be_reused_to_read_arbitrary_scripts()
    {
        await using var app = await BuildAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var admission = scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>();
        var read = StartRoute() with { Tool = "netratel_scripts", Operation = "get", RequiredScopes = Set("netratel.mcp.read") };

        var decision = await admission.EvaluateAsync(read, default);

        decision.Decision.IsAllowed.Should().BeFalse();
        decision.Decision.FailureCode.Should().Be("oauth_scope_missing");
    }

    private static async Task<WebApplication> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "job-start-" + Guid.NewGuid();
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IMcpOperatorJobStore, McpOperatorJobStore>();
        builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        builder.Services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddSingleton<DispatchCounter>();
        builder.Services.AddScoped<IAkkaJobAuthorityService, RecordingAuthority>();
        builder.Services.AddScoped<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientPresenceRouter, Presence>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher, Dispatcher>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        var app = builder.Build();
        app.UseAuthentication();
        // Current-grant and signed-delegation checks are covered separately in
        // McpFileDelegationPermissionTests. Exercise real route/store/plan
        // admission here with the exact Execute-only middleware output.
        app.Use(async (context, next) =>
        {
            context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity(Subject, ClientId, null, [], ["AutomationOperator"], ["netratel.mcp.execute"]),
                "mcp-service", "netratel_job_runs", "start", "start-request", DateTimeOffset.UtcNow.AddMinutes(1),
                Resource, "prod", 42, AgentId, "start-correlation");
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcpOperatorJobEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var policyId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = AgentId, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active, CreatedAtUtc = now });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = AgentId, TenantId = 42, Classification = McpOperatorTargetClassification.ManagedStandard,
            TagsJson = "[]", Version = 1, UpdatedAtUtc = now, UpdatedBy = "independent-admin"
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Name = "owned job execution", Environment = McpOperatorEnvironment.Production,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = Subject,
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 42, AgentId = AgentId,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard,
            OperationFamily = McpOperatorOperationFamily.JobExecution, Operation = "netratel_job_runs/start",
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "independent-admin", Version = 1
        });
        await db.SaveChangesAsync();
        var admitted = await scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>().EvaluateAsync(StartRoute(), default);
        admitted.Decision.IsAllowed.Should().BeTrue();
        db.Jobs.Add(new JobDefinition { Id = JobId, Name = "owned-job", TenantId = 42, AgentId = AgentId, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.McpOperatorJobs.Add(new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(), JobId = JobId, TenantId = 42, AgentId = AgentId, Subject = Subject, ClientId = ClientId,
            McpResource = Resource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            TargetSetDigest = admitted.Decision.TargetSetDigest!, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 2
        });
        db.Scripts.Add(new ScriptDefinition { Id = ScriptId, Name = "owned-script", ScriptType = "bash", Content = Content });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = ScriptId, TenantId = 42, Subject = Subject, ClientId = ClientId,
            McpResource = Resource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            Name = "owned-script", ShellType = "sh", ContentHash = Hash(Content), ManifestHash = Hash(string.Empty),
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", Version = 1
        });
        db.JobSteps.Add(new JobStepDefinition
        {
            Id = 701, JobId = JobId, Ordinal = 1, Type = (int)JobStepKind.LibraryScript, ScriptId = ScriptId, Enabled = true,
            PayloadJson = JsonSerializer.Serialize(new { ScriptVersion = 1, ScriptContentHash = Hash(Content) })
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static McpOperatorRouteAccessRequest StartRoute() => new(McpOperatorEnvironment.Production,
        new McpOperatorPrincipal(Subject, ClientId, null, Set(), Set("AutomationOperator"), Set("netratel.mcp.execute")),
        "mcp-service", Resource, "prod", "netratel_job_runs", "start", 42, AgentId, Set("netratel.mcp.execute"),
        "start-correlation", "start-request", true, true);

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class DispatchCounter { public int Starts { get; set; } }

    private sealed class RecordingAuthority(OrchestratorDbContext db, DispatchCounter counter) : IAkkaJobAuthorityService
    {
        public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("This test requires the reviewed snapshot entry point.");
        public async Task<JobRunInfo> StartReviewedMcpAsync(McpOperatorJobExecutionSnapshot snapshot, RunJobRequest request, CancellationToken cancellationToken)
        {
            var jobId = snapshot.Definition.Job.Id;
            counter.Starts++;
            var now = DateTimeOffset.UtcNow;
            db.JobRuns.Add(new JobRunRecord
            {
                Id = 1001, JobId = checked((long)jobId), TenantId = 42, AgentId = AgentId, StartedBy = request.StartedBy,
                Status = (int)JobRunState.Succeeded, CreatedAtUtc = now, StartedAtUtc = now, CompletedAtUtc = now
            });
            await db.SaveChangesAsync(cancellationToken);
            return new JobRunInfo(1001, jobId, 42, string.Empty, request.StartedBy, JobRunState.Succeeded,
                1, now, now, now, null, request.InputsJson, null, AgentId);
        }
        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Dispatcher : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Presence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(
            new ClientPresenceSnapshot(client, ClientPresenceStatus.Online, 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "test", [], null, "test", true));
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }
}
