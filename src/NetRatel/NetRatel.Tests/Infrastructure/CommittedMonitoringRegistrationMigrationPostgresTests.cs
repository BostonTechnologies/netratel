using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

// Extends the existing provider migration class and fixture. These are schema/history
// regressions; passive fixtures do not prove physical telemetry or execute a Flow.
public sealed partial class CommittedConnectionOwnershipMigrationPostgresTests
{
    private const string PrecedingOwnerMigration = "20261006174907_AddCommittedConnectionOwnership";
    private const string HistoricalMonitoringMigration = "20261002195600_AddMonitoringRuntime";
    private const string CommittedRegistrationMigration = "20261006182927_AddCommittedMonitoringRegistration";
    private const string Preceding148Sha256 = "9877f2b6ba9bcc678da0d248aa903f262dbe9ef4f1459e3e7958742e227938c4";
    private static readonly string[] MonitoringTables =
    [
        "MonitoringAudits", "MonitoringBypasses", "MonitoringEvidenceStreams", "MonitoringEvents",
        "MonitoringFlowOutbox", "MonitoringGroups", "MonitoringOccurrences", "MonitoringRules",
        "MonitoringSeries", "MonitoringTenantConfigurations"
    ];
    private static readonly string[] RegistrationTables =
    [ "MonitoringEvidenceRegistrationAttempts", "MonitoringEvidenceRegistrationCounters" ];

    [Fact]
    public async Task True_preceding_owner_upgrade_applies_both_pending_monitoring_ids_and_preserves_all_prior_rows()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var prior = await PreparePreceding148Async(ct);
        await using var connection = new NpgsqlConnection(prior.Database);
        await connection.OpenAsync(ct);

        // This database was created with the captured preceding assembly's full SQL.
        // Migrate(owner) with this assembly would already install lower-ID Monitoring.
        await MigrateAsync(prior.Database, prior.Plan.CurrentMigration, ct);

        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await AssertPrior148Async(connection, prior, ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        await AssertDefaultsAsync(connection, ct);
        foreach (var table in MonitoringTables.Concat(RegistrationTables))
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
            ((long)(await count.ExecuteScalarAsync(ct))!).Should().Be(0,
                "schema upgrade must not backfill monitoring evidence or registration authority from the real owner or legacy cache");
        }

        var fresh = await _fixture.CreateDatabaseAsync(ct);
        await MigrateAsync(fresh, prior.Plan.CurrentMigration, ct);
        await AssertCurrentModelAsync(fresh, prior.Plan, ct);
        await using var freshConnection = new NpgsqlConnection(fresh);
        await freshConnection.OpenAsync(ct);
        (await CompleteSchemaAsync(freshConnection, ct)).Should().Equal(await CompleteSchemaAsync(connection, ct),
            "true preceding148 upgrade and fresh current creation must produce the same complete public schema");
        var freshGuards = await GuardDefinitionsAsync(freshConnection, ct);
        foreach (var function in GuardFunctions)
            freshGuards[function].Should().Equal(prior.GuardDefinitions[function]);
    }

    [Fact]
    public async Task Incremental_registration_down_and_reupgrade_keep_lower_monitoring_data_history_and_real_owner_guards()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var prior = await PreparePreceding148Async(ct);
        await using var connection = new NpgsqlConnection(prior.Database);
        await connection.OpenAsync(ct);
        await MigrateAsync(prior.Database, prior.Plan.CurrentMigration, ct);
        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await SeedPassiveMonitoringAsync(connection, prior.History, ct);
        await AssertRegistrationDeltaAsync(connection, prior.History, ct);
        var firstSchema = await CompleteSchemaAsync(connection, ct);
        var retainedIds = prior.Plan.CurrentMigrationIds.Where(id => id != CommittedRegistrationMigration).ToArray();
        var retainedHistory = await MigrationHistorySnapshotAsync(connection, retainedIds, ct);
        var monitoring = MonitoringTables.Select(table => new HistoricalTable(table,
            table == "MonitoringEvidenceStreams" ? ["CommittedRegistrationOrdinal"] : [])).ToArray();
        var monitoringRows = await HistoricalSnapshotsAsync(connection, monitoring, ct);

        // Only the higher-ID registration migration is removed. The older Monitoring
        // migration was pending on true148 and now remains installed below the owner.
        await MigrateAsync(prior.Database, PrecedingOwnerMigration, ct);

        (await AppliedAsync(prior.Database, ct)).Should().Equal(retainedIds);
        (await MigrationHistorySnapshotAsync(connection, retainedIds, ct)).Should().Equal(retainedHistory);
        await AssertPrior148Async(connection, prior, ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: false, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        var downRows = await HistoricalSnapshotsAsync(connection, monitoring, ct);
        foreach (var table in monitoring)
            downRows[table.Name].Should().Equal(monitoringRows[table.Name],
                "registration Down removes its delta only, retaining every original lower-ID Monitoring field and JSON value");

        await MigrateAsync(prior.Database, prior.Plan.CurrentMigration, ct);

        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await AssertPrior148Async(connection, prior, ct);
        var restoredRows = await HistoricalSnapshotsAsync(connection, monitoring, ct);
        foreach (var table in monitoring)
            restoredRows[table.Name].Should().Equal(monitoringRows[table.Name]);
        (await CompleteSchemaAsync(connection, ct)).Should().Equal(firstSchema,
            "re-Up reinstalls the exact registration columns, defaults, CHECKs and indexes");
        await AssertRegistrationDeltaAsync(connection, prior.History, ct);
    }

    private sealed record Preceding148(string Database, MigrationPlan Plan, HistoricalRows History,
        HistoricalTable[] Tables, IReadOnlyDictionary<string, byte[]> Rows, byte[] MigrationHistory,
        IReadOnlyDictionary<string, byte[]> GuardDefinitions);

    private async Task<Preceding148> PreparePreceding148Async(CancellationToken ct)
    {
        var preceding = await ReadBaselineAsync("Current148.sql", Preceding148Sha256, 68, ct);
        var services = await ReadBaselineAsync("HistoricalServices.sql", ServicesSha256, 67, ct);
        preceding.MigrationIds.Should().Equal(services.MigrationIds.Append(PrecedingOwnerMigration));
        var database = await _fixture.CreateDatabaseAsync(ct);
        await ApplyBaselineAsync(database, preceding, ct);
        var plan = CurrentPlan(database, services);
        (await AppliedAsync(database, ct)).Should().Equal(preceding.MigrationIds);
        await using (var db = Context(database))
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().Equal(
                [HistoricalMonitoringMigration, CommittedRegistrationMigration],
                "the lower-ID historical Monitoring migration is genuinely unapplied on the authentic preceding schema");
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        await AssertMonitoringPresenceAsync(connection, present: false, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        var history = await SeedHistoricalRowsAsync(connection, preceding.MigrationIds, hasServices: true, ct);
        // SQL seeds only passive legacy data. Actual authority is minted by the same
        // production admission and first-heartbeat transaction used by current ingress.
        await using (var provider = new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(database))
            .AddSingleton(TimeProvider.System)
            .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10), TimeSpan.FromSeconds(10)))
            .AddNetRatelClientServicesPersistence().BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true }))
        {
            var store = provider.GetRequiredService<IClientConnectionEpochStore>();
            var now = DateTimeOffset.UtcNow;
            var reservation = await store.ReserveAsync(new(new(history.TenantId, history.AgentId), Guid.NewGuid(),
                Guid.NewGuid(), 500, now, now.AddSeconds(30), now.AddMinutes(10),
                new("migration-owner", ["presence"], "legacy-preserved")), ct);
            reservation.Disposition.Should().Be(OwnershipDisposition.Accepted);
            var accepted = await store.CommitAsync(reservation.Reservation!,
                new(reservation.Reservation!.Owner, 1, DateTimeOffset.UtcNow), ct);
            accepted.Disposition.Should().Be(OwnershipDisposition.Accepted);
            accepted.Current!.AcceptanceGuardAtUtc.Should().NotBeNull();
            accepted.Current.Owner.Should().Be(reservation.Reservation.Owner);
            accepted.Current.IsEffective(DateTimeOffset.UtcNow).Should().BeTrue();
        }
        var tables = new List<HistoricalTable>();
        await using (var query = new NpgsqlCommand("SELECT tablename FROM pg_catalog.pg_tables WHERE schemaname='public' AND tablename<>'__EFMigrationsHistory' ORDER BY tablename COLLATE \"C\"", connection))
        await using (var reader = await query.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) tables.Add(new(reader.GetString(0), []));
        return new(database, plan, history, tables.ToArray(), await HistoricalSnapshotsAsync(connection, tables, ct),
            await MigrationHistorySnapshotAsync(connection, preceding.MigrationIds, ct), await GuardDefinitionsAsync(connection, ct));
    }

    private static async Task AssertPrior148Async(NpgsqlConnection connection, Preceding148 prior, CancellationToken ct)
    {
        var rows = await HistoricalSnapshotsAsync(connection, prior.Tables, ct);
        foreach (var table in prior.Tables)
            rows[table.Name].Should().Equal(prior.Rows[table.Name],
                "all original preceding148 fields, raw JSON, admission bodies, owner guards and cache/Job rows must survive unchanged");
        (await MigrationHistorySnapshotAsync(connection, prior.History.MigrationIds, ct)).Should().Equal(prior.MigrationHistory);
        var guards = await GuardDefinitionsAsync(connection, ct);
        foreach (var function in GuardFunctions) guards[function].Should().Equal(prior.GuardDefinitions[function]);
    }

    private static async Task AssertMonitoringPresenceAsync(NpgsqlConnection connection, bool present, CancellationToken ct)
    {
        foreach (var table in MonitoringTables)
        {
            await using var query = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
            query.Parameters.AddWithValue("table", $"public.\"{table}\"");
            ((bool)(await query.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
    }

    private static async Task AssertRegistrationPresenceAsync(NpgsqlConnection connection, bool present, CancellationToken ct)
    {
        foreach (var table in RegistrationTables)
        {
            await using var query = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
            query.Parameters.AddWithValue("table", $"public.\"{table}\"");
            ((bool)(await query.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
        await using var column = new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns WHERE table_schema='public'
              AND table_name='MonitoringEvidenceStreams' AND column_name='CommittedRegistrationOrdinal'
            """, connection);
        ((long)(await column.ExecuteScalarAsync(ct))!).Should().Be(present ? 1 : 0);
    }

    private static async Task SeedPassiveMonitoringAsync(NpgsqlConnection connection, HistoricalRows history, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            INSERT INTO "MonitoringTenantConfigurations" VALUES (@tenant,1);
            INSERT INTO "MonitoringRules" VALUES (@tenant,@rule,1,'{"legacyUnknownRule":{"keep":true}}');
            INSERT INTO "MonitoringGroups" VALUES (@tenant,@group,1,'{"legacyUnknownGroup":[1,null,true]}');
            INSERT INTO "MonitoringBypasses" VALUES (@tenant,@bypass,NULL,'{"legacyUnknownBypass":"kept"}');
            INSERT INTO "MonitoringEvidenceStreams" ("TenantId","AgentId","ConnectionId","ConnectionEpoch","EvidenceStreamId","Active","RegisteredAtUtc","Revision")
            VALUES (@tenant,@agent,@connection,500,@stream,FALSE,@at,1);
            INSERT INTO "MonitoringOccurrences" VALUES (@occurrence,@tenant,@rule,@agent,'cpu',@event,@at,@at,'{"legacyUnknownOccurrence":"kept"}');
            INSERT INTO "MonitoringEvents" VALUES (@event,@tenant,@occurrence,@at,'{"legacyUnknownEvent":"kept"}');
            INSERT INTO "MonitoringSeries" VALUES (@tenant,@rule,@agent,'cpu',1,4,0,NULL,@occurrence,FALSE,FALSE,'{"legacyUnknownState":{"keep":true}}',@at);
            INSERT INTO "MonitoringFlowOutbox" ("OutboxId","TenantId","OccurrenceId","EventId","StableFlowDispatchKey","IntentJson","Status","CreatedAtUtc","Attempts","LeaseFence")
            VALUES (@outbox,@tenant,@occurrence,@event,'migration-passive','{"legacyUnknownIntent":"kept"}',6,@at,0,0);
            INSERT INTO "MonitoringAudits" VALUES (@audit,@tenant,'migration',@rule,'fixture',@operator,'passive migration fixture',@at,1,'{"legacyUnknownAudit":"kept"}');
            """, connection);
        Add(command, ("tenant", history.TenantId), ("agent", history.AgentId), ("rule", Guid.NewGuid()),
            ("group", Guid.NewGuid()), ("bypass", Guid.NewGuid()), ("connection", Guid.NewGuid()), ("stream", Guid.NewGuid()),
            ("occurrence", Guid.NewGuid()), ("event", Guid.NewGuid()), ("outbox", Guid.NewGuid()), ("audit", Guid.NewGuid()),
            ("operator", Guid.NewGuid()), ("at", new DateTimeOffset(2026,10,5,12,0,0,TimeSpan.Zero)));
        (await command.ExecuteNonQueryAsync(ct)).Should().Be(10);
    }

    private static async Task AssertRegistrationDeltaAsync(NpgsqlConnection connection, HistoricalRows history, CancellationToken ct)
    {
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await using (var defaults = new NpgsqlCommand("""
            SELECT "CommittedRegistrationOrdinal" FROM "MonitoringEvidenceStreams" WHERE "TenantId"=@tenant AND "AgentId"=@agent
            """, connection))
        {
            Add(defaults, ("tenant", history.TenantId), ("agent", history.AgentId));
            ((long)(await defaults.ExecuteScalarAsync(ct))!).Should().Be(0);
        }
        await using (var checks = new NpgsqlCommand("""
            SELECT conname FROM pg_catalog.pg_constraint WHERE connamespace='public'::regnamespace AND contype='c' AND convalidated
              AND conname=ANY(@names) ORDER BY conname COLLATE "C"
            """, connection))
        {
            var names = new[] { "CK_MonitoringEvidenceRegistrationAttempts_Shape", "CK_MonitoringEvidenceRegistrationCounters_Identity", "CK_MonitoringEvidenceStreams_Ordinal" };
            checks.Parameters.AddWithValue("names", names);
            var actual = new List<string>();
            await using var reader = await checks.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) actual.Add(reader.GetString(0));
            actual.Should().Equal(names.Order(StringComparer.Ordinal));
        }
        await using (var indexes = new NpgsqlCommand("""
            SELECT count(*) FROM pg_catalog.pg_indexes WHERE schemaname='public' AND tablename='MonitoringEvidenceRegistrationAttempts'
              AND indexname=ANY(@names)
            """, connection))
        {
            indexes.Parameters.AddWithValue("names", new[] { "IX_MonitoringEvidenceRegistrationAttempts_RetainUntilUtc", "IX_MonitoringEvidenceRegistrationAttempts_TenantId_AgentId_Reg~" });
            ((long)(await indexes.ExecuteScalarAsync(ct))!).Should().Be(2);
        }
        await using (var counter = new NpgsqlCommand("""
            INSERT INTO "MonitoringEvidenceRegistrationCounters" ("TenantId","AgentId") VALUES (@tenant,@agent)
            RETURNING "LastIssuedOrdinal"
            """, connection))
        {
            Add(counter, ("tenant", history.TenantId), ("agent", history.AgentId));
            ((long)(await counter.ExecuteScalarAsync(ct))!).Should().Be(0);
        }
        foreach (var sql in new[]
        {
            "UPDATE \"MonitoringEvidenceStreams\" SET \"CommittedRegistrationOrdinal\"=-1 WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent",
            "UPDATE \"MonitoringEvidenceRegistrationCounters\" SET \"LastIssuedOrdinal\"=-1 WHERE \"TenantId\"=@tenant AND \"AgentId\"=@agent",
            "INSERT INTO \"MonitoringEvidenceRegistrationAttempts\" VALUES (@tenant,@agent,@registration,@connection,1,1,0,@at,@expires,@retain)"
        }) await AssertRejectedRegistrationAsync(connection, history, sql, PostgresErrorCodes.CheckViolation, ct);
        const string attemptSql = "INSERT INTO \"MonitoringEvidenceRegistrationAttempts\" VALUES (@tenant,@agent,@registration,@connection,1,1,3,@at,@expires,@retain)";
        await using (var attempt = RegistrationCommand(connection, history, attemptSql))
            (await attempt.ExecuteNonQueryAsync(ct)).Should().Be(1);
        await AssertRejectedRegistrationAsync(connection, history, attemptSql, PostgresErrorCodes.UniqueViolation, ct);
    }

    private static NpgsqlCommand RegistrationCommand(NpgsqlConnection connection, HistoricalRows history, string sql)
    {
        var command = new NpgsqlCommand(sql, connection);
        var at = new DateTimeOffset(2026,10,5,12,0,0,TimeSpan.Zero);
        Add(command, ("tenant", history.TenantId), ("agent", history.AgentId), ("registration", Guid.NewGuid()),
            ("connection", Guid.NewGuid()), ("at", at), ("expires", at.AddSeconds(5)), ("retain", at.AddSeconds(10)));
        return command;
    }
    private static async Task AssertRejectedRegistrationAsync(NpgsqlConnection connection, HistoricalRows history,
        string sql, string expectedSqlState, CancellationToken ct)
    {
        await using var command = RegistrationCommand(connection, history, sql);
        var exception = await Assert.ThrowsAsync<PostgresException>(async () => { await command.ExecuteNonQueryAsync(ct); });
        exception.SqlState.Should().Be(expectedSqlState);
    }

    private static async Task<byte[]> CompleteSchemaAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT convert_to(jsonb_build_object(
              'columns',(SELECT jsonb_agg(jsonb_build_array(table_name,column_name,data_type,udt_name,is_nullable,column_default,numeric_precision,numeric_scale,character_maximum_length)
                ORDER BY table_name COLLATE "C",column_name COLLATE "C") FROM information_schema.columns WHERE table_schema='public'),
              'constraints',(SELECT jsonb_agg(jsonb_build_array(rel.relname,con.conname,con.contype,pg_get_constraintdef(con.oid),con.condeferrable,con.condeferred)
                ORDER BY rel.relname COLLATE "C",con.conname COLLATE "C") FROM pg_catalog.pg_constraint con JOIN pg_catalog.pg_class rel ON rel.oid=con.conrelid
                JOIN pg_catalog.pg_namespace ns ON ns.oid=rel.relnamespace WHERE ns.nspname='public'),
              'indexes',(SELECT jsonb_agg(jsonb_build_array(tablename,indexname,indexdef) ORDER BY tablename COLLATE "C",indexname COLLATE "C")
                FROM pg_catalog.pg_indexes WHERE schemaname='public')
            )::text,'UTF8')
            """, connection);
        return (byte[])(await command.ExecuteScalarAsync(ct))!;
    }
}
