using System.Collections.Concurrent;
using System.Security.Cryptography;
using FluentAssertions;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Services;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentFileGatewayClientTests
{
    [Fact]
    public void Operation_cleanup_is_idempotent_when_completion_races_stream_teardown()
    {
        var operation = new AgentFileGatewayClient.ClientFileOperation(
            Guid.NewGuid(), Guid.NewGuid(), "read", Path.GetTempPath());
        operation.Dispose();
        operation.Invoking(value => value.Dispose()).Should().NotThrow();
    }

    [Fact]
    public async Task Physical_write_receives_the_owned_cancellation_token_and_finishes_before_cleanup()
    {
        var stream = new BlockedWriter();
        var presence = new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid());
        using var writer = new AgentFileGatewayClient.FileGatewayWriter(stream, presence, "1.0");
        using var cancellation = new CancellationTokenSource();
        var write = writer.WriteAsync(new AgentFileFrame { Hello = new AgentFileHello() }, cancellation.Token);
        await stream.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancellation.Cancel();
        try
        {
            await FluentActions.Awaiting(() => write.WaitAsync(TimeSpan.FromSeconds(2)))
                .Should().ThrowAsync<OperationCanceledException>();
            stream.Token.IsCancellationRequested.Should().BeTrue();
        }
        finally
        {
            stream.Release.TrySetResult();
            try { await write; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Retired_writer_rejects_late_callbacks_after_its_gate_is_disposed()
    {
        var stream = new BlockedWriter();
        var writer = new AgentFileGatewayClient.FileGatewayWriter(stream,
            new GatewayPresenceSession(7, Guid.NewGuid(), 41, Guid.NewGuid()), "1.0");
        writer.Dispose();
        await FluentActions.Awaiting(() => writer.WriteAsync(new AgentFileFrame(), CancellationToken.None))
            .Should().ThrowAsync<OperationCanceledException>();
        stream.Started.Task.IsCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Stream_cancellation_releases_read_credit_wait_before_operation_disposal()
    {
        using var session = new CancellationTokenSource();
        using var operation = new AgentFileGatewayClient.ClientFileOperation(
            Guid.NewGuid(), Guid.NewGuid(), "read", Path.GetTempPath(), sessionToken: session.Token);
        var waiting = operation.AcquireReadCreditAsync(16 * 1024, operation.Token);
        waiting.IsCompleted.Should().BeFalse();
        session.Cancel();
        await FluentActions.Awaiting(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)))
            .Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Completed_upload_preserves_bytes_and_removes_its_temporary_file()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "complete.txt");
        using var operation = new AgentFileGatewayClient.ClientFileOperation(Guid.NewGuid(), Guid.NewGuid(), "write", path);
        var content = "two bounded upload chunks"u8.ToArray();
        operation.BeginWrite();
        await operation.WriteAsync(content.AsMemory(0, 8), operation.Token);
        await operation.WriteAsync(content.AsMemory(8), operation.Token);
        await operation.CommitWriteAsync(operation.Token);
        var uploaded = await File.ReadAllBytesAsync(path);
        uploaded.Should().Equal(content);
        SHA256.HashData(uploaded).Should().Equal(SHA256.HashData(content));
        Directory.EnumerateFiles(directory.Path, "*.tmp").Should().BeEmpty();
    }

    [Fact]
    public async Task Unexpected_worker_fault_remains_visible_and_is_not_retried_as_a_transport_failure()
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "list");
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var failure = new InvalidOperationException("unexpected file worker defect");
        var run = NewClient(host, _ => Task.FromException(failure))
            .RunForPresenceSessionAsync(NewPresence(), "test-token", stopping.Token);
        try
        {
            (await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<InvalidOperationException>()).Which.Should().BeSameAs(failure);
            gateway.Hellos.Should().ContainSingle();
        }
        finally
        {
            stopping.Cancel();
            gateway.Reset.TrySetResult();
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Stream_loss_joins_scheduled_or_completing_listing_before_next_authorized_browse(bool beforeWorkerStarts)
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "list");
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var scheduled = Signal();
        var operationFinished = Signal();
        var allowExit = Signal();
        var workerExited = Signal();
        var workers = 0;
        var presence = NewPresence();
        var run = NewClient(host, async work =>
        {
            if (Interlocked.Increment(ref workers) != 1) { await work(); return; }
            scheduled.TrySetResult();
            if (beforeWorkerStarts) await allowExit.Task;
            try { await work(); }
            finally
            {
                operationFinished.TrySetResult();
                if (!beforeWorkerStarts) await allowExit.Task;
                workerExited.TrySetResult();
            }
        }).RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
        try
        {
            await (beforeWorkerStarts ? scheduled : operationFinished).Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (!beforeWorkerStarts) presence.SetAccessToken("fresh-token");
            gateway.Reset.TrySetResult();
            await gateway.FirstCallEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.NextBrowse.Task.IsCompleted.Should().BeFalse("the prior worker still owns its resources");
            run.IsCompleted.Should().BeFalse();
            allowExit.TrySetResult();
            await gateway.NextBrowse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            workerExited.Task.IsCompletedSuccessfully.Should().BeTrue();
            gateway.Hellos.Should().HaveCount(2);
            gateway.Authorization.Should().Equal("Bearer test-token", beforeWorkerStarts ? "Bearer test-token" : "Bearer fresh-token");
            gateway.NextEntries.Should().Contain(entry => entry.Name == "data.txt");
            gateway.DeniedOutsideRoot.Should().BeTrue();
            if (beforeWorkerStarts)
                gateway.FirstFrames.Should().NotContain(frame => frame.ListPage != null || frame.Completed != null);
        }
        finally
        {
            stopping.Cancel();
            allowExit.TrySetResult();
            gateway.Reset.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Stream_loss_during_directory_enumeration_joins_the_iterator_and_rejects_its_late_page()
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "list");
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        using var releaseIterator = new ManualResetEventSlim();
        var enumerating = Signal();
        var iteratorExited = Signal();
        var iterations = 0;
        var filesystem = new FileSystemService();
        IEnumerable<FileSystemService.BrowseEntry> Enumerate(string path)
        {
            if (Interlocked.Increment(ref iterations) != 1)
            {
                foreach (var entry in filesystem.EnumerateEntries(path)) yield return entry;
                yield break;
            }
            try
            {
                yield return new(path, "first-page", Path.Combine(path, "first-page"), false, 0);
                enumerating.TrySetResult();
                releaseIterator.Wait();
                yield return new(path, "late-page", Path.Combine(path, "late-page"), false, 0);
            }
            finally { iteratorExited.TrySetResult(); }
        }
        var run = NewClient(host, enumerateEntries: Enumerate)
            .RunForPresenceSessionAsync(NewPresence(), "test-token", stopping.Token);
        try
        {
            await enumerating.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.Reset.TrySetResult();
            await gateway.FirstCallEnded.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.NextBrowse.Task.IsCompleted.Should().BeFalse();
            releaseIterator.Set();
            await gateway.NextBrowse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            iteratorExited.Task.IsCompletedSuccessfully.Should().BeTrue();
            gateway.FirstFrames.SelectMany(frame => frame.ListPage?.Entries.AsEnumerable() ?? [])
                .Should().NotContain(entry => entry.Name == "late-page");
        }
        finally
        {
            stopping.Cancel();
            releaseIterator.Set();
            gateway.Reset.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Credit_stalled_read_is_cancelled_and_joined_before_isolated_file_reconnect()
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "read");
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var started = Signal();
        var finished = Signal();
        var workers = 0;
        var run = NewClient(host, async work =>
        {
            if (Interlocked.Increment(ref workers) != 1) { await work(); return; }
            started.TrySetResult();
            try { await work(); }
            finally { finished.TrySetResult(); }
        }).RunForPresenceSessionAsync(NewPresence(), "test-token", stopping.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await gateway.FirstOperationReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finished.Task.IsCompleted.Should().BeFalse("the server has not granted any read credit");
            gateway.Reset.TrySetResult();
            await gateway.NextBrowse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            finished.Task.IsCompletedSuccessfully.Should().BeTrue();
            gateway.FirstFrames.Should().NotContain(frame => frame.TransferChunk != null);
            gateway.Hellos.Should().HaveCount(2);
        }
        finally
        {
            stopping.Cancel();
            gateway.Reset.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_or_cancelled_upload_deletes_partial_file_and_next_browse_succeeds(bool cancelOperation)
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "write", cancelUpload: cancelOperation);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var run = NewClient(host).RunForPresenceSessionAsync(NewPresence(), "test-token", stopping.Token);
        try
        {
            await gateway.FirstOperationReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var temporary = $"{gateway.UploadPath}.netratel-{gateway.FirstRequestId:N}.tmp";
            File.Exists(temporary).Should().Be(!cancelOperation);
            gateway.Reset.TrySetResult();
            await gateway.NextBrowse.Task.WaitAsync(TimeSpan.FromSeconds(5));
            File.Exists(temporary).Should().BeFalse();
            File.Exists(gateway.UploadPath).Should().BeFalse();
            gateway.NextEntries.Should().Contain(entry => entry.Name == "data.txt");
        }
        finally
        {
            stopping.Cancel();
            gateway.Reset.TrySetResult();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Unresponsive_worker_faults_capability_within_cleanup_budget_and_never_starts_a_replacement()
    {
        using var directory = new TestDirectory();
        var gateway = new ResetFileGateway(directory.Path, "list");
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var scheduled = Signal();
        var allowExit = Signal();
        var exited = Signal();
        var agent = NewClient(host, async work =>
        {
            scheduled.TrySetResult();
            await allowExit.Task;
            try { await work(); }
            finally { exited.TrySetResult(); }
        }, TimeSpan.FromMilliseconds(100));
        var presence = NewPresence();
        var run = agent.RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
        try
        {
            await scheduled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.Reset.TrySetResult();
            await FluentActions.Awaiting(() => run.WaitAsync(TimeSpan.FromSeconds(5)))
                .Should().ThrowAsync<TimeoutException>().WithMessage("File gateway workers*");
            await FluentActions.Awaiting(() => agent.RunForPresenceSessionAsync(presence, "test-token", stopping.Token))
                .Should().ThrowAsync<TimeoutException>().WithMessage("The retired file worker*");
            using (var successorShutdown = new CancellationTokenSource())
            {
                var pendingSuccessor = agent.RunForPresenceSessionAsync(presence, "test-token", successorShutdown.Token);
                successorShutdown.Cancel();
                await FluentActions.Awaiting(() => pendingSuccessor).Should().ThrowAsync<OperationCanceledException>();
            }
            gateway.Hellos.Should().ContainSingle("an unreaped filesystem worker prevents another physical owner");
            allowExit.TrySetResult();
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await agent.RetiredWorkerCleanup.WaitAsync(TimeSpan.FromSeconds(5));
            gateway.FirstFrames.Should().NotContain(frame => frame.ListPage != null || frame.Completed != null);
            var successor = agent.RunForPresenceSessionAsync(presence, "test-token", stopping.Token);
            try
            {
                await gateway.NextBrowse.Task.WaitAsync(TimeSpan.FromSeconds(5));
                gateway.Hellos.Should().HaveCount(2);
                gateway.NextEntries.Should().Contain(entry => entry.Name == "data.txt");
            }
            finally
            {
                stopping.Cancel();
                await successor.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        finally
        {
            stopping.Cancel();
            allowExit.TrySetResult();
            gateway.Reset.TrySetResult();
        }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static GatewayPresenceSession NewPresence() => new(7, Guid.NewGuid(), 41, Guid.NewGuid());

    private static AgentFileGatewayClient NewClient(IHost host, Func<Func<Task>, Task>? startWorker = null,
        TimeSpan? cleanupTimeout = null, Func<string, IEnumerable<FileSystemService.BrowseEntry>>? enumerateEntries = null)
        => new(new GatewayClientOptions { Endpoint = "https://gateway.test" },
        new FileSystemService(), _ => { },
        _ => GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() }),
        startWorker, cleanupTimeout, enumerateEntries);

    private static async Task<IHost> BuildHostAsync(ResetFileGateway gateway)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddSingleton(gateway);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<ResetFileGateway>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"netratel-file-{Guid.NewGuid():N}");
        public TestDirectory()
        {
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "data.txt"), "integrity checked file content");
        }
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }

    private sealed class ResetFileGateway(string root, string firstOperation, bool cancelUpload = false)
        : AgentFileGateway.AgentFileGatewayBase
    {
        private int _attempts;
        public Guid FirstRequestId { get; } = Guid.NewGuid();
        public string UploadPath => Path.Combine(root, "upload.txt");
        public TaskCompletionSource Reset { get; } = Signal();
        public TaskCompletionSource FirstCallEnded { get; } = Signal();
        public TaskCompletionSource FirstOperationReady { get; } = Signal();
        public TaskCompletionSource NextBrowse { get; } = Signal();
        public ConcurrentQueue<AgentFileFrame> Hellos { get; } = new();
        public ConcurrentQueue<string> Authorization { get; } = new();
        public ConcurrentQueue<AgentFileFrame> FirstFrames { get; } = new();
        public List<FileEntry> NextEntries { get; } = [];
        public bool DeniedOutsideRoot { get; private set; }

        public override async Task Connect(IAsyncStreamReader<AgentFileFrame> requests,
            IServerStreamWriter<GatewayFileFrame> responses, ServerCallContext context)
        {
            if (!await requests.MoveNext(context.CancellationToken)) return;
            var hello = requests.Current.Clone();
            var attempt = Interlocked.Increment(ref _attempts);
            Hellos.Enqueue(hello);
            Authorization.Enqueue(context.RequestHeaders.GetValue("authorization") ?? string.Empty);
            ulong sequence = 0;
            var accepted = Frame();
            accepted.Accepted = new FileConnectAccepted { FileAuthority = "akka" };
            await responses.WriteAsync(accepted, context.CancellationToken);
            if (attempt > 1)
            {
                var denied = Dispatch("list", Path.GetDirectoryName(root)!, Guid.NewGuid());
                await responses.WriteAsync(denied, context.CancellationToken);
                var browse = Dispatch("list", root, Guid.NewGuid());
                await responses.WriteAsync(browse, context.CancellationToken);
                while (await requests.MoveNext(context.CancellationToken))
                {
                    var frame = requests.Current;
                    if (frame.Failed?.RequestId == denied.Dispatch.RequestId)
                        DeniedOutsideRoot = frame.Failed.Code == "access_denied";
                    if (frame.ListPage?.RequestId == browse.Dispatch.RequestId)
                        NextEntries.AddRange(frame.ListPage.Entries.Select(entry => entry.Clone()));
                    if (frame.Completed?.RequestId == browse.Dispatch.RequestId)
                    {
                        NextBrowse.TrySetResult();
                        break;
                    }
                }
                try { await Task.Delay(Timeout.Infinite, context.CancellationToken); }
                catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested) { }
                return;
            }

            var path = firstOperation == "list" ? root : firstOperation == "write" ? UploadPath : Path.Combine(root, "data.txt");
            var dispatch = Dispatch(firstOperation, path, FirstRequestId);
            await responses.WriteAsync(dispatch, context.CancellationToken);
            while (await requests.MoveNext(context.CancellationToken))
            {
                FirstFrames.Enqueue(requests.Current.Clone());
                if (requests.Current.RequestAccepted?.RequestId == dispatch.Dispatch.RequestId) break;
            }
            if (firstOperation == "write")
            {
                var content = ByteString.CopyFromUtf8("partial upload");
                var chunk = Frame();
                chunk.TransferChunk = new FileTransferChunk
                {
                    RequestId = dispatch.Dispatch.RequestId,
                    AttemptId = dispatch.Dispatch.AttemptId,
                    ChunkIndex = 0,
                    Content = content,
                    Sha256 = Convert.ToHexString(SHA256.HashData(content.Span)),
                    IsLastChunk = false
                };
                await responses.WriteAsync(chunk, context.CancellationToken);
                if (cancelUpload)
                {
                    var cancel = Frame();
                    cancel.Cancel = new FileRequestCancel { RequestId = dispatch.Dispatch.RequestId, AttemptId = dispatch.Dispatch.AttemptId };
                    await responses.WriteAsync(cancel, context.CancellationToken);
                }
            }
            var probe = Dispatch("stat", Path.Combine(root, "data.txt"), Guid.NewGuid());
            if (firstOperation != "list") await responses.WriteAsync(probe, context.CancellationToken);
            using var drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            var drain = DrainAsync();
            try
            {
                await Reset.Task.WaitAsync(context.CancellationToken);
            }
            finally
            {
                drainCancellation.Cancel();
                try { await drain; }
                catch (OperationCanceledException) when (drainCancellation.IsCancellationRequested) { }
                FirstCallEnded.TrySetResult();
            }
            throw new RpcException(new Status(StatusCode.Unavailable, "simulated isolated file stream reset"));

            async Task DrainAsync()
            {
                while (await requests.MoveNext(drainCancellation.Token))
                {
                    FirstFrames.Enqueue(requests.Current.Clone());
                    if (requests.Current.Completed?.RequestId == probe.Dispatch.RequestId)
                        FirstOperationReady.TrySetResult();
                }
            }

            GatewayFileFrame Frame() => new()
            {
                ProtocolVersion = hello.ProtocolVersion,
                TenantId = hello.TenantId,
                ClientId = hello.ClientId,
                ConnectionEpoch = hello.ConnectionEpoch,
                ConnectionId = hello.ConnectionId,
                Sequence = sequence++
            };

            GatewayFileFrame Dispatch(string operation, string dispatchedPath, Guid requestId)
            {
                var frame = Frame();
                frame.Dispatch = new FileRequestDispatch
                {
                    RequestId = requestId.ToString("D"), AttemptId = Guid.NewGuid().ToString("D"),
                    Operation = operation, Path = dispatchedPath, PageSize = 1, AllowedRoots = { root }
                };
                return frame;
            }
        }
    }

    private sealed class BlockedWriter : IClientStreamWriter<AgentFileFrame>
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public Task WriteAsync(AgentFileFrame frame) => WriteAsync(frame, CancellationToken.None);
        public async Task WriteAsync(AgentFileFrame frame, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }
}
