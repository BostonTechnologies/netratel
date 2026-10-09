using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Tests.Infrastructure;
using NetRatel.Tests.API;
using Npgsql;
using Xunit;
namespace NetRatel.Tests.SystemPairing;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PairingUpgradePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fresh_and_supported_upgrade_retire_old_authority_preserve_identities_keys_credentials_and_history(bool upgrade)
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        if (upgrade) await db.GetService<IMigrator>().MigrateAsync("20261007120000_AddNativeRefreshExchange"); else await db.Database.MigrateAsync();
        await using var identity = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connection).Options);
        await identity.Database.MigrateAsync();
        await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity);
        var credentialService = new IntegrationCredentialService(identity);
        var unrelated = await credentialService.CreateAsync(PairingBusinessAuthorityFixture.Administrator, new("Unrelated API credential", IntegrationCredentialPurpose.Api, DateTimeOffset.UtcNow.AddDays(7), [new(23, NetRatelPermissions.TelemetryRead)]));
        var installation = Guid.NewGuid(); var producer = Guid.NewGuid();
        db.Tenants.Add(new() { Id = 23, Name = "Preserved tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        db.Add(new InstallationIdentityRecord { InstanceId = installation, SourceInstanceId = producer });
        db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = producer });
        db.Add(new ServiceSigningKey { Kid = "preserved-signing-key", Issuer = "https://nr.example.test/services", ProtectedPrivateKey = "preserved-protected-key", PublicModulus = "retained-n", PublicExponent = "retained-e", ActiveSlot = 1, CreatedAtUtc = DateTimeOffset.UtcNow });
        db.Requests.Add(new() { SourceSystem = "preserved-business-history", TargetClientIdentity = "retained-agent", JobDefinitionId = "retained-job", Status = "completed", Logs = [], CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        var principalIds = new List<Guid>();
        if (upgrade)
        {
            foreach (var status in new[] { "active", "pending", "failed" })
            {
                var principal = new ServicePrincipalRegistration { ClientId = "old-" + status, NormalizedClientId = "OLD-" + status.ToUpperInvariant(), AliasKey = "OLD_" + status.ToUpperInvariant(), Name = "Old RatelDesk", TenantId = 23, PeerInstanceId = Guid.NewGuid().ToString("D"), PeerTenantId = "legacy-organization", LinkId = "legacy-" + status, Status = status, AllowedScopesJson = "[\"netratel.orchestration.invoke\"]", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow };
                db.Add(principal); principalIds.Add(principal.Id);
                db.Add(new ServicePrincipalSecret { ServicePrincipalId = principal.Id, CredentialRevision = 1, Salt = new string('1', 64), SecretHash = new string('2', 64), Status = status == "pending" ? "pending" : "active", CreatedAtUtc = DateTimeOffset.UtcNow, ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(1) });
            }
            db.RatelDeskConnectors.Add(new() { TenantId = 23, Id = Guid.NewGuid(), OwnerPrincipalId = PairingBusinessAuthorityFixture.Administrator, ConfigurationJson = "\"malformed legacy configuration\"", ProtectedCredential = "old-protected-outbound", AuthenticationJson = "{\"Mode\":2,\"ManagedLinkId\":\"old\"}", ReadinessJson = "{}", Revision = 1, RowVersion = 1 });
        }
        await db.SaveChangesAsync();
        if (upgrade) await SeedLegacyAttemptsAsync(connection);
        await db.Database.MigrateAsync(); db.ChangeTracker.Clear();
        Assert.Equal(installation, (await db.Set<InstallationIdentityRecord>().SingleAsync()).InstanceId);
        Assert.Equal(producer, (await db.FlowRuntimeIdentity.SingleAsync()).SourceInstanceId);
        Assert.Equal("preserved-protected-key", (await db.Set<ServiceSigningKey>().SingleAsync()).ProtectedPrivateKey);
        Assert.NotNull(await credentialService.VerifyAsync(unrelated.Secret, IntegrationCredentialPurpose.Api));
        Assert.Equal("preserved-business-history", (await db.Requests.SingleAsync()).SourceSystem);
        Assert.Empty(await db.Set<SystemPairRecord>().ToArrayAsync());
        Assert.Empty(await db.Set<PairingConnectionRecord>().ToArrayAsync());
        if (upgrade)
        {
            Assert.All(await db.Set<ServicePrincipalRegistration>().ToArrayAsync(), x => Assert.Equal("revoked", x.Status));
            Assert.All(await db.Set<ServicePrincipalSecret>().ToArrayAsync(), x => Assert.Equal("revoked", x.Status));
            var connector = await db.RatelDeskConnectors.SingleAsync(); Assert.Null(connector.ProtectedCredential); Assert.Null(connector.AuthenticationJson); Assert.Null(connector.ReadinessJson);
            using var config = System.Text.Json.JsonDocument.Parse(connector.ConfigurationJson); Assert.False(config.RootElement.GetProperty("Enabled").GetBoolean());
        }
        await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
        await using var tables = new NpgsqlCommand("SELECT count(*) FROM information_schema.tables WHERE table_schema='public' AND table_name IN ('ServiceLinkAttempts','ServiceLinkOperations','ServiceLinkRotations','ServiceLinkVerificationReceipts','M2MConnectivitySettings')", sql);
        Assert.Equal(0L, Convert.ToInt64(await tables.ExecuteScalarAsync()));
    }
    private static async Task SeedLegacyAttemptsAsync(string connection)
    {
        await using var sql = new NpgsqlConnection(connection); await sql.OpenAsync();
        await using var columns = new NpgsqlCommand("SELECT column_name,data_type FROM information_schema.columns WHERE table_schema='public' AND table_name='ServiceLinkAttempts' AND is_nullable='NO' ORDER BY ordinal_position", sql);
        var fields = new List<(string Name, string Type)>(); await using (var reader = await columns.ExecuteReaderAsync()) while (await reader.ReadAsync()) fields.Add((reader.GetString(0), reader.GetString(1)));
        foreach (var status in new[] { "active", "awaiting_approval", "failed", "malformed" })
        {
            var names = string.Join(',', fields.Select(x => '"' + x.Name + '"')); var values = string.Join(',', fields.Select((_, i) => "@v" + i));
            await using var insert = new NpgsqlCommand($"INSERT INTO \"ServiceLinkAttempts\" ({names}) VALUES ({values})", sql);
            for (var i = 0; i < fields.Count; i++)
            {
                var field = fields[i]; object value = field.Type switch { "boolean" => false, "bigint" => 0L, "integer" => 0, _ => "" };
                if (field.Name == "AttemptId") value = "legacy-" + status;
                if (field.Name == "LifecycleState") value = status;
                if (field.Name == "DescriptorJson") value = status == "malformed" ? "{invalid-json" : "{}";
                insert.Parameters.AddWithValue("v" + i, value);
            }
            await insert.ExecuteNonQueryAsync();
        }
    }
}
