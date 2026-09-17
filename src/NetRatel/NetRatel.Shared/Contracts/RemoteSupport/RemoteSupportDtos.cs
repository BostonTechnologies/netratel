namespace NetRatel.Shared.Contracts.RemoteSupport;

public sealed record OpenRemoteSupportRequest(
    string? Mode = "webrtc",
    string? TargetMode = "auto",
    int? TargetWindowsSessionId = null,
    string? TargetUserSidHash = null,
    string? TargetUsername = null,
    string? RequestedProvider = null,
    ulong? InventorySequence = null,
    IReadOnlyList<RemoteSupportIceServerDto>? IceServers = null,
    RemoteSupportMediaProfileDto? MediaProfile = null);

public abstract record RemoteSupportTarget(string Mode);

public sealed record ConsoleLoginTarget() : RemoteSupportTarget("login");

public sealed record InteractiveSessionTarget(
    int WindowsSessionId,
    string UserSidHash,
    ulong? InventorySequence) : RemoteSupportTarget("assist_user");

public sealed record LegacyAutomaticTarget() : RemoteSupportTarget("auto");

public static class RemoteSupportTargetResolver
{
    public static bool TryResolve(
        OpenRemoteSupportRequest request,
        out RemoteSupportTarget? target,
        out string? error)
    {
        var mode = string.IsNullOrWhiteSpace(request.TargetMode)
            ? "auto"
            : request.TargetMode.Trim().ToLowerInvariant();
        switch (mode)
        {
            case "login":
            case "console_login":
                if (request.TargetWindowsSessionId.HasValue ||
                    !string.IsNullOrWhiteSpace(request.TargetUserSidHash))
                {
                    target = null;
                    error = "Console login requests cannot include a user session target.";
                    return false;
                }

                target = new ConsoleLoginTarget();
                error = null;
                return true;

            case "assist_user":
            case "active_user":
                if (!request.TargetWindowsSessionId.HasValue || request.TargetWindowsSessionId.Value <= 0)
                {
                    target = null;
                    error = "Assist user requests require a positive Windows session id.";
                    return false;
                }

                if (string.IsNullOrWhiteSpace(request.TargetUserSidHash))
                {
                    target = null;
                    error = "Assist user requests require a user identity hash.";
                    return false;
                }

                target = new InteractiveSessionTarget(
                    request.TargetWindowsSessionId.Value,
                    request.TargetUserSidHash.Trim(),
                    request.InventorySequence);
                error = null;
                return true;

            case "auto":
                target = new LegacyAutomaticTarget();
                error = null;
                return true;

            default:
                target = null;
                error = $"Unsupported Remote Support target mode '{request.TargetMode}'.";
                return false;
        }
    }
}

public sealed record RemoteSupportWindowsSessionDto(
    string ClientIdentityHex,
    int WindowsSessionId,
    string State,
    string? Username,
    string? Domain,
    string? DisplayLabel,
    string? UserSidHash,
    bool IsConsoleSession,
    bool IsActive,
    bool IsConnected,
    bool IsLocked,
    bool IsWinlogon,
    bool IsAssistable,
    string? SessionType,
    string? Provider,
    bool HelperConnected,
    bool HelperVersionMatches,
    bool HelperLaunchable,
    bool HelperRepairable,
    int? HelperPid,
    string? HelperVersion,
    ulong InventorySequence,
    long ObservedUnixMs,
    long ExpiresUnixMs,
    string Source,
    bool Stale,
    string? DisabledReason);

public sealed record RemoteSupportWindowsSessionInventoryResponse(
    string ClientIdentityHex,
    bool RemoteSupportWindowsSessionInventorySupported,
    bool RemoteSupportExplicitTargetingSupported,
    bool Stale,
    long? ObservedUnixMs,
    long? ExpiresUnixMs,
    ulong? InventorySequence,
    string? Source,
    string? Message,
    IReadOnlyList<RemoteSupportWindowsSessionDto> Sessions,
    bool LoginAvailable = true,
    bool IsUnavailable = false,
    bool RemoteSupportInventoryDbReady = true,
    IReadOnlyList<string>? RemoteSupportInventoryDbMissingTables = null,
    string? RemoteSupportInventoryDbLatestMigration = null,
    string? RemoteSupportInventoryDbError = null,
    string? RemoteSupportInventoryDbFailureReason = null,
    string? RemoteSupportInventoryDbCurrentDatabase = null,
    string? RemoteSupportInventoryDbCurrentSchema = null,
    IReadOnlyList<string>? RemoteSupportInventoryDbCheckedTables = null,
    string? InventoryUnavailableReason = null,
    string? MissingRelation = null,
    string? ClientReportedVersion = null,
    string? ClientReportedCapabilitiesRaw = null,
    long? ClientInventoryCapabilityReportedAt = null,
    string? ApiStoredClientVersion = null,
    bool? ApiStoredInventorySupported = null,
    bool? ApiStoredExplicitTargetingSupported = null,
    string? ApiCapabilitySource = null,
    long? ApiCapabilityUpdatedAt = null,
    bool? ApiCapabilityStale = null,
    string? InventoryCapabilityDecision = null,
    string? InventoryCapabilityDecisionReason = null);

public sealed record RemoteSupportWindowsSessionRefreshRequest(string? Source = null);

public sealed record RemoteSupportWindowsSessionRefreshResponse(
    string TrackingId,
    string RefreshRequestId,
    bool Accepted,
    bool Completed,
    string Status,
    string Message,
    long RequestedUnixMs,
    ulong? InventorySequence,
    long? ObservedUnixMs,
    RemoteSupportWindowsSessionInventoryResponse? Inventory,
    string? RefreshStage = null,
    long? ApiCacheLastReconciledUnixMs = null,
    int? SpacetimeInventoryRowCount = null,
    int? DbSnapshotRowCount = null,
    long? AgentRefreshObservedUnixMs = null,
    long? AgentAckObservedUnixMs = null);

public sealed record RemoteSupportMediaProfileDto(
    string Profile,
    int MaxWidth,
    int MaxHeight,
    int Fps,
    int TargetKbps);

public sealed record RemoteSupportIceServerDto(
    IReadOnlyList<string> Urls,
    string? Username = null,
    string? Credential = null);

public sealed record RemoteSupportOpenResponse(
    string TrackingId,
    string SessionId,
    string Message,
    bool TargetPreflightRequired = false,
    int ProtocolRevision = 1);

public sealed record RemoteSupportSessionDto(
    string SessionId,
    string ClientIdentityHex,
    string State,
    string Transport,
    long CreatedUnixMs,
    long UpdatedUnixMs,
    long? ConnectedUnixMs,
    long? ClosedUnixMs,
    string? CloseReason,
    string? PayloadJson);

public sealed record RemoteSupportSignalRequest(
    string SignalType,
    string PayloadJson,
    ulong? Sequence = null);

public sealed record RemoteSupportSignalDto(
    string SessionId,
    string SignalType,
    string PayloadJson,
    ulong Sequence,
    long TimestampUnixMs);

public sealed record RemoteSupportActionResponse(
    string TrackingId,
    string Message,
    string? SessionId = null);

public sealed record RemoteSupportCloseRequest(string? Reason);

public sealed record RemoteSupportSasRequest(
    string Action,
    string? Source = null,
    string? ProviderHint = null,
    string? SessionId = null);

public sealed record RemoteSupportSasResponse(
    string TrackingId,
    string Status,
    string Result,
    string Message,
    bool Invoked,
    bool EffectObserved,
    string Route,
    uint? TargetSessionId,
    bool? PolicyAllowsServices,
    bool? ApiAvailable,
    int? Win32Error = null,
    int? HResult = null,
    string? Error = null,
    string? SessionId = null);

public sealed record RemoteSupportHelperRepairRequest(
    string? Source = null,
    string? SessionId = null);

public sealed record RemoteSupportHelperRepairResponse(
    string TrackingId,
    string Status,
    string Message,
    bool Attempted,
    bool Repaired,
    bool ReconnectRequired,
    int? HelperPid = null,
    int? HelperSessionId = null,
    string? HelperVersion = null,
    string? ServiceVersion = null,
    string? LauncherTarget = null,
    bool? TerminateAttempted = null,
    bool? RelaunchAttempted = null,
    string? Error = null,
    string? SessionId = null);

public sealed record RemoteSupportControlFrame(
    string Type,
    string? RequestId = null,
    string? ClientIdentityHex = null,
    string? SessionId = null,
    string? Action = null,
    string? Source = null,
    string? ProviderHint = null,
    string? Status = null,
    string? Result = null,
    string? Message = null,
    bool? Invoked = null,
    bool? EffectObserved = null,
    string? Route = null,
    uint? TargetSessionId = null,
    bool? PolicyAllowsServices = null,
    bool? ApiAvailable = null,
    int? Win32Error = null,
    int? HResult = null,
    string? Error = null,
    string? PayloadJson = null,
    bool? Attempted = null,
    bool? Repaired = null,
    bool? ReconnectRequired = null,
    int? HelperPid = null,
    int? HelperSessionId = null,
    string? HelperVersion = null,
    string? ServiceVersion = null,
    string? LauncherTarget = null,
    bool? TerminateAttempted = null,
    bool? RelaunchAttempted = null,
    long? SentUnixMs = null);

public static class RemoteSupportControlFrameTypes
{
    public const string Hello = "hello";
    public const string Ack = "ack";
    public const string Ping = "ping";
    public const string Pong = "pong";
    public const string SasRequest = "sas_request";
    public const string SasResponse = "sas_response";
    public const string HelperRepairRequest = "helper_repair_request";
    public const string HelperRepairResponse = "helper_repair_response";
    public const string Error = "error";
}

public static class RemoteSupportSignalTypes
{
    public const string Offer = "offer";
    public const string Answer = "answer";
    public const string Ice = "ice";
    public const string Ready = "ready";
    public const string Reject = "reject";
    public const string Reconnect = "reconnect";
    public const string Error = "error";
    public const string Close = "close";
}
