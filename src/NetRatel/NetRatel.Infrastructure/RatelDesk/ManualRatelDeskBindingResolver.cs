using System.Security.Cryptography;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;

namespace NetRatel.Infrastructure.RatelDesk;

public interface IRatelDeskProducerContinuity
{
    Task RequireCurrentAsync(Guid expectedFlowSource, CancellationToken ct);
}
public interface IRatelDeskInstallationIdentityReader
{
    Task<ServiceLinkIdentityDto> GetAsync(CancellationToken ct);
}
// Read adapter only: all deployment application and identity authority stay in the existing store.
public sealed class RatelDeskInstallationIdentityReader(ServiceLinkIdentityStore installation)
    : IRatelDeskInstallationIdentityReader
{
    public Task<ServiceLinkIdentityDto> GetAsync(CancellationToken ct) => installation.GetAsync(ct);
}
public sealed class RatelDeskProducerContinuity(IFlowSourceIdentityResolver flow,
    IRatelDeskInstallationIdentityReader installation) : IRatelDeskProducerContinuity
{
    public async Task RequireCurrentAsync(Guid expectedFlowSource, CancellationToken ct)
    {
        if (expectedFlowSource == Guid.Empty) throw new UnauthorizedAccessException("flow-source-identity-drift");
        // Apply explicit deployment identity before an absent Flow singleton can be seeded.
        var before = await installation.GetAsync(ct);
        if (await flow.EnsureAsync(ct) != expectedFlowSource)
            throw new UnauthorizedAccessException("flow-source-identity-drift");
        var current = await installation.GetAsync(ct); // Recheck after potentially creating Flow identity.
        if (current.InstanceId != before.InstanceId)
            throw new UnauthorizedAccessException("installation-identity-drift");
        // Manual source registration does not require enabling or creating an OAuth service link.
        // An already adopted distinct identity is preserved and requires explicit mapping/reapproval.
        if (current.SourceInstanceId is not null && current.SourceInstanceId != expectedFlowSource.ToString("D"))
            throw new UnauthorizedAccessException("explicit-source-mapping-and-reapproval-required");
    }
}

public sealed record RatelDeskManualProfileObservation(RatelDeskSemanticPeer Peer,
    RatelDeskVerifiedCapability Capability);
public interface IRatelDeskManualProfileProbe
{
    Task<RatelDeskManualProfileObservation> CaptureAsync(RatelDeskConnectorState connector,
        Guid source, string bearer, CancellationToken ct);
}

public sealed class RatelDeskManualProfileProbe(RatelDeskReceiverHttpPipeline http,
    RatelDeskReceiverNetworkPolicy network, TimeProvider time) : IRatelDeskManualProfileProbe
{
    public async Task<RatelDeskManualProfileObservation> CaptureAsync(RatelDeskConnectorState connector,
        Guid source, string bearer, CancellationToken ct)
    {
        var api = network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, connector.Configuration.Origin);
        var reply = await http.ReadAsync(RatelDeskAuthenticationMode.ManualApiBearer, connector.TenantId,
            connector.Id, api, source, bearer, HttpMethod.Get,
            ReceiverWireValidation.Endpoint(api, ReceiverWireValidation.CapabilitiesPath), null, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(reply, "capability");
        // This first authenticated 200 supplies receiver and namespace identity. No header/default/receipt fallback.
        var observation = ReceiverWireValidation.CaptureManual(reply.Body, connector, api, source, time.GetUtcNow());
        var targetBody = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            organizationId = observation.Peer.OrganizationId, customerId = observation.Peer.CustomerId,
            assignedToId = observation.Peer.AssignedToId, categoryIds = observation.Peer.CategoryIds
        }, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var target = await http.ReadAsync(RatelDeskAuthenticationMode.ManualApiBearer, connector.TenantId,
            connector.Id, api, source, bearer, HttpMethod.Post,
            observation.Capability.Endpoints.TargetValidation, targetBody, ct);
        RatelDeskReceiverReadException.RequireJsonSuccess(target, "target-validation");
        ReceiverWireValidation.Target(target.Body, observation.Peer);
        return observation;
    }
}

public sealed class ManualRatelDeskBindingResolver(IRatelDeskConnectorStore connectors,
    IRatelDeskConnectorBindingStore bindings, IRatelDeskConnectorAuthorization authorization,
    IRatelDeskCredentialProtector protector, IRatelDeskProducerContinuity source,
    IRatelDeskManualProfileProbe probe, RatelDeskReceiverNetworkPolicy network) : IRatelDeskOutboundBindingResolver
{
    public async Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
        RatelDeskConnectorAuthentication authentication, Guid flowSource, CancellationToken ct)
    {
        RequireManual(authentication);
        var current = await CurrentAsync(connector, ct);
        await source.RequireCurrentAsync(flowSource, ct);
        var bearer = Unprotect(current);
        var captured = await probe.CaptureAsync(current, flowSource, bearer, ct);
        // Remote awaits cannot authorize a changed local owner, source, mode, target or credential.
        // A concurrent credential replacement triggers a new explicit read-only capture, not mixed responses.
        var after = await CurrentAsync(current, ct);
        await source.RequireCurrentAsync(flowSource, ct);
        if (after.CredentialRevision != current.CredentialRevision || after.ProtectedCredential != current.ProtectedCredential)
            throw new InvalidOperationException("manual-credential-changed-during-capture");
        RequireSemantic(after, captured.Peer);
        return captured.Peer;
    }

    public async Task<string> GetBearerAsync(RatelDeskConnectorState connector,
        RatelDeskSemanticPeer captured, string requiredScope, CancellationToken ct)
    {
        if (!IncidentScopes.Contains(requiredScope, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("incident-operation-scope-required");
        var current = await CurrentAsync(connector, ct);
        await source.RequireCurrentAsync(captured.SourceInstanceId, ct);
        RequireSemantic(current, captured);
        // Credential revision is deliberately not a semantic match. Rotation supplies the
        // newest existing protected rdk_ value for the SAME peer, source namespace and mapping.
        // The receiver checks credential revocation/owner/source/binding on every actual request.
        return Unprotect(current);
    }

    private async Task<RatelDeskConnectorState> CurrentAsync(RatelDeskConnectorState captured, CancellationToken ct)
    {
        var current = await connectors.GetAsync(captured.TenantId, captured.Id, ct);
        if (current is null || current.Id != captured.Id || current.TenantId != captured.TenantId ||
            current.Revision != captured.Revision || current.OwnerPrincipalId != captured.OwnerPrincipalId || !current.Configuration.Enabled ||
            !await authorization.CanExecuteAsync(current.OwnerPrincipalId, null, current.TenantId, ct))
            throw new UnauthorizedAccessException("connector-current-owner-or-revision-denied");
        RequireManual(await bindings.GetAuthenticationAsync(current.TenantId, current.Id, ct));
        _ = network.ValidateApprovedApiBase(RatelDeskAuthenticationMode.ManualApiBearer, current.Configuration.Origin);
        return current;
    }

    private static readonly string[] IncidentScopes =
        ["rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read"];
    private static void RequireManual(RatelDeskConnectorAuthentication authentication)
    {
        if (authentication.Mode != RatelDeskAuthenticationMode.ManualApiBearer || authentication.ManagedLinkId is not null)
            throw new UnauthorizedAccessException("explicit-manual-api-bearer-binding-required");
    }
    private static void RequireSemantic(RatelDeskConnectorState connector, RatelDeskSemanticPeer peer)
    {
        if (peer.Mode != RatelDeskAuthenticationMode.ManualApiBearer || peer.LinkId is not null ||
            peer.LinkRevision is not null || peer.GrantHash is not null || peer.Issuer is not null ||
            peer.Audience is not null || peer.TokenEndpoint is not null || peer.ClientId is not null || peer.DirectionId is not null ||
            peer.LocalTenantId != connector.TenantId || peer.ConnectorId != connector.Id ||
            peer.ApiBaseUrl != RatelDeskApiBase.Canonical(connector.Configuration.Origin) ||
            peer.PeerTenantId != connector.Configuration.OrganizationId ||
            peer.OrganizationId != connector.Configuration.OrganizationId || peer.CustomerId != connector.Configuration.CustomerId ||
            peer.AssignedToId != connector.Configuration.AssignedToId || peer.SourceNamespaceId == Guid.Empty ||
            !Guid.TryParseExact(peer.ReceiverInstanceId, "D", out var receiver) || receiver == Guid.Empty || receiver.ToString("D") != peer.ReceiverInstanceId ||
            !connector.Configuration.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)
                .SequenceEqual(peer.CategoryIds, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("captured-manual-semantic-target-denied");
    }
    private string Unprotect(RatelDeskConnectorState current)
    {
        if (current.ProtectedCredential is null) throw new UnauthorizedAccessException("manual-api-bearer-required");
        string bearer;
        try { bearer = protector.Unprotect(current.TenantId, current.Id, current.ProtectedCredential); }
        catch (CryptographicException) { throw new UnauthorizedAccessException("manual-api-bearer-unavailable"); }
        if (!RatelDeskConnectorService.ValidCredential(bearer))
            throw new UnauthorizedAccessException("manual-api-purpose-bearer-required");
        return bearer;
    }
}
