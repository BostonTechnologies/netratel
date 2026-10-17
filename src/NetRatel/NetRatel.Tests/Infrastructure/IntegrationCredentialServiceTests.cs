using AwesomeAssertions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class IntegrationCredentialServiceTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task One_credential_persists_multiple_independent_tenant_grants_and_discovery_stays_scoped()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-owner", UserName = "owner", PrincipalId = "principal-a", IsEnabled = true, IsInstanceAdministrator = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new(
            "Scoped automation", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead), new(7, NetRatelPermissions.FileRead),
                new(7, NetRatelPermissions.ScriptExecute), new(8, NetRatelPermissions.FileWrite)]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "principal-a"),
            new Claim("netratel_integration_credential_id", created.CredentialId)], "integration"));
        var access = new EffectiveAccessService(db, new ConfigurationBuilder().Build());

        (await service.ListAsync("principal-a")).Single().Grants.Should().HaveCount(4);
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 7)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.FileRead, 7)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.ScriptExecute, 7)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.FileWrite, 8)).Should().BeTrue();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.FileWrite, 7)).Should().BeFalse();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 8)).Should().BeFalse();
        (await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.TelemetryRead)).Should().Equal(7);
        (await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.FileWrite)).Should().Equal(8);

        db.Users.Single().IsEnabled = false;
        await db.SaveChangesAsync();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 7)).Should().BeFalse();
        (await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.TelemetryRead)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("what ?")]
    [InlineData("/mcp")]
    [InlineData("http://mcp.example.test/mcp")]
    [InlineData("https://user:pass@mcp.example.test/mcp")]
    [InlineData("https://mcp.example.test/mcp?test=1")]
    [InlineData("https://mcp.example.test/other")]
    public async Task Invalid_http_mcp_resource_cannot_create_a_credential(string resource)
    {
        await using var db = await CreateDbAsync();
        var service = new IntegrationCredentialService(db);
        var create = () => service.CreateAsync("principal-a", new(
            "Invalid URL", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead)], resource));

        await create.Should().ThrowAsync<ArgumentException>();
        (await db.IntegrationCredentials.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Credential_is_one_way_purpose_bound_and_revocable()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-user", PrincipalId = "principal-a", UserName = "owner", IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);

        var created = await service.CreateAsync("principal-a", new(
            "CLI read access",
            IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead)]));

        created.Secret.Should().StartWith(IntegrationCredentialService.ApiTokenPrefix);
        var stored = await db.IntegrationCredentials.SingleAsync();
        stored.SecretHash.Should().Be(IntegrationCredentialService.Hash(created.Secret));
        stored.SecretHash.Should().NotContain(created.Secret);
        (await service.ListAsync("principal-a")).Should().ContainSingle().Which.Grants
            .Should().ContainSingle().Which.Should().Be(new IntegrationCredentialGrantRequest(7, NetRatelPermissions.TelemetryRead));

        var wrongPurpose = await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.HttpMcp);
        wrongPurpose.Should().BeNull();
        var verified = await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api);
        verified.Should().NotBeNull();
        verified!.OwnerPrincipalId.Should().Be("principal-a");
        verified.Grants.Should().ContainSingle().Which.Should().Be(new IntegrationCredentialGrantRequest(7, NetRatelPermissions.TelemetryRead));

        var rotated = await service.CreateAsync("principal-a", new(
            "CLI read access replacement",
            IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead)]));
        (await service.RevokeAsync("principal-a", created.CredentialId, "principal-a")).Should().BeTrue();
        (await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await service.VerifyAsync(rotated.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
    }

    [Fact]
    public async Task Disabled_local_owner_cannot_use_an_existing_credential()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-user", PrincipalId = "principal-a", UserName = "owner", IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new(
            "CLI read access",
            IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead)]));

        db.Users.Single().IsEnabled = false;
        await db.SaveChangesAsync();

        (await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
    }

    [Fact]
    public async Task Expired_or_unpaired_http_credentials_fail_closed_and_lists_are_chronological()
    {
        await using var db = await CreateDbAsync();
        var service = new IntegrationCredentialService(db);

        var first = await service.CreateAsync("principal-a", new(
            "First", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        var second = await service.CreateAsync("principal-a", new(
            "Second", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        db.IntegrationCredentials.Single(credential => credential.Id == first.CredentialId).CreatedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        (await service.ListAsync("principal-a")).Select(credential => credential.Id)
            .Should().Equal(second.CredentialId, first.CredentialId);

        db.IntegrationCredentials.Single(credential => credential.Id == first.CredentialId).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync();
        (await service.VerifyAsync(first.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await service.CheckCurrentAsync(first.CredentialId, IntegrationCredentialPurpose.Api)).FailureCode.Should().Be("local_credential_expired");

        var unpairedHttp = () => service.CreateAsync("principal-a", new(
            "Unpaired", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        await unpairedHttp.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Current_http_mcp_verification_rechecks_resource_owner_state_and_revocation()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-user", PrincipalId = "principal-a", UserName = "owner", IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new(
            "Local HTTP MCP", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7),
            [new(7, NetRatelPermissions.TelemetryRead)], "https://mcp.example.test/mcp"));

        var current = await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp);
        current.Should().NotBeNull();
        current!.Resource.Should().Be("https://mcp.example.test/mcp");

        await db.IntegrationCredentialGrants.Where(grant => grant.CredentialId == created.CredentialId).ExecuteDeleteAsync();
        var underprivileged = await service.CheckCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp);
        underprivileged.FailureCode.Should().BeNull("removing permissions does not revoke the credential");
        underprivileged.Credential.Should().NotBeNull();
        underprivileged.Credential!.Grants.Should().BeEmpty();

        await service.RevokeAsync("principal-a", created.CredentialId, "principal-a");
        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp)).Should().BeNull();
        (await service.CheckCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp)).FailureCode.Should().Be("delegated_identity_revoked");
    }

    [Fact]
    public async Task Instance_discovery_grant_is_explicit_and_is_returned_by_current_verification()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-user", PrincipalId = "principal-a", UserName = "owner", IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);

        var created = await service.CreateAsync("principal-a", new(
            "Local HTTP MCP discovery", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7), [],
            "https://mcp.example.test/mcp", [NetRatelPermissions.McpDiscoveryRead]));

        var current = await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp);
        current.Should().NotBeNull();
        current!.InstancePermissions.Should().ContainSingle().Which.Should().Be(NetRatelPermissions.McpDiscoveryRead);
        (await service.ListAsync("principal-a")).Single().InstancePermissions.Should().ContainSingle().Which.Should().Be(NetRatelPermissions.McpDiscoveryRead);
    }

    [Fact]
    public async Task Instance_control_plane_grant_is_explicit_and_excludes_credential_management()
    {
        await using var db = await CreateDbAsync();
        db.Users.Add(new LocalUser { Id = "local-user", PrincipalId = "principal-a", UserName = "owner", IsEnabled = true });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);

        var created = await service.CreateAsync("principal-a", new(
            "Local HTTP MCP tenant administration", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7), [],
            "https://mcp.example.test/mcp", [NetRatelPermissions.TenantAdministration]));

        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp))!.InstancePermissions
            .Should().ContainSingle().Which.Should().Be(NetRatelPermissions.TenantAdministration);
        var managementGrant = () => service.CreateAsync("principal-a", new(
            "Invalid", IntegrationCredentialPurpose.HttpMcp, DateTimeOffset.UtcNow.AddDays(7), [],
            "https://mcp.example.test/mcp", [NetRatelPermissions.IntegrationManagement]));
        await managementGrant.Should().ThrowAsync<ArgumentException>();
    }

    private async Task<NetRatelIdentityDbContext> CreateDbAsync()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        var db = new NetRatelIdentityDbContext(
            new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options);
        await db.Database.MigrateAsync();
        return db;
    }

    [Fact]
    public async Task Delete_removes_active_verifier_and_grants_durably_but_keeps_principal_and_nonsecret_history()
    {
        await using var db = await CreateDbAsync();
        db.ApplicationPrincipals.Add(new ApplicationPrincipal { Id = "principal-a" });
        await db.SaveChangesAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new("Temporary automation", IntegrationCredentialPurpose.HttpMcp,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)], "https://mcp.example.test/mcp", [NetRatelPermissions.McpDiscoveryRead]));
        var principal = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("netratel_principal_id", "principal-a"), new Claim("netratel_integration_credential_id", created.CredentialId)], "integration"));
        var access = new EffectiveAccessService(db, new ConfigurationBuilder().Build());
        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp)).Should().NotBeNull();

        (await service.DeleteAsync("principal-a", created.CredentialId, "principal-a")).Should().BeTrue();
        (await service.DeleteAsync("principal-a", created.CredentialId, "principal-a")).Should().BeFalse();
        (await service.DeleteAsync("principal-a", "missing", "principal-a")).Should().BeFalse();

        // Reload through another context, including an already exchanged MCP credential identifier.
        await using var restarted = OpenSameDatabase(db);
        var afterRestart = new IntegrationCredentialService(restarted);
        (await afterRestart.ListAsync("principal-a")).Should().BeEmpty();
        (await afterRestart.VerifyAsync(created.Secret, IntegrationCredentialPurpose.HttpMcp)).Should().BeNull();
        (await afterRestart.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp)).Should().BeNull();
        (await access.AuthorizeAsync(principal, NetRatelPermissions.TelemetryRead, 7)).Should().BeFalse();
        (await restarted.IntegrationCredentialGrants.CountAsync()).Should().Be(0);
        (await restarted.IntegrationCredentialInstanceGrants.CountAsync()).Should().Be(0);
        (await restarted.ApplicationPrincipals.CountAsync()).Should().Be(1);
        var audit = await restarted.IntegrationCredentialLifecycleEvents.SingleAsync();
        audit.CredentialId.Should().Be(created.CredentialId);
        audit.OwnerPrincipalId.Should().Be("principal-a");
        audit.ActorPrincipalId.Should().Be("principal-a");
        audit.Action.Should().Be("deleted");
    }

    [Fact]
    public async Task Delete_is_owner_scoped_and_preserves_revoke_history_when_cleaning_inactive_credentials()
    {
        await using var db = await CreateDbAsync();
        var service = new IntegrationCredentialService(db);
        var revoked = await service.CreateAsync("principal-a", new("Revoked", IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        var expired = await service.CreateAsync("principal-a", new("Expired", IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        var other = await service.CreateAsync("principal-b", new("Other account", IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        await db.IntegrationCredentials.Where(candidate => candidate.Id == expired.CredentialId)
            .ExecuteUpdateAsync(update => update.SetProperty(candidate => candidate.ExpiresAtUtc, DateTimeOffset.UtcNow.AddMinutes(-1)));

        (await service.RevokeAsync("principal-a", revoked.CredentialId, "principal-a")).Should().BeTrue();
        (await service.ListAsync("principal-a")).Single(candidate => candidate.Id == revoked.CredentialId).RevokedAtUtc.Should().NotBeNull();
        (await service.DeleteAsync("principal-a", other.CredentialId, "principal-a")).Should().BeFalse();
        (await service.DeleteAsync("principal-a", revoked.CredentialId, "principal-a")).Should().BeTrue();
        (await service.DeleteAsync("principal-a", expired.CredentialId, "principal-a")).Should().BeTrue();

        (await service.ListAsync("principal-a")).Should().BeEmpty();
        (await service.VerifyAsync(other.Secret, IntegrationCredentialPurpose.Api)).Should().NotBeNull();
        (await db.IntegrationCredentialLifecycleEvents.Where(record => record.CredentialId == revoked.CredentialId)
            .OrderBy(record => record.OccurredAtUtc).Select(record => record.Action).ToArrayAsync()).Should().Equal("revoked", "deleted");
        (await db.IntegrationCredentialLifecycleEvents.AnyAsync(record => record.CredentialId == other.CredentialId)).Should().BeFalse();
    }

    [Fact]
    public async Task Concurrent_delete_revoke_and_authentication_do_not_resurrect_a_tracked_credential()
    {
        await using var db = await CreateDbAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new("Concurrent cleanup", IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        await using var deleting = OpenSameDatabase(db);
        await using var revoking = OpenSameDatabase(db);
        await using var authenticating = OpenSameDatabase(db);
        var deletingService = new IntegrationCredentialService(deleting);
        var revokingService = new IntegrationCredentialService(revoking);
        var authenticatingService = new IntegrationCredentialService(authenticating);

        await Task.WhenAll(
            deletingService.DeleteAsync("principal-a", created.CredentialId, "principal-a"),
            revokingService.RevokeAsync("principal-a", created.CredentialId, "principal-a"),
            authenticatingService.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api));

        // The original context deliberately still tracks the pre-delete entity.
        (await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await service.RevokeAsync("principal-a", created.CredentialId, "principal-a")).Should().BeFalse();
        (await db.IntegrationCredentials.CountAsync()).Should().Be(0);
        (await db.IntegrationCredentialGrants.CountAsync()).Should().Be(0);
        (await db.IntegrationCredentialLifecycleEvents.CountAsync(record => record.Action == "deleted")).Should().Be(1);
    }

    [Fact]
    public async Task Reused_context_cannot_authenticate_its_tracked_snapshot_after_external_revocation()
    {
        await using var db = await CreateDbAsync();
        var service = new IntegrationCredentialService(db);
        var created = await service.CreateAsync("principal-a", new("Tracked snapshot", IntegrationCredentialPurpose.Api,
            DateTimeOffset.UtcNow.AddDays(7), [new(7, NetRatelPermissions.TelemetryRead)]));
        await using var other = OpenSameDatabase(db);
        await new IntegrationCredentialService(other).RevokeAsync("principal-a", created.CredentialId, "principal-a");

        (await service.VerifyAsync(created.Secret, IntegrationCredentialPurpose.Api)).Should().BeNull();
        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.Api)).Should().BeNull();
    }

    private static NetRatelIdentityDbContext OpenSameDatabase(NetRatelIdentityDbContext db) => new(
        new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(db.Database.GetConnectionString()).Options);

    [Fact]
    public async Task Upgrade_preserves_existing_revocation_attribution_after_record_deletion()
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        await using var db = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connectionString).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261014172949_AddIntegrationCredentialPairing");
        var revokedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var credential = new IntegrationCredential
        {
            Name = "Old revoked test", OwnerPrincipalId = "principal-a", PublicId = "old-public-id", TokenPrefix = "nrt_ic_old",
            SecretHash = IntegrationCredentialService.Hash("old-secret"), ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1),
            RevokedAtUtc = revokedAt, RevokedByPrincipalId = "original-actor"
        };
        db.IntegrationCredentials.Add(credential);
        await db.SaveChangesAsync();
        await migrator.MigrateAsync();

        await new IntegrationCredentialService(db).DeleteAsync("principal-a", credential.Id, "deleting-actor");
        var history = await db.IntegrationCredentialLifecycleEvents.AsNoTracking().OrderBy(record => record.OccurredAtUtc).ToArrayAsync();
        history.Select(record => record.Action).Should().Equal("revoked", "deleted");
        history[0].ActorPrincipalId.Should().Be("original-actor");
        // PostgreSQL timestamps have microsecond precision.
        history[0].OccurredAtUtc.Should().BeCloseTo(revokedAt, TimeSpan.FromMicroseconds(1));
        history[1].ActorPrincipalId.Should().Be("deleting-actor");
    }
}
