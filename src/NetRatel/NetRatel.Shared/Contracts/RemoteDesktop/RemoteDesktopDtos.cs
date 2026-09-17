namespace NetRatel.Shared.Contracts.RemoteDesktop;

public sealed record OpenRemoteDesktopRequest(
    int? MaxWidth = 1024,
    int? MaxHeight = 576,
    int? TargetFps = 4,
    int? JpegQuality = 40,
    int? Monitor = 0);

public sealed record CaptureRemoteDesktopScreenshotRequest(
    int? MaxWidth = 1024,
    int? MaxHeight = 576,
    int? JpegQuality = 50,
    int? Monitor = 0);

public sealed record RemoteDesktopOpenResponse(
    string TrackingId,
    string SessionId,
    string Message);

public sealed record RemoteDesktopActionResponse(
    string TrackingId,
    string Message,
    string? SessionId = null);

public sealed record RemoteDesktopSessionDto(
    string SessionId,
    string ClientIdentityHex,
    string State,
    long CreatedUnix,
    long LastActivityUnix,
    string? CloseReason,
    int MaxWidth,
    int MaxHeight,
    int TargetFps,
    int JpegQuality,
    int Monitor);

public sealed record RemoteDesktopScreenshotResponse(
    string TrackingId,
    string SessionId,
    string ClientIdentityHex,
    string? Hostname,
    string? TenantName,
    string? AgentVersion,
    int Width,
    int Height,
    int Monitor,
    string Format,
    string Data,
    long TimestampUnixMs);

public sealed record RemoteDesktopStreamMessage(
    string Kind,
    string? Data = null,
    ulong? Sequence = null,
    string? Reason = null,
    string? Status = null);

public sealed record RemoteDesktopCloseRequest(string? Reason);

public sealed record RemoteDesktopInputRequest(
    string Type,
    double? X = null,
    double? Y = null,
    int? Button = null,
    int? DeltaX = null,
    int? DeltaY = null,
    string? Key = null,
    string? Code = null,
    bool CtrlKey = false,
    bool ShiftKey = false,
    bool AltKey = false,
    bool MetaKey = false,
    string? InputEventId = null,
    DateTimeOffset? BrowserEventCreatedAt = null,
    DateTimeOffset? BrowserSentAt = null,
    DateTimeOffset? DataChannelSentAt = null,
    string? InputKind = null,
    string? KeyCategory = null,
    long? MouseMoveCoalescedCount = null,
    string? RemoteSupportSessionId = null,
    int? TargetWindowsSessionId = null,
    string? PeerInstanceId = null,
    string? DataChannelId = null,
    string? DataChannelLabel = null,
    string? DataChannelState = null);

public sealed record RemoteDesktopControlPayload(
    string Action,
    int? MaxWidth = null,
    int? MaxHeight = null,
    int? TargetFps = null,
    int? JpegQuality = null,
    int? Monitor = null);

public sealed record RemoteDesktopFramePayload(
    int Width,
    int Height,
    int Monitor,
    string Format,
    string Data,
    long TimestampUnixMs,
    int? CursorX = null,
    int? CursorY = null,
    ulong? Sequence = null);

public sealed record RemoteDesktopStatusPayload(
    string Status,
    string? Message = null,
    int? Width = null,
    int? Height = null,
    int? Monitor = null);

public static class RemoteDesktopStreamDirections
{
    public const string Frame = "frame";
    public const string Input = "input";
    public const string Control = "control";
    public const string Status = "status";
}

public static class RemoteDesktopControlActions
{
    public const string Start = "start";
    public const string Screenshot = "screenshot";
    public const string Stop = "stop";
}

public static class RemoteDesktopInputTypes
{
    public const string MouseMove = "mouse_move";
    public const string MouseDown = "mouse_down";
    public const string MouseUp = "mouse_up";
    public const string MouseWheel = "mouse_wheel";
    public const string KeyDown = "key_down";
    public const string KeyUp = "key_up";
}
