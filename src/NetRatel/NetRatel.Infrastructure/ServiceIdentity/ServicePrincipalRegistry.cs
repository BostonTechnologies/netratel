using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.SystemPairing;
using NetRatel.Shared.ServiceIdentity;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.ServiceIdentity;

/// <summary>Business credentials belong to one real-tenant connection; every request reads its current durable authority.</summary>
public sealed class ServicePrincipalRegistry(OrchestratorDbContext db, IServiceIdentityRuntimeOptions options,
    IOptionsMonitor<ServiceIdentityOptions> rawOptions, IServiceClientDeploymentCatalog deployment,
    PairingAuthority authority, TimeProvider time) : IServicePrincipalRegistry
{
    public static bool IsValidClientId(string value) => value is not null && Regex.IsMatch(value, "^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant);
    public static string AliasKey(string value) => new(value.Select(c => char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_').ToArray());
    public static string[] ReadArray(string value) => JsonSerializer.Deserialize<string[]>(value) ?? [];
    public static PairingResourceConstraints ReadConstraints(ServicePrincipalRegistration value) => JsonSerializer.Deserialize<PairingResourceConstraints>(value.ResourceConstraintsJson, PairingTransport.Json) ?? new();
    public static bool IsMachinePrincipal(ClaimsPrincipal p) => p.HasClaim("auth_mode", "service") && p.HasClaim("token_use", ServiceIdentityClaims.Purpose);
    public async Task<CreatedServiceClient> CreateAsync(ServiceClientCreateRequest request, string actorId, bool pending = false, CancellationToken ct = default)
    {
        if (request.TenantId <= 0 || !Guid.TryParseExact(request.LinkId, "D", out _) || request.Scopes.Length == 0 || request.Scopes.Any(x => !ServiceIdentityScopes.Business.Contains(x, StringComparer.Ordinal)) || request.Scopes.Distinct(StringComparer.Ordinal).Count() != request.Scopes.Length || request.GrantHash.Length != 64)
            throw new ArgumentException("A real tenant and a saved named connection are required.");
        var constraints = JsonSerializer.Deserialize<PairingResourceConstraints>(request.ResourceConstraintsJson, PairingTransport.Json) ?? throw new ArgumentException("Explicit resources are required.");
        if (!await authority.ResourcesCurrentAsync(request.TenantId, constraints, ct) || !await authority.CanManageAsync(actorId, request.TenantId, false, true, ct)) throw new UnauthorizedAccessException("connection-resources-not-authorized");
        var clientId = request.ClientId ?? $"nrpair_{Guid.NewGuid():N}";
        if (!IsValidClientId(clientId) || deployment.OwnsIdentity(clientId)) throw new ServiceClientConflictException("Client identity is unavailable.");
        var value = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32)); var salt = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)); var now = time.GetUtcNow();
        var row = new ServicePrincipalRegistration { ClientId = clientId, NormalizedClientId = clientId.ToUpperInvariant(), AliasKey = AliasKey(clientId), Name = request.Name,
            TenantId = request.TenantId, PeerInstanceId = request.PeerInstanceId, PeerTenantId = request.PeerTenantId,
            AllowedScopesJson = JsonSerializer.Serialize(request.Scopes), ResourceConstraintsJson = request.ResourceConstraintsJson,
            LinkId = request.LinkId, GrantHash = request.GrantHash, LinkRevision = request.LinkRevision, Status = pending ? "pending" : "active", CreatedBy = actorId, ApprovedBy = actorId, CreatedAtUtc = now, UpdatedAtUtc = now };
        var credential = new ServicePrincipalSecret { ServicePrincipalId = row.Id, CredentialRevision = 1, Salt = salt, SecretHash = Hash(salt, value), Status = pending ? "pending" : "active", CreatedAtUtc = now, ExpiresAtUtc = now.AddDays(rawOptions.CurrentValue.CredentialMaximumAgeDays) };
        db.Add(row); db.Add(credential); await db.SaveChangesAsync(ct);
        return new(row, value, 1) { CredentialExpiresAtUtc = credential.ExpiresAtUtc };
    }
    public async Task<AuthenticatedServiceClient?> AuthenticateClientAsync(string clientId, string secret, CancellationToken ct = default)
    {
        if (!IsValidClientId(clientId) || secret.Length is < 32 or > 1024 || deployment.OwnsIdentity(clientId)) return null;
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.NormalizedClientId == clientId.ToUpperInvariant(), ct);
        if (row is null || row.ClientId != clientId || !await EnabledAsync(row, ct)) return null;
        var credentials = await db.Set<ServicePrincipalSecret>().AsNoTracking().Where(x => x.ServicePrincipalId == row.Id && x.Status == "active").ToListAsync(ct);
        var credential = credentials.FirstOrDefault(x => x.ExpiresAtUtc > time.GetUtcNow() && SecureEquals(x.SecretHash, Hash(x.Salt, secret)));
        return credential is null ? null : new(row, credential);
    }
    public string[] PermittedScopes(ServicePrincipalRegistration row, ServicePrincipalSecret credential) => row.Status == "active" && credential.Status == "active" ? ReadArray(row.AllowedScopesJson) : [];
    public async Task<bool> CanIssueScopesAsync(AuthenticatedServiceClient client, string[] scopes, CancellationToken ct = default)
    {
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == client.Principal.Id, ct);
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == client.Principal.Id && x.CredentialRevision == client.Credential.CredentialRevision, ct);
        return row is not null && credential is not null && row.Revision == client.Principal.Revision && row.Version == client.Principal.Version && credential.ExpiresAtUtc > time.GetUtcNow() &&
            scopes.All(x => PermittedScopes(row, credential).Contains(x, StringComparer.Ordinal)) && await EnabledAsync(row, ct);
    }
    public async Task<ServicePrincipalRegistration?> ResolvePrincipalAsync(ClaimsPrincipal principal, string? requiredScope = null, CancellationToken ct = default)
    {
        if (!IsMachinePrincipal(principal) || !Guid.TryParseExact(principal.FindFirstValue(ServiceIdentityClaims.PrincipalId), "N", out var id) || !long.TryParse(principal.FindFirstValue(ServiceIdentityClaims.CredentialRevision), out var revision)) return null;
        var row = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct);
        var credential = await db.Set<ServicePrincipalSecret>().AsNoTracking().SingleOrDefaultAsync(x => x.ServicePrincipalId == id && x.CredentialRevision == revision, ct);
        if (row is null || credential is null || credential.Status != "active" || credential.ExpiresAtUtc <= time.GetUtcNow() || !await EnabledAsync(row, ct)) return null;
        if (principal.FindFirstValue("sub") != $"service:{row.Id:N}" || principal.FindFirstValue("client_id") != row.ClientId || principal.FindFirstValue(ServiceIdentityClaims.GrantRevision) != row.Revision.ToString(CultureInfo.InvariantCulture) ||
            principal.FindFirstValue(ServiceIdentityClaims.TenantId) != row.TenantId.ToString(CultureInfo.InvariantCulture) || principal.FindFirstValue(ServiceIdentityClaims.PeerInstanceId) != row.PeerInstanceId || principal.FindFirstValue(ServiceIdentityClaims.PeerTenantId) != row.PeerTenantId ||
            principal.FindFirstValue(ServiceIdentityClaims.LinkId) != row.LinkId || principal.FindFirstValue(ServiceIdentityClaims.GrantHash) != row.GrantHash || principal.FindFirstValue(ServiceIdentityClaims.LinkRevision) != row.LinkRevision.ToString(CultureInfo.InvariantCulture)) return null;
        var scopes = principal.FindAll("scope").SelectMany(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToArray();
        if (scopes.Length == 0 || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length || scopes.Any(x => !PermittedScopes(row, credential).Contains(x, StringComparer.Ordinal)) || requiredScope is not null && !scopes.Contains(requiredScope, StringComparer.Ordinal)) return null;
        // Credentials store the real tenant authority; routes receive a fresh, exact resource projection.
        row.ResourceConstraintsJson = JsonSerializer.Serialize(await authority.ResourcesAsync(row.TenantId, ct), PairingTransport.Json);
        return row;
    }
    private async Task<bool> EnabledAsync(ServicePrincipalRegistration row, CancellationToken ct)
    {
        if (row.Status != "active" || !Guid.TryParseExact(row.LinkId, "D", out var id)) return false;
        var connection = await db.Set<PairingConnectionRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.Active && x.DeletedAtUtc == null, ct);
        if (connection is null || connection.InboundPrincipalId != row.Id || connection.Revision != row.LinkRevision || PairingService.Hash(connection.MappingJson) != row.GrantHash) return false;
        var pair = await db.Set<SystemPairRecord>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == connection.PairId && x.DeletedAtUtc == null && x.ProtectedOutboundSecret != null, ct);
        if (pair is null || pair.PeerInstanceId != row.PeerInstanceId) return false;
        var mapping = JsonSerializer.Deserialize<PairingMapping>(connection.MappingJson, PairingTransport.Json)!;
        return mapping.RunAutomation && mapping.NetRatelTenantId == row.TenantId.ToString(CultureInfo.InvariantCulture) && mapping.RatelDeskOrganizationId == row.PeerTenantId &&
            await authority.CanManageAsync(pair.AdministratorId, row.TenantId, mapping.CreateIncidents, true, ct) && await authority.CanManageAsync(connection.AdministratorId, row.TenantId, mapping.CreateIncidents, true, ct) &&
            await authority.ResourcesCurrentAsync(row.TenantId, ReadConstraints(row), ct);
    }
    public async Task ActivateAsync(Guid id, CancellationToken ct = default)
    {
        var row = await db.Set<ServicePrincipalRegistration>().SingleAsync(x => x.Id == id, ct);
        if (row.Status == "revoked") throw new ServiceClientConflictException("A revoked credential cannot be reopened.");
        row.Status = "active"; row.Version++; row.UpdatedAtUtc = time.GetUtcNow();
        foreach (var secret in await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).ToListAsync(ct)) if (secret.Status != "revoked") secret.Status = "active";
        await db.SaveChangesAsync(ct);
    }
    public async Task RevokeAsync(Guid id, CancellationToken ct = default)
    {
        await db.Set<ServicePrincipalRegistration>().Where(x => x.Id == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "revoked").SetProperty(y => y.Version, y => y.Version + 1).SetProperty(y => y.RevokedAtUtc, time.GetUtcNow()), ct);
        await db.Set<ServicePrincipalSecret>().Where(x => x.ServicePrincipalId == id).ExecuteUpdateAsync(x => x.SetProperty(y => y.Status, "revoked"), ct);
    }
    private static string Hash(string salt, string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(salt + ":" + value)));
    private static bool SecureEquals(string a, string b) => a.Length == b.Length && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
}
