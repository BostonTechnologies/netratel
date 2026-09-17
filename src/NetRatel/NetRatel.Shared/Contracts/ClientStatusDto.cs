namespace NetRatel.Shared.Contracts;

public sealed record ClientStatusDto(
    bool Online,
    bool Enabled,
    bool WifiConnected,
    bool LogStreamingEnabled,
    bool RundeckLogStreamingEnabled
);