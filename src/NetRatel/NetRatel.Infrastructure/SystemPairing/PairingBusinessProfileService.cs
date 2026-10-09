using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;
public sealed record PairingBusinessProfile(Guid MappingId, long Revision, string AuthorityHash,
    PairingMapping Mapping, PairingMetadata Peer, PairingBusinessCredential Credential,
    string AdministratorId, long PairRevision);
public sealed record PairingCallbackAuthorization(string CallbackUrl, string BearerToken);
public sealed class PairingBusinessProfileService(OrchestratorDbContext db, IDataProtectionProvider protection,
    PairingAuthority authority, InstallationIdentityStore identities, PairingTransport transport)
{
    public async Task<PairingBusinessProfile> ResolveAsync(int tenant, string mappingId, string scope, CancellationToken ct)
    {
        if (!Guid.TryParseExact(mappingId, "D", out var id)) throw new PairingException(403, "connection-reference-invalid", "The business connection reference is invalid.");
        var row = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active && x.DeletedAtUtc == null, ct);
        if (row?.ProtectedOutboundCredential is null) throw new PairingException(403, "connection-revoked", "The selected business connection is no longer active.");
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == row.PairId && x.DeletedAtUtc == null && x.ProtectedOutboundSecret != null, ct);
        if (pair is null) throw new PairingException(403, "pair-revoked", "This system pairing is no longer active.");
        var mapping = JsonSerializer.Deserialize<PairingMapping>(row.MappingJson, PairingTransport.Json)!;
        if (mapping.NetRatelTenantId != tenant.ToString(CultureInfo.InvariantCulture) ||
            !PairingService.ExpectedRatelDeskScopes(mapping).Contains(scope, StringComparer.Ordinal) ||
            !await authority.CanManageAsync(pair.AdministratorId, tenant, mapping.CreateIncidents, mapping.RunAutomation, ct) ||
            !await authority.CanManageAsync(row.AdministratorId, tenant, mapping.CreateIncidents, mapping.RunAutomation, ct))
            throw new PairingException(403, "connection-authority-removed", "Current tenant capability authority is required for this connection.");
        var value = protection.CreateProtector("NetRatel.Pairing.v1", row.Id + "/outbound-business").Unprotect(row.ProtectedOutboundCredential);
        var credential = JsonSerializer.Deserialize<PairingBusinessCredential>(value, PairingTransport.Json)!;
        var peer = JsonSerializer.Deserialize<PairingMetadata>(pair.PeerMetadataJson, PairingTransport.Json)!;
        var installed = await identities.GetAsync(ct);
        if (!credential.Scopes.Contains(scope, StringComparer.Ordinal) || (mapping.CreateIncidents ? credential.SourceInstanceId != installed.SourceInstanceId!.Value.ToString("D") || credential.SourceNamespaceId != mappingId : credential.SourceInstanceId is not null || credential.SourceNamespaceId is not null) || credential.TokenEndpoint != peer.ApiOrigin + "/connect/token")
            throw new PairingException(403, "connection-credential-mismatch", "The business credential differs from the saved mapping and producer identity.");
        return new(id, row.Revision, PairingService.Hash(row.MappingJson), mapping, peer, credential, row.AdministratorId, pair.Revision);
    }
    public async Task<string> GetAccessTokenAsync(PairingBusinessProfile profile, string scope, CancellationToken ct)
    {
        var bearer = await transport.TokenAsync(profile.Peer, profile.Credential, scope, ct);
        var current = await ResolveAsync(int.Parse(profile.Mapping.NetRatelTenantId, CultureInfo.InvariantCulture), profile.MappingId.ToString("D"), scope, ct);
        if (current.Revision != profile.Revision || current.AuthorityHash != profile.AuthorityHash || current.PairRevision != profile.PairRevision || current.Peer != profile.Peer || current.Credential.ClientId != profile.Credential.ClientId)
            throw new PairingException(403, "connection-changed", "The connection authority changed while authenticated access was obtained.");
        return bearer;
    }
    public async Task<PairingCallbackAuthorization> GetCallbackAuthorizationAsync(string mappingId, int tenant,
        string peerInstanceId, string peerTenantId, long revision, string authorityHash, string callbackUrl,
        string parentRequestId, string taskId, CancellationToken ct)
    {
        var profile = await ResolveAsync(tenant, mappingId, "rateldesk.orchestration.callback", ct);
        var expected = profile.Peer.ApiOrigin + "/api/v1/orchestration/provider/callback";
        if (profile.Revision != revision || profile.AuthorityHash != authorityHash || profile.Peer.InstallationId != peerInstanceId || profile.Mapping.RatelDeskOrganizationId != peerTenantId || callbackUrl != expected || string.IsNullOrWhiteSpace(parentRequestId) || string.IsNullOrWhiteSpace(taskId))
            throw new PairingException(403, "callback-binding-changed", "The correlated callback no longer belongs to this connection.");
        return new(expected, await GetAccessTokenAsync(profile, "rateldesk.orchestration.callback", ct));
    }
}
