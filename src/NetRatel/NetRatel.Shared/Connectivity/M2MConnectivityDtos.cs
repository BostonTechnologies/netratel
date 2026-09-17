namespace NetRatel.Shared.Connectivity;

public enum TrafficLight { Green, Amber, Red }

public record M2MConnectivitySettingsDto(
    bool Enabled,
    string? RemoteBaseUrl,
    string? RemoteAudience,
    string? RemoteSystemName
);

public record M2MConnectivityTestRequestDto(
    bool IncludeTokenAcquire = true,
    bool IncludeRemoteHealth = true,
    M2MConnectivitySettingsDto? SettingsOverride = null
);

public record M2MConnectivityProbeResultDto(
    string ProbeName,
    string Target,
    TrafficLight Status,
    int? HttpStatus,
    long? LatencyMs,
    string Message,
    string? RemoteSystemName,
    DateTimeOffset CheckedAtUtc
);

public record M2MConnectivityTestResultDto(
    IReadOnlyList<M2MConnectivityProbeResultDto> Probes
);

public record NetRatelOrchestrationSettingsDto(
    string? BaseUrl,
    string? Authority,
    string? Audience,
    string? TokenEndpoint,
    string? Scope
);
