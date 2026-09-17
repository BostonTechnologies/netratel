using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Fanout;

namespace NetRatel.Tests.API;

internal sealed class ThrowingShadowFanoutSink : IShadowFanoutSink
{
    public TaskCompletionSource Called { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public bool TryEnqueue(ShadowFanoutEnvelope envelope)
    {
        Called.TrySetResult();
        throw new InvalidOperationException("controlled diagnostic fanout failure");
    }

    public SignalRShadowFanoutStatus GetStatus() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null);
}
