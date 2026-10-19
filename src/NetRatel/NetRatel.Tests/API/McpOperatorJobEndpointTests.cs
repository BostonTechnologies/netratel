using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Jobs;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorJobEndpointTests
{
    private const string MutationResource = "https://mcp.example.test/mcp";
    private const string MutationSubject = "definition-operator";
    private const string MutationClientId = "definition-client";
    private const string ScriptContent = "printf 'definition-marker'";
    private const long MutationJobId = 901;
    private const long MutationScriptId = 801;
    private const long MutationParameterId = 601;
    private const long MutationStepId = 701;
    private static readonly Guid MutationAgentId = Guid.Parse("965d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static string MutationRoot => $"/api/v2/mcp/operator/agents/42/{MutationAgentId:D}/jobs";

    [Theory]
    [InlineData("create")]
    [InlineData("update")]
    [InlineData("delete")]
    [InlineData("param_add")]
    [InlineData("param_update")]
    [InlineData("param_delete")]
    [InlineData("step_add")]
    [InlineData("step_update")]
    [InlineData("step_reorder")]
    [InlineData("step_delete")]
    public async Task Completed_definition_mutations_replay_the_original_result_without_persistence(string action)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, action, MutationRequest(action));
        var confirmed = await ConfirmMutationAsync(client, action, request);
        confirmed.Replayed.Should().BeFalse();
        confirmed.Deleted.Should().Be(action == "delete");
        confirmed.Version.Should().Be(action == "create" ? "1" : "2");
        if (action is "param_add" or "step_add") confirmed.ItemId.Should().NotBeNullOrEmpty();
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var afterConfirmation = writes.Count;

        var replayed = await ConfirmMutationAsync(client, action, request);

        replayed.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterConfirmation);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            (await db.McpOperatorJobAudits.CountAsync()).Should().Be(1);
            var jobId = long.Parse(confirmed.JobId);
            var owner = await db.McpOperatorJobs.SingleAsync(row => row.JobId == jobId);
            owner.Version.Should().Be(long.Parse(confirmed.Version));
            // Later edits/deletion and a changed script must not replace the
            // recorded outcome or require a new validation of execution material.
            owner.Version++;
            owner.DeletedAtUtc = DateTimeOffset.UtcNow;
            (await db.Jobs.SingleAsync(row => row.Id == jobId)).Name = "later-definition";
            (await db.McpOperatorScripts.SingleAsync()).Version++;
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<IMcpOperatorJobStore>();
            (await store.GetOwnedAsync(jobId, 42, MutationAgentId, MutationPrincipal(), MutationResource, "prod", default)).Should().BeNull();
        }
        var afterLaterChange = writes.Count;

        var replayedAfterChange = await ConfirmMutationAsync(client, action, request);

        replayedAfterChange.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterLaterChange);
    }

    [Fact]
    public async Task Changed_confirmation_does_not_consume_the_reviewed_parameter_add()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "param_add", MutationRequest("param_add"));
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        var changed = await client.PostAsJsonAsync(MutationRoot + "/confirm/param_add",
            request with { Parameter = request.Parameter! with { DefaultValue = "unreviewed" } });

        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        writes.Count.Should().Be(beforeAttempt);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
            (await db.JobParameters.CountAsync()).Should().Be(1);
        }

        var confirmed = await ConfirmMutationAsync(client, "param_add", request);
        confirmed.ItemId.Should().NotBeNullOrEmpty();
        confirmed.Version.Should().Be("2");
    }

    [Fact]
    public async Task Fresh_competing_stale_confirmation_remains_unconsumed_after_the_winning_write()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var stale = await PreviewMutationAsync(client, "update", MutationRequest("update"));
        var winning = await PreviewMutationAsync(client, "update",
            MutationRequest("update") with { Job = new McpOperatorJobDraft("winning-definition", "/") });
        await ConfirmMutationAsync(client, "update", winning);
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeAttempt = writes.Count;

        var rejected = await client.PostAsJsonAsync(MutationRoot + "/confirm/update", stale);

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("job_etag_mismatch");
        writes.Count.Should().Be(beforeAttempt);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorConfirmationPlans.CountAsync(plan => plan.ConsumedAtUtc == null)).Should().Be(1);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.Jobs.SingleAsync()).Name.Should().Be("winning-definition");
        (await db.McpOperatorJobs.SingleAsync()).Version.Should().Be(2);
        (await db.McpOperatorJobAudits.CountAsync()).Should().Be(1);
    }

    [Theory]
    [InlineData("policy-version", "confirmation_plan_stale")]
    [InlineData("policy-disabled", "tenant_not_authorized")]
    [InlineData("expired", "confirmation_plan_expired")]
    [InlineData("scope", "oauth_scope_missing")]
    [InlineData("client", "confirmation_plan_stale")]
    [InlineData("resource", "confirmation_plan_stale")]
    [InlineData("target-version", "confirmation_plan_stale")]
    [InlineData("owner", "idempotency_replay_unavailable")]
    [InlineData("owner-tenant", "idempotency_replay_unavailable")]
    public async Task Deleted_definition_replay_rechecks_current_authority_binding_and_tombstone_ownership(string change, string failureCode)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, "delete", MutationRequest("delete"));
        await ConfirmMutationAsync(client, "delete", request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var caller = app.Services.GetRequiredService<MutationCaller>();
            switch (change)
            {
                case "policy-version": (await db.McpOperatorPolicies.SingleAsync()).Version++; break;
                case "policy-disabled": (await db.McpOperatorPolicies.SingleAsync()).DisabledAtUtc = DateTimeOffset.UtcNow; break;
                case "expired": (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "scope": caller.Scopes = []; break;
                case "client": caller.ClientId = "other-client"; break;
                case "resource": caller.Resource = "https://other.example.test/mcp"; break;
                case "target-version": (await db.McpOperatorTargetProfiles.SingleAsync()).Version++; break;
                case "owner": (await db.McpOperatorJobs.SingleAsync()).Subject = "other-owner"; break;
                case "owner-tenant": (await db.McpOperatorJobs.SingleAsync()).TenantId = 43; break;
            }
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await client.PostAsJsonAsync(MutationRoot + "/confirm/delete", request);

        replayed.IsSuccessStatusCode.Should().BeFalse();
        (await replayed.Content.ReadAsStringAsync()).Should().Contain(failureCode);
        writes.Count.Should().Be(beforeReplay);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorJobAudits.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
    }

    [Theory]
    [InlineData("param_add")]
    [InlineData("delete")]
    public async Task Legacy_job_id_receipts_replay_with_the_authorized_current_version(string action)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewMutationAsync(client, action, MutationRequest(action));
        var confirmed = await ConfirmMutationAsync(client, action, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = confirmed.JobId;
            (await db.McpOperatorJobs.SingleAsync()).Version++;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWriteCounter>();
        var beforeReplay = writes.Count;

        var replayed = await ConfirmMutationAsync(client, action, request);

        replayed.JobId.Should().Be(confirmed.JobId);
        replayed.Version.Should().Be("3");
        replayed.ItemId.Should().BeNull();
        replayed.Deleted.Should().Be(action == "delete");
        replayed.Replayed.Should().BeTrue();
        writes.Count.Should().Be(beforeReplay);
    }

    [Fact]
    public void Gateway_terminal_result_supplies_bounded_redacted_job_output()
    {
        var activity = Activity("""{"stdout":["job-marker","token=synthetic-value"],"stderr":"diagnostic","unrelatedSecret":"never expose"}""");

        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(activity, [], 100);

        logs.Select(log => log.Stream).Should().Equal("stdout", "stdout", "stderr");
        logs.Select(log => log.Message).Should().Equal("job-marker", "token=[REDACTED]", "diagnostic");
        logs.Select(log => log.Sequence).Should().Equal(1, 2, 3);
        logs.Should().OnlyContain(log => log.RequestId == activity.RequestId && log.TimestampUtc == activity.CompletedAtUtc);
    }

    [Fact]
    public void Streamed_logs_take_precedence_over_terminal_snapshot()
    {
        var activity = Activity("""{"stdout":["duplicate snapshot"]}""");
        var persisted = new JobTaskLogInfo(1, activity.RequestId, activity.Id, "client", 42,
            "stdout", "streamed marker", 7, activity.CreatedAtUtc);

        McpOperatorJobEndpoints.ProjectActivityLogs(activity, [persisted], 100)
            .Should().ContainSingle().Which.Message.Should().Be("streamed marker");
    }

    [Theory]
    [InlineData("invalid json")]
    [InlineData("[]")]
    [InlineData("{\"stdout\":[null,123,{}],\"stderr\":false}")]
    public void Malformed_or_nontext_results_do_not_become_log_output(string result)
        => McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 100).Should().BeEmpty();

    [Fact]
    public void Output_respects_remaining_line_budget_and_message_bound()
    {
        var result = System.Text.Json.JsonSerializer.Serialize(new { stdout = new[] { new string('x', 5000), "second", "third" } });
        var logs = McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 2);
        logs.Should().HaveCount(2);
        logs[0].Message.Should().HaveLength(4096);
        logs[1].Message.Should().Be("second");
        McpOperatorJobEndpoints.ProjectActivityLogs(Activity(result), [], 0).Should().BeEmpty();
    }

    private static JobTaskActivityInfo Activity(string result) => new(1, "job-request", 2, 3,
        "client", 42, "exec-library-script", "Completed", null,
        DateTimeOffset.Parse("2026-09-05T00:00:00Z"), DateTimeOffset.Parse("2026-09-05T00:00:01Z"), ResultJson: result);

    private static McpOperatorJobMutationRequest MutationRequest(string action) => action switch
    {
        "create" => new(Job: new McpOperatorJobDraft("created-definition", "/")),
        "update" => new(Job: new McpOperatorJobDraft("updated-definition", "/"), JobId: MutationJobId, ExpectedVersion: 1),
        "delete" => new(JobId: MutationJobId, ExpectedVersion: 1),
        "param_add" => new(JobId: MutationJobId, ExpectedVersion: 1, Parameter: new McpOperatorJobParameter("new_parameter", "string", false, DefaultValue: "reviewed")),
        "param_update" => new(JobId: MutationJobId, ExpectedVersion: 1, ParameterId: MutationParameterId, Parameter: new McpOperatorJobParameter("region", "string", false, DefaultValue: "updated")),
        "param_delete" => new(JobId: MutationJobId, ExpectedVersion: 1, ParameterId: MutationParameterId),
        "step_add" => new(JobId: MutationJobId, ExpectedVersion: 1, Step: new McpOperatorJobStep(2, MutationScriptId, 1, MutationHash(ScriptContent))),
        "step_update" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId, Step: new McpOperatorJobStep(1, MutationScriptId, 1, MutationHash(ScriptContent), Enabled: false)),
        "step_reorder" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId, Ordinal: 2),
        "step_delete" => new(JobId: MutationJobId, ExpectedVersion: 1, StepId: MutationStepId),
        _ => throw new ArgumentOutOfRangeException(nameof(action))
    };

    private static async Task<McpOperatorJobMutationRequest> PreviewMutationAsync(HttpClient client, string action, McpOperatorJobMutationRequest request)
    {
        using var response = await client.PostAsJsonAsync(MutationRoot + "/preview/" + action, request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var preview = (await response.Content.ReadFromJsonAsync<McpOperatorJobPreview>())!;
        return request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
    }

    private static async Task<McpOperatorJobMutationResult> ConfirmMutationAsync(HttpClient client, string action, McpOperatorJobMutationRequest request)
    {
        using var response = await client.PostAsJsonAsync(MutationRoot + "/confirm/" + action, request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<McpOperatorJobMutationResult>())!;
    }

    private static async Task<WebApplication> BuildMutationAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, MutationAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "job-definition-" + Guid.NewGuid();
        builder.Services.AddSingleton<MutationWriteCounter>();
        builder.Services.AddSingleton<MutationCaller>();
        builder.Services.AddDbContext<OrchestratorDbContext>((provider, options) => options.UseInMemoryDatabase(databaseName)
            .AddInterceptors(provider.GetRequiredService<MutationWriteCounter>()));
        builder.Services.AddScoped<IMcpOperatorJobStore, McpOperatorJobStore>();
        builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        builder.Services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddScoped<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientPresenceRouter, MutationPresence>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher, MutationDispatcher>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        var app = builder.Build();
        app.UseAuthentication();
        // Exercise real route policy, ownership and durable confirmation state
        // after the signed delegation middleware's separately tested boundary.
        app.Use(async (context, next) =>
        {
            var caller = context.RequestServices.GetRequiredService<MutationCaller>();
            var action = context.Request.Path.Value!.Split('/')[^1];
            context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity(MutationSubject, caller.ClientId, null, [], ["AutomationOperator"], caller.Scopes),
                "mcp-service", "netratel_jobs", action, "definition-request", DateTimeOffset.UtcNow.AddMinutes(1),
                caller.Resource, "prod", 42, MutationAgentId, "definition-correlation");
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcpOperatorJobEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var policyId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = MutationAgentId, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active, CreatedAtUtc = now });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = MutationAgentId, TenantId = 42, Classification = McpOperatorTargetClassification.ManagedStandard,
            TagsJson = "[]", Version = 1, UpdatedAtUtc = now, UpdatedBy = "independent-admin"
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Name = "owned definition mutations", Environment = McpOperatorEnvironment.Production,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = MutationSubject,
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 42, AgentId = MutationAgentId,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard, OperationFamily = McpOperatorOperationFamily.AutomationWrite,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "independent-admin", Version = 1
        });
        await db.SaveChangesAsync();
        var route = new McpOperatorRouteAccessRequest(McpOperatorEnvironment.Production, MutationPrincipal(), "mcp-service", MutationResource,
            "prod", "netratel_jobs", "update", 42, MutationAgentId, new HashSet<string>(["netratel.mcp.write"], StringComparer.Ordinal),
            "definition-correlation", "definition-request", true, true);
        var admitted = await scope.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>().EvaluateAsync(route, default);
        admitted.Decision.IsAllowed.Should().BeTrue();
        db.Jobs.Add(new JobDefinition { Id = MutationJobId, Name = "owned-definition", TenantId = 42, AgentId = MutationAgentId, CreatedAtUtc = now, UpdatedAtUtc = now });
        db.McpOperatorJobs.Add(new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(), JobId = MutationJobId, TenantId = 42, AgentId = MutationAgentId, Subject = MutationSubject, ClientId = MutationClientId,
            McpResource = MutationResource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            TargetSetDigest = admitted.Decision.TargetSetDigest!, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1
        });
        db.JobParameters.Add(new JobParameterDefinition { Id = MutationParameterId, JobId = MutationJobId, Name = "region", Type = "string" });
        db.Scripts.Add(new ScriptDefinition { Id = MutationScriptId, Name = "owned-script", ScriptType = "bash", Content = ScriptContent });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = MutationScriptId, TenantId = 42, Subject = MutationSubject, ClientId = MutationClientId,
            McpResource = MutationResource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            Name = "owned-script", ShellType = "sh", ContentHash = MutationHash(ScriptContent), ManifestHash = MutationHash(string.Empty),
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", Version = 1
        });
        for (var ordinal = 1; ordinal <= 2; ordinal++) db.JobSteps.Add(new JobStepDefinition
        {
            Id = MutationStepId + ordinal - 1, JobId = MutationJobId, Ordinal = ordinal, Type = (int)JobStepKind.LibraryScript,
            ScriptId = MutationScriptId, Enabled = true,
            PayloadJson = JsonSerializer.Serialize(new { ScriptVersion = 1, ScriptContentHash = MutationHash(ScriptContent) })
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static McpOperatorPrincipal MutationPrincipal() => new(MutationSubject, MutationClientId, null,
        new HashSet<string>(), new HashSet<string>(["AutomationOperator"], StringComparer.Ordinal),
        new HashSet<string>(["netratel.mcp.write"], StringComparer.Ordinal));

    private static string MutationHash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MutationCaller
    {
        public string ClientId { get; set; } = MutationClientId;
        public string Resource { get; set; } = MutationResource;
        public string[] Scopes { get; set; } = ["netratel.mcp.write"];
    }

    private sealed class MutationWriteCounter : SaveChangesInterceptor
    {
        public int Count { get; private set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Count++;
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class MutationPresence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutationDispatcher : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => throw new NotSupportedException();
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Production_job_routes_are_versioned_owned_and_m2m_only()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorJobStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IJobRunService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IAkkaJobAuthorityService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorJobEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/details",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/params",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/validate",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/confirm/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/query",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/steps",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/logs",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/{jobId:long}/runs/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/cancel/confirm",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/jobs/runs/{runId:long}/delete/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
