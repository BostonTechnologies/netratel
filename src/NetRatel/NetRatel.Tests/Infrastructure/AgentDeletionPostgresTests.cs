using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Security;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class AgentDeletionPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Removal_shares_the_caller_transaction_and_fences_admission_and_heartbeat()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = Provider(connection);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.MigrateAsync();
        var agent = await SeedAgentAsync(db);
        var client = new ClientKey(agent.TenantId, agent.Id);
        var store = provider.GetRequiredService<IClientConnectionEpochStore>();
        var active = await ActivateAsync(store, client);
        var pending = (await store.ReserveAsync(Request(client), default)).Reservation!;
        var service = new AgentManagementService(db, NullLogger<AgentManagementService>.Instance);

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await service.DeleteAsync(client.TenantId, client.AgentId, "Replaced server", "admin", default);
            db.Database.CurrentTransaction.Should().BeSameAs(transaction);
            (await store.GetCurrentAsync(client, default))!.Active.Should().BeTrue("removal is not visible before the caller commits");
            await using var competing = new NpgsqlConnection(connection);
            await competing.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT "LastIssuedEpoch" FROM "ClientConnectionEpochs"
                  WHERE "TenantId"=@tenant AND "AgentId"=@agent FOR UPDATE NOWAIT
                """, competing);
            command.Parameters.AddWithValue("tenant", client.TenantId);
            command.Parameters.AddWithValue("agent", client.AgentId);
            Func<Task> takeAdmissionGate = async () => { await command.ExecuteScalarAsync(); };
            (await takeAdmissionGate.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.LockNotAvailable);
            await transaction.RollbackAsync();
        }
        db.ChangeTracker.Clear();
        (await db.Agents.SingleAsync(row => row.Id == client.AgentId)).DeletedAtUtc.Should().BeNull();
        (await db.AgentCredentials.SingleAsync(row => row.AgentId == client.AgentId)).RevokedAtUtc.Should().BeNull();
        (await store.GetCurrentAsync(client, default))!.Active.Should().BeTrue();

        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            (await AgentLifecycleLock.LockAsync(db, client.TenantId, client.AgentId, default)).Should().NotBeNull();
            await service.DeleteAsync(client.TenantId, client.AgentId, "Replaced server", "admin", default);
            await transaction.CommitAsync();
        }

        (await store.GetCurrentAsync(client, default))!.Active.Should().BeFalse();
        (await store.ReserveAsync(Request(client), default)).Disposition.Should().Be(OwnershipDisposition.NoActiveSession);
        (await store.CommitAsync(pending, new(pending.Owner, 1, DateTimeOffset.UtcNow), default)).Disposition.Should().Be(OwnershipDisposition.NoActiveSession);
        (await store.RecordHeartbeatAsync(new(active.Owner, 2, DateTimeOffset.UtcNow), default)).Disposition.Should().Be(OwnershipDisposition.NoActiveSession);
        (await store.RenewAsync(new(active.Owner, 3, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(20)), default))
            .Disposition.Should().Be(OwnershipDisposition.NoActiveSession);
        (await db.AgentCredentials.SingleAsync(row => row.AgentId == client.AgentId)).RevokedAtUtc.Should().NotBeNull();
        await using var readTransaction = await db.Database.BeginTransactionAsync();
        (await ClientConnectionEpochStore.LockEffectiveOwnerSnapshotAsync(db, active.Owner, TimeProvider.System, default)).Should().BeNull();
    }

    [Fact]
    public async Task Persisted_tombstone_blocks_recovery_even_when_the_context_previously_tracked_an_active_agent()
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var provider = Provider(connection);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await db.Database.MigrateAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var installation = AgentInstallationIdentity.Parse(Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), "ecdsa-p256");
        var agent = await SeedAgentAsync(db, installation);
        var code = new EnrollmentCode
        {
            Id = Guid.NewGuid(), TenantId = agent.TenantId, Code = "ENR-DELETION-TEST",
            ValidFromUtc = DateTimeOffset.UtcNow.AddMinutes(-1), ValidToUtc = DateTimeOffset.UtcNow.AddMinutes(10)
        };
        db.EnrollmentCodes.Add(code);
        await db.SaveChangesAsync();
        var client = new ClientKey(agent.TenantId, agent.Id);
        var store = provider.GetRequiredService<IClientConnectionEpochStore>();
        var active = await ActivateAsync(store, client);

        await using (var writer = provider.CreateAsyncScope())
        {
            var writerDb = writer.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var removed = await writerDb.Agents.SingleAsync(row => row.Id == agent.Id);
            // An old persisted tombstone can predate owner retirement. Reads
            // and writes must still deny it while recovery sees fresh state.
            removed.DeletedAtUtc = DateTimeOffset.UtcNow;
            removed.DeletedBy = "admin";
            await writerDb.SaveChangesAsync();
        }
        agent.DeletedAtUtc.Should().BeNull("this context intentionally retains the pre-removal entity");
        (await store.GetCurrentAsync(client, default))!.Active.Should().BeFalse();
        (await store.RecordHeartbeatAsync(new(active.Owner, 2, DateTimeOffset.UtcNow), default)).Disposition.Should().Be(OwnershipDisposition.NoActiveSession);

        var enrollment = new EnrollmentService(db, Options.Create(new AgentAuthOptions()),
            Options.Create(new SecurityHardeningOptions()), new PrimaryClientAgentBindingService(db),
            NullLogger<EnrollmentService>.Instance);
        var request = new AgentEnrollRequest(code.Code, installation.PublicKey, installation.Algorithm,
            """{"hostName":"removed-host"}""", ["netratel:connect"]);
        var timestamp = DateTimeOffset.UtcNow;
        var nonce = Guid.NewGuid().ToString("N");
        var hash = PopSignatureService.ComputeEnrollmentBodyHash(request.EnrollmentCode, request.PublicKey!,
            request.KeyAlgorithm!, request.DeviceInfoJson, request.RequestedScopes);
        request = request with
        {
            ProofTimestampUtc = timestamp, ProofNonce = nonce,
            ProofSignature = Convert.ToBase64String(key.SignData(
                PopSignatureService.BuildSigningMessage("POST", "/api/v1/agents/enroll", timestamp, nonce, hash), HashAlgorithmName.SHA256))
        };
        Func<Task> recover = () => enrollment.EnrollAsync(request, default);
        var failure = await recover.Should().ThrowAsync<AgentAuthException>();
        failure.Which.Code.Should().Be("agent_deleted");
        (await db.AgentCredentials.CountAsync(row => row.AgentId == agent.Id)).Should().Be(1);
        (await db.EnrollmentCodes.AsNoTracking().SingleAsync(row => row.Id == code.Id)).Uses.Should().Be(0);
        (await db.Agents.IgnoreQueryFilters().CountAsync(row => row.PublicKeyFingerprint == installation.Fingerprint)).Should().Be(1);
    }

    private static ServiceProvider Provider(string connection) => new ServiceCollection()
        .AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection))
        .AddSingleton(TimeProvider.System)
        .AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)))
        .AddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>()
        .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static async Task<Agent> SeedAgentAsync(OrchestratorDbContext db, AgentInstallationKey? installation = null)
    {
        var tenant = new Tenant { Name = $"Removal-{Guid.NewGuid():N}" };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        var agent = new Agent
        {
            Id = Guid.NewGuid(), TenantId = tenant.Id, IsEnabled = true, Status = AgentStatus.Active,
            PublicKey = installation?.PublicKey, PublicKeyFingerprint = installation?.Fingerprint,
            CreatedAtUtc = DateTimeOffset.UtcNow
        };
        db.Agents.Add(agent);
        db.AgentCredentials.Add(new AgentCredential
        {
            Id = Guid.NewGuid(), AgentId = agent.Id, RefreshTokenHash = "existing-credential", CreatedAtUtc = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return agent;
    }

    private static AdmissionRequest Request(ClientKey client)
    {
        var now = DateTimeOffset.UtcNow;
        return new(client, Guid.NewGuid(), Guid.NewGuid(), 0, now, now.AddSeconds(30), now.AddMinutes(10), new("removal-test", ["presence"], null));
    }

    private static async Task<OwnerSnapshot> ActivateAsync(IClientConnectionEpochStore store, ClientKey client)
    {
        var reservation = (await store.ReserveAsync(Request(client), default)).Reservation!;
        var result = await store.CommitAsync(reservation, new(reservation.Owner, 1, DateTimeOffset.UtcNow), default);
        result.Disposition.Should().Be(OwnershipDisposition.Accepted);
        return result.Current!;
    }
}
