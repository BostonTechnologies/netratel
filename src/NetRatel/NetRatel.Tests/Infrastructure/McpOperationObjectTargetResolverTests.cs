using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperationObjectTargetResolverTests
{
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
