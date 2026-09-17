using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class McpOperatorRequestStoreTests
{
    [Fact]
    public async Task Create_is_idempotent_owner_scoped_and_linked_to_an_owned_job()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorRequestStore(db);
        var create = new McpOperatorRequestCreateRequest(fixture.JobId, "Install support package", fixture.Decision, fixture.Audit, Guid.Parse("eb7632f3-8b69-4890-a5e0-8b9df5af31c4"), "corr-request-42", fixture.Now);

        var first = await store.CreateOrGetAsync(create, CancellationToken.None);
        var replay = await store.CreateOrGetAsync(create, CancellationToken.None);
        var hidden = await store.GetOwnedAsync(first.RequestId, fixture.TenantId, fixture.AgentId, fixture.Principal with { Subject = "other@example.test" }, fixture.Resource, fixture.Instance, CancellationToken.None);
        var domain = await db.Requests.SingleAsync();

        first.RequestId.Should().BePositive();
        replay.RequestId.Should().Be(first.RequestId);
        first.JobId.Should().Be(fixture.JobId);
        domain.JobDefinitionId.Should().Be(fixture.JobId.ToString());
        domain.JobInputs.Should().BeNull();
        hidden.Should().BeNull();
        (await db.McpOperatorRequestAudits.ToArrayAsync()).Select(audit => audit.Action).Should().Equal("create");
    }

    [Fact]
    public async Task Lifecycle_is_versioned_redacted_and_terminally_fenced()
    {
        await using var db = CreateDb();
        var fixture = await CreateFixtureAsync(db);
        var store = new McpOperatorRequestStore(db);
        var created = await store.CreateOrGetAsync(new McpOperatorRequestCreateRequest(fixture.JobId, "Apply retained setting", fixture.Decision, fixture.Audit, Guid.Parse("e9b1e798-4d66-4e10-9dac-73bd2cf53899"), "corr-request-42", fixture.Now), CancellationToken.None);
        var claimed = await store.MutateAsync(new McpOperatorRequestMutation(created.RequestId, created.Version, "claim", null, null, "worker-42", fixture.Decision, fixture.Audit, fixture.Now.AddMinutes(1)), CancellationToken.None);
        var completed = await store.MutateAsync(new McpOperatorRequestMutation(created.RequestId, claimed!.Version, "complete", null, "token=super-secret", null, fixture.Decision, fixture.Audit, fixture.Now.AddMinutes(2)), CancellationToken.None);
        var terminal = () => store.MutateAsync(new McpOperatorRequestMutation(created.RequestId, completed!.Version, "cancel", null, null, null, fixture.Decision, fixture.Audit, fixture.Now.AddMinutes(3)), CancellationToken.None);

        completed!.State.Should().Be("Completed");
        completed.ResultSummary.Should().NotContain("super-secret");
        await terminal.Should().ThrowAsync<McpOperatorRequestLimitException>().Where(exception => exception.Code == "request_terminal_conflict");
        (await db.McpOperatorRequestAudits.OrderBy(audit => audit.OccurredAtUtc).ToArrayAsync()).Select(audit => audit.Action).Should().Equal("create", "claim", "complete");
    }

    private static async Task<Fixture> CreateFixtureAsync(OrchestratorDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        const int tenantId = 42;
        const string resource = "https://mcp.prod.example/mcp";
        const string instance = "prod";
        var agentId = Guid.Parse("a2f006cc-418b-47fe-8308-9a13aa5c5150");
        var policyId = Guid.Parse("70cefc92-967b-4d2f-bf1a-52d3f93490fd");
        var auditId = Guid.Parse("b2546b1b-60c0-4e2a-9581-00e71fa9bf50");
        var principal = new McpOperatorPrincipal("operator@example.test", "operator-client", "operator-client", Set(), Set(), Set("netratel.mcp.write"));
        var digest = Hash("tenant-42-request-agent");
        var access = new McpOperatorAccessRequest(McpOperatorEnvironment.Production, principal, tenantId, agentId,
            McpOperatorTargetClassification.ManagedStandard, McpOperatorOperationFamily.Requests, "netratel_requests/create",
            Set("netratel.mcp.write"), McpOperatorConfirmationClass.StandardMutation, "corr-request-42", "request-request-42", digest,
            McpResource: resource, McpInstance: instance, Tool: "netratel_requests");
        var decision = new McpOperatorDecision(true, null, null, [policyId], new McpOperatorConstraints(), digest, access, 5);
        var audit = new McpOperatorAcceptedAudit(auditId, policyId, access.Environment, "netratel-mcp-http-prod", principal.Subject,
            principal.ClientId, principal.AuthorizedParty, [], [], principal.Scopes.Order(StringComparer.Ordinal).ToArray(), resource, instance,
            access.Tool, tenantId, agentId, access.OperationFamily, access.Operation, access.CorrelationId, access.RequestId, now);
        var job = new JobDefinition { Name = "owned-request-job", FolderPath = "/operator", ClientIdentity = $"agent:{agentId:D}", TenantId = tenantId, AgentId = agentId, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        db.McpOperatorJobs.Add(new McpOperatorJobRecord
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            TenantId = tenantId,
            AgentId = agentId,
            Subject = principal.Subject,
            ClientId = principal.ClientId!,
            McpResource = resource,
            McpInstance = instance,
            PolicyId = policyId,
            PolicyVersion = 5,
            TargetSetDigest = digest,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Version = 1
        });
        await db.SaveChangesAsync();
        return new Fixture(now, tenantId, agentId, job.Id, principal, decision, audit, resource, instance);
    }

    private static OrchestratorDbContext CreateDb() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
        .Options);

    private static IReadOnlySet<string> Set(params string[] values) => new HashSet<string>(values, StringComparer.Ordinal);
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed record Fixture(DateTimeOffset Now, int TenantId, Guid AgentId, long JobId, McpOperatorPrincipal Principal,
        McpOperatorDecision Decision, McpOperatorAcceptedAudit Audit, string Resource, string Instance);
}
