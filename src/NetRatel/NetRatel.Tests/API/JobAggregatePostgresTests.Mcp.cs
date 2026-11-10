using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobAggregatePostgresTests
{
    private const string McpOwner = "synthetic-mcp-runtime-owner";
    private const string McpResource = "https://mcp.example.test/mcp";
    private const string ReviewedContent = "printf '%s' \"$1\"";

    [Fact]
    public async Task HA_Mcp_confirmation_keeps_whole_pool_grants_but_admits_and_dispatches_only_one_worker()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = budget.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 100, ct, reviewedMcp: true);
        var credential = await SeedReviewedMcpAsync(h, ct);
        await UseHaGroupAsync(h, 2, ct);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            foreach (var policy in await db.McpOperatorPolicies.ToArrayAsync(ct))
                policy.ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 1, MaxFanOut: 1)
                    { MaxConcurrentJobTargets = 1 }, McpOperatorJsonContext.Default.McpOperatorConstraints);
            await db.SaveChangesAsync(ct);
        }
        var request = new McpOperatorJobRunRequest(new Dictionary<string, string> { ["Label"] = "@node.id@" });
        var preview = ResultValue<McpOperatorJobRunPreview>(await CallReviewedMcpAsync(h, credential, request, false, ct));
        preview.TargetCount.Should().Be(1); preview.ResolvedTargets.Should().HaveCount(2);
        h.ClaimCrash.ArmParentAdmission();
        request = request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
        await Assert.ThrowsAsync<IOException>(() => CallReviewedMcpAsync(h, credential, request, true, ct));
        var accepted = ResultValue<McpOperatorJobRunResult>(await CallReviewedMcpAsync(h, credential, request, true, ct));
        var parentId = ulong.Parse(accepted.JobRunId);
        var parent = await h.ReadAsync(parentId, ct);
        parent.TargetCount.Should().Be(1); parent.EffectiveConcurrency.Should().Be(1);
        await using (var proof = h.Services.CreateAsyncScope())
        {
            var runs = proof.ServiceProvider.GetRequiredService<IJobRunService>();
            var actual = (await runs.GetResolvedTargetPlanAsync(parentId, ct))!;
            var protectedReview = (await runs.GetMcpExecutionSnapshotAsync(parentId, ct))!;
            actual.MatchesAdmissionPlan(protectedReview.TargetPlan!).Should().BeTrue();
            protectedReview.ReviewedAuthority!.Targets.Should().HaveCount(2);
            var reservations = await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<McpJobTargetReservationRecord>().ToArrayAsync(ct);
            reservations.Should().HaveCount(2).And.OnlyContain(row => row.TargetCount == 1 && row.TargetPlanDigest == preview.TargetPlanDigest);
        }
        var replay = ResultValue<McpOperatorJobRunResult>(await CallReviewedMcpAsync(h, credential, request, true, ct));
        replay.JobRunId.Should().Be(accepted.JobRunId);
        await h.FillAsync(parentId, ct); h.Gateway.Active.Should().ContainSingle();
        await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(parentId, ct);
        (await h.ReadAsync(parentId, ct)).Status.Should().Be(JobRunState.Succeeded);
        h.Gateway.Dispatches.Should().HaveCount(2);
        h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Actual_Mcp_confirmation_recovers_protected_original_plan_and_dispatches_native_nodes_under_current_credentials()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = budget.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct, reviewedMcp: true);
        var credentialId = await SeedReviewedMcpAsync(h, ct);
        var request = new McpOperatorJobRunRequest(new Dictionary<string, string> { ["Label"] = "@node.id@" });

        // Whole-set policy denial is enforced by the actual reviewed resolver,
        // before any confirmation or canonical run is accepted.
        await using (var deny = h.Services.CreateAsyncScope())
        {
            var db = deny.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorPolicies.SingleAsync(row => row.AgentId == h.AgentIds[1], ct)).DisabledAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
            var route = ReviewedRoute(h, credentialId);
            var policies = deny.ServiceProvider.GetRequiredService<IMcpOperatorRouteAdmission>();
            (await policies.EvaluateAsync(route, ct)).Decision.IsAllowed.Should().BeTrue();
            var deniedTarget = (await policies.EvaluateAsync(route with { AgentId = h.AgentIds[1] }, ct)).Decision;
            deniedTarget.IsAllowed.Should().BeFalse(); deniedTarget.Request.AgentId.Should().Be(h.AgentIds[1]);
        }
        var denied = await CallReviewedMcpAsync(h, credentialId, request, false, ct);
        ((IStatusCodeHttpResult)denied).StatusCode.Should().Be(403);
        JsonSerializer.SerializeToElement(((IValueHttpResult)denied).Value).GetProperty("failure").GetProperty("code")
            .GetString().Should().Be("target_authority_required");
        await using (var restore = h.Services.CreateAsyncScope())
        {
            var db = restore.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorPolicies.SingleAsync(row => row.AgentId == h.AgentIds[1], ct)).DisabledAtUtc = null;
            (await db.McpOperatorConfirmationPlans.CountAsync(ct)).Should().Be(0);
            (await db.JobRuns.CountAsync(ct)).Should().Be(0);
            await db.SaveChangesAsync(ct);
        }
        var preview = ResultValue<McpOperatorJobRunPreview>(await CallReviewedMcpAsync(h, credentialId, request, false, ct));
        preview.TargetCount.Should().Be(2); preview.ResolvedTargets!.Select(node => node.AgentId).Should().BeEquivalentTo(h.AgentIds.Take(2));
        request = request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };

        h.ClaimCrash.ArmReviewedConfirmation();
        await Assert.ThrowsAsync<IOException>(() => CallReviewedMcpAsync(h, credentialId, request, true, ct));
        Guid idempotencyId;
        await using (var committed = h.Services.CreateAsyncScope())
        {
            var db = committed.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var receipt = await db.McpOperatorIdempotencyRecords.SingleAsync(ct);
            receipt.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Pending); idempotencyId = receipt.Id;
            (await db.Set<McpReviewedJobAdmissionRecord>().SingleAsync(ct)).ProtectedSubmissionJson.Should().NotContain(ReviewedContent);
            (await db.JobRuns.CountAsync(ct)).Should().Be(0);
            // The next Confirm must use protected admitted material, including the
            // exact scripts and two-node set, after mutable sources have changed.
            var definition = await db.Jobs.SingleAsync(ct);
            definition.TargetJson = JsonSerializer.Serialize(new JobTargetDefinition
                { Source = JobTargetSource.ExplicitNodes, AgentIds = h.AgentIds.Take(3).ToArray() });
            (await db.Scripts.SingleAsync(ct)).Content = "printf mutable-source";
            await db.SaveChangesAsync(ct);
        }

        var accepted = ResultValue<McpOperatorJobRunResult>(await CallReviewedMcpAsync(h, credentialId, request, true, ct));
        accepted.Replayed.Should().BeTrue(); var parentId = ulong.Parse(accepted.JobRunId);
        var parent = await h.ReadAsync(parentId, ct);
        parent.IsAggregate.Should().BeTrue(); parent.AgentId.Should().BeNull(); parent.TargetCount.Should().Be(2);
        parent.TargetPlanDigest.Should().Be(preview.TargetPlanDigest); parent.DefinitionDigest.Should().Be(preview.DefinitionDigest);
        await using (var fresh = h.Services.CreateAsyncScope())
        {
            var runs = fresh.ServiceProvider.GetRequiredService<IJobRunService>();
            var original = (await runs.GetMcpExecutionSnapshotAsync(parentId, ct))!;
            original.ReviewIdempotencyId.Should().Be(idempotencyId);
            original.Definition.Job.AgentId.Should().BeNull(); original.Definition.Job.Target!.AgentIds.Should().HaveCount(2);
            original.Scripts.Single().Script.Content.Should().Be(ReviewedContent);
            original.ReviewedAuthority!.Route.Principal.Roles.Should().Contain("AutomationOperator");
            original.ReviewedAuthority.Targets.Select(grant => grant.AgentId).Should().BeEquivalentTo(h.AgentIds.Take(2));
            original.TargetPlan!.ComputeDigest().Should().Be(preview.TargetPlanDigest);
        }
        await h.FillAsync(parentId, ct);
        h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Should().BeEquivalentTo(h.AgentIds.Take(2));
        var children = h.Gateway.Active.Keys.Order().ToArray(); children.Should().HaveCount(2);
        foreach (var child in children)
        {
            var dispatch = h.Gateway.Active[child];
            var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(dispatch.PayloadJson!)!;
            payload.ScriptContent.Should().Be(ReviewedContent);
            var agent = h.Gateway.Dispatches.Single(item => item.Dispatch.JobRunId == child).Client.AgentId;
            payload.Parameters!["Label"].Should().Be(agent.ToString("D"));
            await using var fresh = h.Services.CreateAsyncScope();
            var projected = (await fresh.ServiceProvider.GetRequiredService<IJobRunService>().GetMcpExecutionSnapshotAsync(child, ct))!;
            projected.Definition.Job.AgentId.Should().Be(agent); projected.Definition.Job.Target.Should().BeNull();
        }
        var replay = ResultValue<McpOperatorJobRunResult>(await CallReviewedMcpAsync(h, credentialId, request, true, ct));
        replay.JobRunId.Should().Be(accepted.JobRunId); replay.Replayed.Should().BeTrue();
        h.Gateway.Dispatches.Should().HaveCount(2);

        await h.SucceedNodeAsync(children[0], ct);
        (await h.ReadAsync(children[0], ct)).Status.Should().Be(JobRunState.Succeeded);
        await using (var revoke = h.Services.CreateAsyncScope())
            (await revoke.ServiceProvider.GetRequiredService<IntegrationCredentialService>()
                .RevokeAsync(McpOwner, credentialId, McpOwner, ct)).Should().BeTrue();
        await h.ReceiptAsync(children[1], JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
        var deniedChild = await h.ReadAsync(children[1], ct);
        deniedChild.Status.Should().Be(JobRunState.Failed);
        await using (var proof = h.Services.CreateAsyncScope())
        {
            var details = (await proof.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(children[1], ct))!;
            details.Steps.First().Status.Should().Be(JobStepRunState.Succeeded);
            details.Steps.Last().StartedAtUtc.Should().BeNull();
            (await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().JobRuns.CountAsync(ct)).Should().Be(3);
        }
        await h.FillAsync(parentId, ct);
        (await h.ReadAsync(parentId, ct)).Status.Should().Be(JobRunState.Failed);
        h.Gateway.Dispatches.Should().HaveCount(3);
    }

    private static void ConfigureReviewedMcp(IServiceCollection services, string connection)
    {
        services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(connection));
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        services.AddScoped<IApplicationPrincipalResolver, ApplicationPrincipalResolver>();
        services.AddScoped<IntegrationCredentialService>();
        services.AddScoped<IIntegrationCredentialCurrentVerifier>(provider => provider.GetRequiredService<IntegrationCredentialService>());
        services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        services.AddScoped<IMcpOperatorSearchAuthorization, McpOperatorSearchAuthorization>();
        services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        services.AddScoped<IMcpOperatorJobStore, McpOperatorJobStore>();
        services.AddScoped<McpJobTargetPolicyReader>(); services.AddScoped<McpAggregateExecutionAuthority>();
        services.AddScoped<IMcpAggregateExecutionAuthority>(provider => provider.GetRequiredService<McpAggregateExecutionAuthority>());
    }

    private static async Task<string> SeedReviewedMcpAsync(AggregateHarness h, CancellationToken ct)
    {
        await using var scope = h.Services.CreateAsyncScope(); var services = scope.ServiceProvider;
        var identity = services.GetRequiredService<NetRatelIdentityDbContext>(); await identity.Database.MigrateAsync(ct);
        identity.Users.Add(new() { Id = "synthetic-mcp-local-user", PrincipalId = McpOwner, UserName = "synthetic-mcp-owner", IsEnabled = true });
        var role = new AccessRole { Name = "Synthetic job author" };
        role.Permissions.Add(new() { Permission = NetRatelPermissions.JobManagement });
        role.Permissions.Add(new() { Permission = NetRatelPermissions.ScriptEdit });
        identity.AccessRoles.Add(role); identity.PrincipalRoleAssignments.Add(new() { PrincipalId = McpOwner, RoleId = role.Id, TenantId = 7 });
        await identity.SaveChangesAsync(ct);
        var credential = await services.GetRequiredService<IntegrationCredentialService>().CreateAsync(McpOwner,
            new("Synthetic reviewed execution", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddHours(1),
                [new(7, NetRatelPermissions.JobManagement), new(7, NetRatelPermissions.ScriptEdit)], McpResource), ct);
        var db = services.GetRequiredService<OrchestratorDbContext>(); var now = DateTimeOffset.UtcNow;
        var policies = h.AgentIds.Take(2).Select(agent => new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(), Name = "Synthetic per-agent job execution", Environment = McpOperatorEnvironment.Production,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
            PrincipalSelectorValue = McpOwner, TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 7, AgentId = agent,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard, OperationFamily = McpOperatorOperationFamily.JobExecution,
            Operation = "netratel_job_runs/start", ConstraintsJson = JsonSerializer.Serialize(
                new McpOperatorConstraints(MaxJobTargetCount: 2, MaxFanOut: 2) { MaxConcurrentJobTargets = 2 },
                McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "synthetic-independent-admin", Version = 1
        }).ToArray();
        db.McpOperatorPolicies.AddRange(policies);
        db.McpOperatorTargetProfiles.AddRange(h.AgentIds.Take(2).Select(agent => new McpOperatorTargetProfileRecord
            { AgentId = agent, TenantId = 7, Classification = McpOperatorTargetClassification.ManagedStandard,
                TagsJson = "[]", Version = 1, UpdatedAtUtc = now, UpdatedBy = "synthetic-independent-admin" }));
        var job = await db.Jobs.SingleAsync(ct);
        job.TargetJson = JsonSerializer.Serialize(new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = h.AgentIds.Take(2).ToArray() });
        const long scriptId = 91;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(ReviewedContent)));
        db.Scripts.Add(new() { Id = scriptId, Name = "Synthetic reviewed script", ScriptType = "bash", Content = ReviewedContent,
            CreatedAtUtc = now, UpdatedAtUtc = now, SourceRevision = 1 });
        db.ScriptParameters.Add(new() { Id = 92, ScriptId = scriptId, Name = "Label", Type = "string" });
        db.JobParameters.Add(new() { Id = 94, JobId = 41, Name = "Label", Type = "string", Required = true });
        db.McpOperatorScripts.Add(new() { Id = Guid.NewGuid(), ScriptId = scriptId, TenantId = 7, Subject = McpOwner,
            ClientId = credential.CredentialId, McpResource = McpResource, McpInstance = "prod", PolicyId = policies[0].Id, PolicyVersion = 1,
            Name = "Synthetic reviewed script", ShellType = "sh", ContentHash = hash, ManifestHash = hash,
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", Version = 1 });
        foreach (var step in await db.JobSteps.ToArrayAsync(ct))
        {
            step.Type = (int)JobStepKind.LibraryScript; step.ScriptId = scriptId; step.Command = null;
            step.PayloadJson = JsonSerializer.Serialize(new { ScriptVersion = 1, ScriptContentHash = hash });
        }
        await db.SaveChangesAsync(ct);
        var route = ReviewedRoute(h, credential.CredentialId);
        var decision = (await services.GetRequiredService<IMcpOperatorRouteAdmission>().EvaluateAsync(route, ct)).Decision;
        decision.IsAllowed.Should().BeTrue();
        db.McpOperatorJobs.Add(new() { Id = Guid.NewGuid(), JobId = 41, TenantId = 7, AgentId = h.AgentIds[0], Subject = McpOwner,
            ClientId = credential.CredentialId, McpResource = McpResource, McpInstance = "prod", PolicyId = policies[0].Id,
            PolicyVersion = 1, TargetSetDigest = decision.Request.TargetSetDigest!, CreatedAtUtc = now, UpdatedAtUtc = now, Version = 1 });
        await db.SaveChangesAsync(ct); return credential.CredentialId;
    }

    private static McpOperatorRouteAccessRequest ReviewedRoute(AggregateHarness h, string credentialId) => new(McpOperatorEnvironment.Production,
        new(McpOwner, credentialId, null, new HashSet<string>(), new HashSet<string> { "AutomationOperator" },
            new HashSet<string> { "netratel.mcp.execute" }), "synthetic-mcp-service", McpResource, "prod", "netratel_job_runs", "start", 7,
        h.AgentIds[0], new HashSet<string> { "netratel.mcp.execute" }, "synthetic-reviewed-correlation", "synthetic-reviewed-request", true, true);

    private static async Task<IResult> CallReviewedMcpAsync(AggregateHarness h, string credentialId,
        McpOperatorJobRunRequest request, bool confirm, CancellationToken ct)
    {
        await using var scope = h.Services.CreateAsyncScope(); var services = scope.ServiceProvider;
        var http = new DefaultHttpContext { RequestServices = services, User = new(new ClaimsIdentity([], "fixture-validated-delegation")) };
        McpOperatorJobConfirmationPayload.TryCreateBinding("netratel_job_runs", "start",
            JsonSerializer.SerializeToElement(new { jobId = 41, request.Inputs, request.RuntimeTarget, request.PlanToken, request.IdempotencyKey }), out var binding);
        http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
            new(McpOwner, credentialId, null, [], ["AutomationOperator"], ["netratel.mcp.execute"]), "synthetic-mcp-service",
            "netratel_job_runs", "start", "synthetic-reviewed-request", DateTimeOffset.UtcNow.AddMinutes(5), McpResource, "prod", 7,
            h.AgentIds[0], "synthetic-reviewed-correlation") { IngressCredentialId = credentialId, IngressPermission = NetRatelPermissions.JobManagement,
                ObjectReference = "41", ObjectTargetResolutionEnabled = true, ReplayBinding = confirm ? binding : null };
        var readiness = new ReviewedReadiness();
        if (confirm) return await McpOperatorJobEndpoints.ConfirmStartAsync(7, h.AgentIds[0], 41, request, http, new FixtureEnvironment(),
            readiness, services.GetRequiredService<IMcpOperatorRouteAdmission>(), services.GetRequiredService<IMcpOperatorConfirmationService>(),
            new(), readiness, services.GetRequiredService<IMcpOperatorJobStore>(), services.GetRequiredService<IAkkaJobAuthorityService>(), ct);
        return await McpOperatorJobEndpoints.PreviewStartAsync(7, h.AgentIds[0], 41, request, http, new FixtureEnvironment(), readiness,
            services.GetRequiredService<IMcpOperatorRouteAdmission>(), services.GetRequiredService<IMcpOperatorConfirmationService>(), new(),
            readiness, services.GetRequiredService<IMcpOperatorJobStore>(), ct);
    }

    private static T ResultValue<T>(IResult result)
    {
        ((IStatusCodeHttpResult)result).StatusCode.Should().BeInRange(200, 299,
            JsonSerializer.Serialize(((IValueHttpResult)result).Value));
        return Assert.IsType<T>(((IValueHttpResult)result).Value);
    }

    private sealed class ReviewedReadiness : IClientPresenceRouter, IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken ct) => throw new NotSupportedException();
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken ct) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken ct) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken ct) => Task.FromResult(
            new ClientPresenceSnapshot(client, ClientPresenceStatus.Online, 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow,
                "synthetic", [], null, "synthetic", true));
    }
}
