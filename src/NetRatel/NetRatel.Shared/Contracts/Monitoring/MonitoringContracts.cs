using System.Collections.Immutable;
using System.Text.Json.Serialization;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.Shared.Contracts.Monitoring;

public enum MonitoringMetricKind { CpuUsagePercent, DiskFreePercent, DiskFreeSpace, ServiceExpectedState }
public enum MonitoringNumericUnit { Percent, Bytes, KiB, MiB, GiB }
public enum MonitoringTargetMode { AllEligible, Selected }
public enum MonitoringSeverity { Information, Warning, Critical }
public enum MonitoringPhase { Healthy, Pending, Firing, Recovering, Resolved, Cleared, Suspended, NotApplicable }
public enum MonitoringEvidenceQuality { Unknown, Fresh }
public enum MonitoringClassification { Unknown, Breach, Recovery, Neutral }
public enum MonitoringFlowDispatchDisposition { NoFlowSelected, Suppressed, Enqueued, Completed, Failed, DeliveryUnknown, Cancelled }
public enum MonitoringFlowOutcomeKind { Succeeded, Skipped, Failed, DeliveryUnknown }
public enum MonitoringClosureDisposition { Recovered, ManuallyCleared, RuleDisabled, TargetRemoved, ConfigurationChanged }
public enum MonitoringConditionResetPolicy { SuspendOccurrenceAndRequireNewWindow }
public enum MonitoringEventKind { AlertRaised, AlertResolved, AlertCleared, AlertSuspended, AlertAcknowledged }
public enum MonitoringEvaluationDisposition { Accepted, DuplicateOrStaleCursor, WrongSeries, RuleDisabled, TargetNotApplicable }

public sealed record MonitoringTargetSelectionDto(
    MonitoringTargetMode Mode, ImmutableArray<Guid> AgentIds, ImmutableArray<Guid> GroupIds);

public sealed record MonitoringGroupDto(
    int TenantId, Guid GroupId, ulong Revision, string Name, ImmutableArray<Guid> AgentIds);

/// <summary>Only the phase-one metric catalogue is allowed; service conditions have no command payload.</summary>
public sealed record MonitoringConditionDto(
    MonitoringMetricKind Kind,
    MonitoringNumericUnit? Unit,
    double? BreachThreshold,
    double? RecoveryThreshold,
    string? ResourceName,
    ClientServicePlatform? ServicePlatform,
    ImmutableArray<ClientServiceState> ExpectedServiceStates);

/// <summary>EvaluationRevision changes only for scope/condition edits, which require an explicit reset operation.</summary>
public sealed record MonitoringRuleDto(
    int TenantId, Guid RuleId, ulong Revision, ulong EvaluationRevision, string Name, bool Enabled,
    MonitoringSeverity Severity, MonitoringTargetSelectionDto Targets, MonitoringConditionDto Condition,
    TimeSpan BreachHold, TimeSpan RecoveryHold, TimeSpan FreshnessBudget,
    Guid? PublishedFlowVersionId = null, string? ExecutionPrincipalId = null, string? ExecutionCredentialId = null);

public sealed record MonitoringSeriesKey(int TenantId, Guid RuleId, Guid AgentId, string ResourceKey);

/// <summary>Supplied only after the existing authenticated telemetry admission/sequence checks succeed.</summary>
public sealed record MonitoringAcceptedCursor(long ConnectionEpoch, ulong Sequence);

/// <summary>One immutable physical per-disk collection; its identity does not order collections.</summary>
public sealed record MonitoringDiskCollectionStamp(Guid CollectionId, DateTimeOffset CollectedAtUtc, string PayloadFingerprint);

/// <summary>CPU/percent values are percentages; absolute disk values and resolution are canonical bytes.</summary>
public sealed record MonitoringObservationDto(
    MonitoringSeriesKey Series, MonitoringAcceptedCursor Cursor, Guid EvidenceStreamId,
    DateTimeOffset ObservedAtUtc, DateTimeOffset ReceivedAtUtc,
    bool Complete, bool Supported, double? NumericValue = null, double NumericResolution = 0,
    ClientServiceState? ServiceState = null, bool AuthoritativeMissing = false,
    ulong? ServiceWatchPolicyRevision = null, ulong? CurrentServiceWatchPolicyRevision = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitoringDiskCollectionStamp? DiskCollection = null);

public sealed record MonitoringEvidenceDto(
    MonitoringAcceptedCursor Cursor, Guid EvidenceStreamId, DateTimeOffset ObservedAtUtc, DateTimeOffset ReceivedAtUtc,
    MonitoringEvidenceQuality Quality, MonitoringClassification Classification,
    double? NumericValue, double NumericResolution, ClientServiceState? ServiceState,
    string? UnknownReason = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitoringDiskCollectionStamp? DiskCollection = null);

/// <summary>An immutable occurrence keeps its original condition/action version even after a rename or action edit.</summary>
public sealed record MonitoringOccurrenceDto(
    Guid OccurrenceId, Guid RaisedEventId, DateTimeOffset RaisedAtUtc, DateTimeOffset BreachSinceAtUtc,
    MonitoringRuleDto PinnedRule, MonitoringEvidenceDto RaisedEvidence,
    MonitoringFlowDispatchDisposition FlowDispatchDisposition,
    Guid? AcknowledgedBy = null, DateTimeOffset? AcknowledgedAtUtc = null,
    DateTimeOffset? EndedAtUtc = null, MonitoringClosureDisposition? ClosureDisposition = null,
    MonitoringFlowOutcomeDto? FlowOutcome = null, MonitoringClientIdentityDto? ClientIdentity = null);

/// <summary>Condition-false is Skipped; action-node keys and receipts are derived by the pinned flow runtime.</summary>
public sealed record MonitoringIncidentReceiptDto(string IncidentId, string? TrackingNumber = null, string? IncidentUrl = null);
public sealed record MonitoringFlowOutcomeDto(Guid? FlowRunId, MonitoringFlowOutcomeKind Outcome, DateTimeOffset OccurredAtUtc,
    string? Code = null, MonitoringIncidentReceiptDto? Receipt = null);

public sealed record MonitoringSeriesState(
    MonitoringSeriesKey Series, ulong StateRevision, ulong EvaluationRevision,
    MonitoringPhase Phase, MonitoringEvidenceQuality EvidenceQuality,
    MonitoringAcceptedCursor? Cursor = null, MonitoringEvidenceDto? LatestEvidence = null,
    DateTimeOffset? WindowStartedAtUtc = null, DateTimeOffset? PreviousQualifyingReceivedAtUtc = null,
    MonitoringOccurrenceDto? Occurrence = null,
    DateTimeOffset? NotBeforeObservedAtUtc = null, DateTimeOffset? NotBeforeReceivedAtUtc = null,
    bool Suppressed = false, ImmutableArray<Guid> ApplicableBypassIds = default,
    string EvaluationFingerprint = "", Guid? EvidenceStreamId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] MonitoringDiskCollectionStamp? LastDiskCollection = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? WindowStartedObservedAtUtc = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] DateTimeOffset? LastDiskTransportReceivedAtUtc = null, ulong OperatorRevision = 0);

public sealed record MonitoringBypassDto(
    Guid BypassId, int TenantId, Guid? RuleId, Guid? AgentId, string? ResourceKey,
    Guid OperatorId, string Reason, DateTimeOffset StartsAtUtc, DateTimeOffset? ExpiresAtUtc, Guid? GroupId = null);

public sealed record MonitoringEventIntent(
    Guid EventId, MonitoringEventKind Kind, MonitoringSeriesKey Series, Guid OccurrenceId,
    DateTimeOffset AtUtc, MonitoringRuleDto PinnedRule, MonitoringEvidenceDto Evidence,
    MonitoringClosureDisposition? ClosureDisposition = null, string? Reason = null, Guid? OperatorId = null,
    string? OperatorDisplayName = null, MonitoringClientIdentityDto? ClientIdentity = null);

/// <summary>Persist this exact intent with state+event; dispatch later rechecks current authorization/suppression.</summary>
public sealed record MonitoringOutboxIntent(
    string StableFlowDispatchKey, Guid EventId, Guid OccurrenceId, MonitoringSeriesKey Series,
    Guid PublishedFlowVersionId, DateTimeOffset AtUtc,
    MonitoringRuleDto PinnedRule, MonitoringEvidenceDto PinnedEvidence);

public sealed record MonitoringAuditIntent(
    Guid AuditId, MonitoringSeriesKey Series, string Operation, Guid OperatorId, string Reason,
    DateTimeOffset AtUtc, Guid? OccurrenceId, ulong RuleRevision, Guid? BypassId = null, string? OperatorDisplayName = null);

/// <summary>One transaction must compare ExpectedStateRevision and commit State+Events+Outbox+Audits atomically.</summary>
public sealed record MonitoringEvaluationResult(
    MonitoringEvaluationDisposition Disposition, ulong ExpectedStateRevision, MonitoringSeriesState State,
    ImmutableArray<MonitoringEventIntent> Events, ImmutableArray<MonitoringOutboxIntent> Outbox,
    ImmutableArray<MonitoringAuditIntent> Audits);

public static class MonitoringLimits
{
    public const string NotificationEventPrefix = "NetRatel.Monitoring.";
    public const int MaximumRulesPerTenant = 256;
    public const int MaximumTargetClients = 4096;
    public const int MaximumGroupsPerTenant = 128;
    public const int MaximumNameLength = 128;
    public const int MaximumExecutionIdentityLength = 256;
    public const int MaximumReasonLength = 512;
    public const int MaximumResourceKeyLength = 512;
    public const int MaximumBypassesPerEvaluation = 128;
    public const int MaximumSeriesPerClient = 1024;
    public const int MaximumRowsPerRead = 200;
    public const int MaximumOutboxClaims = 50;
    public const int MaximumOutboxAttempts = 8;
    public const int MaximumPendingOutboxPerTenant = 4096;
    public const int MaximumRetainedEventsPerTenant = 100000;
    public const int MaximumRetainedAuditsPerTenant = 100000;
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
    public static readonly TimeSpan MaximumOutboxLease = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan MinimumHold = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumHold = TimeSpan.FromHours(1);
    public static readonly TimeSpan MinimumFreshness = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaximumFreshness = TimeSpan.FromMinutes(15);
}
