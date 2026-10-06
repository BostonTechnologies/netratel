using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ServiceLinkIdentityObservationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Observational_read_sees_another_contexts_committed_adoption_despite_a_tracked_null_source()
    {
        using var fixture = await IdentityFixture.CreateAsync(postgres);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var observingDb = fixture.CreateDb();
        await using var observingOwner = fixture.CreateOwnerDb();
        var observing = fixture.CreateStore(observingDb, observingOwner);
        var initial = await observing.GetAsync(deadline.Token);
        var tracked = await observingDb.Set<ServiceLinkRuntimeIdentity>().SingleAsync(deadline.Token);
        Assert.Null(initial.SourceInstanceId);
        Assert.Null(tracked.SourceInstanceId);
        Assert.Equal(initial.Revision, tracked.Revision);

        var adoptedAt = fixture.Clock.GetUtcNow();
        var committed = await fixture.AdoptInAnotherContextAsync(initial.Revision, deadline.Token);
        Assert.Null(tracked.SourceInstanceId);
        Assert.Equal(initial.Revision, tracked.Revision);

        var observed = await observing.GetAsync(deadline.Token);
        Assert.Equal(committed, observed);
        Assert.Equal(initial.InstanceId, observed.InstanceId);
        Assert.Equal(IdentityFixture.ApprovedProducer.ToString("D"), observed.SourceInstanceId);
        Assert.Equal(initial.Revision + 1, observed.Revision);
        // An observational read must not depend on silently refreshing this identity-map entry.
        Assert.Null(tracked.SourceInstanceId);
        Assert.Equal(initial.Revision, tracked.Revision);
        await fixture.AssertCommittedAsync(committed, adoptedAt, deadline.Token);
    }

    [Theory]
    [InlineData(false, "identity-revision-conflict")]
    [InlineData(true, "source-identity-conflict")]
    public async Task Stale_tracked_null_source_cannot_replace_another_contexts_completed_adoption(
        bool useCurrentRevision, string expectedCode)
    {
        using var fixture = await IdentityFixture.CreateAsync(postgres);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var staleDb = fixture.CreateDb();
        await using var staleOwner = fixture.CreateOwnerDb();
        var stale = fixture.CreateStore(staleDb, staleOwner);
        var initial = await stale.GetAsync(deadline.Token);
        var tracked = await staleDb.Set<ServiceLinkRuntimeIdentity>().SingleAsync(deadline.Token);
        Assert.Null(tracked.SourceInstanceId);

        var adoptedAt = fixture.Clock.GetUtcNow();
        var committed = await fixture.AdoptInAnotherContextAsync(initial.Revision, deadline.Token);
        Assert.Null(tracked.SourceInstanceId);
        Assert.Equal(initial.Revision, tracked.Revision);
        fixture.Clock.Advance(TimeSpan.FromMinutes(2));
        var expectedRevision = useCurrentRevision ? committed.Revision : initial.Revision;

        var error = await Assert.ThrowsAsync<ServiceLinkProtocolException>(() => stale.AdoptSourceAsync(
            fixture.Actor, IdentityFixture.DifferentProducer, expectedRevision, deadline.Token));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(committed, await stale.GetAsync(deadline.Token));
        await fixture.AssertCommittedAsync(committed, adoptedAt, deadline.Token);
    }

    private sealed class IdentityFixture(
        DbContextOptions<OrchestratorDbContext> applicationOptions,
        DbContextOptions<NetRatelIdentityDbContext> ownerOptions) : IDisposable
    {
        public static readonly Guid Installation = Guid.Parse("9b32d29f-319e-4bce-8e9a-149d46110879");
        public static readonly Guid ApprovedProducer = Guid.Parse("81dcaf04-e1d3-4922-99f2-b55f52a685bc");
        public static readonly Guid DifferentProducer = Guid.Parse("f35bd6a7-9e9d-41c2-b83a-0810a975b9ae");
        private const string ActorId = "synthetic-instance-owner";
        private readonly OptionsMonitor<ServiceIdentityOptions> identity = Monitor<ServiceIdentityOptions>(options =>
        {
            options.Enabled = true;
            options.InstanceId = Installation.ToString("D");
            options.ApiBaseUrl = "https://api.example.test";
            options.WebBaseUrl = "https://web.example.test";
            options.Issuer = "https://api.example.test/services";
        });
        private readonly OptionsMonitor<ServiceLinkOptions> linking = Monitor<ServiceLinkOptions>(options =>
        {
            options.Enabled = true;
            options.ApiBaseUrl = "https://api.example.test";
            options.WebBaseUrl = "https://web.example.test";
        });

        public IdentityClock Clock { get; } = new();
        public ClaimsPrincipal Actor { get; } = new(new ClaimsIdentity(
            [new Claim("netratel_principal_id", ActorId)], "synthetic-human"));
        public OrchestratorDbContext CreateDb() => new(applicationOptions);
        public NetRatelIdentityDbContext CreateOwnerDb() => new(ownerOptions);
        public ServiceLinkIdentityStore CreateStore(OrchestratorDbContext db, NetRatelIdentityDbContext owner) =>
            new(db, identity, linking, new EffectiveAccessService(owner, new ConfigurationBuilder().Build()), Clock);

        public static async Task<IdentityFixture> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var fixture = new IdentityFixture(
                new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options,
                new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connection).Options);
            Assert.True(new ServiceIdentityOptionsValidator().Validate(null, fixture.identity.CurrentValue).Succeeded);
            Assert.True(new ServiceLinkOptionsValidator(Options.Create(fixture.identity.CurrentValue))
                .Validate(null, fixture.linking.CurrentValue).Succeeded);
            await using (var db = fixture.CreateDb()) await db.Database.MigrateAsync();
            await using (var owner = fixture.CreateOwnerDb())
            {
                await owner.Database.MigrateAsync();
                owner.Users.Add(new LocalUser
                {
                    Id = "synthetic-local-owner", UserName = "synthetic-owner", PrincipalId = ActorId,
                    IsEnabled = true, IsInstanceAdministrator = true
                });
                await owner.SaveChangesAsync();
                Assert.True(await new EffectiveAccessService(owner, new ConfigurationBuilder().Build())
                    .AuthorizeAsync(fixture.Actor, NetRatelPermissions.IntegrationManagement, null));
            }
            return fixture;
        }

        public async Task<ServiceLinkIdentityDto> AdoptInAnotherContextAsync(long expectedRevision, CancellationToken ct)
        {
            await using var db = CreateDb();
            await using var owner = CreateOwnerDb();
            return await CreateStore(db, owner).AdoptSourceAsync(
                Actor, ApprovedProducer, expectedRevision, ct);
        }

        public async Task AssertCommittedAsync(ServiceLinkIdentityDto expected, DateTimeOffset adoptedAt, CancellationToken ct)
        {
            await using var verify = CreateDb();
            var row = Assert.Single(await verify.Set<ServiceLinkRuntimeIdentity>().AsNoTracking().ToArrayAsync(ct));
            Assert.Equal(1, row.Id);
            Assert.Equal(Installation, row.InstanceId);
            Assert.Equal(ApprovedProducer, row.SourceInstanceId);
            Assert.Equal(expected.Revision, row.Revision);
            Assert.Equal(ActorId, row.SourceAdoptedBy);
            Assert.Equal(adoptedAt.ToUnixTimeSeconds(), row.SourceAdoptedAtUnixSeconds);
            Assert.Empty(await verify.Set<ServiceLinkAttempt>().AsNoTracking().ToArrayAsync(ct));
        }

        private static OptionsMonitor<T> Monitor<T>(Action<T> configure) where T : class =>
            new(new OptionsFactory<T>([new ConfigureOptions<T>(configure)], [], []), [], new OptionsCache<T>());

        public void Dispose()
        {
            identity.Dispose();
            linking.Dispose();
        }
    }

    private sealed class IdentityClock : TimeProvider
    {
        private long ticks = new DateTimeOffset(2026, 10, 5, 12, 0, 0, TimeSpan.Zero).UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref ticks, elapsed.Ticks);
    }
}
