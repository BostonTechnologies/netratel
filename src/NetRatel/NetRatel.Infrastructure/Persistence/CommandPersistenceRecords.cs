using NetRatel.Application.Commands;

namespace NetRatel.Infrastructure.Persistence;

public sealed class CommandInboxReceipt
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public decimal Version { get; set; }
    public decimal Sequence { get; set; }
    public DateTimeOffset FirstReceivedAtUtc { get; set; }
    public DateTimeOffset LastReceivedAtUtc { get; set; }
    public long DuplicateCount { get; set; }
}

public sealed class CommandIntentEventRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset RequestTimestamp { get; set; }
    public DateTimeOffset StatusTimestamp { get; set; }
    public decimal Version { get; set; }
    public decimal Sequence { get; set; }
    public CommandLifecycleStatus Status { get; set; }
    public string Source { get; set; } = "akka-shadow";
    public bool IsAuthoritative { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}

public sealed class CommandOutboxRecord
{
    public Guid Id { get; set; }
    public int TenantId { get; set; }
    public Guid ClientId { get; set; }
    public string CommandId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset RequestTimestamp { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset LastObservedAtUtc { get; set; }
    public DateTimeOffset? TerminalAtUtc { get; set; }
    public decimal LastAcceptedVersion { get; set; }
    public decimal LastAcceptedSequence { get; set; }
    public CommandLifecycleStatus CurrentStatus { get; set; }
    public int ObservedDispatchCount { get; set; }
    public string Mode { get; set; } = "shadow-only";
    public bool IsAuthoritative { get; set; }
}
