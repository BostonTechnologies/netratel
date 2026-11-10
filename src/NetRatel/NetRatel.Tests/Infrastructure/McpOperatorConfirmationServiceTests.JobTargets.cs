using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed partial class McpOperatorConfirmationServiceTests
{
    [Fact]
    public async Task Whole_job_plans_reserve_capacity_once_and_reject_an_insufficient_remainder()
    {
        await using var db = CreateDb();
        var decision = await JobDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var firstPlan = await service.CreatePlanAsync(new(decision, PayloadHash) { JobRequestHash = Digest }, Ct);
        var secondPlan = await service.CreatePlanAsync(new(decision, new string('C', 64)) { JobRequestHash = new string('D', 64) }, Ct);
        var first = JobConfirmation(firstPlan, PayloadHash, decision);
        var admitted = await service.ConfirmAsync(first, Ct);
        admitted.IsNewDispatch.Should().BeTrue();
        var replay = await service.ConfirmAsync(first, Ct);
        replay.IsReplay.Should().BeTrue();
        replay.IdempotencyId.Should().Be(admitted.IdempotencyId);
        var denied = await service.ConfirmAsync(JobConfirmation(secondPlan, new string('C', 64), decision), Ct);
        denied.FailureCode.Should().Be("job_target_quota_exhausted");
        (await db.Set<McpJobTargetReservationRecord>().SingleAsync(Ct)).TargetCount.Should().Be(2);
        (await db.McpOperatorConfirmationPlans.SingleAsync(plan => plan.IdempotencyKey == secondPlan.IdempotencyKey, Ct)).ConsumedAtUtc.Should().BeNull();
        (await db.McpOperatorIdempotencyRecords.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Admitted_job_receipt_keeps_its_original_input_identity_after_preview_expiry_and_group_changes()
    {
        await using var db = CreateDb();
        var decision = await JobDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var plan = await service.CreatePlanAsync(new(decision, PayloadHash) { JobRequestHash = Digest }, Ct);
        var admitted = await service.ConfirmAsync(JobConfirmation(plan, PayloadHash, decision), Ct);
        await service.CompleteAsync(admitted.IdempotencyId!.Value, McpOperatorIdempotencyOutcome.Succeeded, "42", Ct);
        var persisted = await db.McpOperatorConfirmationPlans.SingleAsync(Ct);
        persisted.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(Ct);

        var replay = await service.TryJobReplayAsync(plan.PlanToken, plan.IdempotencyKey, Digest, decision, Ct);
        replay!.IsReplay.Should().BeTrue();
        replay.ResultReference.Should().Be("42");
        replay.IdempotencyId.Should().Be(admitted.IdempotencyId);
        var substituted = await service.TryJobReplayAsync(plan.PlanToken, plan.IdempotencyKey, new string('E', 64), decision, Ct);
        substituted!.FailureCode.Should().Be("idempotency_conflict");
        var foreign = decision with { Request = decision.Request with { TenantId = 43 } };
        (await service.TryJobReplayAsync(plan.PlanToken, plan.IdempotencyKey, Digest, foreign, Ct))!.FailureCode.Should().Be("idempotency_conflict");
        (await db.Set<McpJobTargetReservationRecord>().CountAsync(Ct)).Should().Be(1);
    }

    private static McpOperatorConfirmationRequest JobConfirmation(McpOperatorConfirmationPlan plan, string payload,
        McpOperatorDecision decision) => new(plan.PlanToken, plan.IdempotencyKey, payload, decision)
        { JobTargetPlanDigest = new string('F', 64), JobTargetQuotas = [new(decision.MatchingPolicyIds.Single(), 1, 2, 3)] };

    private static async Task<McpOperatorDecision> JobDecisionAsync(OrchestratorDbContext db)
    {
        var original = await AllowedDecisionAsync(db);
        var policy = await db.McpOperatorPolicies.SingleAsync(Ct);
        policy.Operation = "netratel_job_runs/start";
        policy.OperationFamily = McpOperatorOperationFamily.JobExecution;
        policy.ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 10, MaxFanOut: 10)
        { MaxConcurrentJobTargets = 3 }, McpOperatorJsonContext.Default.McpOperatorConstraints);
        await db.SaveChangesAsync(Ct);
        var request = original.Request with { Tool = "netratel_job_runs", Operation = policy.Operation,
            OperationFamily = McpOperatorOperationFamily.JobExecution };
        return await new McpOperatorAuthorization(db).EvaluateAsync(request, Ct);
    }
}
