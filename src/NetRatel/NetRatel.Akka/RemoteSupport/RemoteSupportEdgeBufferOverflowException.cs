namespace NetRatel.Akka.RemoteSupport;

/// <summary>
/// Signals that a bounded live Remote Support edge could not accept a reliable
/// envelope. The edge is closed so callers cannot mistake dropped data for a
/// healthy stream.
/// </summary>
public sealed class RemoteSupportEdgeBufferOverflowException(string edgeKind)
    : InvalidOperationException($"The {edgeKind} Remote Support edge buffer is full.")
{
    public string EdgeKind { get; } = edgeKind;

    public static bool Is(Exception? exception)
    {
        if (exception is RemoteSupportEdgeBufferOverflowException)
        {
            return true;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.InnerExceptions.Any(Is);
        }

        return exception?.InnerException is { } inner && Is(inner);
    }
}
