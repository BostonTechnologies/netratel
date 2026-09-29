using NetRatel.Application.Notifications;

namespace NetRatel.Infrastructure.Notifications;

/// <summary>
/// Keeps opaque historical 64-hex client identities out of user-facing
/// notifications. Current Agent display names are resolved only from the
/// tenant-scoped directory; they are not interchangeable with these tokens.
/// </summary>
public sealed class LegacyClientIdentityRedactionResolver : IClientDisplayNameResolver
{
    public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities) =>
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
