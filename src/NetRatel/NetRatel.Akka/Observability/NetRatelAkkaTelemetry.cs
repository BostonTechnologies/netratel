using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace NetRatel.Akka.Observability;

/// <summary>
/// Bounded OpenTelemetry instrumentation for Akka migration authority paths.
/// No instrument includes tenant, client, command, or session identifiers.
/// </summary>
public static class NetRatelAkkaTelemetry
{
    public const string MeterName = "NetRatel.Akka";
    public const string ActivitySourceName = "NetRatel.Akka";

    private static readonly Meter Meter = new(MeterName, "1.0.0");
    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    private static readonly Counter<long> PresenceConnected = Counter("akka_presence_connected_total");
    private static readonly Counter<long> PresenceDisconnected = Counter("akka_presence_disconnected_total");
    private static readonly Counter<long> PresenceHeartbeatExpired = Counter("akka_presence_heartbeat_expired_total");
    private static readonly Counter<long> TelemetryAccepted = Counter("akka_telemetry_accepted_total");
    private static readonly Counter<long> TelemetryRejected = Counter("akka_telemetry_rejected_total");
    private static readonly Counter<long> CommandsCreated = Counter("akka_commands_created_total");
    private static readonly Counter<long> CommandsStarted = Counter("akka_commands_started_total");
    private static readonly Counter<long> CommandsCompleted = Counter("akka_commands_completed_total");
    private static readonly Counter<long> CommandsFailed = Counter("akka_commands_failed_total");
    private static readonly Counter<long> CommandsCancelled = Counter("akka_commands_cancelled_total");
    private static readonly Counter<long> CommandReplays = Counter("akka_command_replays_total");
    private static readonly Counter<long> CommandDuplicates = Counter("akka_command_duplicates_total");
    private static readonly Counter<long> CommandRecoveryFailures = Counter("akka_command_recovery_failures_total");
    private static readonly Counter<long> JobsStarted = Counter("akka_jobs_started_total");
    private static readonly Counter<long> JobsCompleted = Counter("akka_jobs_completed_total");
    private static readonly Counter<long> JobsFailed = Counter("akka_jobs_failed_total");
    private static readonly Counter<long> JobsReplayed = Counter("akka_jobs_replayed_total");
    private static readonly Counter<long> TerminalSessionsOpened = Counter("akka_terminal_sessions_opened_total");
    private static readonly Counter<long> TerminalSessionsClosed = Counter("akka_terminal_sessions_closed_total");
    private static readonly Counter<long> TerminalFrames = Counter("akka_terminal_frames_total");
    private static readonly Counter<long> TerminalOutputDrops = Counter("akka_terminal_output_dropped_total");
    private static readonly Counter<long> TerminalTransportRegistrations = Counter("akka_terminal_transport_registrations_total");
    private static readonly Counter<long> TerminalTransportReconnects = Counter("akka_terminal_transport_reconnects_total");
    private static readonly Counter<long> TerminalStartReplays = Counter("akka_terminal_start_replays_total");
    private static readonly Counter<long> TerminalOpeningTimeouts = Counter("akka_terminal_opening_timeouts_total");
    private static readonly Counter<long> TerminalCompensatingCloses = Counter("akka_terminal_compensating_closes_total");
    private static readonly Counter<long> TerminalStalePtysRejected = Counter("akka_terminal_stale_ptys_rejected_total");
    private static readonly Counter<long> TerminalAuthorityEvents = Counter("akka_terminal_authority_events_total");
    private static readonly Counter<long> TerminalAuthorityFailures = Counter("akka_terminal_authority_failures_total");
    private static readonly Counter<long> RemoteSupportSessions = Counter("akka_remote_support_sessions_total");
    private static readonly Counter<long> RemoteSupportOffers = Counter("akka_remote_support_offers_total");
    private static readonly Counter<long> RemoteSupportAnswers = Counter("akka_remote_support_answers_total");
    private static readonly Counter<long> RemoteSupportIce = Counter("akka_remote_support_ice_total");
    private static readonly Counter<long> FileBrowserRequests = Counter("akka_filebrowser_requests_total");
    private static readonly Counter<long> FileBrowserCompleted = Counter("akka_filebrowser_completed_total");
    private static readonly Counter<long> FileBrowserFailed = Counter("akka_filebrowser_failed_total");
    private static readonly Counter<long> FileBrowserDropped = Counter("akka_filebrowser_dropped_total");
    private static readonly Counter<long> SignalRPublished = Counter("akka_signalr_publish_total");
    private static readonly Counter<long> SignalRDropped = Counter("akka_signalr_drop_total");
    private static readonly Counter<long> SignalRTimeouts = Counter("akka_signalr_timeout_total");
    private static readonly Counter<long> SignalRFailures = Counter("akka_signalr_failure_total");
    private static readonly Counter<long> AuthorityRequests = Counter("akka_authority_requests_total");
    private static readonly Counter<long> AuthorityFailures = Counter("akka_authority_failures_total");
    private static readonly Counter<long> AuthorityFallbacks = Counter("akka_authority_fallback_total");
    private static readonly Counter<long> PresenceAuthorityEvents = Counter("akka_presence_authority_events_total");
    private static readonly Counter<long> TelemetryAuthorityEvents = Counter("akka_telemetry_authority_events_total");
    private static readonly Counter<long> FileBrowserAuthorityEvents = Counter("akka_filebrowser_authority_events_total");
    private static readonly Counter<long> CommandsAuthorityEvents = Counter("akka_commands_authority_events_total");
    private static readonly Counter<long> CommandAuthorityEvents = Counter("akka_command_authority_events_total");
    private static readonly Counter<long> CommandAuthorityFailures = Counter("akka_command_authority_failures_total");
    private static readonly Counter<long> JobsAuthorityEvents = Counter("akka_jobs_authority_events_total");
    private static readonly Counter<long> JobsAuthorityFailures = Counter("akka_jobs_authority_failures_total");
    private static readonly Counter<long> JobsAuthorityCompleted = Counter("akka_jobs_authority_completed");
    private static readonly Counter<long> RemoteSupportAuthorityEvents = Counter("akka_remote_support_authority_events_total");
    private static readonly Counter<long> RemoteSupportAuthorityFailures = Counter("akka_remote_support_authority_failures_total");
    private static readonly Counter<long> SignalRAuthorityEvents = Counter("akka_signalr_authority_events_total");
    private static readonly Counter<long> SignalRAuthorityFailures = Counter("akka_signalr_authority_failures_total");
    private static readonly Counter<long> LogSessionsOpened = Counter("akka_log_sessions_opened_total");
    private static readonly Counter<long> LogSessionsClosed = Counter("akka_log_sessions_closed_total");
    private static readonly Counter<long> LogBatches = Counter("akka_log_batches_total");
    private static readonly Counter<long> LogRecords = Counter("akka_log_records_total");
    private static readonly Counter<long> LogBytes = Counter("akka_log_bytes_total");
    private static readonly Counter<long> LogDropped = Counter("akka_log_dropped_records_total");
    private static readonly Counter<long> LogResync = Counter("akka_log_resync_requests_total");
    private static readonly Counter<long> RemoteSupportSessionsCompleted = Counter("akka_remote_support_sessions_completed");

    private static long _presenceActiveClients;
    private static long _telemetryActiveClients;
    private static long _commandsActive;
    private static long _commandAuthorityActive;
    private static long _commandInboxDepth;
    private static long _commandOutboxDepth;
    private static long _jobsActive;
    private static long _jobsAuthorityActive;
    private static long _jobsAuthorityRunning;
    private static long _remoteSupportAuthorityActive;
    private static long _remoteSupportAuthoritySessionsActive;
    private static long _terminalActiveSessions;
    private static long _terminalActiveTransports;
    private static long _terminalOpenedSessions;
    private static long _terminalOpeningSessions;
    private static long _terminalSuspendedSessions;
    private static long _remoteSupportActiveSessions;
    private static long _fileBrowserIngressDepth;
    private static long _fileBrowserHighWatermark;
    private static long _signalRQueueDepth;
    private static long _signalRHighWatermark;
    private static long _signalRSubscriptions;
    private static long _signalRGroups;
    private static long _signalRMemberships;
    private static long _logActiveSessions;
    private static AuthorityPathState _authorityPathState = AuthorityPathState.Disabled;

    static NetRatelAkkaTelemetry()
    {
        Gauge("akka_presence_active_clients", () => _presenceActiveClients);
        Gauge("akka_telemetry_active_clients", () => _telemetryActiveClients);
        Gauge("akka_commands_active", () => _commandsActive);
        Gauge("akka_command_authority_active", () => _commandAuthorityActive);
        Gauge("akka_command_inbox_depth", () => _commandInboxDepth);
        Gauge("akka_command_outbox_depth", () => _commandOutboxDepth);
        Gauge("akka_jobs_active", () => _jobsActive);
        Gauge("akka_jobs_authority_active", () => _jobsAuthorityActive);
        Gauge("akka_jobs_authority_running", () => _jobsAuthorityRunning);
        Meter.CreateObservableGauge("akka_remote_support_authority_active", ObserveRemoteSupportAuthorityActive);
        Meter.CreateObservableGauge("akka_remote_support_sessions_active", ObserveRemoteSupportAuthoritySessions);
        Gauge("akka_terminal_active_sessions", () => _terminalActiveSessions);
        Gauge("akka_terminal_sessions_active", () => _terminalActiveSessions);
        Gauge("akka_terminal_active_transports", () => _terminalActiveTransports);
        Gauge("akka_terminal_opened_sessions", () => _terminalOpenedSessions);
        Gauge("akka_terminal_opening_sessions", () => _terminalOpeningSessions);
        Gauge("akka_terminal_suspended_sessions", () => _terminalSuspendedSessions);
        Gauge("akka_remote_support_active_sessions", () => _remoteSupportActiveSessions);
        Gauge("akka_filebrowser_ingress_depth", () => _fileBrowserIngressDepth);
        Gauge("akka_filebrowser_high_watermark", () => _fileBrowserHighWatermark);
        Gauge("akka_signalr_queue_depth", () => _signalRQueueDepth);
        Gauge("akka_signalr_high_watermark", () => _signalRHighWatermark);
        Gauge("akka_signalr_subscriptions", () => _signalRSubscriptions);
        Gauge("akka_signalr_groups", () => _signalRGroups);
        Gauge("akka_signalr_memberships", () => _signalRMemberships);
        Gauge("akka_log_active_sessions", () => _logActiveSessions);
        Meter.CreateObservableGauge(
            "akka_authority_active_paths",
            ObserveAuthorityPaths,
            description: "Whether each bounded migration feature currently has DEV Akka authority.");
    }

    public static Activity? StartActivity(
        string name,
        string surface,
        string? lifecycle = null,
        ActivityContext parentContext = default)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var activity = parentContext != default
            ? ActivitySource.StartActivity(name, ActivityKind.Internal, parentContext)
            : ActivitySource.StartActivity(name, ActivityKind.Internal);
        activity?.SetTag("netratel.akka.surface", surface);
        if (lifecycle is not null)
        {
            activity?.SetTag("netratel.akka.lifecycle", lifecycle);
        }

        return activity;
    }

    public static Activity? StartAuthorityActivity(
        string feature,
        string authority,
        string operation,
        bool fallbackUsed,
        string environment)
    {
        if (!ActivitySource.HasListeners())
        {
            return null;
        }

        var activity = ActivitySource.StartActivity($"akka.authority.{feature}", ActivityKind.Internal);
        activity?.SetTag("authority", authority);
        activity?.SetTag("migration_phase", "authority-cutover");
        activity?.SetTag("feature", feature);
        activity?.SetTag("fallback_used", fallbackUsed);
        activity?.SetTag("environment", NormalizeEnvironment(environment));
        activity?.SetTag("netratel.akka.operation", operation);
        return activity;
    }

    public static void PresenceClientConnected() => PresenceConnected.Add(1);
    public static void PresenceClientDisconnected() => PresenceDisconnected.Add(1);
    public static void PresenceClientHeartbeatExpired() => PresenceHeartbeatExpired.Add(1);
    public static void TelemetryAcceptedSnapshot() => TelemetryAccepted.Add(1);
    public static void TelemetryRejectedSnapshot() => TelemetryRejected.Add(1);
    public static void CommandCreated() => CommandsCreated.Add(1);
    public static void CommandStarted() => CommandsStarted.Add(1);
    public static void CommandCompleted() => CommandsCompleted.Add(1);
    public static void CommandFailed() => CommandsFailed.Add(1);
    public static void CommandCancelled() => CommandsCancelled.Add(1);
    public static void CommandReplayed() => CommandReplays.Add(1);
    public static void CommandDuplicateDetected() => CommandDuplicates.Add(1);
    public static void CommandRecoveryFailed() => CommandRecoveryFailures.Add(1);
    public static void JobStarted() => JobsStarted.Add(1);
    public static void JobCompleted() => JobsCompleted.Add(1);
    public static void JobFailed() => JobsFailed.Add(1);
    public static void JobReplayed() => JobsReplayed.Add(1);
    public static void TerminalSessionOpened() => TerminalSessionsOpened.Add(1);
    public static void TerminalSessionClosed() => TerminalSessionsClosed.Add(1);
    public static void TerminalFrameObserved() => TerminalFrames.Add(1);
    public static void TerminalOutputDropped() => TerminalOutputDrops.Add(1);
    public static void TerminalTransportRegistered() => TerminalTransportRegistrations.Add(1);
    public static void TerminalTransportReconnected() => TerminalTransportReconnects.Add(1);
    public static void TerminalStartReplayed() => TerminalStartReplays.Add(1);
    public static void TerminalOpeningTimedOut() => TerminalOpeningTimeouts.Add(1);
    public static void TerminalCompensatingCloseQueued() => TerminalCompensatingCloses.Add(1);
    public static void TerminalStalePtyRejected() => TerminalStalePtysRejected.Add(1);
    public static void RemoteSupportSessionObserved() => RemoteSupportSessions.Add(1);
    public static void RemoteSupportOfferObserved() => RemoteSupportOffers.Add(1);
    public static void RemoteSupportAnswerObserved() => RemoteSupportAnswers.Add(1);
    public static void RemoteSupportIceObserved() => RemoteSupportIce.Add(1);
    public static void FileBrowserRequestObserved() => FileBrowserRequests.Add(1);
    public static void FileBrowserRequestCompleted() => FileBrowserCompleted.Add(1);
    public static void FileBrowserRequestFailed() => FileBrowserFailed.Add(1);
    public static void FileBrowserIngressDropped() => FileBrowserDropped.Add(1);
    public static void LogSessionOpened()
    {
        LogSessionsOpened.Add(1);
        Interlocked.Increment(ref _logActiveSessions);
    }

    public static void LogSessionClosed()
    {
        LogSessionsClosed.Add(1);
        DecrementNonNegative(ref _logActiveSessions);
    }

    public static void LogBatchAccepted(int records, int bytes, ulong droppedRecords, bool resyncRequired)
    {
        LogBatches.Add(1);
        LogRecords.Add(records);
        LogBytes.Add(bytes);
        if (droppedRecords > 0) LogDropped.Add(checked((long)Math.Min(droppedRecords, (ulong)long.MaxValue)));
        if (resyncRequired) LogResync.Add(1);
    }
    public static void RecordSignalRPublished()
    {
        SignalRPublished.Add(1);
        var state = Volatile.Read(ref _authorityPathState);
        if (state.SignalRActive)
        {
            SignalRAuthorityEvents.Add(1, AuthorityTags("signalr", "akka", fallbackUsed: false, state.Environment));
        }
    }
    public static void RecordSignalRDropped() => SignalRDropped.Add(1);
    public static void RecordSignalRTimedOut()
    {
        SignalRTimeouts.Add(1);
        RecordSignalRAuthorityFailureIfActive();
    }
    public static void RecordSignalRFailed()
    {
        SignalRFailures.Add(1);
        RecordSignalRAuthorityFailureIfActive();
    }

    public static void RecordAuthorityRequest(
        string feature,
        string authority,
        bool fallbackUsed,
        string environment) =>
        AuthorityRequests.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));

    public static void RecordAuthorityFailure(
        string feature,
        string authority,
        bool fallbackUsed,
        string environment)
    {
        AuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        if (feature == "commands")
        {
            CommandAuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        }
        else if (feature == "jobs")
        {
            JobsAuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        }
        else if (feature == "remote-support")
        {
            RemoteSupportAuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        }
        else if (feature == "terminal")
        {
            TerminalAuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        }
        else if (feature == "signalr")
        {
            SignalRAuthorityFailures.Add(1, AuthorityTags(feature, authority, fallbackUsed, environment));
        }
    }

    public static void RecordAuthorityFallback(string feature, string authority, string environment) =>
        AuthorityFallbacks.Add(1, AuthorityTags(feature, authority, fallbackUsed: true, environment));

    public static void RecordAuthorityEvent(
        string feature,
        string authority,
        bool fallbackUsed,
        string environment)
    {
        var tags = AuthorityTags(feature, authority, fallbackUsed, environment);
        switch (feature)
        {
            case "presence":
                PresenceAuthorityEvents.Add(1, tags);
                break;
            case "telemetry":
                TelemetryAuthorityEvents.Add(1, tags);
                break;
            case "file-browser":
                FileBrowserAuthorityEvents.Add(1, tags);
                break;
            case "commands":
                CommandsAuthorityEvents.Add(1, tags);
                CommandAuthorityEvents.Add(1, tags);
                break;
            case "jobs":
                JobsAuthorityEvents.Add(1, tags);
                break;
            case "remote-support":
                RemoteSupportAuthorityEvents.Add(1, tags);
                break;
            case "terminal":
                TerminalAuthorityEvents.Add(1, tags);
                break;
            case "signalr":
                SignalRAuthorityEvents.Add(1, tags);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(feature), feature, "Unknown authority feature.");
        }
    }

    public static void ConfigureAuthorityPaths(
        bool presenceActive,
        bool telemetryActive,
        bool fileBrowseActive,
        bool commandsActive,
        bool jobsActive,
        bool remoteSupportActive,
        bool terminalActive,
        bool signalRActive,
        string environment)
    {
        Volatile.Write(
            ref _authorityPathState,
            new AuthorityPathState(
                presenceActive,
                telemetryActive,
                fileBrowseActive,
                commandsActive,
                jobsActive,
                remoteSupportActive,
                terminalActive,
                signalRActive,
                NormalizeEnvironment(environment)));
        Set(ref _commandAuthorityActive, commandsActive ? 1 : 0);
        Set(ref _jobsAuthorityActive, jobsActive ? 1 : 0);
        Set(ref _remoteSupportAuthorityActive, remoteSupportActive ? 1 : 0);
    }

    public static void SetPresenceActiveClients(long value) => Set(ref _presenceActiveClients, value);
    public static void SetTelemetryActiveClients(long value) => Set(ref _telemetryActiveClients, value);
    public static void SetCommandsActive(long value) => Set(ref _commandsActive, value);
    public static void SetCommandPersistenceDepths(long inbox, long outbox)
    {
        Set(ref _commandInboxDepth, inbox);
        Set(ref _commandOutboxDepth, outbox);
    }

    public static void SetJobsActive(long value) => Set(ref _jobsActive, value);
    public static void SetJobsAuthorityRunning(long value) => Set(ref _jobsAuthorityRunning, value);
    public static void JobAuthorityCompleted(string authority, bool fallbackUsed, string environment) =>
        JobsAuthorityCompleted.Add(1, AuthorityTags("jobs", authority, fallbackUsed, environment));
    public static void RemoteSupportAuthoritySessionCompleted(string authority, bool fallbackUsed, string environment) =>
        RemoteSupportSessionsCompleted.Add(1, AuthorityTags("remote-support", authority, fallbackUsed, environment));
    public static void SetRemoteSupportAuthoritySessionsActive(long value) =>
        Set(ref _remoteSupportAuthoritySessionsActive, value);
    public static void SetTerminalActiveSessions(long value) => Set(ref _terminalActiveSessions, value);
    public static void SetTerminalActiveTransports(long value) => Set(ref _terminalActiveTransports, value);
    public static void SetTerminalLifecycleSessions(long opened, long opening, long suspended)
    {
        Set(ref _terminalOpenedSessions, opened);
        Set(ref _terminalOpeningSessions, opening);
        Set(ref _terminalSuspendedSessions, suspended);
    }
    public static void SetRemoteSupportActiveSessions(long value) => Set(ref _remoteSupportActiveSessions, value);
    public static void SetFileBrowserIngress(long depth, long highWatermark)
    {
        Set(ref _fileBrowserIngressDepth, depth);
        Set(ref _fileBrowserHighWatermark, highWatermark);
    }

    public static void SetSignalRStatus(
        long queueDepth,
        long highWatermark,
        long subscriptions,
        long groups,
        long memberships)
    {
        Set(ref _signalRQueueDepth, queueDepth);
        Set(ref _signalRHighWatermark, highWatermark);
        Set(ref _signalRSubscriptions, subscriptions);
        Set(ref _signalRGroups, groups);
        Set(ref _signalRMemberships, memberships);
    }

    public static void SetSignalRQueue(long queueDepth, long highWatermark)
    {
        Set(ref _signalRQueueDepth, queueDepth);
        Set(ref _signalRHighWatermark, highWatermark);
    }

    public static void SetSignalRSubscriptions(long subscriptions, long groups, long memberships)
    {
        Set(ref _signalRSubscriptions, subscriptions);
        Set(ref _signalRGroups, groups);
        Set(ref _signalRMemberships, memberships);
    }

    private static Counter<long> Counter(string name) => Meter.CreateCounter<long>(name);

    private static void RecordSignalRAuthorityFailureIfActive()
    {
        var state = Volatile.Read(ref _authorityPathState);
        if (state.SignalRActive)
        {
            SignalRAuthorityFailures.Add(1, AuthorityTags("signalr", "akka", fallbackUsed: false, state.Environment));
        }
    }

    private static void Gauge(string name, Func<long> value) =>
        Meter.CreateObservableGauge(name, () => Math.Max(0, value()));

    private static void Set(ref long location, long value) => Interlocked.Exchange(ref location, Math.Max(0, value));

    private static void DecrementNonNegative(ref long location)
    {
        while (true)
        {
            var current = Volatile.Read(ref location);
            if (current <= 0 || Interlocked.CompareExchange(ref location, current - 1, current) == current) return;
        }
    }

    private static IEnumerable<Measurement<long>> ObserveAuthorityPaths()
    {
        var state = Volatile.Read(ref _authorityPathState);
        yield return AuthorityPathMeasurement("presence", state.PresenceActive, state.Environment);
        yield return AuthorityPathMeasurement("telemetry", state.TelemetryActive, state.Environment);
        yield return AuthorityPathMeasurement("file-browser", state.FileBrowseActive, state.Environment);
        yield return AuthorityPathMeasurement("commands", state.CommandsActive, state.Environment);
        yield return AuthorityPathMeasurement("jobs", state.JobsActive, state.Environment);
        yield return AuthorityPathMeasurement("remote-support", state.RemoteSupportActive, state.Environment);
        yield return AuthorityPathMeasurement("terminal", state.TerminalActive, state.Environment);
        yield return AuthorityPathMeasurement("signalr", state.SignalRActive, state.Environment);
    }

    private static IEnumerable<Measurement<long>> ObserveRemoteSupportAuthorityActive()
    {
        var state = Volatile.Read(ref _authorityPathState);
        yield return new Measurement<long>(
            Math.Max(0, Volatile.Read(ref _remoteSupportAuthorityActive)),
            AuthorityTags("remote-support", state.RemoteSupportActive ? "akka" : "unavailable", fallbackUsed: false, state.Environment));
    }

    private static IEnumerable<Measurement<long>> ObserveRemoteSupportAuthoritySessions()
    {
        var state = Volatile.Read(ref _authorityPathState);
        yield return new Measurement<long>(
            Math.Max(0, Volatile.Read(ref _remoteSupportAuthoritySessionsActive)),
            AuthorityTags("remote-support", state.RemoteSupportActive ? "akka" : "unavailable", fallbackUsed: false, state.Environment));
    }

    private static Measurement<long> AuthorityPathMeasurement(string feature, bool active, string environment)
    {
        var authority = active ? "akka" : "unavailable";
        return new Measurement<long>(
            active ? 1 : 0,
            AuthorityTags(feature, authority, fallbackUsed: false, environment));
    }

    private static TagList AuthorityTags(
        string feature,
        string authority,
        bool fallbackUsed,
        string environment) =>
        new()
        {
            { "authority", authority },
            { "migration_phase", "authority-cutover" },
            { "feature", feature },
            { "fallback_used", fallbackUsed ? "true" : "false" },
            { "environment", NormalizeEnvironment(environment) }
        };

    private static string NormalizeEnvironment(string environment) =>
        string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)
            ? "dev"
            : string.IsNullOrWhiteSpace(environment)
                ? "unknown"
                : environment.ToLowerInvariant();

    private sealed record AuthorityPathState(
        bool PresenceActive,
        bool TelemetryActive,
        bool FileBrowseActive,
        bool CommandsActive,
        bool JobsActive,
        bool RemoteSupportActive,
        bool TerminalActive,
        bool SignalRActive,
        string Environment)
    {
        public static AuthorityPathState Disabled { get; } = new(false, false, false, false, false, false, false, false, "unknown");
    }
}
