using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class JobTargetPlansMigrationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Upgrade_retains_incomplete_singletons_and_enforces_parent_node_native_identity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261107100937_AddFlowDeletionAuditCorrelation", ct);
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "Jobs" ("Id","Name","FolderPath","TenantId","ClientIdentity","CreatedAtUtc","UpdatedAtUtc")
            VALUES (41,'legacy incomplete','/',NULL,'historic-name','2026-11-01T10:00:00Z','2026-11-01T10:00:00Z');
            INSERT INTO "JobRuns" ("Id","JobId","TenantId","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc")
            VALUES (51,41,NULL,'historic-name','fixture',2,0,'2026-11-01T10:00:00Z');
            """, ct);
        await db.Database.MigrateAsync(ct);
        db.Database.HasPendingModelChanges().Should().BeFalse();
        var legacy = await db.JobRuns.AsNoTracking().SingleAsync(ct);
        legacy.TenantId.Should().BeNull(); legacy.AgentId.Should().BeNull();
        legacy.ParentRunId.Should().BeNull(); legacy.IsAggregate.Should().BeFalse();
        legacy.ClientIdentity.Should().Be("historic-name");

        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        db.Agents.AddRange(new Agent { Id = first, TenantId = 7 }, new Agent { Id = second, TenantId = 7 });
        await db.SaveChangesAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "JobRuns" ("Id","JobId","TenantId","AgentId","ParentRunId","IsAggregate","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc")
            VALUES (61,41,7,NULL,NULL,true,'','fixture',0,0,'2026-11-01T10:00:00Z'),
                   (62,41,7,{{first}},61,false,'','fixture',0,0,'2026-11-01T10:00:00Z'),
                   (63,41,7,{{second}},61,false,'','fixture',0,0,'2026-11-01T10:00:00Z'),
                   (64,41,8,NULL,NULL,true,'','fixture',0,0,'2026-11-01T10:00:00Z');
            INSERT INTO "JobRunNodes" ("ParentRunId","AgentId","TenantId","Ordinal","NativeRunId","State","TargetJson")
            VALUES (61,{{first}},7,1,62,1,'{}'), (61,{{second}},7,2,63,1,'{}');
            """, ct);

        var duplicate = () => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "JobRunNodes" ("ParentRunId","AgentId","TenantId","Ordinal","State","TargetJson")
            VALUES (61,{{first}},7,3,0,'{}');
            """, ct);
        (await duplicate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        var foreignTenant = () => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "JobRunNodes" ("ParentRunId","AgentId","TenantId","Ordinal","State","TargetJson")
            VALUES (64,{{first}},7,1,0,'{}');
            """, ct);
        (await foreignTenant.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        var foreignAgentTenant = () => db.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO "JobRunNodes" ("ParentRunId","AgentId","TenantId","Ordinal","State","TargetJson")
            VALUES (64,{{first}},8,1,0,'{}');
            """, ct);
        var agentTenantFailure = (await foreignAgentTenant.Should().ThrowAsync<PostgresException>()).Which;
        agentTenantFailure.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        agentTenantFailure.ConstraintName.Should().Be("FK_JobRunNodes_Agents_TenantId_AgentId");
        var substitutedNative = () => db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "JobRunNodes" SET "NativeRunId" = 63 WHERE "ParentRunId" = 61 AND "AgentId" = {{first}};
            """, ct);
        (await substitutedNative.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().BeOneOf(PostgresErrorCodes.ForeignKeyViolation, PostgresErrorCodes.UniqueViolation);
        // Isolate the composite native binding from the unique native ID constraint.
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "JobRunNodes" SET "NativeRunId" = NULL WHERE "ParentRunId" = 61 AND "AgentId" = {{second}};
            """, ct);
        (await substitutedNative.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        (await db.JobRunNodes.CountAsync(ct)).Should().Be(2);
        var inventedParentAgent = () => db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "JobRuns" SET "AgentId" = {{first}} WHERE "Id" = 61;
            """, ct);
        (await inventedParentAgent.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }
}
