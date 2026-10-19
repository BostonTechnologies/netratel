using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperationObjectTargetResolverTests
{
    [Theory]
    [InlineData("netratel_jobs", "delete", false)]
    [InlineData("netratel_jobs", "update", false)]
    [InlineData("netratel_jobs", "param_add", false)]
    [InlineData("netratel_jobs", "delete", true)]
    [InlineData("netratel_job_runs", "delete", false)]
    [InlineData("netratel_job_runs", "cancel", false)]
    [InlineData("netratel_job_runs", "delete", true)]
    public async Task Tombstones_require_an_exact_successful_owned_receipt_and_remain_hidden_to_ordinary_operations(string tool, string operation, bool legacy)
    {
        await using var db = CreateDb();
        var delegation = await SeedDeletedReceiptAsync(db, tool, operation);
        if (legacy)
        {
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = delegation.ObjectReference;
            await db.SaveChangesAsync();
        }
        var resolver = new McpOperationObjectTargetResolver(db);

        (await resolver.MatchesDelegationAsync(delegation, default)).Should().BeTrue();
        (await resolver.MatchesDelegationAsync(delegation with { ReplayBinding = null }, default)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(delegation with { Operation = "get", ReplayBinding = null }, default)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(delegation with { TenantId = 43 }, default)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(delegation with { AgentId = Guid.NewGuid() }, default)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(delegation with { ObjectTargetResolutionEnabled = false }, default)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(delegation with { IngressCredentialId = null }, default)).Should().BeFalse();
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("unconsumed")]
    [InlineData("unlinked")]
    [InlineData("pending")]
    [InlineData("failed")]
    [InlineData("incomplete")]
    [InlineData("result-object")]
    [InlineData("result-shape")]
    [InlineData("owner")]
    [InlineData("client")]
    [InlineData("credential")]
    [InlineData("resource")]
    [InlineData("instance")]
    [InlineData("payload")]
    [InlineData("key")]
    [InlineData("operation")]
    [InlineData("family")]
    [InlineData("confirmation-class")]
    [InlineData("receipt-owner")]
    [InlineData("receipt-client")]
    [InlineData("receipt-tenant")]
    [InlineData("receipt-agent")]
    [InlineData("receipt-target")]
    [InlineData("receipt-policy")]
    [InlineData("receipt-operation")]
    [InlineData("receipt-key")]
    [InlineData("receipt-payload")]
    [InlineData("receipt-environment")]
    [InlineData("policy-disabled")]
    [InlineData("policy-version")]
    [InlineData("policy-expired")]
    public async Task A_deleted_object_cannot_be_admitted_by_another_identity_or_a_partial_stale_or_unrelated_receipt(string change)
    {
        await using var db = CreateDb();
        var delegation = await SeedDeletedReceiptAsync(db, "netratel_jobs", "delete");
        var plan = await db.McpOperatorConfirmationPlans.SingleAsync();
        var receipt = await db.McpOperatorIdempotencyRecords.SingleAsync();
        var policy = await db.McpOperatorPolicies.SingleAsync();
        switch (change)
        {
            case "expired": plan.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1); break;
            case "unconsumed": plan.ConsumedAtUtc = null; break;
            case "unlinked": plan.ConsumedIdempotencyId = Guid.NewGuid(); break;
            case "pending": receipt.Outcome = McpOperatorIdempotencyOutcome.Pending; break;
            case "failed": receipt.Outcome = McpOperatorIdempotencyOutcome.Failed; break;
            case "incomplete": receipt.CompletedAtUtc = null; break;
            case "result-object": receipt.ResultReference = "job-mutation:2001:2:"; break;
            case "result-shape": receipt.ResultReference = "job-mutation:1001:invalid:"; break;
            case "owner": delegation = delegation with { Identity = delegation.Identity with { Subject = "another-owner" } }; break;
            case "client": delegation = delegation with { Identity = delegation.Identity with { ClientId = "another-client" }, IngressCredentialId = "another-client" }; break;
            case "credential": delegation = delegation with { IngressCredentialId = "another-credential" }; break;
            case "resource": delegation = delegation with { Resource = "https://other.example.test/mcp" }; break;
            case "instance": delegation = delegation with { Instance = "dev" }; break;
            case "payload": delegation = delegation with { ReplayBinding = delegation.ReplayBinding! with { PayloadHash = McpOperatorReplayBinding.Hash("another-payload") } }; break;
            case "key": delegation = delegation with { ReplayBinding = delegation.ReplayBinding! with { IdempotencyKeyHash = McpOperatorReplayBinding.Hash("another-key") } }; break;
            case "operation": plan.Operation = "netratel_jobs/update"; break;
            case "family": plan.OperationFamily = McpOperatorOperationFamily.JobExecution; break;
            case "confirmation-class": plan.ConfirmationClass = McpOperatorConfirmationClass.None; break;
            case "receipt-owner": receipt.Subject = "another-owner"; break;
            case "receipt-client": receipt.ClientId = "another-client"; break;
            case "receipt-tenant": receipt.TenantId++; break;
            case "receipt-agent": receipt.AgentId = Guid.NewGuid(); break;
            case "receipt-target": receipt.TargetSetDigest = "another-target"; break;
            case "receipt-policy": receipt.PolicyVersion++; break;
            case "receipt-operation": receipt.Operation = "netratel_jobs/update"; break;
            case "receipt-key": receipt.IdempotencyKey = "another-key"; break;
            case "receipt-payload": receipt.PayloadHash = McpOperatorReplayBinding.Hash("another-payload"); break;
            case "receipt-environment": receipt.Environment = McpOperatorEnvironment.Development; break;
            case "policy-disabled": policy.DisabledAtUtc = DateTimeOffset.UtcNow; break;
            case "policy-version": policy.Version++; break;
            case "policy-expired": policy.ExpiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(-1); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
        await db.SaveChangesAsync();

        (await new McpOperationObjectTargetResolver(db).MatchesDelegationAsync(delegation, default)).Should().BeFalse();
    }

    [Fact]
    public async Task Deleted_run_receipts_cannot_borrow_another_job_records_identity_or_result()
    {
        await using var db = CreateDb();
        var delegation = await SeedDeletedReceiptAsync(db, "netratel_job_runs", "delete");
        var receipt = await db.McpOperatorIdempotencyRecords.SingleAsync();
        receipt.ResultReference = "job-run-action:1001:Succeeded";
        await db.SaveChangesAsync();
        var resolver = new McpOperationObjectTargetResolver(db);
        (await resolver.MatchesDelegationAsync(delegation, default)).Should().BeFalse();
        receipt.ResultReference = "job-run-action:1002:Succeeded";
        (await db.McpOperatorJobRuns.SingleAsync()).JobRecordId = Guid.NewGuid();
        await db.SaveChangesAsync();
        (await resolver.MatchesDelegationAsync(delegation, default)).Should().BeFalse();
    }

    private static async Task<McpOperatorDelegation> SeedDeletedReceiptAsync(OrchestratorDbContext db, string tool, string operation)
    {
        var now = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();
        var policyId = Guid.NewGuid();
        var receiptId = Guid.NewGuid();
        var jobRecordId = Guid.NewGuid();
        var descriptor = McpOperatorOperationCatalog.Find(tool, operation)!;
        var binding = new McpOperatorReplayBinding(McpOperatorReplayBinding.Hash("original-plan"),
            McpOperatorReplayBinding.Hash("original-idempotency-key"), McpOperatorReplayBinding.Hash("original-canonical-payload"));
        var delegation = Delegation(tool, operation, tool == "netratel_jobs" ? "1001" : "1002", 42, agentId) with
        { ObjectTargetResolutionEnabled = true, IngressCredentialId = "credential", ReplayBinding = binding };
        db.McpOperatorJobs.Add(new McpOperatorJobRecord
        {
            Id = jobRecordId, JobId = 1001, TenantId = 42, AgentId = agentId, Subject = "local-owner", ClientId = "credential",
            McpResource = delegation.Resource!, McpInstance = "prod", TargetSetDigest = "owned-target", DeletedAtUtc = now, Version = 2
        });
        db.McpOperatorJobRuns.Add(new McpOperatorJobRunRecord
        {
            Id = Guid.NewGuid(), JobRunId = 1002, JobRecordId = jobRecordId, JobId = 1001, TenantId = 42, AgentId = agentId,
            TargetSetDigest = "owned-target", DeletedAtUtc = now, Version = 2
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Environment = McpOperatorEnvironment.Production, Effect = McpOperatorPolicyEffect.Allow,
            OperationFamily = descriptor.OperationFamily, Version = 3
        });
        db.McpOperatorConfirmationPlans.Add(new McpOperatorConfirmationPlanRecord
        {
            Id = Guid.NewGuid(), TokenHash = binding.PlanTokenHash, Environment = McpOperatorEnvironment.Production,
            Subject = "local-owner", ClientId = "credential", McpResource = delegation.Resource, McpInstance = "prod",
            TenantId = 42, AgentId = agentId, TargetSetDigest = "owned-target", PolicyId = policyId, PolicyVersion = 3,
            OperationFamily = descriptor.OperationFamily, Operation = $"{tool}/{operation}", ConfirmationClass = descriptor.ConfirmationClass,
            PayloadHash = binding.PayloadHash, IdempotencyKey = "original-idempotency-key", CreatedAtUtc = now.AddMinutes(-1),
            ExpiresAtUtc = now.AddMinutes(2), ConsumedAtUtc = now, ConsumedIdempotencyId = receiptId, Version = 2
        });
        db.McpOperatorIdempotencyRecords.Add(new McpOperatorIdempotencyRecord
        {
            Id = receiptId, Environment = McpOperatorEnvironment.Production, Subject = "local-owner", ClientId = "credential",
            TenantId = 42, AgentId = agentId, TargetSetDigest = "owned-target", PolicyId = policyId, PolicyVersion = 3,
            OperationFamily = descriptor.OperationFamily, Operation = $"{tool}/{operation}", PayloadHash = binding.PayloadHash,
            IdempotencyKey = "original-idempotency-key", Outcome = McpOperatorIdempotencyOutcome.Succeeded, CreatedAtUtc = now,
            CompletedAtUtc = now, ResultReference = tool == "netratel_jobs" ? "job-mutation:1001:2:17" : "job-run-action:1002:Succeeded", Version = 2
        });
        await db.SaveChangesAsync();
        return delegation;
    }

    [Fact]
    public async Task Policy_reference_ownership_is_resolved_from_persistence_including_tenant_only_selectors()
    {
        await using var db = CreateDb();
        var policyId = Guid.Parse("22222222-2222-4222-8222-222222222222");
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord { Id = policyId, TenantId = 17 });
        await db.SaveChangesAsync();
        var resolver = new McpOperationObjectTargetResolver(db);
        var resolved = await resolver.ResolvePolicyTargetAsync(policyId.ToString("D"), CancellationToken.None);
        resolved.Should().Be(new McpOperationResolvedPolicyTarget(17, null));
        var delegation = Delegation("netratel_policy", "policy", policyId.ToString("D"), 17, Guid.NewGuid()) with { AgentId = null };
        (await resolver.MatchesDelegationAsync(delegation, CancellationToken.None)).Should().BeTrue();
        (await resolver.MatchesDelegationAsync(delegation with { TenantId = 29 }, CancellationToken.None)).Should().BeFalse();
        (await resolver.ResolvePolicyTargetAsync(Guid.NewGuid().ToString("D"), CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData("netratel_jobs", "get", "1001")]
    [InlineData("netratel_job_runs", "get", "1002")]
    [InlineData("netratel_tasks", "get", "1003")]
    [InlineData("netratel_requests", "get", "1004")]
    public async Task Durable_object_reference_must_match_its_persisted_tenant_and_agent_owner(
        string tool,
        string operation,
        string objectReference)
    {
        await using var db = CreateDb();
        var tenantId = 42;
        var agentId = Guid.Parse("d1fa8c2e-3c95-4b4d-8f69-2492b0e6b9b2");
        await SeedOwnersAsync(db, tenantId, agentId);
        var resolver = new McpOperationObjectTargetResolver(db);

        var matching = Delegation(tool, operation, objectReference, tenantId, agentId);
        var crossTenant = Delegation(tool, operation, objectReference, tenantId + 1, agentId);
        var wrongAgent = Delegation(tool, operation, objectReference, tenantId, Guid.NewGuid());

        (await resolver.MatchesDelegationAsync(matching, CancellationToken.None)).Should().BeTrue();
        (await resolver.MatchesDelegationAsync(crossTenant, CancellationToken.None)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(wrongAgent, CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Required_object_reference_cannot_be_omitted_or_resolved_from_a_different_operation_family()
    {
        await using var db = CreateDb();
        var agentId = Guid.NewGuid();
        await SeedOwnersAsync(db, 42, agentId);
        var resolver = new McpOperationObjectTargetResolver(db);

        (await resolver.MatchesDelegationAsync(Delegation("netratel_jobs", "get", null, 42, agentId), CancellationToken.None)).Should().BeFalse();
        (await resolver.MatchesDelegationAsync(Delegation("netratel_jobs", "get", "1002", 42, agentId), CancellationToken.None)).Should().BeFalse();
    }

    private static McpOperatorDelegation Delegation(string tool, string operation, string? objectReference, int tenantId, Guid agentId)
        => new(
            new McpOperatorDelegationIdentity("local-owner", "credential", "netratel-local-http-mcp", [], [], []),
            "netratel-mcp-http",
            tool,
            operation,
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.UtcNow.AddMinutes(1),
            "https://mcp.example.test/mcp",
            "prod",
            tenantId,
            agentId)
        {
            ObjectReference = objectReference
        };

    private static async Task SeedOwnersAsync(OrchestratorDbContext db, int tenantId, Guid agentId)
    {
        db.McpOperatorJobs.Add(new McpOperatorJobRecord { Id = Guid.NewGuid(), JobId = 1001, TenantId = tenantId, AgentId = agentId });
        db.McpOperatorJobRuns.Add(new McpOperatorJobRunRecord { Id = Guid.NewGuid(), JobRunId = 1002, JobId = 1001, TenantId = tenantId, AgentId = agentId });
        db.McpOperatorTasks.Add(new McpOperatorTaskRecord { Id = Guid.NewGuid(), TaskActivityId = 1003, TenantId = tenantId, AgentId = agentId });
        db.McpOperatorRequests.Add(new McpOperatorRequestRecord { Id = Guid.NewGuid(), RequestId = 1004, TenantId = tenantId, AgentId = agentId });
        await db.SaveChangesAsync();
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);
}
