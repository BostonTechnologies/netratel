using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.SystemPairing;
using NetRatel.Tests.API;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.SystemPairing;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class PairingReceiverIdentityPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Signed_distinct_receiver_identity_drives_capabilities_and_committed_receipts_without_rewriting_history()
    {
        var connection = await postgres.CreateDatabaseAsync();
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);
        await db.Database.MigrateAsync();
        await using var identity = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connection).Options);
        await identity.Database.MigrateAsync();
        await PairingBusinessAuthorityFixture.SeedAdministratorAsync(identity, "human-owner");
        var source = RatelDeskReceiverFixture.Id(1); var installation = RatelDeskReceiverFixture.Id(99);
        db.Tenants.Add(new() { Id = 71, Name = "Preserved tenant", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
        db.Add(new InstallationIdentityRecord { InstanceId = installation, SourceInstanceId = source });
        db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = source });
        var receiver = RatelDeskReceiverFixture.Id(8).ToString("D");
        using var key = RSA.Create(2048);
        var metadata = new PairingMetadata(PairingProtocol.Contract, "rateldesk", RatelDeskReceiverFixture.Id(9).ToString("D"),
            "Upgraded helpdesk", "https://desk.example.test", "https://api.example.test", null,
            Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), receiver);
        var nonce = Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var proof = new PairingMetadataProof(metadata, nonce, Convert.ToBase64String(key.SignData(
            Encoding.UTF8.GetBytes(JsonSerializer.Serialize(metadata, PairingTransport.Json) + ":" + nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        PairingTransport.VerifyProof(proof, nonce, metadata.SigningPublicKey);
        PairingTransport.ValidateMetadata(proof.Metadata, "rateldesk");
        Assert.Throws<PairingException>(() => PairingTransport.VerifyProof(proof with
            { Metadata = metadata with { ReceiverInstanceId = metadata.InstallationId } }, nonce, metadata.SigningPublicKey));
        var mappingId = RatelDeskReceiverFixture.Id(6); var pairId = PairingService.Hash("distinct preserved helpdesk identities");
        var mapping = new PairingMapping(mappingId, pairId, "Preserved incident target", "71", "org-1", "customer-1", true, false);
        var protection = new EphemeralDataProtectionProvider();
        var credential = new PairingBusinessCredential("receiver-scoped-client", new string('s', 43), metadata.ApiOrigin + "/connect/token",
            "rateldesk-api", "https://issuer.example.test/services", PairingService.ExpectedRatelDeskScopes(mapping), source.ToString("D"), mappingId.ToString("D"));
        db.Add(new SystemPairRecord { Id = pairId, PeerInstanceId = metadata.InstallationId, AdministratorId = "human-owner",
            PeerMetadataJson = JsonSerializer.Serialize(proof.Metadata, PairingTransport.Json), CreatedAtUtc = DateTimeOffset.UtcNow,
            ProtectedOutboundSecret = protection.CreateProtector("NetRatel.Pairing.v1", pairId + "/outbound").Protect(new string('p', 43)) });
        db.Add(new PairingConnectionRecord { Id = mappingId, PairId = pairId, MappingJson = JsonSerializer.Serialize(mapping, PairingTransport.Json),
            AdministratorId = "human-owner", Active = true, CreatedAtUtc = DateTimeOffset.UtcNow,
            ProtectedOutboundCredential = protection.CreateProtector("NetRatel.Pairing.v1", mappingId + "/outbound-business").Protect(JsonSerializer.Serialize(credential, PairingTransport.Json)) });
        await db.SaveChangesAsync();
        var store = new RatelDeskConnectorStore(db);
        var connector = RatelDeskReceiverFixture.Connector() with
        { Configuration = RatelDeskReceiverFixture.Connector().Configuration with { Origin = metadata.ApiOrigin }, Authentication = new(RatelDeskAuthenticationMode.PairedSystem, mappingId.ToString("D")) };
        Assert.True(await store.SaveAsync(connector, 0, default));
        await using var services = new ServiceCollection().AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(connection)).BuildServiceProvider();
        var flow = new FlowPersistenceService(services.GetRequiredService<IServiceScopeFactory>(), TimeProvider.System);
        var authority = new PairingAuthority(new PairingBusinessAuthorityFixture.Access(), identity, db);
        using var http = new HttpClient();
        var profiles = new PairingBusinessProfileService(db, protection, authority,
            new InstallationIdentityStore(db, flow, new Monitor()), new PairingTransport(http));
        var bindings = new PairingRatelDeskBindingResolver(store,
            new RatelDeskConnectorAuthorization(new PairingBusinessAuthorityFixture.Access(), identity), profiles, flow);
        var captured = await bindings.CaptureAsync(connector, connector.Authentication!, source, default);
        Assert.Equal(receiver, captured.ReceiverInstanceId); Assert.NotEqual(metadata.InstallationId, captured.ReceiverInstanceId);
        var capabilityBody = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(RatelDeskReceiverFixture.Capability(captured))
            .Replace(RatelDeskReceiverFixture.Api, metadata.ApiOrigin, StringComparison.Ordinal));
        var capability = ReceiverWireValidation.Capability(capabilityBody, captured, RatelDeskReceiverFixture.Now);
        var wrongCapability = JsonNode.Parse(capabilityBody)!; wrongCapability["receiverInstanceId"] = metadata.InstallationId;
        Assert.Equal("receiver-identity-mismatch", Assert.Throws<RatelDeskReceiverReadException>(() =>
            ReceiverWireValidation.Capability(RatelDeskReceiverFixture.Bytes(wrongCapability), captured, RatelDeskReceiverFixture.Now)).Code);
        var action = RatelDeskReceiverFixture.Action(); var historical = JsonSerializer.SerializeToUtf8Bytes(action);
        var prepared = new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint()).Build(RatelDeskReceiverFixture.Draft(), action,
            captured, capability, RatelDeskReceiverFixture.Now, null);
        var receiptBody = JsonNode.Parse(RatelDeskReceiverFixture.Receipt(prepared))!;
        const string location = "/api/v1/incidents/incident-1"; receiptBody["integrationReceipt"]!["location"] = location;
        var receipt = ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Bytes(receiptBody), location, prepared);
        Assert.Equal(receiver, receipt.ReceiverInstanceId);
        receiptBody["integrationReceipt"]!["receiverInstanceId"] = metadata.InstallationId;
        Assert.Throws<InvalidDataException>(() => ReceiverWireValidation.Receipt(RatelDeskReceiverFixture.Bytes(receiptBody), location, prepared));
        Assert.Equal(historical, JsonSerializer.SerializeToUtf8Bytes(action));
        Assert.Equal(installation, (await db.Set<InstallationIdentityRecord>().AsNoTracking().SingleAsync()).InstanceId);
        Assert.Equal(source, (await db.FlowRuntimeIdentity.AsNoTracking().SingleAsync()).SourceInstanceId);
        Assert.Equal(metadata.InstallationId, JsonSerializer.Deserialize<PairingMetadata>((await db.Set<SystemPairRecord>().AsNoTracking().SingleAsync()).PeerMetadataJson, PairingTransport.Json)!.InstallationId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    [InlineData("AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA")]
    public void RatelDesk_metadata_requires_a_canonical_preserved_receiver_identity(string? receiver)
    {
        var metadata = new PairingMetadata(PairingProtocol.Contract, "rateldesk", RatelDeskReceiverFixture.Id(9).ToString("D"),
            "Helpdesk", "https://desk.example.test", "https://api.example.test", null, "present-public-key", receiver);
        Assert.Equal("receiver-identity-unavailable", Assert.Throws<PairingException>(() => PairingTransport.ValidateMetadata(metadata, "rateldesk")).Code);
    }

    private sealed class Monitor : IOptionsMonitor<ServiceIdentityOptions>
    {
        public ServiceIdentityOptions CurrentValue => new();
        public ServiceIdentityOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<ServiceIdentityOptions, string?> listener) => null;
    }
}
