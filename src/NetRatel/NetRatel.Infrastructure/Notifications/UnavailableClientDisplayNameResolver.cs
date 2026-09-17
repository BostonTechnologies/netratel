using NetRatel.Application.Notifications;

namespace NetRatel.Infrastructure.Notifications;

/// <summary>
/// Prevents legacy Spacetime client identities from being exposed in notifications
/// after the gateway becomes the authoritative client runtime.
/// </summary>
public sealed class UnavailableClientDisplayNameResolver : IClientDisplayNameResolver
{
    public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
