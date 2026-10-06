using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Services;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

/// <summary>
/// Upgrades the authentic preceding foundation and historical Services schemas, then
/// exercises the committed-ownership migration's guarded Down/Up within the current registered history.
/// Legacy allocator, cache and prepared Job rows grant no current connection authority.
/// </summary>
[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed partial class CommittedConnectionOwnershipMigrationPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    private readonly PostgreSqlPersistenceFixture _fixture = fixture;
    private const string ResourcePrefix = "NetRatel.Tests.MigrationBaselines.CommittedConnectionOwnership.";
    private const string FoundationSha256 = "432115f47c10fb74e6b68a5403fa0c9ef4fa74f63855074a212584a9aad42a8e";
    private const string ServicesSha256 = "90af775cdc7f48184d086feaedd45977fbebdeedeb997b4deedec2ce3296f6dc";
    private const string HistoricalServicesMigration = "20261002170000_AddClientServicesSnapshots";

    [Fact]
    public async Task True_foundation_upgrade_preserves_legacy_jobs_and_matches_fresh_current_schema()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var foundation = await ReadBaselineAsync("Foundation.sql", FoundationSha256, 66, ct);
        var services = await ReadBaselineAsync("HistoricalServices.sql", ServicesSha256, 67, ct);
        AssertBaselineRelationship(foundation, services);
        var database = await _fixture.CreateDatabaseAsync(ct);
        await ApplyBaselineAsync(database, foundation, ct);
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        (await AppliedAsync(database, ct)).Should().Equal(foundation.MigrationIds);
        await AssertServicesPresenceAsync(connection, present: false, ct);
        await AssertAdditionsAbsentAsync(connection, ct);
        var history = await SeedHistoricalRowsAsync(connection, foundation.MigrationIds, hasServices: false, ct);
        var plan = CurrentPlan(database, services);

        await MigrateAsync(database, plan.CurrentMigration, ct);

        await AssertUpgradeAsync(database, connection, history, plan, ct);
        foreach (var table in ServicesTables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table.Name}\"", connection);
            ((long)(await count.ExecuteScalarAsync(ct))!).Should().Be(0,
                "the true foundation had neither a Services cache nor an issued Services epoch");
        }

        var freshDatabase = await _fixture.CreateDatabaseAsync(ct);
        await MigrateAsync(freshDatabase, plan.CurrentMigration, ct);
        await using var freshConnection = new NpgsqlConnection(freshDatabase);
        await freshConnection.OpenAsync(ct);
        await AssertCurrentModelAsync(freshDatabase, plan, ct);
        await AssertGuardsAsync(freshConnection, present: true, ct);
        await AssertDefaultsAsync(freshConnection, ct);
        await AssertNoAuthorityRowsAsync(freshConnection, ct);
        (await OwnershipSchemaAsync(freshConnection, ct)).Should().Equal(await OwnershipSchemaAsync(connection, ct),
            "fresh creation and true-foundation upgrade must produce the same ownership schema");
        var upgradedGuards = await GuardDefinitionsAsync(connection, ct);
        var freshGuards = await GuardDefinitionsAsync(freshConnection, ct);
        foreach (var function in GuardFunctions)
            freshGuards[function].Should().Equal(upgradedGuards[function]);
    }

    [Fact]
    public async Task Populated_historical_services_upgrade_preserves_raw_cache_without_backfilling_authority()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var services = await ReadBaselineAsync("HistoricalServices.sql", ServicesSha256, 67, ct);
        var database = await _fixture.CreateDatabaseAsync(ct);
        await ApplyBaselineAsync(database, services, ct);
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        (await AppliedAsync(database, ct)).Should().Equal(services.MigrationIds);
        await AssertServicesPresenceAsync(connection, present: true, ct);
        await AssertAdditionsAbsentAsync(connection, ct);
        var history = await SeedHistoricalRowsAsync(connection, services.MigrationIds, hasServices: true, ct);
        var plan = CurrentPlan(database, services);

        await MigrateAsync(database, plan.CurrentMigration, ct);

        await AssertUpgradeAsync(database, connection, history, plan, ct);
    }

    [Fact]
    public async Task Incremental_owner_down_and_reupgrade_preserve_services_and_reinstall_the_same_guards()
    {
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var ct = budget.Token;
        var services = await ReadBaselineAsync("HistoricalServices.sql", ServicesSha256, 67, ct);
        var database = await _fixture.CreateDatabaseAsync(ct);
        await ApplyBaselineAsync(database, services, ct);
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        (await AppliedAsync(database, ct)).Should().Equal(services.MigrationIds);
        var history = await SeedHistoricalRowsAsync(connection, services.MigrationIds, hasServices: true, ct);
        var plan = CurrentPlan(database, services);
        await MigrateAsync(database, plan.CurrentMigration, ct);
        await AssertUpgradeAsync(database, connection, history, plan, ct);
        var firstGuardDefinitions = await GuardDefinitionsAsync(connection, ct);
        var firstSchema = await OwnershipSchemaAsync(connection, ct);

        // Down crosses receiver, registration and ownership. The registered lower-ID
        // Monitoring, Flow and Connector migrations stay alongside all 67 baseline IDs.
        await MigrateAsync(database, plan.PreviousMigration, ct);

        (await AppliedAsync(database, ct)).Should().Equal(plan.PreOwnerMigrationIds);
        await AssertServicesPresenceAsync(connection, present: true, ct);
        await AssertMonitoringPresenceAsync(connection, present: true, ct);
        await AssertRegistrationPresenceAsync(connection, present: false, ct);
        await AssertAdditionsAbsentAsync(connection, ct);
        await AssertHistoricalRowsAsync(connection, history, ct);
        await MigrateAsync(database, plan.CurrentMigration, ct);
        await AssertUpgradeAsync(database, connection, history, plan, ct);
        (await OwnershipSchemaAsync(connection, ct)).Should().Equal(firstSchema);
        var reinstalledGuardDefinitions = await GuardDefinitionsAsync(connection, ct);
        foreach (var function in GuardFunctions)
            reinstalledGuardDefinitions[function].Should().Equal(firstGuardDefinitions[function],
                "the actual owner migration must reinstall the same PostgreSQL function body after Down");
    }

    private sealed record Baseline(string Sql, string[] MigrationIds);
    private sealed record MigrationPlan(string OwnerMigration, string PreviousMigration, string CurrentMigration,
        string[] PreOwnerMigrationIds, string[] CurrentMigrationIds);

    private static async Task<Baseline> ReadBaselineAsync(string resource, string expectedSha256, int expectedCount, CancellationToken ct)
    {
        await using var stream = typeof(CommittedConnectionOwnershipMigrationPostgresTests).Assembly
            .GetManifestResourceStream(ResourcePrefix + resource)
            ?? throw new InvalidOperationException($"Missing embedded migration baseline: {resource}");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var bytes = buffer.ToArray();
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant().Should().Be(expectedSha256,
            "the baseline must retain the exact genuine EF script bytes, including its UTF-8 BOM");
        var sql = Encoding.UTF8.GetString(bytes);
        if (sql.Length > 0 && sql[0] == '\uFEFF') sql = sql[1..];
        var history = Regex.Matches(sql,
            """INSERT INTO "__EFMigrationsHistory" \("MigrationId", "ProductVersion"\)\s*VALUES \('([^']+)', '([^']+)'\);""");
        history.Count.Should().Be(expectedCount);
        var ids = history.Select(match => match.Groups[1].Value).ToArray();
        ids.Should().OnlyHaveUniqueItems().And.BeInAscendingOrder(StringComparer.Ordinal);
        history.Select(match => match.Groups[2].Value).Should().OnlyContain(version => version == "10.0.12");
        return new(sql, ids);
    }

    private static void AssertBaselineRelationship(Baseline foundation, Baseline services)
    {
        foundation.MigrationIds.Should().NotContain(HistoricalServicesMigration);
        services.MigrationIds.Except(foundation.MigrationIds).Should().Equal([HistoricalServicesMigration]);
        foundation.MigrationIds.Except(services.MigrationIds).Should().BeEmpty();
        services.MigrationIds.Last().Should().Be(foundation.MigrationIds.Last());
    }

    private static OrchestratorDbContext Context(string database) => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(database).Options);

    private static MigrationPlan CurrentPlan(string database, Baseline services)
    {
        using var db = Context(database);
        var migrations = db.Database.GetMigrations().ToArray();
        var owner = migrations.Where(id => id.EndsWith("_AddCommittedConnectionOwnership", StringComparison.Ordinal))
            .Should().ContainSingle().Which;
        owner.Should().Be(PrecedingOwnerMigration);
        var expected = services.MigrationIds.Append(HistoricalMonitoringMigration).Append(FlowPreceding149PendingMigration)
            .Append(HistoricalConnectorMigration).Append(owner).Append(CommittedRegistrationMigration)
            .Append(ReceiverEvidenceMigration).Order(StringComparer.Ordinal).ToArray();
        migrations.Should().Equal(expected,
            "current history retains the exact Services baseline, lower-ID Monitoring, Flow and Connector, genuine owner and registration, and genuine receiver delta");
        var previous = services.MigrationIds.Last();
        return new(owner, previous, ReceiverEvidenceMigration,
            migrations.Where(id => StringComparer.Ordinal.Compare(id, previous) <= 0).ToArray(), migrations);
    }

    private static async Task ApplyBaselineAsync(string database, Baseline baseline, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync(ct);
        // Execute the embedded predecessor assembly's full SQL, not a range produced by
        // the current assembly (which already contains the historical Services migration).
        await using var command = new NpgsqlCommand(baseline.Sql, connection) { CommandTimeout = 180 };
        await command.ExecuteNonQueryAsync(ct);
    }

    private static async Task MigrateAsync(string database, string target, CancellationToken ct)
    {
        await using var db = Context(database);
        await db.Database.GetService<IMigrator>().MigrateAsync(target, ct);
    }

    private static async Task<string[]> AppliedAsync(string database, CancellationToken ct)
    {
        await using var db = Context(database);
        return (await db.Database.GetAppliedMigrationsAsync(ct)).ToArray();
    }

    private sealed record HistoricalTable(string Name, string[] AddedColumns);
    private static readonly HistoricalTable[] FoundationTables =
    [
        new("Tenants", []), new("Agents", []), new("Jobs", []), new("JobRuns", []),
        new("JobRunControls", ["DispatchOwnerConnectionId", "DispatchOwnerEpoch"])
    ];
    private static readonly HistoricalTable[] ServicesTables =
    [ new("ClientConnectionEpochs", ["CancellationBarrierUntilUtc"]), new("ClientServicesSnapshots", []) ];
    private sealed record HistoricalRows(int TenantId, Guid AgentId, long JobRunId, bool HasServices,
        HistoricalTable[] Tables, string[] MigrationIds, IReadOnlyDictionary<string, byte[]> Rows, byte[] MigrationHistory);

    private static async Task<HistoricalRows> SeedHistoricalRowsAsync(NpgsqlConnection connection,
        string[] migrationIds, bool hasServices, CancellationToken ct)
    {
        const int tenant = 186;
        const long job = 81001;
        const long jobRun = 81002;
        var at = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
        var agent = Guid.NewGuid();
        var principal = Guid.NewGuid().ToString("N");
        await using var transaction = await connection.BeginTransactionAsync(ct);
        await using var command = new NpgsqlCommand("""
            INSERT INTO "Tenants" ("Id","Name","Domains","CreatedAtUtc","UpdatedAtUtc")
            VALUES (@tenant,'Migration history tenant',ARRAY[]::text[],@at,@at);
            INSERT INTO "Agents" ("Id","TenantId","Name","Status","IsEnabled","CreatedAtUtc")
            VALUES (@agent,@tenant,'Historical agent',0,TRUE,@at);
            INSERT INTO "Jobs" ("Id","Name","FolderPath","TenantId","AgentId","ClientIdentity","CreatedAtUtc","UpdatedAtUtc","OptionsJson")
            VALUES (@job,'Historical job','/migration',@tenant,@agent,@identity,@at,@at,'{"executionPolicy":{"expectedRuntimeSeconds":60,"hardTimeoutSeconds":60}}');
            INSERT INTO "JobRuns" ("Id","JobId","TenantId","AgentId","ClientIdentity","StartedBy","Status","CurrentStepOrdinal","CreatedAtUtc","InputsJson","OptionsJson")
            VALUES (@job_run,@job,@tenant,@agent,@identity,@principal,0,0,@at,'{"argument":"kept","unknownInput":{"legacy":true}}','{"executionPolicy":{"expectedRuntimeSeconds":60,"hardTimeoutSeconds":60}}');
            INSERT INTO "JobRunControls" ("RunId","Revision","DispatchPreparedAtUtc","NativeDeadlineUtc","CancellationRequestedAtUtc","CancellationReason")
            VALUES (@job_run,2,@at,@deadline,@cancelled_at,'historical-request');
            """, connection, transaction);
        Add(command, ("tenant", tenant), ("agent", agent), ("at", at), ("job", job), ("job_run", jobRun),
            ("identity", agent.ToString("D")), ("principal", principal), ("deadline", at.AddMinutes(1)), ("cancelled_at", at.AddSeconds(2)));
        if (hasServices)
        {
            var service = new ClientServiceObservation("historical.service", "Historical service", ClientServicePlatform.LinuxSystemd,
                ClientServiceState.Running, "active/running", null, "loaded", "active", "running", "enabled", at);
            var collection = Guid.NewGuid();
            var cache = new ClientServicesState(new ClientKey(tenant, agent), 250, 7, 9,
                new(collection, 250, 7, at, at, [service]),
                new(collection, ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Complete, 250, 7, at, at, 3),
                [service], [service.Name], 3, Guid.NewGuid());
            var rawCache = JsonSerializer.SerializeToNode(cache)!.AsObject();
            rawCache["legacyUnknownCache"] = JsonNode.Parse("""{"opaque":"keep-cache","nested":[null,true,{"counter":18446744073709551615}],"escaped":"line\nkept"}""");
            rawCache["WatchedServices"]![0]!["legacyUnknownObservation"] = JsonNode.Parse("""{"opaque":"keep-service","raw":{"futureShape":[1,2,3]}}""");
            command.CommandText += """
                INSERT INTO "ClientConnectionEpochs" ("TenantId","AgentId","LastIssuedEpoch") VALUES (@tenant,@agent,500);
                INSERT INTO "ClientServicesSnapshots" ("TenantId","AgentId","ConnectionEpoch","LastAcceptedSequence","Revision","StateJson","UpdatedAtUtc")
                VALUES (@tenant,@agent,250,7,9,CAST(@cache AS jsonb),@at);
                """;
            command.Parameters.AddWithValue("cache", rawCache.ToJsonString());
        }
        (await command.ExecuteNonQueryAsync(ct)).Should().Be(hasServices ? 7 : 5);
        await transaction.CommitAsync(ct);
        var tables = hasServices ? FoundationTables.Concat(ServicesTables).ToArray() : FoundationTables;
        return new(tenant, agent, jobRun, hasServices, tables, migrationIds,
            await HistoricalSnapshotsAsync(connection, tables, ct), await MigrationHistorySnapshotAsync(connection, migrationIds, ct));
    }

    // Compare canonical PostgreSQL jsonb bytes, including unknown cache data. Only the
    // owner migration's new nullable columns are excluded from the original row shape.
    private static async Task<IReadOnlyDictionary<string, byte[]>> HistoricalSnapshotsAsync(NpgsqlConnection connection,
        IEnumerable<HistoricalTable> tables, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var table in tables)
        {
            await using var command = new NpgsqlCommand($"""
                SELECT convert_to(COALESCE(jsonb_agg(old_shape ORDER BY old_shape::text),'[]'::jsonb)::text,'UTF8')
                FROM (SELECT to_jsonb(r) - CAST(@added_columns AS text[]) AS old_shape FROM "{table.Name}" AS r) AS historical
                """, connection);
            command.Parameters.AddWithValue("added_columns", table.AddedColumns);
            result.Add(table.Name, (byte[])(await command.ExecuteScalarAsync(ct))!);
        }
        return result;
    }

    private static async Task<byte[]> MigrationHistorySnapshotAsync(NpgsqlConnection connection, string[] migrationIds, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT convert_to(COALESCE(jsonb_agg(to_jsonb(history) ORDER BY "MigrationId" COLLATE "C"),'[]'::jsonb)::text,'UTF8')
            FROM "__EFMigrationsHistory" AS history WHERE "MigrationId"=ANY(@ids)
            """, connection);
        command.Parameters.AddWithValue("ids", migrationIds);
        return (byte[])(await command.ExecuteScalarAsync(ct))!;
    }

    private static async Task AssertHistoricalRowsAsync(NpgsqlConnection connection, HistoricalRows history, CancellationToken ct)
    {
        var current = await HistoricalSnapshotsAsync(connection, history.Tables, ct);
        foreach (var table in history.Tables)
            current[table.Name].Should().Equal(history.Rows[table.Name], "every original column and raw cache value must survive unchanged");
        (await MigrationHistorySnapshotAsync(connection, history.MigrationIds, ct)).Should().Equal(history.MigrationHistory,
            "the exact predecessor migration IDs and ProductVersion values must survive unchanged");
    }

    private static readonly string[] NewTables = [ "ClientConnectionOwners", "ClientConnectionAdmissions" ];
    private static readonly string[] GuardFunctions = [ "public.nr_check_admission_immutable()", "public.nr_check_owner_acceptance()" ];
    private sealed record GuardTrigger(string Name, string Table, string Function, int Type, bool Deferrable, bool InitiallyDeferred);
    private static readonly GuardTrigger[] GuardTriggers =
    [
        new("nr_admission_body_immutable", "ClientConnectionAdmissions", "nr_check_admission_immutable", 19, false, false),
        new("nr_owner_acceptance_guard_insert", "ClientConnectionOwners", "nr_check_owner_acceptance", 5, true, true),
        new("nr_owner_acceptance_guard_update", "ClientConnectionOwners", "nr_check_owner_acceptance", 17, true, true)
    ];

    private static async Task AssertUpgradeAsync(string database, NpgsqlConnection connection, HistoricalRows history,
        MigrationPlan plan, CancellationToken ct)
    {
        await AssertCurrentModelAsync(database, plan, ct);
        await AssertServicesPresenceAsync(connection, present: true, ct);
        await AssertHistoricalRowsAsync(connection, history, ct);
        await AssertNoAuthorityRowsAsync(connection, ct);
        await using (var command = new NpgsqlCommand("""
            SELECT "DispatchPreparedAtUtc" IS NOT NULL AND "NativeDeadlineUtc" IS NOT NULL
                AND "CancellationRequestedAtUtc" IS NOT NULL AND "CancellationReason"='historical-request'
                AND "DispatchOwnerConnectionId" IS NULL AND "DispatchOwnerEpoch" IS NULL
            FROM "JobRunControls" WHERE "RunId"=@run
            """, connection))
        {
            command.Parameters.AddWithValue("run", history.JobRunId);
            ((bool)(await command.ExecuteScalarAsync(ct))!).Should().BeTrue();
        }
        if (history.HasServices)
        {
            await using var command = new NpgsqlCommand("""
                SELECT epochs."LastIssuedEpoch"=500 AND epochs."CancellationBarrierUntilUtc" IS NULL
                    AND cache."ConnectionEpoch"=250 AND cache."LastAcceptedSequence"=7 AND cache."Revision"=9
                    AND cache."StateJson" ? 'legacyUnknownCache'
                    AND cache."StateJson" #>> '{WatchedServices,0,legacyUnknownObservation,opaque}'='keep-service'
                FROM "ClientConnectionEpochs" AS epochs JOIN "ClientServicesSnapshots" AS cache USING ("TenantId","AgentId")
                WHERE epochs."TenantId"=@tenant AND epochs."AgentId"=@agent
                """, connection);
            Add(command, ("tenant", history.TenantId), ("agent", history.AgentId));
            ((bool)(await command.ExecuteScalarAsync(ct))!).Should().BeTrue();
        }
        await using (var command = new NpgsqlCommand("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema='public' AND is_nullable='YES' AND
                ((table_name='JobRunControls' AND column_name IN ('DispatchOwnerConnectionId','DispatchOwnerEpoch'))
                 OR (table_name='ClientConnectionEpochs' AND column_name='CancellationBarrierUntilUtc'))
            """, connection))
            ((long)(await command.ExecuteScalarAsync(ct))!).Should().Be(3);
        await AssertGuardsAsync(connection, present: true, ct);
        await AssertDefaultsAsync(connection, ct);
    }

    private static async Task AssertCurrentModelAsync(string database, MigrationPlan plan, CancellationToken ct)
    {
        await using var db = Context(database);
        (await db.Database.GetAppliedMigrationsAsync(ct)).Should().Equal(plan.CurrentMigrationIds);
        (await db.Database.GetPendingMigrationsAsync(ct)).Should().BeEmpty();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    private static async Task AssertNoAuthorityRowsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        foreach (var table in NewTables)
        {
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM \"{table}\"", connection);
            ((long)(await count.ExecuteScalarAsync(ct))!).Should().Be(0,
                "legacy allocator, Services cache and prepared Job intent must not manufacture a committed owner or admission");
        }
    }

    private static async Task AssertServicesPresenceAsync(NpgsqlConnection connection, bool present, CancellationToken ct)
    {
        foreach (var table in ServicesTables)
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NOT NULL", connection);
            command.Parameters.AddWithValue("table", $"public.\"{table.Name}\"");
            ((bool)(await command.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
    }

    private static async Task AssertAdditionsAbsentAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await AssertGuardsAsync(connection, present: false, ct);
        foreach (var table in NewTables)
        {
            await using var command = new NpgsqlCommand("SELECT to_regclass(@table) IS NULL", connection);
            command.Parameters.AddWithValue("table", $"public.\"{table}\"");
            ((bool)(await command.ExecuteScalarAsync(ct))!).Should().BeTrue();
        }
        foreach (var table in FoundationTables.Concat(ServicesTables).Where(table => table.AddedColumns.Length > 0))
        {
            await using var command = new NpgsqlCommand("""
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema='public' AND table_name=@table AND column_name=ANY(@columns)
                """, connection);
            Add(command, ("table", table.Name), ("columns", table.AddedColumns));
            ((long)(await command.ExecuteScalarAsync(ct))!).Should().Be(0);
        }
    }

    private static async Task AssertGuardsAsync(NpgsqlConnection connection, bool present, CancellationToken ct)
    {
        foreach (var function in GuardFunctions)
        {
            await using var command = new NpgsqlCommand("SELECT to_regprocedure(@function) IS NOT NULL", connection);
            command.Parameters.AddWithValue("function", function);
            ((bool)(await command.ExecuteScalarAsync(ct))!).Should().Be(present);
        }
        var actual = new List<GuardTrigger>();
        await using var triggers = new NpgsqlCommand("""
            SELECT tr.tgname,rel.relname,fn.proname,tr.tgtype::int,tr.tgdeferrable,tr.tginitdeferred
            FROM pg_catalog.pg_trigger AS tr
            JOIN pg_catalog.pg_class AS rel ON rel.oid=tr.tgrelid
            JOIN pg_catalog.pg_namespace AS ns ON ns.oid=rel.relnamespace
            JOIN pg_catalog.pg_proc AS fn ON fn.oid=tr.tgfoid
            JOIN pg_catalog.pg_namespace AS function_schema ON function_schema.oid=fn.pronamespace
            WHERE ns.nspname='public' AND function_schema.nspname='public'
                AND NOT tr.tgisinternal AND tr.tgenabled='O' AND tr.tgname=ANY(@names)
            ORDER BY tr.tgname COLLATE "C"
            """, connection);
        triggers.Parameters.AddWithValue("names", GuardTriggers.Select(trigger => trigger.Name).ToArray());
        await using var reader = await triggers.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            actual.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetBoolean(4), reader.GetBoolean(5)));
        actual.Should().Equal(present ? GuardTriggers.OrderBy(trigger => trigger.Name, StringComparer.Ordinal) : Enumerable.Empty<GuardTrigger>(),
            "owner guards must be AFTER ROW and initially deferred; the admission body guard must be BEFORE UPDATE ROW");
    }

    private static async Task<IReadOnlyDictionary<string, byte[]>> GuardDefinitionsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        var result = new Dictionary<string, byte[]>();
        foreach (var function in GuardFunctions)
        {
            await using var command = new NpgsqlCommand("SELECT convert_to(pg_get_functiondef(to_regprocedure(@function)),'UTF8')", connection);
            command.Parameters.AddWithValue("function", function);
            result.Add(function, (byte[])(await command.ExecuteScalarAsync(ct))!);
        }
        return result;
    }

    private static async Task AssertDefaultsAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        (string Table, string Column, string Type, string Value)[] defaults =
        [
            ("ClientConnectionOwners", "ConnectionEpoch", "bigint", "0"),
            ("ClientConnectionOwners", "Active", "boolean", "false"),
            ("ClientConnectionOwners", "LastHeartbeatSequence", "numeric(20,0)", "0"),
            ("ClientConnectionOwners", "OwnerRevision", "bigint", "0"),
            ("ClientConnectionOwners", "MetadataJson", "jsonb", "{}")
        ];
        foreach (var expected in defaults)
        {
            await using var metadata = new NpgsqlCommand("""
                SELECT pg_get_expr(definition.adbin,definition.adrelid)
                FROM pg_catalog.pg_attrdef AS definition
                JOIN pg_catalog.pg_attribute AS attribute ON attribute.attrelid=definition.adrelid AND attribute.attnum=definition.adnum
                WHERE definition.adrelid=to_regclass(@table) AND attribute.attname=@column
                """, connection);
            Add(metadata, ("table", $"public.\"{expected.Table}\""), ("column", expected.Column));
            var expression = (string?)await metadata.ExecuteScalarAsync(ct);
            expression.Should().NotBeNullOrEmpty();
            // Evaluate the genuine catalog default without inserting any authority row.
            await using var evaluate = new NpgsqlCommand($"SELECT ({expression})::{expected.Type}::text", connection);
            ((string)(await evaluate.ExecuteScalarAsync(ct))!).Should().Be(expected.Value);
        }
    }

    private static async Task<byte[]> OwnershipSchemaAsync(NpgsqlConnection connection, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand("""
            SELECT convert_to(jsonb_build_object(
                'columns', (SELECT jsonb_agg(jsonb_build_array(table_name,column_name,
                    data_type,udt_name,is_nullable,column_default,numeric_precision,numeric_scale,character_maximum_length)
                    ORDER BY table_name COLLATE "C",column_name COLLATE "C")
                    FROM information_schema.columns WHERE table_schema='public' AND
                        (table_name IN ('ClientConnectionOwners','ClientConnectionAdmissions') OR
                         (table_name='ClientConnectionEpochs' AND column_name='CancellationBarrierUntilUtc') OR
                         (table_name='JobRunControls' AND column_name IN ('DispatchOwnerConnectionId','DispatchOwnerEpoch')))),
                'constraints', (SELECT jsonb_agg(jsonb_build_array(rel.relname,con.conname,con.contype,
                    pg_get_constraintdef(con.oid),con.condeferrable,con.condeferred)
                    ORDER BY rel.relname COLLATE "C",con.conname COLLATE "C")
                    FROM pg_catalog.pg_constraint AS con JOIN pg_catalog.pg_class AS rel ON rel.oid=con.conrelid
                    JOIN pg_catalog.pg_namespace AS ns ON ns.oid=rel.relnamespace
                    WHERE ns.nspname='public' AND (rel.relname IN ('ClientConnectionOwners','ClientConnectionAdmissions')
                        OR con.conname='CK_JobRunControls_DispatchOwner')),
                'indexes', (SELECT jsonb_agg(jsonb_build_array(tablename,indexname,indexdef)
                    ORDER BY tablename COLLATE "C",indexname COLLATE "C") FROM pg_catalog.pg_indexes
                    WHERE schemaname='public' AND tablename IN ('ClientConnectionOwners','ClientConnectionAdmissions'))
            )::text,'UTF8')
            """, connection);
        return (byte[])(await command.ExecuteScalarAsync(ct))!;
    }

    private static void Add(NpgsqlCommand command, params (string Name, object Value)[] parameters)
    {
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
    }
}
