using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

/// <summary>One PostgreSQL server for this class, with an isolated migrated database per case.</summary>
public sealed class ExecutionGroupPostgresTests(PostgreSqlPersistenceFixture postgres) : IClassFixture<PostgreSqlPersistenceFixture>
{
    private static readonly ClaimsPrincipal Manager = new(new ClaimsIdentity([new("netratel_principal_id", "synthetic-group-manager")], "test"));

    [Fact]
    public async Task Empty_group_roundtrips_five_members_rename_revision_and_audit_without_granting_execution()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var db = await CreateAsync(ct);
        var ids = await SeedAsync(db, ct);
        var service = Service(db);
        ExecutionGroupDetailsDto created;
        await using (var reviewedMutation = await db.Database.BeginTransactionAsync(ct))
        {
            created = await service.CreateAsync(7, new("Test systems", null, true, false, []), Manager, ct);
            await reviewedMutation.CommitAsync(ct);
        }
        created.Group.MemberCount.Should().Be(0);
        var saved = await service.UpdateAsync(7, created.Group.Id, new("Five test systems", "synthetic", true, false, ids, created.Group.Revision), Manager, ct);
        saved!.Group.Id.Should().Be(created.Group.Id); saved.Group.Revision.Should().Be(2);
        saved.Members.Should().HaveCount(5); saved.Members.Select(x => x.AgentId).Should().BeEquivalentTo(ids);
        (await service.GetAsync(7, created.Group.Id, Manager, ct))!.Members.Select(x => x.AgentId).Should().BeEquivalentTo(ids);
        (await db.ExecutionGroupAudits.OrderBy(x => x.Revision).Select(x => x.Operation).ToArrayAsync(ct)).Should().Equal("created", "updated");
        (await new ManagerAccess().AuthorizeAsync(Manager, NetRatelPermissions.ScriptExecute, 7, ct)).Should().BeFalse();
        var duplicate = await Assert.ThrowsAsync<JobTargetValidationException>(() => service.UpdateAsync(7, created.Group.Id,
            new("Five test systems", null, true, false, [ids[0], ids[0]], saved.Group.Revision), Manager, ct));
        duplicate.Code.Should().Be("execution_group_duplicate_member");
        (await service.GetAsync(7, created.Group.Id, Manager, ct))!.Group.Revision.Should().Be(2);
        await Assert.ThrowsAsync<JobTargetValidationException>(() => service.UpdateAsync(7, created.Group.Id,
            new("stale", null, true, false, [], 1), Manager, ct));
        (await service.GetAsync(7, created.Group.Id, Manager, ct))!.Group.MemberCount.Should().Be(5);
        var page = await service.ListAsync(7, "FIVE", 1, 1, Manager, ct);
        page.TotalCount.Should().Be(1); page.Items[0].MemberCount.Should().Be(5);
    }

    [Fact]
    public async Task Tenant_constraints_duplicate_names_and_atomic_conflicting_revisions_are_enforced_by_postgres()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        // Upgrade populated group data, then prove mutable Agent metadata remains
        // guarded by PostgreSQL tenant constraints rather than an immutable EF key.
        await using var db = await CreateAsync(ct, "20261110054634_AddExecutionGroups"); var ids = await SeedAsync(db, ct);
        var group = await Service(db).CreateAsync(7, new("Case sensitive name", null, true, false, ids), Manager, ct);
        await db.Database.MigrateAsync(ct);
        (await db.ExecutionGroupMembers.CountAsync(ct)).Should().Be(5);
        await using (var relocation = NewDb(db.Database.GetConnectionString()!))
        {
            var member = await relocation.Agents.SingleAsync(agent => agent.Id == ids[0], ct);
            member.TenantId = 8;
            var rejected = await Assert.ThrowsAsync<DbUpdateException>(() => relocation.SaveChangesAsync(ct));
            var postgresError = rejected.InnerException.Should().BeOfType<PostgresException>().Which;
            postgresError.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
            postgresError.ConstraintName.Should().Be("FK_ExecutionGroupMembers_Agents_TenantId_AgentId");
        }
        await using (var other = NewDb(db.Database.GetConnectionString()!))
        {
            (await Assert.ThrowsAsync<JobTargetValidationException>(() => Service(other).CreateAsync(7,
                new("  CASE SENSITIVE NAME  ", null, true, false, []), Manager, ct))).Code.Should().Be("execution_group_name_conflict");
        }
        var foreignId = Guid.NewGuid();
        db.Agents.Add(new() { Id = foreignId, TenantId = 8, Name = "foreign", CreatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(ct);
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => Service(db).UpdateAsync(7, group.Group.Id,
            new("foreign", null, true, false, [ids[0], foreignId], group.Group.Revision), Manager, ct))).Code.Should().Be("execution_group_member_unavailable");
        var foreignConstraint = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ExecutionGroupMembers" ("GroupId","TenantId","AgentId") VALUES ({{group.Group.Id}},7,{{foreignId}});
            """, ct));
        foreignConstraint.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        var duplicateConstraint = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "ExecutionGroupMembers" ("GroupId","TenantId","AgentId") VALUES ({{group.Group.Id}},7,{{ids[0]}});
            """, ct));
        duplicateConstraint.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        var connection = db.Database.GetConnectionString()!;
        async Task<bool> Attempt(string name, Guid member)
        {
            await using var concurrent = NewDb(connection);
            try { await Service(concurrent).UpdateAsync(7, group.Group.Id, new(name, null, true, false, [member], group.Group.Revision), Manager, ct); return true; }
            catch (JobTargetValidationException error) when (error.Code == "execution_group_revision_conflict") { return false; }
        }
        var results = await Task.WhenAll(Attempt("first edit", ids[0]), Attempt("second edit", ids[1]));
        results.Count(result => result).Should().Be(1);
        db.ChangeTracker.Clear();
        var current = (await Service(db).GetAsync(7, group.Group.Id, Manager, ct))!;
        current.Group.Revision.Should().Be(2); current.Members.Should().ContainSingle();
        (await db.ExecutionGroupAudits.CountAsync(ct)).Should().Be(2);
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_members_remain_visible_fresh_resolution_changes_but_admitted_snapshot_and_references_are_retained()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var db = await CreateAsync(ct); var ids = await SeedAsync(db, ct); var service = Service(db);
        var group = await service.CreateAsync(7, new("TestSystem", null, true, false, ids.Take(4).ToArray()), Manager, ct);
        var target = new JobTargetDefinition { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = group.Group.Id };
        var resolver = new JobTargetResolver(db, new JobNodeDirectory(db));
        var admitted = await resolver.ResolveAsync(7, target, null, null, null, null, ct);
        await service.ChangeMembershipAsync(7, group.Group.Id, new(1, [ids[4]], []), Manager, ct);
        (await resolver.ResolveAsync(7, target, null, null, null, null, ct)).Nodes.Should().HaveCount(5);
        admitted.Nodes.Should().HaveCount(4); admitted.GroupRevision.Should().Be(1);
        var first = await db.Agents.SingleAsync(x => x.Id == ids[0], ct); first.IsEnabled = false; await db.SaveChangesAsync(ct);
        (await service.GetAsync(7, group.Group.Id, Manager, ct))!.Members.Should().Contain(x => x.AgentId == ids[0] && !x.IsEnabled);
        var renamed = await service.UpdateAsync(7, group.Group.Id, new("Renamed with unavailable member", null, true, false, ids, 2), Manager, ct);
        renamed!.Group.Revision.Should().Be(3);
        renamed.Members.Should().HaveCount(5).And.Contain(x => x.AgentId == ids[0] && !x.IsEnabled);
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(7, target, null, null, null, null, ct))).Code.Should().Be("target_node_unavailable");
        first.IsEnabled = true; await db.SaveChangesAsync(ct);
        db.Jobs.Add(new() { Id = 41, Name = "Referenced job", TenantId = 7, TargetJson = JsonSerializer.Serialize(target), CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(ct);
        var blockedJob = await Assert.ThrowsAsync<ExecutionGroupReferencedException>(() => service.DeleteAsync(7, group.Group.Id, 3, Manager, ct));
        blockedJob.References.Should().Contain(x => x.Kind == "job" && x.Id == "41");
        (await db.Jobs.SingleAsync(ct)).DeletedAtUtc = DateTimeOffset.UtcNow;
        var flowId = Guid.NewGuid();
        db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 7, Name = "Retained Flow", DraftJson = "{}", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        db.FlowVersions.Add(new() { Id = Guid.NewGuid(), TenantId = 7, FlowId = flowId, VersionNumber = 1, PublishedBy = "fixture", PublishedAtUtc = DateTimeOffset.UtcNow,
            GraphJson = JsonSerializer.Serialize(new { nodes = new[] { new { executionAction = new { jobTarget = new { source = 1, executionGroupId = group.Group.Id } } } } }) });
        await db.SaveChangesAsync(ct);
        (await Assert.ThrowsAsync<ExecutionGroupReferencedException>(() => service.DeleteAsync(7, group.Group.Id, 3, Manager, ct))).References.Should().Contain(x => x.Kind == "flow-version");
        var archived = await service.UpdateAsync(7, group.Group.Id, new("TestSystem archived", null, false, true, ids, 3), Manager, ct);
        archived!.Group.Archived.Should().BeTrue();
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => resolver.ResolveAsync(7, target, null, null, null, null, ct))).Code.Should().Be("target_group_unavailable");
    }

    private async Task<OrchestratorDbContext> CreateAsync(CancellationToken ct, string? migration = null)
    {
        var db = NewDb(await postgres.CreateDatabaseAsync(ct));
        if (migration is null) await db.Database.MigrateAsync(ct);
        else await db.GetService<IMigrator>().MigrateAsync(migration, ct);
        return db;
    }
    private static OrchestratorDbContext NewDb(string connection) => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
    private static async Task<Guid[]> SeedAsync(OrchestratorDbContext db, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        db.Tenants.AddRange(new Tenant { Id = 7, Name = "Synthetic lab", CreatedAtUtc = now, UpdatedAtUtc = now }, new Tenant { Id = 8, Name = "Other synthetic tenant", CreatedAtUtc = now, UpdatedAtUtc = now });
        var ids = Enumerable.Range(1, 5).Select(_ => Guid.NewGuid()).ToArray();
        db.Agents.AddRange(ids.Select((id, index) => new Agent { Id = id, TenantId = 7, Name = $"lab-node-{index + 1:00}", Tags = ["TestSystem"], CreatedAtUtc = now,
            DeviceInfoJson = JsonSerializer.Serialize(new { hostName = $"lab-node-{index + 1:00}", os = "Linux", architecture = "x64" }) }));
        await db.SaveChangesAsync(ct); return ids;
    }
    private static ExecutionGroupService Service(OrchestratorDbContext db) => new(db, new ManagerAccess(), new JobNodeDirectory(db), [new PublishedFlowExecutionGroupReferences(db)], TimeProvider.System);
    private sealed class ManagerAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 7 && permission is NetRatelPermissions.TenantAdministration or NetRatelPermissions.JobManagement or NetRatelPermissions.FlowRead);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
