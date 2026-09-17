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
