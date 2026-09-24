using FluentAssertions;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
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

        await service.RevokeAsync("principal-a", created.CredentialId, "principal-a");
        (await service.VerifyCurrentAsync(created.CredentialId, IntegrationCredentialPurpose.HttpMcp)).Should().BeNull();
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
}
