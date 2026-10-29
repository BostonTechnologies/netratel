namespace NetRatel.Shared.Contracts;

/// <summary>Presentation-safe persistent Agent directory entry used by Global Search.</summary>
public sealed record GlobalSearchAgentDto(
    int TenantId,
    Guid AgentId,
    string DisplayName,
    string? HostName,
    string? TenantName,
    string? OperatingSystem,
    string? AgentVersion,
    bool Enabled);

/// <summary>Current PostgreSQL request result for Global Search.</summary>
public sealed record GlobalSearchRequestDto(
    int Id,
    string SourceSystem,
    string Status,
    string? JobDefinitionId,
    string? ExecutionId,
    string? ResultMessage,
    int? TenantId,
    Guid? AgentId,
    string? TenantName,
    string? AgentDisplayName,
    string? AgentHostName,
    string? LegacyTargetIdentity);

/// <summary>Bounded display metadata, never an alert payload or investigation transcript.</summary>
public sealed record GlobalSearchAlertDto(Guid OccurrenceId, int TenantId, string Title, string TenantName,
    string? ClientName, string Resource, string Severity, string State);

public sealed record GlobalSearchFlowDto(Guid FlowId, int TenantId, string Title, string TenantName,
    bool Enabled, int PublishedVersionNumber);

public sealed record GlobalSearchInvestigationDto(Guid InvestigationId, int TenantId, string Title, string TenantName,
    string State, string? ClientContext, Guid? OccurrenceId, Guid? FlowRunId, bool IsGeneralChat = false);

/// <summary>Exact persisted occurrence selection, including closed alerts outside the loaded page.</summary>
public sealed record MonitoringAlertDetailDto(int TenantId, Guid RuleId, Guid AgentId, string Resource,
    NetRatel.Shared.Contracts.Monitoring.MonitoringOccurrenceDto Occurrence);
