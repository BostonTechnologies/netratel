using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpJobTargetDiscoveryTests
{
    [Fact]
    public async Task Bounded_policy_inventory_filters_the_complete_result_and_groups_before_pagination()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase("job-discovery-" + Guid.NewGuid()).Options);
        var nodes = Enumerable.Range(1, 5).Select(index => new Agent
        {
            Id = Guid.NewGuid(), TenantId = 7, Name = $"node-{index}", Tags = ["TestSystem"],
            DeviceInfoJson = JsonSerializer.Serialize(new { hostName = $"lab-node-{index:00}" }), IsEnabled = true,
            CreatedAtUtc = DateTimeOffset.UtcNow
        }).ToArray();
        db.Tenants.Add(new Tenant { Id = 7, Name = "Example tenant" });
        db.Agents.AddRange(nodes);
        foreach (var node in nodes.Take(3))
            db.McpOperatorPolicies.Add(new()
            {
                Id = Guid.NewGuid(), Name = "Exact allowed inventory", Environment = McpOperatorEnvironment.Development,
                Effect = McpOperatorPolicyEffect.Allow, PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject,
                PrincipalSelectorValue = "target-reader", TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent,
                TenantId = 7, AgentId = node.Id, OperationFamily = McpOperatorOperationFamily.AutomationRead,
                ConstraintsJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, CreatedBy = "test-admin", Version = 1
            });
        var permittedGroup = new ExecutionGroupRecord { Id = Guid.NewGuid(), TenantId = 7, Name = "Allowed group", NormalizedName = "ALLOWED GROUP" };
        var deniedGroup = new ExecutionGroupRecord { Id = Guid.NewGuid(), TenantId = 7, Name = "Other group", NormalizedName = "OTHER GROUP" };
        db.ExecutionGroups.AddRange(permittedGroup, deniedGroup);
        db.ExecutionGroupMembers.AddRange(
            new() { GroupId = permittedGroup.Id, TenantId = 7, AgentId = nodes[0].Id },
            new() { GroupId = permittedGroup.Id, TenantId = 7, AgentId = nodes[1].Id },
            new() { GroupId = deniedGroup.Id, TenantId = 7, AgentId = nodes[0].Id },
            new() { GroupId = deniedGroup.Id, TenantId = 7, AgentId = nodes[4].Id });
        await db.SaveChangesAsync();
        var directory = new JobNodeDirectory(db);
        var reader = new McpJobTargetPolicyReader(db, new McpOperatorSearchAuthorization(db));
        var principal = new McpOperatorPrincipal("target-reader", "reader-client", null, new HashSet<string>(),
            new HashSet<string> { "Operator" }, new HashSet<string> { "netratel.mcp.observe" });
        var allowed = await reader.AllowedAsync(new McpJobTargetPolicyContext(McpOperatorEnvironment.Development,
            principal, "relay", "https://mcp.example.test/mcp", "dev", "netratel_job_targets", "preview", 7,
            principal.Scopes, "discovery", "read-1"), await directory.ReadAsync(7, null, false, default), default);
        allowed.Keys.Should().BeEquivalentTo(nodes.Take(3).Select(node => node.Id));
        var authorized = allowed.Keys.ToHashSet();
        var access = new ReadOnlyJobAccess();
        var user = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "reader")], "test"));
        var resolver = new JobTargetResolver(db, directory);
        var api = new JobTargetApiService(directory, resolver, access);
        var first = await api.PreviewAsync(7, new("tags: TestSystem", PageSize: 2), user, default, authorized);
        var second = await api.PreviewAsync(7, new("tags: TestSystem", Page: 2, PageSize: 2), user, default, authorized);
        first.TotalCount.Should().Be(3); first.NextPage.Should().Be(2); second.TotalCount.Should().Be(3);
        first.Items.Concat(second.Items).Select(node => node.AgentId).Should().BeEquivalentTo(authorized);
        var groups = new ExecutionGroupService(db, access, directory, [], TimeProvider.System);
        var groupPage = await groups.ListForTargetsAsync(7, null, 1, 1, authorized, user, default);
        groupPage.TotalCount.Should().Be(2); groupPage.NextPage.Should().Be(2);
        groupPage.Items.Single().Id.Should().Be(permittedGroup.Id);
        var mixedPage = await groups.ListForTargetsAsync(7, null, 2, 1, authorized, user, default);
        mixedPage.Items.Single().Id.Should().Be(deniedGroup.Id);
        mixedPage.Items.Single().MemberCount.Should().Be(1, "denied workers must not leak into visible pool counts");
        var pool = await resolver.ResolveAsync(7, new() { Source = JobTargetSource.ExecutionGroup,
            ExecutionGroupId = deniedGroup.Id }, null, null, null, authorized, default);
        pool.Nodes.Select(node => node.AgentId).Should().Equal(nodes[0].Id);
        (await groups.ListForTargetsAsync(7, null, 1, 1, new HashSet<Guid> { nodes[2].Id }, user, default)).TotalCount.Should().Be(0);
    }

    private sealed class ReadOnlyJobAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(tenantId == 7 && permission == NetRatelPermissions.JobManagement);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectiveAccessSnapshot("reader", false, false, new HashSet<string> { NetRatelPermissions.JobManagement }));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
