using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.SystemPairing;
namespace NetRatel.Infrastructure.SystemPairing;

public sealed class PairingAuthority(IEffectiveAccessService access, NetRatelIdentityDbContext identity, OrchestratorDbContext db)
{
    public static string ActorId(ClaimsPrincipal actor) => actor.FindFirstValue("netratel_principal_id") ?? "";
    public static ClaimsPrincipal Retained(string id) => new(new ClaimsIdentity([new("netratel_principal_id", id)], "RetainedPairingAdministrator"));
    public async Task RequireAdministratorAsync(ClaimsPrincipal actor, CancellationToken ct)
    {
        var id = ActorId(actor);
        if (actor.Identity?.IsAuthenticated != true || actor.HasClaim("auth_mode", "service") || actor.HasClaim("auth_mode", "machine_token") || actor.FindFirst("netratel_integration_credential_id") is not null || !await CurrentAsync(id, ct))
            throw new PairingException(403, "administrator-required", "Sign in with a current administrator account permitted to manage integrations.");
        var tenants = await access.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
        if (!await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct) && tenants is not null && !tenants.Any())
            throw new PairingException(403, "integration-management-required", "Integration management permission is required.");
    }
    public Task<bool> CanManageInstanceAsync(ClaimsPrincipal actor, CancellationToken ct) => access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, null, ct);
    public async Task<bool> CurrentAsync(string id, CancellationToken ct) => !string.IsNullOrEmpty(id) &&
        await identity.ApplicationPrincipals.AsNoTracking().AnyAsync(x => x.Id == id, ct) && !await identity.Users.AsNoTracking().AnyAsync(x => x.PrincipalId == id && !x.IsEnabled, ct);
    public async Task<bool> CanManageAsync(string id, int tenant, bool incidents, bool automation, CancellationToken ct)
    {
        if (!await CurrentAsync(id, ct)) return false;
        var actor = Retained(id);
        return await access.AuthorizeAsync(actor, NetRatelPermissions.IntegrationManagement, tenant, ct) &&
            (!incidents || await access.AuthorizeAsync(actor, NetRatelPermissions.SecretUse, tenant, ct)) &&
            (!automation || await access.AuthorizeAsync(actor, NetRatelPermissions.JobManagement, tenant, ct));
    }
    private static bool SafeId(string? value) => value is { Length: > 0 and <= 128 } && value.Trim() == value && !value.Any(char.IsControl);
    public static void RequireShape(PairingMapping mapping)
    {
        if (mapping is null || !int.TryParse(mapping.NetRatelTenantId, NumberStyles.None, CultureInfo.InvariantCulture, out var tenant) || tenant <= 0 || tenant.ToString(CultureInfo.InvariantCulture) != mapping.NetRatelTenantId ||
            mapping.Id == Guid.Empty || mapping.PairId is not { Length: 64 } || mapping.Name is not { Length: > 0 and <= 128 } || mapping.Name.Any(char.IsControl) || !SafeId(mapping.RatelDeskOrganizationId) || !mapping.CreateIncidents && !mapping.RunAutomation ||
            mapping.CreateIncidents && !SafeId(mapping.RatelDeskCustomerId) || !mapping.CreateIncidents && mapping.RatelDeskCustomerId is not null)
            throw new PairingException(422, "mapping-invalid", "Choose a NetRatel tenant, RatelDesk organization and customer, at least one capability, and enter a connection name.");
    }
    public async Task RequireMappingAsync(PairingMapping mapping, string owner, CancellationToken ct)
    {
        RequireShape(mapping); var tenant = int.Parse(mapping.NetRatelTenantId, CultureInfo.InvariantCulture);
        if (!await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenant, ct)) throw new PairingException(422, "mapping-tenant-unavailable", "Choose an existing NetRatel tenant.");
        if (!await CanManageAsync(owner, tenant, mapping.CreateIncidents, mapping.RunAutomation, ct))
            throw new PairingException(403, "mapping-not-authorized", "The retained administrator no longer has the permissions required for this tenant and selected capabilities.");
    }
    public async Task<PairingDirectory> DirectoryAsync(string owner, CancellationToken ct)
    {
        if (!await CurrentAsync(owner, ct)) throw new PairingException(403, "administrator-unavailable", "The administrator who authorized this pairing is no longer active.");
        var actor = Retained(owner);
        var ids = await access.GetAuthorizedTenantIdsAsync(actor, NetRatelPermissions.IntegrationManagement, ct);
        var rows = await db.Tenants.AsNoTracking().Where(x => ids == null || ids.Contains(x.Id)).OrderBy(x => x.Name).ToListAsync(ct);
        return new(rows.Select(x => new PairingChoice(x.Id.ToString(CultureInfo.InvariantCulture), x.Name)).ToArray(), []);
    }
    public async Task<PairingResourceConstraints> ResourcesAsync(int tenant, CancellationToken ct)
    {
        var agents = await db.Agents.AsNoTracking().Where(x => x.TenantId == tenant && x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.DeletedAtUtc == null && x.SupersededAtUtc == null && x.SupersededByAgentId == null).Select(x => x.Id).ToArrayAsync(ct);
        var jobs = await db.Jobs.AsNoTracking().Where(x => x.TenantId == tenant && x.AgentId != null && agents.Contains(x.AgentId.Value)).Select(x => new { x.Id, x.AgentId }).ToArrayAsync(ct);
        return new() { TenantId = tenant.ToString(CultureInfo.InvariantCulture), ResourceIds = jobs.Select(x => x.AgentId!.Value.ToString("D")).Distinct().Order(StringComparer.Ordinal).ToArray(), RequestDefinitionIds = jobs.Select(x => x.Id.ToString(CultureInfo.InvariantCulture)).Order(StringComparer.Ordinal).ToArray() };
    }
    public async Task<bool> ResourcesCurrentAsync(int tenant, PairingResourceConstraints constraints, CancellationToken ct)
    {
        if (constraints.TenantId != tenant.ToString(CultureInfo.InvariantCulture) || constraints.OrganizationId is not null || constraints.CustomerIds.Length != 0 || constraints.RequestIds.Length != 0 || constraints.TaskIds.Length != 0 || constraints.ResourceIds is null || constraints.RequestDefinitionIds is null) return false;
        var resources = new List<Guid>(); var jobs = new List<long>();
        foreach (var value in constraints.ResourceIds) { if (!Guid.TryParseExact(value, "D", out var id) || resources.Contains(id)) return false; resources.Add(id); }
        foreach (var value in constraints.RequestDefinitionIds) { if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0 || jobs.Contains(id)) return false; jobs.Add(id); }
        if (await db.Agents.AsNoTracking().CountAsync(x => resources.Contains(x.Id) && x.TenantId == tenant && x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.DeletedAtUtc == null && x.SupersededAtUtc == null && x.SupersededByAgentId == null, ct) != resources.Count) return false;
        return await db.Jobs.AsNoTracking().CountAsync(x => jobs.Contains(x.Id) && x.TenantId == tenant && x.AgentId != null && resources.Contains(x.AgentId.Value), ct) == jobs.Count;
    }
}
