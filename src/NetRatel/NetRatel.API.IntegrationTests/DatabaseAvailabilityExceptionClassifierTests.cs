using AwesomeAssertions;
using NetRatel.Infrastructure.Persistence;
using Npgsql;
using System.IO;
using System.Net.Sockets;
using Xunit;

public sealed class DatabaseAvailabilityExceptionClassifierTests
{
    [Fact]
    public void AvailabilityClassifierAcceptsOnlyKnownDependencyAvailabilityFailures()
    {
        var stoppedDatabase = new InvalidOperationException(
            "Execution strategy wrapper.",
            CreatePostgresException("55000", "CheckMyDatabase"));

        DatabaseExceptionClassifier.IsAvailabilityFailure(stoppedDatabase).Should().BeTrue();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("08006", null)).Should().BeTrue();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new SocketException((int)SocketError.ConnectionRefused)).Should().BeTrue();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new NpgsqlException(
            "Provider wrapper around a network failure.",
            new IOException("connection reset"))).Should().BeTrue();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new TimeoutException("database operation timed out")).Should().BeTrue();

        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("55000", "UnrelatedOperation")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException(PostgresErrorCodes.UniqueViolation, "_bt_check_unique")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("28000", null)).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("40001", "ExecConstraints")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("40P01", "DeadLockReport")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(CreatePostgresException("55P03", "LockAcquire")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new NpgsqlException(
            "Provider wrapper around a non-availability server error.",
            CreatePostgresException("55000", "UnrelatedOperation"))).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new InvalidOperationException("application failure")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new OperationCanceledException()).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new NpgsqlException(
            "Provider wrapper around caller cancellation.",
            new OperationCanceledException("the request was cancelled", new TimeoutException("the request was cancelled")))).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new FileNotFoundException("unrelated file error")).Should().BeFalse();
        DatabaseExceptionClassifier.IsAvailabilityFailure(new AggregateException(
            new SocketException((int)SocketError.ConnectionRefused),
            new InvalidOperationException("unrelated failure"))).Should().BeFalse();
    }

    private static PostgresException CreatePostgresException(string sqlState, string? routine) =>
        routine is null
            ? new PostgresException("Database operation failed.", "FATAL", "FATAL", sqlState)
            : new PostgresException(
                "Database operation failed.",
                "FATAL",
                "FATAL",
                sqlState,
                null,
                null,
                0,
                0,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                null,
                routine);
}
