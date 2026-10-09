using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class RatelDeskIdentityObservationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly Guid Installation = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Producer = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");

    [Fact]
    public async Task Pairing_automatically_preserves_existing_installation_and_Flow_producer_across_contexts()
    {
        var connection = await postgres.CreateDatabaseAsync(); await using var services = Services(connection);
        await using var first = Database(connection); await first.Database.MigrateAsync();
        first.Add(new InstallationIdentityRecord { InstanceId = Installation, Revision = 7 });
        first.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer }); await first.SaveChangesAsync();
        var store = Store(first, services);
        var adopted = await store.GetAsync(default);
        Assert.Equal(Installation, adopted.InstanceId); Assert.Equal(Producer, adopted.SourceInstanceId); Assert.Equal(8, adopted.Revision);
        Assert.Equal(Producer, (await first.FlowRuntimeIdentity.AsNoTracking().SingleAsync()).SourceInstanceId);
        await using var restarted = Database(connection);
        var current = await Store(restarted, services).GetAsync(default);
        Assert.Equal(adopted.InstanceId, current.InstanceId); Assert.Equal(adopted.SourceInstanceId, current.SourceInstanceId); Assert.Equal(adopted.Revision, current.Revision);
    }

    [Fact]
    public async Task Stale_tracked_null_is_not_used_as_current_installation_authority()
    {
        var connection = await postgres.CreateDatabaseAsync(); await using var services = Services(connection);
        await using var first = Database(connection); await first.Database.MigrateAsync();
        first.Add(new InstallationIdentityRecord { InstanceId = Installation }); first.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer }); await first.SaveChangesAsync();
        var stale = await first.Set<InstallationIdentityRecord>().SingleAsync(); Assert.Null(stale.SourceInstanceId);
        await using (var other = Database(connection)) await Store(other, services).GetAsync(default);
        var current = await Store(first, services).GetAsync(default);
        Assert.Equal(Producer, current.SourceInstanceId); Assert.Null(stale.SourceInstanceId);
    }

    [Fact]
    public async Task Foreign_producer_drift_fails_without_rewriting_durable_installation_or_Flow_identity()
    {
        var connection = await postgres.CreateDatabaseAsync(); await using var services = Services(connection); var foreign = Guid.NewGuid();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        db.Add(new InstallationIdentityRecord { InstanceId = Installation, SourceInstanceId = foreign, Revision = 9 });
        db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer }); await db.SaveChangesAsync();
        var failure = await Assert.ThrowsAsync<PairingException>(() => Store(db, services).GetAsync(default));
        Assert.Equal("producer-identity-drift", failure.Code);
        var persisted = await db.Set<InstallationIdentityRecord>().AsNoTracking().SingleAsync();
        Assert.Equal(Installation, persisted.InstanceId); Assert.Equal(foreign, persisted.SourceInstanceId); Assert.Equal(9, persisted.Revision);
        Assert.Equal(Producer, (await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync()).SourceInstanceId);
    }

    [Fact]
    public async Task Concurrent_adoption_during_Flow_read_rechecks_durable_identity_before_returning_authority()
    {
        var connection = await postgres.CreateDatabaseAsync(); await using var services = Services(connection); var foreign = Guid.NewGuid();
        await using var db = Database(connection); await db.Database.MigrateAsync();
        db.Add(new InstallationIdentityRecord { InstanceId = Installation }); db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer }); await db.SaveChangesAsync();
        var flow = new GatedFlow(new FlowPersistenceService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System));
        var store = new InstallationIdentityStore(db, flow, new Monitor());
        var pending = store.GetAsync(TestContext.Current.CancellationToken);
        await flow.Reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        await using (var other = Database(connection))
            await other.Set<InstallationIdentityRecord>().Where(x => x.Id == 1).ExecuteUpdateAsync(s => s.SetProperty(x => x.SourceInstanceId, foreign));
        flow.Release.TrySetResult();
        var failure = await Assert.ThrowsAsync<PairingException>(() => pending);
        Assert.Equal("producer-identity-drift", failure.Code);
        Assert.Equal(Producer, (await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync()).SourceInstanceId);
    }

    private static OrchestratorDbContext Database(string connection) => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
    private static InstallationIdentityStore Store(OrchestratorDbContext db, IServiceProvider provider) => new(db,
        new FlowPersistenceService(provider.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System), new Monitor());
    private static ServiceProvider Services(string connection)
    {
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(o => o.UseNpgsql(connection));
        return services.BuildServiceProvider();
    }
    private sealed class Monitor : IOptionsMonitor<ServiceIdentityOptions>
    { public ServiceIdentityOptions CurrentValue => new() { InstanceId = Installation.ToString("D") }; public ServiceIdentityOptions Get(string? name) => CurrentValue; public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null; }
    private sealed class GatedFlow(IFlowSourceIdentityResolver inner) : IFlowSourceIdentityResolver
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Guid> EnsureAsync(CancellationToken ct)
        { var producer = await inner.EnsureAsync(ct); Reached.TrySetResult(); await Release.Task.WaitAsync(ct); return producer; }
    }
}
