namespace NetRatel.Shared.Contracts.Projections;

public sealed record ActiveJobRunProjectionDto(
    ulong RunId,
    ulong JobId,
    string JobName,
    int? TenantId,
    string ClientIdentity,
    string? ClientDisplayName,
    string StartedBy,
    string Status,
    int CurrentStepOrdinal,
    int TotalSteps,
    long CreatedAtUnixMilliseconds,
    long? StartedAtUnixMilliseconds,
    long? UpdatedAtUnixMilliseconds,
    string? Error,
    string ScopeKey);

public sealed record ActiveJobStepProjectionDto(
    ulong StepRunId,
    ulong RunId,
    ulong StepId,
    int Ordinal,
    string StepName,
    string StepType,
    string Status,
    string? TaskRequestId,
    string? Error,
    long? StartedAtUnixMilliseconds,
    long? CompletedAtUnixMilliseconds,
    string ScopeKey);

public sealed record RequestLiveSummaryProjectionDto(
    int RequestId,
    string SourceSystem,
    string TargetClientIdentity,
    string RundeckJobDefinitionId,
    string? RundeckExecutionId,
    string Status,
    string? ResultMessage,
    long CreatedAtUnixMilliseconds,
    long UpdatedAtUnixMilliseconds,
    string ScopeKey);

public sealed record TenantOperationsProjectionDto(
    int TenantId,
    string TenantName,
    int ActiveJobRuns,
    int PendingRequests,
    int OnlineClients,
    int AlertCount,
    long GeneratedAtUnixMilliseconds,
    string ScopeKey);

public sealed record NotificationFeedProjectionDto(
    string NotificationId,
    string Category,
    string Severity,
    string Title,
    string Message,
    string? EntityId,
    string? CorrelationId,
    int? TenantId,
    long OccurredAtUnixMilliseconds,
    string ScopeKey);
