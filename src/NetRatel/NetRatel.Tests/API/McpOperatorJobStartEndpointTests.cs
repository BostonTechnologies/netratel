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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Endpoints.Auth;
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
    private const long RunId = 1001;
    private static readonly Guid AgentId = Guid.Parse("965d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static string Root => $"/api/v2/mcp/operator/agents/42/{AgentId:D}/jobs/{JobId}/runs";
    private static string ActionRoot(string action) => $"/api/v2/mcp/operator/agents/42/{AgentId:D}/jobs/runs/{RunId}/{action}";

    [Theory]
    [InlineData("cancel", false)]
    [InlineData("delete", false)]
    [InlineData("cancel", true)]
    [InlineData("delete", true)]
    public async Task Completed_run_actions_replay_the_original_state_without_repeating_dispatch_or_persistence(string action, bool localDelegation)
    {
        await using var app = await BuildAsync(action, localDelegation);
        using var client = app.GetTestClient();
        var boundaryApp = localDelegation ? app : null;
        var request = await PreviewRunActionAsync(client, action, boundaryApp);
        var confirmed = await ConfirmRunActionAsync(client, action, request, boundaryApp);
        confirmed.Replayed.Should().BeFalse();
        confirmed.State.Should().Be(action == "cancel" ? nameof(JobRunState.Running) : nameof(JobRunState.Succeeded));
        var dispatch = app.Services.GetRequiredService<DispatchCounter>();
        dispatch.Cancels.Should().Be(action == "cancel" ? 1 : 0);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (action == "cancel") (await db.JobRuns.SingleAsync()).Status = (int)JobRunState.Cancelled;
            (await db.McpOperatorJobs.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow;
            if (localDelegation) (await db.McpOperatorJobRuns.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
            if (action == "delete")
            {
                var store = scope.ServiceProvider.GetRequiredService<IMcpOperatorJobStore>();
                (await store.GetRunOwnedAsync(RunId, 42, AgentId, StartRoute().Principal, Resource, "prod", default)).Should().BeNull();
            }
        }
        var writes = app.Services.GetRequiredService<RunWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await ConfirmRunActionAsync(client, action, request, boundaryApp);

        replayed.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(beforeReplay);
        dispatch.Cancels.Should().Be(action == "cancel" ? 1 : 0);
        if (localDelegation)
        {
            foreach (var operation in new[] { "get", action })
            {
                using var ordinary = await McpJobLocalDelegationTestBoundary.ExchangeAsync(app, client, "netratel_job_runs", operation, RunId.ToString(), AgentId);
                ordinary.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            }
            var assertion = await McpJobLocalDelegationTestBoundary.AssertionAsync(app, client, "netratel_job_runs", action, RunId.ToString(), AgentId,
                McpOperatorJobConfirmationPayload.RunActionBinding(action, RunId, request.Reason, request.PlanToken!, request.IdempotencyKey!));
            using var preview = await McpJobLocalDelegationTestBoundary.PostAsync(client, ActionRoot(action) + "/preview", request, assertion);
            preview.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            using var changed = await McpJobLocalDelegationTestBoundary.PostAsync(client, ActionRoot(action) + "/confirm", request with { Reason = "another reason" }, assertion);
            changed.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            writes.Count.Should().Be(beforeReplay);
            dispatch.Cancels.Should().Be(action == "cancel" ? 1 : 0);
        }
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorJobRunAudits.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorJobRuns.SingleAsync()).Version.Should().Be(2);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task Fresh_run_action_with_invalid_current_state_does_not_consume_the_plan(string action)
    {
        await using var app = await BuildAsync(action);
        using var client = app.GetTestClient();
        var request = await PreviewRunActionAsync(client, action);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (action == "cancel") (await db.JobRuns.SingleAsync()).Status = (int)JobRunState.Succeeded;
            else (await db.McpOperatorJobRuns.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<RunWriteCounter>();
        var beforeAttempt = writes.Count;

        using var rejected = await client.PostAsJsonAsync(ActionRoot(action) + "/confirm", request);

        rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("job_run_action_invalid");
        writes.Count.Should().Be(beforeAttempt);
        app.Services.GetRequiredService<DispatchCounter>().Cancels.Should().Be(0);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
        (await verificationDb.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
        (await verificationDb.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
    }

    [Theory]
    [InlineData("cancel", "pending", "idempotency_pending")]
    [InlineData("delete", "pending", "idempotency_pending")]
    [InlineData("cancel", "gateway", "capability_unavailable")]
    [InlineData("cancel", "expired", "confirmation_plan_expired")]
    [InlineData("delete", "owner", "idempotency_replay_unavailable")]
    [InlineData("delete", "target", "idempotency_replay_unavailable")]
    [InlineData("delete", "policy", "tenant_not_authorized")]
    [InlineData("delete", "reason", "confirmation_plan_stale")]
    public async Task Run_action_replay_preserves_pending_and_current_admission_denials(string action, string change, string failureCode)
    {
        await using var app = await BuildAsync(action);
        using var client = app.GetTestClient();
        var request = await PreviewRunActionAsync(client, action);
        if (change != "pending") await ConfirmRunActionAsync(client, action, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            if (change == "pending")
            {
                var plan = await db.McpOperatorConfirmationPlans.SingleAsync();
                var decision = await scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>()
                    .EvaluateAsync(StartRoute() with { Operation = action }, default);
                var admission = await scope.ServiceProvider.GetRequiredService<IMcpOperatorConfirmationService>()
                    .ConfirmAsync(new(request.PlanToken!, request.IdempotencyKey!, plan.PayloadHash, decision.Decision), default);
                admission.IsNewDispatch.Should().BeTrue();
            }
            if (action == "cancel") (await db.JobRuns.SingleAsync()).Status = (int)JobRunState.Cancelled;
            else (await db.McpOperatorJobRuns.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow;
            switch (change)
            {
                case "gateway": app.Services.GetRequiredService<DispatchCounter>().Available = false; break;
                case "expired": (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "owner": (await db.McpOperatorJobs.SingleAsync()).Subject = "another-owner"; break;
                case "target": (await db.McpOperatorJobRuns.SingleAsync()).TargetSetDigest = Hash("different-target"); break;
                case "policy": (await db.McpOperatorPolicies.SingleAsync()).DisabledAtUtc = DateTimeOffset.UtcNow; break;
                case "reason": request = request with { Reason = "unreviewed reason" }; break;
            }
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<RunWriteCounter>();
        var beforeReplay = writes.Count;
        var dispatch = app.Services.GetRequiredService<DispatchCounter>();
        var cancelsBeforeReplay = dispatch.Cancels;

        using var replayed = await client.PostAsJsonAsync(ActionRoot(action) + "/confirm", request);

        replayed.IsSuccessStatusCode.Should().BeFalse();
        (await replayed.Content.ReadAsStringAsync()).Should().Contain(failureCode);
        writes.Count.Should().Be(beforeReplay);
        dispatch.Cancels.Should().Be(cancelsBeforeReplay);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(
            change == "pending" ? McpOperatorIdempotencyOutcome.Pending : McpOperatorIdempotencyOutcome.Succeeded);
        (await verificationDb.McpOperatorJobRunAudits.CountAsync()).Should().Be(change == "pending" ? 0 : 1);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("delete")]
    public async Task Legacy_run_id_receipts_replay_the_current_owned_state(string action)
    {
        await using var app = await BuildAsync(action);
        using var client = app.GetTestClient();
        var request = await PreviewRunActionAsync(client, action);
        var confirmed = await ConfirmRunActionAsync(client, action, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = confirmed.JobRunId;
            if (action == "cancel") (await db.JobRuns.SingleAsync()).Status = (int)JobRunState.Cancelled;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<RunWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await ConfirmRunActionAsync(client, action, request);

        replayed.Should().Be(confirmed with { State = action == "cancel" ? nameof(JobRunState.Cancelled) : confirmed.State, Replayed = true });
        writes.Count.Should().Be(beforeReplay);
        app.Services.GetRequiredService<DispatchCounter>().Cancels.Should().Be(action == "cancel" ? 1 : 0);
    }

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

    private static async Task<McpOperatorJobRunActionRequest> PreviewRunActionAsync(HttpClient client, string action, WebApplication? app = null)
    {
        var request = new McpOperatorJobRunActionRequest("reviewed action");
        using var response = app is null ? await client.PostAsJsonAsync(ActionRoot(action) + "/preview", request) :
            await McpJobLocalDelegationTestBoundary.PostAsync(client, ActionRoot(action) + "/preview", request,
                await McpJobLocalDelegationTestBoundary.AssertionAsync(app, client, "netratel_job_runs", action, RunId.ToString(), AgentId));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var preview = (await response.Content.ReadFromJsonAsync<McpOperatorJobRunPreview>())!;
        return request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
    }

    private static async Task<McpOperatorJobRunResult> ConfirmRunActionAsync(HttpClient client, string action, McpOperatorJobRunActionRequest request, WebApplication? app = null)
    {
        using var response = app is null ? await client.PostAsJsonAsync(ActionRoot(action) + "/confirm", request) :
            await McpJobLocalDelegationTestBoundary.PostAsync(client, ActionRoot(action) + "/confirm", request,
                await McpJobLocalDelegationTestBoundary.AssertionAsync(app, client, "netratel_job_runs", action, RunId.ToString(), AgentId,
                    McpOperatorJobConfirmationPayload.RunActionBinding(action, RunId, request.Reason, request.PlanToken!, request.IdempotencyKey!)));
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<McpOperatorJobRunResult>())!;
    }

    private static async Task<WebApplication> BuildAsync(string operation = "start", bool localDelegation = false)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "job-start-" + Guid.NewGuid();
        builder.Services.AddSingleton<RunWriteCounter>();
        builder.Services.AddDbContext<OrchestratorDbContext>((provider, options) => options.UseInMemoryDatabase(databaseName)
            .AddInterceptors(provider.GetRequiredService<RunWriteCounter>()));
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
        if (localDelegation) McpJobLocalDelegationTestBoundary.Configure(builder, Subject, ClientId, Resource);
        var app = builder.Build();
        app.UseAuthentication();
        if (localDelegation) app.UseMiddleware<McpOperatorDelegationMiddleware>();
        else
        {
            // Keep the original direct route/store fixture for its focused
            // policy and execution tests; local cases use the full boundary.
            app.Use(async (context, next) =>
            {
                context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                    new McpOperatorDelegationIdentity(Subject, ClientId, null, [], ["AutomationOperator"], ["netratel.mcp.execute"]),
                    "mcp-service", "netratel_job_runs", operation, "start-request", DateTimeOffset.UtcNow.AddMinutes(1),
                    Resource, "prod", 42, AgentId, "start-correlation");
                await next(context);
            });
        }
        app.UseAuthorization();
        if (localDelegation) app.MapMcpLocalDelegationEndpoints();
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
            OperationFamily = McpOperatorOperationFamily.JobExecution, Operation = "netratel_job_runs/" + operation,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "independent-admin", Version = 1
        });
        await db.SaveChangesAsync();
        var route = StartRoute() with { Operation = operation };
        var admitted = await scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>().EvaluateAsync(route, default);
        admitted.Decision.IsAllowed.Should().BeTrue();
        db.Jobs.Add(new JobDefinition { Id = JobId, Name = "owned-job", TenantId = 42, AgentId = AgentId, CreatedAtUtc = now, UpdatedAtUtc = now });
        var owner = new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(), JobId = JobId, TenantId = 42, AgentId = AgentId, Subject = Subject, ClientId = ClientId,
            McpResource = Resource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            TargetSetDigest = admitted.Decision.TargetSetDigest!, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 2
        };
        db.McpOperatorJobs.Add(owner);
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
        if (operation != "start")
        {
            db.JobRuns.Add(new JobRunRecord
            {
                Id = RunId, JobId = JobId, TenantId = 42, AgentId = AgentId, StartedBy = "reviewed-operator",
                Status = (int)(operation == "cancel" ? JobRunState.Running : JobRunState.Succeeded), CreatedAtUtc = now, StartedAtUtc = now
            });
            db.McpOperatorJobRuns.Add(new McpOperatorJobRunRecord
            {
                Id = Guid.NewGuid(), JobRunId = RunId, JobRecordId = owner.Id, JobId = JobId, TenantId = 42, AgentId = AgentId,
                AcceptedAuditId = Guid.NewGuid(), TargetSetDigest = owner.TargetSetDigest, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
            });
        }
        await db.SaveChangesAsync();
        return app;
    }

    private static McpOperatorRouteAccessRequest StartRoute() => new(McpOperatorEnvironment.Production,
        new McpOperatorPrincipal(Subject, ClientId, null, Set(), Set("AutomationOperator"), Set("netratel.mcp.execute")),
        "mcp-service", Resource, "prod", "netratel_job_runs", "start", 42, AgentId, Set("netratel.mcp.execute"),
        "start-correlation", "start-request", true, true);

    private static IReadOnlySet<string> Set(params string[] values) => values.ToHashSet(StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class DispatchCounter
    {
        public int Starts { get; set; }
        public int Cancels { get; set; }
        public bool Available { get; set; } = true;
    }

    private sealed class RunWriteCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

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
        public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
        {
            counter.Cancels++;
            return Task.FromResult(true);
        }
    }

    private sealed class Dispatcher(DispatchCounter counter) : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => counter.Available;
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
            new AuthenticationTicket(new ClaimsPrincipal(Context.RequestServices.GetService<McpJobLocalDelegationTestBoundary>()?.Identity(Scheme.Name) ??
                new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }
}
