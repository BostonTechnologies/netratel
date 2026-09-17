using System.Collections.Concurrent;

namespace NetRatel.API.Services.Jobs;

public sealed class JobRunExecutionRegistry
{
    private readonly ConcurrentDictionary<ulong, CancellationTokenSource> _activeRuns = new();

    public JobRunExecutionLease Register(ulong runId, CancellationToken parentToken = default)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);

        if (!_activeRuns.TryAdd(runId, cts))
        {
            cts.Dispose();
            throw new InvalidOperationException($"JobRun {runId} is already registered as active.");
        }

        return new JobRunExecutionLease(this, runId, cts);
    }

    public bool IsActive(ulong runId) => _activeRuns.ContainsKey(runId);

    public bool Cancel(ulong runId)
    {
        if (!_activeRuns.TryGetValue(runId, out var cts))
        {
            return false;
        }

        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    private void Release(ulong runId, CancellationTokenSource cts)
    {
        if (_activeRuns.TryGetValue(runId, out var current) && ReferenceEquals(current, cts))
        {
            _activeRuns.TryRemove(runId, out _);
        }

        cts.Dispose();
    }

    public sealed class JobRunExecutionLease : IDisposable
    {
        private readonly JobRunExecutionRegistry _owner;
        private readonly ulong _runId;
        private CancellationTokenSource? _cts;

        internal JobRunExecutionLease(JobRunExecutionRegistry owner, ulong runId, CancellationTokenSource cts)
        {
            _owner = owner;
            _runId = runId;
            _cts = cts;
        }

        public CancellationToken Token => _cts?.Token ?? CancellationToken.None;

        public void Dispose()
        {
            var cts = Interlocked.Exchange(ref _cts, null);
            if (cts is null)
            {
                return;
            }

            _owner.Release(_runId, cts);
        }
    }
}
