using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.RemoteDesktop;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace NetRatel.Client.Service.RemoteSupport;

internal static class RemoteSupportDesktopStates
{
    public const string NoUser = "no_user";
    public const string LoggedIn = "logged_in";
    public const string Locked = "locked";
    public const string Unlocked = "unlocked";
    public const string Disconnected = "disconnected";
    public const string Unknown = "unknown";
}

internal static class RemoteSupportProviderKinds
{
    public const string InteractiveUserHelper = "interactive_user_helper";
    public const string ConsoleSecureDesktopHelper = "console_secure_desktop_helper";
    public const string Unsupported = "unsupported";
}

internal static class RemoteSupportSupportLevels
{
    public const string DiagnosticsOnly = "diagnostics_only";
    public const string MediaPreview = "media_preview";
}

internal static class RemoteSupportStatusCodes
{
    public const string NoInteractiveUser = "no_interactive_user";
    public const string WindowsLogonDesktopDetected = "windows_logon_desktop_detected";
    public const string WindowsLockedDesktopDetected = "windows_locked_desktop_detected";
    public const string SecureDesktopCaptureAvailable = "secure_desktop_capture_available";
    public const string SecureDesktopCaptureUnavailable = "secure_desktop_capture_unavailable";
    public const string SecureDesktopInputAvailable = "secure_desktop_input_available";
    public const string SecureDesktopInputUnavailable = "secure_desktop_input_unavailable";
    public const string DesktopContextSwitching = "desktop_context_switching";
    public const string DesktopContextChangedReconnectRequired = "desktop_context_changed_reconnect_required";
    public const string ConsoleSecureDesktopHelperStarting = "console_secure_desktop_helper_starting";
    public const string ConsoleSecureDesktopHelperUnavailable = "console_secure_desktop_helper_unavailable";
    public const string ConsoleSecureDesktopMediaPreviewAvailable = "console_secure_desktop_media_preview_available";
    public const string ConsoleProviderPipeHostNotStarted = "console_provider_pipe_host_not_started";
    public const string ConsoleProviderPipeNotReady = "console_provider_pipe_not_ready";
    public const string ConsoleProviderLaunchNotAttempted = "console_provider_launch_not_attempted";
    public const string ConsoleProviderLaunchFailed = "console_provider_launch_failed";
    public const string ConsoleProviderStartedButNoHello = "console_provider_started_but_no_hello";
    public const string ConsoleProviderConnectedButOfferNotForwarded = "console_provider_connected_but_offer_not_forwarded";
    public const string ConsoleProviderOfferForwardedButNoAnswer = "console_provider_offer_forwarded_but_no_answer";
    public const string ConsoleProviderWebRtcFailed = "console_provider_webrtc_failed";
    public const string SasInvoked = "sas_invoked";
    public const string SasEffectObserved = "sas_effect_observed";
    public const string SasNoVisibleEffect = "sas_no_visible_effect";
    public const string SasInvokedEffectUnknown = "sas_invoked_effect_unknown";
    public const string SasPolicyNotEnabled = "sas_policy_not_enabled";
    public const string SasApiNotAvailable = "sas_api_not_available";
    public const string SasFailedBeforeInvoke = "sas_failed_before_invoke";
    public const string HelperVersionMismatchDetected = "helper_version_mismatch_detected";
    public const string HelperRepairStarted = "helper_repair_started";
    public const string HelperRepairCompletedReconnectRequired = "helper_repair_completed_reconnect_required";
    public const string HelperRepairFailedStaleHelperNotSafeToKill = "helper_repair_failed_stale_helper_not_safe_to_kill";
    public const string HelperRelaunchFailed = "helper_relaunch_failed";
    public const string HelperRepairRequiresUserLogoff = "helper_repair_requires_user_logoff";
    public const string HelperVersionMatchRestored = "helper_version_match_restored";
    public const string InteractiveHelperMissing = "interactive_helper_missing";
    public const string InteractiveHelperLaunchStarted = "interactive_helper_launch_started";
    public const string InteractiveHelperLaunchRequested = "interactive_helper_launch_requested";
    public const string InteractiveHelperWaitingForHello = "interactive_helper_waiting_for_hello";
    public const string InteractiveHelperConnected = "interactive_helper_connected";
    public const string InteractiveHelperVersionMismatchDetected = "interactive_helper_version_mismatch_detected";
    public const string InteractiveHelperRepairStarted = "interactive_helper_repair_started";
    public const string InteractiveHelperReady = "interactive_helper_ready";
    public const string InteractiveHelperRepairFailed = "interactive_helper_repair_failed";
    public const string InteractiveHelperLaunchTimeout = "interactive_helper_launch_timeout";
    public const string InteractiveHelperRepairRequiresLogoff = "interactive_helper_repair_requires_logoff";
    public const string InteractiveHelperRepairBlockedUnsafeProcess = "interactive_helper_repair_blocked_unsafe_process";
    public const string InteractiveDesktopRecovering = "interactive_desktop_recovering";
    public const string InteractiveDesktopReady = "interactive_desktop_ready";
    public const string InteractiveDesktopRecoveryExhausted = "interactive_desktop_recovery_exhausted";
    public const string InteractiveHelperRestartStarted = "interactive_helper_restart_started";
    public const string InteractiveHelperRestartedReconnectRequired = "interactive_helper_restarted_reconnect_required";
    public const string TargetValidating = "target_validating";
    public const string TargetHelperLaunching = "target_helper_launching";
    public const string TargetHelperReady = "target_helper_ready";
    public const string TargetSessionStale = "target_session_stale";
    public const string TargetIdentityMismatch = "target_identity_mismatch";
    public const string TargetHelperUnavailable = "target_helper_unavailable";
    public const string ReadyForOffer = "ready_for_offer";
}

internal sealed record RemoteSupportProviderDecision(
    string DesktopState,
    string Provider,
    string SupportLevel,
    string StatusCode,
    string Message,
    bool UserHelperConnected,
    bool HelperVersionMatchesService,
    int? HelperSessionId,
    string? HelperVersion,
    uint? ActiveConsoleSessionId,
    string? InputDesktopName,
    bool CaptureAvailable,
    bool InputAvailable,
    bool MediaSupported,
    bool ReconnectRequired,
    string? DiagnosticError,
    bool HelperMatchesActiveConsole = false,
    bool RdpOrNonConsoleHelperDetected = false,
    string? SelectedProviderReason = null)
{
    public bool CanUseInteractiveUserHelper =>
        string.Equals(Provider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal) &&
        MediaSupported;

    public bool CanUseConsoleSecureDesktopHelper =>
        string.Equals(Provider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
        string.Equals(SupportLevel, RemoteSupportSupportLevels.MediaPreview, StringComparison.Ordinal);
}

internal static class RemoteSupportDiagnosticState
{
    private static readonly object Sync = new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string StatePath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRatel",
            "Client",
            "remote-support-state.json");

    public static void Write(RemoteSupportProviderDecision decision)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var state = ReadNoLock();
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                state.DesktopState = decision.DesktopState;
                state.Provider = decision.Provider;
                state.SupportLevel = decision.SupportLevel;
                state.StatusCode = decision.StatusCode;
                state.Message = decision.Message;
                state.UserHelperConnected = decision.UserHelperConnected;
                state.HelperVersionMatchesService = decision.HelperVersionMatchesService;
                state.HelperSessionId = decision.HelperSessionId;
                state.HelperVersion = decision.HelperVersion;
                state.HelperMatchesActiveConsole = decision.HelperMatchesActiveConsole;
                state.RdpOrNonConsoleHelperDetected = decision.RdpOrNonConsoleHelperDetected;
                state.SelectedProviderReason = decision.SelectedProviderReason;
                state.ActiveConsoleSessionId = decision.ActiveConsoleSessionId;
                state.InputDesktopName = decision.InputDesktopName;
                state.CaptureAvailable = decision.CaptureAvailable;
                state.InputAvailable = decision.InputAvailable;
                state.MediaSupported = decision.MediaSupported;
                state.ReconnectRequired = decision.ReconnectRequired;
                state.DiagnosticError = decision.DiagnosticError;
                WriteNoLock(state);
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDiagnostics] State write failed: {ex.Message}");
        }
    }

    public static State Read()
    {
        lock (Sync)
        {
            return ReadNoLock();
        }
    }

    public static void UpdateConsoleProvider(Action<State> update)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var state = ReadNoLock();
                update(state);
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                WriteNoLock(state);
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDiagnostics] Console provider state update failed: {ex.Message}");
        }
    }

    public static void UpdateCaptureProvider(Action<State> update)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var state = ReadNoLock();
                update(state);
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                WriteNoLock(state);
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDiagnostics] Capture provider state update failed: {ex.Message}");
        }
    }

    public static void UpdateInventory(Action<State> update)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var state = ReadNoLock();
                update(state);
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                WriteNoLock(state);
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDiagnostics] Inventory state update failed: {ex.Message}");
        }
    }

    public static void UpdateTelemetry(Action<State> update)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
                var state = ReadNoLock();
                update(state);
                state.UpdatedUtc = DateTimeOffset.UtcNow;
                WriteNoLock(state);
            }
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportDiagnostics] Telemetry state update failed: {ex.Message}");
        }
    }

    private static State ReadNoLock()
    {
        if (!File.Exists(StatePath))
        {
            return new State();
        }

        try
        {
            return JsonSerializer.Deserialize<State>(File.ReadAllText(StatePath), Json) ?? new State();
        }
        catch
        {
            return new State();
        }
    }

    private static void WriteNoLock(State state) =>
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state, Json));

    public sealed class State
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public string DesktopState { get; set; } = RemoteSupportDesktopStates.Unknown;
        public string Provider { get; set; } = RemoteSupportProviderKinds.Unsupported;
        public string SupportLevel { get; set; } = RemoteSupportSupportLevels.DiagnosticsOnly;
        public string StatusCode { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public bool UserHelperConnected { get; set; }
        public bool HelperVersionMatchesService { get; set; }
        public int? HelperSessionId { get; set; }
        public string? HelperVersion { get; set; }
        public bool HelperMatchesActiveConsole { get; set; }
        public bool RdpOrNonConsoleHelperDetected { get; set; }
        public string? SelectedProviderReason { get; set; }
        public uint? ActiveConsoleSessionId { get; set; }
        public string? InputDesktopName { get; set; }
        public bool CaptureAvailable { get; set; }
        public bool InputAvailable { get; set; }
        public bool MediaSupported { get; set; }
        public bool ReconnectRequired { get; set; }
        public string? DiagnosticError { get; set; }
        public string? ConsoleProviderPipeName { get; set; }
        public string? ConsoleProviderPipePath { get; set; }
        public bool ConsoleProviderPipeExists { get; set; }
        public bool ConsoleProviderPipeHostStarted { get; set; }
        public bool ConsoleProviderPipeHostReady { get; set; }
        public string? ConsoleProviderPipeSecurity { get; set; }
        public bool ConsoleProviderHelperLaunchAttempted { get; set; }
        public string? ConsoleProviderHelperLaunchCommand { get; set; }
        public int? ConsoleProviderHelperLaunchExitCode { get; set; }
        public string? ConsoleProviderHelperLaunchError { get; set; }
        public string? ConsoleProviderLauncherBackend { get; set; }
        public bool ConsoleProviderDuplicatedTokenSucceeded { get; set; }
        public bool ConsoleProviderSetTokenSessionIdSucceeded { get; set; }
        public int? ConsoleProviderTokenSessionId { get; set; }
        public bool ConsoleProviderCreateEnvironmentBlockSucceeded { get; set; }
        public bool ConsoleProviderCreateProcessAsUserSucceeded { get; set; }
        public int? ConsoleProviderLaunchedProcessSessionId { get; set; }
        public string? ConsoleProviderLaunchedDesktop { get; set; }
        public int? ConsoleProviderLaunchWin32Error { get; set; }
        public int? ConsoleProviderLaunchHresult { get; set; }
        public string? ConsoleProviderLaunchException { get; set; }
        public string? ConsoleProviderLaunchAttemptsJson { get; set; }
        public int? ConsoleProviderHelperPid { get; set; }
        public int? ConsoleProviderHelperSessionId { get; set; }
        public uint? ConsoleProviderHelperActiveConsoleSessionId { get; set; }
        public bool ConsoleProviderHelperLaunchedInTargetSession { get; set; }
        public string? ConsoleProviderHelperWindowStation { get; set; }
        public string? ConsoleProviderHelperDesktop { get; set; }
        public bool ConsoleProviderHelperConnected { get; set; }
        public string? ConsoleProviderHelperVersion { get; set; }
        public bool ConsoleProviderVersionMatchesService { get; set; }
        public bool ConsoleProviderHelloReceived { get; set; }
        public int? ConsoleProviderRawFirstMessageBytes { get; set; }
        public string? ConsoleProviderRawFirstMessagePreview { get; set; }
        public string? ConsoleProviderHelloParseError { get; set; }
        public string? ConsoleProviderLastStage { get; set; }
        public string? ConsoleProviderLastError { get; set; }
        public DateTimeOffset? ConsoleProviderLastOfferForwarded { get; set; }
        public DateTimeOffset? ConsoleProviderLastIceForwarded { get; set; }
        public DateTimeOffset? ConsoleProviderLastCloseForwarded { get; set; }
        public DateTimeOffset? ConsoleProviderLastAnswerReceived { get; set; }
        public string? CaptureProviderName { get; set; }
        public string? CaptureProviderBackendName { get; set; }
        public bool CaptureProviderSelected { get; set; }
        public DateTimeOffset? CaptureProviderInitStarted { get; set; }
        public bool CaptureProviderInitSucceeded { get; set; }
        public bool CaptureProviderInitFailed { get; set; }
        public string? CaptureProviderInitError { get; set; }
        public string? CaptureProviderDesktopName { get; set; }
        public string? CaptureProviderSessionId { get; set; }
        public string? CaptureProviderActiveConsoleSessionId { get; set; }
        public string? CaptureProviderThreadDesktopBefore { get; set; }
        public string? CaptureProviderThreadDesktopAfter { get; set; }
        public bool CaptureProviderSetThreadDesktopSucceeded { get; set; }
        public DateTimeOffset? CaptureProviderLastFrameAttemptUtc { get; set; }
        public string? CaptureProviderLastFrameError { get; set; }
        public int CaptureProviderConsecutiveFailures { get; set; }
        public DateTimeOffset? CaptureProviderLastSuccessfulFrameUtc { get; set; }
        public int? CaptureProviderLastWin32Error { get; set; }
        public string? CaptureProviderBestBackend { get; set; }
        public string? CaptureProviderFailedBackends { get; set; }
        public string? CaptureProviderMatrixJson { get; set; }
        public bool RemoteSupportTransportSupported { get; set; }
        public bool RemoteSupportPeerConnected { get; set; }
        public DateTimeOffset? RemoteSupportPeerConnectedAt { get; set; }
        public string? RemoteSupportPeerLastState { get; set; }
        public bool RemoteSupportDataChannelOpen { get; set; }
        public DateTimeOffset? RemoteSupportDataChannelOpenAt { get; set; }
        public bool RemoteSupportCaptureSupported { get; set; }
        public bool RemoteSupportFirstFrameDelivered { get; set; }
        public bool RemoteSupportInputSupported { get; set; }
        public ulong RemoteSupportRenderedFrames { get; set; }
        public string? RemoteSupportSessionId { get; set; }
        public bool RemoteSupportStateIsCurrentSession { get; set; }
        public DateTimeOffset? RemoteSupportStateUpdatedUtc { get; set; }
        public string? RemoteSupportStateSource { get; set; }
        public bool RemoteSupportIsLiveSession { get; set; }
        public bool RemoteSupportIsLastClosedSession { get; set; }
        public DateTimeOffset? RemoteSupportClosedAt { get; set; }
        public string? RemoteSupportCloseReason { get; set; }
        public DateTimeOffset? RemoteSupportFirstFrameDeliveredAt { get; set; }
        public DateTimeOffset? RemoteSupportLastFrameRenderedAt { get; set; }
        public string? ActiveInputProvider { get; set; }
        public string? InputProviderName { get; set; }
        public int? InputProviderSessionId { get; set; }
        public uint? InputProviderActiveConsoleSessionId { get; set; }
        public bool InputProviderMatchesTargetSession { get; set; }
        public string? InputProviderDesktopName { get; set; }
        public bool InputProviderReady { get; set; }
        public DateTimeOffset? InputProviderLastInputReceivedAt { get; set; }
        public DateTimeOffset? InputProviderLastInputInjectedAt { get; set; }
        public string? InputProviderLastInputError { get; set; }
        public ulong InputProviderInjectedMouseCount { get; set; }
        public ulong InputProviderInjectedKeyCount { get; set; }
        public ulong InputProviderRejectedCount { get; set; }
        public ulong InputProviderReceivedMouseCount { get; set; }
        public ulong InputProviderReceivedKeyCount { get; set; }
        public ulong MouseMoveReceivedCount { get; set; }
        public ulong MouseMoveSentCount { get; set; }
        public ulong MouseMoveCoalescedCount { get; set; }
        public ulong MouseClickSentCount { get; set; }
        public string? InputProviderLastInputEventId { get; set; }
        public string? InputProviderLastInputKind { get; set; }
        public string? InputProviderLastKeyCategory { get; set; }
        public DateTimeOffset? InputProviderLastBrowserSentAt { get; set; }
        public DateTimeOffset? InputProviderLastDataChannelSentAt { get; set; }
        public DateTimeOffset? InputProviderLastReceivedAt { get; set; }
        public DateTimeOffset? InputProviderLastSendInputAttemptedAt { get; set; }
        public int? InputProviderLastSendInputResultCount { get; set; }
        public int? InputProviderLastSendInputWin32Error { get; set; }
        public ulong CaptureFrameSequence { get; set; }
        public string? CaptureFrameHash { get; set; }
        public bool CaptureFrameChanged { get; set; }
        public ulong CaptureSameFrameCount { get; set; }
        public DateTimeOffset? CaptureLastChangedFrameUtc { get; set; }
        public string? CaptureLastInputEventId { get; set; }
        public DateTimeOffset? CaptureLastInputInjectedUtc { get; set; }
        public DateTimeOffset? CaptureFirstFrameAfterInputUtc { get; set; }
        public DateTimeOffset? CaptureFirstChangedFrameAfterInputUtc { get; set; }
        public long? CaptureInputToChangedFrameMs { get; set; }
        public long? CaptureInputToRenderedFrameMs { get; set; }
        public ulong EncoderFrameSequence { get; set; }
        public DateTimeOffset? EncoderKeyframeRequestedAt { get; set; }
        public string? EncoderKeyframeStatus { get; set; }
        public DateTimeOffset? EncoderLastFrameSentAt { get; set; }
        public DateTimeOffset? WebLastFrameRenderedAt { get; set; }
        public string? InputVisualFeedbackStatus { get; set; }
        public bool HandoverSupported { get; set; }
        public bool HandoverInProgress { get; set; }
        public int HandoverGeneration { get; set; }
        public string? HandoverPreviousProvider { get; set; }
        public string? HandoverTargetProvider { get; set; }
        public string? HandoverReason { get; set; }
        public DateTimeOffset? HandoverDetectedAt { get; set; }
        public DateTimeOffset? HandoverStartedAt { get; set; }
        public DateTimeOffset? HandoverTargetProviderReadyAt { get; set; }
        public DateTimeOffset? HandoverOfferSentAt { get; set; }
        public DateTimeOffset? HandoverAnswerReceivedAt { get; set; }
        public DateTimeOffset? HandoverPeerConnectedAt { get; set; }
        public DateTimeOffset? HandoverCompletedAt { get; set; }
        public DateTimeOffset? HandoverFailedAt { get; set; }
        public string? HandoverFailureReason { get; set; }
        public string? HandoverLastTrigger { get; set; }
        public int HandoverAttemptCount { get; set; }
        public string? LastDesktopStateBeforeHandover { get; set; }
        public string? LastDesktopStateAfterHandover { get; set; }
        public string? LastProviderBeforeHandover { get; set; }
        public string? LastProviderAfterHandover { get; set; }
        public string? ActiveProvider { get; set; }
        public string? DesiredProvider { get; set; }
        public int CurrentProviderGeneration { get; set; }
        public int? ActiveHelperSessionId { get; set; }
        public bool RemoteSupportSasSupported { get; set; }
        public bool RemoteSupportSasPolicyAllowsServices { get; set; }
        public bool RemoteSupportSasApiAvailable { get; set; }
        public string? RemoteSupportSasProvider { get; set; }
        public uint? RemoteSupportSasTargetSessionId { get; set; }
        public DateTimeOffset? RemoteSupportSasLastAttemptAt { get; set; }
        public string? RemoteSupportSasLastResult { get; set; }
        public string? RemoteSupportSasLastError { get; set; }
        public int? RemoteSupportSasLastWin32Error { get; set; }
        public int? RemoteSupportSasLastHresult { get; set; }
        public int? SoftwareSasGenerationRawValue { get; set; }
        public string? SoftwareSasGenerationInterpretedValue { get; set; }
        public bool SoftwareSasAllowsServices { get; set; }
        public bool SoftwareSasAllowsEaseOfAccess { get; set; }
        public string? SoftwareSasPolicySource { get; set; }
        public string? RemoteSupportOsCaption { get; set; }
        public string? RemoteSupportOsVersion { get; set; }
        public string? RemoteSupportOsBuild { get; set; }
        public bool RemoteSupportOsIsServer { get; set; }
        public bool InteractiveHelperRepairSupported { get; set; }
        public bool InteractiveHelperRepairInProgress { get; set; }
        public DateTimeOffset? InteractiveHelperRepairLastAttemptAt { get; set; }
        public string? InteractiveHelperRepairLastResult { get; set; }
        public string? InteractiveHelperRepairLastError { get; set; }
        public string? InteractiveHelperLauncherTarget { get; set; }
        public string? InteractiveHelperLauncherTargetVersion { get; set; }
        public string? InteractiveHelperConnectedVersion { get; set; }
        public string? InteractiveHelperServiceVersion { get; set; }
        public long? InteractiveHelperVersionMismatchAgeSeconds { get; set; }
        public int? InteractiveHelperStalePid { get; set; }
        public int? InteractiveHelperStaleSessionId { get; set; }
        public bool InteractiveHelperTerminateAttempted { get; set; }
        public bool InteractiveHelperRelaunchAttempted { get; set; }
        public string? InteractiveHelperRelaunchResult { get; set; }
        public bool RemoteSupportInventoryPublisherStarted { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastEnumerationStartedAt { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastEnumerationCompletedAt { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastPublishStartedAt { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastPublishCompletedAt { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastRefreshObservedAt { get; set; }
        public DateTimeOffset? RemoteSupportInventoryLastRefreshAckSentAt { get; set; }
        public string? RemoteSupportInventoryLastRefreshRequestId { get; set; }
        public string? RemoteSupportInventoryLastSource { get; set; }
        public ulong RemoteSupportInventoryLastSequence { get; set; }
        public int RemoteSupportInventoryLastSessionCount { get; set; }
        public int RemoteSupportInventoryPendingRefreshCount { get; set; }
        public string? RemoteSupportInventoryLastError { get; set; }
        public DateTimeOffset? TelemetrySinkSeenAt { get; set; }
        public DateTimeOffset? TelemetryLastPublishAt { get; set; }
        public string? TelemetryLastError { get; set; }
    }
}

internal static class RemoteSupportProviderDiagnostics
{
    public static RemoteSupportProviderDecision Evaluate(
        ConnectedUserHelper? helper,
        string serviceVersion,
        bool helperVersionMatchesService)
    {
        if (!OperatingSystem.IsWindows())
        {
            return new RemoteSupportProviderDecision(
                RemoteSupportDesktopStates.Unknown,
                RemoteSupportProviderKinds.Unsupported,
                RemoteSupportSupportLevels.DiagnosticsOnly,
                "native_webrtc_unsupported_os",
                "Native remote support media is currently Windows-only.",
                helper is not null,
                helperVersionMatchesService,
                helper?.SessionId,
                helper?.Version,
                null,
                null,
                false,
                false,
                false,
                false,
                null);
        }

#pragma warning disable CA1416
        return EvaluateWindows(helper, serviceVersion, helperVersionMatchesService);
#pragma warning restore CA1416
    }

    [SupportedOSPlatform("windows")]
    public static RemoteSupportProviderDecision EvaluateCurrentProcess()
    {
        var serviceVersion = typeof(RemoteSupportProviderDiagnostics).Assembly.GetName().Version?.ToString() ?? "Unknown";
        return EvaluateWindows(null, serviceVersion, false);
    }

    [SupportedOSPlatform("windows")]
    private static RemoteSupportProviderDecision EvaluateWindows(
        ConnectedUserHelper? helper,
        string serviceVersion,
        bool helperVersionMatchesService)
    {
        var activeConsoleSessionId = WTSGetActiveConsoleSessionId();
        var sessionState = activeConsoleSessionId == uint.MaxValue
            ? WtsConnectState.Unknown
            : QuerySessionConnectState(activeConsoleSessionId, out _);
        var userName = activeConsoleSessionId == uint.MaxValue
            ? null
            : QuerySessionString(activeConsoleSessionId, WtsInfoClass.UserName, out _);
        var inputDesktop = TryGetInputDesktopName(out var inputDesktopError);
        var captureAvailable = TryProbeCurrentDesktopCapture(out var captureError);
        var inputAvailable = !string.IsNullOrWhiteSpace(inputDesktop);
        var diagnosticError = FirstNonEmpty(inputDesktopError, captureError);
        var hasInteractiveUser = !string.IsNullOrWhiteSpace(userName);
        var desktopState = ResolveDesktopState(sessionState, hasInteractiveUser, inputDesktop);
        var activeConsoleSession = activeConsoleSessionId == uint.MaxValue ? (int?)null : unchecked((int)activeConsoleSessionId);
        var helperMatchesActiveConsole = helper is not null &&
            activeConsoleSession.HasValue &&
            helper.SessionId == activeConsoleSession.Value;
        var nonConsoleHelper = helper is not null && !helperMatchesActiveConsole;

        if (helper is not null &&
            helperVersionMatchesService &&
            helperMatchesActiveConsole &&
            !string.Equals(desktopState, RemoteSupportDesktopStates.NoUser, StringComparison.Ordinal) &&
            !string.Equals(desktopState, RemoteSupportDesktopStates.Locked, StringComparison.Ordinal) &&
            !string.Equals(desktopState, RemoteSupportDesktopStates.Disconnected, StringComparison.Ordinal))
        {
            return new RemoteSupportProviderDecision(
                desktopState,
                RemoteSupportProviderKinds.InteractiveUserHelper,
                RemoteSupportSupportLevels.DiagnosticsOnly,
                "interactive_user_helper_available",
                "Interactive user helper is connected and remains the selected WebRTC media provider.",
                true,
                true,
                helper.SessionId,
                helper.Version,
                activeConsoleSessionId == uint.MaxValue ? null : activeConsoleSessionId,
                inputDesktop,
                captureAvailable,
                inputAvailable,
                true,
                false,
                diagnosticError,
                helperMatchesActiveConsole,
                false,
                "active_console_interactive_helper");
        }

        if (!hasInteractiveUser)
        {
            return new RemoteSupportProviderDecision(
                RemoteSupportDesktopStates.NoUser,
                RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
                RemoteSupportSupportLevels.MediaPreview,
                RemoteSupportStatusCodes.NoInteractiveUser,
                "No interactive user helper is connected. Console secure-desktop media preview may be attempted.",
                helper is not null,
                helperVersionMatchesService,
                helper?.SessionId,
                helper?.Version,
                activeConsoleSessionId == uint.MaxValue ? null : activeConsoleSessionId,
                inputDesktop,
                captureAvailable,
                inputAvailable,
                false,
                false,
                diagnosticError,
                helperMatchesActiveConsole,
                nonConsoleHelper,
                nonConsoleHelper ? "helper_connected_non_console_session" : "no_interactive_user");
        }

        if (string.Equals(desktopState, RemoteSupportDesktopStates.Locked, StringComparison.Ordinal))
        {
            return new RemoteSupportProviderDecision(
                desktopState,
                RemoteSupportProviderKinds.ConsoleSecureDesktopHelper,
                RemoteSupportSupportLevels.MediaPreview,
                RemoteSupportStatusCodes.WindowsLockedDesktopDetected,
                "Windows lock/logon desktop detected. Console secure-desktop media preview may be attempted.",
                helper is not null,
                helperVersionMatchesService,
                helper?.SessionId,
                helper?.Version,
                activeConsoleSessionId == uint.MaxValue ? null : activeConsoleSessionId,
                inputDesktop,
                captureAvailable,
                inputAvailable,
                false,
                true,
                diagnosticError,
                helperMatchesActiveConsole,
                nonConsoleHelper,
                nonConsoleHelper ? "helper_connected_non_console_session" : "windows_locked_desktop_detected");
        }

        var status = nonConsoleHelper
            ? "helper_connected_non_console_session"
            : helper is null
            ? RemoteSupportStatusCodes.InteractiveHelperMissing
            : RemoteSupportStatusCodes.InteractiveHelperVersionMismatchDetected;
        var message = nonConsoleHelper
            ? "Interactive helper is connected in a non-console session and is not selected for the active-console remote-support target."
            : helper is null
            ? "Active console user is logged in, but no interactive helper is connected yet."
            : "Active console interactive helper version does not match the service version.";

        return new RemoteSupportProviderDecision(
            desktopState,
            RemoteSupportProviderKinds.InteractiveUserHelper,
            RemoteSupportSupportLevels.DiagnosticsOnly,
            status,
            message,
            helper is not null,
            helperVersionMatchesService,
            helper?.SessionId,
            helper?.Version,
            activeConsoleSessionId == uint.MaxValue ? null : activeConsoleSessionId,
            inputDesktop,
            captureAvailable,
            inputAvailable,
            false,
            false,
            diagnosticError,
            helperMatchesActiveConsole,
            nonConsoleHelper,
            nonConsoleHelper ? "active_console_helper_missing_non_console_helper_detected" : status);
    }

    [SupportedOSPlatform("windows")]
    private static string ResolveDesktopState(WtsConnectState sessionState, bool hasInteractiveUser, string? inputDesktop)
    {
        if (sessionState is WtsConnectState.Disconnected or WtsConnectState.Reset or WtsConnectState.Down)
        {
            return RemoteSupportDesktopStates.Disconnected;
        }

        if (!hasInteractiveUser)
        {
            return RemoteSupportDesktopStates.NoUser;
        }

        if (string.Equals(inputDesktop, "Winlogon", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteSupportDesktopStates.Locked;
        }

        if (string.Equals(inputDesktop, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return RemoteSupportDesktopStates.Unlocked;
        }

        return hasInteractiveUser ? RemoteSupportDesktopStates.LoggedIn : RemoteSupportDesktopStates.Unknown;
    }

    [SupportedOSPlatform("windows")]
    private static WtsConnectState QuerySessionConnectState(uint sessionId, out string? error)
    {
        if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.ConnectState, out var buffer, out var bytes) && bytes >= sizeof(int))
        {
            try
            {
                error = null;
                return (WtsConnectState)Marshal.ReadInt32(buffer);
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return WtsConnectState.Unknown;
    }

    [SupportedOSPlatform("windows")]
    private static string? QuerySessionString(uint sessionId, WtsInfoClass infoClass, out string? error)
    {
        if (WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out var bytes) && bytes > 1)
        {
            try
            {
                error = null;
                return Marshal.PtrToStringUni(buffer)?.Trim();
            }
            finally
            {
                WTSFreeMemory(buffer);
            }
        }

        error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
        return null;
    }

    [SupportedOSPlatform("windows")]
    private static string? TryGetInputDesktopName(out string? error)
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == IntPtr.Zero)
        {
            error = "OpenInputDesktop failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
            return null;
        }

        try
        {
            var buffer = new byte[512];
            if (!GetUserObjectInformation(desktop, UserObjectName, buffer, buffer.Length, out var needed))
            {
                error = "GetUserObjectInformation failed: " + new Win32Exception(Marshal.GetLastWin32Error()).Message;
                return null;
            }

            error = null;
            return System.Text.Encoding.Unicode.GetString(buffer, 0, Math.Max(0, needed - 2)).TrimEnd('\0');
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryProbeCurrentDesktopCapture(out string? error)
    {
        try
        {
            var width = Math.Max(1, GetSystemMetrics(0));
            var height = Math.Max(1, GetSystemMetrics(1));
            using var bitmap = new Bitmap(1, 1, PixelFormat.Format24bppRgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(0, 0, 0, 0, new Size(Math.Min(width, 1), Math.Min(height, 1)), CopyPixelOperation.SourceCopy);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = "CopyFromScreen probe failed: " + ex.Message;
            return false;
        }
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private const int UserObjectName = 2;
    private const uint DesktopReadObjects = 0x0001;

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [SupportedOSPlatform("windows")]
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WtsInfoClass wtsInfoClass,
        out IntPtr ppBuffer,
        out uint pBytesReturned);

    [SupportedOSPlatform("windows")]
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pointer);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint dwFlags, bool fInherit, uint dwDesiredAccess);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr hDesktop);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetUserObjectInformation(IntPtr hObj, int nIndex, byte[] pvInfo, int nLength, out int lpnLengthNeeded);

    [SupportedOSPlatform("windows")]
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private enum WtsInfoClass
    {
        ConnectState = 8,
        UserName = 5
    }

    private enum WtsConnectState
    {
        Active = 0,
        Connected = 1,
        ConnectQuery = 2,
        Shadow = 3,
        Disconnected = 4,
        Idle = 5,
        Listen = 6,
        Reset = 7,
        Down = 8,
        Init = 9,
        Unknown = 10
    }
}
