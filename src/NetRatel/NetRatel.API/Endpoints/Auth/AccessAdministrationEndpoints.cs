using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>
/// Instance-administrator APIs for role definitions and explicit principal
/// assignments. Browser editing is added in the guided setup/account phase;
/// these routes are also the authoritative boundary for it.
/// </summary>
public static class AccessAdministrationEndpoints
{
    public static IEndpointRouteBuilder MapAccessAdministrationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/access")
            .WithTags("Access administration")
            .RequireAuthorization("InstanceAdministrator");

        group.MapGet("/roles", async (NetRatelIdentityDbContext db, CancellationToken ct) =>
        {
            var roles = await db.AccessRoles.AsNoTracking()
                .OrderByDescending(role => role.IsBuiltIn)
                .ThenByDescending(role => role.DelegationRank)
                .ThenBy(role => role.Name)
                .Select(role => new AccessRoleResponse(
                    role.Id,
                    role.Name,
                    role.Description,
                    role.IsBuiltIn,
                    role.IsInstanceAdministratorRole,
                    role.DelegationRank,
                    role.Permissions.OrderBy(permission => permission.Permission).Select(permission => permission.Permission).ToArray()))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(roles);
        });

        group.MapPost("/roles", async (
            [FromBody] CreateAccessRoleRequest request,
            ClaimsPrincipal actor,
            NetRatelIdentityDbContext db,
            IEffectiveAccessService access,
            CancellationToken ct) =>
        {
            var name = request.Name?.Trim();
            var permissions = (request.Permissions ?? [])
                .Where(permission => !string.IsNullOrWhiteSpace(permission))
                .Select(permission => permission.Trim())
                .ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(name) || name.Length > 128 || permissions.Count == 0 || !permissions.IsSubsetOf(NetRatelPermissions.All))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["role"] = ["A unique name and known permission set are required."] });
            }

            if (await db.AccessRoles.AnyAsync(role => role.Name == name, ct).ConfigureAwait(false))
            {
                return Results.Conflict(new { error = "role_name_exists" });
            }

            var actorAccess = await access.GetSnapshotAsync(actor, tenantId: request.TenantId, ct).ConfigureAwait(false);
            if (!actorAccess.IsLegacyOperator && !actorAccess.IsInstanceAdministrator && !permissions.IsSubsetOf(actorAccess.Permissions))
            {
                return Results.Forbid();
            }

            var role = new AccessRole
            {
                Name = name,
                Description = request.Description?.Trim(),
                DelegationRank = Math.Clamp(request.DelegationRank, 0, 999),
                IsBuiltIn = false,
                IsInstanceAdministratorRole = false
            };
            foreach (var permission in permissions)
            {
                role.Permissions.Add(new AccessRolePermission { Permission = permission });
            }

            db.AccessRoles.Add(role);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Created($"/api/v2/access/roles/{role.Id}", ToResponse(role));
        });

        group.MapGet("/principals/{principalId}/assignments", async (string principalId, NetRatelIdentityDbContext db, CancellationToken ct) =>
        {
            var assignments = await db.PrincipalRoleAssignments.AsNoTracking()
                .Where(assignment => assignment.PrincipalId == principalId)
                .OrderBy(assignment => assignment.TenantId)
                .Select(assignment => new RoleAssignmentResponse(
                    assignment.Id,
                    assignment.PrincipalId,
                    assignment.RoleId,
                    assignment.Role!.Name,
                    assignment.TenantId,
                    assignment.CreatedAtUtc))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(assignments);
        });

        group.MapPut("/principals/{principalId}/assignments", async (
            string principalId,
            [FromBody] AssignRoleRequest request,
            ClaimsPrincipal actor,
            NetRatelIdentityDbContext identityDb,
            OrchestratorDbContext appDb,
            CancellationToken ct) =>
        {
            if (!await identityDb.ApplicationPrincipals.AnyAsync(principal => principal.Id == principalId, ct).ConfigureAwait(false))
            {
                return Results.NotFound();
            }

            var role = await identityDb.AccessRoles.SingleOrDefaultAsync(candidate => candidate.Id == request.RoleId, ct).ConfigureAwait(false);
            if (role is null)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["roleId"] = ["The role does not exist."] });
            }

            if (request.TenantId is int tenantId && !await appDb.Tenants.AnyAsync(tenant => tenant.Id == tenantId, ct).ConfigureAwait(false))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["tenantId"] = ["The tenant does not exist."] });
            }

            if (await identityDb.PrincipalRoleAssignments.AnyAsync(assignment => assignment.PrincipalId == principalId && assignment.RoleId == role.Id && assignment.TenantId == request.TenantId, ct).ConfigureAwait(false))
            {
                return Results.NoContent();
            }

            var assignment = new PrincipalRoleAssignment
            {
                PrincipalId = principalId,
                RoleId = role.Id,
                TenantId = request.TenantId,
                CreatedByPrincipalId = actor.FindFirst("netratel_principal_id")?.Value
            };
            identityDb.PrincipalRoleAssignments.Add(assignment);
            await identityDb.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.Created($"/api/v2/access/principals/{principalId}/assignments/{assignment.Id}",
                new RoleAssignmentResponse(assignment.Id, assignment.PrincipalId, role.Id, role.Name, assignment.TenantId, assignment.CreatedAtUtc));
        });

        group.MapDelete("/principals/{principalId}/assignments/{assignmentId}", async (
            string principalId,
            string assignmentId,
            NetRatelIdentityDbContext db,
            CancellationToken ct) =>
        {
            var assignment = await db.PrincipalRoleAssignments
                .Include(candidate => candidate.Role)
                .SingleOrDefaultAsync(candidate => candidate.Id == assignmentId && candidate.PrincipalId == principalId, ct)
                .ConfigureAwait(false);
            if (assignment is null)
            {
                return Results.NotFound();
            }

            if (assignment.TenantId is null && assignment.Role!.IsInstanceAdministratorRole &&
                await InstanceAdministratorCountAsync(db, ct).ConfigureAwait(false) <= 1)
            {
                return Results.Conflict(new { error = "last_instance_administrator" });
            }

            db.PrincipalRoleAssignments.Remove(assignment);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        group.MapGet("/users", async (UserManager<LocalUser> users, CancellationToken ct) =>
        {
            var response = await users.Users.AsNoTracking()
                .OrderBy(user => user.Email)
                .Select(user => new LocalUserAccessResponse(user.Id, user.PrincipalId, user.Email!, user.DisplayName, user.IsEnabled, user.IsInstanceAdministrator))
                .ToListAsync(ct)
                .ConfigureAwait(false);
            return Results.Ok(response);
        });

        return app;
    }

    private static async Task<int> InstanceAdministratorCountAsync(NetRatelIdentityDbContext db, CancellationToken ct)
    {
        var localAdministrators = await db.Users
            .Where(user => user.IsEnabled && user.IsInstanceAdministrator)
            .Select(user => user.PrincipalId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var assignedAdministrators = await db.PrincipalRoleAssignments
            .Where(assignment => assignment.TenantId == null && assignment.Role!.IsInstanceAdministratorRole)
            .Select(assignment => assignment.PrincipalId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return localAdministrators.Concat(assignedAdministrators).Distinct(StringComparer.Ordinal).Count();
    }

    private static AccessRoleResponse ToResponse(AccessRole role) => new(
        role.Id, role.Name, role.Description, role.IsBuiltIn, role.IsInstanceAdministratorRole,
        role.DelegationRank, role.Permissions.Select(permission => permission.Permission).OrderBy(permission => permission).ToArray());

    public sealed record CreateAccessRoleRequest(string? Name, string? Description, int DelegationRank, int? TenantId, IReadOnlyList<string>? Permissions);
    public sealed record AssignRoleRequest(string RoleId, int? TenantId);
    public sealed record AccessRoleResponse(string Id, string Name, string? Description, bool IsBuiltIn, bool IsInstanceAdministratorRole, int DelegationRank, IReadOnlyList<string> Permissions);
    public sealed record RoleAssignmentResponse(string Id, string PrincipalId, string RoleId, string RoleName, int? TenantId, DateTimeOffset CreatedAtUtc);
    public sealed record LocalUserAccessResponse(string UserId, string PrincipalId, string Email, string DisplayName, bool IsEnabled, bool IsInstanceAdministrator);
}
