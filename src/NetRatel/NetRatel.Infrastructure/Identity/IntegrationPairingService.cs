using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Client;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public sealed class IntegrationPairingUnavailableException() : Exception("The connection request is unavailable. Start a new connection request.");

/// <summary>Durable, capability-bound pairing with one ordinary credential and a bounded retry envelope.</summary>
public sealed class IntegrationPairingService(NetRatelIdentityDbContext db, IIntegrationCredentialService credentials,
    IIntegrationCredentialGrantValidator validator, IDataProtectionProvider protection, TimeProvider clock,
    IDeploymentBrandingService? branding = null, IConfiguration? configuration = null, OrchestratorDbContext? applicationDb = null)
{
    private static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DeliveryLifetime = TimeSpan.FromMinutes(5);
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<IntegrationPairingStartResponse> StartAsync(IntegrationPairingStartRequest input, string origin, string? canonicalHttpMcpResource = null,
        CancellationToken cancellationToken = default)
    {
        origin = ClientEndpointAddress.NormalizePublicOrigin(origin).TrimEnd('/');
        var name = input.Name?.Trim();
        var requested = input.RequestedPermissions ?? [];
        if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(char.IsControl) || !Enum.IsDefined(input.Purpose) ||
            requested.Count > 32 || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count ||
            requested.Any(permission => !Authorization.NetRatelPermissions.All.Contains(permission) || permission == Authorization.NetRatelPermissions.IntegrationManagement))
            throw new IntegrationPairingUnavailableException();
        if (await db.IntegrationPairingRequests.CountAsync(request => request.State == IntegrationPairingState.Pending ||
            request.State == IntegrationPairingState.Approved || request.State == IntegrationPairingState.Issued, cancellationToken).ConfigureAwait(false) >= 4096)
            throw new IntegrationPairingUnavailableException();
        var resource = input.Purpose == PairingCredentialPurpose.HttpMcp ? canonicalHttpMcpResource ?? origin + "/mcp" : null;
        if (resource is not null && !McpResourceUri.TryNormalize(resource, out resource)) throw new IntegrationPairingUnavailableException();
        if ((input.Purpose == PairingCredentialPurpose.Api && !string.IsNullOrWhiteSpace(input.Resource)) ||
            (input.Purpose == PairingCredentialPurpose.HttpMcp && input.Resource is not null &&
                (!McpResourceUri.TryNormalize(input.Resource, out var supplied) || supplied != resource)))
            throw new IntegrationPairingUnavailableException();
        var now = clock.GetUtcNow();
        var code = string.Concat(RandomNumberGenerator.GetBytes(8).Select(value => Alphabet[value % Alphabet.Length]));
        var capability = "nrt_pair_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var request = new IntegrationPairingRequest
        {
            Name = name, PublicOrigin = origin, Purpose = (IntegrationCredentialPurpose)input.Purpose, Resource = resource,
            UserCodeHash = IntegrationCredentialService.Hash(code), DeviceCapabilityHash = IntegrationCredentialService.Hash(capability),
            RequestedPermissionsJson = JsonSerializer.Serialize(requested), CreatedAtUtc = now, ExpiresAtUtc = now.Add(RequestLifetime),
            NextPollAtUtc = now.AddSeconds(IntegrationPairingProtocol.PollIntervalSeconds)
        };
        db.IntegrationPairingRequests.Add(request);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return new(request.Id, FormatCode(code), capability, origin + "/account/integration-credentials?tab=credentials", origin,
            request.ExpiresAtUtc, request.PollIntervalSeconds);
    }

    public Task<IntegrationPairingReview> ReviewAsync(string code, CancellationToken cancellationToken = default) =>
        MutateAsync(CodeHash(code), false, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.State != IntegrationPairingState.Pending) throw new IntegrationPairingUnavailableException();
            return new IntegrationPairingReview(request.Id, FormatCode(NormalizeCode(code)), request.Name,
                (PairingCredentialPurpose)request.Purpose, request.PublicOrigin, request.Resource,
                Deserialize<string>(request.RequestedPermissionsJson), request.ExpiresAtUtc, Status(request));
        }, cancellationToken);

    public Task<IntegrationPairingPollResponse> ApproveAsync(ClaimsPrincipal principal, IntegrationPairingApprovalRequest approval,
        CancellationToken cancellationToken = default) => MutateAsync(CodeHash(approval.UserCode), false, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.State != IntegrationPairingState.Pending || !await BindingIsCurrentAsync(request, cancellationToken).ConfigureAwait(false))
                throw new IntegrationPairingUnavailableException();
            var owner = principal.FindFirstValue("netratel_principal_id");
            var create = new IntegrationCredentialCreateRequest(request.Name, request.Purpose, approval.ExpiresAtUtc,
                (approval.Grants ?? []).Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray(),
                request.Resource, approval.InstancePermissions ?? []);
            await validator.ValidateAsync(principal, create, cancellationToken).ConfigureAwait(false);
            // Browser provider claims are not a durable delegation grant. Use the same current owner identity as redemption.
            var durableOwner = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", owner!)], "pairing-owner"));
            try { await validator.ValidateAsync(durableOwner, create, cancellationToken).ConfigureAwait(false); }
            catch (IntegrationCredentialGrantValidationException)
            { throw new IntegrationCredentialGrantValidationException("durableAuthority"); }
            request.OwnerPrincipalId = owner;
            request.ApprovedGrantsJson = JsonSerializer.Serialize(create.Grants);
            request.ApprovedInstancePermissionsJson = JsonSerializer.Serialize(create.InstancePermissions);
            request.CredentialExpiresAtUtc = create.ExpiresAtUtc;
            request.State = IntegrationPairingState.Approved;
            await SaveAsync(request, cancellationToken).ConfigureAwait(false);
            return new IntegrationPairingPollResponse(Status(request));
        }, cancellationToken);

    public Task<IntegrationPairingPollResponse> DenyAsync(string code, CancellationToken cancellationToken = default) =>
        MutateAsync(CodeHash(code), false, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.State != IntegrationPairingState.Pending) throw new IntegrationPairingUnavailableException();
            request.State = IntegrationPairingState.Denied;
            await SaveAsync(request, cancellationToken).ConfigureAwait(false);
            return new IntegrationPairingPollResponse(Status(request));
        }, cancellationToken);

    public Task<IntegrationPairingPollResponse> PollAsync(string capability, CancellationToken cancellationToken = default) =>
        MutateAsync(CapabilityHash(capability), true, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            if (request.State is not (IntegrationPairingState.Pending or IntegrationPairingState.Approved or IntegrationPairingState.Issued))
                return new IntegrationPairingPollResponse(Status(request));
            if (request.NextPollAtUtc > now)
            {
                request.PollIntervalSeconds = Math.Min(30, request.PollIntervalSeconds + 5);
                request.NextPollAtUtc = now.AddSeconds(request.PollIntervalSeconds);
                await SaveAsync(request, cancellationToken).ConfigureAwait(false);
                return new IntegrationPairingPollResponse("slow_down", request.PollIntervalSeconds);
            }
            request.NextPollAtUtc = now.AddSeconds(request.PollIntervalSeconds);
            // Compare-and-swap before creating a credential. The transaction rolls both records back on failure.
            await SaveAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.State == IntegrationPairingState.Pending)
                return new IntegrationPairingPollResponse("pending", request.PollIntervalSeconds);
            if (!await ValidateApprovalAsync(request, cancellationToken).ConfigureAwait(false))
            {
                await RevokeAndClearAsync(request, IntegrationPairingState.Denied, cancellationToken).ConfigureAwait(false);
                return new IntegrationPairingPollResponse("denied");
            }
            if (request.State == IntegrationPairingState.Approved)
            {
                var created = await credentials.CreateAsync(request.OwnerPrincipalId!, CreateRequest(request), cancellationToken).ConfigureAwait(false);
                request.CredentialId = created.CredentialId;
                (await db.IntegrationCredentials.SingleAsync(credential => credential.Id == created.CredentialId, cancellationToken).ConfigureAwait(false)).PairingRequestId = request.Id;
                request.DeliveryExpiresAtUtc = now.Add(DeliveryLifetime);
                request.ProtectedDelivery = Protector(request).Protect(created.Secret);
                request.State = IntegrationPairingState.Issued;
                await SaveAsync(request, cancellationToken).ConfigureAwait(false);
            }
            string secret;
            try { secret = Protector(request).Unprotect(request.ProtectedDelivery!); }
            catch (CryptographicException)
            {
                await RevokeAndClearAsync(request, IntegrationPairingState.Cancelled, cancellationToken).ConfigureAwait(false);
                return new IntegrationPairingPollResponse("cancelled");
            }
            return new IntegrationPairingPollResponse("issued", request.PollIntervalSeconds,
                new(request.CredentialId!, secret, request.Name, (PairingCredentialPurpose)request.Purpose, request.Resource,
                    request.CredentialExpiresAtUtc!.Value,
                    await DeliveryGrantsAsync(request, cancellationToken).ConfigureAwait(false),
                    Deserialize<string>(request.ApprovedInstancePermissionsJson!)));
        }, cancellationToken);

    public Task<IntegrationPairingPollResponse> CancelAsync(string capability, CancellationToken cancellationToken = default) =>
        MutateAsync(CapabilityHash(capability), true, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            // Acknowledgement ends this capability's ability to revoke; account credential management owns revocation after save.
            if (request.State is IntegrationPairingState.Pending or IntegrationPairingState.Approved or IntegrationPairingState.Issued)
                await RevokeAndClearAsync(request, IntegrationPairingState.Cancelled, cancellationToken).ConfigureAwait(false);
            return new IntegrationPairingPollResponse(Status(request));
        }, cancellationToken);

    public Task<IntegrationPairingPollResponse> AcknowledgeAsync(string capability, string credentialId,
        CancellationToken cancellationToken = default) => MutateAsync(CapabilityHash(capability), true, async request =>
        {
            await ExpireAsync(request, cancellationToken).ConfigureAwait(false);
            if (request.CredentialId != credentialId) throw new IntegrationPairingUnavailableException();
            if (request.State == IntegrationPairingState.Issued)
            {
                if (!await ValidateApprovalAsync(request, cancellationToken).ConfigureAwait(false))
                    await RevokeAndClearAsync(request, IntegrationPairingState.Denied, cancellationToken).ConfigureAwait(false);
                else
                {
                    request.State = IntegrationPairingState.Consumed;
                    (await db.IntegrationCredentials.SingleAsync(credential => credential.Id == request.CredentialId, cancellationToken).ConfigureAwait(false)).PairingAcknowledgedAtUtc = clock.GetUtcNow();
                    request.ProtectedDelivery = null;
                    request.DeliveryExpiresAtUtc = null;
                    await SaveAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            return new IntegrationPairingPollResponse(Status(request));
        }, cancellationToken);

    public async Task CleanupAsync(CancellationToken cancellationToken = default)
    {
        var now = clock.GetUtcNow();
        var hashes = await db.IntegrationPairingRequests.AsNoTracking()
            .Where(request => ((request.State == IntegrationPairingState.Pending || request.State == IntegrationPairingState.Approved) && request.ExpiresAtUtc <= now) ||
                (request.State == IntegrationPairingState.Issued && request.DeliveryExpiresAtUtc <= now))
            .OrderBy(request => request.CreatedAtUtc).Take(256).Select(request => request.DeviceCapabilityHash).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        foreach (var hash in hashes)
            await MutateAsync(hash, true, async request => { await ExpireAsync(request, cancellationToken).ConfigureAwait(false); return true; }, cancellationToken).ConfigureAwait(false);
        var cutoff = now.AddDays(-1);
        var expiredIds = await db.IntegrationPairingRequests.AsNoTracking().Where(request => request.CreatedAtUtc < cutoff &&
            request.State != IntegrationPairingState.Pending && request.State != IntegrationPairingState.Approved && request.State != IntegrationPairingState.Issued)
            .OrderBy(request => request.CreatedAtUtc).Take(256).Select(request => request.Id).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        await db.IntegrationPairingRequests.Where(request => expiredIds.Contains(request.Id)).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<T> MutateAsync<T>(string hash, bool device, Func<IntegrationPairingRequest, Task<T>> mutate, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            try
            {
                var request = await db.IntegrationPairingRequests.SingleOrDefaultAsync(candidate => device
                    ? candidate.DeviceCapabilityHash == hash : candidate.UserCodeHash == hash, ct).ConfigureAwait(false)
                    ?? throw new IntegrationPairingUnavailableException();
                var result = await mutate(request).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }
            catch (DbUpdateConcurrencyException) when (attempt < 3)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
        }
    }

    private async Task<bool> ValidateApprovalAsync(IntegrationPairingRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.OwnerPrincipalId) || !await BindingIsCurrentAsync(request, ct).ConfigureAwait(false)) return false;
        var owner = new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", request.OwnerPrincipalId)], "pairing-owner"));
        try
        {
            await validator.ValidateAsync(owner, CreateRequest(request), ct).ConfigureAwait(false);
            if (request.CredentialId is not null)
            {
                var credential = await db.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == request.CredentialId, ct).ConfigureAwait(false);
                if (credential is null || credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= clock.GetUtcNow()) return false;
            }
            return true;
        }
        catch (IntegrationCredentialGrantValidationException) { return false; }
    }

    private async Task<bool> BindingIsCurrentAsync(IntegrationPairingRequest request, CancellationToken ct)
    {
        // Runtime bindings are deployment-owned. A changed origin/resource requires a new approval.
        if (branding is null) return true;
        try
        {
            var origin = ClientEndpointAddress.NormalizePublicOrigin((await branding.GetEffectiveAsync(ct).ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/');
            if (!string.Equals(origin, request.PublicOrigin, StringComparison.Ordinal)) return false;
            if (request.Purpose == IntegrationCredentialPurpose.Api) return request.Resource is null;
            return McpResourceUri.TryNormalize(configuration?["Integration:HttpMcp:PublicResourceUri"] ?? origin + "/mcp", out var resource) &&
                string.Equals(resource, request.Resource, StringComparison.Ordinal);
        }
        catch (ArgumentException) { return false; }
    }

    private IntegrationCredentialCreateRequest CreateRequest(IntegrationPairingRequest request) => new(request.Name, request.Purpose,
        request.CredentialExpiresAtUtc!.Value, Deserialize<IntegrationCredentialGrantRequest>(request.ApprovedGrantsJson!), request.Resource,
        Deserialize<string>(request.ApprovedInstancePermissionsJson!));

    private async Task<IReadOnlyList<IntegrationPairingGrant>> DeliveryGrantsAsync(IntegrationPairingRequest request, CancellationToken ct)
    {
        var grants = Deserialize<IntegrationCredentialGrantRequest>(request.ApprovedGrantsJson!);
        var ids = grants.Select(x => x.TenantId).Distinct().ToArray();
        var names = applicationDb is null ? new Dictionary<int, string>() : await applicationDb.Tenants.AsNoTracking()
            .Where(x => ids.Contains(x.Id)).Select(x => new { x.Id, x.Name }).ToDictionaryAsync(x => x.Id, x => x.Name, ct).ConfigureAwait(false);
        return grants.Select(grant => new IntegrationPairingGrant(grant.TenantId, grant.Permission)
        {
            TenantName = names.GetValueOrDefault(grant.TenantId),
            PermissionLabel = IntegrationPermissionCatalog.Delegable.FirstOrDefault(x => x.Id == grant.Permission)?.Label
        }).ToArray();
    }

    private async Task ExpireAsync(IntegrationPairingRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        if (((request.State is IntegrationPairingState.Pending or IntegrationPairingState.Approved) && request.ExpiresAtUtc <= now) ||
            (request.State == IntegrationPairingState.Issued && request.DeliveryExpiresAtUtc <= now))
            await RevokeAndClearAsync(request, IntegrationPairingState.Expired, ct).ConfigureAwait(false);
    }

    private async Task RevokeAndClearAsync(IntegrationPairingRequest request, IntegrationPairingState state, CancellationToken ct)
    {
        if (request.CredentialId is not null && request.OwnerPrincipalId is not null)
            await credentials.RevokeAsync(request.OwnerPrincipalId, request.CredentialId, request.OwnerPrincipalId, ct).ConfigureAwait(false);
        request.ProtectedDelivery = null;
        request.DeliveryExpiresAtUtc = null;
        request.State = state;
        await SaveAsync(request, ct).ConfigureAwait(false);
    }

    private async Task SaveAsync(IntegrationPairingRequest request, CancellationToken ct)
    {
        request.Revision++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
    private IDataProtector Protector(IntegrationPairingRequest request) => protection.CreateProtector("NetRatel.IntegrationPairing.Delivery.v1", request.Id);
    private static string Status(IntegrationPairingRequest request) => request.State.ToString().ToLowerInvariant();
    private static T[] Deserialize<T>(string json) => JsonSerializer.Deserialize<T[]>(json) ?? [];
    private static string FormatCode(string code) => code[..4] + "-" + code[4..];
    private static string NormalizeCode(string? code)
    {
        var normalized = code?.Trim().Replace("-", "", StringComparison.Ordinal).ToUpperInvariant();
        if (normalized is null || normalized.Length != 8 || normalized.Any(character => !Alphabet.Contains(character)))
            throw new IntegrationPairingUnavailableException();
        return normalized;
    }
    private static string CodeHash(string code) => IntegrationCredentialService.Hash(NormalizeCode(code));
    private static string CapabilityHash(string? capability)
    {
        if (capability is null || capability.Length != 73 || !capability.StartsWith("nrt_pair_", StringComparison.Ordinal) ||
            capability[9..].Any(character => !char.IsAsciiHexDigit(character))) throw new IntegrationPairingUnavailableException();
        return IntegrationCredentialService.Hash(capability);
    }
}
