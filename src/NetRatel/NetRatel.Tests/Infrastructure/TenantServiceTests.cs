using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Middleware;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using System.Text.Json;
using NetRatel.Application.Tenants;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class TenantServiceTests(PostgreSqlPersistenceFixture postgres) : IClassFixture<PostgreSqlPersistenceFixture>
{
    [Fact]
    public async Task UpdateAsync_Persists_AutoUpdate()
    {
        await using var db = CreateDb();
        var service = new TenantService(db);
        var created = await service.CreateAsync(new CreateTenantCommand(
            "Camelot-Estate",
            "Internet Lab",
            "Estate Hek",
            ["camelot-estate.co.za"],
            "Konrad",
            "security@camelot-estate.co.za",
            AutoUpdate: false));
        created.AutoUpdateChannel.Should().Be("stable");

        var updated = await service.UpdateAsync(new UpdateTenantCommand(
            created.TenantId,
            created.Name,
            created.Description,
            created.Location,
            created.Domains,
            created.ContactPerson,
            created.ContactEmail,
            AutoUpdate: true,
            AutoUpdateChannel: "prerelease",
            AutoUpdateTargetVersion: "0.4.103-rc.1"));

        updated.Should().NotBeNull();
        updated!.AutoUpdate.Should().BeTrue();
        updated.Version.Should().Be(2);
        var reloaded = await service.GetAsync(created.TenantId);
        reloaded!.AutoUpdate.Should().BeTrue();
        reloaded.AutoUpdateChannel.Should().Be("prerelease");
        reloaded.AutoUpdateTargetVersion.Should().Be("0.4.103-rc.1");
        reloaded.Version.Should().Be(2);
    }

    [Fact]
    public async Task Versioned_mutations_reject_stale_tenant_revisions()
    {
        await using var db = CreateDb();
        var service = new TenantService(db);
        var created = await service.CreateAsync(new CreateTenantCommand(
            "Camelot-Estate",
            "Internet Lab",
            "Estate Hek",
            ["camelot-estate.co.za"],
            "Konrad",
            "security@camelot-estate.co.za",
            AutoUpdate: false));

        await FluentActions.Invoking(() => service.UpdateAsync(new UpdateTenantCommand(
            created.TenantId, created.Name, created.Description, created.Location, created.Domains,
            created.ContactPerson, created.ContactEmail, AutoUpdate: true, ExpectedVersion: 99)))
            .Should().ThrowAsync<TenantConcurrencyException>();
        await FluentActions.Invoking(() => service.DeleteAsync(created.TenantId, expectedVersion: 99))
            .Should().ThrowAsync<TenantConcurrencyException>();
    }

    private async Task<OrchestratorDbContext> PostgresAsync()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Delete_retires_prompts_credentials_and_enrollment_atomically_while_preserving_history_and_other_tenants()
    {
        await using var db = await PostgresAsync();
        var service = new TenantService(db);
        var tenant = await service.CreateAsync(new("Disposable", null, null, [], null, null, false));
        var other = await service.CreateAsync(new("Retained", null, null, [], null, null, false));
        var promptId = Guid.NewGuid();
        db.AiPrompts.Add(new() { TenantId = tenant.TenantId, Id = promptId, Name = "Private prompt", Body = "fixture body", CreatedBy = "fixture", UpdatedBy = "fixture" });
        db.AiPromptRevisions.Add(new() { TenantId = tenant.TenantId, PromptId = promptId, Revision = 1, Name = "Private prompt", Body = "fixture body", CreatedBy = "fixture" });
        db.AiPromptDefaults.Add(new() { TenantId = tenant.TenantId, PromptId = promptId });
        var principal = new ServicePrincipalRegistration { TenantId = tenant.TenantId, ClientId = "disposable", NormalizedClientId = "DISPOSABLE", AliasKey = "DISPOSABLE", Status = "active" };
        var retained = new ServicePrincipalRegistration { TenantId = other.TenantId, ClientId = "retained", NormalizedClientId = "RETAINED", AliasKey = "RETAINED", Status = "active" };
        db.AddRange(principal, retained);
        db.Add(new ServicePrincipalSecret { ServicePrincipalId = principal.Id, CredentialRevision = 1, Status = "active" });
        db.EnrollmentCodes.Add(new() { Id = Guid.NewGuid(), TenantId = tenant.TenantId, Code = "fixture-enrollment", ValidToUtc = DateTimeOffset.UtcNow.AddHours(1) });
        await db.SaveChangesAsync();

        Assert.NotNull(await service.DeleteAsync(tenant.TenantId, tenant.Version));
        Assert.Null(await service.GetAsync(tenant.TenantId));
        Assert.False(await service.ExistsAsync(tenant.TenantId));
        Assert.NotNull(await service.GetAsync(other.TenantId));
        Assert.NotNull((await db.Tenants.IgnoreQueryFilters().SingleAsync(x => x.Id == tenant.TenantId)).DeletedAtUtc);
        Assert.True((await db.AiPrompts.SingleAsync()).Archived);
        Assert.Single(await db.AiPromptRevisions.ToListAsync());
        Assert.Empty(await db.AiPromptDefaults.ToListAsync());
        Assert.Equal("revoked", (await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == principal.Id)).Status);
        Assert.Equal("revoked", (await db.Set<ServicePrincipalSecret>().SingleAsync()).Status);
        Assert.Equal("active", (await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == retained.Id)).Status);
        Assert.NotNull((await db.EnrollmentCodes.SingleAsync()).RevokedAtUtc);
        var replacement = await service.CreateAsync(new("Disposable", null, null, [], null, null, false));
        Assert.NotEqual(tenant.TenantId, replacement.TenantId);
    }

    [Fact]
    public async Task Active_client_conflict_rolls_back_then_decommissioned_tenant_can_be_deleted()
    {
        await using var db = await PostgresAsync();
        var service = new TenantService(db);
        var tenant = await service.CreateAsync(new("Disposable", null, null, [], null, null, false));
        var agent = new Agent { Id = Guid.NewGuid(), TenantId = tenant.TenantId };
        db.Agents.Add(agent); await db.SaveChangesAsync();
        var conflict = await Assert.ThrowsAsync<TenantDeletionConflictException>(() => service.DeleteAsync(tenant.TenantId));
        Assert.Equal("tenant_has_clients", conflict.Code);
        Assert.Contains("Decommission", conflict.Message);
        Assert.True(await service.ExistsAsync(tenant.TenantId));
        agent.DeletedAtUtc = agent.RevokedAtUtc = DateTimeOffset.UtcNow; agent.IsEnabled = false; agent.Status = AgentStatus.Disabled;
        await db.SaveChangesAsync();
        await service.DeleteAsync(tenant.TenantId);
        Assert.False(await service.ExistsAsync(tenant.TenantId));
        Assert.Single(await db.Agents.IgnoreQueryFilters().ToListAsync());
    }

    [Fact]
    public async Task Named_connection_requires_disconnect_and_shared_pair_and_other_tenant_connection_survive()
    {
        await using var db = await PostgresAsync();
        var service = new TenantService(db);
        var tenant = await service.CreateAsync(new("Disposable", null, null, [], null, null, false));
        var other = await service.CreateAsync(new("Other", null, null, [], null, null, false));
        var pair = new SystemPairRecord { Id = "shared-pair", PeerInstanceId = "fixture-peer" };
        var target = new PairingConnectionRecord { Id = Guid.NewGuid(), PairId = pair.Id, Active = true,
            MappingJson = JsonSerializer.Serialize(new PairingMapping(Guid.NewGuid(), pair.Id, "Target", tenant.TenantId.ToString(), "organization", null, false, false), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        var unrelated = new PairingConnectionRecord { Id = Guid.NewGuid(), PairId = pair.Id, Active = true,
            MappingJson = JsonSerializer.Serialize(new PairingMapping(Guid.NewGuid(), pair.Id, "Other", other.TenantId.ToString(), "organization", null, false, false), new JsonSerializerOptions(JsonSerializerDefaults.Web)) };
        db.AddRange(pair, target, unrelated); await db.SaveChangesAsync();
        var conflict = await Assert.ThrowsAsync<TenantDeletionConflictException>(() => service.DeleteAsync(tenant.TenantId));
        Assert.Equal("tenant_has_connection", conflict.Code);
        target.Active = false; await db.SaveChangesAsync();
        await service.DeleteAsync(tenant.TenantId);
        Assert.False(await service.ExistsAsync(tenant.TenantId));
        Assert.Single(await db.Set<SystemPairRecord>().ToListAsync());
        Assert.True((await db.Set<PairingConnectionRecord>().SingleAsync(x => x.Id == unrelated.Id)).Active);
        Assert.True(await service.ExistsAsync(other.TenantId));
    }

    [Fact]
    public async Task Real_fk_failure_keeps_original_error_and_independent_notification_without_retrying_tracked_delete()
    {
        await using var db = await PostgresAsync();
        var tenant = new Tenant { Name = "Disposable" };
        db.Tenants.Add(tenant); await db.SaveChangesAsync();
        db.AiPrompts.Add(new() { TenantId = tenant.Id, Id = Guid.NewGuid(), Name = "Blocker", Body = "body", CreatedBy = "fixture", UpdatedBy = "fixture" });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        db.Attach(tenant); // Leave the restricted dependent in PostgreSQL, outside this request's tracker.
        DbUpdateException? original = null;
        var middleware = new ExceptionNotificationMiddleware(async _ =>
        {
            db.Tenants.Remove(tenant);
            try { await db.SaveChangesAsync(); }
            catch (DbUpdateException exception) { original = exception; throw; }
        }, NullLogger<ExceptionNotificationMiddleware>.Instance);
        var http = new DefaultHttpContext();
        http.Request.Method = "DELETE"; http.Request.Path = $"/api/v1/tenants/{tenant.Id}";
        http.Request.RouteValues["id"] = tenant.Id;
        var thrown = await Assert.ThrowsAsync<DbUpdateException>(() => middleware.InvokeAsync(http,
            new OutboxEventRecorder(db, NullLogger<OutboxEventRecorder>.Instance), new FixedCorrelation()));
        Assert.Same(original, thrown);
        Assert.Equal(EntityState.Deleted, db.Entry(tenant).State);
        await using var independent = new OrchestratorDbContext((DbContextOptions<OrchestratorDbContext>)db.GetService<IDbContextOptions>());
        Assert.Single(await independent.Tenants.ToListAsync());
        Assert.Single(await independent.AiPrompts.ToListAsync());
        var notification = Assert.Single(await independent.OutboxMessages.ToListAsync());
        Assert.Equal(NetRatelEventTypes.System.UnhandledException, notification.Type);
        Assert.Equal("tenant-delete-fixture", notification.CorrelationId);
        Assert.Equal(tenant.Id.ToString(), notification.TenantId);
        Assert.Equal(tenant.Id.ToString(), notification.EntityId);
        Assert.DoesNotContain("23503", notification.PayloadJson);
        Assert.DoesNotContain("FK_AiPrompts", notification.PayloadJson);
        var reader = new NetRatelNotificationService(independent,
            new NetRatelNotificationDisplaySanitizer(new EmptyNames()));
        var readable = await reader.GetByIdAsync(notification.Id, "fixture-operator", default);
        Assert.NotNull(readable);
        Assert.Equal(notification.CorrelationId, readable.CorrelationId);
    }

    private sealed class FixedCorrelation : ICorrelationContext { public string? Current => "tenant-delete-fixture"; public string GetOrCreate() => "tenant-delete-fixture"; }
    private sealed class EmptyNames : NetRatel.Application.Notifications.IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities) => new Dictionary<string, string>();
    }

    private static OrchestratorDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;

        return new OrchestratorDbContext(options);
    }
}
