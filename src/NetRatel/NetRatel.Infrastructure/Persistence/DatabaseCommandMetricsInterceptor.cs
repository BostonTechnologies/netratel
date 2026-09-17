using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace NetRatel.Infrastructure.Persistence;

/// <summary>Captures request-scoped database command counts and elapsed time for endpoint diagnostics.</summary>
public sealed class DatabaseCommandMetricsInterceptor : DbCommandInterceptor
{
    private int _commandCount;
    private long _elapsedTicks;

    public void Reset()
    {
        Interlocked.Exchange(ref _commandCount, 0);
        Interlocked.Exchange(ref _elapsedTicks, 0);
    }

    public DatabaseCommandMetricsSnapshot Snapshot() =>
        new(Volatile.Read(ref _commandCount), TimeSpan.FromTicks(Volatile.Read(ref _elapsedTicks)));

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result)
    {
        CountCommand();
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        CountCommand();
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        RecordElapsed(eventData.Duration);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        RecordElapsed(eventData.Duration);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    private void CountCommand() => Interlocked.Increment(ref _commandCount);

    private void RecordElapsed(TimeSpan elapsed) => Interlocked.Add(ref _elapsedTicks, elapsed.Ticks);
}

public readonly record struct DatabaseCommandMetricsSnapshot(int CommandCount, TimeSpan Elapsed);
