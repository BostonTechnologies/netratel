using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.Channels;
using Google.Protobuf;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>
/// Owns the bounded, transient transport state for one agent file stream.
/// It never persists file bytes or paths in actor state: request metadata is
/// fenced to the active presence lease and bytes remain in bounded channels.
/// </summary>
public interface IAgentFileGatewaySessionRegistry
{
    AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch);

    AgentFileGatewayRegistration Register(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        IReadOnlySet<string> capabilities) =>
        Register(client, connectionId, connectionEpoch);

    AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch,
        IReadOnlySet<string> capabilities, bool provisional) => Register(client, connectionId, connectionEpoch, capabilities);

    /// <summary>
    /// Returns only bounded, transport-local readiness facts for an admitted
    /// stream. It deliberately excludes paths, requests, and transferred data.
    /// </summary>
    GatewayFileGatewayAvailability? GetAvailability(ClientKey client) => null;

    Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken);

    Task<IReadOnlyList<GatewayFileEntry>> ListAsync(
        ClientKey client,
        string path,
        int pageSize,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        ListAsync(client, path, pageSize, cancellationToken);

    Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken);

    Task<GatewayFileReadOperation> ReadAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        ReadAsync(client, path, cancellationToken);

    Task<GatewayFileMetadata> StatAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        Task.FromException<GatewayFileMetadata>(new AgentFileGatewayOperationException("capability_unavailable"));

    Task<GatewayFileMetadata> StatAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        StatAsync(client, path, cancellationToken);

    Task CreateDirectoryAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        Task.FromException(new AgentFileGatewayOperationException("capability_unavailable"));

    Task CreateDirectoryAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        CreateDirectoryAsync(client, path, cancellationToken);

    Task DeleteAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        Task.FromException(new AgentFileGatewayOperationException("capability_unavailable"));

    Task DeleteAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        DeleteAsync(client, path, cancellationToken);

    Task CopyAsync(
        ClientKey client,
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        Task.FromException(new AgentFileGatewayOperationException("capability_unavailable"));

    Task MoveAsync(
        ClientKey client,
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        Task.FromException(new AgentFileGatewayOperationException("capability_unavailable"));

    Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken);

    Task WriteAsync(
        ClientKey client,
        string path,
        Stream source,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        WriteAsync(client, path, source, cancellationToken);

    bool TryAccept(ClientKey client, FileRequestAccepted accepted);

    bool TryAddPage(ClientKey client, FileListPage page);

    Task<bool> TryAddReadChunkAsync(ClientKey client, FileTransferChunk chunk, CancellationToken cancellationToken);

    bool TrySetMetadata(ClientKey client, FileMetadata metadata) => false;

    bool TryComplete(ClientKey client, FileRequestCompleted completed);

    bool TryFail(ClientKey client, FileRequestFailed failed);
}

public sealed record GatewayFileEntry(string Name, string FullPath, bool IsDirectory, long SizeBytes);

/// <summary>
/// Bounded metadata for one canonical file or directory. It intentionally
/// excludes file content and content hashes so a stat request never turns
/// into an unbounded read on the admitted agent.
/// </summary>
public sealed record GatewayFileMetadata(
    string FullPath,
    bool IsDirectory,
    long SizeBytes,
    DateTimeOffset LastModifiedUtc,
    string MimeType);

/// <summary>
/// Server-issued physical-root constraint for an operator-policy file request.
/// It is carried over the authenticated gateway stream so the agent can reject
/// link and reparse-point escapes at the only machine able to resolve them.
/// </summary>
public sealed record GatewayFileAccessPolicy(IReadOnlyList<string> AllowedRoots);

/// <summary>
/// Bounded readiness facts for one active file gateway stream. Connection IDs
/// remain server-local; callers receive only whether they match presence.
/// </summary>
public sealed record GatewayFileGatewayAvailability(
    Guid ConnectionId,
    ulong ConnectionEpoch,
    IReadOnlyList<string> NegotiatedCapabilities,
    DateTimeOffset RegisteredAtUtc);

/// <summary>
/// Server-issued, independently constrained roots for a dual-path operation.
/// Copy reads only from <see cref="SourceAllowedRoots"/> and writes only to
/// <see cref="DestinationAllowedRoots"/>. Move constrains both ends to write
/// roots because it removes the source after creating the destination.
/// </summary>
public sealed record GatewayFileMoveCopyAccessPolicy(
    IReadOnlyList<string> SourceAllowedRoots,
    IReadOnlyList<string> DestinationAllowedRoots);

public sealed class AgentFileGatewaySessionUnavailableException(ClientKey client, string code = "file_gateway_session_unavailable")
    : InvalidOperationException($"No active file gateway session exists for tenant {client.TenantId}, agent {client.AgentId:D}. Reason: {code}.")
{
    public string Code { get; } = code;
}

/// <summary>
/// A non-sensitive filesystem outcome reported by the admitted remote client.
/// The code is safe to surface to an operator at the API edge.
/// </summary>
public sealed class AgentFileGatewayOperationException(string code)
    : InvalidOperationException($"The remote file operation failed: {code}.")
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code) ? "operation_failed" : code;
}

public sealed class AgentFileGatewaySessionRegistry(IClientPresenceRouter presenceRouter)
    : IAgentFileGatewaySessionRegistry
{
    private const int MaximumPageSize = 256;
    private readonly ConcurrentDictionary<ClientKey, AgentFileGatewaySession> _sessions = new();

    public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) =>
        Register(client, connectionId, connectionEpoch, new HashSet<string>(StringComparer.Ordinal));

    public AgentFileGatewayRegistration Register(
        ClientKey client,
        Guid connectionId,
        ulong connectionEpoch,
        IReadOnlySet<string> capabilities) => Register(client, connectionId, connectionEpoch, capabilities, provisional: false);

    public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch,
        IReadOnlySet<string> capabilities, bool provisional)
    {
        var session = new AgentFileGatewaySession(
            client,
            connectionId,
            connectionEpoch,
            presenceRouter,
            capabilities.Contains("file-policy-roots-v1"),
            capabilities.Contains("file-stat-v1"),
            capabilities.Contains("file-create-directory-v1"),
            capabilities.Contains("file-delete-v1"),
            capabilities.Contains("file-copy-v1"),
            capabilities.Contains("file-move-v1"),
            NormalizeCapabilities(capabilities));
        if (!provisional) session.Activate();

        // A transport reconnect can overlap a server-side stream teardown.
        // Replace the old instance atomically, fail only its pending work, and
        // never let its eventual Dispose remove the newer registration.
        while (true)
        {
            if (_sessions.TryAdd(client, session))
            {
                return CreateRegistration(client, session);
            }

            if (!_sessions.TryGetValue(client, out var previous))
            {
                continue;
            }

            if (!GatewaySessionRegistrationFence.CanReplace(
                    connectionId,
                    connectionEpoch,
                    previous.ConnectionId,
                    previous.ConnectionEpoch))
            {
                session.Complete("file_gateway_session_fenced");
                throw new AgentGatewayRegistrationFencedException();
            }

            if (_sessions.TryUpdate(client, session, previous))
            {
                previous.Complete("file_gateway_session_replaced");
                return CreateRegistration(client, session);
            }
        }
    }

    public GatewayFileGatewayAvailability? GetAvailability(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? session.Availability
            : null;

    public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken) =>
        GetSession(client).ListAsync(path, Math.Clamp(pageSize, 1, MaximumPageSize), cancellationToken);

    public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(
        ClientKey client,
        string path,
        int pageSize,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).ListAsync(path, Math.Clamp(pageSize, 1, MaximumPageSize), accessPolicy, cancellationToken);

    public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        GetSession(client).ReadAsync(path, cancellationToken);

    public Task<GatewayFileReadOperation> ReadAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).ReadAsync(path, accessPolicy, cancellationToken);

    public Task<GatewayFileMetadata> StatAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        GetSession(client).StatAsync(path, cancellationToken);

    public Task<GatewayFileMetadata> StatAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).StatAsync(path, accessPolicy, cancellationToken);

    public Task CreateDirectoryAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        GetSession(client).CreateDirectoryAsync(path, cancellationToken);

    public Task CreateDirectoryAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).CreateDirectoryAsync(path, accessPolicy, cancellationToken);

    public Task DeleteAsync(ClientKey client, string path, CancellationToken cancellationToken) =>
        GetSession(client).DeleteAsync(path, cancellationToken);

    public Task DeleteAsync(
        ClientKey client,
        string path,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).DeleteAsync(path, accessPolicy, cancellationToken);

    public Task CopyAsync(
        ClientKey client,
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).CopyAsync(sourcePath, destinationPath, accessPolicy, cancellationToken);

    public Task MoveAsync(
        ClientKey client,
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).MoveAsync(sourcePath, destinationPath, accessPolicy, cancellationToken);

    public Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken) =>
        GetSession(client).WriteAsync(path, source, cancellationToken);

    public Task WriteAsync(
        ClientKey client,
        string path,
        Stream source,
        GatewayFileAccessPolicy accessPolicy,
        CancellationToken cancellationToken) =>
        GetSession(client).WriteAsync(path, source, accessPolicy, cancellationToken);

    public bool TryAccept(ClientKey client, FileRequestAccepted accepted) =>
        _sessions.TryGetValue(client, out var session) && session.TryAccept(accepted);

    public bool TryAddPage(ClientKey client, FileListPage page) =>
        _sessions.TryGetValue(client, out var session) && session.TryAddPage(page);

    public Task<bool> TryAddReadChunkAsync(ClientKey client, FileTransferChunk chunk, CancellationToken cancellationToken) =>
        _sessions.TryGetValue(client, out var session)
            ? session.TryAddReadChunkAsync(chunk, cancellationToken)
            : Task.FromResult(false);

    public bool TrySetMetadata(ClientKey client, FileMetadata metadata) =>
        _sessions.TryGetValue(client, out var session) && session.TrySetMetadata(metadata);

    public bool TryComplete(ClientKey client, FileRequestCompleted completed) =>
        _sessions.TryGetValue(client, out var session) && session.TryComplete(completed);

    public bool TryFail(ClientKey client, FileRequestFailed failed) =>
        _sessions.TryGetValue(client, out var session) && session.TryFail(failed);

    private AgentFileGatewayRegistration CreateRegistration(ClientKey client, AgentFileGatewaySession session) =>
        new(
            session.Reader,
            accepted => IsCurrent(client, session) && session.TryAccept(accepted),
            page => IsCurrent(client, session) && session.TryAddPage(page),
            (chunk, token) => IsCurrent(client, session) ? session.TryAddReadChunkAsync(chunk, token) : Task.FromResult(false),
            metadata => IsCurrent(client, session) && session.TrySetMetadata(metadata),
            completed => IsCurrent(client, session) && session.TryComplete(completed),
            failed => IsCurrent(client, session) && session.TryFail(failed),
            () =>
            {
                ((ICollection<KeyValuePair<ClientKey, AgentFileGatewaySession>>)_sessions)
                    .Remove(new KeyValuePair<ClientKey, AgentFileGatewaySession>(client, session));
                session.Complete("file_gateway_session_ended");
            },
            () => IsCurrent(client, session),
            () => IsCurrent(client, session) && session.Activate() && IsCurrent(client, session),
            session.CompletionToken);

    private bool IsCurrent(ClientKey client, AgentFileGatewaySession session) =>
        _sessions.TryGetValue(client, out var current) && ReferenceEquals(current, session) && !session.CompletionToken.IsCancellationRequested;

    private static IReadOnlyList<string> NormalizeCapabilities(IReadOnlySet<string> capabilities) =>
        capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability) && capability.Length <= 128)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(capability => capability, StringComparer.Ordinal)
            .ToArray();

    private AgentFileGatewaySession GetSession(ClientKey client) =>
        _sessions.TryGetValue(client, out var session) && session.IsActive
            ? session
            : throw new AgentFileGatewaySessionUnavailableException(client);
}

public sealed class AgentFileGatewayRegistration(
    ChannelReader<GatewayFileFrame> reader,
    Func<FileRequestAccepted, bool> tryAccept,
    Func<FileListPage, bool> tryAddPage,
    Func<FileTransferChunk, CancellationToken, Task<bool>> tryAddReadChunkAsync,
    Func<FileMetadata, bool> trySetMetadata,
    Func<FileRequestCompleted, bool> tryComplete,
    Func<FileRequestFailed, bool> tryFail,
    Action unregister,
    Func<bool>? isCurrent = null,
    Func<bool>? tryActivate = null,
    CancellationToken completionToken = default) : IDisposable
{
    private int _disposed;

    public Guid RegistrationId { get; } = Guid.NewGuid();
    public bool IsCurrent => Volatile.Read(ref _disposed) == 0 && (isCurrent?.Invoke() ?? true);
    public bool TryActivate() => IsCurrent && (tryActivate?.Invoke() ?? true);
    public CancellationToken CompletionToken => completionToken;
    public ChannelReader<GatewayFileFrame> Reader { get; } = reader;

    // Inbound frames are bound to this exact registration. An older stream can
    // therefore never complete requests that belong to its replacement.
    public bool TryAccept(FileRequestAccepted accepted) => tryAccept(accepted);
    public bool TryAddPage(FileListPage page) => tryAddPage(page);
    public Task<bool> TryAddReadChunkAsync(FileTransferChunk chunk, CancellationToken cancellationToken) => tryAddReadChunkAsync(chunk, cancellationToken);
    public bool TrySetMetadata(FileMetadata metadata) => trySetMetadata(metadata);
    public bool TryComplete(FileRequestCompleted completed) => tryComplete(completed);
    public bool TryFail(FileRequestFailed failed) => tryFail(failed);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            unregister();
        }
    }
}

public sealed class GatewayFileReadOperation(
    ChannelReader<ReadOnlyMemory<byte>> chunks,
    Task completion,
    Func<string, CancellationToken, Task> cancel)
{
    public ChannelReader<ReadOnlyMemory<byte>> Chunks { get; } = chunks;
    public Task Completion { get; } = completion;
    public Task CancelAsync(string reason, CancellationToken cancellationToken) => cancel(reason, cancellationToken);
}

internal sealed class AgentFileGatewaySession(
    ClientKey client,
    Guid connectionId,
    ulong connectionEpoch,
    IClientPresenceRouter presenceRouter,
    bool supportsPolicyRoots,
    bool supportsStat,
    bool supportsCreateDirectory,
    bool supportsDelete,
    bool supportsCopy,
    bool supportsMove,
    IReadOnlyList<string> negotiatedCapabilities)
{
    private const int OutboundCapacity = 32;
    private const int ReadChunkCapacity = 8;
    private const int MaximumEntriesPerRequest = 4_096;
    private const int MaximumTransferChunkBytes = FileTransferFrameBudget.PayloadBytes;
    private readonly Channel<GatewayFileFrame> _outbound = Channel.CreateBounded<GatewayFileFrame>(
        new BoundedChannelOptions(OutboundCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
    private readonly ConcurrentDictionary<Guid, PendingFileRequest> _requests = new();
    private long _outboundSequence;
    private readonly CancellationTokenSource _completion = new();
    private int _active;
    private int _completed;
    public CancellationToken CompletionToken => _completion.Token;
    public bool IsActive => Volatile.Read(ref _active) != 0 && !CompletionToken.IsCancellationRequested;
    public bool Activate()
    {
        Volatile.Write(ref _active, 1);
        return !CompletionToken.IsCancellationRequested;
    }
    private readonly DateTimeOffset _registeredAtUtc = DateTimeOffset.UtcNow;

    public ChannelReader<GatewayFileFrame> Reader => _outbound.Reader;
    public Guid ConnectionId => connectionId;
    public ulong ConnectionEpoch => connectionEpoch;
    public GatewayFileGatewayAvailability Availability => new(connectionId, connectionEpoch, negotiatedCapabilities, _registeredAtUtc);

    public async Task<IReadOnlyList<GatewayFileEntry>> ListAsync(string path, int pageSize, CancellationToken cancellationToken) =>
        await ListAsync(path, pageSize, null, cancellationToken).ConfigureAwait(false);

    public async Task<IReadOnlyList<GatewayFileEntry>> ListAsync(
        string path,
        int pageSize,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        var pending = await BeginAsync("list", path, pageSize, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return pending.Entries.Values
                .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, "operator_list_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    public Task<GatewayFileReadOperation> ReadAsync(string path, CancellationToken cancellationToken) =>
        ReadAsync(path, null, cancellationToken);

    public async Task<GatewayFileReadOperation> ReadAsync(
        string path,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        var pending = await BeginAsync("read", path, 0, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _requests.TryRemove(pending.RequestId, out _);
            throw;
        }

        return new GatewayFileReadOperation(
            pending.ReadChunks!.Reader,
            pending.Completion.Task,
            (reason, ct) => CancelAsync(pending, reason, ct));
    }

    public Task<GatewayFileMetadata> StatAsync(string path, CancellationToken cancellationToken) =>
        StatAsync(path, null, cancellationToken);

    public async Task<GatewayFileMetadata> StatAsync(
        string path,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!supportsStat)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        var pending = await BeginAsync("stat", path, 0, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return await pending.Metadata!.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, "operator_stat_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    public Task CreateDirectoryAsync(string path, CancellationToken cancellationToken) =>
        CreateDirectoryAsync(path, null, cancellationToken);

    public async Task CreateDirectoryAsync(
        string path,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!supportsCreateDirectory)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        var pending = await BeginAsync("create_directory", path, 0, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, "operator_create_directory_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    public Task DeleteAsync(string path, CancellationToken cancellationToken) =>
        DeleteAsync(path, null, cancellationToken);

    public async Task DeleteAsync(
        string path,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!supportsDelete)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        var pending = await BeginAsync("delete", path, 0, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, "operator_delete_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    public async Task CopyAsync(
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!supportsCopy)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        await MoveCopyAsync("copy", sourcePath, destinationPath, accessPolicy, cancellationToken).ConfigureAwait(false);
    }

    public async Task MoveAsync(
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        if (!supportsMove)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        await MoveCopyAsync("move", sourcePath, destinationPath, accessPolicy, cancellationToken).ConfigureAwait(false);
    }

    public Task WriteAsync(string path, Stream source, CancellationToken cancellationToken) =>
        WriteAsync(path, source, null, cancellationToken);

    public async Task WriteAsync(
        string path,
        Stream source,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken)
    {
        var pending = await BeginAsync("write", path, 0, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            var buffer = GC.AllocateUninitializedArray<byte>(MaximumTransferChunkBytes);
            ulong chunkIndex = 0;
            int bytesRead;
            while ((bytesRead = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                var content = ByteString.CopyFrom(buffer, 0, bytesRead);
                await WriteOutboundAsync(new GatewayFileFrame
                {
                    ProtocolVersion = "1.0",
                    TenantId = client.TenantId,
                    ClientId = client.AgentId.ToString("D"),
                    ConnectionEpoch = connectionEpoch,
                    ConnectionId = connectionId.ToString("D"),
                    Sequence = NextOutboundSequence(),
                    TransferChunk = new FileTransferChunk
                    {
                        RequestId = pending.RequestId.ToString("D"),
                        AttemptId = pending.AttemptId.ToString("D"),
                        ChunkIndex = chunkIndex++,
                        Content = content,
                        Sha256 = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant(),
                        IsLastChunk = false
                    }
                }, cancellationToken).ConfigureAwait(false);
            }

            await WriteOutboundAsync(new GatewayFileFrame
            {
                ProtocolVersion = "1.0",
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                Sequence = NextOutboundSequence(),
                TransferChunk = new FileTransferChunk
                {
                    RequestId = pending.RequestId.ToString("D"),
                    AttemptId = pending.AttemptId.ToString("D"),
                    ChunkIndex = chunkIndex,
                    IsLastChunk = true
                }
            }, cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, "operator_upload_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    public bool TryAccept(FileRequestAccepted accepted)
    {
        if (!TryGet(accepted.RequestId, accepted.AttemptId, out var pending) || !pending.Accepted.TrySetResult())
        {
            return false;
        }

        if (!string.Equals(pending.Operation, "read", StringComparison.Ordinal))
        {
            return true;
        }

        // A read starts with exactly one 16 KiB credit. Each accepted chunk
        // returns its byte credit after it has entered the bounded API
        // transport buffer, so an agent cannot outrun that buffer.
        if (TryGrantReadCredit(pending, MaximumTransferChunkBytes))
        {
            return true;
        }

        FailPending(pending, new InvalidOperationException("The gateway could not grant the initial file read credit."));
        return false;
    }

    public bool TryAddPage(FileListPage page)
    {
        if (!TryGet(page.RequestId, page.AttemptId, out var pending) || !string.Equals(pending.Operation, "list", StringComparison.Ordinal))
        {
            return false;
        }

        if (pending.ListCompleted || page.PageIndex != pending.NextPageIndex ||
            pending.Entries.Count + page.Entries.Count > MaximumEntriesPerRequest)
        {
            FailAndRemove(pending, "The agent sent an invalid, out-of-order, or oversized file list page.");
            return false;
        }

        foreach (var entry in page.Entries)
        {
            if (string.IsNullOrWhiteSpace(entry.FullPath) || string.IsNullOrWhiteSpace(entry.Name))
            {
                return false;
            }

            pending.Entries[entry.FullPath] = new GatewayFileEntry(entry.Name, entry.FullPath, entry.IsDirectory, entry.SizeBytes);
        }

        pending.NextPageIndex++;
        pending.ListCompleted = page.IsLastPage;

        return true;
    }

    public async Task<bool> TryAddReadChunkAsync(FileTransferChunk chunk, CancellationToken cancellationToken)
    {
        if (!TryGet(chunk.RequestId, chunk.AttemptId, out var pending))
        {
            return false;
        }

        if (!string.Equals(pending.Operation, "read", StringComparison.Ordinal) ||
            pending.ReadChunks is null || chunk.Content.Length > MaximumTransferChunkBytes ||
            chunk.ChunkIndex != pending.NextReadChunkIndex || !HasValidHash(chunk))
        {
            FailAndRemove(pending, "The agent sent an invalid or out-of-order file read chunk.");
            return false;
        }

        pending.NextReadChunkIndex++;
        try
        {
            await pending.ReadChunks.Writer.WriteAsync(chunk.Content.Memory, cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
            FailAndRemove(pending, "The gateway file read consumer closed before receiving the chunk.");
            return false;
        }

        if (!TryGrantReadCredit(pending, checked((uint)chunk.Content.Length)))
        {
            FailAndRemove(pending, "The gateway could not return file read credit.");
            return false;
        }

        return true;
    }

    public bool TrySetMetadata(FileMetadata metadata)
    {
        if (!TryGet(metadata.RequestId, metadata.AttemptId, out var pending) ||
            !string.Equals(pending.Operation, "stat", StringComparison.Ordinal) ||
            pending.Metadata is null || string.IsNullOrWhiteSpace(metadata.FullPath) ||
            metadata.SizeBytes < 0 || string.IsNullOrWhiteSpace(metadata.MimeType) || metadata.LastModifiedUtc is null)
        {
            return false;
        }

        DateTimeOffset lastModifiedUtc;
        try
        {
            lastModifiedUtc = metadata.LastModifiedUtc.ToDateTimeOffset();
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        return pending.Metadata.TrySetResult(new GatewayFileMetadata(
            metadata.FullPath,
            metadata.IsDirectory,
            metadata.SizeBytes,
            lastModifiedUtc,
            metadata.MimeType));
    }

    public bool TryComplete(FileRequestCompleted completed)
    {
        if (!TryGet(completed.RequestId, completed.AttemptId, out var pending))
        {
            return false;
        }

        if (string.Equals(pending.Operation, "list", StringComparison.Ordinal) && !pending.ListCompleted)
        {
            FailAndRemove(pending, "The agent completed a file list before sending its final page.");
            return false;
        }

        if (string.Equals(pending.Operation, "stat", StringComparison.Ordinal) && pending.Metadata is { Task.IsCompletedSuccessfully: false })
        {
            FailAndRemove(pending, "The agent completed a file stat before sending metadata.");
            return false;
        }

        pending.ReadChunks?.Writer.TryComplete();
        pending.Completion.TrySetResult();
        _requests.TryRemove(pending.RequestId, out _);
        return true;
    }

    public bool TryFail(FileRequestFailed failed)
    {
        if (!TryGet(failed.RequestId, failed.AttemptId, out var pending))
        {
            return false;
        }

        FailAndRemove(pending, new AgentFileGatewayOperationException(failed.Code));
        return true;
    }

    public void Complete(string reason)
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0) return;
        _completion.Cancel();
        _outbound.Writer.TryComplete();
        // Remove before completing. This prevents a racing late frame from
        // completing a request after its owning transport has been fenced.
        foreach (var pair in _requests.ToArray())
        {
            if (!_requests.TryRemove(pair.Key, out var pending))
            {
                continue;
            }

            var exception = new AgentFileGatewaySessionUnavailableException(client, reason);
            FailPending(pending, exception);
        }
    }

    private async Task<PendingFileRequest> BeginAsync(
        string operation,
        string path,
        int pageSize,
        GatewayFileAccessPolicy? accessPolicy,
        CancellationToken cancellationToken) =>
        await BeginAsync(operation, path, null, pageSize, accessPolicy, null, cancellationToken).ConfigureAwait(false);

    private async Task MoveCopyAsync(
        string operation,
        string sourcePath,
        string destinationPath,
        GatewayFileMoveCopyAccessPolicy accessPolicy,
        CancellationToken cancellationToken)
    {
        var pending = await BeginAsync(operation, sourcePath, destinationPath, 0, null, accessPolicy, cancellationToken).ConfigureAwait(false);
        try
        {
            await pending.Accepted.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            await pending.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await CancelAsync(pending, $"operator_{operation}_cancelled", CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        finally
        {
            _requests.TryRemove(pending.RequestId, out _);
        }
    }

    private async Task<PendingFileRequest> BeginAsync(
        string operation,
        string path,
        string? destinationPath,
        int pageSize,
        GatewayFileAccessPolicy? accessPolicy,
        GatewayFileMoveCopyAccessPolicy? moveCopyAccessPolicy,
        CancellationToken cancellationToken)
    {
        if ((accessPolicy is { AllowedRoots.Count: > 0 } ||
                moveCopyAccessPolicy is { SourceAllowedRoots.Count: > 0, DestinationAllowedRoots.Count: > 0 }) &&
            !supportsPolicyRoots)
        {
            throw new AgentFileGatewayOperationException("capability_unavailable");
        }

        if (moveCopyAccessPolicy is not null &&
            (string.IsNullOrWhiteSpace(destinationPath) || moveCopyAccessPolicy.SourceAllowedRoots.Count == 0 || moveCopyAccessPolicy.DestinationAllowedRoots.Count == 0))
        {
            throw new AgentFileGatewayOperationException("access_denied");
        }

        await EnsurePresenceLeaseAsync(cancellationToken).ConfigureAwait(false);
        CompletionToken.ThrowIfCancellationRequested();
        var pending = new PendingFileRequest(operation);
        if (!_requests.TryAdd(pending.RequestId, pending))
        {
            throw new InvalidOperationException("The generated gateway file request ID was already in use.");
        }

        try
        {
            await WriteOutboundAsync(new GatewayFileFrame
            {
                ProtocolVersion = "1.0",
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                Sequence = NextOutboundSequence(),
                Dispatch = new FileRequestDispatch
                {
                    RequestId = pending.RequestId.ToString("D"),
                    AttemptId = pending.AttemptId.ToString("D"),
                    Operation = operation,
                    Path = path,
                    PageSize = checked((uint)pageSize),
                    AllowedRoots = { accessPolicy?.AllowedRoots ?? [] },
                    DestinationPath = destinationPath ?? string.Empty,
                    SourceAllowedRoots = { moveCopyAccessPolicy?.SourceAllowedRoots ?? [] },
                    DestinationAllowedRoots = { moveCopyAccessPolicy?.DestinationAllowedRoots ?? [] }
                }
            }, cancellationToken).ConfigureAwait(false);
            return pending;
        }
        catch
        {
            _requests.TryRemove(pending.RequestId, out _);
            throw;
        }
    }

    private async Task CancelAsync(PendingFileRequest pending, string reason, CancellationToken cancellationToken)
    {
        if (_requests.TryRemove(pending.RequestId, out _))
        {
            await WriteOutboundAsync(new GatewayFileFrame
            {
                ProtocolVersion = "1.0",
                TenantId = client.TenantId,
                ClientId = client.AgentId.ToString("D"),
                ConnectionEpoch = connectionEpoch,
                ConnectionId = connectionId.ToString("D"),
                Sequence = NextOutboundSequence(),
                Cancel = new FileRequestCancel
                {
                    RequestId = pending.RequestId.ToString("D"),
                    AttemptId = pending.AttemptId.ToString("D"),
                    Reason = string.IsNullOrWhiteSpace(reason) ? "cancelled" : reason[..Math.Min(reason.Length, 128)]
                }
            }, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task EnsurePresenceLeaseAsync(CancellationToken cancellationToken)
    {
        var presence = await presenceRouter.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (presence.Status != ShadowPresenceStatus.Online || presence.ConnectionId != connectionId ||
            presence.ConnectionEpoch != checked((long)connectionEpoch))
        {
            throw new AgentFileGatewaySessionUnavailableException(client);
        }
    }

    private async ValueTask WriteOutboundAsync(GatewayFileFrame frame, CancellationToken cancellationToken) =>
        await _outbound.Writer.WriteAsync(frame, cancellationToken).ConfigureAwait(false);

    private bool TryGrantReadCredit(PendingFileRequest pending, uint availableBytes) =>
        _outbound.Writer.TryWrite(new GatewayFileFrame
        {
            ProtocolVersion = "1.0",
            TenantId = client.TenantId,
            ClientId = client.AgentId.ToString("D"),
            ConnectionEpoch = connectionEpoch,
            ConnectionId = connectionId.ToString("D"),
            Sequence = NextOutboundSequence(),
            Credit = new FileTransferCredit
            {
                RequestId = pending.RequestId.ToString("D"),
                AttemptId = pending.AttemptId.ToString("D"),
                AvailableBytes = availableBytes
            }
        });

    private ulong NextOutboundSequence() => checked((ulong)Interlocked.Increment(ref _outboundSequence));

    private bool TryGet(string requestId, string attemptId, out PendingFileRequest pending)
    {
        pending = null!;
        if (!Guid.TryParse(requestId, out var request) || !Guid.TryParse(attemptId, out var attempt) ||
            !_requests.TryGetValue(request, out var found) || found is null || found.AttemptId != attempt)
        {
            return false;
        }

        pending = found;
        return true;
    }

    private static bool HasValidHash(FileTransferChunk chunk) =>
        chunk.Content.Length == 0 || (!string.IsNullOrWhiteSpace(chunk.Sha256) &&
            string.Equals(chunk.Sha256, Convert.ToHexString(SHA256.HashData(chunk.Content.Span)), StringComparison.OrdinalIgnoreCase));

    private static void FailPending(PendingFileRequest pending, Exception exception)
    {
        pending.ReadChunks?.Writer.TryComplete(exception);
        pending.Metadata?.TrySetException(exception);
        pending.Accepted.TrySetException(exception);
        pending.Completion.TrySetException(exception);
    }

    private void FailAndRemove(PendingFileRequest pending, string message)
    {
        FailAndRemove(pending, new InvalidOperationException(message));
    }

    private void FailAndRemove(PendingFileRequest pending, Exception exception)
    {
        FailPending(pending, exception);
        _requests.TryRemove(pending.RequestId, out _);
    }

    private sealed class PendingFileRequest
    {
        public PendingFileRequest(string operation)
        {
            Operation = operation;
            if (string.Equals(operation, "read", StringComparison.Ordinal))
            {
                ReadChunks = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(ReadChunkCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                    AllowSynchronousContinuations = false
                });
            }

            if (string.Equals(operation, "stat", StringComparison.Ordinal))
            {
                Metadata = new TaskCompletionSource<GatewayFileMetadata>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        public Guid RequestId { get; } = Guid.NewGuid();
        public Guid AttemptId { get; } = Guid.NewGuid();
        public string Operation { get; }
        public TaskCompletionSource Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Dictionary<string, GatewayFileEntry> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Channel<ReadOnlyMemory<byte>>? ReadChunks { get; }
        public TaskCompletionSource<GatewayFileMetadata>? Metadata { get; }
        public ulong NextReadChunkIndex { get; set; }
        public uint NextPageIndex { get; set; }
        public bool ListCompleted { get; set; }
    }
}
