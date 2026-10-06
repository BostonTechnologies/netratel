using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class RatelDeskIdentityObservationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly Guid Installation = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Producer = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid ForeignProducer = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static ClaimsPrincipal Human => new(new ClaimsIdentity([new("netratel_principal_id", "owner")], "Oidc"));

    [Fact]
    public async Task Repeated_identity_observation_sees_other_context_adoption_despite_an_old_tracked_null()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var before = await rig.Identity.GetAsync(default);
        before.SourceInstanceId.Should().BeNull();
        var tracked = rig.Db.ChangeTracker.Entries<ServiceLinkRuntimeIdentity>().Single().Entity;
        tracked.SourceInstanceId.Should().BeNull();

        await rig.AdoptFromOtherContextAsync(ForeignProducer, before.Revision);

        var current = await rig.Identity.GetAsync(default);
        current.InstanceId.Should().Be(Installation.ToString("D"));
        current.SourceInstanceId.Should().Be(ForeignProducer.ToString("D"));
        current.Revision.Should().Be(before.Revision + 1);
        // Observation does not repurpose a preexisting tracked entity as an authority cache.
        tracked.SourceInstanceId.Should().BeNull();
        (await rig.Flow.EnsureAsync(default)).Should().Be(Producer);
    }

    [Fact]
    public async Task Stale_tracked_adoption_cannot_overwrite_a_producer_committed_by_another_human_scope()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var original = await rig.Identity.GetAsync(default);
        await rig.AdoptFromOtherContextAsync(Producer, original.Revision);

        Func<Task> replace = () => rig.Identity.AdoptSourceAsync(Human, ForeignProducer, original.Revision, default);
        var failure = await replace.Should().ThrowExactlyAsync<ServiceLinkProtocolException>();
        failure.Which.Code.Should().Be("identity-revision-conflict");
        var durable = await rig.Db.Set<ServiceLinkRuntimeIdentity>().AsNoTracking().SingleAsync();
        durable.InstanceId.Should().Be(Installation);
        durable.SourceInstanceId.Should().Be(Producer);
        durable.SourceAdoptedBy.Should().Be("owner");
        (await rig.Flow.EnsureAsync(default)).Should().Be(Producer);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Manual_continuity_rechecks_real_adoption_after_a_delayed_actual_Flow_read(bool adoptForeign)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var before = await rig.Identity.GetAsync(default);
        var flow = new GatedFlow(rig.Flow);
        var continuity = new RatelDeskProducerContinuity(flow, new RatelDeskInstallationIdentityReader(rig.Identity));
        var pending = continuity.RequireCurrentAsync(Producer, TestContext.Current.CancellationToken);
        await flow.Reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { await rig.AdoptFromOtherContextAsync(adoptForeign ? ForeignProducer : Producer, before.Revision); }
        finally { flow.Release.TrySetResult(); }

        if (adoptForeign)
        {
            Func<Task> finish = () => pending;
            await finish.Should().ThrowExactlyAsync<UnauthorizedAccessException>()
                .WithMessage("explicit-source-mapping-and-reapproval-required");
        }
        else await pending;
        var current = await rig.Identity.GetAsync(default);
        current.InstanceId.Should().Be(Installation.ToString("D"));
        current.SourceInstanceId.Should().Be((adoptForeign ? ForeignProducer : Producer).ToString("D"));
        (await rig.Flow.EnsureAsync(default)).Should().Be(Producer);
    }

    [Fact]
    public async Task Managed_capture_sees_foreign_adoption_after_its_actual_Flow_read_and_never_resolves_a_grant()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var before = await rig.Identity.GetAsync(default);
        var flow = new GatedFlow(rig.Flow);
        var connector = new RatelDeskConnectorState(Guid.NewGuid(), 17, 1, 1, "owner",
            new("Desk", "https://receiver.example", "org", "customer", null, [], new(), true), null, 0);
        var resolver = new ManagedRatelDeskBindingResolver(null!, rig.Identity, flow, new ConnectorAuthority(), null!);
        // There is no approved link in this database. The source denial must happen before
        // the unused profile dependency; this is not a fabricated managed capability.
        var pending = resolver.CaptureAsync(connector,
            new(RatelDeskAuthenticationMode.ManagedServiceLink, Guid.NewGuid().ToString("D")), Producer,
            TestContext.Current.CancellationToken);
        await flow.Reached.Task.WaitAsync(TestContext.Current.CancellationToken);
        try { await rig.AdoptFromOtherContextAsync(ForeignProducer, before.Revision); }
        finally { flow.Release.TrySetResult(); }

        Func<Task> finish = () => pending;
        await finish.Should().ThrowExactlyAsync<InvalidOperationException>().WithMessage("flow-source-adoption-required");
        (await rig.Identity.GetAsync(default)).SourceInstanceId.Should().Be(ForeignProducer.ToString("D"));
        (await rig.Flow.EnsureAsync(default)).Should().Be(Producer);
    }

    private sealed class GatedFlow(IFlowSourceIdentityResolver actual) : IFlowSourceIdentityResolver
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<Guid> EnsureAsync(CancellationToken ct)
        {
            var persisted = await actual.EnsureAsync(ct);
            Reached.TrySetResult();
            await Release.Task.WaitAsync(ct);
            return persisted;
        }
    }

    private sealed class Rig(ServiceProvider provider, IServiceScope scope, OrchestratorDbContext db,
        ServiceLinkIdentityStore identity, IFlowSourceIdentityResolver flow) : IAsyncDisposable
    {
        public OrchestratorDbContext Db => db;
        public ServiceLinkIdentityStore Identity => identity;
        public IFlowSourceIdentityResolver Flow => flow;
        public async Task AdoptFromOtherContextAsync(Guid source, long revision)
        {
            await using var other = provider.CreateAsyncScope();
            var result = await other.ServiceProvider.GetRequiredService<ServiceLinkIdentityStore>()
                .AdoptSourceAsync(Human, source, revision, TestContext.Current.CancellationToken);
            result.SourceInstanceId.Should().Be(source.ToString("D"));
        }
        public static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var services = new ServiceCollection();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection));
            services.AddSingleton(TimeProvider.System);
            // Explicit policy seam: these cases exercise real durable stores and contexts,
            // not HTTP/JWT or production principal authorization acceptance.
            services.AddSingleton<IEffectiveAccessService>(new InstanceAuthority());
            services.AddSingleton<IOptionsMonitor<ServiceIdentityOptions>>(new Options<ServiceIdentityOptions>(new()));
            services.AddSingleton<IOptionsMonitor<ServiceLinkOptions>>(new Options<ServiceLinkOptions>(new()));
            services.AddScoped<ServiceLinkIdentityStore>(); services.AddSingleton<FlowPersistenceService>();
            var provider = services.BuildServiceProvider(); var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // The real new appended migration is mandatory; do not bypass pending history.
            await db.Database.MigrateAsync();
            db.Set<ServiceLinkRuntimeIdentity>().Add(new() { InstanceId = Installation });
            db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer });
            await db.SaveChangesAsync();
            return new(provider, scope, db, scope.ServiceProvider.GetRequiredService<ServiceLinkIdentityStore>(),
                provider.GetRequiredService<FlowPersistenceService>());
        }
        public async ValueTask DisposeAsync() { scope.Dispose(); await provider.DisposeAsync(); }
    }
    private sealed class ConnectorAuthority : IRatelDeskConnectorAuthorization
    {
        public Task<bool> CanManageAsync(ClaimsPrincipal actor, int tenant, CancellationToken ct) => Task.FromResult(false);
        public Task<bool> CanExecuteAsync(string principal, string? credential, int tenant, CancellationToken ct) =>
            Task.FromResult(principal == "owner" && tenant == 17 && credential is null);
    }
    private sealed class InstanceAuthority : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal actor, string permission, int? tenant, CancellationToken ct = default) =>
            Task.FromResult(actor.Identity?.IsAuthenticated == true && tenant is null && permission == NetRatelPermissions.IntegrationManagement);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, int? tenant, CancellationToken ct = default) =>
            Task.FromResult(new EffectiveAccessSnapshot(null, false, false, new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Options<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue => value; public T Get(string? name) => value;
        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
