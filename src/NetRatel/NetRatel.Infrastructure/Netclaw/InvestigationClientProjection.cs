using System.Text.Json;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.Infrastructure.Netclaw;

public static class InvestigationClientProjection
{
    public static InvestigationClientDto Create(Agent agent, string tenantName, ClientConnectionOwnerRecord? owner, DateTimeOffset now)
    {
        var identity = ClientDirectoryIdentity.Parse(agent.DeviceInfoJson);
        var displayName = !string.IsNullOrWhiteSpace(agent.FriendlyName) ? agent.FriendlyName.Trim()
            : !string.IsNullOrWhiteSpace(agent.Name) && !Guid.TryParse(agent.Name, out _) ? agent.Name.Trim() : null;
        var observed = owner?.LastReceivedAtUtc ?? agent.LastSeenUtc;
        var committed = owner is { ConnectionEpoch: > 0, AcceptanceGuardAtUtc: not null, LastReceivedAtUtc: not null };
        var online = committed && agent.IsEnabled && owner!.Active && owner.PresenceExpiresAtUtc > now && owner.AuthenticationExpiresAtUtc > now;
        string? version = null;
        if (owner is not null)
        {
            try { version = JsonSerializer.Deserialize<PresenceMetadata>(owner.MetadataJson)?.AgentVersion; }
            catch (JsonException) { /* Missing cached version remains unknown. */ }
        }
        return new(agent.Id, identity.HostName ?? "Unknown hostname", agent.FriendlyName, agent.Tags,
            agent.LastSeenUtc, identity.OperatingSystem, identity.Architecture, identity.PrimaryAddress,
            identity.Addresses, committed ? online ? "online" : "offline" : "unknown", version, observed,
            identity.HostName is not null, tenantName, displayName);
    }

    public static bool Matches(InvestigationClientDto client, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        var term = search.Trim();
        bool Has(string? value) => value?.Contains(term, StringComparison.OrdinalIgnoreCase) == true;
        return Has(client.HostName) || Has(client.FriendlyName) || Has(client.DisplayName) || Has(client.PrimaryIpAddress) ||
            client.IpAddresses?.Any(Has) == true || client.Tags.Any(Has) || Has(client.AgentId.ToString("D"));
    }
}
