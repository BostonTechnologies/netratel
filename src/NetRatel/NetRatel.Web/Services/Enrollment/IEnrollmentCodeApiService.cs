using NetRatel.Shared.Contracts.Enrollment;

namespace NetRatel.Web.Services.Enrollment;

public interface IEnrollmentCodeApiService
{
    Task<IReadOnlyList<EnrollmentCodeDto>> ListAsync(int tenantId, string? status, string? search = null, CancellationToken ct = default);
    Task<IssuedEnrollmentCodeDto> IssueAsync(int tenantId, IssueEnrollmentCodeRequest request, CancellationToken ct = default);
    Task RevokeAsync(int tenantId, Guid codeId, string? reason, CancellationToken ct = default);
}
