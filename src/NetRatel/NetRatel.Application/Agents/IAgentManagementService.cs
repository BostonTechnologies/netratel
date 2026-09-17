namespace NetRatel.Application.Agents;

public sealed record AgentListQuery(
    string? Search,
    bool? IsEnabled,
    int Page,
    int PageSize);

public sealed record AgentSummaryDto(
    int TenantId,
    Guid AgentId,
    string? DisplayName,
    bool IsEnabled,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastTokenIssuedAtUtc);

public sealed record AgentDetailDto(
    int TenantId,
    Guid AgentId,
    string? DisplayName,
    bool IsEnabled,
    string? DisabledReason,
    DateTimeOffset CreatedAtUtc,
    string? CreatedBy,
    DateTimeOffset? LastSeenAtUtc,
    DateTimeOffset? LastTokenIssuedAtUtc,
    DateTimeOffset? RevokedAtUtc,
    DateTimeOffset? DeletedAtUtc = null);

public sealed record AgentListResponse(
    IReadOnlyList<AgentSummaryDto> Items,
    int Page,
    int PageSize,
    int Total);

public interface IAgentManagementService
{
    Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct);
    Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct);
    Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct);
    Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct);
    Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct);
}
