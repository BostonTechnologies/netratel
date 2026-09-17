using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using NetRatel.Akka.Observability;
using NetRatel.Application.Commands;

namespace NetRatel.Akka.Commands;

/// <summary>
/// Local-only Phase 3 command region. It isolates command entities and records
/// bounded scalar diagnostics without acquiring command dispatch authority.
/// </summary>
public sealed class ClientCommandRouterActor : ReceiveActor
{
    private readonly ICommandPersistenceStore? _persistenceStore;
    private readonly DateTimeOffset _startedAtUtc = DateTimeOffset.UtcNow;
    private int _activeCommands;
    private ulong _completedCommands;
    private ulong _failedCommands;
    private ulong _invalidTransitions;
    private ulong _staleEvents;

    public ClientCommandRouterActor() : this(null)
    {
    }

    public ClientCommandRouterActor(ICommandPersistenceStore? persistenceStore)
    {
        _persistenceStore = persistenceStore;
        Receive<RecordCommandLifecycleEvent>(message =>
            GetOrCreateCommandActor(message.Command).Tell(new RoutedCommandRecord(message, Sender)));
        Receive<GetCommandShadowState>(message =>
        {
            var commandActor = GetCommandActor(message.Command);
            if (commandActor.IsNobody())
            {
                Sender.Tell(CommandActor.EmptyState(message.Command));
            }
            else
            {
                commandActor.Forward(message);
            }
        });
        Receive<RoutedCommandResult>(HandleResult);
        Receive<ProbeClientCommandRoute>(_ => Sender.Tell(CreateStatus()));
    }

    public static Props Props() =>
        global::Akka.Actor.Props.Create(() => new ClientCommandRouterActor());

    public static Props Props(ICommandPersistenceStore persistenceStore) =>
        global::Akka.Actor.Props.Create(() => new ClientCommandRouterActor(persistenceStore));

    private IActorRef GetOrCreateCommandActor(CommandKey command)
    {
        var existing = GetCommandActor(command);
        if (!existing.IsNobody())
        {
            return existing;
        }

        using var activity = NetRatelAkkaTelemetry.StartActivity("akka.actor.create", "command");
        return Context.ActorOf(
            _persistenceStore is null
                ? CommandActor.Props(command)
                : CommandActor.Props(command, _persistenceStore),
            CreateActorName(command.EntityId));
    }

    private IActorRef GetCommandActor(CommandKey command) =>
        Context.Child(CreateActorName(command.EntityId));

    private void HandleResult(RoutedCommandResult message)
    {
        switch (message.Result.Disposition)
        {
            case CommandMessageDisposition.Accepted:
                UpdateCommandCounts(message.PreviousStatus, message.CurrentStatus);
                break;
            case CommandMessageDisposition.InvalidTransition:
                _invalidTransitions = IncrementSaturating(_invalidTransitions);
                break;
            case CommandMessageDisposition.StaleEvent:
                _staleEvents = IncrementSaturating(_staleEvents);
                break;
        }

        message.ReplyTo.Tell(message.Result);
    }

    private void UpdateCommandCounts(
        CommandLifecycleStatus? previousStatus,
        CommandLifecycleStatus? currentStatus)
    {
        if (!previousStatus.HasValue && IsActive(currentStatus))
        {
            if (_activeCommands < int.MaxValue)
            {
                _activeCommands++;
            }
        }
        else if (IsActive(previousStatus) && IsTerminal(currentStatus))
        {
            if (_activeCommands > 0)
            {
                _activeCommands--;
            }
        }

        if (currentStatus == CommandLifecycleStatus.Completed)
        {
            NetRatelAkkaTelemetry.CommandCompleted();
            _completedCommands = IncrementSaturating(_completedCommands);
        }
        else if (currentStatus == CommandLifecycleStatus.Failed)
        {
            NetRatelAkkaTelemetry.CommandFailed();
            _failedCommands = IncrementSaturating(_failedCommands);
        }

        switch (currentStatus)
        {
            case CommandLifecycleStatus.Created:
                NetRatelAkkaTelemetry.CommandCreated();
                break;
            case CommandLifecycleStatus.Started:
                NetRatelAkkaTelemetry.CommandStarted();
                break;
            case CommandLifecycleStatus.Cancelled:
                NetRatelAkkaTelemetry.CommandCancelled();
                break;
        }

        NetRatelAkkaTelemetry.SetCommandsActive(_activeCommands);
        using var activity = NetRatelAkkaTelemetry.StartActivity(
            "akka.command.lifecycle", "command", currentStatus?.ToString());
    }

    private ClientCommandRouteStatus CreateStatus() =>
        new(
            _activeCommands,
            _completedCommands,
            _failedCommands,
            _invalidTransitions,
            _staleEvents,
            _startedAtUtc,
            "local-shadow",
            "unavailable");

    private static bool IsActive(CommandLifecycleStatus? status) =>
        status is CommandLifecycleStatus.Created or
            CommandLifecycleStatus.Dispatched or
            CommandLifecycleStatus.Accepted or
            CommandLifecycleStatus.Started;

    private static bool IsTerminal(CommandLifecycleStatus? status) =>
        status is CommandLifecycleStatus.Completed or
            CommandLifecycleStatus.Failed or
            CommandLifecycleStatus.Cancelled;

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private static string CreateActorName(string entityId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(entityId));
        return $"command-{Convert.ToHexString(digest).ToLowerInvariant()}";
    }
}
