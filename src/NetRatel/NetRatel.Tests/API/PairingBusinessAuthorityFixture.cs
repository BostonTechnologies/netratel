using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;

namespace NetRatel.Tests.API;

/// <summary>Explicit owning-layer setup; production token issuance and live connection checks remain real.</summary>
internal static class PairingBusinessAuthorityFixture
{
    internal const string Administrator = "synthetic-pairing-administrator";
    internal const string Organization = "00000000-0000-4000-8000-000000000073";

    internal static async Task<CreatedServiceClient> CreateAsync(OrchestratorDbContext db,
        IServicePrincipalRegistry registry, int tenant, string[] scopes,
        string administrator = Administrator, string? clientId = null, bool active = true,
        CancellationToken ct = default, IDataProtectionProvider? protection = null)
    {
        var peer = "00000000-0000-4000-8000-000000000074";
        var pairId = PairingService.Hash(Guid.NewGuid().ToString("N"));
        var id = Guid.NewGuid();
        var mapping = new PairingMapping(id, pairId, "Scoped test connection", tenant.ToString(), Organization, null, false, true);
        var json = JsonSerializer.Serialize(mapping, PairingTransport.Json);
        db.Add(new SystemPairRecord { Id = pairId, PeerInstanceId = peer, AdministratorId = administrator,
            ProtectedOutboundSecret = "owning-layer-fixture-ciphertext",
            PeerMetadataJson = JsonSerializer.Serialize(new PairingMetadata(PairingProtocol.Contract, "rateldesk", peer, "Scoped test peer", "https://peer.example.test", "https://peer.example.test", null), PairingTransport.Json), CreatedAtUtc = DateTimeOffset.UtcNow });
        // Immutable peer identities are unique, including separate named mappings.
        var existing = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.PeerInstanceId == peer, ct);
        if (existing is not null)
        {
            db.ChangeTracker.Entries<SystemPairRecord>().Single(x => x.Entity.Id == pairId).State = EntityState.Detached;
            pairId = existing.Id;
            mapping = mapping with { PairId = pairId };
            json = JsonSerializer.Serialize(mapping, PairingTransport.Json);
        }
        var connection = new PairingConnectionRecord { Id = id, PairId = pairId, MappingJson = json,
            AdministratorId = administrator, Active = active, CreatedAtUtc = DateTimeOffset.UtcNow };
        if (protection is not null)
        {
            var producer = Guid.Parse("00000000-0000-4000-8000-000000000075");
            if (!await db.FlowRuntimeIdentity.AnyAsync(ct)) db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = producer });
            if (!await db.Set<InstallationIdentityRecord>().AnyAsync(ct)) db.Add(new InstallationIdentityRecord
            { InstanceId = Guid.Parse("c0c8e681-b1d0-4e44-92c2-50dce9d0c2ce"), SourceInstanceId = producer });
            var credential = new PairingBusinessCredential("scoped-test-peer", "owning-layer-fixture-secret", "https://peer.example.test/connect/token",
                "rateldesk-api", "https://peer.example.test/services", ["rateldesk.orchestration.callback"], null, null);
            connection.ProtectedOutboundCredential = protection.CreateProtector("NetRatel.Pairing.v1", id + "/outbound-business")
                .Protect(JsonSerializer.Serialize(credential, PairingTransport.Json));
        }
        db.Add(connection);
        await db.SaveChangesAsync(ct);
        var created = await registry.CreateAsync(new ServiceClientCreateRequest("Scoped paired client", tenant,
            peer, Organization, scopes, JsonSerializer.Serialize(new PairingResourceConstraints
                { TenantId = tenant.ToString(System.Globalization.CultureInfo.InvariantCulture) }, PairingTransport.Json),
            id.ToString("D"), PairingService.Hash(json), 1, clientId),
            administrator, pending: !active, ct: ct);
        connection.InboundPrincipalId = created.Principal.Id;
        await db.SaveChangesAsync(ct);
        return created;
    }

    internal static async Task SeedAdministratorAsync(NetRatelIdentityDbContext identity, string id = Administrator,
        CancellationToken ct = default)
    {
        if (!await identity.ApplicationPrincipals.AnyAsync(x => x.Id == id, ct))
        {
            identity.ApplicationPrincipals.Add(new() { Id = id });
            await identity.SaveChangesAsync(ct);
        }
    }

    internal sealed class Access : IEffectiveAccessService
    {
        internal bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal actor, string permission, int? tenantId, CancellationToken ct = default) => Task.FromResult(Allowed);
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal actor, string permission, CancellationToken ct = default) => Task.FromResult<int[]?>(Allowed ? null : []);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal actor, int? tenantId, CancellationToken ct = default) => Task.FromResult(new EffectiveAccessSnapshot(null, false, Allowed, new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
