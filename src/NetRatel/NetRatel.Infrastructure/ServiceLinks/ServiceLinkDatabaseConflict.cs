using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NetRatel.Infrastructure.ServiceLinks;

/// <summary>Recognizes PostgreSQL transactions that definitely aborted because of contention.</summary>
public static class ServiceLinkDatabaseConflict
{
    public static bool IsAbortedTransaction(Exception error)
    {
        // Npgsql's non-retrying EF execution strategy wraps a transient SaveChanges
        // failure in InvalidOperationException -> DbUpdateException. Inspect only
        // those known wrappers; unrelated application failures remain failures.
        for (var depth = 0; depth < 4; depth++)
        {
            if (error is PostgresException postgres)
                return postgres.SqlState is PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected;
            if (error is not (InvalidOperationException or DbUpdateException) || error.InnerException is null)
                return false;
            error = error.InnerException;
        }
        return false;
    }
}
