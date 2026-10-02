namespace NetRatel.Shared.Contracts.Flows;

public enum FlowNodeKind { AlertRaised = 1, Condition = 2, MapIncident = 3, CreateIncident = 4 }
public enum FlowValueField { NumericValue = 1, Severity = 2, Metric = 3, Resource = 4, RuleName = 5, ClientName = 6, ServiceState = 7 }
public enum FlowComparison { Equal = 1, NotEqual = 2, GreaterThan = 3, GreaterThanOrEqual = 4, LessThan = 5, LessThanOrEqual = 6 }
public enum FlowRunStatus { Queued = 1, Running = 2, RetryWaiting = 3, Succeeded = 4, Skipped = 5, Failed = 6, DeliveryUnknown = 7 }
public enum FlowActionStatus { Pending = 1, Dispatching = 2, RetryWaiting = 3, Succeeded = 4, Failed = 5, DeliveryUnknown = 6 }

public sealed record FlowPositionDto(double X, double Y);
public sealed record FlowViewportDto(double Scale = 1, double ScrollX = 0, double ScrollY = 0,
    double NegativeX = 0, double NegativeY = 0, double PositiveX = 0, double PositiveY = 0);
public sealed record FlowConditionDto(FlowValueField Field, FlowComparison Comparison, double? NumericValue = null, string? TextValue = null);
public sealed record FlowIncidentMappingDto(string TitleTemplate, string DescriptionTemplate, int? Priority = null);
public sealed record FlowNodeDto(Guid Id, FlowNodeKind Kind, string Name, FlowPositionDto Position,
    FlowConditionDto? Condition = null, FlowIncidentMappingDto? Mapping = null, Guid? ConnectorId = null, long? ConnectorRevision = null);
public sealed record FlowEdgeDto(Guid SourceNodeId, string SourcePort, Guid TargetNodeId, string TargetPort);
public sealed record FlowGraphDto(int SchemaVersion, IReadOnlyList<FlowNodeDto> Nodes, IReadOnlyList<FlowEdgeDto> Edges, FlowViewportDto Viewport);
public sealed record FlowValidationIssueDto(string Code, Guid? NodeId = null);
public sealed record FlowValidationResultDto(bool Valid, IReadOnlyList<FlowValidationIssueDto> Issues);

public sealed record FlowEventDataDto(Guid AgentId, Guid RuleId, string RuleName, string ClientName, string Resource,
    string Metric, string Severity, double? NumericValue, string? ServiceState, DateTimeOffset ObservedAtUtc);
public sealed record FlowIncidentFieldsDto(string Title, string Description, int? Priority = null);
public sealed record FlowDryRunRequest(FlowGraphDto Graph, FlowEventDataDto Input);
public sealed record FlowDryRunResultDto(bool Valid, bool Skipped, string Code, FlowIncidentFieldsDto? Incident,
    IReadOnlyList<Guid> VisitedNodeIds, IReadOnlyList<FlowValidationIssueDto> Issues);
public sealed record FlowCreateRequest(string Name, bool UseTemplate = true);
public sealed record FlowSaveDraftRequest(long ExpectedRevision, string Name, FlowGraphDto Graph);
public sealed record FlowRevisionRequest(long ExpectedRevision);
public sealed record FlowCloneRequest(long ExpectedRevision, string Name);
public sealed record FlowEnabledRequest(long ExpectedRevision, bool Enabled);

public sealed record FlowDefinitionDto(Guid Id, int TenantId, string Name, long Revision, bool Enabled,
    FlowGraphDto Draft, Guid? PublishedVersionId, int PublishedVersionNumber, DateTimeOffset UpdatedAtUtc,
    FlowRunSummaryDto? LatestRun = null);
public sealed record FlowVersionDto(Guid Id, Guid FlowId, int TenantId, int VersionNumber, FlowGraphDto Graph,
    string ConfigurationHash, string PublishedBy, DateTimeOffset PublishedAtUtc);
public sealed record FlowRunSummaryDto(Guid Id, Guid FlowId, Guid FlowVersionId, Guid EventId, Guid OccurrenceId,
    FlowRunStatus Status, DateTimeOffset CreatedAtUtc, DateTimeOffset? CompletedAtUtc, string? Code = null);
public sealed record FlowActionReceiptDto(string IncidentId, string? TrackingId = null, string? SafeLink = null);
public sealed record FlowActionSummaryDto(Guid NodeId, string IdempotencyKey, FlowActionStatus Status, int Attempts,
    long? ConnectorRevision, string? Code, FlowActionReceiptDto? Receipt);
public sealed record FlowRunDetailDto(FlowRunSummaryDto Run, FlowEventDataDto Input, IReadOnlyList<FlowActionSummaryDto> Actions);
public sealed record FlowConnectorReferenceDto(Guid Id, int TenantId, string Name, bool Enabled, bool CanExecute, string? UnavailableReason = null, long Revision = 0);
public sealed record FlowTenantAccessDto(int TenantId, string TenantName, bool CanEdit, bool CanPublish, bool CanExecute);

public static class FlowLimits
{
    public const int SchemaVersion = 1;
    public const int MaximumFlowsPerTenant = 128;
    public const int MaximumVersionsPerFlow = 128;
    public const int MaximumNodes = 8;
    public const int MaximumEdges = 7;
    public const int MaximumNameLength = 128;
    public const int MaximumTemplateLength = 4096;
    public const int MaximumTitleLength = 200;
    public const int MaximumDescriptionLength = 8000;
    public const int MaximumEventTextLength = 512;
    public const int MaximumGraphBytes = 32768;
    public const int MaximumEventBytes = 8192;
    public const int MaximumActionBytes = 24576;
    public const int MaximumRunsPerTenant = 4096;
    public const int MaximumQueuedRunsPerTenant = 256;
    public const int MaximumActionAttempts = 5;
    public const int MaximumHistoryRows = 100;
    public const string OutputPort = "out";
    public const string InputPort = "in";
    public static readonly TimeSpan ExecutionTimeout = TimeSpan.FromSeconds(20);
    public static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan MaximumRetryAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan HistoryRetention = TimeSpan.FromDays(90);
}
