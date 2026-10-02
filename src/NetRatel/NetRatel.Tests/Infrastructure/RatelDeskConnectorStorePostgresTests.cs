using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.RatelDesk;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class RatelDeskConnectorStorePostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Actual_migration_preserves_tenant_ciphertext_and_separate_revisions_across_scope_restart()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var id = Guid.NewGuid();
        var configuration = new RatelDeskConnectorConfiguration("Helpdesk", "https://helpdesk.example", "organization", "requester", null, [], new(), true);
        var initial = new RatelDeskConnectorState(id, 4, 1, 1, "owner", configuration, "protected-credential", 1);
        await using (var db = new OrchestratorDbContext(options))
        {
            Assert.False(db.Database.HasPendingModelChanges()); await db.Database.MigrateAsync();
            Assert.True(await new RatelDeskConnectorStore(db).SaveAsync(initial, 0, CancellationToken.None));
            Assert.DoesNotContain("ProtectedCredential", (await db.RatelDeskConnectors.SingleAsync()).ConfigurationJson);
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            var store = new RatelDeskConnectorStore(db); var current = await store.GetAsync(4, id, CancellationToken.None);
            Assert.Equal(JsonSerializer.Serialize(initial), JsonSerializer.Serialize(current)); Assert.Null(await store.GetAsync(5, id, CancellationToken.None));
            Assert.True(await store.SaveAsync(current! with { RowVersion = 2, CredentialRevision = 2, ProtectedCredential = "rotated" }, 1, CancellationToken.None));
        }
        await using (var db = new OrchestratorDbContext(options))
        {
            var current = (await new RatelDeskConnectorStore(db).GetAsync(4, id, CancellationToken.None))!;
            Assert.Equal(1, current.Revision); Assert.Equal(2, current.CredentialRevision); Assert.Equal("rotated", current.ProtectedCredential);
            Assert.Equal(configuration.CustomerId, current.Configuration.CustomerId);
        }
    }

    [Fact]
    public async Task Stale_competing_writes_and_same_revision_mapping_changes_fail_without_overwriting()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var id = Guid.NewGuid(); var configuration = new RatelDeskConnectorConfiguration("Helpdesk", "https://helpdesk.example", "organization", "requester", null, [], new(), true);
        var initial = new RatelDeskConnectorState(id, 4, 1, 1, "owner", configuration, null, 0);
        await using var firstDb = new OrchestratorDbContext(options); await firstDb.Database.MigrateAsync();
        var first = new RatelDeskConnectorStore(firstDb); Assert.True(await first.SaveAsync(initial, 0, CancellationToken.None));
        await using var secondDb = new OrchestratorDbContext(options); var second = new RatelDeskConnectorStore(secondDb);
        var competing = await second.GetAsync(4, id, CancellationToken.None);
        Assert.True(await first.SaveAsync(initial with { Revision = 2, RowVersion = 2, Configuration = configuration with { CustomerId = "changed" } }, 1, CancellationToken.None));
        Assert.False(await second.SaveAsync(competing! with { Revision = 2, RowVersion = 2, Configuration = configuration with { OrganizationId = "changed" } }, 1, CancellationToken.None));
        var current = (await second.GetAsync(4, id, CancellationToken.None))!;
        Assert.False(await second.SaveAsync(current with { RowVersion = 3, Configuration = configuration }, 2, CancellationToken.None));
        Assert.Equal("changed", (await second.GetAsync(4, id, CancellationToken.None))!.Configuration.CustomerId);
    }

    [Fact]
    public async Task Parallel_different_ID_admissions_serialize_the_last_slot_and_reject_the_loser_visibly()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var configuration = new RatelDeskConnectorConfiguration("Desk", "https://desk.example", "org", "customer", null, [], new(), true);
        await using (var seed = new OrchestratorDbContext(options))
        {
            await seed.Database.MigrateAsync();
            seed.RatelDeskConnectors.AddRange(Enumerable.Range(0, RatelDeskConnectorLimits.MaximumConnectorsPerTenant - 1)
                .Select(_ => Record(Guid.NewGuid(), configuration)));
            await seed.SaveChangesAsync();
        }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var gate = new NpgsqlConnection(connection); await gate.OpenAsync(deadline.Token);
        await using var transaction = await gate.BeginTransactionAsync(deadline.Token);
        await using (var acquire = new NpgsqlCommand("SELECT pg_advisory_xact_lock(733460001, 4)", gate, transaction))
            await acquire.ExecuteNonQueryAsync(deadline.Token);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var tasks = ids.Select(AdmitAsync).ToArray();
        var released = false;
        try
        {
            // Observe both real PostgreSQL lock waits before releasing either request, rather than rely on scheduling a count race.
            await using var observer = new NpgsqlConnection(connection); await observer.OpenAsync(deadline.Token);
            await using var waits = new NpgsqlCommand("SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = 733460001::oid AND objid = 4::oid AND objsubid = 2 AND NOT granted", observer);
            while ((long)(await waits.ExecuteScalarAsync(deadline.Token))! != 2)
            {
                Assert.All(tasks, task => Assert.False(task.IsCompleted));
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            Assert.All(tasks, task => Assert.False(task.IsCompleted));
            await transaction.CommitAsync(deadline.Token); released = true;
            var outcomes = await Task.WhenAll(tasks).WaitAsync(deadline.Token);
            Assert.Single(outcomes, value => value == "saved");
            Assert.Single(outcomes, value => value == "connector-capacity-exhausted");
            await using var check = new OrchestratorDbContext(options);
            var store = new RatelDeskConnectorStore(check);
            var visible = await store.ListAsync(4, deadline.Token);
            Assert.Equal(RatelDeskConnectorLimits.MaximumConnectorsPerTenant, visible.Count);
            Assert.Equal(RatelDeskConnectorLimits.MaximumConnectorsPerTenant, await check.RatelDeskConnectors.CountAsync(deadline.Token));
            for (var index = 0; index < ids.Length; index++)
                Assert.Equal(outcomes[index] == "saved", visible.Any(row => row.Id == ids[index]));
        }
        finally
        {
            if (!released) await transaction.RollbackAsync(CancellationToken.None);
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        }

        async Task<string> AdmitAsync(Guid id)
        {
            await using var db = new OrchestratorDbContext(options);
            var state = new RatelDeskConnectorState(id, 4, 1, 1, "owner", configuration, null, 0);
            try { return await new RatelDeskConnectorStore(db).SaveAsync(state, 0, deadline.Token) ? "saved" : "conflict"; }
            catch (InvalidOperationException e) when (e.Message == "connector-capacity-exhausted") { return e.Message; }
        }
    }

    [Fact]
    public async Task Existing_overflow_fails_listing_instead_of_silently_hiding_a_connector()
    {
        var connection = await fixture.CreateDatabaseAsync();
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        await using var db = new OrchestratorDbContext(options); await db.Database.MigrateAsync();
        var configuration = new RatelDeskConnectorConfiguration("Desk", "https://desk.example", "org", "customer", null, [], new(), true);
        db.RatelDeskConnectors.AddRange(Enumerable.Range(0, RatelDeskConnectorLimits.MaximumConnectorsPerTenant + 1)
            .Select(_ => Record(Guid.NewGuid(), configuration)));
        await db.SaveChangesAsync();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => new RatelDeskConnectorStore(db).ListAsync(4, CancellationToken.None));
        Assert.Equal("connector-capacity-exhausted", error.Message);
        Assert.Equal(RatelDeskConnectorLimits.MaximumConnectorsPerTenant + 1, await db.RatelDeskConnectors.CountAsync());
    }

    private static RatelDeskConnectorRecord Record(Guid id, RatelDeskConnectorConfiguration configuration) => new()
    {
        Id = id, TenantId = 4, Revision = 1, RowVersion = 1, OwnerPrincipalId = "owner",
        ConfigurationJson = JsonSerializer.Serialize(configuration), CredentialRevision = 0
    };
}
