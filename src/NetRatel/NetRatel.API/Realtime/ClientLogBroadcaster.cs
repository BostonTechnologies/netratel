using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace NetRatel.API.Realtime;

public sealed class ClientLogBroadcaster
{
    private sealed class Subscription
    {
        public readonly string ClientIdentity;
        public readonly ConcurrentQueue<string> Queue = new();
        public readonly SemaphoreSlim Signal = new(0, int.MaxValue);
        public volatile bool Completed;

        public Subscription(string clientIdentity)
        {
            ClientIdentity = clientIdentity;
        }

        public void Enqueue(string item)
        {
            if (Completed) return;
            Queue.Enqueue(item);
            try { Signal.Release(); } catch (SemaphoreFullException) { }
        }

        public void Complete()
        {
            Completed = true;
            try { Signal.Release(); } catch { }
        }
    }

    private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();

    public ValueTask PublishAsync(string clientIdentity, string sseLine)
    {
        foreach (var sub in _subs.Values)
        {
            if (string.Equals(sub.ClientIdentity, clientIdentity, StringComparison.OrdinalIgnoreCase))
            {
                sub.Enqueue(sseLine);
            }
        }

        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<string> StreamAsync(string clientIdentity, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var sub = new Subscription(clientIdentity);
        _subs[id] = sub;
        return ReadAsync(id, sub, ct);
    }

    private async IAsyncEnumerable<string> ReadAsync(Guid id, Subscription sub, [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            while (true)
            {
                while (sub.Queue.TryDequeue(out var item))
                {
                    yield return item;
                }

                if (sub.Completed || ct.IsCancellationRequested)
                {
                    yield break;
                }

                try
                {
                    await sub.Signal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
        finally
        {
            if (_subs.TryRemove(id, out var removed))
            {
                removed.Complete();
            }
        }
    }
}
