using System.Net.Http.Json;

namespace NetRatel.Web.Services.Access;

public interface IAccessAdministrationApiService
{
    Task<IReadOnlyList<AccessRoleDto>> GetRolesAsync(CancellationToken cancellationToken = default);
    Task<EffectiveAccessSummaryDto> GetSelfAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<LocalUserAccessDto>> GetUsersAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<RoleAssignmentDto>> GetAssignmentsAsync(string principalId, CancellationToken cancellationToken = default);
    Task AssignAsync(string principalId, string roleId, int? tenantId, CancellationToken cancellationToken = default);
    Task RemoveAssignmentAsync(string principalId, string assignmentId, CancellationToken cancellationToken = default);
}

public sealed class AccessAdministrationApiService(IHttpClientFactory clients) : IAccessAdministrationApiService
{
    private HttpClient Client => clients.CreateClient("OrchestratorApi");

    public async Task<IReadOnlyList<AccessRoleDto>> GetRolesAsync(CancellationToken cancellationToken = default) =>
        await Client.GetFromJsonAsync<List<AccessRoleDto>>("/api/v2/access/roles", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<EffectiveAccessSummaryDto> GetSelfAsync(CancellationToken cancellationToken = default) =>
        await Client.GetFromJsonAsync<EffectiveAccessSummaryDto>("/api/v2/access/self", cancellationToken).ConfigureAwait(false)
        ?? new(null, false, []);

    public async Task<IReadOnlyList<LocalUserAccessDto>> GetUsersAsync(CancellationToken cancellationToken = default) =>
        await Client.GetFromJsonAsync<List<LocalUserAccessDto>>("/api/v2/access/users", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task<IReadOnlyList<RoleAssignmentDto>> GetAssignmentsAsync(string principalId, CancellationToken cancellationToken = default) =>
        await Client.GetFromJsonAsync<List<RoleAssignmentDto>>($"/api/v2/access/principals/{Uri.EscapeDataString(principalId)}/assignments", cancellationToken).ConfigureAwait(false) ?? [];

    public async Task AssignAsync(string principalId, string roleId, int? tenantId, CancellationToken cancellationToken = default)
    {
        var response = await Client.PutAsJsonAsync($"/api/v2/access/principals/{Uri.EscapeDataString(principalId)}/assignments", new { roleId, tenantId }, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    public async Task RemoveAssignmentAsync(string principalId, string assignmentId, CancellationToken cancellationToken = default)
    {
        var response = await Client.DeleteAsync($"/api/v2/access/principals/{Uri.EscapeDataString(principalId)}/assignments/{Uri.EscapeDataString(assignmentId)}", cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
}

public sealed record AccessRoleDto(string Id, string Name, string? Description, bool IsBuiltIn, bool IsInstanceAdministratorRole, int DelegationRank, IReadOnlyList<string> Permissions);
public sealed record EffectiveAccessSummaryDto(string? PrincipalId, bool IsInstanceAdministrator, IReadOnlyList<string> Permissions);
public sealed record RoleAssignmentDto(string Id, string PrincipalId, string RoleId, string RoleName, int? TenantId, DateTimeOffset CreatedAtUtc);
public sealed record LocalUserAccessDto(string UserId, string PrincipalId, string Email, string DisplayName, bool IsEnabled, bool IsInstanceAdministrator);
