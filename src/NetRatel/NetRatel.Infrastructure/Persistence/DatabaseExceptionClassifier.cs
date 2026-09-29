using Microsoft.EntityFrameworkCore;
using System.Net.Sockets;
using Npgsql;

namespace NetRatel.Infrastructure.Persistence;

public static class DatabaseExceptionClassifier
{
    public static bool IsAvailabilityFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return exception switch
        {
            AggregateException aggregate => aggregate.InnerExceptions.Count > 0 &&
                aggregate.InnerExceptions.All(IsAvailabilityFailure),
            OperationCanceledException => false,
            PostgresException postgres => IsAvailabilitySqlState(postgres.SqlState, postgres.Routine),
            NpgsqlException { InnerException: IOException } => true,
            NpgsqlException { InnerException: { } inner } => IsAvailabilityFailure(inner),
            SocketException or TimeoutException => true,
            _ => exception.InnerException is not null && IsAvailabilityFailure(exception.InnerException)
        };
    }

    public static bool IsUniqueViolation(DbUpdateException exception, string? postgresConstraint = null)
    {
        var root = exception.GetBaseException();
        return root switch
        {
            PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: var constraint }
                when postgresConstraint is null || string.Equals(constraint, postgresConstraint, StringComparison.Ordinal) => true,
            _ => false
        };
    }

    private static bool IsAvailabilitySqlState(string sqlState, string? routine) =>
        sqlState.StartsWith("08", StringComparison.Ordinal) ||
        sqlState is "53300" or "57P01" or "57P02" or "57P03" or "57P04" ||
        (sqlState == "55000" && string.Equals(routine, "CheckMyDatabase", StringComparison.Ordinal));
}
