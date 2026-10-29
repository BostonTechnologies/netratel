using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.Operations;

namespace NetRatel.API.Endpoints.Client;

public static class McpOperatorPolicyDirectoryEndpoints
{
    public static void MapPolicyDirectory(this RouteGroupBuilder group)
    {
        group.MapGet("/directory", async (string? search, int? tenantId, HttpContext http,
            OrchestratorDbContext db, NetRatelIdentityDbContext identity, CancellationToken ct) =>
        {
            if ((search?.Length ?? 0) > 100 || tenantId < 0) return Results.BadRequest();
            http.Response.Headers.CacheControl = "no-store";
            var query = search?.Trim() ?? "";
            var tenants = await db.Tenants.AsNoTracking().Where(t => query == "" || t.Name.Contains(query))
                .OrderBy(t => t.Name).Take(101).Select(t => new McpPolicyTenant(t.Id, t.Name)).ToArrayAsync(ct);
            var clients = tenantId is > 0 ? await db.Agents.AsNoTracking().Where(a => a.TenantId == tenantId && a.DeletedAtUtc == null &&
                (query == "" || (a.FriendlyName ?? a.Name ?? "").Contains(query))).OrderBy(a => a.FriendlyName ?? a.Name)
                .Take(101).Select(a => new McpPolicyClient(a.Id, a.TenantId, a.FriendlyName ?? a.Name ?? "Unnamed client")).ToArrayAsync(ct) : [];
            var principals = new List<McpPolicyPrincipal>();
            var credentials = await identity.IntegrationCredentials.AsNoTracking().Where(c => query == "" || c.Name.Contains(query) || c.Id.Contains(query))
                .OrderBy(c => c.Name).Take(101).Select(c => new { c.Id, c.Name, c.Purpose, c.RevokedAtUtc, c.ExpiresAtUtc }).ToArrayAsync(ct);
            principals.AddRange(credentials.Take(100).Select(c => new McpPolicyPrincipal(McpOperatorPrincipalSelectorKind.OAuthClientId,
                c.Id, c.Name, $"{c.Purpose} · {(c.RevokedAtUtc is not null ? "Revoked" : c.ExpiresAtUtc <= DateTimeOffset.UtcNow ? "Expired" : "Active")} · {c.Id[..8]}")));
            var users = await identity.Users.AsNoTracking().Where(u => query == "" || (u.DisplayName ?? u.UserName ?? "").Contains(query) || u.PrincipalId.Contains(query))
                .OrderBy(u => u.DisplayName ?? u.UserName).Take(101).Select(u => new { u.PrincipalId, u.DisplayName, u.UserName, u.IsEnabled }).ToArrayAsync(ct);
            principals.AddRange(users.Take(100).Select(u => new McpPolicyPrincipal(McpOperatorPrincipalSelectorKind.OAuthSubject,
                u.PrincipalId, u.DisplayName ?? u.UserName ?? "Account", $"{(u.IsEnabled ? "Enabled" : "Disabled")} · {u.PrincipalId[..8]}")));
            var services = await db.Set<ServicePrincipalRegistration>().AsNoTracking().Where(s => query == "" || s.Name.Contains(query) || s.ClientId.Contains(query))
                .OrderBy(s => s.Name).Take(101).Select(s => new { s.ClientId, s.Name, s.Status }).ToArrayAsync(ct);
            principals.AddRange(services.Take(100).Select(s => new McpPolicyPrincipal(McpOperatorPrincipalSelectorKind.ServicePrincipal, s.ClientId, s.Name, s.Status)));
            var external = await identity.ApplicationPrincipals.AsNoTracking().Where(p => p.ExternalSubject != null && p.ExternalAuthorityExpiresAtUtc > DateTimeOffset.UtcNow && (query == "" || p.ExternalSubject.Contains(query) || (p.ExternalAuthorityClaimsJson ?? "").Contains(query)))
                .OrderBy(p => p.Id).Take(500).Select(p => new { p.ExternalSubject, p.ExternalAuthorityClaimsJson }).ToArrayAsync(ct);
            foreach (var entry in external)
            {
                var claims = JsonSerializer.Deserialize<ExternalAuthorityClaim[]>(entry.ExternalAuthorityClaimsJson ?? "[]") ?? [];
                var display = claims.FirstOrDefault(c => c.Type is "name" or "preferred_username" or "email" or ClaimTypes.Name)?.Value ?? entry.ExternalSubject!;
                principals.Add(new(McpOperatorPrincipalSelectorKind.OAuthSubject, entry.ExternalSubject!, display, "External subject"));
                foreach (var claim in claims.Where(c => c.Type is "groups" or "roles" or "role" or ClaimTypes.Role or "azp" or "appid"))
                    principals.Add(new(claim.Type == "groups" ? McpOperatorPrincipalSelectorKind.OidcGroup :
                        claim.Type is "azp" or "appid" ? McpOperatorPrincipalSelectorKind.OAuthClientId : McpOperatorPrincipalSelectorKind.MappedRole,
                        claim.Value, claim.Value, "Validated identity provider value"));
            }
            principals.AddRange(Enum.GetValues<McpOperationAccessScope>().SelectMany(McpOperationRoleRequirements.AnyOfRoles).Distinct()
                .Select(role => new McpPolicyPrincipal(McpOperatorPrincipalSelectorKind.MappedRole, role, role, "Published MCP role")));
            var tagsJson = tenantId is > 0 ? await db.McpOperatorTargetProfiles.AsNoTracking().Where(p => p.TenantId == tenantId)
                .OrderBy(p => p.AgentId).Take(500).Select(p => p.TagsJson).ToArrayAsync(ct) : [];
            var tags = tagsJson.SelectMany(json => JsonSerializer.Deserialize<string[]>(json) ?? []).Distinct().Where(t => t.Contains(query, StringComparison.OrdinalIgnoreCase)).Order().Take(100).ToArray();
            return Results.Ok(new McpPolicyDirectory(tenants.Take(100).ToArray(), clients.Take(100).ToArray(),
                principals.Where(p => query == "" || p.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .DistinctBy(p => (p.Kind, p.Id)).OrderBy(p => p.Name).ToArray(), tags,
                tenants.Length > 100 || clients.Length > 100 || credentials.Length > 100 || users.Length > 100 || services.Length > 100 || external.Length == 500 || tagsJson.Length == 500));
        }).RequireAuthorization("InteractiveAccount");
    }
}
