using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.SystemPairing;
namespace NetRatel.Infrastructure.RatelDesk;
public sealed class PairingRatelDeskBindingResolver(IRatelDeskConnectorStore connectors,
    IRatelDeskConnectorAuthorization authorization, PairingBusinessProfileService profiles,
    IFlowSourceIdentityResolver flowIdentity) : IRatelDeskOutboundBindingResolver
{
    public async Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
        RatelDeskConnectorAuthentication authentication, Guid source, CancellationToken ct)
    {
        await CurrentAsync(connector, ct);
        if (authentication.Mode != RatelDeskAuthenticationMode.PairedSystem || !Guid.TryParseExact(authentication.ManagedLinkId, "D", out var mappingId) || mappingId != connector.Id)
            throw new UnauthorizedAccessException("paired-connection-required");
        var profile = await profiles.ResolveAsync(connector.TenantId, mappingId.ToString("D"), "rateldesk.incidents.create", ct);
        if (source != await flowIdentity.EnsureAsync(ct) || profile.Credential.SourceInstanceId != source.ToString("D") || profile.Mapping.RatelDeskOrganizationId != connector.Configuration.OrganizationId || profile.Mapping.RatelDeskCustomerId != connector.Configuration.CustomerId || profile.Peer.ApiOrigin != connector.Configuration.Origin)
            throw new UnauthorizedAccessException("saved-incident-target-changed");
        if (!Guid.TryParseExact(profile.Peer.ReceiverInstanceId, "D", out var receiver) || receiver == Guid.Empty || receiver.ToString("D") != profile.Peer.ReceiverInstanceId)
            throw new UnauthorizedAccessException("signed-receiver-identity-unavailable");
        return new(RatelDeskAuthenticationMode.PairedSystem, connector.TenantId, connector.Id, mappingId.ToString("D"), profile.Revision,
            profile.AuthorityHash, receiver.ToString("D"), profile.Mapping.RatelDeskOrganizationId, profile.Peer.ApiOrigin,
            profile.Credential.Issuer, profile.Credential.Audience, profile.Credential.TokenEndpoint, profile.Credential.ClientId,
            "netratel_to_rateldesk", source, mappingId, connector.Configuration.OrganizationId, connector.Configuration.CustomerId,
            connector.Configuration.AssignedToId, connector.Configuration.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal).ToArray());
    }
    public async Task<string> GetBearerAsync(RatelDeskConnectorState connector, RatelDeskSemanticPeer captured, string scope, CancellationToken ct)
    {
        var current = await CaptureAsync(connector, connector.Authentication!, captured.SourceInstanceId, ct);
        if (current != captured && !Same(current, captured)) throw new UnauthorizedAccessException("captured-connection-no-longer-authorized");
        var profile = await profiles.ResolveAsync(connector.TenantId, captured.LinkId!, scope, ct);
        var bearer = await profiles.GetAccessTokenAsync(profile, scope, ct);
        var after = await CaptureAsync(connector, connector.Authentication!, captured.SourceInstanceId, ct);
        if (!Same(after, captured)) throw new UnauthorizedAccessException("connection-changed-during-token-acquisition");
        return bearer;
    }
    public static bool Same(RatelDeskSemanticPeer a, RatelDeskSemanticPeer b) => System.Text.Json.JsonSerializer.Serialize(a) == System.Text.Json.JsonSerializer.Serialize(b);
    private async Task CurrentAsync(RatelDeskConnectorState expected, CancellationToken ct)
    {
        var current = await connectors.GetAsync(expected.TenantId, expected.Id, ct);
        if (current is null || !current.Configuration.Enabled || current.Revision != expected.Revision || current.OwnerPrincipalId != expected.OwnerPrincipalId || current.Authentication != expected.Authentication || !await authorization.CanExecuteAsync(current.OwnerPrincipalId, null, current.TenantId, ct))
            throw new UnauthorizedAccessException("connector-current-owner-or-revision-denied");
    }
}
