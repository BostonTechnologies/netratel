using Microsoft.AspNetCore.Mvc;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.API.Endpoints;

internal static partial class SecretVaultEndpoints
{
    private static Task<IResult> SearchProfileTargetsAsync(int tenantId, string? search, int? page, int? pageSize,
        HttpContext http, IEffectiveAccessService access, JobTargetApiService targets, CancellationToken ct) =>
        WithProfileTargetPermissionAsync(tenantId, http, access,
            async () => Results.Ok(await targets.ReadNodesAsync(tenantId, search, page ?? 1, pageSize ?? 25, null, ct)), ct);

    private static Task<IResult> ReadProfileTargetIdentitiesAsync(int tenantId, [FromBody] Guid[] ids, HttpContext http,
        IEffectiveAccessService access, JobTargetApiService targets, CancellationToken ct) =>
        WithProfileTargetPermissionAsync(tenantId, http, access,
            async () => Results.Ok(await targets.ReadIdentitiesAsync(tenantId, ids, null, ct)), ct);

    private static Task<IResult> ReadAllProfileTargetsAsync(int tenantId, bool? windowsOnly, HttpContext http,
        IEffectiveAccessService access, IJobNodeDirectory directory, CancellationToken ct) =>
        WithProfileTargetPermissionAsync(tenantId, http, access, async () =>
        {
            var nodes = await directory.ReadAsync(tenantId, null, false, ct);
            var ids = nodes.Where(node => windowsOnly != true ||
                    node.OperatingSystem?.Contains("Windows", StringComparison.OrdinalIgnoreCase) == true)
                .Select(node => node.AgentId).Take(CredentialExecutionScope.MaximumClients + 1).ToArray();
            if (ids.Length > CredentialExecutionScope.MaximumClients)
                return Results.BadRequest(new SecretVaultError("profile_target_limit",
                    $"Select up to {CredentialExecutionScope.MaximumClients} clients individually."));
            return Results.Ok(ids);
        }, ct);

    private static Task<IResult> WithProfileTargetPermissionAsync(int tenantId, HttpContext http, IEffectiveAccessService access,
        Func<Task<IResult>> operation, CancellationToken ct) =>
        WithPermissionAsync(http, access, tenantId, NetRatelPermissions.CredentialProfileManagement, async () =>
        {
            if (!IsHuman(http) || !await AllowsAsync(http, access, tenantId, NetRatelPermissions.SecretUse, ct))
                return Results.Forbid();
            try { return await operation(); }
            catch (JobTargetValidationException error)
            {
                return Results.Json(new SecretVaultError(error.Code, error.Message), statusCode: error.StatusCode);
            }
        }, ct);
}
