using NetRatel.Application.Presence;

namespace NetRatel.Akka.Presence;

/// <summary>
/// Defines the stable entity identifier shared by the Phase 1 local router and
/// the future cluster-sharding adapter.
/// </summary>
public sealed class ClientPresenceMessageExtractor
{
    public string EntityId(IClientPresenceMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!message.Client.IsValid)
        {
            throw new ArgumentException(
                "A positive tenant ID and non-empty agent ID are required.",
                nameof(message));
        }

        return message.Client.EntityId;
    }
}
