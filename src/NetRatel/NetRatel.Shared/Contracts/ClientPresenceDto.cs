namespace NetRatel.Shared.Contracts;

/// <summary>
/// Presence-only gateway client record. Its identity is an enrolled Agent ID,
/// not a legacy SpacetimeDB client identity, so legacy remote actions cannot
/// be applied to it.
/// </summary>
public sealed record ClientPresenceDto(
    string PresenceId,
    int TenantId,
    Guid AgentId,
    string DisplayName,
    string? HostName,
    string? OperatingSystem,
    string? Architecture,
    bool Online,
    bool IsEnabled,
    DateTimeOffset? LastReceivedAtUtc,
    string? AgentVersion,
    IReadOnlyList<string> Capabilities,
    string Source,
    string Authority,
    bool IsAuthoritative,
    long Revision,
    GatewayTerminalCapabilityDto? Terminal = null,
    string? TenantName = null,
    GatewayFileCapabilityDto? File = null);

public sealed record GatewayTerminalCapabilityDto(
    bool Supported,
    IReadOnlyList<string> AvailableShells,
    bool TransportReady,
    string? ReadinessReason,
    DateTimeOffset? ObservedAtUtc);

/// <summary>
/// Bounded readiness evidence for the agent file gateway. No path, request,
/// stream, connection identifier, token, or file content is exposed.
/// </summary>
public sealed record GatewayFileCapabilityDto(
    bool Configured,
    bool Advertised,
    bool SessionActive,
    bool FenceMatchesPresence,
    string? ClientVersion,
    IReadOnlyList<string> NegotiatedCapabilities,
    DateTimeOffset? PresenceObservedAtUtc,
    DateTimeOffset? SessionRegisteredAtUtc,
    string? ReadinessReason);

public sealed record ClientPresenceListDto(
    string Mode,
    long Revision,
    IReadOnlyList<ClientPresenceDto> Items);
