using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
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
        // Migrate(owner) with this assembly would already install lower-ID Monitoring and Flow.
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

        // Only the higher-ID registration migration is removed. Older Monitoring and Flow
        // were pending on true148 and now remain installed below the owner.
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
                [HistoricalMonitoringMigration, FlowPreceding149PendingMigration, CommittedRegistrationMigration],
                "the lower-ID historical Monitoring and Flow migrations and registration delta are genuinely unapplied on the authentic preceding schema");
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
                new("migration-owner", ["presence"], new string('a', 64))), ct);
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

    private const string FlowPreceding149Sha256 = "089d2a4a6d04235ad2d91a16f3dfe26cce103e63613f7adde740aa233d4e71eb";
    private const string FlowPreceding149PendingMigration = "20261002195852_AddDurableFlows";
    private static readonly string[] FlowPreceding149FlowTables =
    [ "FlowDefinitions", "FlowVersions", "FlowRuns", "FlowActions", "FlowAudits", "FlowRuntimeIdentity" ];

    [Fact]
    public async Task True_preceding14970_upgrade_applies_pending_flow_and_preserves_all_prior_rows_and_guards()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var prior = await PrepareFlowPreceding149Async(ct);
        await using var connection = new NpgsqlConnection(prior.Database);
        await connection.OpenAsync(ct);

        // Apply the genuine pending lower-ID Flow migration to the captured 70-ID
        // predecessor. Migrate(owner/current) cannot manufacture this predecessor.
        await MigrateAsync(prior.Database, prior.Plan.CurrentMigration, ct);

        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await AssertFlowPreceding149PreservedAsync(connection, prior, ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        await AssertDefaultsAsync(connection, ct);
        foreach (var table in FlowPreceding149FlowTables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
            ((long)(await count.ExecuteScalarAsync(ct))!).Should().Be(0,
                "schema upgrade must not manufacture Flow definitions, execution history or runtime identity from the prior owner and Monitoring data");
        }

        var fresh = await _fixture.CreateDatabaseAsync(ct);
        await MigrateAsync(fresh, prior.Plan.CurrentMigration, ct);
        await AssertCurrentModelAsync(fresh, prior.Plan, ct);
        await using var freshConnection = new NpgsqlConnection(fresh);
        await freshConnection.OpenAsync(ct);
        await AssertGuardsAsync(freshConnection, present: true, ct);
        await AssertDefaultsAsync(freshConnection, ct);
        (await CompleteSchemaAsync(freshConnection, ct)).Should().Equal(await CompleteSchemaAsync(connection, ct),
            "authentic preceding149 upgrade and fresh current creation must produce the same public columns, constraints and indexes");
        var freshGuards = await GuardDefinitionsAsync(freshConnection, ct);
        foreach (var function in GuardFunctions)
            freshGuards[function].Should().Equal(prior.GuardDefinitions[function]);
    }

    private sealed record FlowPreceding149(string Database, MigrationPlan Plan, HistoricalRows History,
        HistoricalTable[] Tables, IReadOnlyDictionary<string, byte[]> Rows, byte[] MigrationHistory,
        IReadOnlyDictionary<string, byte[]> GuardDefinitions);

    private async Task<FlowPreceding149> PrepareFlowPreceding149Async(CancellationToken ct)
    {
        var preceding = await ReadBaselineAsync("Current14970.sql", FlowPreceding149Sha256, 70, ct);
        var services = await ReadBaselineAsync("HistoricalServices.sql", ServicesSha256, 67, ct);
        preceding.MigrationIds.Should().Equal(services.MigrationIds.Append(HistoricalMonitoringMigration)
            .Append(PrecedingOwnerMigration).Append(CommittedRegistrationMigration).Order(StringComparer.Ordinal));
        var database = await _fixture.CreateDatabaseAsync(ct);
        await ApplyBaselineAsync(database, preceding, ct);
        var plan = CurrentPlan(database, services);
        plan.CurrentMigrationIds.Should().Equal(preceding.MigrationIds.Append(FlowPreceding149PendingMigration)
            .Order(StringComparer.Ordinal));
        (await AppliedAsync(database, ct)).Should().Equal(preceding.MigrationIds);
        await using (var db = Context(database))
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().Equal([FlowPreceding149PendingMigration],
                "only the retained lower-ID Flow migration is unapplied on the authentic current149 schema");
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        foreach (var table in FlowPreceding149FlowTables)
        {
            await using var absent = new NpgsqlCommand("SELECT to_regclass(@table) IS NULL", connection);
            absent.Parameters.AddWithValue("table", $"public.\"{table}\"");
            ((bool)(await absent.ExecuteScalarAsync(ct))!).Should().BeTrue();
        }
        var history = await SeedHistoricalRowsAsync(connection, preceding.MigrationIds, hasServices: true, ct);
        await SeedPassiveMonitoringAsync(connection, history, ct);
        await AssertRegistrationDeltaAsync(connection, history, ct);

        // These legacy/Monitoring/registration rows are passive migration fixtures.
        // Authority comes only from production reservation and first-heartbeat commit.
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
                new("migration-owner", ["presence"], new string('a', 64))), ct);
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

    private static async Task AssertFlowPreceding149PreservedAsync(NpgsqlConnection connection,
        FlowPreceding149 prior, CancellationToken ct)
    {
        var rows = await HistoricalSnapshotsAsync(connection, prior.Tables, ct);
        foreach (var table in prior.Tables)
            rows[table.Name].Should().Equal(prior.Rows[table.Name],
                "all original current149 columns, raw JSON, registration data, owner/admission bodies and cache/Job rows must survive unchanged");
        (await MigrationHistorySnapshotAsync(connection, prior.History.MigrationIds, ct)).Should().Equal(prior.MigrationHistory,
            "all 70 genuine preceding migration IDs and ProductVersion values must survive unchanged");
        var guards = await GuardDefinitionsAsync(connection, ct);
        foreach (var function in GuardFunctions)
            guards[function].Should().Equal(prior.GuardDefinitions[function]);
    }

    private const string ScopedFlowDownResource = "HistoricalFlowOnlyDown.sql";
    private const string ScopedFlowUpResource = "HistoricalFlowOnlyUp.sql";
    private const string ScopedFlowDownSha256 = "0e9ac4b60628485d2c4739bf0795ac782a4ffdd3db69e14c29418cf4990ff695";
    private const string ScopedFlowUpSha256 = "24247e04ba45bd23ecd306b645e6dfff8c75d6bed899ab9d2ae768301c027f49";
    private static readonly string[] ScopedFlowTables =
    [ "FlowActions", "FlowAudits", "FlowRuntimeIdentity", "FlowRuns", "FlowVersions", "FlowDefinitions" ];
    private static readonly string[] ScopedFlowFunctions =
    [ "netratel_flow_event_immutable", "netratel_flow_action_immutable", "netratel_flow_version_immutable" ];

    [Fact]
    public async Task Scoped_historical_flow_down_and_up_preserve_newer_owner_registration_rows_history_and_guards()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        // These exact range bytes come from the installed EF tool and actual compiled
        // current source. They are not a chronological downgrade of the current database.
        var down = await ReadScopedFlowRangeAsync(ScopedFlowDownResource, ScopedFlowDownSha256, down: true, ct);
        var up = await ReadScopedFlowRangeAsync(ScopedFlowUpResource, ScopedFlowUpSha256, down: false, ct);
        var prior = await PrepareFlowPreceding149Async(ct);
        await using var connection = new NpgsqlConnection(prior.Database);
        await connection.OpenAsync(ct);
        var precedingSchema = await CompleteSchemaAsync(connection, ct);

        await MigrateAsync(prior.Database, prior.Plan.CurrentMigration, ct);
        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await AssertFlowPreceding149PreservedAsync(connection, prior, ct);
        await SeedScopedPassiveFlowRowsAsync(prior.Database, prior.History.TenantId, ct);
        await AssertScopedFlowPresenceAsync(connection, present: true, ct);
        await AssertScopedFlowCountsAsync(connection, expected: 1, ct);
        var currentSchema = await CompleteSchemaAsync(connection, ct);
        var flowPrograms = await ScopedFlowProgramObjectsAsync(connection, flowOnly: true, ct);
        var retainedPrograms = await ScopedFlowProgramObjectsAsync(connection, flowOnly: false, ct);
        var retainedIds = prior.Plan.CurrentMigrationIds.Where(id => id != FlowPreceding149PendingMigration).ToArray();
        retainedIds.Should().Equal(prior.History.MigrationIds);
        var retainedHistory = await MigrationHistorySnapshotAsync(connection, retainedIds, ct);
        var retainedRows = await HistoricalSnapshotsAsync(connection, prior.Tables, ct);

        await ApplyScopedFlowRangeAsync(connection, down, ct);

        (await AppliedAsync(prior.Database, ct)).Should().Equal(retainedIds);
        (await MigrationHistorySnapshotAsync(connection, retainedIds, ct)).Should().Equal(retainedHistory);
        await using (var db = Context(prior.Database))
            (await db.Database.GetPendingMigrationsAsync(ct)).Should().Equal([FlowPreceding149PendingMigration]);
        await AssertScopedFlowPresenceAsync(connection, present: false, ct);
        (await CompleteSchemaAsync(connection, ct)).Should().Equal(precedingSchema,
            "the scoped historical Flow Down leaves the genuine preceding149 schema intact despite four newer installed IDs");
        await AssertScopedRetainedRowsAsync(connection, prior.Tables, retainedRows, ct);
        await AssertFlowPreceding149PreservedAsync(connection, prior, ct);
        (await ScopedFlowProgramObjectsAsync(connection, flowOnly: false, ct)).Should().Equal(retainedPrograms);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await AssertGuardsAsync(connection, present: true, ct);

        await ApplyScopedFlowRangeAsync(connection, up, ct);

        await AssertCurrentModelAsync(prior.Database, prior.Plan, ct);
        await AssertScopedFlowPresenceAsync(connection, present: true, ct);
        (await CompleteSchemaAsync(connection, ct)).Should().Equal(currentSchema);
        (await ScopedFlowProgramObjectsAsync(connection, flowOnly: true, ct)).Should().Equal(flowPrograms,
            "the exact historical Up reinstalls the same Flow immutability functions and triggers");
        (await ScopedFlowProgramObjectsAsync(connection, flowOnly: false, ct)).Should().Equal(retainedPrograms);
        (await MigrationHistorySnapshotAsync(connection, retainedIds, ct)).Should().Equal(retainedHistory);
        await AssertScopedRetainedRowsAsync(connection, prior.Tables, retainedRows, ct);
        await AssertFlowPreceding149PreservedAsync(connection, prior, ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: true, ct);
        await AssertGuardsAsync(connection, present: true, ct);
        // Historical Down destroys Flow data. Re-Up restores schema and history only.
        await AssertScopedFlowCountsAsync(connection, expected: 0, ct);

        var fresh = await _fixture.CreateDatabaseAsync(ct);
        await MigrateAsync(fresh, prior.Plan.CurrentMigration, ct);
        await AssertCurrentModelAsync(fresh, prior.Plan, ct);
        await using var freshConnection = new NpgsqlConnection(fresh);
        await freshConnection.OpenAsync(ct);
        (await CompleteSchemaAsync(freshConnection, ct)).Should().Equal(currentSchema);
        (await ScopedFlowProgramObjectsAsync(freshConnection, flowOnly: true, ct)).Should().Equal(flowPrograms);
    }

    private static async Task<string> ReadScopedFlowRangeAsync(string resource, string expectedSha256, bool down, CancellationToken ct)
    {
        if (expectedSha256.StartsWith("PENDING_", StringComparison.Ordinal))
            throw new InvalidOperationException("Bind the genuine compiled-source Flow range capture before running this Fact.");
        await using var stream = typeof(CommittedConnectionOwnershipMigrationPostgresTests).Assembly
            .GetManifestResourceStream(ResourcePrefix + resource)
            ?? throw new InvalidOperationException($"Missing genuine embedded Flow range: {resource}");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(expectedSha256);
        var sql = Encoding.UTF8.GetString(bytes);
        if (sql.Length > 0 && sql[0] == '\uFEFF') sql = sql[1..];
        var inserts = Regex.Matches(sql,
            """INSERT INTO "__EFMigrationsHistory" \("MigrationId", "ProductVersion"\)\s*VALUES \('([^']+)', '([^']+)'\);""");
        var deletes = Regex.Matches(sql,
            """DELETE FROM "__EFMigrationsHistory"\s+WHERE "MigrationId"\s*=\s*'([^']+)'\s*;""");
        if (down)
        {
            inserts.Should().BeEmpty();
            deletes.Select(match => match.Groups[1].Value).Should().Equal([FlowPreceding149PendingMigration]);
            Regex.Matches(sql, """DROP TABLE "([^"]+)";""")
                .Select(match => match.Groups[1].Value).Should().Equal(ScopedFlowTables);
            Regex.Matches(sql, """DROP FUNCTION(?: IF EXISTS)? (netratel_flow_[a-z_]+)\(\);""")
                .Select(match => match.Groups[1].Value).Should().Equal(ScopedFlowFunctions);
        }
        else
        {
            deletes.Should().BeEmpty();
            inserts.Select(match => match.Groups[1].Value).Should().Equal([FlowPreceding149PendingMigration]);
            inserts.Select(match => match.Groups[2].Value).Should().OnlyContain(version => version == "10.0.12");
        }
        return sql;
    }

    private static async Task ApplyScopedFlowRangeAsync(NpgsqlConnection connection, string sql, CancellationToken ct)
    {
        // Execute only the hash-bound genuine EF output, retaining its transaction and
        // history statements; no fixture-authored schema or history mutation is used.
        await using var command = new NpgsqlCommand(sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task SeedScopedPassiveFlowRowsAsync(string database, int tenant, CancellationToken ct)
    {
        await using var db = Context(database);
        var definition = Guid.NewGuid();
        var version = Guid.NewGuid();
        var run = Guid.NewGuid();
        var at = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        // Passive, relationally valid migration rows exercise loss/preservation scope.
        // They do not claim a connector send, executable graph or physical evidence.
        db.FlowDefinitions.Add(new() { Id = definition, TenantId = tenant, Name = "Passive migration definition",
            Revision = 1, Enabled = false, DraftJson = "{\"migrationFixture\":true}", PublishedVersionId = version,
            PublishedVersionNumber = 1, CreatedAtUtc = at, UpdatedAtUtc = at });
        db.FlowVersions.Add(new() { Id = version, TenantId = tenant, FlowId = definition, VersionNumber = 1,
            GraphJson = "{\"migrationFixture\":true}", ConfigurationHash = new string('a', 64),
            PublishedBy = "migration-fixture", PublishedAtUtc = at });
        db.FlowRuns.Add(new() { Id = run, TenantId = tenant, FlowId = definition, FlowVersionId = version,
            EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid(), EventJson = "{\"migrationFixture\":true}",
            EventFingerprint = new string('b', 64), CreatedAtUtc = at });
        db.FlowActions.Add(new() { RunId = run, NodeId = Guid.NewGuid(), TenantId = tenant,
            IdempotencyKey = "passive-migration-" + run.ToString("N"), DraftJson = "{\"migrationFixture\":true}",
            PreparedJson = "{\"passivePrepared\":true}", ConnectorRevision = 1, SemanticFingerprint = new string('c', 64),
            ReceiptJson = "{\"passiveReceipt\":true}" });
        db.FlowAudits.Add(new() { Id = Guid.NewGuid(), TenantId = tenant, FlowId = definition, Revision = 1,
            ActorId = "migration-fixture", Operation = "passive-schema-fixture", AtUtc = at });
        db.FlowRuntimeIdentity.Add(new() { Id = 1, SourceInstanceId = Guid.NewGuid() });
        (await db.SaveChangesAsync(ct)).Should().Be(6);
    }

    private static async Task AssertScopedFlowCountsAsync(NpgsqlConnection connection, long expected, CancellationToken ct)
    {
        foreach (var table in ScopedFlowTables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
            ((long)(await count.ExecuteScalarAsync(ct))!).Should().Be(expected);
        }
    }

    private static async Task AssertScopedFlowPresenceAsync(NpgsqlConnection connection, bool present, CancellationToken ct)
    {
        foreach (var table in ScopedFlowTables)
        {
            await using var query = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
            query.Parameters.AddWithValue("table", $"public.\"{table}\"");
            ((bool)(await query.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
        foreach (var function in ScopedFlowFunctions)
        {
            await using var query = new NpgsqlCommand("SELECT to_regprocedure(@function) IS NOT NULL", connection);
            query.Parameters.AddWithValue("function", "public." + function + "()");
            ((bool)(await query.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
    }

    private static async Task AssertScopedRetainedRowsAsync(NpgsqlConnection connection, HistoricalTable[] tables,
        IReadOnlyDictionary<string, byte[]> expected, CancellationToken ct)
    {
        var actual = await HistoricalSnapshotsAsync(connection, tables, ct);
        foreach (var table in tables)
            actual[table.Name].Should().Equal(expected[table.Name],
                "scoped historical Flow rollback and re-Up must preserve every field of every non-Flow row");
    }

    private static async Task<byte[]> ScopedFlowProgramObjectsAsync(NpgsqlConnection connection, bool flowOnly, CancellationToken ct)
    {
        await using var query = new NpgsqlCommand("""
            SELECT convert_to(jsonb_build_object(
              'functions',(SELECT jsonb_agg(jsonb_build_array(p.proname,pg_get_function_identity_arguments(p.oid),pg_get_functiondef(p.oid))
                ORDER BY p.proname COLLATE "C",pg_get_function_identity_arguments(p.oid) COLLATE "C")
                FROM pg_catalog.pg_proc p JOIN pg_catalog.pg_namespace n ON n.oid=p.pronamespace
                WHERE n.nspname='public' AND p.prokind IN ('f','p') AND (p.proname=ANY(@functions))=@flow_only),
              'triggers',(SELECT jsonb_agg(jsonb_build_array(rel.relname,tr.tgname,pg_get_triggerdef(tr.oid),tr.tgenabled)
                ORDER BY rel.relname COLLATE "C",tr.tgname COLLATE "C") FROM pg_catalog.pg_trigger tr
                JOIN pg_catalog.pg_class rel ON rel.oid=tr.tgrelid JOIN pg_catalog.pg_namespace n ON n.oid=rel.relnamespace
                WHERE n.nspname='public' AND NOT tr.tgisinternal AND (rel.relname=ANY(@tables))=@flow_only)
            )::text,'UTF8')
            """, connection);
        Add(query, ("functions", ScopedFlowFunctions), ("tables", ScopedFlowTables), ("flow_only", flowOnly));
        return (byte[])(await query.ExecuteScalarAsync(ct))!;
    }
}
