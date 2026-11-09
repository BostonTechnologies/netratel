using NetRatel.Web.Models.Clients;

namespace NetRatel.Web.Services.Clients;

/// <summary>One deduplicated lazy directory read per Monitoring scope, shared by every row.</summary>
public sealed class ClientToolReadiness
{
    private Task<ClientPresentationLoadResult>? _load;
    private int _tenant;
    private DateTimeOffset _expires;
    public async Task<ClientPresentationModel?> ResolveAsync(IServiceProvider services, int tenant, Guid agent)
    {
        if (tenant <= 0 || agent == Guid.Empty) return null;
        if (_tenant != tenant || _load is null || _load.IsCompleted && DateTimeOffset.UtcNow >= _expires)
        {
            _tenant = tenant; _expires = DateTimeOffset.UtcNow.AddSeconds(30);
            _load = services.GetService<ClientPresentationService>()?.GetClientsAsync()
                ?? Task.FromResult(ClientPresentationLoadResult.Unavailable(new(ClientPresentationFailureKind.PermissionDenied)));
        }
        var result = await _load;
        return result.IsAvailable ? result.Clients.SingleOrDefault(client => client.TenantId == tenant && client.AgentId == agent) : null;
    }
}
