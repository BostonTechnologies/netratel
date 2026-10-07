using System.Globalization;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.ServiceLinks;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class ManagedRatelDeskBindingResolver(ServiceLinkProfileService profiles,
    ServiceLinkIdentityStore installation, IFlowSourceIdentityResolver flowIdentity,
    IRatelDeskConnectorAuthorization authorization, IRatelDeskConnectorStore connectors) : IRatelDeskOutboundBindingResolver
{
    public async Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
        RatelDeskConnectorAuthentication authentication, Guid source, CancellationToken ct)
    {
        if (authentication.Mode != RatelDeskAuthenticationMode.ManagedServiceLink ||
            string.IsNullOrEmpty(authentication.ManagedLinkId))
            throw new InvalidOperationException("explicit-manual-binding-resolver-required");
        await CurrentOwner(connector, ct);
        if (source == Guid.Empty || await flowIdentity.EnsureAsync(ct) != source)
            throw new InvalidOperationException("flow-source-identity-drift");
        var installed = await installation.GetAsync(ct);
        if (installed.SourceInstanceId != source.ToString("D"))
            throw new InvalidOperationException("flow-source-adoption-required");
        var current = await profiles.ResolveAsync(connector.TenantId, authentication.ManagedLinkId,
            "rateldesk.incidents.create", ct);
        RequireIncidentScopes(current);
        var grant = current.Grant;
        var constraints = grant.ResourceConstraints;
        if (current.Peer.ApiBaseUrl != connector.Configuration.Origin || current.SourceInstanceId != source.ToString("D") ||
            current.PeerTenantId != connector.Configuration.OrganizationId ||
            constraints.OrganizationId != connector.Configuration.OrganizationId ||
            !constraints.CustomerIds.Contains(connector.Configuration.CustomerId, StringComparer.Ordinal) ||
            constraints.CustomerIds.Length != 1)
            throw new UnauthorizedAccessException("approved-incident-target-mismatch");
        return new(RatelDeskAuthenticationMode.ManagedServiceLink, connector.TenantId, connector.Id,
            current.LinkId, current.LinkRevision, current.GrantHash, current.PeerInstanceId,
            current.PeerTenantId, current.Peer.ApiBaseUrl, current.Credential.Issuer,
            current.Credential.Audience, current.Credential.TokenEndpoint, current.Credential.ClientId,
            grant.DirectionId, source, Guid.ParseExact(current.SourceNamespaceId, "D"),
            connector.Configuration.OrganizationId, connector.Configuration.CustomerId,
            connector.Configuration.AssignedToId,
            connector.Configuration.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal).ToArray());
    }

    public Task<string> GetBearerAsync(RatelDeskConnectorState currentConnector,
        RatelDeskSemanticPeer captured, string scope, CancellationToken ct) =>
        GetBearerWithRetryAsync(currentConnector, captured, scope, remainingRefreshes: 1, ct);

    private async Task<string> GetBearerWithRetryAsync(RatelDeskConnectorState currentConnector,
        RatelDeskSemanticPeer captured, string scope, int remainingRefreshes, CancellationToken ct)
    {
        if (captured.Mode != RatelDeskAuthenticationMode.ManagedServiceLink || captured.LinkId is null)
            throw new InvalidOperationException("explicit-manual-binding-resolver-required");
        currentConnector = await CurrentConnectorAsync(currentConnector, captured, ct);
        var current = await profiles.ResolveAsync(captured.LocalTenantId, captured.LinkId, scope, ct);
        RequireCapturedPeer(current, captured);
        // Fresh profile, including its current credential revision, is passed to the existing
        // token cache. If rotation races this call, reacquire same target and retry BEFORE send.
        try
        {
            var bearer = await profiles.GetAccessTokenAsync(current, scope, ct);
            // Token acquisition is remote I/O. All local authority observations made
            // before it must be repeated against durable current state after it.
            currentConnector = await CurrentConnectorAsync(currentConnector, captured, ct);
            var afterToken = await profiles.ResolveAsync(captured.LocalTenantId, captured.LinkId, scope, ct);
            RequireCapturedPeer(afterToken, captured);
            ServiceLinkProfileService.RequireCurrentSnapshot(current, afterToken);
            return bearer;
        }
        catch (ServiceLinkProtocolException error) when (remainingRefreshes > 0 &&
            error.StatusCode == 409 && error.Code == "profile-revision-conflict")
        {
            // Exactly one bounded fresh retry, before business send. Every semantic,
            // source, owner and connector check above runs again for the same capture.
            return await GetBearerWithRetryAsync(currentConnector, captured, scope, remainingRefreshes - 1, ct);
        }
    }

    private async Task<RatelDeskConnectorState> CurrentConnectorAsync(RatelDeskConnectorState expected,
        RatelDeskSemanticPeer captured, CancellationToken ct)
    {
        var current = await connectors.GetAsync(expected.TenantId, expected.Id, ct);
        if (current is null || current.Revision != expected.Revision || current.OwnerPrincipalId != expected.OwnerPrincipalId)
            throw new UnauthorizedAccessException("captured-connector-revision-no-longer-authorized");
        if (current.Authentication?.Mode != RatelDeskAuthenticationMode.ManagedServiceLink ||
            current.Authentication?.ManagedLinkId != captured.LinkId)
            throw new UnauthorizedAccessException("captured-connector-binding-no-longer-authorized");
        await CurrentOwner(current, ct);
        if (current.TenantId != captured.LocalTenantId || current.Id != captured.ConnectorId ||
            current.Configuration.Origin != captured.ApiBaseUrl ||
            current.Configuration.OrganizationId != captured.OrganizationId ||
            current.Configuration.CustomerId != captured.CustomerId ||
            current.Configuration.AssignedToId != captured.AssignedToId ||
            !current.Configuration.CategoryIds.Select(x => x.ToString("D")).Order(StringComparer.Ordinal)
                .SequenceEqual(captured.CategoryIds, StringComparer.Ordinal) ||
            await flowIdentity.EnsureAsync(ct) != captured.SourceInstanceId ||
            (await installation.GetAsync(ct)).SourceInstanceId != captured.SourceInstanceId.ToString("D"))
            throw new UnauthorizedAccessException("captured-flow-source-no-longer-authorized");
        return current;
    }

    private static void RequireCapturedPeer(ServiceLinkResolvedProfile current, RatelDeskSemanticPeer captured)
    {
        RequireIncidentScopes(current);
        // CredentialRevision/ProfileRevision deliberately excluded: rotation must not retarget work.
        if (current.LinkRevision != captured.LinkRevision || current.GrantHash != captured.GrantHash ||
            current.PeerInstanceId != captured.ReceiverInstanceId || current.PeerTenantId != captured.PeerTenantId ||
            current.Peer.ApiBaseUrl != captured.ApiBaseUrl || current.Credential.Issuer != captured.Issuer ||
            current.Credential.Audience != captured.Audience || current.Credential.TokenEndpoint != captured.TokenEndpoint ||
            current.Credential.ClientId != captured.ClientId || current.Grant.DirectionId != captured.DirectionId ||
            current.SourceInstanceId != captured.SourceInstanceId.ToString("D") ||
            current.SourceNamespaceId != captured.SourceNamespaceId.ToString("D") ||
            current.Grant.ResourceConstraints.OrganizationId != captured.OrganizationId ||
            current.Grant.ResourceConstraints.CustomerIds.Length != 1 ||
            !current.Grant.ResourceConstraints.CustomerIds.Contains(captured.CustomerId, StringComparer.Ordinal))
            throw new UnauthorizedAccessException("captured-semantic-target-no-longer-authorized");
    }

    private async Task CurrentOwner(RatelDeskConnectorState connector, CancellationToken ct)
    {
        if (!connector.Configuration.Enabled || !await authorization.CanExecuteAsync(
                connector.OwnerPrincipalId, null, connector.TenantId, ct))
            throw new UnauthorizedAccessException("connector-disabled-or-owner-denied");
    }
    private static void RequireIncidentScopes(ServiceLinkResolvedProfile profile)
    {
        foreach (var scope in new[] { "rateldesk.incidents.create", "rateldesk.incident-receipts.read", "rateldesk.incident-targets.read" })
            if (!profile.Grant.Scopes.Contains(scope, StringComparer.Ordinal))
                throw new UnauthorizedAccessException("complete-incident-receiver-grant-required");
    }
}
