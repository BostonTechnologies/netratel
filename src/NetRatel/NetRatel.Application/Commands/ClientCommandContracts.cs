using NetRatel.Application.Presence;

namespace NetRatel.Application.Commands;

public readonly record struct CommandKey(int TenantId, string CommandId)
{
    public bool IsValid => TenantId > 0 && !string.IsNullOrWhiteSpace(CommandId);

    public string EntityId => $"{TenantId}:{CommandId}";

    public override string ToString() => EntityId;
}

public enum CommandLifecycleStatus
{
    Created = 0,
    Dispatched = 1,
    Accepted = 2,
    Started = 3,
    Completed = 4,
    Failed = 5,
    Cancelled = 6
}

public enum CommandMessageDisposition
{
    Accepted = 0,
    Duplicate = 1,
    StaleEvent = 2,
    InvalidTransition = 3,
    IdentityMismatch = 4,
    PersistenceUnavailable = 5
}

public sealed record CommandLifecycleEvent(
    ClientKey Client,
    string CommandId,
    string CorrelationId,
    DateTimeOffset RequestTimestamp,
    DateTimeOffset StatusTimestamp,
    ulong Version,
    ulong Sequence,
    CommandLifecycleStatus Status,
    string Source = "akka-shadow",
    bool IsAuthoritative = false)
{
    public CommandKey Command => new(Client.TenantId, CommandId);
}

public interface IClientCommandMessage
{
    CommandKey Command { get; }
}

public sealed record RecordCommandLifecycleEvent(CommandLifecycleEvent Event) : IClientCommandMessage
{
    public CommandKey Command => Event.Command;
}

public sealed record GetCommandShadowState(CommandKey Command) : IClientCommandMessage;

public sealed record CommandMessageResult(
    CommandKey Command,
    CommandMessageDisposition Disposition,
    CommandLifecycleStatus? CurrentStatus,
    ulong LastAcceptedVersion,
    ulong LastAcceptedSequence,
    DateTimeOffset? CurrentStatusTimestamp = null);

public sealed record CommandHistoryEntry(
    CommandLifecycleStatus Status,
    DateTimeOffset StatusTimestamp,
    ulong Version,
    ulong Sequence);

public sealed record CommandShadowState(
    CommandKey Command,
    ClientKey? Client,
    string? CorrelationId,
    DateTimeOffset? RequestTimestamp,
    CommandLifecycleStatus? CurrentStatus,
    ulong LastAcceptedVersion,
    ulong LastAcceptedSequence,
    IReadOnlyList<CommandHistoryEntry> History,
    string Source,
    bool IsAuthoritative);

public sealed record ProbeClientCommandRoute;

public sealed record ClientCommandRouteStatus(
    int ActiveCommands,
    ulong CompletedCommands,
    ulong FailedCommands,
    ulong InvalidTransitions,
    ulong StaleEvents,
    DateTimeOffset StartedAtUtc,
    string Mode,
    string Authority);

public interface IClientCommandRouter
{
    Task<CommandMessageResult> RecordAsync(
        RecordCommandLifecycleEvent message,
        CancellationToken cancellationToken);

    Task<CommandShadowState> GetStateAsync(
        CommandKey command,
        CancellationToken cancellationToken);

    Task<ClientCommandRouteStatus> ProbeAsync(CancellationToken cancellationToken);
}
