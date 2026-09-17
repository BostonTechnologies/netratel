using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading;

namespace NetRatel.API.Realtime;

public sealed class ClientUpdateBroadcaster
{
    private sealed class Subscription
    {
        public readonly ConcurrentQueue<string> Queue = new();
        public readonly SemaphoreSlim Signal = new(0, int.MaxValue);
        public volatile bool Completed;

        public void Enqueue(string item)
        {
            if (Completed) return;
            Queue.Enqueue(item);
            try { Signal.Release(); } catch (SemaphoreFullException) { /* benign */ }
        }

        public void Complete()
        {
            Completed = true;
            try { Signal.Release(); } catch { /* benign */ }
        }
    }

    private readonly ConcurrentDictionary<Guid, Subscription> _subs = new();

    public ValueTask PublishAsync(string sseLine)
    {
        foreach (var sub in _subs.Values)
            sub.Enqueue(sseLine);
        return ValueTask.CompletedTask;
    }

    public IAsyncEnumerable<string> StreamAsync(CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var sub = new Subscription();
        _subs[id] = sub;

        return ReadAsync(id, sub, ct);
    }

    private async IAsyncEnumerable<string> ReadAsync(
        Guid id,
        Subscription sub,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            while (true)
            {
                // Drain any queued items first
                while (sub.Queue.TryDequeue(out var item))
                    yield return item;

                if (sub.Completed || ct.IsCancellationRequested)
                    yield break;

                try
                {
                    // Wait for the next signal; if the client navigates away this cancels
                    await sub.Signal.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Normal: client disconnected / navigated away
                    yield break;
                }
            }
        }
        finally
        {
            if (_subs.TryRemove(id, out var s))
                s.Complete();
        }
    }
}
