using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed class MonitoringTenantConfigurationRecord
{
    public int TenantId { get; set; }
    public ulong Revision { get; set; }
}

public sealed class MonitoringRuleRecord
{
    public int TenantId { get; set; }
    public Guid RuleId { get; set; }
    public ulong Revision { get; set; }
    public string DefinitionJson { get; set; } = "{}";
}

public sealed class MonitoringGroupRecord
{
    public int TenantId { get; set; }
    public Guid GroupId { get; set; }
    public ulong Revision { get; set; }
    public string DefinitionJson { get; set; } = "{}";
}

public sealed class MonitoringBypassRecord
{
    public int TenantId { get; set; }
    public Guid BypassId { get; set; }
    public DateTimeOffset? ExpiresAtUtc { get; set; }
    public string DefinitionJson { get; set; } = "{}";
}

/// <summary>Server registration boundary; replacing/ending a stream prevents its old worker from committing or claiming actions.</summary>
public sealed class MonitoringEvidenceStreamRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid ConnectionId { get; set; }
    public long ConnectionEpoch { get; set; }
    public Guid EvidenceStreamId { get; set; }
    public long CommittedRegistrationOrdinal { get; set; }
    public bool Active { get; set; }
    public DateTimeOffset RegisteredAtUtc { get; set; }
    public ulong Revision { get; set; }
}

public sealed class MonitoringSeriesRecord
{
    public int TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid AgentId { get; set; }
    public string ResourceKey { get; set; } = "";
    public ulong StateRevision { get; set; }
    public MonitoringPhase Phase { get; set; }
    public MonitoringEvidenceQuality EvidenceQuality { get; set; }
    public Guid? ActiveOccurrenceId { get; set; }
    public Guid? LatestOccurrenceId { get; set; }
    public bool Acknowledged { get; set; }
    public bool Suppressed { get; set; }
    public string StateJson { get; set; } = "{}";
    public DateTimeOffset UpdatedAtUtc { get; set; }
}

public sealed class MonitoringOccurrenceRecord
{
    public Guid OccurrenceId { get; set; }
    public int TenantId { get; set; }
    public Guid RuleId { get; set; }
    public Guid AgentId { get; set; }
    public string ResourceKey { get; set; } = "";
    public Guid RaisedEventId { get; set; }
    public DateTimeOffset RaisedAtUtc { get; set; }
    public DateTimeOffset? EndedAtUtc { get; set; }
    public string OccurrenceJson { get; set; } = "{}";
}

/// <summary>Authoritative immutable history, independent of notification display/read/delete state.</summary>
public sealed class MonitoringEventRecord
{
    public Guid EventId { get; set; }
    public int TenantId { get; set; }
    public Guid OccurrenceId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string EventJson { get; set; } = "{}";
}

public sealed class MonitoringFlowOutboxRecord
{
    public Guid OutboxId { get; set; }
    public int TenantId { get; set; }
    public Guid OccurrenceId { get; set; }
    public Guid EventId { get; set; }
    public string StableFlowDispatchKey { get; set; } = "";
    public string IntentJson { get; set; } = "{}";
    public MonitoringOutboxStatus Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset? NextAttemptAtUtc { get; set; }
    public int Attempts { get; set; }
    public Guid? WorkerId { get; set; }
    public Guid? LeaseId { get; set; }
    public long LeaseFence { get; set; }
    public DateTimeOffset? LeaseExpiresAtUtc { get; set; }
    public string? OutcomeJson { get; set; }
    public string? Code { get; set; }
    public Guid? FlowRunId { get; set; }
    public DateTimeOffset? HandedOffAtUtc { get; set; }
}

public sealed class MonitoringAuditRecord
{
    public Guid AuditId { get; set; }
    public int TenantId { get; set; }
    public string EntityKind { get; set; } = "";
    public Guid EntityId { get; set; }
    public string Operation { get; set; } = "";
    public Guid OperatorId { get; set; }
    public string Reason { get; set; } = "";
    public DateTimeOffset AtUtc { get; set; }
    public ulong ConfigurationRevision { get; set; }
    public string DetailsJson { get; set; } = "{}";
}

/// <summary>Reservations order retries across replicas without replacing active evidence.</summary>
public sealed class MonitoringEvidenceRegistrationCounterRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public long LastIssuedOrdinal { get; set; }
}
public sealed class MonitoringEvidenceRegistrationAttemptRecord
{
    public int TenantId { get; set; }
    public Guid AgentId { get; set; }
    public Guid RegistrationId { get; set; }
    public Guid ConnectionId { get; set; }
    public long ConnectionEpoch { get; set; }
    public long RegistrationOrdinal { get; set; }
    public short Status { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public DateTimeOffset ExpiresAtUtc { get; set; }
    public DateTimeOffset RetainUntilUtc { get; set; }
}
