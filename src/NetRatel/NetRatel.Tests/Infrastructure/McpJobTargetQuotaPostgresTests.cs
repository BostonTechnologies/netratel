using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class McpJobTargetQuotaPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Concurrent_whole_plan_confirmations_cannot_each_consume_the_same_remaining_policy_quota()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        await using var setup = new OrchestratorDbContext(options);
        await setup.Database.MigrateAsync(ct);
        var policyId = Guid.NewGuid();
        var principal = new McpOperatorPrincipal("quota-operator", "quota-client", "quota-client", new HashSet<string>(),
            new HashSet<string> { "AutomationOperator" }, new HashSet<string> { "netratel.mcp.execute" });
        var request = new McpOperatorAccessRequest(McpOperatorEnvironment.Development, principal, 42, Guid.NewGuid(),
            McpOperatorTargetClassification.DevelopmentSafe, McpOperatorOperationFamily.JobExecution, "netratel_job_runs/start",
            principal.Scopes, McpOperatorConfirmationClass.RemoteExecution, "quota-correlation", "quota-request",
            new string('A', 64), McpResource: "https://mcp.example.test/mcp", McpInstance: "dev", Tool: "netratel_job_runs");
        setup.McpOperatorPolicies.Add(new()
        {
            Id = policyId, Name = "Bounded reviewed targets", Environment = request.Environment,
            Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
            PrincipalSelectorValue = principal.Subject, TargetSelectorKind = McpOperatorTargetSelectorKind.Tenant, TenantId = 42,
            OperationFamily = request.OperationFamily, Operation = request.Operation,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(MaxJobTargetCount: 10, MaxFanOut: 10)
            { MaxConcurrentJobTargets = 3 }, McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "quota-admin", Version = 1
        });
        await setup.SaveChangesAsync(ct);
        var decision = await new McpOperatorAuthorization(setup).EvaluateAsync(request, ct);
        decision.IsAllowed.Should().BeTrue();
        var service = new McpOperatorConfirmationService(setup);
        var first = await service.CreatePlanAsync(new(decision, new string('B', 64)) { JobRequestHash = new string('C', 64) }, ct);
        var second = await service.CreatePlanAsync(new(decision, new string('D', 64)) { JobRequestHash = new string('E', 64) }, ct);

        async Task<McpOperatorConfirmationAdmission> Confirm(McpOperatorConfirmationPlan plan, char payload)
        {
            await using var db = new OrchestratorDbContext(options);
            return await new McpOperatorConfirmationService(db).ConfirmAsync(new(plan.PlanToken, plan.IdempotencyKey,
                new string(payload, 64), decision)
            { JobTargetPlanDigest = new string('F', 64), JobTargetQuotas = [new(policyId, 1, 2, 3)] }, ct);
        }
        var outcomes = await Task.WhenAll(Confirm(first, 'B'), Confirm(second, 'D'));
        outcomes.Count(result => result.IsNewDispatch).Should().Be(1);
        outcomes.Count(result => result.FailureCode == "job_target_quota_exhausted").Should().Be(1);
        setup.ChangeTracker.Clear();
        (await setup.Set<McpJobTargetReservationRecord>().SingleAsync(ct)).TargetCount.Should().Be(2);
        (await setup.McpOperatorIdempotencyRecords.CountAsync(ct)).Should().Be(1);
        (await setup.McpOperatorConfirmationPlans.CountAsync(plan => plan.ConsumedAtUtc != null, ct)).Should().Be(1);
        var winner = outcomes[0].IsNewDispatch ? first : second;
        var repeated = await Confirm(winner, outcomes[0].IsNewDispatch ? 'B' : 'D');
        repeated.IsReplay.Should().BeTrue();
        repeated.IdempotencyId.Should().Be(outcomes.Single(result => result.IsNewDispatch).IdempotencyId);
        (await setup.Set<McpJobTargetReservationRecord>().CountAsync(ct)).Should().Be(1);
    }
}
