using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class JobDefinitionRetirementMigrationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Upgrade_preserves_history_backfills_owner_retirement_and_refuses_resurrection_on_downgrade()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options);
        var migrator = db.GetService<IMigrator>();
        const string preceding = "20261022181626_AddCallbackTerminalRejection";
        await migrator.MigrateAsync(preceding, ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Jobs" ("Id","Name","FolderPath","TenantId","ClientIdentity","CreatedAtUtc","UpdatedAtUtc")
            VALUES (41,'retained plain job','/',7,'','2026-10-01T10:00:00Z','2026-10-01T10:00:00Z'),
                   (42,'legacy retired owner job','/',7,'','2026-10-01T10:00:00Z','2026-10-01T10:01:00Z');
            INSERT INTO "JobRuns" ("Id","JobId","TenantId","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc","CompletedAtUtc")
            VALUES (51,41,7,'','fixture',2,0,'2026-10-01T10:00:00Z','2026-10-01T10:01:00Z');
            """, ct);
        var retiredAt = DateTimeOffset.Parse("2026-10-01T10:01:00Z");
        var policyId = Guid.NewGuid();
        db.McpOperatorPolicies.Add(new() { Id = policyId, Name = "retirement fixture", TenantId = 7,
            PrincipalSelectorValue = "fixture", CreatedBy = "fixture", CreatedAtUtc = retiredAt, Version = 1 });
        db.McpOperatorJobs.Add(new() { Id = Guid.NewGuid(), JobId = 42, TenantId = 7,
            AgentId = Guid.NewGuid(), Subject = "fixture", ClientId = "fixture",
            McpResource = "https://mcp.example/mcp", McpInstance = "fixture", PolicyId = policyId,
            PolicyVersion = 1, TargetSetDigest = new string('a', 64), CreatedAtUtc = retiredAt,
            UpdatedAtUtc = retiredAt, DeletedAtUtc = retiredAt, Version = 2 });
        await db.SaveChangesAsync(ct);

        await db.Database.MigrateAsync(ct);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var definitions = await db.Jobs.IgnoreQueryFilters().AsNoTracking().OrderBy(job => job.Id).ToArrayAsync(ct);
        definitions.Should().HaveCount(2);
        definitions[0].DeletedAtUtc.Should().BeNull();
        definitions[1].DeletedAtUtc.Should().Be(retiredAt);
        var jobs = new JobDefinitionService(db);
        (await jobs.ListAsync(ct)).Should().ContainSingle(job => job.Id == 41);
        (await jobs.DeleteAsync(41, ct)).Should().NotBeNull();
        (await jobs.GetAsync(41, ct)).Should().BeNull();
        (await db.Jobs.IgnoreQueryFilters().CountAsync(ct)).Should().Be(2);
        var runs = new JobRunService(db, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider());
        (await runs.GetAsync(51, ct))!.Status.Should().Be(JobRunState.Succeeded);
        await Assert.ThrowsAsync<JobDefinitionUnavailableException>(() => runs.UpsertRunAsync(new(52, 41, 7,
            "", "fixture", JobRunState.Pending, 0, retiredAt, null, null, null, null, null), ct));

        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(preceding, ct));
        failure.Message.Should().Contain("Retired job definitions prevent downgrade");
        (await db.JobRuns.CountAsync(ct)).Should().Be(1);
    }
}
