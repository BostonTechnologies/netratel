using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.ServiceLinks;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed record ServiceLinkProfileStatus(bool Enabled, bool ManagedSenderEnabled, bool ManagedByDeployment, bool HasClientSecret,
    string? LinkId, long LinkRevision, int Revision, string? LocalTenantId, string? PeerInstanceId, string? PeerTenantId);

/// <summary>Server-only complete snapshot. Never serialize its credential into an API response or telemetry.</summary>
public sealed record ServiceLinkResolvedProfile(string LinkId, long LinkRevision, string GrantHash, int LocalTenantId,
    string PeerInstanceId, string PeerTenantId, ServiceLinkMetadata Peer, ServiceLinkGrant Grant,
    string SourceInstanceId, string SourceNamespaceId, int ProfileRevision, long CredentialRevision, ServiceDirectionalCredential Credential);
public sealed record ServiceLinkCallbackAuthorization(string CallbackUrl, string BearerToken);

/// <summary>The durable approved attempt is the sole managed outbound profile, shared by callbacks and the Flow adapter.</summary>
public sealed class ServiceLinkProfileService(OrchestratorDbContext db, IServicePublicSettingsResolver publicSettings,
    IDataProtectionProvider protection, ServiceLinkTransport transport, IMemoryCache cache, TimeProvider clock)
{
    internal async Task<ServiceLinkProfileStatus> GetAsync(string? linkId, CancellationToken ct)
    {
        var a = linkId is null ? null : await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == linkId, ct);
        return new(a?.LocalBusinessSenderEnabled ?? false, a?.LocalBusinessSenderEnabled ?? false, false, a?.ProtectedOutboundCredential is not null,
            a?.LinkId, a?.LinkRevision ?? 0, a?.OutboundProfileRevision ?? 0, a?.LocalTenantId, a?.PeerInstanceId, a?.PeerTenantId);
    }

    internal Task<int> StageAsync(ServiceLinkAttempt a, ServiceDirectionalCredential credential, CancellationToken ct)
    {
        Require(a.GrantSummaryJson is not null && a.LinkId is not null && a.Decision != "abort", "profile-ownership-conflict", "A final approved link must own the outbound profile.", 409);
        var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(a.GrantSummaryJson!);
        var grant = summary.Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator));
        var peer = a.Role == "initiator" ? summary.ResponderEndpointSnapshot : summary.InitiatorEndpointSnapshot;
        Credential(credential, grant, peer);
        a.LocalBusinessSenderEnabled = false; a.OutboundProfileRevision = checked((a.OutboundProfileRevision ?? 0) + 1);
        return Task.FromResult(a.OutboundProfileRevision.Value);
    }

    internal Task<int> SetSenderAsync(ServiceLinkAttempt a, int expectedRevision, bool enabled, CancellationToken ct)
    {
        Require(a.OutboundProfileRevision == expectedRevision, "profile-revision-conflict", "Reload the managed profile revision.", 409);
        Require(!enabled || a.Decision == "commit" && a.LocalInboundActive && a.PeerActiveAcknowledged && a.LocalPreparedAcknowledged && a.PeerPreparedAcknowledged,
            "activation-not-proven", "The sender requires the shared commit and active acknowledgement.", 409);
        a.LocalBusinessSenderEnabled = enabled; a.OutboundProfileRevision = checked(expectedRevision + 1);
        return Task.FromResult(a.OutboundProfileRevision.Value);
    }

    public async Task<ServiceLinkResolvedProfile> ResolveAsync(int localTenantId, string linkId, string requiredScope, CancellationToken ct)
    {
        var a = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleOrDefaultAsync(x => x.LinkId == linkId && x.LocalTenantId == localTenantId.ToString(System.Globalization.CultureInfo.InvariantCulture), ct);
        Require(a is not null && a.LifecycleState == "active" && a.Decision == "commit" && a.LocalBusinessSenderEnabled && a.PeerActiveAcknowledged && a.ProtectedOutboundCredential is not null &&
            await ServiceLinkAuthority.InboundUsableAsync(db, a, clock, await publicSettings.ResolveAsync(ct), ct), "grant-unavailable", "The exact linked service profile has no current business authority.", 403);
        var summary = ServiceLinkCanonicalJson.Deserialize<ServiceLinkGrantSummary>(a!.GrantSummaryJson!);
        var grant = summary.Grants.Single(x => x.DirectionId == (a.Role == "initiator" ? ServiceLinkContract.InitiatorToResponder : ServiceLinkContract.ResponderToInitiator));
        Require(grant.TargetProduct == "rateldesk" && grant.Scopes.Contains(requiredScope, StringComparer.Ordinal), "scope-not-authorized", "The approved peer grant does not authorize this operation.", 403);
        var peer = a.Role == "initiator" ? summary.ResponderEndpointSnapshot : summary.InitiatorEndpointSnapshot;
        var credential = ServiceLinkCanonicalJson.Deserialize<ServiceDirectionalCredential>(Protector(a).Unprotect(a.ProtectedOutboundCredential!));
        Credential(credential, grant, peer);
        return new(a.LinkId!, a.LinkRevision, a.GrantHash!, localTenantId, a.PeerInstanceId, a.PeerTenantId!, peer, grant,
            grant.SourceInstanceId!, grant.SourceNamespaceId!, a.OutboundProfileRevision!.Value, credential.CredentialRevision, credential);
    }

    public async Task<string> GetAccessTokenAsync(ServiceLinkResolvedProfile profile, string scope, CancellationToken ct)
    {
        var current = await ResolveAsync(profile.LocalTenantId, profile.LinkId, scope, ct);
        RequireCurrentSnapshot(profile, current);
        var key = "netratel-service/" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", new object?[] { current.LinkId, current.LinkRevision, current.GrantHash,
            current.LocalTenantId, current.PeerInstanceId, current.PeerTenantId, current.Peer.ApiBaseUrl, current.Credential.Issuer, current.Credential.Audience,
            current.Credential.ClientId, scope, current.ProfileRevision, current.CredentialRevision, current.SourceInstanceId, current.SourceNamespaceId }))));
        if (cache.TryGetValue<CachedAccessToken>(key, out var cached) && cached is not null)
        {
            if (cached.ReuseUntilUtc > clock.GetUtcNow()) return cached.Token;
            cache.Remove(key);
        }
        // Start before the HTTP operation: peer issuance and response transit
        // cannot extend our conservative reuse deadline past the actual lifetime.
        var requestStartedAt = clock.GetUtcNow();
        var issued = await transport.AcquireTokenAsync(current.Credential, scope, ct);
        // A completed disable, unlink or grant change during token HTTP must deny
        // this token before it enters the cache or escapes to a business sender.
        RequireCurrentSnapshot(current, await ResolveAsync(current.LocalTenantId, current.LinkId, scope, ct));
        var reuseUntil = requestStartedAt.AddSeconds(Math.Min(45, issued.ExpiresIn - 5));
        var remaining = reuseUntil - clock.GetUtcNow();
        if (remaining > TimeSpan.Zero)
            cache.Set(key, new CachedAccessToken(issued.AccessToken, reuseUntil), remaining);
        return issued.AccessToken;
    }

    internal static void RequireCurrentSnapshot(ServiceLinkResolvedProfile captured, ServiceLinkResolvedProfile current)
    {
        Require(current.LinkId == captured.LinkId && current.LocalTenantId == captured.LocalTenantId &&
            current.LinkRevision == captured.LinkRevision && current.GrantHash == captured.GrantHash &&
            current.PeerInstanceId == captured.PeerInstanceId && current.PeerTenantId == captured.PeerTenantId &&
            current.SourceInstanceId == captured.SourceInstanceId && current.SourceNamespaceId == captured.SourceNamespaceId &&
            ServiceLinkCanonicalJson.HashObject(current.Peer) == ServiceLinkCanonicalJson.HashObject(captured.Peer) &&
            ServiceLinkCanonicalJson.HashObject(current.Grant) == ServiceLinkCanonicalJson.HashObject(captured.Grant) &&
            ServiceLinkCanonicalJson.HashObject(current.Credential with { ClientSecret = "", CredentialRevision = 1, Scopes = Set(current.Credential.Scopes) }) ==
                ServiceLinkCanonicalJson.HashObject(captured.Credential with { ClientSecret = "", CredentialRevision = 1, Scopes = Set(captured.Credential.Scopes) }),
            "profile-semantic-conflict", "The current approved profile differs from the captured semantic target.", 403);
        if (current.ProfileRevision != captured.ProfileRevision || current.CredentialRevision != captured.CredentialRevision)
        {
            // Only a completed credential successor for this unchanged approval can
            // use the caller's existing single refresh. Sender state changes cannot.
            Require(current.ProfileRevision > captured.ProfileRevision && current.CredentialRevision > captured.CredentialRevision,
                "profile-state-conflict", "The current profile change is not a credential successor.", 403);
            throw new ServiceLinkProtocolException(409, "profile-revision-conflict", "Resolve the current credential for the same captured semantic target.");
        }
        Require(current.Credential.ClientSecret == captured.Credential.ClientSecret,
            "profile-state-conflict", "The current credential no longer matches its captured revision.", 403);
    }

    public async Task<ServiceLinkCallbackAuthorization> GetCallbackAuthorizationAsync(string linkId, int tenantId, string peerInstanceId,
        string peerTenantId, long linkRevision, string grantHash, string callbackUrl, string requestId, string taskId, CancellationToken ct)
    {
        var profile = await ResolveAsync(tenantId, linkId, "rateldesk.orchestration.callback", ct);
        var constraints = profile.Grant.ResourceConstraints;
        Require(profile.PeerInstanceId == peerInstanceId && profile.PeerTenantId == peerTenantId && profile.LinkRevision == linkRevision && profile.GrantHash == grantHash &&
            callbackUrl == Endpoint(profile.Peer.ApiBaseUrl, "/api/v1/orchestration/provider/callback") && !string.IsNullOrEmpty(requestId) && !string.IsNullOrEmpty(taskId) &&
            (constraints.RequestIds.Length == 0 || constraints.RequestIds.Contains(requestId, StringComparer.Ordinal)) &&
            (constraints.TaskIds.Length == 0 || constraints.TaskIds.Contains(taskId, StringComparer.Ordinal)), "callback-not-authorized", "The task callback differs from its recorded approved link and correlation.", 403);
        return new(callbackUrl, await GetAccessTokenAsync(profile, "rateldesk.orchestration.callback", ct));
    }

    private sealed record CachedAccessToken(string Token, DateTimeOffset ReuseUntilUtc);

    private IDataProtector Protector(ServiceLinkAttempt a) => protection.CreateProtector("NetRatel.ServiceLink.v1", a.AttemptId, a.PeerInstanceId,
        "outbound-credential", a.LocalTenantId + "/" + a.LinkId + "/" + a.GrantHash + "/" + a.LinkRevision);
}
