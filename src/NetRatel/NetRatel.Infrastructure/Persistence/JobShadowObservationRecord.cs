using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;

namespace NetRatel.Infrastructure.Persistence;

public sealed class JobShadowObservationRecord
{
    public Guid Id { get; set; }
    public string SourceSystem { get; set; } = string.Empty;
    public long SourceEventId { get; set; }
    public decimal JobRunId { get; set; }
    public decimal JobId { get; set; }
    public int? TenantId { get; set; }
    public string ClientIdentity { get; set; } = string.Empty;
    public JobShadowObservationKind Kind { get; set; }
    public string? StartedBy { get; set; }
    public JobRunState? RunStatus { get; set; }
    public int? CurrentStepOrdinal { get; set; }
    public DateTimeOffset? RunCreatedAtUtc { get; set; }
    public decimal? JobStepRunId { get; set; }
    public decimal? JobStepId { get; set; }
    public JobStepRunState? StepStatus { get; set; }
    public int? StepOrdinal { get; set; }
    public string? TaskRequestId { get; set; }
    public JobCommandCorrelationStatus CommandCorrelationStatus { get; set; }
    public CommandLifecycleStatus? CorrelatedCommandStatus { get; set; }
    public DateTimeOffset? StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public DateTimeOffset ObservedAtUtc { get; set; }
    public bool IsAuthoritative { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
}
