using System.Text.Json;
using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Shared.Contracts.Netclaw;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed partial class FlowPostgreSqlTests
{
    [Fact]
    public async Task Tombstone_migration_upgrades_existing_definitions_without_changing_their_state()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        var previous = db.Database.GetMigrations().TakeWhile(migration => !migration.EndsWith("_AddFlowDeletion", StringComparison.Ordinal)).Last();
        await db.GetService<IMigrator>().MigrateAsync(previous);
        var id = Guid.NewGuid(); var now = DateTimeOffset.UtcNow; var graphJson = "{}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "FlowDefinitions" ("Id","TenantId","Name","Revision","Enabled","DraftJson","PublishedVersionNumber","CreatedAtUtc","UpdatedAtUtc")
            VALUES ({id},{17},{"Synthetic pre-upgrade flow"},{4},{true},CAST({graphJson} AS jsonb),{0},{now},{now})
            """);
        await db.Database.MigrateAsync();
        var definition = await db.FlowDefinitions.SingleAsync();
        definition.Revision.Should().Be(4); definition.Enabled.Should().BeTrue();
        definition.DeletedAtUtc.Should().BeNull(); definition.DeletedBy.Should().BeNull(); definition.DeletedFromRevision.Should().BeNull();
        db.Database.HasPendingModelChanges().Should().BeFalse();
    }

    [Fact]
    public async Task Live_native_and_automated_AI_obligations_block_terminal_history_until_their_current_work_settles()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync();
        var admitted = await h.Ingress.EnqueueAsync(h.Event(version));
        await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var run = await db.FlowRuns.SingleAsync(); run.Status = FlowRunStatus.DeliveryUnknown; run.CompletedAtUtc = h.Clock.GetUtcNow();
        db.Jobs.Add(new() { Id = 1, TenantId = 17, Name = "Synthetic native obligation" });
        var native = new JobRunRecord { Id = 99, JobId = 1, TenantId = 17, Status = 0 };
        db.JobRuns.Add(native);
        db.Set<ExecutionAdmissionRecord>().Add(new() { ActionKey = Guid.NewGuid(), TenantId = 17, FlowRunId = run.Id,
            FlowVersionId = version.Id, Kind = 1, ExecutionId = "99", NodeId = Guid.NewGuid(),
            PrincipalId = "synthetic-operator", PayloadHash = new string('a',64), ProtectedPayloadJson = "retained", NodeAttempt = 1 });
        await db.SaveChangesAsync();
        var revision = (await h.Definitions.GetMetadataAsync(17, version.FlowId))!.Revision;
        (await h.Definitions.DeleteAsync(17, version.FlowId, new(revision), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.ActiveWork);
        native.Status = 6; native.CompletedAtUtc = h.Clock.GetUtcNow();
        // A terminal Flow cannot resume this uncertain script. Its retained Pending projection
        // and accepted ID must not make historical unknown delivery permanently undeletable.
        db.JobTaskActivities.Add(new() { RequestId = "synthetic-historical-script", TenantId = 17, TaskType = "ExecLibraryScript", Status = "Pending" });
        db.Set<ExecutionAdmissionRecord>().Add(new() { ActionKey = Guid.NewGuid(), TenantId = 17, FlowRunId = run.Id,
            FlowVersionId = version.Id, Kind = 2, ExecutionId = "synthetic-historical-script", NodeId = Guid.NewGuid(),
            DispatchStartedAtUtc = h.Clock.GetUtcNow().AddDays(-2), DispatchAcceptedAtUtc = h.Clock.GetUtcNow().AddDays(-2),
            PrincipalId = "synthetic-operator", PayloadHash = new string('b',64), ProtectedPayloadJson = "retained", NodeAttempt = 1 });
        var investigation = new AiInvestigationRecord { Id = Guid.NewGuid(), RequestId = Guid.NewGuid(), TenantId = 17,
            FlowRunId = run.Id, SourceActionKey = "synthetic-ai-obligation", Automated = true, State = InvestigationState.Processing, CreatedBy = "synthetic-operator" };
        db.Set<AiInvestigationRecord>().Add(investigation); await db.SaveChangesAsync();
        (await h.Definitions.DeleteAsync(17, version.FlowId, new(revision), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.ActiveWork);
        investigation.State = InvestigationState.DeliveryUnknown; await db.SaveChangesAsync();
        (await h.Definitions.DeleteAsync(17, version.FlowId, new(revision), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.Deleted);
        (await db.Set<ExecutionAdmissionRecord>().CountAsync()).Should().Be(2);
        (await db.Set<AiInvestigationRecord>().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Deletion_is_graph_free_revision_checked_repeatable_and_releases_exactly_one_slot()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var draft = (await h.Definitions.CreateAsync(17, new("Synthetic draft"), "author")).Definition!;
        await using var scope = h.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        // No graph authoring or parsing is required, even for a future/unreadable stored graph.
        var row = await db.FlowDefinitions.SingleAsync(row => row.Id == draft.Id);
        row.DraftJson = "{\"schemaVersion\":2,\"privatePinnedPrompt\":\"never disclose\"}";
        for (var i = 1; i < FlowLimits.MaximumFlowsPerTenant; i++)
            db.FlowDefinitions.Add(new() { Id = Guid.NewGuid(), TenantId = 17, Name = $"Capacity {i:D3}", Revision = 1,
                DraftJson = "{}", CreatedAtUtc = h.Clock.GetUtcNow(), UpdatedAtUtc = h.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        (await h.Definitions.CreateAsync(17, new("Over capacity"), "author")).Disposition.Should().Be(FlowWriteDisposition.CapacityExceeded);
        (await h.Definitions.DeleteAsync(18, draft.Id, new(1), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.NotFound);
        (await h.Definitions.DeleteAsync(17, draft.Id, new(2), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.Conflict);
        (await h.Definitions.GetMetadataAsync(17, draft.Id))!.Enabled.Should().BeTrue();
        var deleted = await h.Definitions.DeleteAsync(17, draft.Id, new(1), "deleter");
        deleted.Disposition.Should().Be(FlowDeleteDisposition.Deleted); deleted.Revision.Should().Be(2);
        JsonSerializer.Serialize(deleted).Should().NotContain("Draft").And.NotContain("never disclose");
        var repeat = await h.Definitions.DeleteAsync(17, draft.Id, new(1), "other-current-deleter");
        repeat.Should().Be(deleted with { Disposition = FlowDeleteDisposition.AlreadyDeleted, Code = "flow-already-deleted" });
        (await h.Definitions.DeleteAsync(17, draft.Id, new(2), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.Conflict);
        (await h.Definitions.GetAsync(17, draft.Id)).Should().BeNull();
        (await h.Definitions.SetEnabledAsync(17, draft.Id, new(2, true), "author")).Disposition.Should().Be(FlowWriteDisposition.NotFound);
        (await h.Definitions.CloneAsync(17, draft.Id, new(2, "Clone"), "author")).Disposition.Should().Be(FlowWriteDisposition.NotFound);
        (await h.Definitions.PublishAsync(17, draft.Id, new(2), FlowTestData.Authority)).Disposition.Should().Be(FlowWriteDisposition.NotFound);
        (await h.Definitions.CreateAsync(17, new("One freed slot"), "author")).Disposition.Should().Be(FlowWriteDisposition.Stored);
        (await h.Definitions.CreateAsync(17, new("Still bounded"), "author")).Disposition.Should().Be(FlowWriteDisposition.CapacityExceeded);
        db.ChangeTracker.Clear(); row = await db.FlowDefinitions.SingleAsync(row => row.Id == draft.Id);
        row.DeletedBy.Should().Be("deleter"); row.Enabled.Should().BeFalse(); row.DraftJson.Should().Contain("never disclose");
        (await db.FlowAudits.CountAsync(audit => audit.FlowId == draft.Id && audit.Operation == "deleted")).Should().Be(1);
        (await h.Definitions.QueryAsync(17, new())).Total.Should().Be(128);
    }

    [Theory]
    [InlineData(FlowRunStatus.Queued)]
    [InlineData(FlowRunStatus.Running)]
    [InlineData(FlowRunStatus.RetryWaiting)]
    [InlineData(FlowRunStatus.WaitingForAi)]
    [InlineData(FlowRunStatus.AwaitingApproval)]
    [InlineData(FlowRunStatus.WaitingForExecution)]
    public async Task Every_actual_nonterminal_state_conflicts_without_partially_disabling(FlowRunStatus status)
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync();
        var admitted = await h.Ingress.EnqueueAsync(h.Event(version));
        await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var run = await db.FlowRuns.SingleAsync(); run.Status = status;
        // CompletedAt is deliberately misleading. Current status owns scheduling.
        run.CompletedAtUtc = h.Clock.GetUtcNow(); await db.SaveChangesAsync();
        var current = (await h.Definitions.GetMetadataAsync(17, version.FlowId))!;
        var result = await h.Definitions.DeleteAsync(17, version.FlowId, new(current.Revision), "deleter");
        result.Disposition.Should().Be(FlowDeleteDisposition.ActiveWork);
        (await h.Definitions.GetMetadataAsync(17, version.FlowId)).Should().Be(current);
        (await db.FlowAudits.CountAsync(row => row.Operation == "deleted")).Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Terminal_unknown_retains_evidence_and_history_but_a_live_lease_blocks(bool schemaTwo)
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync();
        var input = h.Event(version); var admitted = await h.Ingress.EnqueueAsync(input);
        await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var run = await db.FlowRuns.SingleAsync(); run.Status = FlowRunStatus.DeliveryUnknown;
        run.CompletedAtUtc = h.Clock.GetUtcNow().AddDays(-91); run.ContinuationJson = "{\"retainedEvidence\":true}";
        var definition = await db.FlowDefinitions.SingleAsync();
        if (schemaTwo) definition.DraftJson = "{\"schemaVersion\":2}";
        run.LeaseExpiresAtUtc = h.Clock.GetUtcNow().AddMinutes(10); await db.SaveChangesAsync();
        var current = (await h.Definitions.GetMetadataAsync(17, version.FlowId))!;
        (await h.Definitions.DeleteAsync(17, version.FlowId, new(current.Revision), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.ActiveWork);
        run.LeaseExpiresAtUtc = null; await db.SaveChangesAsync(); await db.Entry(run).ReloadAsync();
        if (!schemaTwo)
        {
            await h.Definitions.SetEnabledAsync(17, version.FlowId, new(current.Revision, false), "author");
            current = (await h.Definitions.GetMetadataAsync(17, version.FlowId))!;
        }
        (await h.Definitions.DeleteAsync(17, version.FlowId, new(current.Revision), "deleter")).Disposition.Should().Be(FlowDeleteDisposition.Deleted);
        (await h.Definitions.GetVersionsAsync(17, version.FlowId)).Should().ContainSingle();
        (await h.Definitions.GetRunsAsync(17, version.FlowId)).Single().CompletedAtUtc.Should().Be(run.CompletedAtUtc);
        (await h.Ingress.EnqueueAsync(input)).Disposition.Should().Be(FlowIngressDisposition.Duplicate);
        (await h.Ingress.EnqueueAsync(h.Event(version))).Disposition.Should().Be(FlowIngressDisposition.Disabled);
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull();
        await h.Store.PruneHistoryAsync();
        (await h.Ingress.FindOutcomeAsync(17, input.EventId, version.Id))!.Status.Should().Be(FlowRunStatus.DeliveryUnknown);
        (await h.Definitions.ListAsync(17)).Should().BeEmpty();
    }

    [Fact]
    public async Task Summary_query_filters_full_candidates_and_sorts_latest_run_before_paging()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync();
        await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = h.Clock.GetUtcNow();
        var rows = Enumerable.Range(0, 104).Select(i => new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = 17,
            Name = i == 103 ? "Z literal %_\\ match" : $"Name {i / 2:D3}", Revision = 1, Enabled = i % 2 == 0,
            PublishedVersionId = i % 3 == 0 ? Guid.NewGuid() : null, PublishedVersionNumber = i % 3 == 0 ? 1 : 0,
            DraftJson = "{\"privateGraph\":true}", CreatedAtUtc = now, UpdatedAtUtc = now.AddSeconds(i) }).ToArray();
        db.FlowDefinitions.AddRange(rows); await db.SaveChangesAsync();
        foreach (var definition in rows.Where(row => row.PublishedVersionId is not null))
            db.FlowVersions.Add(new() { Id = definition.PublishedVersionId!.Value, TenantId = 17, FlowId = definition.Id,
                VersionNumber = 1, GraphJson = FlowGraphSerialization.Serialize(version.Graph), ConfigurationHash = version.ConfigurationHash,
                PublishedBy = "synthetic-author", PublishedAtUtc = now });
        await db.SaveChangesAsync();
        for (var i = 0; i < 4; i++) db.FlowRuns.Add(new() { Id = Guid.NewGuid(), TenantId = 17, FlowId = rows[90 + i * 3].Id,
            FlowVersionId = rows[90 + i * 3].PublishedVersionId!.Value, EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid(), Status = FlowRunStatus.Succeeded,
            CreatedAtUtc = now.AddSeconds(i), CompletedAtUtc = now, EventJson = "{}", EventFingerprint = new string('a', 64) });
        var tiedLatest = Guid.Parse("ffffffff-ffff-ffff-ffff-ffffffffffff");
        db.FlowRuns.Add(new() { Id = tiedLatest, TenantId = 17, FlowId = rows[99].Id, FlowVersionId = rows[99].PublishedVersionId!.Value,
            EventId = Guid.NewGuid(), OccurrenceId = Guid.NewGuid(), Status = FlowRunStatus.Succeeded, CreatedAtUtc = now.AddSeconds(3),
            CompletedAtUtc = now, EventJson = "{}", EventFingerprint = new string('b',64) });
        await db.SaveChangesAsync();
        var recorder = new SummarySqlRecorder();
        await using var measuredProvider = h.NewProvider(recorder);
        var first = await measuredProvider.GetRequiredService<IFlowDefinitionService>().QueryAsync(17, new(Sort: FlowSort.LatestRunAt));
        first.Total.Should().Be(105); first.Items.Should().HaveCount(20);
        recorder.Commands.Should().HaveCount(2, "one count and one bounded relational page, without per-row reads");
        var command = recorder.Commands.Last();
        command.Sql.Should().NotContain("DraftJson").And.NotContain("GraphJson").And.Contain("LIMIT");
        await using (var explainConnection = new NpgsqlConnection(h.Connection))
        {
            await explainConnection.OpenAsync();
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + command.Sql, explainConnection);
            foreach (var parameter in command.Parameters) explain.Parameters.AddWithValue(parameter.Name, parameter.Value);
            using var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync())!);
            var measurement = plan.RootElement[0];
            measurement.GetProperty("Plan").GetProperty("Actual Rows").GetInt32().Should().Be(20);
            Console.WriteLine($"Flow summary PostgreSQL page: {measurement.GetProperty("Execution Time").GetDouble():F3}ms, two commands, 20 projected rows.");
        }
        first = await h.Definitions.QueryAsync(17, new());
        var second = await h.Definitions.QueryAsync(17, new(PageIndex: 1));
        first.Items.Select(row => row.Id).Intersect(second.Items.Select(row => row.Id)).Should().BeEmpty();
        (await h.Definitions.QueryAsync(17, new(PageSize: 50))).Items.Should().HaveCount(50);
        (await h.Definitions.QueryAsync(17, new(PageSize: 100))).Items.Should().HaveCount(100);
        var literal = await h.Definitions.QueryAsync(17, new(Search: "  LITERAL %_\\  ")); literal.Total.Should().Be(1);
        literal.Items.Single().Id.Should().Be(rows[103].Id);
        var filtered = await h.Definitions.QueryAsync(17, new(Enabled: FlowEnabledFilter.Disabled, Publication: FlowPublicationFilter.Published));
        filtered.Total.Should().Be(rows.Count(row => !row.Enabled && row.PublishedVersionId != null));
        foreach (var descending in new[] { false, true })
        {
            var sorted = await h.Definitions.QueryAsync(17, new(Sort: FlowSort.LatestRunAt, Descending: descending, PageSize: 2));
            sorted.Items.Select(row => row.Id).Should().Equal(descending ? [rows[99].Id, rows[96].Id] : new[] { rows[90].Id, rows[93].Id });
            if (descending) sorted.Items[0].LatestRun!.Id.Should().Be(tiedLatest);
            var tail = await h.Definitions.QueryAsync(17, new(Sort: FlowSort.LatestRunAt, Descending: descending, PageIndex: 5));
            tail.Items.Should().OnlyContain(row => row.LatestRun == null);
        }
        JsonSerializer.Serialize(first).Should().NotContain("privateGraph").And.NotContain("DraftJson");
        foreach (var invalid in new[] { new FlowQueryDto(PageIndex: int.MaxValue), new(PageSize: 0), new(PageSize: 101), new(Search: new string('x',129)), new(Sort: (FlowSort)42) })
            await Assert.ThrowsAsync<ArgumentException>(() => h.Definitions.QueryAsync(17, invalid));
    }
    private sealed class SummarySqlRecorder : DbCommandInterceptor
    {
        public List<(string Sql, (string Name, object Value)[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<DbParameter>().Select(parameter => (parameter.ParameterName, parameter.Value!)).ToArray()));
            return ValueTask.FromResult(result);
        }
    }

}
