using System.Data;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Identity.Authorization;

/// <summary>
/// Defines a recoverable instance administrator and serializes mutations that
/// could remove the final recoverable administrator.
/// </summary>
public sealed class InstanceAdministratorInvariant(NetRatelIdentityDbContext db)
{
    // This lock is intentionally database-scoped. It coordinates independent
    // API processes without adding an in-process ownership assumption.
    private const long AdvisoryLockId = 0x4E6574526174656C;

    public async Task<int> ViableAdministratorCountAsync(CancellationToken cancellationToken)
    {
        var localAdministrators =
            from user in db.Users
            join principal in db.ApplicationPrincipals on user.PrincipalId equals principal.Id
            where user.IsEnabled && user.IsInstanceAdministrator
            select principal.Id;

        var roleAdministrators =
            from assignment in db.PrincipalRoleAssignments
            join role in db.AccessRoles on assignment.RoleId equals role.Id
            join principal in db.ApplicationPrincipals on assignment.PrincipalId equals principal.Id
            where assignment.TenantId == null && role.IsInstanceAdministratorRole
            where db.Users.Any(user => user.PrincipalId == principal.Id && user.IsEnabled)
                  || (!string.IsNullOrWhiteSpace(principal.ExternalIssuer) &&
                      !string.IsNullOrWhiteSpace(principal.ExternalSubject))
            select principal.Id;

        return await localAdministrators
            .Concat(roleAdministrators)
            .Distinct()
            .CountAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<T> ExecuteDestructiveMutationAsync<T>(Func<CancellationToken, Task<T>> mutation, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational())
            return await mutation(cancellationToken).ConfigureAwait(false);

        // PostgreSQL fixes a serializable snapshot before pg_advisory_xact_lock
        // can wait. The transaction-scoped lock is the serializing primitive
        // there, so take the snapshot only after acquiring it at ReadCommitted.
        var isolationLevel = db.Database.IsNpgsql()
            ? IsolationLevel.ReadCommitted
            : IsolationLevel.Serializable;
        await using var transaction = await db.Database
            .BeginTransactionAsync(isolationLevel, cancellationToken)
            .ConfigureAwait(false);

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                    $"SELECT pg_advisory_xact_lock({AdvisoryLockId})",
                    cancellationToken)
                .ConfigureAwait(false);
        }

        var result = await mutation(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }
}
