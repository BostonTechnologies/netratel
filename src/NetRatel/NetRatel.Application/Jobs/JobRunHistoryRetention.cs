namespace NetRatel.Application.Jobs;

public sealed class JobAggregateHistoryRetainedException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

/// <summary>Aggregate/native identity and immutable replay/output bindings are retained together.</summary>
public static class JobRunHistoryRetention
{
    public static void RequireDeletable(bool isAggregate, bool isInternalChild)
    {
        if (isInternalChild)
            throw new JobAggregateHistoryRetainedException("aggregate_node_history_retained",
                "Native node history belongs to its aggregate parent and is retained for receipts, recovery and isolated output.");
        if (isAggregate)
            throw new JobAggregateHistoryRetainedException("aggregate_history_retained",
                "Aggregate history and its native receipt/replay identities are retained. Cancel active work through the parent; deleting history cannot stop physical execution.");
    }
}
