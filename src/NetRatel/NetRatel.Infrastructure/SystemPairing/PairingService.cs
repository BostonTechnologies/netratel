using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
using NetRatel.Shared.Contracts.RatelDesk;
namespace NetRatel.Infrastructure.SystemPairing;

/// <summary>A setup-only system relationship followed by independent named business mappings.</summary>
public sealed partial class PairingService(OrchestratorDbContext db, IDataProtectionProvider protection,
    InstallationIdentityStore identities, IServicePublicSettingsResolver settings, ServiceSigningKeyStore signing,
    PairingAuthority authority, IServicePrincipalRegistry principals, PairingTransport transport,
    IRatelDeskConnectorStore connectors, RatelDeskConnectorReceiver receiver, TimeProvider clock)
{
    private static readonly string Alphabet = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";
    private static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(5);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    public static string PairId(string a, string b) => Hash(string.Join(':', new[] { a, b }.Order(StringComparer.Ordinal)));
    private static string Json<T>(T value) => JsonSerializer.Serialize(value, PairingTransport.Json);
    private static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, PairingTransport.Json)!;
    private string Protect(string purpose, string value) => protection.CreateProtector("NetRatel.Pairing.v1", purpose).Protect(value);
    private string Unprotect(string purpose, string value) => protection.CreateProtector("NetRatel.Pairing.v1", purpose).Unprotect(value);
    private static string NewSecret() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    private static bool SecretValid(string? value) => value is { Length: 43 } && value.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');
    private static bool Same(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private async Task LockAsync(CancellationToken ct) => await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(786526194319)", ct);
    public async Task<PairingMetadata> MetadataAsync(CancellationToken ct)
    {
        var installed = await identities.GetAsync(ct); var config = (await settings.ResolveAsync(ct)).Identity;
        if (string.IsNullOrEmpty(config.WebBaseUrl) || string.IsNullOrEmpty(config.ApiBaseUrl))
            throw new PairingException(503, "public-address-unavailable", "The installation's public Web and API addresses are not configured.");
        using var key = await signing.GetSigningKeyAsync(ct);
        return new(PairingProtocol.Contract, "netratel", installed.InstanceId.ToString("D"), "NetRatel", PairingTransport.Origin(config.WebBaseUrl), PairingTransport.Origin(config.ApiBaseUrl), installed.SourceInstanceId!.Value.ToString("D"), Convert.ToBase64String(key.Rsa.ExportSubjectPublicKeyInfo()));
    }
    public async Task<PairingMetadataProof> MetadataProofAsync(string nonce, CancellationToken ct)
    {
        if (!SecretValid(nonce)) throw new PairingException(422, "nonce-invalid", "A bounded metadata challenge is required.");
        var metadata = await MetadataAsync(ct); using var key = await signing.GetSigningKeyAsync(ct);
        return new(metadata, nonce, Convert.ToBase64String(key.Rsa.SignData(Encoding.UTF8.GetBytes(Json(metadata) + ":" + nonce), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
    }
    public async Task<PairingCodeResponse> GenerateAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct);
        var row = await db.Set<PairingCodeRecord>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (row is null) { row = new(); db.Add(row); }
        var code = new string(Enumerable.Range(0, 8).Select(_ => Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)]).ToArray());
        row.CodeHash = Hash(code); row.AdministratorId = PairingAuthority.ActorId(actor); row.ExpiresAtUtc = clock.GetUtcNow() + CodeLifetime; row.FailedAttempts = 0;
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(code[..4] + "-" + code[4..], row.ExpiresAtUtc);
    }
    private static string NormalizeCode(string value) => value.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
    private async Task<SystemPairRecord> EnsurePairAsync(PairingMetadata peer, string actorId, bool allowRenew, CancellationToken ct)
    {
        var local = await identities.GetAsync(ct); var id = PairId(local.InstanceId.ToString("D"), peer.InstallationId);
        var row = await db.Set<SystemPairRecord>().SingleOrDefaultAsync(x => x.Id == id, ct);
        if (row is not null)
        {
            var retained = Read<PairingMetadata>(row.PeerMetadataJson);
            if (retained.InstallationId != peer.InstallationId || retained.Product != peer.Product || retained.SigningPublicKey != peer.SigningPublicKey)
                throw new PairingException(409, "peer-identity-changed", "The peer's immutable signing identity changed. Restore its original protected signing keys.");
            if (row.DeletedAtUtc is not null)
            {
                if (!allowRenew) throw new PairingException(410, "pair-deleted", "This pairing was deleted. Generate and use a fresh code.");
                var secret = NewSecret(); row.InboundSecretHash = Hash(secret); row.ProtectedInboundSecret = Protect(id + "/inbound", secret); row.ProtectedOutboundSecret = null; row.AdministratorId = actorId; row.DeletedAtUtc = null; row.Revision++;
            }
            row.PeerMetadataJson = Json(peer); return row;
        }
        var createdSecret = NewSecret();
        row = new() { Id = id, PeerInstanceId = peer.InstallationId, PeerMetadataJson = Json(peer), AdministratorId = actorId, InboundSecretHash = Hash(createdSecret), ProtectedInboundSecret = Protect(id + "/inbound", createdSecret), CreatedAtUtc = clock.GetUtcNow() };
        db.Add(row); return row;
    }
    private void VerifyExchangeSignature(PairingExchangeRequest request)
    {
        try
        {
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(request.Peer.SigningPublicKey), out _);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Json(request with { Signature = "" })), Convert.FromBase64String(request.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { throw new PairingException(403, "caller-proof-invalid", "The caller could not prove control of its installation identity."); }
    }
    public async Task<PairingExchangeResponse> ExchangeAsync(PairingExchangeRequest request, CancellationToken ct)
    {
        if (request is null || request.Peer is null || request.Code is not { Length: > 0 and <= 32 } || request.OperationId == Guid.Empty || !SecretValid(request.InboundSecret) || request.Signature is not { Length: > 0 and <= 8192 }) throw new PairingException(422, "exchange-invalid", "The pairing exchange is incomplete.");
        PairingTransport.ValidateMetadata(request.Peer, "rateldesk"); VerifyExchangeSignature(request);
        var verified = await transport.DiscoverAsync(request.Peer.WebOrigin, "rateldesk", ct);
        if (Json(verified) != Json(request.Peer)) throw new PairingException(403, "caller-identity-mismatch", "The caller's current addresses and signed installation identity do not match.");
        var local = await MetadataAsync(ct); var fingerprint = Hash(Json(request));
        await using var tx = await db.Database.BeginTransactionAsync(ct); await LockAsync(ct);
        var retry = await db.Set<PairingRedemption>().AsNoTracking().SingleOrDefaultAsync(x => x.OperationId == request.OperationId, ct);
        if (retry is not null)
        {
            var current = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == retry.PairId && x.DeletedAtUtc == null, ct);
            if (retry.ExpiresAtUtc <= clock.GetUtcNow() || retry.RequestHash != fingerprint || retry.PeerInstanceId != request.Peer.InstallationId || current?.ProtectedOutboundSecret is null)
                throw new PairingException(410, "pairing-retry-unavailable", "This code was already consumed or the successful pairing was deleted. Use a fresh code.");
            await authority.RequireAdministratorAsync(PairingAuthority.Retained(current.AdministratorId), ct);
            var retained = Read<PairingExchangeResponse>(Unprotect(retry.PairId + "/exchange", retry.ProtectedResponse));
            if (!Same(Hash(retained.InboundSecret), current.InboundSecretHash) || !Same(Unprotect(current.Id + "/outbound", current.ProtectedOutboundSecret), request.InboundSecret))
                throw new PairingException(410, "pairing-retry-unavailable", "The system pairing changed after this exchange. Generate and use a fresh code.");
            return retained;
        }
        var code = await db.Set<PairingCodeRecord>().SingleOrDefaultAsync(x => x.Id == 1, ct);
        if (code is null || code.ExpiresAtUtc <= clock.GetUtcNow() || code.FailedAttempts >= 10 || !Same(code.CodeHash, Hash(NormalizeCode(request.Code))))
        {
            if (code is not null) { code.FailedAttempts++; if (code.FailedAttempts >= 10) code.CodeHash = ""; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); }
            throw new PairingException(403, "pairing-code-invalid", "The pairing code is wrong, expired, replaced or already used. Generate a fresh code on the other system.");
        }
        await authority.RequireAdministratorAsync(PairingAuthority.Retained(code.AdministratorId), ct);
        var pair = await EnsurePairAsync(request.Peer, code.AdministratorId, true, ct);
        if (pair.ProtectedOutboundSecret is not null && !Same(Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret), request.InboundSecret))
        {
            await RevokePairMappingsAsync(pair.Id, ct);
            var newSecret = NewSecret(); pair.InboundSecretHash = Hash(newSecret); pair.ProtectedInboundSecret = Protect(pair.Id + "/inbound", newSecret); pair.Revision++;
        }
        pair.ProtectedOutboundSecret = Protect(pair.Id + "/outbound", request.InboundSecret);
        code.CodeHash = ""; code.ExpiresAtUtc = clock.GetUtcNow();
        var result = new PairingExchangeResponse(pair.Id, local, Unprotect(pair.Id + "/inbound", pair.ProtectedInboundSecret));
        using (var key = await signing.GetSigningKeyAsync(ct)) result = result with { Signature = Convert.ToBase64String(key.Rsa.SignData(Encoding.UTF8.GetBytes(Json(result)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
        db.Add(new PairingRedemption { OperationId = request.OperationId, PeerInstanceId = request.Peer.InstallationId, RequestHash = fingerprint, PairId = pair.Id, ProtectedResponse = Protect(pair.Id + "/exchange", Json(result)), ExpiresAtUtc = clock.GetUtcNow().AddMinutes(10) });
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
    public async Task<PairingConnectionDto> ConnectAsync(PairingConnectRequest request, ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        if (request is null || request.PairingCode is not { Length: > 0 and <= 32 } || request.OperationId == Guid.Empty) throw new PairingException(422, "operation-required", "Start this pairing operation again.");
        var peer = await transport.DiscoverAsync(request.Address, "rateldesk", ct); var local = await MetadataAsync(ct);
        if (peer.InstallationId == local.InstallationId) throw new PairingException(422, "same-installation", "Choose the other product's installation.");
        SystemPairRecord pair; PairingExchangeRequest exchange; long revision;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(ct);
            var existing = await db.Set<PairingConnectAttempt>().SingleOrDefaultAsync(x => x.OperationId == request.OperationId, ct);
            if (existing is not null)
            {
                exchange = Read<PairingExchangeRequest>(Unprotect(existing.PairId + "/connect", existing.ProtectedRequest));
                pair = await db.Set<SystemPairRecord>().SingleAsync(x => x.Id == existing.PairId, ct);
                if (existing.ExpiresAtUtc <= clock.GetUtcNow() || pair.DeletedAtUtc is not null || pair.Revision != existing.PairRevision || exchange.Peer.InstallationId != local.InstallationId || peer.InstallationId != pair.PeerInstanceId || NormalizeCode(exchange.Code) != NormalizeCode(request.PairingCode))
                    throw new PairingException(410, "pairing-retry-unavailable", "This operation changed or was deleted. Use a fresh pairing code.");
                if (existing.Completed && pair.ProtectedOutboundSecret is not null) return ToDto(pair, null);
            }
            else
            {
                pair = await EnsurePairAsync(peer, PairingAuthority.ActorId(actor), true, ct);
                exchange = new(request.PairingCode, request.OperationId, local, Unprotect(pair.Id + "/inbound", pair.ProtectedInboundSecret));
                using var key = await signing.GetSigningKeyAsync(ct);
                exchange = exchange with { Signature = Convert.ToBase64String(key.Rsa.SignData(Encoding.UTF8.GetBytes(Json(exchange)), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
                db.Add(new PairingConnectAttempt { OperationId = request.OperationId, PairId = pair.Id, PairRevision = pair.Revision, RequestHash = Hash(Json(exchange)), ProtectedRequest = Protect(pair.Id + "/connect", Json(exchange)), ExpiresAtUtc = clock.GetUtcNow().AddMinutes(10) });
            }
            revision = pair.Revision; await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        var response = await transport.SendAsync<PairingExchangeResponse>(peer.ApiOrigin, HttpMethod.Post, "/exchange", exchange, null, null, ct);
        try
        {
            if (response?.Peer is null || response.Signature is not { Length: > 0 and <= 8192 }) throw new CryptographicException();
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peer.SigningPublicKey), out _);
            if (!rsa.VerifyData(Encoding.UTF8.GetBytes(Json(response with { Signature = "" })), Convert.FromBase64String(response.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { throw new PairingException(403, "exchange-response-proof-invalid", "The peer could not authenticate its pairing response."); }
        if (response.PairId != pair.Id || Json(response.Peer) != Json(peer) || !SecretValid(response.InboundSecret)) throw new PairingException(502, "exchange-response-invalid", "The peer's pairing response does not match this installation.");
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAsync(ct); db.ChangeTracker.Clear();
            pair = await db.Set<SystemPairRecord>().SingleAsync(x => x.Id == pair.Id, ct);
            if (pair.DeletedAtUtc is not null || pair.Revision != revision) throw new PairingException(410, "pair-deleted", "The pairing was deleted while the exchange completed.");
            if (pair.ProtectedOutboundSecret is not null && !Same(Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret), response.InboundSecret))
            {
                // A peer deleted and freshly paired while this side retained its old business mappings.
                // Preserve the local inbound secret already accepted by the peer, but retire old authority.
                await RevokePairMappingsAsync(pair.Id, ct); pair.Revision++;
            }
            pair.ProtectedOutboundSecret = Protect(pair.Id + "/outbound", response.InboundSecret);
            var completed = await db.Set<PairingConnectAttempt>().SingleAsync(x => x.OperationId == request.OperationId, ct);
            completed.PairRevision = pair.Revision; completed.Completed = true;
            await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        }
        return ToDto(pair, null);
    }
    public async Task<SystemPairRecord> AuthenticateSetupAsync(string peerId, string secret, CancellationToken ct, string? callerSecretHash = null)
    {
        if (!Guid.TryParseExact(peerId, "D", out _) || !SecretValid(secret)) throw new PairingException(401, "pairing-authentication-required", "Current system pairing authentication is required.");
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.PeerInstanceId == peerId && x.DeletedAtUtc == null, ct);
        if (pair is null || pair.ProtectedOutboundSecret is null || callerSecretHash is not { Length: 64 } || !Same(pair.InboundSecretHash, Hash(secret)) || !Same(Hash(Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret)), callerSecretHash)) throw new PairingException(401, "pairing-revoked", "This system pairing credential is no longer valid.");
        await authority.RequireAdministratorAsync(PairingAuthority.Retained(pair.AdministratorId), ct); return pair;
    }
    private async Task<SystemPairRecord> HumanPairAsync(string id, ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.DeletedAtUtc == null, ct) ?? throw new PairingException(404, "pair-unavailable", "This system pairing was removed.");
        await authority.RequireAdministratorAsync(PairingAuthority.Retained(pair.AdministratorId), ct);
        return pair;
    }
    private PairingConnectionDto ToDto(SystemPairRecord pair, PairingConnectionRecord? connection) => new(connection?.Id.ToString("D") ?? pair.Id, pair.Id,
        connection is null ? null : Read<PairingMapping>(connection.MappingJson), Read<PairingMetadata>(pair.PeerMetadataJson), connection is null ? pair.ProtectedOutboundSecret is null ? "Pairing incomplete" : "Systems paired" : connection.Active ? "Connected" : "Systems paired", connection?.LastTestJson is null ? null : Read<PairingTestResult>(connection.LastTestJson));
    public async Task<PairingConnectionDto[]> ListAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        await authority.RequireAdministratorAsync(actor, ct);
        var pairs = await db.Set<SystemPairRecord>().AsNoTracking().Where(x => x.DeletedAtUtc == null).OrderBy(x => x.CreatedAtUtc).ToArrayAsync(ct);
        var mappings = await db.Set<PairingConnectionRecord>().AsNoTracking().Where(x => x.DeletedAtUtc == null).ToArrayAsync(ct); var result = new List<PairingConnectionDto>();
        var actorId = PairingAuthority.ActorId(actor); var instanceAccess = await authority.CanManageInstanceAsync(actor, ct);
        foreach (var pair in pairs)
        {
            var children = mappings.Where(x => x.PairId == pair.Id).ToArray();
            if (children.Length == 0)
            {
                if (instanceAccess || pair.AdministratorId == actorId) result.Add(ToDto(pair, null));
            }
            else foreach (var child in children)
            {
                var mapping = Read<PairingMapping>(child.MappingJson);
                if (instanceAccess || !child.Active && child.AdministratorId == actorId || await authority.CanManageAsync(actorId, int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture), false, false, ct)) result.Add(ToDto(pair, child));
            }
        }
        return result.ToArray();
    }
    public Task<PairingDirectory> DirectoryAsync(SystemPairRecord pair, CancellationToken ct) => authority.DirectoryAsync(pair.AdministratorId, ct);
    public async Task<PairingDirectories> DirectoriesAsync(string id, ClaimsPrincipal actor, CancellationToken ct)
    {
        var pair = await HumanPairAsync(id, actor, ct);
        if (pair.ProtectedOutboundSecret is null) throw new PairingException(409, "pair-incomplete", "Complete Pair & connect before choosing the tenant mapping.");
        var owned = await authority.DirectoryAsync(pair.AdministratorId, ct); var current = await authority.DirectoryAsync(PairingAuthority.ActorId(actor), ct);
        var peer = Read<PairingMetadata>(pair.PeerMetadataJson); var installed = await identities.GetAsync(ct);
        var remote = await transport.SendAsync<PairingDirectory>(peer.ApiOrigin, HttpMethod.Get, "/directory", null, Unprotect(pair.Id + "/outbound", pair.ProtectedOutboundSecret), installed.InstanceId.ToString("D"), ct, callerSecretHash: pair.InboundSecretHash);
        return new(owned.Tenants.Where(x => current.Tenants.Any(y => y.Id == x.Id)).ToArray(), remote.Tenants, remote.Customers);
    }
}
