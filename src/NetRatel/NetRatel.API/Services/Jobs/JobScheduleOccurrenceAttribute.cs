using Hangfire.Common;
using Hangfire.Server;

namespace NetRatel.API.Services.Jobs;

/// <summary>
/// Supplies the real Hangfire occurrence to retained serialized dispatcher signatures. No
/// timestamp or recurring-job ID is used: retries of the background job retain its actual ID.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class JobScheduleOccurrenceAttribute : JobFilterAttribute, IServerFilter
{
    private const string ScopeItem = "NetRatel.JobScheduleOccurrenceScope";

    public void OnPerforming(PerformingContext context)
    {
        context.Items[ScopeItem] = JobScheduleOccurrenceContext.Enter(context.BackgroundJob.Id);
    }

    public void OnPerformed(PerformedContext context)
    {
        if (context.Items.TryGetValue(ScopeItem, out var scope) && scope is IDisposable lifetime) lifetime.Dispose();
    }
}

internal static class JobScheduleOccurrenceContext
{
    private static readonly AsyncLocal<string?> Occurrence = new();
    public static string? Current => Occurrence.Value;

    internal static IDisposable Enter(string occurrenceId)
    {
        if (string.IsNullOrWhiteSpace(occurrenceId)) throw new ArgumentException("A real background-job occurrence is required.");
        var previous = Occurrence.Value; Occurrence.Value = occurrenceId;
        return new Scope(previous);
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Occurrence.Value = previous;
    }
}
