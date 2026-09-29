using System.Collections.Concurrent;
using NetRatel.API.Realtime;
using NetRatel.Application.Fanout;

namespace NetRatel.Tests.API;

internal sealed class RecordingRealtimeFanoutSink : IRealtimeFanoutSink
{
    private readonly ConcurrentQueue<RealtimeFanoutEnvelope> _envelopes = new();

    public IReadOnlyCollection<RealtimeFanoutEnvelope> Envelopes => _envelopes.ToArray();

    public bool TryEnqueue(RealtimeFanoutEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        _envelopes.Enqueue(envelope);
        return true;
    }

    public RealtimeFanoutHealthStatus GetStatus() =>
        new(false, 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null);
}
