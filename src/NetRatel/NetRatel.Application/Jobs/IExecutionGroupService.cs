using System.Security.Claims;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Application.Jobs;

public interface IExecutionGroupService
{
    Task<ExecutionGroupPageDto> ListAsync(int tenantId, string? search, int page, int pageSize, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<ExecutionGroupDetailsDto?> GetAsync(int tenantId, Guid groupId, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<ExecutionGroupDetailsDto> CreateAsync(int tenantId, ExecutionGroupWriteDto request, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<ExecutionGroupDetailsDto?> UpdateAsync(int tenantId, Guid groupId, ExecutionGroupWriteDto request, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<ExecutionGroupDetailsDto?> ChangeMembershipAsync(int tenantId, Guid groupId, ExecutionGroupMembershipWriteDto request, ClaimsPrincipal principal, CancellationToken cancellationToken);
    Task<bool> DeleteAsync(int tenantId, Guid groupId, long expectedRevision, ClaimsPrincipal principal, CancellationToken cancellationToken);
}

/// <summary>Additional retained sources, including immutable published Flow pins, that prevent hard deletion.</summary>
public interface IExecutionGroupReferenceProvider
{
    Task<IReadOnlyList<ExecutionGroupReferenceDto>> FindFlowReferencesAsync(int tenantId, Guid groupId, CancellationToken cancellationToken);
}

public sealed class ExecutionGroupReferencedException(IReadOnlyList<ExecutionGroupReferenceDto> references)
    : InvalidOperationException("The group is referenced by retained definitions. Disable or archive it, or retire its references before deletion.")
{
    public IReadOnlyList<ExecutionGroupReferenceDto> References { get; } = references;
}

public interface IJobNodeDirectory
{
    Task<IReadOnlyList<JobNodeInventoryDto>> ReadAsync(int tenantId, IReadOnlySet<Guid>? authorizedAgentIds, bool includeInvalid, CancellationToken cancellationToken);
}
