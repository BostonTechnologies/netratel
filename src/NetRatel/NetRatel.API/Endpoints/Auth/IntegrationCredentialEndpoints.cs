using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>Current-account lifecycle for one-time-revealed integration credentials.</summary>
public static class IntegrationCredentialEndpoints
{
    public static IEndpointRouteBuilder MapIntegrationCredentialEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/account/integration-credentials")
            .WithTags("Integration credentials")
            .RequireAuthorization("InteractiveAccount");

        group.MapGet("/tenant-scopes", async (
            ClaimsPrincipal principal,
            NetRatelIdentityDbContext identityDb,
            OrchestratorDbContext applicationDb,
            IEffectiveAccessService access,
            CancellationToken ct) =>
        {
            var owner = PrincipalId(principal);
            if (owner is null)
            {
                return Results.Forbid();
            }

            if ((await access.GetSnapshotAsync(principal, tenantId: null, ct).ConfigureAwait(false)).IsInstanceAdministrator)
            {
                return Results.Ok(await applicationDb.Tenants.AsNoTracking()
                    .OrderBy(tenant => tenant.Name)
                    .Select(tenant => new CredentialTenantScopeResponse(tenant.Id, tenant.Name))
                    .ToListAsync(ct)
                    .ConfigureAwait(false));
            }

            var tenantIds = await identityDb.PrincipalRoleAssignments.AsNoTracking()
                .Where(assignment => assignment.PrincipalId == owner && assignment.TenantId != null)
                .Where(assignment => assignment.Role!.Permissions.Any(permission =>
                    permission.Permission != NetRatelPermissions.IntegrationManagement))
                .Select(assignment => assignment.TenantId!.Value)
                .Distinct()
                .ToArrayAsync(ct)
                .ConfigureAwait(false);

            return Results.Ok(await applicationDb.Tenants.AsNoTracking()
                .Where(tenant => tenantIds.Contains(tenant.Id))
                .OrderBy(tenant => tenant.Name)
                .Select(tenant => new CredentialTenantScopeResponse(tenant.Id, tenant.Name))
                .ToListAsync(ct)
                .ConfigureAwait(false));
        });

        group.MapGet("/authority", async (
            ClaimsPrincipal principal,
            NetRatelIdentityDbContext identityDb,
            OrchestratorDbContext applicationDb,
            IEffectiveAccessService access,
            IConfiguration configuration,
            CancellationToken ct) =>
        {
            var owner = PrincipalId(principal);
            if (owner is null) return Results.Forbid();

            var instance = await access.GetSnapshotAsync(principal, null, ct).ConfigureAwait(false);
            int[] candidateIds = instance.IsInstanceAdministrator ? [] : await identityDb.PrincipalRoleAssignments.AsNoTracking()
                .Where(assignment => assignment.PrincipalId == owner && assignment.TenantId != null)
                .Select(assignment => assignment.TenantId!.Value).Distinct().ToArrayAsync(ct).ConfigureAwait(false);
            var candidates = instance.IsInstanceAdministrator
                ? await applicationDb.Tenants.AsNoTracking().OrderBy(tenant => tenant.Name)
                    .Select(tenant => new CredentialTenantScopeResponse(tenant.Id, tenant.Name)).ToArrayAsync(ct).ConfigureAwait(false)
                : await applicationDb.Tenants.AsNoTracking()
                    .Where(tenant => candidateIds.Contains(tenant.Id))
                    .OrderBy(tenant => tenant.Name)
                    .Select(tenant => new CredentialTenantScopeResponse(tenant.Id, tenant.Name)).ToArrayAsync(ct).ConfigureAwait(false);

            var tenants = new List<CredentialTenantAuthorityResponse>();
            foreach (var candidate in candidates)
            {
                var permissions = new List<IntegrationPermissionDescriptor>();
                foreach (var descriptor in IntegrationPermissionCatalog.Delegable)
                {
                    if (await access.AuthorizeAsync(principal, descriptor.Id, candidate.TenantId, ct).ConfigureAwait(false))
                        permissions.Add(descriptor);
                }
                if (permissions.Count > 0)
                    tenants.Add(new(candidate.TenantId, candidate.Name, permissions));
            }

            var instancePermissions = new List<IntegrationPermissionDescriptor>();
            foreach (var descriptor in IntegrationPermissionCatalog.Delegable)
            {
                if (await access.AuthorizeAsync(principal, descriptor.Id, null, ct).ConfigureAwait(false))
                    instancePermissions.Add(descriptor);
            }

            var configuredResource = configuration["Integration:HttpMcp:PublicResourceUri"];
            if (!McpResourceUri.TryNormalize(configuredResource, out var canonicalResource)) canonicalResource = string.Empty;
            return Results.Ok(new CredentialAuthorityResponse(tenants, instancePermissions, canonicalResource));
        });

        group.MapGet("/", async (ClaimsPrincipal principal, IIntegrationCredentialService credentials, HttpContext context, CancellationToken ct) =>
        {
            var owner = PrincipalId(principal);
            if (owner is null)
            {
                return Results.Forbid();
            }

            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(await credentials.ListAsync(owner, ct).ConfigureAwait(false));
        });

        group.MapPost("/", async (
            [FromBody] CreateIntegrationCredentialRequest request,
            ClaimsPrincipal principal,
            IIntegrationCredentialService credentials,
            IIntegrationCredentialGrantValidator validator,
            HttpContext context,
            CancellationToken ct) =>
        {
            var owner = PrincipalId(principal);
            if (owner is null || !IsCookieMutationValidated(principal, context))
            {
                return Results.Forbid();
            }

            var grants = request.Grants ?? [];
            var instancePermissions = request.InstancePermissions ?? [];
            var create = new IntegrationCredentialCreateRequest(request.Name, request.Purpose, request.ExpiresAtUtc,
                grants, request.Resource, instancePermissions);
            try
            {
                await validator.ValidateAsync(principal, create, ct).ConfigureAwait(false);
            }
            catch (IntegrationCredentialGrantValidationException exception)
            {
                return exception.Forbidden ? Results.Forbid() : Results.ValidationProblem(
                    new Dictionary<string, string[]> { [exception.Field] = ["Review the credential fields and selected access, then retry."] });
            }

            try
            {
                var created = await credentials.CreateAsync(owner, new IntegrationCredentialCreateRequest(
                    request.Name,
                    request.Purpose,
                    request.ExpiresAtUtc,
                    grants.Select(grant => new IntegrationCredentialGrantRequest(grant.TenantId, grant.Permission)).ToArray(),
                    request.Resource,
                    instancePermissions), ct).ConfigureAwait(false);
                context.Response.Headers.CacheControl = "no-store";
                return Results.Created($"/api/v2/account/integration-credentials/{created.CredentialId}", created);
            }
            catch (ArgumentException)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["credential"] = ["Review the credential fields and selected access, then retry."] });
            }
        });

        group.MapPost("/{credentialId}/revoke", async (
            string credentialId,
            ClaimsPrincipal principal,
            IIntegrationCredentialService credentials,
            HttpContext context,
            CancellationToken ct) =>
        {
            var owner = PrincipalId(principal);
            if (owner is null || !IsCookieMutationValidated(principal, context))
            {
                return Results.Forbid();
            }

            context.Response.Headers.CacheControl = "no-store";
            return await credentials.RevokeAsync(owner, credentialId, owner, ct).ConfigureAwait(false)
                ? Results.NoContent()
                : Results.NoContent();
        });

        group.MapDelete("/{credentialId}", async (
            string credentialId,
            ClaimsPrincipal principal,
            IIntegrationCredentialService credentials,
            HttpContext context,
            CancellationToken ct) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var owner = PrincipalId(principal);
            if (owner is null || !IsCookieMutationValidated(principal, context)) return Results.Forbid();

            await credentials.DeleteAsync(owner, credentialId, owner, ct).ConfigureAwait(false);
            // Missing and other-owner identifiers intentionally have the same result.
            return Results.NoContent();
        });

        return app;
    }

    private static string? PrincipalId(ClaimsPrincipal principal) => principal.FindFirstValue("netratel_principal_id");

    private static bool IsCookieMutationValidated(ClaimsPrincipal principal, HttpContext context) =>
        !principal.HasClaim("auth_mode", "local") ||
        string.Equals(context.Request.Headers["X-NetRatel-Account-Request"], "1", StringComparison.Ordinal);

    public sealed record CreateIntegrationCredentialRequest(
        string Name,
        IntegrationCredentialPurpose Purpose,
        DateTimeOffset ExpiresAtUtc,
        IReadOnlyList<IntegrationCredentialGrantRequest>? Grants,
        string? Resource = null,
        IReadOnlyList<string>? InstancePermissions = null);

    public sealed record CredentialTenantScopeResponse(int TenantId, string Name);
    public sealed record CredentialTenantAuthorityResponse(int TenantId, string Name, IReadOnlyList<IntegrationPermissionDescriptor> Permissions);
    public sealed record CredentialAuthorityResponse(IReadOnlyList<CredentialTenantAuthorityResponse> Tenants,
        IReadOnlyList<IntegrationPermissionDescriptor> InstancePermissions, string ConfiguredHttpMcpServerUrl);
}
