using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorConfirmationServiceTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;
    private const string Digest = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string PayloadHash = "BBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBBB";

    [Fact]
    public async Task ConfirmAsync_ConsumesTheBoundPlanOnceAndReplaysTheCompletedResult()
    {
        await using var db = CreateDb();
        var decision = await AllowedDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var plan = await service.CreatePlanAsync(new(decision, PayloadHash), Ct);

        var admitted = await service.ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey, PayloadHash, decision), Ct);
        await service.CompleteAsync(admitted.IdempotencyId!.Value, McpOperatorIdempotencyOutcome.Succeeded, "run-42", Ct);
        var replayed = await service.ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey, PayloadHash, decision), Ct);

        admitted.IsNewDispatch.Should().BeTrue();
        admitted.IsReplay.Should().BeFalse();
        replayed.IsNewDispatch.Should().BeFalse();
        replayed.IsReplay.Should().BeTrue();
        replayed.Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        replayed.ResultReference.Should().Be("run-42");
        var persistedPlan = await db.McpOperatorConfirmationPlans.SingleAsync(Ct);
        persistedPlan.TokenHash.Should().NotBe(plan.PlanToken);
        persistedPlan.PayloadHash.Should().Be(PayloadHash);
    }

    [Fact]
    public async Task ConfirmAsync_RejectsAPlanWhenThePolicyVersionChangesAfterPreview()
    {
        await using var db = CreateDb();
        var decision = await AllowedDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var plan = await service.CreatePlanAsync(new(decision, PayloadHash), Ct);
        var policy = await db.McpOperatorPolicies.SingleAsync(Ct);
        policy.Version++;
        await db.SaveChangesAsync(Ct);
        var current = await new McpOperatorAuthorization(db).EvaluateAsync(decision.Request, Ct);

        var result = await service.ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey, PayloadHash, current), Ct);

        result.IsNewDispatch.Should().BeFalse();
        result.FailureCode.Should().Be("confirmation_plan_stale");
    }

    [Fact]
    public async Task ConfirmAsync_RejectsAConflictingExistingIdempotencyKey()
    {
        await using var db = CreateDb();
        var decision = await AllowedDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var plan = await service.CreatePlanAsync(new(decision, PayloadHash), Ct);
        var request = decision.Request;
        db.McpOperatorIdempotencyRecords.Add(new McpOperatorIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Environment = request.Environment,
            Subject = request.Principal.Subject,
            ClientId = request.Principal.ClientId!,
            TenantId = request.TenantId,
            AgentId = request.AgentId,
            TargetSetDigest = request.TargetSetDigest!,
            PolicyId = decision.MatchingPolicyIds.Single(),
            PolicyVersion = decision.SelectedPolicyVersion!.Value,
            OperationFamily = request.OperationFamily,
            Operation = request.Operation,
            IdempotencyKey = plan.IdempotencyKey,
            PayloadHash = "CCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCCC",
            Outcome = McpOperatorIdempotencyOutcome.Succeeded,
            ResultReference = "different-run",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Version = 1
        });
        await db.SaveChangesAsync(Ct);

        var result = await service.ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey, PayloadHash, decision), Ct);

        result.IsNewDispatch.Should().BeFalse();
        result.FailureCode.Should().Be("idempotency_conflict");
    }

    [Fact]
    public async Task ConfirmAsync_ScopesTheIdempotencyKeyByOperationFamily()
    {
        await using var db = CreateDb();
        var decision = await AllowedDecisionAsync(db);
        var service = new McpOperatorConfirmationService(db);
        var plan = await service.CreatePlanAsync(new(decision, PayloadHash), Ct);
        var request = decision.Request;
        db.McpOperatorIdempotencyRecords.Add(new McpOperatorIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            Environment = request.Environment,
            Subject = request.Principal.Subject,
            ClientId = request.Principal.ClientId!,
            TenantId = request.TenantId,
            AgentId = request.AgentId,
            TargetSetDigest = request.TargetSetDigest!,
            PolicyId = decision.MatchingPolicyIds.Single(),
            PolicyVersion = decision.SelectedPolicyVersion!.Value,
            OperationFamily = McpOperatorOperationFamily.FileWrite,
            Operation = request.Operation,
            IdempotencyKey = plan.IdempotencyKey,
            PayloadHash = PayloadHash,
            Outcome = McpOperatorIdempotencyOutcome.Succeeded,
            ResultReference = "file-write-42",
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            Version = 1
        });
        await db.SaveChangesAsync(Ct);

        var result = await service.ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey, PayloadHash, decision), Ct);

        result.IsNewDispatch.Should().BeTrue();
        result.FailureCode.Should().BeNull();
    }

    private static async Task<McpOperatorDecision> AllowedDecisionAsync(OrchestratorDbContext db)
    {
        var request = new McpOperatorAccessRequest(
            McpOperatorEnvironment.Development,
            new McpOperatorPrincipal("operator@example.test", "oauth-client", "oauth-client", Set("netratel-operators"), Set("AutomationOperator"), Set("netratel.mcp.execute")),
            42,
            Guid.Parse("bb69ba7b-1e56-4ea8-bc36-533950b50610"),
            McpOperatorTargetClassification.DevelopmentSafe,
            McpOperatorOperationFamily.TerminalExecute,
            "terminal/open",
            Set("netratel.mcp.execute"),
            McpOperatorConfirmationClass.RemoteExecution,
            "corr-42",
            "request-42",
            TargetSetDigest: Digest,
            McpResource: "https://mcp.dev.example/mcp",
            McpInstance: "dev",
            Tool: "netratel_terminal");
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = Guid.NewGuid(),
            Name = "confirmation-test",
            Environment = request.Environment,
            Effect = McpOperatorPolicyEffect.Allow,
            Priority = 0,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
            PrincipalSelectorValue = request.Principal.Subject,
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
            TenantId = request.TenantId,
            AgentId = request.AgentId,
            TargetClassification = request.TargetClassification,
            OperationFamily = request.OperationFamily,
            Operation = request.Operation,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = "policy-admin",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            Version = 1
        });
        await db.SaveChangesAsync(Ct);
        var decision = await new McpOperatorAuthorization(db).EvaluateAsync(request, Ct);
        decision.IsAllowed.Should().BeTrue();
        return decision;
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new OrchestratorDbContext(options);
    }

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
}
