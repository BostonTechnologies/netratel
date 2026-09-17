namespace NetRatel.Application.Agents;

public sealed record EnrollmentCodeIssueRequest(
    int TenantId,
    int ValidForMinutes,
    int MaxUses,
    string? CreatedBy,
    string? Notes,
    Guid? DevelopmentMcpTargetAgentId = null,
    string? DevelopmentMcpMarker = null);

public sealed record EnrollmentCodeIssueResult(
    Guid EnrollmentCodeId,
    int TenantId,
    string Code,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ValidToUtc);

/// <summary>
/// Issues a single-use enrollment secret that is atomically reserved for one
/// server-validated legacy primary-client identity. The identity is supplied
/// only after API-side legacy directory validation.
/// </summary>
public sealed record PrimaryClientEnrollmentIssueRequest(
    int TenantId,
    string PrimaryClientIdentity,
    int ValidForMinutes,
    string CreatedBy,
    string? Notes);

public sealed record PrimaryClientEnrollmentIssueResult(
    EnrollmentCodeIssueResult EnrollmentCode,
    PrimaryClientAgentBindingDto Binding);

public sealed record EnrollmentCodeListItem(
    Guid EnrollmentCodeId,
    int TenantId,
    string Code,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ValidFromUtc,
    DateTimeOffset ValidToUtc,
    int Uses,
    int? MaxUses,
    DateTimeOffset? RevokedAtUtc,
    string? RevokedBy,
    string? Notes,
    DateTimeOffset? LastUsedUtc,
    bool IsActive);

/// <summary>
/// Persisted ownership metadata for the Dev-only MCP onboarding adapter.
/// The enrollment code value is deliberately excluded.
/// </summary>
public sealed record DevelopmentMcpEnrollmentOwnershipRecord(
    Guid EnrollmentCodeId,
    int TenantId,
    Guid TargetAgentId,
    string Marker,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset ValidToUtc,
    int MaxUses,
    int Uses,
    DateTimeOffset? RevokedAtUtc);

public interface IEnrollmentCodeIssueService
{
    Task<EnrollmentCodeIssueResult> IssueAsync(EnrollmentCodeIssueRequest request, CancellationToken ct);
    Task<PrimaryClientEnrollmentIssueResult> IssueForPrimaryClientBindingAsync(
        PrimaryClientEnrollmentIssueRequest request,
        CancellationToken ct);
    Task<EnrollmentCodeIssueResult> GetActiveAsync(Guid enrollmentCodeId, int tenantId, CancellationToken ct);
    Task<DevelopmentMcpEnrollmentOwnershipRecord?> GetDevelopmentMcpOwnershipAsync(
        Guid enrollmentCodeId,
        int tenantId,
        Guid targetAgentId,
        CancellationToken ct);
    Task<EnrollmentCodeIssueResult> ValidateActiveCodeAsync(string enrollmentCode, int tenantId, CancellationToken ct);
    Task<IReadOnlyList<EnrollmentCodeListItem>> ListAsync(int tenantId, string? status, string? search, CancellationToken ct);
    Task RevokeAsync(Guid enrollmentCodeId, int tenantId, string? actor, string? reason, CancellationToken ct);
}
