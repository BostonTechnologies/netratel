using System.Security.Claims;
using System.Text.Json;
using NetRatel.Shared.ServiceLinks;
using static NetRatel.Infrastructure.ServiceLinks.ServiceLinkValidation;

namespace NetRatel.Infrastructure.ServiceLinks;

public sealed partial class ServiceLinkCoordinator
{
    public async Task<ServiceLinkTestResult> TestAsync(string linkId, ClaimsPrincipal actor, CancellationToken ct)
    {
        var a = await Link(linkId, ct); await Authorize(actor, a.LocalTenantId, ct);
        var scope = OutboundGrant(a).Scopes.Contains("rateldesk.orchestration.callback", StringComparer.Ordinal)
            ? "rateldesk.orchestration.callback" : "rateldesk.incident-receipts.read";
        var profile = await providers.ResolveAsync(int.Parse(a.LocalTenantId, System.Globalization.CultureInfo.InvariantCulture), linkId, scope, ct);
        // Fresh actual token and inert GETs. The guided test never creates a receipt, incident, job or Flow.
        var token = await transport.TokenAsync(profile.Credential, scope, ct);
        using var observation = await transport.GetAsync<JsonDocument>(Endpoint(profile.Peer.ApiBaseUrl,
            scope == "rateldesk.orchestration.callback" ? "/api/v1/orchestration/provider/m2m/ping" : "/api/v1/integrations/netratel/capabilities"),
            ct, token, scope == "rateldesk.incident-receipts.read" ? profile.SourceInstanceId : null);
        var control = await ProtocolToken(a, ServiceLinkContract.ControlScope, "status", null, profile.Credential, ct);
        using var peerStatus = await transport.GetAsync<JsonDocument>(Endpoint(profile.Peer.ServiceLinkEndpoint, "/links/" + linkId + "/status"), ct, control);
        ValidatePeerResult(a, peerStatus.RootElement);
        // This is the peer's acknowledgement backed by the stored reciprocal verification receipts,
        // not a claim that the local server possesses the peer's hash-only inbound secret.
        var peerAcknowledged = a.InitiatorVerificationReceiptId is not null && a.ResponderVerificationReceiptId is not null &&
            (Boolean(peerStatus.RootElement, "local_business_sender_enabled") || IncidentOnlyGrant(InboundGrant(a)) &&
                Boolean(peerStatus.RootElement, "local_inbound_active") && String(peerStatus.RootElement, "lifecycle_state") == "active") && String(peerStatus.RootElement, "commit_id") == a.CommitId;
        if (!peerAcknowledged) return new(true, false, false, "peer-acknowledgement-pending");
        if (connectorSetup is null) return new(true, true, false, "connector-readiness-required");
        var completion = await connectorSetup.CompleteAsync(int.Parse(a.LocalTenantId,
            System.Globalization.CultureInfo.InvariantCulture), linkId, actor, ct);
        return new(true, true, completion.Ready, completion.Ready ? null : completion.Code);
    }
}
