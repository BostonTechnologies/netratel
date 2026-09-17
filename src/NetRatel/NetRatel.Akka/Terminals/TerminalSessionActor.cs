using Akka.Actor;
using NetRatel.Application.Terminals;

namespace NetRatel.Akka.Terminals;

/// <summary>
/// Models bounded metadata for one terminal session. It never receives raw PTY
/// bytes and has no terminal transport, browser, execution, or persistence authority.
/// </summary>
public sealed class TerminalSessionActor : ReceiveActor
{
    private readonly TerminalShadowSessionKey _session;
    private readonly TerminalShadowRetentionPolicy _retention;
    private readonly Queue<TerminalShadowMetadataEntry> _recentMetadata;
    private readonly Dictionary<TerminalShadowSequenceStream, SequenceFence> _sequenceFences = [];
    private TerminalShadowSessionStatus? _status;
    private int _retainedByteCount;
    private ulong _droppedMetadataEvents;
    private ulong _oversizeFrames;
    private ulong _rejectedStaleEvents;
    private ulong _sequenceConflicts;
    private ulong _invalidTransitions;
    private ulong _invalidObservations;
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

    public TerminalSessionActor(TerminalShadowSessionKey session)
        : this(session, TerminalShadowRetentionPolicy.Default)
    {
    }

    public TerminalSessionActor(
        TerminalShadowSessionKey session,
        TerminalShadowRetentionPolicy retention)
    {
        if (!session.IsValid)
        {
            throw new ArgumentException(
                "A positive tenant ID, client ID, terminal session ID, and concrete transport are required.",
                nameof(session));
        }

        ArgumentNullException.ThrowIfNull(retention);
        if (!retention.IsValid)
        {
            throw new ArgumentException("Terminal shadow retention bounds are invalid.", nameof(retention));
        }

        _session = session;
        _retention = retention;
        _recentMetadata = new Queue<TerminalShadowMetadataEntry>(retention.MaxRetainedMetadata);

        Receive<RecordTerminalShadowEvent>(message => Sender.Tell(Record(message)));
        Receive<RoutedTerminalRecord>(message =>
        {
            var previousStatus = _status;
            var previousRetainedCount = _recentMetadata.Count;
            var previousDropped = _droppedMetadataEvents;
            const ulong previousCoalesced = 0;
            var result = Record(message.Message);
            Context.Parent.Tell(new RoutedTerminalResult(
                message.Message.Event,
                result,
                message.ReplyTo,
                previousStatus,
                previousRetainedCount,
                previousDropped,
                previousCoalesced));
        });
        Receive<GetTerminalShadowState>(message =>
            Sender.Tell(message.Session == _session
                ? CreateState()
                : EmptyState(message.Session)));
    }

    public static Props Props(TerminalShadowSessionKey session) =>
        global::Akka.Actor.Props.Create(() => new TerminalSessionActor(session));

    public static Props Props(
        TerminalShadowSessionKey session,
        TerminalShadowRetentionPolicy retention) =>
        global::Akka.Actor.Props.Create(() => new TerminalSessionActor(session, retention));

    private TerminalShadowMessageResult Record(RecordTerminalShadowEvent message)
    {
        var observation = message.Event;
        var disposition = GetDisposition(observation);
        switch (disposition)
        {
            case TerminalShadowMessageDisposition.Accepted:
                Apply(observation);
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
        }

        return CreateResult(disposition);
    }

    private TerminalShadowMessageDisposition GetDisposition(TerminalShadowEvent observation)
    {
        if (observation.Session != _session)
        {
            return TerminalShadowMessageDisposition.IdentityMismatch;
        }

        if (observation.IsAuthoritative || !IsStructurallyValid(observation))
        {
            return TerminalShadowMessageDisposition.InvalidObservation;
        }

        if (IsLifecycle(observation.Kind))
        {
            if (IsDuplicateLifecycle(observation.Kind))
            {
                return TerminalShadowMessageDisposition.Duplicate;
            }

            return IsValidLifecycleTransition(observation.Kind)
                ? TerminalShadowMessageDisposition.Accepted
                : TerminalShadowMessageDisposition.InvalidTransition;
        }

        if (_status != TerminalShadowSessionStatus.Opened)
        {
            return TerminalShadowMessageDisposition.InvalidTransition;
        }

        var sequenceStream = GetSequenceStream(observation.StreamType);
        if (!_sequenceFences.TryGetValue(sequenceStream, out var fence))
        {
            return TerminalShadowMessageDisposition.Accepted;
        }

        var sequence = observation.Sequence!.Value;
        if (sequence < fence.Sequence)
        {
            return TerminalShadowMessageDisposition.StaleSequence;
        }

        if (sequence > fence.Sequence)
        {
            return TerminalShadowMessageDisposition.Accepted;
        }

        return fence.Fingerprint == CreateFingerprint(observation)
            ? TerminalShadowMessageDisposition.Duplicate
            : TerminalShadowMessageDisposition.SequenceConflict;
    }

    private static bool IsStructurallyValid(TerminalShadowEvent observation)
    {
        if (observation.PayloadLength < 0)
        {
            return false;
        }

        return observation.Kind switch
        {
            TerminalShadowEventKind.SessionRequested or
                TerminalShadowEventKind.SessionOpened or
                TerminalShadowEventKind.SessionCloseRequested or
                TerminalShadowEventKind.SessionClosed or
                TerminalShadowEventKind.SessionFailed =>
                observation.StreamType == TerminalShadowStreamType.Lifecycle &&
                observation.Sequence is null &&
                observation.PayloadLength == 0 &&
                observation.Columns is null &&
                observation.Rows is null,
            TerminalShadowEventKind.InputObserved =>
                observation.StreamType == TerminalShadowStreamType.StandardInput &&
                observation.Sequence is > 0 &&
                observation.Columns is null &&
                observation.Rows is null,
            TerminalShadowEventKind.OutputObserved =>
                observation.StreamType is TerminalShadowStreamType.StandardOutput or
                    TerminalShadowStreamType.StandardError &&
                observation.Sequence is > 0 &&
                observation.Columns is null &&
                observation.Rows is null,
            TerminalShadowEventKind.ResizeObserved =>
                observation.StreamType == TerminalShadowStreamType.Resize &&
                observation.Sequence is > 0 &&
                observation.PayloadLength == 0 &&
                observation.Columns is > 0 &&
                observation.Rows is > 0,
            _ => false
        };
    }

    private bool IsDuplicateLifecycle(TerminalShadowEventKind kind) =>
        (kind, _status) switch
        {
            (TerminalShadowEventKind.SessionRequested, TerminalShadowSessionStatus.Requested) => true,
            (TerminalShadowEventKind.SessionOpened, TerminalShadowSessionStatus.Opened) => true,
            (TerminalShadowEventKind.SessionCloseRequested, TerminalShadowSessionStatus.Closing) => true,
            (TerminalShadowEventKind.SessionClosed, TerminalShadowSessionStatus.Closed) => true,
            (TerminalShadowEventKind.SessionFailed, TerminalShadowSessionStatus.Failed) => true,
            _ => false
        };

    private bool IsValidLifecycleTransition(TerminalShadowEventKind kind) =>
        (_status, kind) switch
        {
            (null, TerminalShadowEventKind.SessionRequested) => true,
            (TerminalShadowSessionStatus.Requested, TerminalShadowEventKind.SessionOpened) => true,
            (TerminalShadowSessionStatus.Requested, TerminalShadowEventKind.SessionCloseRequested) => true,
            (TerminalShadowSessionStatus.Requested, TerminalShadowEventKind.SessionClosed) => true,
            (TerminalShadowSessionStatus.Requested, TerminalShadowEventKind.SessionFailed) => true,
            (TerminalShadowSessionStatus.Opened, TerminalShadowEventKind.SessionCloseRequested) => true,
            (TerminalShadowSessionStatus.Opened, TerminalShadowEventKind.SessionClosed) => true,
            (TerminalShadowSessionStatus.Opened, TerminalShadowEventKind.SessionFailed) => true,
            (TerminalShadowSessionStatus.Closing, TerminalShadowEventKind.SessionClosed) => true,
            (TerminalShadowSessionStatus.Closing, TerminalShadowEventKind.SessionFailed) => true,
            _ => false
        };

    private void Apply(TerminalShadowEvent observation)
    {
        switch (observation.Kind)
        {
            case TerminalShadowEventKind.SessionRequested:
                _status = TerminalShadowSessionStatus.Requested;
                _requestedCount = IncrementSaturating(_requestedCount);
                break;
            case TerminalShadowEventKind.SessionOpened:
                _status = TerminalShadowSessionStatus.Opened;
                _openedCount = IncrementSaturating(_openedCount);
                break;
            case TerminalShadowEventKind.SessionCloseRequested:
                _status = TerminalShadowSessionStatus.Closing;
                _closeRequestedCount = IncrementSaturating(_closeRequestedCount);
                break;
            case TerminalShadowEventKind.SessionClosed:
                _status = TerminalShadowSessionStatus.Closed;
                _closedCount = IncrementSaturating(_closedCount);
                break;
            case TerminalShadowEventKind.SessionFailed:
                _status = TerminalShadowSessionStatus.Failed;
                _failedCount = IncrementSaturating(_failedCount);
                break;
            case TerminalShadowEventKind.InputObserved:
                ApplySequence(observation);
                _inputFrames = IncrementSaturating(_inputFrames);
                _inputBytes = AddSaturating(_inputBytes, observation.PayloadLength);
                break;
            case TerminalShadowEventKind.OutputObserved:
                ApplySequence(observation);
                _outputFrames = IncrementSaturating(_outputFrames);
                _outputBytes = AddSaturating(_outputBytes, observation.PayloadLength);
                break;
            case TerminalShadowEventKind.ResizeObserved:
                ApplySequence(observation);
                _resizeEvents = IncrementSaturating(_resizeEvents);
                break;
        }

        RetainMetadata(observation);
    }

    private void ApplySequence(TerminalShadowEvent observation)
    {
        var sequence = observation.Sequence!.Value;
        _sequenceFences[GetSequenceStream(observation.StreamType)] = new(
            sequence,
            CreateFingerprint(observation));
        _maximumObservedSequence = Math.Max(_maximumObservedSequence, sequence);
    }

    private void RetainMetadata(TerminalShadowEvent observation)
    {
        if (!IsLifecycle(observation.Kind) &&
            observation.PayloadLength > _retention.MaxObservedFrameBytes)
        {
            _oversizeFrames = IncrementSaturating(_oversizeFrames);
            _droppedMetadataEvents = IncrementSaturating(_droppedMetadataEvents);
            return;
        }

        while (_recentMetadata.Count >= _retention.MaxRetainedMetadata ||
               _retainedByteCount + observation.PayloadLength > _retention.MaxRetainedByteCount)
        {
            var removed = _recentMetadata.Dequeue();
            _retainedByteCount -= removed.PayloadLength;
            _droppedMetadataEvents = IncrementSaturating(_droppedMetadataEvents);
        }

        _recentMetadata.Enqueue(new(
            observation.Kind,
            observation.StreamType,
            observation.Timestamp,
            observation.Sequence,
            observation.PayloadLength,
            observation.Columns,
            observation.Rows,
            observation.CloseKind));
        _retainedByteCount += observation.PayloadLength;
    }

    private TerminalShadowMessageResult CreateResult(TerminalShadowMessageDisposition disposition) =>
        new(
            _session,
            disposition,
            _status,
            _maximumObservedSequence,
            _recentMetadata.Count,
            _retainedByteCount,
            _droppedMetadataEvents,
            CoalescedMetadataEvents: 0);

    private TerminalShadowState CreateState() =>
        new(
            _session,
            _status,
            _sequenceFences
                .OrderBy(static pair => pair.Key)
                .Select(static pair => new TerminalShadowStreamSequence(pair.Key, pair.Value.Sequence))
                .ToArray(),
            _recentMetadata.ToArray(),
            _retainedByteCount,
            _droppedMetadataEvents,
            CoalescedMetadataEvents: 0,
            _oversizeFrames,
            _rejectedStaleEvents,
            _sequenceConflicts,
            _invalidTransitions,
            _invalidObservations,
            _inputFrames,
            _outputFrames,
            _resizeEvents,
            _inputBytes,
            _outputBytes,
            _maximumObservedSequence,
            CreateLifecycleCounts(),
            "api-terminal-observer",
            IsAuthoritative: false);

    internal static TerminalShadowState EmptyState(TerminalShadowSessionKey session) =>
        new(
            session,
            null,
            [],
            [],
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            new(0, 0, 0, 0, 0),
            "api-terminal-observer",
            IsAuthoritative: false);

    private TerminalShadowLifecycleCounts CreateLifecycleCounts() =>
        new(_requestedCount, _openedCount, _closeRequestedCount, _closedCount, _failedCount);

    private static bool IsLifecycle(TerminalShadowEventKind kind) =>
        kind is TerminalShadowEventKind.SessionRequested or
            TerminalShadowEventKind.SessionOpened or
            TerminalShadowEventKind.SessionCloseRequested or
            TerminalShadowEventKind.SessionClosed or
            TerminalShadowEventKind.SessionFailed;

    private static TerminalShadowSequenceStream GetSequenceStream(TerminalShadowStreamType stream) =>
        stream switch
        {
            TerminalShadowStreamType.StandardInput => TerminalShadowSequenceStream.Input,
            TerminalShadowStreamType.StandardOutput or TerminalShadowStreamType.StandardError =>
                TerminalShadowSequenceStream.Output,
            TerminalShadowStreamType.Resize => TerminalShadowSequenceStream.Resize,
            _ => throw new ArgumentOutOfRangeException(nameof(stream), stream, null)
        };

    private static SequenceFingerprint CreateFingerprint(TerminalShadowEvent observation) =>
        new(
            observation.Kind,
            observation.StreamType,
            observation.PayloadLength,
            observation.Columns,
            observation.Rows);

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static ulong AddSaturating(ulong value, int increment)
    {
        var unsignedIncrement = checked((ulong)increment);
        return ulong.MaxValue - value < unsignedIncrement ? ulong.MaxValue : value + unsignedIncrement;
    }

    private sealed record SequenceFence(ulong Sequence, SequenceFingerprint Fingerprint);

    private readonly record struct SequenceFingerprint(
        TerminalShadowEventKind Kind,
        TerminalShadowStreamType StreamType,
        int PayloadLength,
        int? Columns,
        int? Rows);
}

internal sealed record RoutedTerminalRecord(
    RecordTerminalShadowEvent Message,
    IActorRef ReplyTo);

internal sealed record RoutedTerminalResult(
    TerminalShadowEvent Event,
    TerminalShadowMessageResult Result,
    IActorRef ReplyTo,
    TerminalShadowSessionStatus? PreviousStatus,
    int PreviousRetainedCount,
    ulong PreviousDroppedMetadataEvents,
    ulong PreviousCoalescedMetadataEvents);
