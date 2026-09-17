using Akka.Actor;
using Akka.Event;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;

namespace NetRatel.Akka.Commands;

/// <summary>
/// Owns one command lifecycle and persists accepted transitions before applying
/// them. Shadow and authoritative streams retain separate identity; dispatch
/// remains with the gateway command authority.
/// </summary>
public sealed class CommandActor : ReceiveActor
{
    private readonly CommandKey _command;
    private readonly ICommandPersistenceStore? _persistenceStore;
    private readonly ILoggingAdapter _logger;
    private readonly CancellationTokenSource _stopping = new();
    private readonly List<CommandHistoryEntry> _history = new(5);
    private ClientKey? _client;
    private string? _correlationId;
    private DateTimeOffset? _requestTimestamp;
    private CommandLifecycleStatus? _currentStatus;
    private ulong _lastAcceptedVersion;
    private ulong _lastAcceptedSequence;
    private bool _isAuthoritative;
    private bool _recovered;

    public CommandActor(CommandKey command) : this(command, null)
    {
    }

    public CommandActor(CommandKey command, ICommandPersistenceStore? persistenceStore)
    {
        if (!command.IsValid)
        {
            throw new ArgumentException("A positive tenant ID and non-empty command ID are required.", nameof(command));
        }

        _command = command;
        _persistenceStore = persistenceStore;
        _logger = Context.GetLogger();
        _recovered = persistenceStore is null;
        ReceiveAsync<RecordCommandLifecycleEvent>(async message =>
        {
            var replyTo = Sender;
            replyTo.Tell(await RecordAsync(message).ConfigureAwait(false));
        });
        ReceiveAsync<RoutedCommandRecord>(async message =>
        {
            // ReceiveAsync continuations do not retain an active ActorContext.
            // Capture the parent before the first await so the durable replay path
            // can complete without touching Context after resumption.
            var parent = Context.Parent;
            await EnsureRecoveredAsync().ConfigureAwait(false);
            var previousStatus = _currentStatus;
            var result = await RecordAsync(message.Message).ConfigureAwait(false);
            parent.Tell(new RoutedCommandResult(
                result,
                message.ReplyTo,
                previousStatus,
                result.Disposition == CommandMessageDisposition.Accepted
                    ? result.CurrentStatus
                    : previousStatus));
        });
        ReceiveAsync<GetCommandShadowState>(async message =>
        {
            var replyTo = Sender;
            try
            {
                await EnsureRecoveredAsync().ConfigureAwait(false);
                replyTo.Tell(message.Command == _command ? CreateState() : EmptyState(message.Command));
            }
            catch (Exception) when (!_stopping.IsCancellationRequested)
            {
                replyTo.Tell(EmptyState(message.Command, "akka-shadow-persistence-unavailable"));
            }
        });
    }

    public static Props Props(CommandKey command) =>
        global::Akka.Actor.Props.Create(() => new CommandActor(command));

    public static Props Props(CommandKey command, ICommandPersistenceStore persistenceStore) =>
        global::Akka.Actor.Props.Create(() => new CommandActor(command, persistenceStore));

    protected override void PostStop()
    {
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    private async Task<CommandMessageResult> RecordAsync(RecordCommandLifecycleEvent message)
    {
        try
        {
            await EnsureRecoveredAsync().ConfigureAwait(false);
            var lifecycleEvent = message.Event;
            var disposition = GetDisposition(lifecycleEvent);
            if (disposition == CommandMessageDisposition.Accepted && lifecycleEvent.IsAuthoritative)
            {
                // The gateway supplies server receipt time. Preserve logical
                // chronology if the server clock steps backwards, without
                // changing the request identity or version/sequence authority.
                var lowerBound = _history.Count > 0 ? _history[^1].StatusTimestamp : lifecycleEvent.RequestTimestamp;
                if (lowerBound < lifecycleEvent.RequestTimestamp) lowerBound = lifecycleEvent.RequestTimestamp;
                if (lifecycleEvent.StatusTimestamp < lowerBound)
                    lifecycleEvent = lifecycleEvent with { StatusTimestamp = lowerBound };
            }
            if (_persistenceStore is not null &&
                disposition is CommandMessageDisposition.Accepted or CommandMessageDisposition.Duplicate)
            {
                var persistenceResult = await _persistenceStore
                    .RecordAsync(lifecycleEvent, _stopping.Token)
                    .ConfigureAwait(false);
                if (persistenceResult.Disposition == CommandPersistenceWriteDisposition.Duplicate &&
                    disposition == CommandMessageDisposition.Accepted)
                {
                    NetRatelAkkaTelemetry.CommandDuplicateDetected();
                    ResetState();
                    _recovered = false;
                    await EnsureRecoveredAsync().ConfigureAwait(false);
                    disposition = CommandMessageDisposition.Duplicate;
                }
            }

            if (disposition == CommandMessageDisposition.Accepted)
            {
                Apply(lifecycleEvent);
            }

            return CreateResult(disposition);
        }
        catch (Exception exception) when (!_stopping.IsCancellationRequested)
        {
            _logger.Error(exception, "Command lifecycle persistence failed for {0} at {1}.", _command, message.Event.Status);
            NetRatelAkkaTelemetry.CommandRecoveryFailed();
            return CreateResult(CommandMessageDisposition.PersistenceUnavailable);
        }
    }

    private CommandMessageDisposition GetDisposition(CommandLifecycleEvent lifecycleEvent)
    {
        if (lifecycleEvent.Command != _command ||
            (_client.HasValue && lifecycleEvent.IsAuthoritative != _isAuthoritative) ||
            (_client.HasValue && lifecycleEvent.Client != _client.Value) ||
            (_correlationId is not null &&
             !string.Equals(lifecycleEvent.CorrelationId, _correlationId, StringComparison.Ordinal)) ||
            (_requestTimestamp.HasValue && !HaveSamePersistedTimestamp(lifecycleEvent.RequestTimestamp, _requestTimestamp.Value)))
        {
            return CommandMessageDisposition.IdentityMismatch;
        }

        if (_currentStatus.HasValue &&
            lifecycleEvent.Version == _lastAcceptedVersion &&
            lifecycleEvent.Sequence == _lastAcceptedSequence &&
            lifecycleEvent.Status == _currentStatus.Value)
        {
            return CommandMessageDisposition.Duplicate;
        }

        if (_currentStatus.HasValue &&
            (lifecycleEvent.Version <= _lastAcceptedVersion ||
             lifecycleEvent.Sequence <= _lastAcceptedSequence))
        {
            return CommandMessageDisposition.StaleEvent;
        }

        return CommandLifecycleRules.IsValidTransition(_currentStatus, lifecycleEvent.Status)
            ? CommandMessageDisposition.Accepted
            : CommandMessageDisposition.InvalidTransition;
    }

    private static bool HaveSamePersistedTimestamp(DateTimeOffset left, DateTimeOffset right) =>
        left.UtcDateTime.Ticks / TimeSpan.TicksPerMicrosecond ==
        right.UtcDateTime.Ticks / TimeSpan.TicksPerMicrosecond;

    private async Task EnsureRecoveredAsync()
    {
        if (_recovered || _persistenceStore is null)
        {
            return;
        }

        var replay = await _persistenceStore.ReplayAsync(_command, _stopping.Token)
            .ConfigureAwait(false);
        NetRatelAkkaTelemetry.CommandReplayed();
        ResetState();
        try
        {
            foreach (var lifecycleEvent in replay)
            {
                if (GetDisposition(lifecycleEvent) != CommandMessageDisposition.Accepted)
                {
                    throw new InvalidOperationException(
                        $"Durable command history for '{_command}' contains an invalid transition.");
                }

                Apply(lifecycleEvent);
            }
        }
        catch
        {
            ResetState();
            throw;
        }

        _recovered = true;
        _persistenceStore.RecordRecoverySucceeded();
    }

    private void Apply(CommandLifecycleEvent lifecycleEvent)
    {
        _client ??= lifecycleEvent.Client;
        _isAuthoritative = lifecycleEvent.IsAuthoritative;
        _correlationId ??= lifecycleEvent.CorrelationId;
        _requestTimestamp ??= lifecycleEvent.RequestTimestamp;
        _currentStatus = lifecycleEvent.Status;
        _lastAcceptedVersion = lifecycleEvent.Version;
        _lastAcceptedSequence = lifecycleEvent.Sequence;
        if (_history.Count == 5)
        {
            _history.RemoveAt(0);
        }

        _history.Add(new(
            lifecycleEvent.Status,
            lifecycleEvent.StatusTimestamp,
            lifecycleEvent.Version,
            lifecycleEvent.Sequence));
    }

    private CommandMessageResult CreateResult(CommandMessageDisposition disposition) =>
        new(
            _command,
            disposition,
            _currentStatus,
            _lastAcceptedVersion,
            _lastAcceptedSequence,
            _history.Count > 0 ? _history[^1].StatusTimestamp : null);

    private void ResetState()
    {
        _client = null;
        _correlationId = null;
        _requestTimestamp = null;
        _currentStatus = null;
        _lastAcceptedVersion = 0;
        _lastAcceptedSequence = 0;
        _history.Clear();
        _isAuthoritative = false;
    }

    private CommandShadowState CreateState() =>
        new(
            _command,
            _client,
            _correlationId,
            _requestTimestamp,
            _currentStatus,
            _lastAcceptedVersion,
            _lastAcceptedSequence,
            _history.ToArray(),
            _isAuthoritative ? "akka" : "akka-shadow",
            IsAuthoritative: _isAuthoritative);

    internal static CommandShadowState EmptyState(
        CommandKey command,
        string source = "akka-shadow") =>
        new(
            command,
            null,
            null,
            null,
            null,
            0,
            0,
            [],
            source,
            IsAuthoritative: false);
}

internal sealed record RoutedCommandRecord(
    RecordCommandLifecycleEvent Message,
    IActorRef ReplyTo);

internal sealed record RoutedCommandResult(
    CommandMessageResult Result,
    IActorRef ReplyTo,
    CommandLifecycleStatus? PreviousStatus,
    CommandLifecycleStatus? CurrentStatus);
