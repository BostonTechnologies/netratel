using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class RatelDeskFlowSourceAdoptionPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly Guid Installation = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Producer = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static ClaimsPrincipal Human => new(new ClaimsIdentity([new("netratel_principal_id", "owner")], "Oidc"));

    [Fact]
    public async Task Installation_A_adopts_the_existing_Flow_B_without_changing_either_identity()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var before = await rig.Identity.GetAsync(default);
        var result = await rig.Setup.AdoptAsync(17, before.Revision, Human, default);
        result.InstallationInstanceId.Should().Be(Installation.ToString("D"));
        result.FlowSourceInstanceId.Should().Be(Producer);
        result.AdoptedSourceInstanceId.Should().Be(Producer.ToString("D"));
        result.IdentityRevision.Should().Be(before.Revision + 1);
        (await rig.Db.FlowRuntimeIdentity.SingleAsync()).SourceInstanceId.Should().Be(Producer);
        (await rig.Db.Set<ServiceLinkRuntimeIdentity>().SingleAsync()).SourceAdoptedBy.Should().Be("owner");
        (await rig.Db.Set<ServiceLinkAttempt>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Guided_preparation_uses_existing_Flow_producer_once_and_preserves_revision_on_repeat()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.Setup.PrepareProducerAsync(Human, default);
        var approved = await rig.Identity.GetAsync(default);
        approved.SourceInstanceId.Should().Be(Producer.ToString("D"));
        await rig.Setup.PrepareProducerAsync(Human, default);
        (await rig.Identity.GetAsync(default)).Should().Be(approved);
        (await rig.Db.Set<ServiceLinkAttempt>().CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Guided_first_preparation_rejects_unauthorized_actor_before_creating_a_new_producer()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        rig.Db.FlowRuntimeIdentity.Remove(await rig.Db.FlowRuntimeIdentity.SingleAsync());
        await rig.Db.SaveChangesAsync();
        rig.Access.AllowInstance = false;
        Func<Task> prepare = () => rig.Setup.PrepareProducerAsync(Human, default);
        await prepare.Should().ThrowExactlyAsync<ServiceLinkProtocolException>();
        (await rig.Db.FlowRuntimeIdentity.CountAsync()).Should().Be(0);
        (await rig.Identity.GetAsync(default)).SourceInstanceId.Should().BeNull();
    }

    [Theory]
    [InlineData("AccountApi")]
    [InlineData("service")]
    [InlineData("machine_token")]
    public async Task Account_API_and_service_actors_cannot_adopt_even_with_a_matching_owner_claim(string mode)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var claims = new List<Claim> { new("netratel_principal_id", "owner") };
        if (mode == "AccountApi") claims.Add(new("netratel_integration_credential_id", "synthetic-account-credential"));
        else { claims.Add(new("auth_mode", mode)); if (mode == "service") claims.Add(new(ServiceIdentityClaims.PrincipalId, "synthetic-service")); }
        var actor = new ClaimsPrincipal(new ClaimsIdentity(claims, mode));
        var before = await rig.Identity.GetAsync(default);
        Func<Task> adopt = () => rig.Setup.AdoptAsync(17, before.Revision, actor, default);
        await adopt.Should().ThrowExactlyAsync<UnauthorizedAccessException>();
        var after = await rig.Identity.GetAsync(default);
        after.Should().Be(before);
        (await rig.Db.FlowRuntimeIdentity.SingleAsync()).SourceInstanceId.Should().Be(Producer);
    }

    [Fact]
    public async Task Tenant_foreign_manager_is_denied_before_instance_identity_mutation()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var before = await rig.Identity.GetAsync(default);
        Func<Task> adopt = () => rig.Setup.AdoptAsync(23, before.Revision, Human, default);
        await adopt.Should().ThrowExactlyAsync<UnauthorizedAccessException>();
        (await rig.Identity.GetAsync(default)).Should().Be(before);
    }

    [Fact]
    public async Task Tenant_manager_without_instance_permission_cannot_adopt()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        rig.Access.AllowInstance = false;
        var before = await rig.Identity.GetAsync(default);
        Func<Task> adopt = () => rig.Setup.AdoptAsync(17, before.Revision, Human, default);
        var failure = await adopt.Should().ThrowExactlyAsync<ServiceLinkProtocolException>();
        failure.Which.StatusCode.Should().Be(403);
        (await rig.Identity.GetAsync(default)).Should().Be(before);
    }

    [Fact]
    public async Task Existing_adopted_C_conflicts_with_Flow_B_and_preserves_durable_identities()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var existing = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
        var row = await rig.Db.Set<ServiceLinkRuntimeIdentity>().SingleAsync();
        row.SourceInstanceId = existing; row.Revision = 9; row.SourceAdoptedBy = "previous-owner";
        await rig.Db.SaveChangesAsync();
        var protectedWork = await SeedExistingWorkAsync(rig.Db);
        var before = await rig.Identity.GetAsync(default);
        Func<Task> adopt = () => rig.Setup.AdoptAsync(17, before.Revision, Human, default);
        var failure = await adopt.Should().ThrowExactlyAsync<ServiceLinkProtocolException>();
        failure.Which.StatusCode.Should().Be(409); failure.Which.Code.Should().Be("source-identity-conflict");
        rig.Db.ChangeTracker.Clear();
        (await rig.Identity.GetAsync(default)).Should().Be(before);
        (await rig.Db.FlowRuntimeIdentity.SingleAsync()).SourceInstanceId.Should().Be(Producer);
        await AssertProtectedWorkAsync(rig.Db, protectedWork);
    }

    [Fact]
    public async Task Existing_attempt_blocks_initial_adoption_without_rewriting_pending_action_identity()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var protectedWork = await SeedExistingWorkAsync(rig.Db);
        var before = await rig.Identity.GetAsync(default);
        Func<Task> adopt = () => rig.Setup.AdoptAsync(17, before.Revision, Human, default);
        var failure = await adopt.Should().ThrowExactlyAsync<ServiceLinkProtocolException>();
        failure.Which.Code.Should().Be("source-identity-conflict");
        rig.Db.ChangeTracker.Clear();
        (await rig.Identity.GetAsync(default)).Should().Be(before);
        await AssertProtectedWorkAsync(rig.Db, protectedWork);
    }

    private static async Task<(string Attempt, Guid Run, Guid Node, string Key)> SeedExistingWorkAsync(OrchestratorDbContext db)
    {
        var attempt = Guid.NewGuid().ToString("D"); var flow = Guid.NewGuid(); var version = Guid.NewGuid();
        var run = Guid.NewGuid(); var node = Guid.NewGuid(); var occurrence = Guid.NewGuid(); var eventId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = $"{Producer:N}:{occurrence:N}:{eventId:N}:{version:N}:{node:N}";
        db.Set<ServiceLinkAttempt>().Add(new() { AttemptId = attempt, Role = "initiator", LocalTenantId = "17",
            LocalActorId = "owner", PeerInstanceId = Installation.ToString("D"), DescriptorJson = "{}",
            DescriptorHash = new string('d', 64), Revision = 7, CreatedAtUnixSeconds = now.ToUnixTimeSeconds() });
        db.FlowDefinitions.Add(new() { Id = flow, TenantId = 17, Name = "Existing flow", Revision = 1,
            DraftJson = "{}", CreatedAtUtc = now, UpdatedAtUtc = now });
        db.FlowVersions.Add(new() { Id = version, TenantId = 17, FlowId = flow, VersionNumber = 1,
            GraphJson = "{}", ConfigurationHash = new string('a', 64), PublishedBy = "owner", PublishedAtUtc = now });
        db.FlowRuns.Add(new() { Id = run, TenantId = 17, FlowId = flow, FlowVersionId = version,
            EventId = eventId, OccurrenceId = occurrence, EventJson = "{}", EventFingerprint = new string('b', 64),
            Status = FlowRunStatus.DeliveryUnknown, CreatedAtUtc = now });
        db.FlowActions.Add(new() { RunId = run, NodeId = node, TenantId = 17, IdempotencyKey = key,
            DraftJson = "{}", PreparedJson = "{}", SemanticFingerprint = new string('c', 64),
            Status = FlowActionStatus.DeliveryUnknown, Attempts = 1, LeaseFence = 1 });
        await db.SaveChangesAsync(); return (attempt, run, node, key);
    }
    private static async Task AssertProtectedWorkAsync(OrchestratorDbContext db, (string Attempt, Guid Run, Guid Node, string Key) before)
    {
        var attempt = await db.Set<ServiceLinkAttempt>().SingleAsync(x => x.AttemptId == before.Attempt);
        attempt.Revision.Should().Be(7); attempt.DescriptorHash.Should().Be(new string('d', 64));
        var action = await db.FlowActions.SingleAsync(x => x.RunId == before.Run && x.NodeId == before.Node);
        action.IdempotencyKey.Should().Be(before.Key); action.PreparedJson.Should().Be("{}");
        action.SemanticFingerprint.Should().Be(new string('c', 64)); action.Attempts.Should().Be(1);
        action.Status.Should().Be(FlowActionStatus.DeliveryUnknown);
    }

    private sealed class Rig(ServiceProvider provider, IServiceScope scope, OrchestratorDbContext db,
        ServiceLinkIdentityStore identity, RatelDeskConnectorSetupService setup, Access access) : IAsyncDisposable
    {
        public OrchestratorDbContext Db => db;
        public ServiceLinkIdentityStore Identity => identity;
        public RatelDeskConnectorSetupService Setup => setup;
        public Access Access => access;
        public static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var connection = await postgres.CreateDatabaseAsync();
            var services = new ServiceCollection();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection));
            services.AddSingleton(TimeProvider.System);
            var access = new Access(); services.AddSingleton<IEffectiveAccessService>(access);
            services.AddSingleton<IOptionsMonitor<ServiceIdentityOptions>>(new Options<ServiceIdentityOptions>(new()));
            services.AddSingleton<IOptionsMonitor<ServiceLinkOptions>>(new Options<ServiceLinkOptions>(new()));
            services.AddScoped<ServiceLinkIdentityStore>(); services.AddSingleton<FlowPersistenceService>();
            var provider = services.BuildServiceProvider(); var scope = provider.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            // Must use the actual appended migration after ordered integration; never bypass pending models with EnsureCreated.
            await db.Database.MigrateAsync();
            db.Set<ServiceLinkRuntimeIdentity>().Add(new() { InstanceId = Installation });
            db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = Producer }); await db.SaveChangesAsync();
            var identity = scope.ServiceProvider.GetRequiredService<ServiceLinkIdentityStore>();
            var flow = (IFlowSourceIdentityResolver)provider.GetRequiredService<FlowPersistenceService>();
            var setup = new RatelDeskConnectorSetupService(new TenantAuthority(), flow, identity, null!, db);
            // No outbound links are seeded; the unused profile dependency cannot fabricate a managed grant.
            return new(provider, scope, db, identity, setup, access);
        }
        public async ValueTask DisposeAsync() { scope.Dispose(); await provider.DisposeAsync(); }
    }
    private sealed class TenantAuthority : IRatelDeskConnectorAuthorization
    {
        public Task<bool> CanManageAsync(ClaimsPrincipal actor, int tenant, CancellationToken ct) => Task.FromResult(tenant == 17 && actor.Identity?.IsAuthenticated == true);
        public Task<bool> CanExecuteAsync(string principal, string? credential, int tenant, CancellationToken ct) => Task.FromResult(false);
    }
    private sealed class Access : IEffectiveAccessService
    {
        public bool AllowInstance = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal actor, string permission, int? tenant, CancellationToken ct = default) =>
            Task.FromResult(AllowInstance && tenant is null && permission == NetRatelPermissions.IntegrationManagement);
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
