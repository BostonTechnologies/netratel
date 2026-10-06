using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Services;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Handles Agent-ID keyed file browse and transfer requests for one admitted
/// presence session. It does not initialise or touch the Spacetime file
/// manager, and all transfer buffers are bounded to a single 16 KiB frame.
/// </summary>
public sealed class AgentFileGatewayClient
{
    private readonly GatewayClientOptions options;
    private readonly FileSystemService fileSystem;
    private readonly Action<string> log;
    private readonly Func<Uri, GrpcChannel>? createChannel;
    private readonly Func<Func<Task>, Task> startWorker;
    private readonly Func<string, IEnumerable<FileSystemService.BrowseEntry>> enumerateEntries;
    private readonly TimeSpan cleanupTimeout;
    private readonly SemaphoreSlim presenceGate = new(1, 1);
    private Task? retiredCleanup;
    internal Task RetiredWorkerCleanup => Volatile.Read(ref retiredCleanup) ?? Task.CompletedTask;

    public AgentFileGatewayClient(GatewayClientOptions options, FileSystemService fileSystem, Action<string> log)
        : this(options, fileSystem, log, null)
    {
    }

    internal AgentFileGatewayClient(GatewayClientOptions options, FileSystemService fileSystem, Action<string> log,
        Func<Uri, GrpcChannel>? createChannel, Func<Func<Task>, Task>? startWorker = null, TimeSpan? cleanupTimeout = null,
        Func<string, IEnumerable<FileSystemService.BrowseEntry>>? enumerateEntries = null)
    {
        this.options = options;
        this.fileSystem = fileSystem;
        this.log = log;
        this.createChannel = createChannel;
        this.startWorker = startWorker ?? (work => Task.Run(work, CancellationToken.None));
        this.enumerateEntries = enumerateEntries ?? fileSystem.EnumerateEntries;
        this.cleanupTimeout = cleanupTimeout ?? TimeSpan.FromSeconds(5);
    }
    private const int ChunkBytes = FileTransferFrameBudget.PayloadBytes;
    private static readonly TimeSpan InitialRetryDelay = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(30);

    public async Task RunForPresenceSessionAsync(GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidOperationException("Gateway:Endpoint must be an absolute HTTPS URL.");
        }

        if (!await presenceGate.WaitAsync(cleanupTimeout, stoppingToken).ConfigureAwait(false))
        {
            throw new TimeoutException("The previous file presence owner has not retired; the file capability cannot be activated yet.");
        }
        try
        {
            await RunOwnedPresenceSessionAsync(endpoint, session, accessToken, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            presenceGate.Release();
        }
    }

    private async Task RunOwnedPresenceSessionAsync(Uri endpoint, GatewayPresenceSession session, string accessToken,
        CancellationToken stoppingToken)
    {
        if (Volatile.Read(ref retiredCleanup) is { } pendingCleanup)
        {
            try
            {
                await pendingCleanup.WaitAsync(cleanupTimeout, stoppingToken).ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                throw new TimeoutException("The retired file worker still owns filesystem operations; the file capability cannot be activated yet.", exception);
            }
            _ = Interlocked.CompareExchange(ref retiredCleanup, null, pendingCleanup);
        }

        var retryDelay = InitialRetryDelay;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunStreamAsync(endpoint, session, accessToken, stoppingToken).ConfigureAwait(false);
                retryDelay = InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (stoppingToken.IsCancellationRequested && IsTransientFailure(exception))
            {
                break;
            }
            catch (Exception exception) when (IsTransientFailure(exception))
            {
                log($"File gateway session failed: {exception.GetType().Name}: {exception.Message}. Retrying in {retryDelay.TotalSeconds:0}s.");
                try
                {
                    await Task.Delay(retryDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, MaximumRetryDelay.TotalSeconds));
            }
        }
    }

    private async Task RunStreamAsync(Uri endpoint, GatewayPresenceSession session, string accessToken, CancellationToken stoppingToken)
    {
        var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var streamToken = streamCancellation.Token;
        using var channel = createChannel?.Invoke(endpoint) ?? GrpcChannel.ForAddress(endpoint);
        var client = new AgentFileGateway.AgentFileGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {session.GetAccessToken(accessToken)}" } };
        using var call = client.Connect(headers, cancellationToken: streamToken);
        var writer = new FileGatewayWriter(call.RequestStream, session, options.ProtocolVersion);
        var operations = new ConcurrentDictionary<Guid, ClientFileOperation>();
        var workers = new FileOperationWorkers(startWorker, streamCancellation);
        Exception? failure = null;
        try
        {
            await writer.WriteAsync(new AgentFileFrame
            {
                Sequence = 0,
                Hello = new AgentFileHello { Capabilities = { "file-list", "file-read", "file-write", "file-stat-v1", "file-create-directory-v1", "file-delete-v1", "file-copy-v1", "file-move-v1", "chunk-sha256", "file-policy-roots-v1" } }
            }, streamToken).ConfigureAwait(false);

            if (!await call.ResponseStream.MoveNext(streamToken).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayFileFrame.PayloadOneofCase.Accepted)
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "File gateway closed before accepting the session."));
            }

            ValidateAccepted(call.ResponseStream.Current, session);
            log($"File gateway admitted. authority={call.ResponseStream.Current.Accepted.FileAuthority}.");

            ulong lastServerSequence = 0;
            while (await call.ResponseStream.MoveNext(streamToken).ConfigureAwait(false))
            {
                var frame = call.ResponseStream.Current;
                ValidateFrame(frame, session);
                if (frame.Sequence == 0 || frame.Sequence <= lastServerSequence)
                {
                    throw new RpcException(new Status(StatusCode.DataLoss, "File gateway returned a stale or out-of-order frame."));
                }

                lastServerSequence = frame.Sequence;
                switch (frame.PayloadCase)
                {
                    case GatewayFileFrame.PayloadOneofCase.Dispatch:
                        await HandleDispatchAsync(frame.Dispatch, operations, workers, writer, streamToken).ConfigureAwait(false);
                        break;
                    case GatewayFileFrame.PayloadOneofCase.TransferChunk:
                        await HandleWriteChunkAsync(frame.TransferChunk, operations, writer, streamToken).ConfigureAwait(false);
                        break;
                    case GatewayFileFrame.PayloadOneofCase.Credit:
                        GrantReadCredit(frame.Credit, operations);
                        break;
                    case GatewayFileFrame.PayloadOneofCase.Cancel:
                        Cancel(frame.Cancel, operations);
                        break;
                    default:
                        throw new RpcException(new Status(StatusCode.DataLoss, "File gateway returned an unsupported frame."));
                }
            }

            throw new RpcException(new Status(StatusCode.Unavailable, "File gateway closed the session."));
        }
        catch (Exception exception)
        {
            failure = workers.Failure ?? exception;
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            // Retire before cancelling or joining: late filesystem callbacks
            // cannot enter a closed physical stream or its successor.
            writer.Retire();
            streamCancellation.Cancel();
            call.Dispose();
            var joined = workers.JoinAsync();
            var deferredCleanup = false;
            try
            {
                await joined.WaitAsync(cleanupTimeout).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // A synchronous filesystem call cannot always be interrupted.
                // Keep its resources owned until it exits, expose the fault,
                // and do not start another attempt beside that worker.
                deferredCleanup = true;
                Volatile.Write(ref retiredCleanup, DisposeAfterWorkersAsync(joined, operations, writer, streamCancellation));
                throw new TimeoutException($"File gateway workers did not stop within the {cleanupTimeout.TotalSeconds:0.###}-second cleanup budget; the capability was retired.", failure);
            }
            finally
            {
                if (!deferredCleanup)
                {
                    DisposeStreamResources(operations, writer, streamCancellation);
                }
            }

            if (failure is null && workers.Failure is { } workerFailure)
            {
                ExceptionDispatchInfo.Capture(workerFailure).Throw();
            }
        }
    }

    private async Task DisposeAfterWorkersAsync(Task joined, ConcurrentDictionary<Guid, ClientFileOperation> operations,
        FileGatewayWriter writer, CancellationTokenSource streamCancellation)
    {
        try
        {
            await joined.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            log($"Retired file gateway worker failed during cleanup: {exception.GetType().Name}.");
        }
        finally
        {
            try
            {
                DisposeStreamResources(operations, writer, streamCancellation);
            }
            catch (Exception exception)
            {
                // This path has no caller after bounded retirement. Observe
                // cleanup faults explicitly rather than faulting a detached task.
                log($"Retired file gateway resource cleanup failed: {exception.GetType().Name}.");
            }
        }
    }

    private static void DisposeStreamResources(ConcurrentDictionary<Guid, ClientFileOperation> operations,
        FileGatewayWriter writer, CancellationTokenSource streamCancellation)
    {
        foreach (var operation in operations.Values)
        {
            operation.Dispose();
        }
        operations.Clear();
        writer.Dispose();
        streamCancellation.Dispose();
    }

    private static bool IsTransientFailure(Exception exception) => exception is HttpRequestException or IOException ||
        exception is RpcException { StatusCode: StatusCode.Cancelled or StatusCode.Unavailable or StatusCode.Internal or
            StatusCode.DeadlineExceeded or StatusCode.ResourceExhausted or StatusCode.Aborted };

    private static bool IsOperationFailure(Exception exception) => exception is IOException or UnauthorizedAccessException or
        ArgumentException or NotSupportedException or OperationCanceledException;

    private async Task HandleDispatchAsync(
        FileRequestDispatch dispatch,
        ConcurrentDictionary<Guid, ClientFileOperation> operations,
        FileOperationWorkers workers,
        FileGatewayWriter writer,
        CancellationToken stoppingToken)
    {
        if (!Guid.TryParse(dispatch.RequestId, out var requestId) || requestId == Guid.Empty ||
            !Guid.TryParse(dispatch.AttemptId, out var attemptId) || attemptId == Guid.Empty ||
            !fileSystem.IsValidPath(dispatch.Path) || string.IsNullOrWhiteSpace(dispatch.Operation))
        {
            await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, "invalid_dispatch", writer, stoppingToken).ConfigureAwait(false);
            return;
        }

        var operationName = dispatch.Operation.Trim().ToLowerInvariant();
        var isMoveCopy = operationName is "copy" or "move";
        var resolvedPath = dispatch.Path;
        var resolvedDestinationPath = string.Empty;
        if (isMoveCopy)
        {
            if (string.IsNullOrWhiteSpace(dispatch.DestinationPath) || !fileSystem.IsValidPath(dispatch.DestinationPath) ||
                dispatch.SourceAllowedRoots.Count == 0 || dispatch.DestinationAllowedRoots.Count == 0 ||
                !fileSystem.TryResolveGatewayPath(dispatch.Path, dispatch.SourceAllowedRoots, requireExisting: true, out resolvedPath) ||
                !fileSystem.TryResolveGatewayPath(dispatch.DestinationPath, dispatch.DestinationAllowedRoots, requireExisting: false, out resolvedDestinationPath))
            {
                await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, "access_denied", writer, stoppingToken).ConfigureAwait(false);
                return;
            }
        }
        else if (dispatch.AllowedRoots.Count > 0 &&
            !fileSystem.TryResolveGatewayPath(
                dispatch.Path,
                dispatch.AllowedRoots,
                requireExisting: operationName is "list" or "read" or "stat" or "delete",
                out resolvedPath))
        {
            await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, "access_denied", writer, stoppingToken).ConfigureAwait(false);
            return;
        }

        var operation = new ClientFileOperation(requestId, attemptId, operationName, resolvedPath, resolvedDestinationPath, stoppingToken);
        if (!operations.TryAdd(requestId, operation))
        {
            operation.Dispose();
            await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, "duplicate_request", writer, stoppingToken).ConfigureAwait(false);
            return;
        }

        await writer.WriteAsync(new AgentFileFrame
        {
            RequestAccepted = new FileRequestAccepted { RequestId = dispatch.RequestId, AttemptId = dispatch.AttemptId }
        }, stoppingToken).ConfigureAwait(false);

        switch (operation.Operation)
        {
            case "list":
                workers.Start(() => HandleListAsync(operation, Math.Clamp((int)dispatch.PageSize, 1, 256), operations, writer, stoppingToken));
                break;
            case "read":
                workers.Start(() => HandleReadAsync(operation, operations, writer, stoppingToken));
                break;
            case "stat":
                workers.Start(() => HandleStatAsync(operation, operations, writer, stoppingToken));
                break;
            case "create_directory":
                workers.Start(() => HandleCreateDirectoryAsync(operation, operations, writer, stoppingToken));
                break;
            case "delete":
                workers.Start(() => HandleDeleteAsync(operation, operations, writer, stoppingToken));
                break;
            case "copy":
                workers.Start(() => HandleCopyAsync(operation, operations, writer, stoppingToken));
                break;
            case "move":
                workers.Start(() => HandleMoveAsync(operation, operations, writer, stoppingToken));
                break;
            case "write":
                try
                {
                    operation.BeginWrite();
                }
                catch (Exception exception) when (IsOperationFailure(exception))
                {
                    operations.TryRemove(requestId, out _);
                    operation.Dispose();
                    await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, FailureCodeFor("write", exception), writer, stoppingToken).ConfigureAwait(false);
                }
                break;
            default:
                operations.TryRemove(requestId, out _);
                operation.Dispose();
                await WriteFailureAsync(dispatch.RequestId, dispatch.AttemptId, "unsupported_operation", writer, stoppingToken).ConfigureAwait(false);
                break;
        }
    }

    private async Task HandleListAsync(ClientFileOperation operation, int pageSize, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            var page = new List<FileEntry>(pageSize);
            uint pageIndex = 0;
            foreach (var entry in enumerateEntries(operation.Path))
            {
                operation.Token.ThrowIfCancellationRequested();
                page.Add(new FileEntry { Name = entry.Name, FullPath = entry.FullPath, IsDirectory = entry.IsDirectory, SizeBytes = entry.SizeBytes });
                if (page.Count == pageSize)
                {
                    await WritePageAsync(operation, pageIndex++, page, false, writer, stoppingToken).ConfigureAwait(false);
                    page.Clear();
                }
            }

            await WritePageAsync(operation, pageIndex, page, true, writer, stoppingToken).ConfigureAwait(false);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File list failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("list", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleReadAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            await using var stream = new FileStream(operation.Path, FileMode.Open, FileAccess.Read, FileShare.Read, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var buffer = GC.AllocateUninitializedArray<byte>(ChunkBytes);
            ulong chunkIndex = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer.AsMemory(), operation.Token).ConfigureAwait(false)) > 0)
            {
                await operation.AcquireReadCreditAsync(read, operation.Token).ConfigureAwait(false);
                var content = ByteString.CopyFrom(buffer, 0, read);
                await writer.WriteAsync(new AgentFileFrame
                {
                    TransferChunk = new FileTransferChunk
                    {
                        RequestId = operation.RequestId.ToString("D"),
                        AttemptId = operation.AttemptId.ToString("D"),
                        ChunkIndex = chunkIndex++,
                        Content = content,
                        Sha256 = Convert.ToHexString(SHA256.HashData(content.Span)).ToLowerInvariant(),
                        IsLastChunk = false
                    }
                }, stoppingToken).ConfigureAwait(false);
            }

            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File read failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("read", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleStatAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            FileSystemInfo entry = Directory.Exists(operation.Path)
                ? new DirectoryInfo(operation.Path)
                : File.Exists(operation.Path)
                    ? new FileInfo(operation.Path)
                    : throw new FileNotFoundException("File or directory not found.", operation.Path);
            var isDirectory = entry is DirectoryInfo;
            var sizeBytes = entry is FileInfo file ? file.Length : 0L;
            var lastModifiedUtc = entry.LastWriteTimeUtc;

            await writer.WriteAsync(new AgentFileFrame
            {
                Metadata = new FileMetadata
                {
                    RequestId = operation.RequestId.ToString("D"),
                    AttemptId = operation.AttemptId.ToString("D"),
                    FullPath = entry.FullName,
                    IsDirectory = isDirectory,
                    SizeBytes = sizeBytes,
                    LastModifiedUtc = Timestamp.FromDateTime(lastModifiedUtc),
                    MimeType = ContentType(entry.FullName, isDirectory)
                }
            }, stoppingToken).ConfigureAwait(false);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File stat failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("stat", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleCreateDirectoryAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (Directory.Exists(operation.Path) || File.Exists(operation.Path))
            {
                await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "path_already_exists", writer, stoppingToken).ConfigureAwait(false);
                return;
            }

            Directory.CreateDirectory(operation.Path);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File create-directory failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("create_directory", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleDeleteAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            operation.Token.ThrowIfCancellationRequested();
            if (File.Exists(operation.Path))
            {
                File.Delete(operation.Path);
            }
            else if (Directory.Exists(operation.Path))
            {
                using var entries = Directory.EnumerateFileSystemEntries(operation.Path).GetEnumerator();
                if (entries.MoveNext())
                {
                    await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "directory_not_empty", writer, stoppingToken).ConfigureAwait(false);
                    return;
                }

                Directory.Delete(operation.Path, recursive: false);
            }
            else
            {
                await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "path_not_found", writer, stoppingToken).ConfigureAwait(false);
                return;
            }

            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File delete failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("delete", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleCopyAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            if (!TryValidateMoveCopy(operation, out var failureCode))
            {
                await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), failureCode, writer, stoppingToken).ConfigureAwait(false);
                return;
            }

            operation.Token.ThrowIfCancellationRequested();
            File.Copy(operation.Path, operation.DestinationPath!, overwrite: false);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (IOException) when (DestinationExists(operation.DestinationPath))
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "destination_already_exists", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File copy failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("copy", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private async Task HandleMoveAsync(ClientFileOperation operation, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        try
        {
            if (!TryValidateMoveCopy(operation, out var failureCode))
            {
                await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), failureCode, writer, stoppingToken).ConfigureAwait(false);
                return;
            }

            operation.Token.ThrowIfCancellationRequested();
            File.Move(operation.Path, operation.DestinationPath!, overwrite: false);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "cancelled", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (IOException) when (DestinationExists(operation.DestinationPath))
        {
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), "destination_already_exists", writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File move failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(operation.RequestId.ToString("D"), operation.AttemptId.ToString("D"), FailureCodeFor("move", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(operation.RequestId, out _);
            operation.Dispose();
        }
    }

    private static bool TryValidateMoveCopy(ClientFileOperation operation, out string failureCode)
    {
        failureCode = string.Empty;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.IsNullOrWhiteSpace(operation.DestinationPath) ||
            string.Equals(operation.Path, operation.DestinationPath, pathComparison))
        {
            failureCode = "invalid_destination";
            return false;
        }

        if (!File.Exists(operation.Path))
        {
            failureCode = Directory.Exists(operation.Path) ? "source_not_file" : "path_not_found";
            return false;
        }

        if (DestinationExists(operation.DestinationPath))
        {
            failureCode = "destination_already_exists";
            return false;
        }

        return true;
    }

    private static bool DestinationExists(string? path) =>
        !string.IsNullOrWhiteSpace(path) && (File.Exists(path) || Directory.Exists(path));

    private async Task HandleWriteChunkAsync(FileTransferChunk chunk, ConcurrentDictionary<Guid, ClientFileOperation> operations, FileGatewayWriter writer, CancellationToken stoppingToken)
    {
        if (!Guid.TryParse(chunk.RequestId, out var requestId) || !Guid.TryParse(chunk.AttemptId, out var attemptId) ||
            !operations.TryGetValue(requestId, out var operation) || operation.AttemptId != attemptId ||
            !string.Equals(operation.Operation, "write", StringComparison.Ordinal) || chunk.Content.Length > ChunkBytes ||
            chunk.ChunkIndex != operation.NextChunkIndex ||
            (chunk.Content.Length > 0 && !string.Equals(chunk.Sha256, Convert.ToHexString(SHA256.HashData(chunk.Content.Span)), StringComparison.OrdinalIgnoreCase)))
        {
            await WriteFailureAsync(chunk.RequestId, chunk.AttemptId, "invalid_write_chunk", writer, stoppingToken).ConfigureAwait(false);
            return;
        }

        operation.NextChunkIndex++;
        try
        {
            await operation.WriteAsync(chunk.Content.Memory, operation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File write failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            operations.TryRemove(requestId, out _);
            operation.Dispose();
            await WriteFailureAsync(chunk.RequestId, chunk.AttemptId, FailureCodeFor("write", exception), writer, stoppingToken).ConfigureAwait(false);
            return;
        }

        if (!chunk.IsLastChunk)
        {
            return;
        }

        try
        {
            await operation.CommitWriteAsync(operation.Token).ConfigureAwait(false);
            await WriteCompletedAsync(operation, writer, stoppingToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsOperationFailure(exception))
        {
            log($"File write failed request={operation.RequestId:D}: {exception.GetType().Name}.");
            await WriteFailureAsync(chunk.RequestId, chunk.AttemptId, FailureCodeFor("write", exception), writer, stoppingToken).ConfigureAwait(false);
        }
        finally
        {
            operations.TryRemove(requestId, out _);
            operation.Dispose();
        }
    }

    private static Task WritePageAsync(ClientFileOperation operation, uint pageIndex, IReadOnlyList<FileEntry> entries, bool last, FileGatewayWriter writer, CancellationToken cancellationToken) =>
        writer.WriteAsync(new AgentFileFrame
        {
            ListPage = new FileListPage
            {
                RequestId = operation.RequestId.ToString("D"),
                AttemptId = operation.AttemptId.ToString("D"),
                PageIndex = pageIndex,
                IsLastPage = last,
                Entries = { entries }
            }
        }, cancellationToken);

    private static Task WriteCompletedAsync(ClientFileOperation operation, FileGatewayWriter writer, CancellationToken cancellationToken) =>
        writer.WriteAsync(new AgentFileFrame { Completed = new FileRequestCompleted { RequestId = operation.RequestId.ToString("D"), AttemptId = operation.AttemptId.ToString("D") } }, cancellationToken);

    private static Task WriteFailureAsync(string requestId, string attemptId, string code, FileGatewayWriter writer, CancellationToken cancellationToken) =>
        writer.WriteAsync(new AgentFileFrame { Failed = new FileRequestFailed { RequestId = requestId, AttemptId = attemptId, Code = code } }, cancellationToken);

    private static string FailureCodeFor(string operation, Exception exception) => exception switch
    {
        FileNotFoundException => "file_not_found",
        DirectoryNotFoundException => "directory_not_found",
        UnauthorizedAccessException => "access_denied",
        OperationCanceledException => "cancelled",
        IOException => "io_failure",
        ArgumentException or NotSupportedException => "invalid_path",
        _ => $"{operation}_failed"
    };

    private static string ContentType(string path, bool isDirectory) =>
        isDirectory
            ? "inode/directory"
            : Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".txt" or ".log" or ".csv" or ".json" or ".xml" or ".yaml" or ".yml" => "text/plain; charset=utf-8",
                ".zip" => "application/zip",
                ".gz" => "application/gzip",
                ".pdf" => "application/pdf",
                _ => "application/octet-stream"
            };

    private static void Cancel(FileRequestCancel cancel, ConcurrentDictionary<Guid, ClientFileOperation> operations)
    {
        if (Guid.TryParse(cancel.RequestId, out var requestId) && Guid.TryParse(cancel.AttemptId, out var attemptId) &&
            operations.TryGetValue(requestId, out var operation) && operation.AttemptId == attemptId)
        {
            operation.Cancel();
            if (operation.Operation == "write" && operations.TryRemove(new KeyValuePair<Guid, ClientFileOperation>(requestId, operation)))
            {
                operation.Dispose();
            }
        }
    }

    private static void GrantReadCredit(FileTransferCredit credit, ConcurrentDictionary<Guid, ClientFileOperation> operations)
    {
        if (!Guid.TryParse(credit.RequestId, out var requestId) || !Guid.TryParse(credit.AttemptId, out var attemptId) ||
            credit.AvailableBytes == 0 || !operations.TryGetValue(requestId, out var operation) || operation.AttemptId != attemptId ||
            !string.Equals(operation.Operation, "read", StringComparison.Ordinal))
        {
            return;
        }

        operation.GrantReadCredit(credit.AvailableBytes);
    }

    private void ValidateAccepted(GatewayFileFrame frame, GatewayPresenceSession session)
    {
        ValidateFrame(frame, session);
        if (!GatewayWireProtocol.HasAkkaAuthority(frame.Accepted.FileAuthority))
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, "File gateway returned an unsupported authority token."));
        }
    }

    private void ValidateFrame(GatewayFileFrame frame, GatewayPresenceSession session)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) || frame.TenantId != session.TenantId ||
            !string.Equals(frame.ClientId, session.AgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != session.ConnectionEpoch || !string.Equals(frame.ConnectionId, session.ConnectionId.ToString("D"), StringComparison.OrdinalIgnoreCase))
        {
            throw new RpcException(new Status(StatusCode.DataLoss, "File gateway returned a frame for another presence session."));
        }
    }

    internal sealed class FileGatewayWriter(IClientStreamWriter<AgentFileFrame> stream, GatewayPresenceSession session, string protocolVersion) : IDisposable
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private ulong _sequence;
        private int _retired;
        private int _disposed;

        public void Retire() => Interlocked.Exchange(ref _retired, 1);

        public async Task WriteAsync(AgentFileFrame frame, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _retired) != 0) throw new OperationCanceledException("The file stream was retired.");
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Volatile.Read(ref _retired) != 0) throw new OperationCanceledException("The file stream was retired.");
                frame.ProtocolVersion = protocolVersion;
                frame.TenantId = session.TenantId;
                frame.ClientId = session.AgentId.ToString("D");
                frame.ConnectionEpoch = session.ConnectionEpoch;
                frame.ConnectionId = session.ConnectionId.ToString("D");
                frame.Sequence = frame.PayloadCase == AgentFileFrame.PayloadOneofCase.Hello ? 0 : checked(++_sequence);
                await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            Retire();
            if (Interlocked.Exchange(ref _disposed, 1) == 0) _gate.Dispose();
        }
    }

    internal sealed class ClientFileOperation(Guid requestId, Guid attemptId, string operation, string path,
        string? destinationPath = null, CancellationToken sessionToken = default) : IDisposable
    {
        private readonly object _lifetimeSync = new();
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(sessionToken);
        private int _disposed;
        private FileStream? _writeStream;
        private string? _temporaryPath;
        private readonly object _creditSync = new();
        private TaskCompletionSource _readCreditChanged = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private long _availableReadCreditBytes;

        public Guid RequestId { get; } = requestId;
        public Guid AttemptId { get; } = attemptId;
        public string Operation { get; } = operation;
        public string Path { get; } = path;
        public string? DestinationPath { get; } = destinationPath;
        public CancellationToken Token => _cancellation.Token;
        public ulong NextChunkIndex { get; set; }

        public void Cancel()
        {
            lock (_lifetimeSync)
            {
                if (_disposed == 0) _cancellation.Cancel();
            }
        }

        public void GrantReadCredit(uint availableBytes)
        {
            TaskCompletionSource signal;
            lock (_creditSync)
            {
                _availableReadCreditBytes = checked(_availableReadCreditBytes + availableBytes);
                signal = _readCreditChanged;
                _readCreditChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            signal.TrySetResult();
        }

        public async Task AcquireReadCreditAsync(int bytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                Task wait;
                lock (_creditSync)
                {
                    if (_availableReadCreditBytes >= bytes)
                    {
                        _availableReadCreditBytes -= bytes;
                        return;
                    }

                    wait = _readCreditChanged.Task;
                }

                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        public void BeginWrite()
        {
            Token.ThrowIfCancellationRequested();
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            _temporaryPath = $"{Path}.netratel-{RequestId:N}.tmp";
            _writeStream = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }

        public Task WriteAsync(ReadOnlyMemory<byte> content, CancellationToken cancellationToken) =>
            _writeStream is null ? throw new InvalidOperationException("The write stream has not been opened.") : _writeStream.WriteAsync(content, cancellationToken).AsTask();

        public async Task CommitWriteAsync(CancellationToken cancellationToken)
        {
            if (_writeStream is null || _temporaryPath is null) throw new InvalidOperationException("The write stream has not been opened.");
            await _writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _writeStream.DisposeAsync().ConfigureAwait(false);
            _writeStream = null;
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(_temporaryPath, Path, true);
            _temporaryPath = null;
        }

        public void Dispose()
        {
            lock (_lifetimeSync)
            {
                if (_disposed != 0) return;
                _disposed = 1;
                _cancellation.Cancel();
            }

            lock (_creditSync)
            {
                _readCreditChanged.TrySetCanceled();
            }
            try
            {
                _writeStream?.Dispose();
                _writeStream = null;
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(_temporaryPath))
                {
                    try
                    {
                        File.Delete(_temporaryPath);
                        _temporaryPath = null;
                    }
                    catch (Exception exception)
                    {
                        System.Diagnostics.Trace.TraceWarning($"NetRatel file gateway could not clean up a partial upload: {exception.GetType().Name}.");
                    }
                }
                _cancellation.Dispose();
            }
        }
    }

    private sealed class FileOperationWorkers(Func<Func<Task>, Task> startWorker, CancellationTokenSource streamCancellation)
    {
        private readonly object _sync = new();
        private readonly HashSet<Task> _active = [];
        private Exception? _failure;
        public Exception? Failure { get { lock (_sync) return _failure; } }

        public void Start(Func<Task> work)
        {
            var task = ObserveAsync(startWorker(work));
            lock (_sync) _active.Add(task);
            _ = task.ContinueWith(completed =>
            {
                lock (_sync) _active.Remove(completed);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        private async Task ObserveAsync(Task worker)
        {
            try
            {
                await worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (streamCancellation.IsCancellationRequested)
            {
                // Owned shutdown; the operation's finally has run.
            }
            catch (RpcException exception) when (streamCancellation.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
            {
                // The physical call was aborted during owned shutdown.
            }
            catch (Exception exception)
            {
                lock (_sync) _failure ??= exception;
                streamCancellation.Cancel();
            }
        }

        public Task JoinAsync()
        {
            lock (_sync) return Task.WhenAll(_active.ToArray());
        }
    }
}
