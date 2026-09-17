namespace NetRatel.Application.Notifications;

public interface IClientDisplayNameResolver
{
    IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> clientIdentities);
}
