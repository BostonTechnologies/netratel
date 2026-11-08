using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

public sealed record FileBrowserContentLease(Guid Id, string OperatorId, GatewayFileSystemStatResponse File,
    string ContentType, DateTimeOffset ExpiresAtUtc, CancellationToken Lifetime);

/// <summary>Reusable range leases hold only private metadata, never file bytes.</summary>
public sealed class FileBrowserContentRegistry(TimeProvider clock) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, LeaseOwner> _leases = new();
    private readonly object _admission = new();
    public FileBrowserContentLease Create(string operatorId, GatewayFileSystemStatResponse file, string contentType, CancellationToken owner = default)
    {
        lock (_admission)
        {
            foreach (var pair in _leases)
                if (pair.Value.Lease.ExpiresAtUtc <= clock.GetUtcNow()) Release(pair.Key);
            if (_leases.Count >= 1024 || _leases.Values.Count(item => item.Lease.OperatorId == operatorId) >= 16)
                throw new InvalidOperationException("Close another file preview before opening more.");
            var lifetime = new CancellationTokenSource(TimeSpan.FromMinutes(15), clock);
            var registration = owner.Register(static state => ((CancellationTokenSource)state!).Cancel(), lifetime);
            var lease = new FileBrowserContentLease(Guid.NewGuid(), operatorId, file, contentType, clock.GetUtcNow().AddMinutes(15), lifetime.Token);
            _leases[lease.Id] = new(lease, lifetime, registration);
            return lease;
        }
    }
    public bool TryGet(Guid id, string operatorId, out FileBrowserContentLease lease)
    {
        if (_leases.TryGetValue(id, out var found) && found.Lease.OperatorId == operatorId &&
            found.Lease.ExpiresAtUtc > clock.GetUtcNow() && !found.Lease.Lifetime.IsCancellationRequested)
        { lease = found.Lease; return true; }
        lease = null!;
        return false;
    }
    public void Release(Guid id)
    {
        if (_leases.TryRemove(id, out var owner))
        {
            try { owner.Lifetime.Cancel(); }
            finally { owner.Registration.Dispose(); owner.Lifetime.Dispose(); }
        }
    }
    public void Dispose() { foreach (var id in _leases.Keys) Release(id); }
    private sealed record LeaseOwner(FileBrowserContentLease Lease, CancellationTokenSource Lifetime, CancellationTokenRegistration Registration);
    public static string OperatorId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub") ?? user.FindFirstValue("preferred_username") ?? user.Identity?.Name ?? throw new UnauthorizedAccessException();
}

public sealed class FileBrowserPreview(Stream? stream, string? url, GatewayFileSystemStatResponse file,
    string contentType, Action? release = null) : IAsyncDisposable
{
    private int _disposed;
    public Stream? Stream { get; } = stream;
    public string? Url { get; } = url;
    public GatewayFileSystemStatResponse File { get; } = file;
    public string ContentType { get; } = contentType;
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { if (Stream is not null) await Stream.DisposeAsync(); }
        finally { release?.Invoke(); }
    }
}

public sealed class FileBrowserContentService(GatewayFileSystemApiService gateway, FileBrowserContentRegistry leases,
    AuthenticationStateProvider authentication, IOptions<FileBrowserOptions> options, FileBrowserCircuitLifetime? circuit = null)
{
    public async Task<FileBrowserPreview> AcquireAsync(int tenantId, Guid agentId, string path, CancellationToken cancellationToken)
    {
        using var acquisition = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, circuit?.Token ?? CancellationToken.None);
        cancellationToken = acquisition.Token;
        var stat = await gateway.StatAsync(tenantId, agentId, path, cancellationToken);
        if (stat.IsDirectory) throw new InvalidOperationException("Select a file to preview.");
        var type = SeekableType(path);
        if (type == "application/pdf" && stat.SizeBytes > 64L * 1024 * 1024)
            throw new InvalidOperationException("PDF preview is limited to 64 MiB of range-cache input. Use Download.");
        if (type is not null && stat.CanReadRange && stat.ObservedVersion.Length > 0)
        {
            var identity = FileBrowserContentRegistry.OperatorId((await authentication.GetAuthenticationStateAsync()).User);
            cancellationToken.ThrowIfCancellationRequested();
            var lease = leases.Create(identity, stat, type, circuit?.Token ?? CancellationToken.None);
            return new(null, $"/file-browser/content/{lease.Id:D}", stat, type, () => leases.Release(lease.Id));
        }
        var limit = options.Value.MaxPreviewBytes;
        if (stat.SizeBytes > limit) throw new InvalidOperationException($"This file exceeds the {limit / 1024 / 1024} MiB whole-file preview limit. Use Download.");
        using var response = await gateway.DownloadAsync(tenantId, agentId, path, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        var memory = new MemoryStream();
        try
        {
            var buffer = new byte[16 * 1024];
            int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            {
                if (memory.Length + read > limit) throw new InvalidOperationException("The file grew beyond the preview limit. Use Download.");
                await memory.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
            var current = await gateway.StatAsync(tenantId, agentId, path, cancellationToken);
            if (stat.ObservedVersion != current.ObservedVersion || memory.Length != stat.SizeBytes)
                throw new InvalidOperationException("The remote file changed. Refresh and open it again.");
            memory.Position = 0;
            return new(memory, null, stat, type ?? "application/octet-stream");
        }
        catch { await memory.DisposeAsync(); throw; }
    }

    public static string? SeekableType(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".pdf" => "application/pdf", ".mp4" => "video/mp4", ".webm" => "video/webm",
        ".mp3" => "audio/mpeg", ".ogg" => "audio/ogg", ".wav" => "audio/wav", _ => null
    };
}
