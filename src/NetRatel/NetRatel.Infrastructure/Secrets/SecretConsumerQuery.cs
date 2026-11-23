using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using static NetRatel.Infrastructure.Secrets.SecretProviderValidation;

namespace NetRatel.Infrastructure.Secrets;

internal static class SecretConsumerQuery
{
    public static async Task LockTenantAsync(OrchestratorDbContext db, int tenantId, CancellationToken ct, bool requireTenant = true)
    {
        var locked = await db.Database.SqlQuery<int>($"""
            SELECT "Id" AS "Value" FROM "Tenants" WHERE "Id" = {tenantId} FOR UPDATE
            """).SingleOrDefaultAsync(ct);
        if (locked == 0 && requireTenant)
            throw Missing();
    }

    public static Task<bool> HasConsumersAsync(OrchestratorDbContext db, int tenantId, Guid connectionId, string? locator,
        CancellationToken ct) => Profiles(db, tenantId, connectionId, locator).AnyAsync(ct);

    public static Task<string[]> FindConsumersAsync(OrchestratorDbContext db, int tenantId, Guid connectionId, string locator,
        CancellationToken ct) => Profiles(db, tenantId, connectionId, locator).OrderBy(x => x.DisplayName).ThenBy(x => x.Id)
        .Select(x => x.DisplayName).Take(20).ToArrayAsync(ct);

    private static IQueryable<CredentialProfileRecord> Profiles(OrchestratorDbContext db, int tenantId, Guid connectionId,
        string? locator) => db.CredentialProfiles.AsNoTracking().Where(profile => profile.TenantId == tenantId &&
            (profile.UsernameConnectionId == connectionId && (locator == null || profile.UsernameLocator == locator) ||
             profile.PasswordConnectionId == connectionId && (locator == null || profile.PasswordLocator == locator)));
}
