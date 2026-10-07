using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

internal sealed partial class PhysicalIncidentFixture
{
    public async Task<PhysicalRotation> RotateThroughActualOwnerServiceLinkAsync(CancellationToken ct)
    {
        var before = await pair.StatusAsync(pair.NetRatel.Administrator, ct);
        var readiness = await ReadReadinessAsync(ct);
        var oldToken = await pair.TokenAsync(false, "rateldesk.incident-receipts.read", ct);
        var oldRevision = CredentialRevision(oldToken);
        oldToken = "";
        var direction = before.GrantSummary!.Grants.Single(x => x.TargetProduct == "rateldesk").DirectionId;
        var requested = await SendAsync<ServiceLinkAdminStatus>(pair.NetRatel.Administrator, HttpMethod.Post,
            $"/api/v1/admin/service-links/links/{before.LinkId}/rotate", new ServiceLinkAdminAction(DirectionId: direction), ct);
        var rotationId = requested.Rotations.Single(x => x.DirectionId == direction).RotationId;
        ServiceLinkAdminStatus after;
        ServiceLinkRotationSummary rotation;
        // The real background workers execute the durable offer/verify/activation/
        // switch/retirement protocol. Read-only status never seeds or drives a phase.
        while (true)
        {
            await disk.AssertAliveAsync(ct);
            await Task.Delay(TimeSpan.FromSeconds(10), ct); // Preserve sensitive-route admission headroom.
            after = await pair.StatusAsync(pair.NetRatel.Administrator, ct);
            rotation = after.Rotations.Single(x => x.RotationId == rotationId);
            Require(after.LinkId == before.LinkId && after.LinkRevision == before.LinkRevision && after.GrantHash == before.GrantHash,
                "Secret-only rotation changed the actual semantic approved link.");
            if (rotation.CallerSwitchRevision is not null && rotation.SuccessorCredentialRevision > oldRevision &&
                rotation.ActivateDecisionId is not null && rotation.RotationState is "retiring" or "completed") break;
            Require(rotation.RotationState != "aborted", "The actual owner rotation aborted.");
        }
        // Receipt reconciliation can proceed with the verified selected successor
        // during its fixed predecessor overlap. Retirement is separately covered by
        // the normal four real rotation cases; this physical lane proves queued work.
        var successorToken = await pair.TokenAsync(false, "rateldesk.incident-receipts.read", ct);
        var successorRevision = CredentialRevision(successorToken); successorToken = "";
        Require(successorRevision == rotation.SuccessorCredentialRevision && successorRevision > oldRevision,
            "The actual selected successor did not issue the current business authorization.");
        selectedCredentialRevision = successorRevision;
        var current = await ReadReadinessAsync(ct);
        Require(JsonSerializer.Serialize(current.Peer, json) == JsonSerializer.Serialize(readiness.Peer, json) && current.Peer.SourceNamespaceId == sourceNamespace,
            "Secret-only rotation changed the prepared semantic connector or producer namespace.");
        return new(oldRevision, successorRevision, before.LinkRevision, after.LinkRevision,
            readiness.Peer.SourceNamespaceId, current.Peer.SourceNamespaceId);
    }

    public async Task<IPrivateCachedPhysicalAuthorization> CaptureActuallyIssuedBusinessAuthorizationPrivatelyAsync(CancellationToken ct)
    {
        var receiver = await pair.TokenAsync(false, "rateldesk.incident-receipts.read", ct);
        var inbound = await pair.TokenAsync(true, "netratel.orchestration.read", ct);
        // Both cached channels must still be live at every control/denial. Only
        // their conservative common expiry leaves this private token owner.
        var expiry = new[] { TokenExpiry(receiver), TokenExpiry(inbound) }.Min();
        var issued = new CachedPhysicalAuthorization(receiver, inbound, expiry, CredentialRevision(receiver));
        try
        {
            Require(issued.Revision == selectedCredentialRevision && issued.Revision > 1,
                "The cached physical receiver token was not issued for the actual selected successor.");
            Require(await ReadNetRatelWithCachedAsync(issued, false, ct) == HttpStatusCode.OK &&
                await ReadNetRatelWithCachedAsync(issued, true, ct) == HttpStatusCode.OK,
                "The actual current cached inbound token lacked a positive control on both API processes.");
            cachedAuthorization = issued;
            return issued;
        }
        catch { await issued.DisposeAsync(); throw; }
    }

    public async Task<HttpStatusCode> ProtectedReceiverLookupWithCachedAuthorizationAsync(
        IPrivateCachedPhysicalAuthorization handle, PhysicalAction action, CancellationToken ct)
    {
        var cached = (CachedPhysicalAuthorization)handle;
        cached.RequireUnexpired();
        Require(cached.ReceiverToken is not null && action.SourceInstanceId == pair.NetRatel.SourceInstanceId && action.SourceNamespaceId == sourceNamespace,
            "The actual private cached authorization or scoped lookup identity is unavailable.");
        using var request = new HttpRequestMessage(HttpMethod.Get,
            "/api/v1/integrations/netratel/incident-receipts/" + action.ReceiverKey);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cached.ReceiverToken);
        request.Headers.Add("X-NetRatel-Source-Instance", action.SourceInstanceId.ToString("D"));
        using var response = await pair.RatelDesk.Anonymous.SendAsync(request, ct);
        cached.ObserveResponse(response.StatusCode, inboundReplica: null);
        return response.StatusCode;
    }
    public async Task StopOnlyFixtureRatelDeskPeerAsync(CancellationToken ct) => _ = await pair.RatelDesk.StopPhysicalApiAsync(ct);

    public async Task UnlinkThroughActualOwnerAsync(string reason, CancellationToken ct)
    {
        Require(reason == "isolated physical acceptance complete", "The actual physical unlink reason differs.");
        var result = await SendAsync<ServiceLinkAdminStatus>(pair.NetRatel.Administrator, HttpMethod.Post,
            $"/api/v1/admin/service-links/links/{pair.Review.GrantSummary.LinkId}/revoke",
            new ServiceLinkAdminAction(ReasonCode: "physical-acceptance-complete"), ct);
        Require(!result.LocalBusinessSenderEnabled && !result.LocalInboundActive &&
            result.LifecycleState == "revocation_pending", "The real owner unlink did not report immediate local denial and truthful pending peer completion.");
        localUnlinkVerified = true;
    }

    public async Task AssertImmediateLocalSenderAndInboundAuthorityStoppedAsync(CancellationToken ct)
    {
        Require(localUnlinkVerified, "The actual local owner unlink has not completed.");
        var current = await GetAsync<RatelDeskConnectorDto>(pair.NetRatel.Administrator,
            $"/api/v2/tenants/{Tenant}/connectors/rateldesk/{connector:D}", ct);
        Require(!current.AutomaticDeliveryAvailable, "The actual connector still permitted automatic delivery after local unlink.");
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var link = await db.Set<ServiceLinkAttempt>().AsNoTracking().SingleAsync(x => x.LinkId == pair.Review.GrantSummary.LinkId, ct);
        Require(!link.LocalBusinessSenderEnabled && !link.LocalInboundActive && !link.PeerRevocationAcknowledged && link.RevocationId is not null,
            "The actual durable local revocation state does not disable sender and inbound authority.");
        Require(cachedAuthorization is not null, "The immediate unlink proof requires the actual positively controlled cached token.");
        foreach (var replica in new[] { false, true })
        {
            Require(await ReadNetRatelWithCachedAsync(cachedAuthorization!, replica, ct) is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "Immediate local unlink did not deny the same current cached inbound token on both actual API processes.");
            cachedAuthorization!.ObserveImmediateReplicaDenial(replica);
        }
    }

    public async Task RestoreFixtureRatelDeskAndSettleUnlinkAsync(CancellationToken ct)
    {
        await pair.RatelDesk.RestorePhysicalApiAsync(ct);
        while (true)
        {
            await disk.AssertAliveAsync(ct);
            var local = await pair.StatusAsync(pair.NetRatel.Administrator, ct);
            if (local.LifecycleState == "revoked")
            {
                var remote = await pair.StatusAsync(pair.RatelDesk.Administrator, ct);
                Require(!local.LocalBusinessSenderEnabled && !local.LocalInboundActive && !remote.LocalBusinessSenderEnabled &&
                    !remote.LocalInboundActive, "Actual peer unlink completion left business authority enabled.");
                return;
            }
            Require(!local.LocalBusinessSenderEnabled && !local.LocalInboundActive, "Pending peer revocation revived local business authority.");
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
        }
    }

    public async Task AssertNoOldAuthorizationCanIssueOrSendAsync(IPrivateCachedPhysicalAuthorization handle, CancellationToken ct)
    {
        var cached = (CachedPhysicalAuthorization)handle;
        foreach (var replica in new[] { false, true })
        {
            Require(await ReadNetRatelWithCachedAsync(cached, replica, ct) is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden,
                "The same unexpired cached inbound token revived business access on an actual API process after unlink.");
            cached.ObserveSettledReplicaDenial(replica);
        }
        foreach (var issuer in new[] { true, false })
        {
            var peer = issuer ? pair.NetRatel.Proxy : pair.RatelDesk.Proxy;
            var identity = peer.ObservedClients.Values.Single();
            using var response = await (issuer ? pair.NetRatel.Anonymous : pair.RatelDesk.Anonymous).PostAsync("/connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials", ["client_id"] = identity.ClientId,
                    ["client_secret"] = identity.Secret, ["scope"] = issuer ? "netratel.orchestration.read" : "rateldesk.incident-receipts.read" }), ct);
            Require(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest or HttpStatusCode.Forbidden,
                "The revoked current service credential still issued new business authorization.");
        }
        // Include the final issuance probes in the safe latest denial timestamp.
        // They may not let an expired cached token qualify any earlier denial.
        cached.ObserveFinalDenialBoundary();
    }

    private async Task<HttpStatusCode> ReadNetRatelWithCachedAsync(CachedPhysicalAuthorization cached, bool replica, CancellationToken ct)
    {
        cached.RequireUnexpired();
        Require(cached.InboundToken is not null, "The private actual inbound authorization has been disposed.");
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false })
        { BaseAddress = new Uri(replica ? pair.NetRatel.PhysicalReplicaBaseUrl : pair.NetRatel.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/internal/health");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", cached.InboundToken);
        using var response = await client.SendAsync(request, ct);
        cached.ObserveResponse(response.StatusCode, replica);
        return response.StatusCode;
    }
    private static DateTimeOffset TokenExpiry(string token) => new(new JwtSecurityTokenHandler().ReadJwtToken(token).ValidTo, TimeSpan.Zero);
    private static long CredentialRevision(string token) => long.Parse(new JwtSecurityTokenHandler().ReadJwtToken(token)
        .Claims.Single(x => x.Type == ServiceIdentityClaims.CredentialRevision).Value, CultureInfo.InvariantCulture);
    private sealed class CachedPhysicalAuthorization(string receiverToken, string inboundToken, DateTimeOffset expiresAtUtc,
        long revision) : IPrivateCachedPhysicalAuthorization
    {
        public string? ReceiverToken { get; private set; } = receiverToken;
        public string? InboundToken { get; private set; } = inboundToken;
        public DateTimeOffset ExpiresAtUtc => expiresAtUtc;
        public DateTimeOffset? LatestDenialObservedAtUtc { get; private set; }
        private readonly HashSet<bool> positiveReplicas = [];
        private readonly HashSet<bool> immediateDeniedReplicas = [];
        private readonly HashSet<bool> settledDeniedReplicas = [];
        public int PositivelyControlledInboundReplicas => positiveReplicas.Count;
        public int ImmediateDeniedInboundReplicas => immediateDeniedReplicas.Count;
        public int SettledDeniedInboundReplicas => settledDeniedReplicas.Count;
        public long Revision => revision;
        public void RequireUnexpired() => Require(ReceiverToken is not null && InboundToken is not null &&
            ExpiresAtUtc > DateTimeOffset.UtcNow, "Both actual cached authorization channels must remain unexpired.");
        public void ObserveResponse(HttpStatusCode status, bool? inboundReplica)
        {
            RequireUnexpired(); // Recheck after HTTP, including either actual replica.
            if (status == HttpStatusCode.OK && inboundReplica.HasValue) positiveReplicas.Add(inboundReplica.Value);
            if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                LatestDenialObservedAtUtc = DateTimeOffset.UtcNow;
            RequireUnexpired(); // The recorded observation itself must precede both expiries.
        }
        public void ObserveImmediateReplicaDenial(bool replica) { RequireUnexpired(); immediateDeniedReplicas.Add(replica); }
        public void ObserveSettledReplicaDenial(bool replica) { RequireUnexpired(); settledDeniedReplicas.Add(replica); }
        public void ObserveFinalDenialBoundary()
        {
            RequireUnexpired();
            Require(positiveReplicas.Count == 2 && immediateDeniedReplicas.Count == 2 && settledDeniedReplicas.Count == 2,
                "Both cached inbound replica controls and both unlink denial phases must be observed.");
            LatestDenialObservedAtUtc = DateTimeOffset.UtcNow;
            RequireUnexpired();
        }
        public ValueTask DisposeAsync() { ReceiverToken = null; InboundToken = null; return ValueTask.CompletedTask; }
    }
}
