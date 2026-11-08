using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Options;
using NetRatel.Shared.Contracts.FileSystem;

namespace NetRatel.Web.Services.FileSystem;

public sealed record FileBrowserContentLease(Guid Id, string OperatorId, GatewayFileSystemStatResponse File,
    string ContentType, DateTimeOffset ExpiresAtUtc);

/// <summary>Reusable range leases hold only private metadata, never file bytes.</summary>
public sealed class FileBrowserContentRegistry(TimeProvider clock)
{
    private readonly ConcurrentDictionary<Guid, FileBrowserContentLease> _leases = new();
    private readonly object _admission = new();
    public FileBrowserContentLease Create(string operatorId, GatewayFileSystemStatResponse file, string contentType)
    {
        lock (_admission)
        {
            foreach (var pair in _leases)
                if (pair.Value.ExpiresAtUtc <= clock.GetUtcNow()) _leases.TryRemove(pair.Key, out _);
            if (_leases.Count >= 1024 || _leases.Values.Count(item => item.OperatorId == operatorId) >= 16)
                throw new InvalidOperationException("Close another file preview before opening more.");
            var lease = new FileBrowserContentLease(Guid.NewGuid(), operatorId, file, contentType, clock.GetUtcNow().AddMinutes(15));
            _leases[lease.Id] = lease;
            return lease;
        }
    }
    public bool TryGet(Guid id, string operatorId, out FileBrowserContentLease lease)
    {
        if (_leases.TryGetValue(id, out var found) && found.OperatorId == operatorId && found.ExpiresAtUtc > clock.GetUtcNow())
        { lease = found; return true; }
        lease = null!;
        return false;
    }
    public void Release(Guid id) => _leases.TryRemove(id, out _);
    public static string OperatorId(ClaimsPrincipal user) => user.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? user.FindFirstValue("sub") ?? user.Identity?.Name ?? throw new UnauthorizedAccessException();
}

public sealed class FileBrowserPreview(Stream? stream, string? url, GatewayFileSystemStatResponse file,
    string contentType, Action? release = null) : IAsyncDisposable
{
    public Stream? Stream { get; } = stream;
    public string? Url { get; } = url;
    public GatewayFileSystemStatResponse File { get; } = file;
    public string ContentType { get; } = contentType;
    public async ValueTask DisposeAsync()
    {
        if (Stream is not null) await Stream.DisposeAsync();
        release?.Invoke();
    }
}

public sealed class FileBrowserContentService(GatewayFileSystemApiService gateway, FileBrowserContentRegistry leases,
    AuthenticationStateProvider authentication, IOptions<FileBrowserOptions> options)
{
    public async Task<FileBrowserPreview> AcquireAsync(int tenantId, Guid agentId, string path, CancellationToken cancellationToken)
    {
        var stat = await gateway.StatAsync(tenantId, agentId, path, cancellationToken);
        if (stat.IsDirectory) throw new InvalidOperationException("Select a file to preview.");
        var type = SeekableType(path);
        if (type is not null && stat.CanReadRange && stat.ObservedVersion.Length > 0)
        {
            var identity = FileBrowserContentRegistry.OperatorId((await authentication.GetAuthenticationStateAsync()).User);
            cancellationToken.ThrowIfCancellationRequested();
            var lease = leases.Create(identity, stat, type);
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
