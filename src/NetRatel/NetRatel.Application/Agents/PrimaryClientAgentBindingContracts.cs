namespace NetRatel.Application.Agents;

/// <summary>
/// Lifecycle of a server-owned bridge between a legacy primary client and an
/// enrolled NetRatel gateway agent.
/// </summary>
public enum PrimaryClientAgentBindingStatus
{
    Pending = 0,
    Bound = 1,
    Conflict = 2,
    Revoked = 3
}

public sealed record PrimaryClientAgentBindingDto(
    Guid BindingId,
    int TenantId,
    Guid? AgentId,
    Guid? EnrollmentCodeId,
    string PrimaryClientIdentity,
    PrimaryClientAgentBindingStatus Status,
    DateTimeOffset CreatedAtUtc,
    string CreatedBy,
    DateTimeOffset? BoundAtUtc,
    string? BoundBy,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedBy,
    string BindingSource,
    string? Notes,
    uint Version);

public sealed record CreatePendingPrimaryClientAgentBinding(
    int TenantId,
    Guid EnrollmentCodeId,
    string PrimaryClientIdentity,
    string CreatedBy,
    string? Notes);

public sealed record CreateManualPrimaryClientAgentBinding(
    int TenantId,
    Guid AgentId,
    string PrimaryClientIdentity,
    string CreatedBy,
    string Notes);

public enum PrimaryClientAgentBindingDisposition
{
    Created,
    AlreadyBound,
    EnrollmentCodeNotFound,
    EnrollmentCodeMustBeSingleUse,
    AgentNotFound,
    AgentAlreadyBound,
    PrimaryClientAlreadyBound,
    EnrollmentCodeAlreadyBound,
    BindingNotFound,
    BindingNotPending,
    TenantMismatch
}

public sealed record PrimaryClientAgentBindingResult(
    PrimaryClientAgentBindingDisposition Disposition,
    PrimaryClientAgentBindingDto? Binding);

public sealed record PrimaryClientAgentBindingConflict(
    string Kind,
    string Key,
    IReadOnlyList<Guid> BindingIds);

public sealed record PrimaryClientAgentBindingDiagnosticsDto(
    int TenantId,
    int TotalAgents,
    int BoundAgents,
    int PendingBindings,
    IReadOnlyList<Guid> UnboundAgentIds,
    IReadOnlyList<PrimaryClientAgentBindingConflict> AmbiguousAgentBindings,
    IReadOnlyList<PrimaryClientAgentBindingConflict> ConflictingPrimaryClientBindings,
    IReadOnlyList<PrimaryClientAgentBindingDto> StaleOrRevokedBindings);

public interface IPrimaryClientAgentBindingService
{
    Task<PrimaryClientAgentBindingDto?> GetByAgentAsync(int tenantId, Guid agentId, CancellationToken ct);

    Task<PrimaryClientAgentBindingDto?> GetByPrimaryClientIdentityAsync(
        int tenantId,
        string primaryClientIdentity,
        CancellationToken ct);

    Task<IReadOnlyList<PrimaryClientAgentBindingDto>> ListBoundAsync(int tenantId, CancellationToken ct);

    Task<PrimaryClientAgentBindingResult> CreatePendingAsync(
        CreatePendingPrimaryClientAgentBinding request,
        CancellationToken ct);

    Task<PrimaryClientAgentBindingResult> CreateManualRepairAsync(
        CreateManualPrimaryClientAgentBinding request,
        CancellationToken ct);

    Task<PrimaryClientAgentBindingResult> BindEnrollmentAsync(
        int tenantId,
        Guid enrollmentCodeId,
        Guid agentId,
        CancellationToken ct);

    Task<bool> RevokeAsync(int tenantId, Guid bindingId, string revokedBy, string reason, CancellationToken ct);

    Task<PrimaryClientAgentBindingDiagnosticsDto> GetDiagnosticsAsync(int tenantId, CancellationToken ct);
}
