using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Shared;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class McpReviewedJobAdmissionPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Crash_after_whole_plan_admission_before_parent_retains_original_protected_material_after_group_and_definition_edits()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        var agent = Guid.NewGuid(); var replacement = Guid.NewGuid(); var groupId = Guid.NewGuid(); var policyId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var principal = new McpOperatorPrincipal("recovery-operator", "recovery-client", null, new HashSet<string>(),
            new HashSet<string> { "AutomationOperator" }, new HashSet<string> { "netratel.mcp.execute" });
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Development, principal, 42, agent,
            McpOperatorTargetClassification.DevelopmentSafe, McpOperatorOperationFamily.JobExecution, "netratel_job_runs/start",
            principal.Scopes, McpOperatorConfirmationClass.RemoteExecution, "recovery-correlation", "recovery-request", new string('A', 64),
            McpResource: "https://mcp.example.test/mcp", McpInstance: "dev", Tool: "netratel_job_runs");
        var target = new JobTargetDefinition { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = groupId };
        var policy = new JobExecutionPolicy(1);
        McpOperatorDecision decision; McpOperatorConfirmationPlan preview; Guid receiptId; string requestHash;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            db.Tenants.Add(new() { Id = 42, Name = "Recovery fixture", CreatedAtUtc = now, UpdatedAtUtc = now });
            db.Agents.AddRange(new Agent { Id = agent, TenantId = 42, Name = "original-node", CreatedAtUtc = now },
                new Agent { Id = replacement, TenantId = 42, Name = "replacement-node", CreatedAtUtc = now });
            db.ExecutionGroups.Add(new() { Id = groupId, TenantId = 42, Name = "Reviewed group", NormalizedName = "REVIEWED GROUP",
                CreatedAtUtc = now, UpdatedAtUtc = now, Members = [new() { TenantId = 42, AgentId = agent }] });
            db.Jobs.Add(new() { Id = 11, TenantId = 42, Name = "original-definition", CreatedAtUtc = now, UpdatedAtUtc = now,
                TargetJson = JsonSerializer.Serialize(target) });
            db.McpOperatorPolicies.Add(new() { Id = policyId, Name = "Recovery quota", Environment = access.Environment,
                Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                PrincipalSelectorValue = principal.Subject, TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant, TenantId = 42,
                OperationFamily = access.OperationFamily, Operation = access.Operation, CreatedAtUtc = now, CreatedBy = "fixture", Version = 1,
                ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxFanOut: 2, MaxJobTargetCount: 2)
                { MaxConcurrentJobTargets = 2 }, McpOperatorJsonContext.Default.McpOperatorConstraints) });
            await db.SaveChangesAsync(ct);
            decision = await new McpOperatorAuthorization(db).EvaluateAsync(access, ct);
            decision.IsAllowed.Should().BeTrue();
            var definition = new JobDefinitionDetails(new JobDefinitionInfo(11, "original-definition", "/", null, 42, "", now, now, null)
                { Target = target, ExecutionPolicy = policy }, [], []);
            var snapshot = new McpOperatorJobExecutionSnapshot(definition, 1, principal.Subject, principal.ClientId!,
                access.McpResource!, access.McpInstance!, access.TargetSetDigest!, [],
                [new(new ScriptInfo(12, "original-script", "/", null, "PRIVATE_REVIEWED_SOURCE", null, "bash", now, now), [], 1,
                    new string('B', 64), "/tmp", 15)]);
            var route = new McpOperatorRouteAccessRequest(access.Environment, principal, "mcp-service", access.McpResource!,
                access.McpInstance!, "netratel_job_runs", "start", 42, agent, principal.Scopes, "recovery-correlation", "recovery-request", true, true);
            var plan = new JobResolvedTargetPlan(1, 42, 11, snapshot.ComputeDefinitionDigest(), target, 1, [],
                [new(agent, "original-node", null, [])], policy);
            snapshot = snapshot with { TargetPlan = plan, ReviewedAuthority = new(route, null, "recovery-client",
                [new(agent, policyId, 1, access.TargetSetDigest!, new string('C', 64))]) };
            var submission = new McpReviewedJobSubmission(snapshot, new("mcp-reviewed:recovery-operator", "{\"input\":\"PRIVATE_REVIEWED_INPUT\"}", null)
                { ExpectedTargetPlanDigest = plan.ComputeDigest() });
            requestHash = submission.ComputeRequestHash();
            var service = new McpOperatorConfirmationService(db, keys);
            preview = await service.CreatePlanAsync(new(decision, submission.ComputePayloadHash()) { JobRequestHash = requestHash }, ct);
            var receipt = await service.ConfirmAsync(new(preview.PlanToken, preview.IdempotencyKey, submission.ComputePayloadHash(), decision)
            { JobSubmission = submission, JobTargetPlanDigest = plan.ComputeDigest(), JobTargetQuotas = [new(policyId, 1, 1, 2)] }, ct);
            receipt.IsNewDispatch.Should().BeTrue(); receiptId = receipt.IdempotencyId!.Value;
            (await db.JobRuns.CountAsync(ct)).Should().Be(0);
            (await db.Set<McpJobTargetReservationRecord>().SingleAsync(ct)).IdempotencyId.Should().Be(receiptId);
            var material = await db.Set<McpReviewedJobAdmissionRecord>().SingleAsync(ct);
            material.ProtectedSubmissionJson.Should().NotContain("PRIVATE_REVIEWED_SOURCE").And.NotContain("PRIVATE_REVIEWED_INPUT");
            await Assert.ThrowsAsync<JobDefinitionInUseException>(() => new JobDefinitionService(db).DeleteAsync(11, ct));
            var group = await db.ExecutionGroups.Include(row => row.Members).SingleAsync(ct);
            group.Revision = 2; group.Members.Clear(); group.Members.Add(new() { TenantId = 42, AgentId = replacement });
            (await db.Jobs.SingleAsync(ct)).Name = "edited-definition";
            await db.SaveChangesAsync(ct);
        }
        // New context models a process restart with no parent/dispatch receipt.
        await using var reloaded = new OrchestratorDbContext(options);
        var recoveredService = new McpOperatorConfirmationService(reloaded, keys);
        var retained = await recoveredService.TryJobReplayAsync(preview.PlanToken, preview.IdempotencyKey, requestHash, decision, ct);
        retained!.IdempotencyId.Should().Be(receiptId); retained.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Pending);
        var original = await recoveredService.GetReviewedJobSubmissionAsync(receiptId, decision, ct);
        original.Should().NotBeNull();
        original!.Snapshot.Definition.Job.Name.Should().Be("original-definition");
        original.Snapshot.TargetPlan!.GroupRevision.Should().Be(1);
        original.Snapshot.TargetPlan.Nodes.Select(node => node.AgentId).Should().Equal(agent);
        original.Snapshot.Scripts.Single().Script.Content.Should().Be("PRIVATE_REVIEWED_SOURCE");
        original.Request.InputsJson.Should().Contain("PRIVATE_REVIEWED_INPUT");
        original.Snapshot.ReviewIdempotencyId.Should().Be(receiptId);
        (await recoveredService.GetReviewedJobSubmissionAsync(receiptId, decision with { Request = access with { TenantId = 43 } }, ct))
            .Should().BeNull();
        var current = new NetRatel.Tests.API.McpJobLocalDelegationTestBoundary("recovery-operator", "recovery-client", access.McpResource!)
        { CurrentFailureCode = "delegated_identity_revoked" };
        var verifier = new McpAggregateExecutionAuthority(null!, null!, null!, current, null!, current, null!);
        var native = new RejectingNativeAuthority();
        var sweep = new McpReviewedJobAdmissionRecovery(reloaded, recoveredService, verifier, native,
            new McpOperatorJobStore(reloaded), NullLogger<McpReviewedJobAdmissionRecovery>.Instance);
        await sweep.SweepAsync(ct: ct);
        native.Starts.Should().Be(0);
        reloaded.ChangeTracker.Clear();
        var settled = await reloaded.McpOperatorIdempotencyRecords.SingleAsync(ct);
        settled.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Failed);
        settled.ResultReference.Should().Be("job_current_authority_denied");
        (await reloaded.JobRuns.CountAsync(ct)).Should().Be(0);
        (await reloaded.McpOperatorAcceptedAudits.CountAsync(ct)).Should().Be(1);
        var replacementPreview = await recoveredService.CreatePlanAsync(new(decision, new string('D', 64)), ct);
        var remaining = await recoveredService.ConfirmAsync(new(replacementPreview.PlanToken, replacementPreview.IdempotencyKey,
            new string('D', 64), decision) { JobTargetPlanDigest = new string('F', 64), JobTargetQuotas = [new(policyId, 1, 2, 2)] }, ct);
        remaining.IsNewDispatch.Should().BeTrue();
        (await new JobDefinitionService(reloaded).DeleteAsync(11, ct)).Should().NotBeNull();
        (await reloaded.Set<McpReviewedJobAdmissionRecord>().CountAsync(ct)).Should().Be(1);
    }

    private sealed class RejectingNativeAuthority : IAkkaJobAuthorityService
    {
        public int Starts { get; private set; }
        public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken ct)
            => throw new InvalidOperationException("Only protected reviewed admission may be used.");
        public Task<JobRunInfo> StartReviewedMcpAsync(McpOperatorJobExecutionSnapshot snapshot, RunJobRequest request, CancellationToken ct)
        {
            Starts++;
            throw new InvalidOperationException("A revoked pre-parent admission must never reach native start.");
        }
        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken ct) => throw new NotSupportedException();
    }
}
