using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Akka.Observability;
using NetRatel.Application.Terminals;

namespace NetRatel.Akka.Terminals;

/// <summary>
/// Local-only terminal shadow region. Each child owns one session's bounded
/// metadata and the router owns scalar diagnostics only.
/// </summary>
public sealed class ClientTerminalRouterActor : ReceiveActor
{
    private readonly TerminalShadowRetentionPolicy _retention;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private int _activeTerminalSessions;
    private ulong _closedTerminalSessions;
    private ulong _failedTerminalSessions;
    private ulong _acceptedEvents;
    private ulong _duplicateEvents;
    private ulong _rejectedStaleEvents;
    private ulong _sequenceConflicts;
    private ulong _invalidTransitions;
    private ulong _invalidObservations;
    private ulong _identityConflicts;
    private ulong _droppedMetadataEvents;
    private ulong _coalescedMetadataEvents;
    private int _retainedMetadataCount;
    private ulong _inputFrames;
    private ulong _outputFrames;
    private ulong _resizeEvents;
    private ulong _inputBytes;
    private ulong _outputBytes;
    private ulong _maximumObservedSequence;
    private ulong _requestedCount;
    private ulong _openedCount;
    private ulong _closeRequestedCount;
    private ulong _closedCount;
    private ulong _failedCount;

    public ClientTerminalRouterActor()
        : this(TerminalShadowRetentionPolicy.Default)
    {
    }

    public ClientTerminalRouterActor(TerminalShadowRetentionPolicy retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        if (!retention.IsValid)
        {
            throw new ArgumentException("Terminal shadow retention bounds are invalid.", nameof(retention));
        }

        _retention = retention;
        Receive<RecordTerminalShadowEvent>(message =>
        {
            if (!message.Event.Session.IsValid)
            {
                _invalidObservations = IncrementSaturating(_invalidObservations);
                Sender.Tell(CreateInvalidSessionResult(message.Event.Session));
                return;
            }

            GetOrCreateSessionActor(message.Event.Session)
                .Tell(new RoutedTerminalRecord(message, Sender));
        });
        Receive<GetTerminalShadowState>(message =>
        {
            var actor = GetSessionActor(message.Session);
            if (actor.IsNobody())
            {
                Sender.Tell(TerminalSessionActor.EmptyState(message.Session));
            }
            else
            {
                actor.Forward(message);
            }
        });
        Receive<RoutedTerminalResult>(HandleResult);
        Receive<ProbeTerminalShadowRoute>(_ => Sender.Tell(CreateStatus()));
    }

    public static Props Props() =>
        global::Akka.Actor.Props.Create(() => new ClientTerminalRouterActor());

    public static Props Props(TerminalShadowRetentionPolicy retention) =>
        global::Akka.Actor.Props.Create(() => new ClientTerminalRouterActor(retention));

    private IActorRef GetOrCreateSessionActor(TerminalShadowSessionKey session)
    {
        var existing = GetSessionActor(session);
        if (!existing.IsNobody())
        {
            return existing;
        }

        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.actor.create", "terminal");
        return Context.ActorOf(TerminalSessionActor.Props(session, _retention), CreateActorName(session.EntityId));
    }

    private IActorRef GetSessionActor(TerminalShadowSessionKey session) =>
        Context.Child(CreateActorName(session.EntityId));

    private void HandleResult(RoutedTerminalResult message)
    {
        var result = message.Result;
        switch (result.Disposition)
        {
            case TerminalShadowMessageDisposition.Accepted:
                _acceptedEvents = IncrementSaturating(_acceptedEvents);
                ApplyAccepted(message);
                break;
            case TerminalShadowMessageDisposition.Duplicate:
                _duplicateEvents = IncrementSaturating(_duplicateEvents);
                break;
            case TerminalShadowMessageDisposition.StaleSequence:
                _rejectedStaleEvents = IncrementSaturating(_rejectedStaleEvents);
                break;
            case TerminalShadowMessageDisposition.SequenceConflict:
                _sequenceConflicts = IncrementSaturating(_sequenceConflicts);
                break;
            case TerminalShadowMessageDisposition.InvalidTransition:
                _invalidTransitions = IncrementSaturating(_invalidTransitions);
                break;
            case TerminalShadowMessageDisposition.InvalidObservation:
                _invalidObservations = IncrementSaturating(_invalidObservations);
                break;
            case TerminalShadowMessageDisposition.IdentityMismatch:
                _identityConflicts = IncrementSaturating(_identityConflicts);
                break;
        }

        message.ReplyTo.Tell(result);
    }

    private void ApplyAccepted(RoutedTerminalResult message)
    {
        using var activity = NetRatelAkkaTelemetry.StartActivity(
            "akka.terminal.lifecycle", "terminal", message.Event.Kind.ToString());
        UpdateSessionCounts(message.PreviousStatus, message.Result.CurrentStatus);
        _retainedMetadataCount = Math.Max(
            0,
            _retainedMetadataCount + message.Result.RetainedMetadataCount - message.PreviousRetainedCount);
        _droppedMetadataEvents = AddSaturating(
            _droppedMetadataEvents,
            message.Result.DroppedMetadataEvents - message.PreviousDroppedMetadataEvents);
        _coalescedMetadataEvents = AddSaturating(
            _coalescedMetadataEvents,
            message.Result.CoalescedMetadataEvents - message.PreviousCoalescedMetadataEvents);
        _maximumObservedSequence = Math.Max(_maximumObservedSequence, message.Result.MaximumObservedSequence);

        switch (message.Event.Kind)
        {
            case TerminalShadowEventKind.SessionRequested:
                _requestedCount = IncrementSaturating(_requestedCount);
                break;
            case TerminalShadowEventKind.SessionOpened:
                NetRatelAkkaTelemetry.TerminalSessionOpened();
                _openedCount = IncrementSaturating(_openedCount);
                break;
            case TerminalShadowEventKind.SessionCloseRequested:
                _closeRequestedCount = IncrementSaturating(_closeRequestedCount);
                break;
            case TerminalShadowEventKind.SessionClosed:
                NetRatelAkkaTelemetry.TerminalSessionClosed();
                _closedCount = IncrementSaturating(_closedCount);
                break;
            case TerminalShadowEventKind.SessionFailed:
                _failedCount = IncrementSaturating(_failedCount);
                break;
            case TerminalShadowEventKind.InputObserved:
                NetRatelAkkaTelemetry.TerminalFrameObserved();
                _inputFrames = IncrementSaturating(_inputFrames);
                _inputBytes = AddSaturating(_inputBytes, checked((ulong)message.Event.PayloadLength));
                break;
            case TerminalShadowEventKind.OutputObserved:
                NetRatelAkkaTelemetry.TerminalFrameObserved();
                _outputFrames = IncrementSaturating(_outputFrames);
                _outputBytes = AddSaturating(_outputBytes, checked((ulong)message.Event.PayloadLength));
                break;
            case TerminalShadowEventKind.ResizeObserved:
                NetRatelAkkaTelemetry.TerminalFrameObserved();
                _resizeEvents = IncrementSaturating(_resizeEvents);
                break;
        }
    }

    private void UpdateSessionCounts(
        TerminalShadowSessionStatus? previous,
        TerminalShadowSessionStatus? current)
    {
        if (!previous.HasValue && IsActive(current))
        {
            if (_activeTerminalSessions < int.MaxValue)
            {
                _activeTerminalSessions++;
            }
        }

        else if (IsActive(previous) && IsTerminal(current))
        {
            if (_activeTerminalSessions > 0)
            {
                _activeTerminalSessions--;
            }

            if (current == TerminalShadowSessionStatus.Closed)
            {
                _closedTerminalSessions = IncrementSaturating(_closedTerminalSessions);
            }
            else
            {
                _failedTerminalSessions = IncrementSaturating(_failedTerminalSessions);
            }
        }

        NetRatelAkkaTelemetry.SetTerminalActiveSessions(_activeTerminalSessions);
    }

    private TerminalShadowRouteStatus CreateStatus() =>
        new(
            _activeTerminalSessions,
            _closedTerminalSessions,
            _failedTerminalSessions,
            Context.GetChildren().Count(),
            _acceptedEvents,
            _duplicateEvents,
            _rejectedStaleEvents,
            _sequenceConflicts,
            _invalidTransitions,
            _invalidObservations,
            _identityConflicts,
            _droppedMetadataEvents,
            _coalescedMetadataEvents,
            _retainedMetadataCount,
            _inputFrames,
            _outputFrames,
            _resizeEvents,
            _inputBytes,
            _outputBytes,
            _maximumObservedSequence,
            new(
                _requestedCount,
                _openedCount,
                _closeRequestedCount,
                _closedCount,
                _failedCount),
            _startedAtUtc,
            "local-shadow",
            "unavailable");

    private static TerminalShadowMessageResult CreateInvalidSessionResult(
        TerminalShadowSessionKey session) =>
        new(
            session,
            TerminalShadowMessageDisposition.InvalidObservation,
            CurrentStatus: null,
            MaximumObservedSequence: 0,
            RetainedMetadataCount: 0,
            RetainedByteCount: 0,
            DroppedMetadataEvents: 0,
            CoalescedMetadataEvents: 0);

    private static bool IsActive(TerminalShadowSessionStatus? status) =>
        status is TerminalShadowSessionStatus.Requested or
            TerminalShadowSessionStatus.Opened or
            TerminalShadowSessionStatus.Closing;

    private static bool IsTerminal(TerminalShadowSessionStatus? status) =>
        status is TerminalShadowSessionStatus.Closed or TerminalShadowSessionStatus.Failed;

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static ulong AddSaturating(ulong value, ulong increment) =>
        ulong.MaxValue - value < increment ? ulong.MaxValue : value + increment;

    private static string CreateActorName(string entityId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(entityId));
        return $"terminal-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
