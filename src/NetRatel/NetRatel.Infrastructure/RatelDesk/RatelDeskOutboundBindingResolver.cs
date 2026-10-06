using NetRatel.Application.RatelDesk;

namespace NetRatel.Infrastructure.RatelDesk;

public sealed class RatelDeskOutboundBindingResolver(IRatelDeskConnectorBindingStore bindings,
    ManualRatelDeskBindingResolver manual, ManagedRatelDeskBindingResolver managed)
    : IRatelDeskOutboundBindingResolver
{
    public async Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector,
        RatelDeskConnectorAuthentication authentication, Guid source, CancellationToken ct)
    {
        var current = await bindings.GetAuthenticationAsync(connector.TenantId, connector.Id, ct);
        if (current != authentication) throw new UnauthorizedAccessException("connector-authentication-reference-changed");
        return await Select(current).CaptureAsync(connector, current, source, ct);
    }

    public async Task<string> GetBearerAsync(RatelDeskConnectorState connector,
        RatelDeskSemanticPeer captured, string requiredScope, CancellationToken ct)
    {
        var current = await bindings.GetAuthenticationAsync(connector.TenantId, connector.Id, ct);
        if (current.Mode != captured.Mode || current.ManagedLinkId != captured.LinkId)
            throw new UnauthorizedAccessException("captured-authentication-reference-no-longer-authorized");
        return await Select(current).GetBearerAsync(connector, captured, requiredScope, ct);
    }

    private IRatelDeskOutboundBindingResolver Select(RatelDeskConnectorAuthentication current) => current switch
    {
        { Mode: RatelDeskAuthenticationMode.ManualApiBearer, ManagedLinkId: null } => manual,
        { Mode: RatelDeskAuthenticationMode.ManagedServiceLink, ManagedLinkId: { Length: > 0 } } => managed,
        _ => throw new UnauthorizedAccessException("invalid-connector-authentication-reference")
    };
}
