using System.Collections.Concurrent;

namespace NetRatel.Web.Services.FileSystem;

public sealed record FileBrowserDownloadTransferStatus(
    Guid TransferId,
    string FileName,
    string State,
    long BytesTransferred,
    long? ExpectedBytes,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    string? FailureCode);

public sealed record FileBrowserDownloadTransferLease(Guid TransferId, CancellationToken CancellationToken);

public interface IFileBrowserDownloadTransferRegistry
{
    FileBrowserDownloadTransferLease Begin(string operatorId, Guid transferId, string fileName, long? expectedBytes);
    bool TryGet(string operatorId, Guid transferId, out FileBrowserDownloadTransferStatus status);
    bool TryCancel(string operatorId, Guid transferId);
    void CleanupExpired();
    void Cancelled(Guid transferId, long bytesTransferred);
    void Streaming(Guid transferId);
    void Progress(Guid transferId, long bytesTransferred);
    void Complete(Guid transferId, long bytesTransferred);
    void Fail(Guid transferId, string failureCode, long bytesTransferred);
}

/// <summary>
/// Holds small, operator-owned download state only. Browser attachment bytes
/// remain on the HTTP response path and are never retained by this registry.
/// </summary>
public sealed class FileBrowserDownloadTransferRegistry(TimeProvider timeProvider) : IFileBrowserDownloadTransferRegistry
{
    private const int MaximumTransfersPerOperator = 8;
    private static readonly TimeSpan ActiveLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan TerminalLifetime = TimeSpan.FromMinutes(2);
    private readonly ConcurrentDictionary<Guid, Entry> _entries = new();

    public FileBrowserDownloadTransferLease Begin(string operatorId, Guid transferId, string fileName, long? expectedBytes)
    {
        var now = timeProvider.GetUtcNow();
        Cleanup(now);
        var active = _entries.Values.Count(entry => string.Equals(entry.OperatorId, operatorId, StringComparison.Ordinal));
        if (active >= MaximumTransfersPerOperator)
        {
            throw new InvalidOperationException("The maximum number of active file downloads has been reached.");
        }

        var entry = new Entry(operatorId, fileName, expectedBytes, now);
        if (!_entries.TryAdd(transferId, entry))
        {
            throw new InvalidOperationException("The file download transfer identifier is already in use.");
        }

        return new FileBrowserDownloadTransferLease(transferId, entry.Cancellation.Token);
    }

    public bool TryGet(string operatorId, Guid transferId, out FileBrowserDownloadTransferStatus status)
    {
        CleanupExpired();
        if (_entries.TryGetValue(transferId, out var entry) && string.Equals(entry.OperatorId, operatorId, StringComparison.Ordinal))
        {
            status = entry.ToStatus(transferId);
            return true;
        }

        status = null!;
        return false;
    }

    public bool TryCancel(string operatorId, Guid transferId)
    {
        if (!_entries.TryGetValue(transferId, out var entry) || !string.Equals(entry.OperatorId, operatorId, StringComparison.Ordinal))
        {
            return false;
        }

        entry.Cancel(timeProvider.GetUtcNow());
        return true;
    }

    public void CleanupExpired() => Cleanup(timeProvider.GetUtcNow());

    public void Streaming(Guid transferId) => Update(transferId, entry => entry.Streaming(timeProvider.GetUtcNow()));
    public void Progress(Guid transferId, long bytesTransferred) => Update(transferId, entry => entry.Progress(bytesTransferred, timeProvider.GetUtcNow()));
    public void Complete(Guid transferId, long bytesTransferred) => Update(transferId, entry => entry.Complete(bytesTransferred, timeProvider.GetUtcNow()));
    public void Cancelled(Guid transferId, long bytesTransferred) => Update(transferId, entry => entry.Cancelled(bytesTransferred, timeProvider.GetUtcNow()));
    public void Fail(Guid transferId, string failureCode, long bytesTransferred) => Update(transferId, entry => entry.Fail(failureCode, bytesTransferred, timeProvider.GetUtcNow()));

    private void Update(Guid transferId, Action<Entry> update)
    {
        if (_entries.TryGetValue(transferId, out var entry))
        {
            update(entry);
        }
    }

    private void Cleanup(DateTimeOffset now)
    {
        foreach (var (transferId, entry) in _entries)
        {
            if (entry.ExpiresAtUtc <= now && _entries.TryRemove(transferId, out var removed))
            {
                removed.Dispose();
            }
        }
    }

    private sealed class Entry(string operatorId, string fileName, long? expectedBytes, DateTimeOffset startedAtUtc) : IDisposable
    {
        private readonly object _sync = new();
        public string OperatorId { get; } = operatorId;
        public CancellationTokenSource Cancellation { get; } = new();
        public string FileName { get; } = fileName;
        public long? ExpectedBytes { get; } = expectedBytes is >= 0 ? expectedBytes : null;
        public string State { get; private set; } = "preparing";
        public long BytesTransferred { get; private set; }
        public DateTimeOffset StartedAtUtc { get; } = startedAtUtc;
        public DateTimeOffset UpdatedAtUtc { get; private set; } = startedAtUtc;
        public DateTimeOffset ExpiresAtUtc { get; private set; } = startedAtUtc + ActiveLifetime;
        public string? FailureCode { get; private set; }

        public void Streaming(DateTimeOffset now) => Update("streaming", BytesTransferred, null, now, ActiveLifetime);
        public void Progress(long bytes, DateTimeOffset now) => Update("streaming", bytes, null, now, ActiveLifetime);
        public void Complete(long bytes, DateTimeOffset now) => Update("completed", bytes, null, now, TerminalLifetime);
        public void Cancelled(long bytes, DateTimeOffset now) => Update("cancelled", bytes, "cancelled", now, TerminalLifetime);
        public void Fail(string code, long bytes, DateTimeOffset now) => Update("failed", bytes, code, now, TerminalLifetime);
        public void Cancel(DateTimeOffset now)
        {
            Cancellation.Cancel();
            Cancelled(BytesTransferred, now);
        }

        public FileBrowserDownloadTransferStatus ToStatus(Guid transferId)
        {
            lock (_sync)
            {
                return new FileBrowserDownloadTransferStatus(transferId, FileName, State, BytesTransferred, ExpectedBytes, StartedAtUtc, UpdatedAtUtc, FailureCode);
            }
        }

        private void Update(string state, long bytes, string? failureCode, DateTimeOffset now, TimeSpan lifetime)
        {
            lock (_sync)
            {
                State = state;
                BytesTransferred = Math.Max(0, bytes);
                FailureCode = failureCode;
                UpdatedAtUtc = now;
                ExpiresAtUtc = now + lifetime;
            }
        }

        public void Dispose() => Cancellation.Dispose();
    }
}
