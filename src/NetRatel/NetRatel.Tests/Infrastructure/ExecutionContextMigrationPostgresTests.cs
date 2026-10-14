using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ExecutionContextMigrationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Upgrade_PreservesHistoricalUnknownContextAndNumericStatesWithoutSyntheticReceipts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        var database = await postgres.CreateDatabaseAsync(ct);
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(database).Options);
        await db.GetService<IMigrator>().MigrateAsync("20261013144538_AddLocalClientReleaseDeletion", ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Jobs" ("Id","Name","FolderPath","ClientIdentity","CreatedAtUtc","UpdatedAtUtc")
            VALUES (41,'historical job','/','historical-client','2026-10-01T10:00:00Z','2026-10-01T10:00:00Z');
            INSERT INTO "JobRuns" ("Id","JobId","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc","OptionsJson")
            VALUES (51,41,'historical-client','operator',3,1,'2026-10-01T10:00:00Z','{"historical":true}');
            INSERT INTO "JobTaskActivities" ("Id","RequestId","JobRunId","ClientIdentity","TaskType","Status","CreatedAtUtc","ResultJson")
            VALUES (61,'historical-request',51,'historical-client','exec-library-script','Failed','2026-10-01T10:00:00Z','{"stderr":["original failure"]}');
            """, ct);
        await db.Database.MigrateAsync(ct);
        Assert.False(db.Database.HasPendingModelChanges());
        var run = await db.JobRuns.AsNoTracking().SingleAsync(ct);
        Assert.Equal(3, run.Status); Assert.Null(run.Environment); Assert.Null(run.TargetDisplayName); Assert.Null(run.TargetHostName); Assert.Null(run.StartedAtUtc);
        Assert.Equal("{\"historical\":true}", run.OptionsJson);
        var task = await db.JobTaskActivities.AsNoTracking().SingleAsync(ct);
        Assert.Null(task.Environment); Assert.Null(task.StartedAtUtc); Assert.Null(task.ExitCode); Assert.Null(task.ScriptName); Assert.Null(task.ScriptContentHash);
        Assert.Equal("{\"stderr\":[\"original failure\"]}", task.ResultJson);
        Assert.Equal(0, await db.Set<JobGatewayLifecycleReceiptRecord>().CountAsync(ct));
        Assert.Equal(4, (int)NetRatel.Application.Jobs.JobStepRunState.Skipped);
        Assert.Equal(5, (int)NetRatel.Application.Jobs.JobRunState.TimedOut);
        Assert.Equal(4, (int)NetRatel.Shared.Contracts.Jobs.JobStepRunStatusDto.Skipped);
        Assert.Equal(5, (int)NetRatel.Shared.Contracts.Jobs.JobRunStatusDto.TimedOut);
    }
}
