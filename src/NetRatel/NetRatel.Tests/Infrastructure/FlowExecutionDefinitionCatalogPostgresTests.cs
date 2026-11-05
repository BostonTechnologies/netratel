using System.Security.Claims;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionDefinitionCatalogPostgresTests(PostgreSqlPersistenceFixture postgres) : IAsyncLifetime
{
    private DbContextOptions<OrchestratorDbContext> _options = null!;
    public async ValueTask InitializeAsync()
    {
        _options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(await postgres.CreateDatabaseAsync()).Options;
        await using var db = new OrchestratorDbContext(_options);
        await db.Database.MigrateAsync();
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task Search_is_tenant_scoped_paged_and_named_and_selection_returns_actual_schema_and_bound_target()
    {
        await using var db = new OrchestratorDbContext(_options);
        var target = Guid.NewGuid();
        db.Agents.Add(new() { Id = target, TenantId = 7, Name = "synthetic-host", FriendlyName = "Synthetic worker", CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var scripts = new ScriptService(db);
        const string source = "printf synthetic-secret-source";
        var script = await scripts.CreateAsync(new("Synthetic repair", "/", null, source, "bash", null));
        var jobs = new JobDefinitionService(db);
        var first = await jobs.CreateAsync(new("Synthetic repair first", "/", null, 7, "", AgentId: target));
        var next = await jobs.CreateAsync(new("Synthetic repair second", "/", null, 7, "", AgentId: target));
        await jobs.CreateAsync(new("Synthetic repair another tenant", "/", null, 8, "", AgentId: target));
        await jobs.AddStepAsync(new(first.Id, 1, JobStepKind.LibraryScript, null, null, script.Id, null, true));
        var service = Catalog(db, new Access(true));
        var page = await service.ListAsync(7, new("operator"), FlowExecutionSourceKind.Job, "REPAIR", limit: 1);
        page.Items.Should().ContainSingle();
        page.Items[0].Definition.Id.Should().Be(first.Id.ToString());
        page.Items[0].Definition.Name.Should().Be("Synthetic repair first");
        page.Items[0].Target!.AgentId.Should().Be(target);
        page.Items[0].Target!.DisplayName.Should().Be("Synthetic worker");
        page.NextAfterId.Should().Be(first.Id.ToString());
        var second = await service.ListAsync(7, new("operator"), FlowExecutionSourceKind.Job, "repair", page.NextAfterId, 1);
        second.Items.Should().ContainSingle().Which.Definition.Id.Should().Be(next.Id.ToString());
        second.NextAfterId.Should().BeNull();
        var detail = await service.GetAsync(7, new("operator"), FlowExecutionSourceKind.Job, first.Id.ToString());
        detail!.Target!.AgentId.Should().Be(target);
        detail.Definition.Revision.Should().HaveLength(64);
        detail.Definition.ContentHash.Should().Be(detail.Definition.Revision);
        detail.PinnedSnapshotReference.Should().BeNull();
        System.Text.Json.JsonSerializer.Serialize(detail).Should().NotContain(source);
        (await service.GetAsync(7, new("operator"), FlowExecutionSourceKind.Script, script.Id.ToString()))!.Definition.Name.Should().Be(script.Name);
    }

    [Fact]
    public async Task Current_permissions_and_supported_source_are_required_before_discovery()
    {
        await using var db = new OrchestratorDbContext(_options);
        var denied = () => Catalog(db, new Access(false)).ListAsync(7, new("operator"), FlowExecutionSourceKind.Script);
        await denied.Should().ThrowAsync<ExecutionAdmissionDeniedException>();
        var unsupported = () => Catalog(db, new Access(true)).ListAsync(7, new("operator"), FlowExecutionSourceKind.Request);
        await unsupported.Should().ThrowAsync<NotSupportedException>();
        var invalid = () => Catalog(db, new Access(true)).ListAsync(7, new("operator"), FlowExecutionSourceKind.Job, afterId: "9e15");
        await invalid.Should().ThrowAsync<ArgumentException>();
    }

    private static FlowExecutionDefinitionCatalog Catalog(OrchestratorDbContext db, Access access)
    {
        var scripts = new ScriptService(db);
        var pins = new FlowExecutionDefinitionPins(db, new PinnedJobExecutionSnapshotCapture(db, new JobDefinitionService(db), scripts),
            scripts, new Authority(), access, new EphemeralDataProtectionProvider());
        return new(db, new Authority(), access, pins);
    }
    private sealed class Authority : IFlowExecutionAuthorityVerifier
    {
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default)
            => Task.FromResult(tenantId == 7 && authority.PrincipalId == "operator");
    }
    private sealed class Access(bool allowed) : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken ct = default)
            => Task.FromResult(allowed && tenantId == 7);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
