using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class IntegrationCredentialServiceTests
{
    [Fact]
    public async Task Credential_is_one_way_purpose_bound_and_revocable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
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
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
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
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
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
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        await using var db = CreateDb(connection);
        await db.Database.EnsureCreatedAsync();
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

    private static NetRatelIdentityDbContext CreateDb(SqliteConnection connection) => new(
        new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseSqlite(connection).Options);
}
