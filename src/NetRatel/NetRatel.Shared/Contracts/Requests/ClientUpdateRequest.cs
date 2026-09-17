using NetRatel.Shared;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.Shared.Contracts.Requests;

public sealed record ClientUpdateRequest(
    string? ClientName,
    int? TenantId,
    ClientEnvironment? Environment,
    bool? EnableClientLogForwarding,
    bool? EnableRundeckLogForwarding,
    bool? Enabled,
    TerminalTransportKind? TerminalTransportOverride = null,
    bool? ClearTerminalTransportOverride = null
);
