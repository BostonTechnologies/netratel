namespace NetRatel.Shared.Contracts.RemoteSupport;

/// <summary>Agent-ID keyed response for the gateway remote-support canary.</summary>
public sealed record GatewayRemoteSupportOpenResponse(
    string SessionId,
    int TenantId,
    Guid AgentId,
    string Authority,
    string State,
    DateTimeOffset CreatedAtUtc);

/// <summary>Bounded, transient signalling event for the gateway browser adapter.</summary>
public sealed record GatewayRemoteSupportSignalDto(
    string SessionId,
    string SignalType,
    string PayloadJson,
    ulong Sequence,
    string Direction,
    DateTimeOffset ReceivedAtUtc);
