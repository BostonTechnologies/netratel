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

    public static async Task<bool> HasConsumersAsync(OrchestratorDbContext db, int tenantId, Guid connectionId, string? locator,
        CancellationToken ct) => await Profiles(db, tenantId, connectionId, locator).AnyAsync(ct) ||
        await Jobs(db, tenantId, connectionId, locator).AnyAsync(ct) ||
        await HasActiveRunsAsync(db, tenantId, connectionId, locator, ct);

    public static async Task<string[]> FindConsumersAsync(OrchestratorDbContext db, int tenantId, Guid connectionId, string locator,
        CancellationToken ct)
    {
        var known = await Profiles(db, tenantId, connectionId, locator).Select(x => x.DisplayName)
            .Union(Jobs(db, tenantId, connectionId, locator).Select(_ => "Job parameter")).OrderBy(x => x).Take(20).ToArrayAsync(ct);
        return await HasActiveRunsAsync(db, tenantId, connectionId, locator, ct) ? ["Active job run", .. known.Take(19)] : known;
    }

    private static Task<bool> HasActiveRunsAsync(OrchestratorDbContext db, int tenantId, Guid connectionId, string? locator, CancellationToken ct) =>
        db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM "JobAggregateControls" control
                JOIN "JobRuns" run ON run."Id" = control."ParentRunId"
                CROSS JOIN LATERAL jsonb_array_elements(control."ProviderInputBindingsJson"::jsonb) binding
                WHERE run."TenantId" = {tenantId} AND control."TerminalReadyAtUtc" IS NULL
                  AND binding->'reference'->>'connectionId' = {connectionId.ToString("D")}
                  AND ({locator}::text IS NULL OR binding->'reference'->>'locator' = {locator})
            ) AS "Value"
            """).SingleAsync(ct);

    private static IQueryable<JobParameterDefinition> Jobs(OrchestratorDbContext db, int tenantId, Guid connectionId, string? locator) =>
        db.JobParameters.AsNoTracking().Where(parameter => parameter.Job.TenantId == tenantId &&
            parameter.ProviderDefaultConnectionId == connectionId && (locator == null || parameter.ProviderDefaultLocator == locator));

    private static IQueryable<CredentialProfileRecord> Profiles(OrchestratorDbContext db, int tenantId, Guid connectionId,
        string? locator) => db.CredentialProfiles.AsNoTracking().Where(profile => profile.TenantId == tenantId &&
            (profile.UsernameConnectionId == connectionId && (locator == null || profile.UsernameLocator == locator) ||
             profile.PasswordConnectionId == connectionId && (locator == null || profile.PasswordLocator == locator)));
}
