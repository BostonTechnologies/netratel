using NetRatel.API.Realtime;
using NetRatel.Application.Fanout;

namespace NetRatel.Tests.API;

internal sealed class ThrowingRealtimeFanoutSink : IRealtimeFanoutSink
{
    public TaskCompletionSource Called { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryEnqueue(RealtimeFanoutEnvelope envelope)
    {
        Called.TrySetResult();
        throw new InvalidOperationException("controlled diagnostic fanout failure");
    }

    public RealtimeFanoutHealthStatus GetStatus() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null);
}
