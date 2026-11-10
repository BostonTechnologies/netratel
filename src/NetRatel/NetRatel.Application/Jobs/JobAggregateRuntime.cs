namespace NetRatel.Application.Jobs;

/// <summary>A durable scheduling turn. Physical node workflows remain owned by the native job runtime.</summary>
public interface IJobAggregatePump
{
    Task<JobAggregatePumpResult> PumpAsync(ulong parentRunId, CancellationToken cancellationToken);
}

public sealed record JobAggregatePumpResult(bool Active, bool Immediate = false);

public interface IJobAggregateWakeup
{
    Task WakeAsync(ulong parentRunId, CancellationToken cancellationToken);
}
