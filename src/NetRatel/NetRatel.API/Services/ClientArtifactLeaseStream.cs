using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services;

/// <summary>A bounded download lease survives requests and drains safely after a process crash.</summary>
internal sealed class ClientArtifactLeaseStream(Stream inner, Guid leaseId, DateTimeOffset expiresAt,
    TimeProvider clock, DbContextOptions<OrchestratorDbContext> dbOptions) : Stream
{
    private bool _disposed;
    private void CheckLease()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (clock.GetUtcNow() >= expiresAt) throw new IOException("The archive download lease expired. Start a new download.");
    }
    public override bool CanRead => !_disposed && inner.CanRead;
    public override bool CanSeek => !_disposed && inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set { CheckLease(); inner.Position = value; } }
    public override int Read(byte[] buffer, int offset, int count) { CheckLease(); return inner.Read(buffer, offset, count); }
    public override int Read(Span<byte> buffer) { CheckLease(); return inner.Read(buffer); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    { CheckLease(); return inner.ReadAsync(buffer, cancellationToken); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { CheckLease(); return inner.ReadAsync(buffer, offset, count, cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin) { CheckLease(); return inner.Seek(offset, origin); }
    public override void Flush() => inner.Flush();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            inner.Dispose();
            try
            {
                using var db = new OrchestratorDbContext(dbOptions);
                var lease = db.ClientArtifactDownloadLeases.Find(leaseId);
                if (lease is not null) { db.Remove(lease); db.SaveChanges(); }
            }
            catch { /* The bounded lease expires even if cleanup cannot contact the database. */ }
        }
        base.Dispose(disposing);
    }
    public override async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await inner.DisposeAsync();
        try
        {
            await using var db = new OrchestratorDbContext(dbOptions);
            var lease = await db.ClientArtifactDownloadLeases.FindAsync(leaseId);
            if (lease is not null) { db.Remove(lease); await db.SaveChangesAsync(); }
        }
        catch { /* Recoverable expiry is authoritative when disposal cleanup fails. */ }
        GC.SuppressFinalize(this);
    }
}
