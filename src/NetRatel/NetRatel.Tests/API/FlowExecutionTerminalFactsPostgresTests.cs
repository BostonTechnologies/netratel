using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionTerminalFactsPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData(FlowExecutionSourceKind.Job, FlowExecutionSourceKind.Script)]
    [InlineData(FlowExecutionSourceKind.Script, FlowExecutionSourceKind.Job)]
    public async Task Colliding_native_identifiers_from_another_family_cannot_hide_the_recorded_terminal_fact(
        FlowExecutionSourceKind requested, FlowExecutionSourceKind other)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options);
        await db.Database.MigrateAsync(ct);
        const string executionId = "9007199254740993";
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        var expected = Fact(requested, executionId, now);
        Assert.True(FlowExecutionEventValidator.Validate(expected).Valid);
        db.OutboxMessages.Add(Row(expected));
        for (var ordinal = 1; ordinal <= 12; ordinal++)
            db.OutboxMessages.Add(Row(Fact(other, executionId, now.AddMilliseconds(ordinal))));
        var foreignTenant = Fact(requested, executionId, now.AddSeconds(3)) with { TenantId = 8 };
        db.OutboxMessages.Add(Row(foreignTenant));
        await db.SaveChangesAsync(ct);
        // The canonical writer rejects these malformed rows. Inject isolated storage corruption with
        // parameterized test SQL so the reader's independent identity/provenance defenses are exercised.
        var forged = Fact(requested, executionId, now.AddSeconds(1)) with { Provenance = Provenance(other) };
        await InsertCorruptRowAsync(db, Row(forged, source: Provenance(requested)), ct);
        var mismatchedEvent = Fact(requested, executionId, now.AddSeconds(2));
        await InsertCorruptRowAsync(db, Row(mismatchedEvent, rowId: Guid.NewGuid()), ct);
        db.ChangeTracker.Clear();

        var actual = await FlowExecutionTerminalFacts.ReadAsync(db, 7, requested, executionId, ct);

        Assert.NotNull(actual);
        Assert.Equal(expected.EventId, actual.EventId);
        Assert.Equal(requested, actual.SourceKind);
        Assert.Equal(Provenance(requested), actual.Provenance);
        Assert.Equal(executionId, actual.ExecutionId);
        Assert.Equal(FlowExecutionOutcome.Failed, actual.Outcome);
        var otherProvenance = Provenance(other);
        Assert.Equal(12, await db.OutboxMessages.CountAsync(row => row.EntityId == executionId &&
            row.Source == otherProvenance, ct));
    }

    private static Task<int> InsertCorruptRowAsync(OrchestratorDbContext db, OutboxMessage row, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "OutboxMessages"
                ("Id", "OccurredUtc", "Type", "PayloadJson", "Source", "CorrelationId", "TenantId", "EntityId", "Status", "Attempts")
            VALUES ({row.Id}, {row.OccurredUtc}, {row.Type}, {row.PayloadJson}, {row.Source},
                {row.CorrelationId}, {row.TenantId}, {row.EntityId}, {row.Status}, {row.Attempts})
            """, ct);

    private static FlowExecutionEventDto Fact(FlowExecutionSourceKind kind, string id, DateTimeOffset at) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 7, kind, new("41", "Synthetic definition"), id, id,
            new(Guid.Parse("5ff38796-0f23-44a8-9b19-fae4b45ba445")), FlowExecutionOutcome.Failed, "Failed", at, at)
        { Provenance = Provenance(kind), CompletedAtUtc = at };

    private static OutboxMessage Row(FlowExecutionEventDto fact, string? source = null, Guid? rowId = null) => new()
    {
        Id = rowId ?? fact.EventId, Type = FlowExecutionEventLimits.OutboxType,
        TenantId = fact.TenantId.ToString(CultureInfo.InvariantCulture), EntityId = fact.ExecutionId,
        Source = source ?? fact.Provenance, OccurredUtc = fact.OccurredAtUtc,
        PayloadJson = JsonSerializer.Serialize(fact, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto),
        Status = OutboxStatuses.Pending
    };

    private static string Provenance(FlowExecutionSourceKind kind) =>
        kind == FlowExecutionSourceKind.Job ? "job-terminal-ready" : "script-terminal-commit";
}
