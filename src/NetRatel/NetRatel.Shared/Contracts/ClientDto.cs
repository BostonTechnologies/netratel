using System;
using System.Collections.Generic;
using NetRatel.Shared;

namespace NetRatel.Shared.Contracts;

public sealed record ClientDto(
    string ClientIdentity,
    string DisplayName,
    string ShortId,
    int? TenantId,
    string? ClientName,
    string? HostName,
    string? IpAddress,
    bool Online,
    bool Enabled,
    DateTimeOffset? LastHeartbeat,
    bool EnableRundeckLogForwarding,
    bool EnableClientLogForwarding,
    IReadOnlyList<string> Logs,
    string? DetectedOs,
    IReadOnlyList<string> AvailableShells,
    string? ClientInfoJson,
    string? AgentVersion,
    long? LastLatencyMs,
    DateTimeOffset? LastLatencyTimestamp,
    ClientEnvironment Environment,
    string? TenantName,
    IReadOnlyList<ClientDiskDto> Disks,
    ClientStatusDto Status,
    NetRatel.Shared.Contracts.Terminals.TerminalTransportKind? TerminalTransportOverride = null,
    NetRatel.Shared.Contracts.Terminals.TerminalTransportKind DefaultTerminalTransport = NetRatel.Shared.Contracts.Terminals.TerminalTransportKind.ApiWebSocket,
    NetRatel.Shared.Contracts.Terminals.TerminalTransportKind RequestedTerminalTransport = NetRatel.Shared.Contracts.Terminals.TerminalTransportKind.ApiWebSocket,
    NetRatel.Shared.Contracts.Terminals.TerminalTransportKind EffectiveTerminalTransport = NetRatel.Shared.Contracts.Terminals.TerminalTransportKind.ApiWebSocket,
    bool TerminalTunnelConnected = false,
    DateTimeOffset? TerminalTunnelLastSeen = null
)
{
    public string Identity => ClientIdentity;
}
