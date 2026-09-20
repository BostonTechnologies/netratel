using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public static class DatabaseExceptionClassifier
{
    public static bool IsUniqueViolation(DbUpdateException exception, string? postgresConstraint = null)
    {
        var root = exception.GetBaseException();
        return root switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var constraint }
                when postgresConstraint is null || string.Equals(constraint, postgresConstraint, StringComparison.Ordinal) => true,
            SqliteException { SqliteExtendedErrorCode: 1555 or 2067 } => true,
            _ => false
        };
    }
}
