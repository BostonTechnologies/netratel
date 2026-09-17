using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace NetRatel.Client.Service.RemoteSupport;

internal static class RemoteSupportHandoverStates
{
    public const string Stable = "stable";
    public const string DesktopContextSwitching = "desktop_context_switching";
    public const string HandoverPending = "handover_pending";
    public const string HandoverWaitingForTargetProvider = "handover_waiting_for_target_provider";
    public const string HandoverRenegotiating = "handover_renegotiating";
    public const string HandoverConnected = "handover_connected";
    public const string HandoverFailed = "handover_failed";
    public const string HandoverReconnectRequired = "handover_reconnect_required";
}

internal sealed record RemoteSupportHandoverNotification(
    string SessionId,
    int ProviderGeneration,
    string PreviousProvider,
    string TargetProvider,
    string HandoverReason,
    string HandoverState,
    RemoteSupportProviderDecision Decision);

internal sealed class RemoteSupportProviderTransitionCoordinator : IDisposable
{
    private readonly ConcurrentDictionary<string, RemoteSupportTransitionSession> _sessions = new(StringComparer.Ordinal);
    private readonly Func<RemoteSupportProviderDecision> _getProviderDecision;
    private readonly Action<RemoteSupportHandoverNotification> _notify;
    private readonly Timer _timer;
    private bool _disposed;

    public RemoteSupportProviderTransitionCoordinator(
        Func<RemoteSupportProviderDecision> getProviderDecision,
        Action<RemoteSupportHandoverNotification> notify)
    {
        _getProviderDecision = getProviderDecision;
        _notify = notify;
        _timer = new Timer(_ => EvaluateAll("poll"), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public void RegisterSession(string sessionId)
    {
        _sessions.GetOrAdd(sessionId, id => new RemoteSupportTransitionSession(id));
    }

    public void RemoveSession(string sessionId)
    {
        _sessions.TryRemove(sessionId, out _);
    }

    public int GetGeneration(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session.ProviderGeneration
            : 1;

    public string? GetProvider(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session)
            ? session.CurrentProvider
            : null;

    public void NoteOffer(string sessionId, string provider, int providerGeneration, string? handoverReason)
    {
        var session = _sessions.GetOrAdd(sessionId, id => new RemoteSupportTransitionSession(id));
        lock (session.Sync)
        {
            session.ProviderGeneration = Math.Max(session.ProviderGeneration, Math.Max(1, providerGeneration));
            session.CurrentProvider = provider;
            session.DesiredProvider = provider;
            if (session.HandoverInProgress &&
                providerGeneration >= session.ProviderGeneration &&
                string.Equals(provider, session.TargetProvider, StringComparison.Ordinal))
            {
                session.HandoverState = RemoteSupportHandoverStates.HandoverRenegotiating;
                session.HandoverOfferSentAt = DateTimeOffset.UtcNow;
            }

            session.HandoverReason = handoverReason ?? session.HandoverReason;
        }

        WriteDiagnostics(session, "offer");
    }

    public void NoteProviderSignal(string sessionId, string provider, string signalType, int providerGeneration)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        lock (session.Sync)
        {
            if (providerGeneration < session.ProviderGeneration)
            {
                return;
            }

            if (string.Equals(signalType, NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportSignalTypes.Answer, StringComparison.OrdinalIgnoreCase))
            {
                session.HandoverAnswerReceivedAt = DateTimeOffset.UtcNow;
            }
            else if (string.Equals(signalType, NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportSignalTypes.Ready, StringComparison.OrdinalIgnoreCase) &&
                     session.HandoverInProgress &&
                     string.Equals(provider, session.TargetProvider, StringComparison.Ordinal))
            {
                session.HandoverInProgress = false;
                session.HandoverState = RemoteSupportHandoverStates.HandoverConnected;
                session.HandoverCompletedAt = DateTimeOffset.UtcNow;
                session.LastProviderAfterHandover = provider;
                session.CurrentProvider = provider;
            }
            else if (string.Equals(signalType, NetRatel.Shared.Contracts.RemoteSupport.RemoteSupportSignalTypes.Error, StringComparison.OrdinalIgnoreCase))
            {
                Evaluate(sessionId, "provider_error");
            }
        }

        WriteDiagnostics(session, "provider_signal");
    }

    public void Trigger(string sessionId, string trigger)
    {
        RegisterSession(sessionId);
        Evaluate(sessionId, trigger);
    }

    private void EvaluateAll(string trigger)
    {
        foreach (var sessionId in _sessions.Keys)
        {
            Evaluate(sessionId, trigger);
        }
    }

    private void Evaluate(string sessionId, string trigger)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
        {
            return;
        }

        RemoteSupportProviderDecision decision;
        try
        {
            decision = _getProviderDecision();
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportHandover] Provider decision failed session={sessionId}: {ex.Message}");
            return;
        }

        var desiredProvider = decision.Provider;
        var targetReady = IsProviderReady(decision);
        RemoteSupportHandoverNotification? notification = null;
        lock (session.Sync)
        {
            session.DesiredProvider = desiredProvider;
            session.ActiveHelperSessionId = decision.HelperSessionId;
            session.HelperMatchesActiveConsole = decision.HelperMatchesActiveConsole;
            session.RdpOrNonConsoleHelperDetected = decision.RdpOrNonConsoleHelperDetected;
            session.HandoverLastTrigger = trigger;

            if (string.IsNullOrWhiteSpace(session.CurrentProvider) ||
                string.Equals(session.CurrentProvider, RemoteSupportProviderKinds.Unsupported, StringComparison.Ordinal))
            {
                if (targetReady)
                {
                    session.CurrentProvider = desiredProvider;
                    session.DesiredProvider = desiredProvider;
                }

                WriteDiagnostics(session, trigger);
                return;
            }

            if (string.Equals(desiredProvider, RemoteSupportProviderKinds.Unsupported, StringComparison.Ordinal) ||
                string.Equals(desiredProvider, session.CurrentProvider, StringComparison.Ordinal))
            {
                if (!session.HandoverInProgress)
                {
                    session.HandoverState = RemoteSupportHandoverStates.Stable;
                }

                WriteDiagnostics(session, trigger);
                return;
            }

            var reason = ResolveReason(session.CurrentProvider, desiredProvider, decision.DesktopState, trigger);
            if (!session.HandoverInProgress ||
                !string.Equals(session.TargetProvider, desiredProvider, StringComparison.Ordinal) ||
                !string.Equals(session.HandoverReason, reason, StringComparison.Ordinal))
            {
                session.HandoverInProgress = true;
                session.HandoverState = RemoteSupportHandoverStates.DesktopContextSwitching;
                session.HandoverDetectedAt = DateTimeOffset.UtcNow;
                session.HandoverStartedAt = DateTimeOffset.UtcNow;
                session.HandoverAttemptCount++;
                session.PreviousProvider = session.CurrentProvider;
                session.TargetProvider = desiredProvider;
                session.HandoverReason = reason;
                session.LastDesktopStateBeforeHandover = session.LastDesktopStateAfterHandover ?? decision.DesktopState;
                session.LastProviderBeforeHandover = session.CurrentProvider;
                session.ProviderGeneration++;
            }

            session.LastDesktopStateAfterHandover = decision.DesktopState;
            if (targetReady)
            {
                session.HandoverState = RemoteSupportHandoverStates.HandoverRenegotiating;
                session.HandoverTargetProviderReadyAt ??= DateTimeOffset.UtcNow;
            }
            else
            {
                session.HandoverState = RemoteSupportHandoverStates.HandoverWaitingForTargetProvider;
            }

            notification = new RemoteSupportHandoverNotification(
                session.SessionId,
                session.ProviderGeneration,
                session.PreviousProvider ?? RemoteSupportProviderKinds.Unsupported,
                session.TargetProvider ?? desiredProvider,
                session.HandoverReason ?? reason,
                session.HandoverState,
                decision);
        }

        WriteDiagnostics(session, trigger);
        if (notification is not null)
        {
            _notify(notification);
        }
    }

    private static bool IsProviderReady(RemoteSupportProviderDecision decision) =>
        decision.CanUseInteractiveUserHelper ||
        (decision.CanUseConsoleSecureDesktopHelper && decision.MediaSupported);

    private static string ResolveReason(string fromProvider, string toProvider, string desktopState, string trigger)
    {
        if (string.Equals(fromProvider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal) &&
            string.Equals(toProvider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal))
        {
            return trigger.Contains("unlock", StringComparison.OrdinalIgnoreCase)
                ? "console_to_interactive_after_unlock"
                : "console_to_interactive_after_login";
        }

        if (string.Equals(fromProvider, RemoteSupportProviderKinds.InteractiveUserHelper, StringComparison.Ordinal) &&
            string.Equals(toProvider, RemoteSupportProviderKinds.ConsoleSecureDesktopHelper, StringComparison.Ordinal))
        {
            return string.Equals(desktopState, RemoteSupportDesktopStates.Locked, StringComparison.Ordinal)
                ? "interactive_to_console_after_lock"
                : "interactive_to_console_after_logoff";
        }

        return trigger switch
        {
            "provider_capture_failed" => "provider_capture_failed_desktop_changed",
            "provider_pipe_disconnected" => "provider_pipe_disconnected",
            "peer_failed" => "peer_failed_desktop_changed",
            "manual_refresh" => "manual_refresh_requested",
            "quality_reconnect" => "quality_reconnect_requested",
            _ => "desktop_context_changed"
        };
    }

    private static void WriteDiagnostics(RemoteSupportTransitionSession session, string trigger)
    {
        lock (session.Sync)
        {
            RemoteSupportDiagnosticState.UpdateConsoleProvider(state =>
            {
                state.HandoverSupported = true;
                state.HandoverInProgress = session.HandoverInProgress;
                state.HandoverGeneration = session.ProviderGeneration;
                state.HandoverPreviousProvider = session.PreviousProvider;
                state.HandoverTargetProvider = session.TargetProvider;
                state.HandoverReason = session.HandoverReason;
                state.HandoverDetectedAt = session.HandoverDetectedAt;
                state.HandoverStartedAt = session.HandoverStartedAt;
                state.HandoverTargetProviderReadyAt = session.HandoverTargetProviderReadyAt;
                state.HandoverOfferSentAt = session.HandoverOfferSentAt;
                state.HandoverAnswerReceivedAt = session.HandoverAnswerReceivedAt;
                state.HandoverPeerConnectedAt = session.HandoverPeerConnectedAt;
                state.HandoverCompletedAt = session.HandoverCompletedAt;
                state.HandoverFailedAt = session.HandoverFailedAt;
                state.HandoverFailureReason = session.HandoverFailureReason;
                state.HandoverLastTrigger = trigger;
                state.HandoverAttemptCount = session.HandoverAttemptCount;
                state.LastDesktopStateBeforeHandover = session.LastDesktopStateBeforeHandover;
                state.LastDesktopStateAfterHandover = session.LastDesktopStateAfterHandover;
                state.LastProviderBeforeHandover = session.LastProviderBeforeHandover;
                state.LastProviderAfterHandover = session.LastProviderAfterHandover;
                state.ActiveProvider = session.CurrentProvider;
                state.DesiredProvider = session.DesiredProvider;
                state.CurrentProviderGeneration = session.ProviderGeneration;
                state.ActiveHelperSessionId = session.ActiveHelperSessionId;
                state.HelperMatchesActiveConsole = session.HelperMatchesActiveConsole;
                state.RdpOrNonConsoleHelperDetected = session.RdpOrNonConsoleHelperDetected;
            });
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Dispose();
    }

    private sealed class RemoteSupportTransitionSession
    {
        public RemoteSupportTransitionSession(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public object Sync { get; } = new();
        public string? CurrentProvider { get; set; }
        public string? DesiredProvider { get; set; }
        public string? PreviousProvider { get; set; }
        public string? TargetProvider { get; set; }
        public int ProviderGeneration { get; set; } = 1;
        public bool HandoverInProgress { get; set; }
        public string HandoverState { get; set; } = RemoteSupportHandoverStates.Stable;
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
        public int? ActiveHelperSessionId { get; set; }
        public bool HelperMatchesActiveConsole { get; set; }
        public bool RdpOrNonConsoleHelperDetected { get; set; }
    }
}
