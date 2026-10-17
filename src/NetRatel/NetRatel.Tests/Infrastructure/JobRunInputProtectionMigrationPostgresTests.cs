using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class JobRunInputProtectionMigrationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Upgrade_protects_retained_material_on_inspection_and_refuses_destructive_downgrade()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options);
        var migrator = db.GetService<IMigrator>();
        const string preceding = "20261014203629_AddClientUpdateReconciliationEvidence";
        await migrator.MigrateAsync(preceding, ct);
        const string original = "{\"phrase\":\"garden rain Ω 908\",\"normal\":false}";
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Jobs" ("Id","Name","FolderPath","ClientIdentity","CreatedAtUtc","UpdatedAtUtc")
            VALUES (41,'owned inert history','/','','2026-10-01T10:00:00Z','2026-10-01T10:00:00Z');
            INSERT INTO "JobRuns" ("Id","JobId","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc","InputsJson")
            VALUES (51,41,'','fixture',2,0,'2026-10-01T10:00:00Z',{0});
            """, new object[] { original }, ct);
        await db.Database.MigrateAsync(ct);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var protection = new EphemeralDataProtectionProvider();
        var runs = new JobRunService(db, protection);
        var inspected = (await runs.GetAsync(51, ct))!;
        inspected.InputProtectionStatus.Should().Be("legacy-unknown-v1");
        inspected.InputsJson.Should().NotContain("garden rain");
        (await runs.GetExecutionInputsAsync(51, ct)).Should().Be(original);
        (await db.JobRuns.AsNoTracking().SingleAsync(ct)).ProtectedExecutionJson.Should().NotContain("garden rain");
        var failure = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => migrator.MigrateAsync(preceding, ct));
        failure.Message.Should().Contain("Protected run inputs").And.NotContain("garden rain");
    }
}
