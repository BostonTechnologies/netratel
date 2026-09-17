using System.Collections.Concurrent;
using FluentAssertions;
using Grpc.Core;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Terminal;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class AgentTerminalGatewayClientTests
{
    [Fact]
    public async Task Draining_writer_before_opened_cannot_emit_synchronous_startup_output()
    {
        var presence = NewPresence();
        using var stream = new RecordingWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(CancellationToken.None);
        using var host = new TestTerminalHost("early output");
        using var terminal = NewTerminal(presence, host);
        terminal.Attach(writer);
        await host.StartAsync(chunk => terminal.WriteOutputAsync(chunk.Data, CancellationToken.None), CancellationToken.None);
        // Drain every queued transport frame, rather than relying on a timing-based
        // assertion while the physical writer could still be scheduled later.
        await writer.StopAsync();
        stream.Frames.Should().BeEmpty("startup bytes must not enter the physical queue until Opened is written");
    }

    [Fact]
    public async Task Concurrent_stdout_stderr_callbacks_preserve_wire_sequence_order()
    {
        var presence = NewPresence();
        using var stream = new RecordingWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(CancellationToken.None);
        using var terminal = NewTerminal(presence);
        terminal.Attach(writer);
        terminal.MarkStarted();
        terminal.TryQueueOpened().Should().BeTrue();
        await Task.WhenAll(Enumerable.Range(0, 32).Select(index => Task.Run(() =>
            terminal.WriteOutputAsync($"callback-{index}", CancellationToken.None))));
        await stream.Output.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await writer.StopAsync();
        stream.Frames.Where(frame => frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output)
            .Select(frame => frame.Output.SessionSequence).Should().Equal(Enumerable.Range(1, 32).Select(value => (ulong)value));
        terminal.DroppedOutputFrames.Should().Be(0);
    }

    [Fact]
    public async Task Output_emitted_synchronously_during_host_start_is_written_after_opened()
    {
        var presence = NewPresence();
        using var stream = new RecordingWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(CancellationToken.None);
        using var host = new TestTerminalHost("startup prompt");
        using var terminal = NewTerminal(presence, host);
        terminal.Attach(writer);

        // Exercise the production host callback order: output occurs inside StartAsync,
        // before the caller can mark the host started or queue its Opened frame.
        await host.StartAsync(chunk => terminal.WriteOutputAsync(chunk.Data, CancellationToken.None), CancellationToken.None);
        stream.Frames.Should().BeEmpty();
        terminal.MarkStarted();
        terminal.TryQueueOpened().Should().BeTrue();
        await stream.Output.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await writer.StopAsync();

        stream.Frames.Select(frame => frame.PayloadCase).Should().Equal(
            AgentTerminalFrame.PayloadOneofCase.Opened, AgentTerminalFrame.PayloadOneofCase.Output);
        stream.Frames.Last().Output.Content.ToStringUtf8().Should().Be("startup prompt");
        terminal.DroppedOutputFrames.Should().Be(0);
    }

    [Fact]
    public async Task Pending_opened_write_keeps_startup_output_bounded_without_blocking_callbacks()
    {
        var presence = NewPresence();
        using var stream = new GatedOpenedWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(CancellationToken.None);
        using var terminal = NewTerminal(presence);
        terminal.Attach(writer);
        terminal.MarkStarted();
        terminal.TryQueueOpened().Should().BeTrue();
        await stream.OpenedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        for (var index = 0; index < 70; index++)
        {
            var pending = terminal.WriteOutputAsync($"startup-{index}", CancellationToken.None);
            pending.IsCompletedSuccessfully.Should().BeTrue("the PTY reader must never wait for Opened or transport capacity");
            await pending;
        }
        await terminal.WriteOutputAsync(new string('x', 16 * 1024 + 1), CancellationToken.None);
        terminal.DroppedOutputFrames.Should().Be(7);
        stream.Frames.Should().BeEmpty("even a queued Opened is not yet a completed physical write");
        stream.AllowOpened();
        await stream.Output.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await writer.StopAsync();
        stream.Frames.First().PayloadCase.Should().Be(AgentTerminalFrame.PayloadOneofCase.Opened);
        var output = stream.Frames.Where(frame => frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output).ToArray();
        output.Should().HaveCount(64);
        output.Select(frame => frame.Output.SessionSequence).Should().Equal(Enumerable.Range(1, 64).Select(value => (ulong)value));
    }

    [Fact]
    public async Task Late_opened_completion_on_replaced_writer_cannot_release_new_writer_output()
    {
        var presence = NewPresence();
        using var stale = new GatedOpenedWriter();
        using var current = new RecordingWriter();
        using var staleWriter = new AgentTerminalGatewayClient.GatewayWriter(stale, presence, "1.0");
        using var currentWriter = new AgentTerminalGatewayClient.GatewayWriter(current, presence, "1.0");
        staleWriter.Start(CancellationToken.None);
        currentWriter.Start(CancellationToken.None);
        using var terminal = NewTerminal(presence);
        terminal.Attach(staleWriter);
        await terminal.WriteOutputAsync("stale prompt", CancellationToken.None);
        terminal.MarkStarted();
        terminal.TryQueueOpened().Should().BeTrue();
        await stale.OpenedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        terminal.Detach(staleWriter);
        terminal.Attach(currentWriter);
        await terminal.WriteOutputAsync("current prompt", CancellationToken.None);
        stale.AllowOpened();
        await stale.OpenedCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await staleWriter.StopAsync();
        current.Frames.Should().BeEmpty();
        await terminal.QueueOpenedAsync(currentWriter, CancellationToken.None);
        await current.Output.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await currentWriter.StopAsync();
        current.Frames.Select(frame => frame.PayloadCase).Should().Equal(
            AgentTerminalFrame.PayloadOneofCase.Opened, AgentTerminalFrame.PayloadOneofCase.Output);
        current.Frames.Last().Output.Content.ToStringUtf8().Should().Be("current prompt");
        terminal.DroppedOutputFrames.Should().Be(1);
    }

    [Fact]
    public async Task Cancelled_opening_discards_staged_output_and_does_not_release_it_after_cleanup()
    {
        var presence = NewPresence();
        using var stream = new GatedOpenedWriter();
        using var cancellation = new CancellationTokenSource();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(cancellation.Token);
        using var terminal = NewTerminal(presence);
        terminal.Attach(writer);
        await terminal.WriteOutputAsync("do not release", CancellationToken.None);
        terminal.TryQueueOpened().Should().BeTrue();
        await stream.OpenedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        terminal.TryBeginCleanup().Should().BeTrue();
        cancellation.Cancel();
        await FluentActions.Awaiting(writer.StopAsync).Should().ThrowAsync<OperationCanceledException>();
        stream.AllowOpened();
        terminal.TryQueueOpened().Should().BeFalse();
        terminal.DroppedOutputFrames.Should().Be(1);
        stream.Frames.Should().NotContain(frame => frame.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output);
    }

    [Theory]
    [InlineData(8, 1)]
    [InlineData(1, 1)]
    public async Task New_presence_owner_recovers_after_server_epoch_reset_and_fences_old_ptys(int oldEpoch, int newEpoch)
    {
        using var client = NewClient();
        using var oldOwner = new CancellationTokenSource();
        using var newOwner = new CancellationTokenSource();
        var oldPresence = NewPresence((ulong)oldEpoch);
        var newPresence = NewPresence((ulong)newEpoch) with { ConnectionId = Guid.NewGuid() };
        var oldFence = AgentTerminalGatewayClient.GatewayPresenceFence.From(oldPresence);
        var newFence = AgentTerminalGatewayClient.GatewayPresenceFence.From(newPresence);
        var oldTerminal = NewTerminal(oldPresence);

        client.TryPreparePresenceFence(oldFence, oldOwner.Token).Should().BeTrue();
        client.TryAddActiveSession(oldTerminal).Should().BeTrue();
        oldOwner.Cancel();

        client.TryPreparePresenceFence(newFence, newOwner.Token).Should().BeTrue();
        client.IsActivePresenceFence(newFence).Should().BeTrue();
        client.IsActivePresenceFence(oldFence).Should().BeFalse();
        oldTerminal.IsFenced.Should().BeTrue();

        using var replacement = NewTerminal(newPresence);
        client.TryAddActiveSession(replacement).Should().BeTrue();
        client.TryPreparePresenceFence(oldFence, oldOwner.Token).Should().BeFalse();
        client.IsActivePresenceFence(newFence).Should().BeTrue();

        using var output = new RecordingWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(output, newPresence, "1.0");
        writer.Start(newOwner.Token);
        oldTerminal.Attach(writer);
        await oldTerminal.WriteOutputAsync("stale output", CancellationToken.None);
        await writer.StopAsync();
        output.Frames.Should().BeEmpty();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cancelled_owner_does_not_allow_a_different_agent_or_tenant(bool changeTenant)
    {
        using var client = NewClient();
        using var oldOwner = new CancellationTokenSource();
        var presence = NewPresence();
        var original = AgentTerminalGatewayClient.GatewayPresenceFence.From(presence);
        client.TryPreparePresenceFence(original, oldOwner.Token).Should().BeTrue();
        oldOwner.Cancel();
        var unrelated = changeTenant
            ? presence with { TenantId = presence.TenantId + 1 }
            : presence with { AgentId = Guid.NewGuid() };

        client.TryPreparePresenceFence(
                AgentTerminalGatewayClient.GatewayPresenceFence.From(unrelated), CancellationToken.None)
            .Should().BeFalse();
        client.IsActivePresenceFence(original).Should().BeTrue();
    }

    [Fact]
    public void Cancelled_or_stale_presence_invocation_cannot_displace_a_newer_fence()
    {
        using var client = NewClient();
        var newerPresence = NewPresence(connectionEpoch: 8);
        var stalePresence = NewPresence(connectionEpoch: 7);

        client.TryPreparePresenceFence(
                AgentTerminalGatewayClient.GatewayPresenceFence.From(newerPresence),
                CancellationToken.None)
            .Should()
            .BeTrue();

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        client.TryPreparePresenceFence(
                AgentTerminalGatewayClient.GatewayPresenceFence.From(stalePresence),
                cancelled.Token)
            .Should()
            .BeFalse();

        client.TryPreparePresenceFence(
                AgentTerminalGatewayClient.GatewayPresenceFence.From(stalePresence),
                CancellationToken.None)
            .Should()
            .BeFalse();

        var sameEpochDifferentConnection = newerPresence with { ConnectionId = Guid.NewGuid() };
        client.TryPreparePresenceFence(
                AgentTerminalGatewayClient.GatewayPresenceFence.From(sameEpochDifferentConnection),
                CancellationToken.None)
            .Should()
            .BeFalse();

        client.IsActivePresenceFence(AgentTerminalGatewayClient.GatewayPresenceFence.From(newerPresence)).Should().BeTrue();
        client.IsActivePresenceFence(AgentTerminalGatewayClient.GatewayPresenceFence.From(stalePresence)).Should().BeFalse();
    }

    [Fact]
    public async Task Exiting_session_reserves_its_closing_key_before_a_duplicate_start_can_be_admitted()
    {
        var client = NewClient();
        var presence = NewPresence();
        var host = new BlockingCloseTerminalHost();
        var exiting = NewTerminal(presence, host);
        var duplicate = NewTerminal(presence);

        try
        {
            client.TryPreparePresenceFence(
                    AgentTerminalGatewayClient.GatewayPresenceFence.From(presence),
                    CancellationToken.None)
                .Should()
                .BeTrue();
            client.TryAddActiveSession(exiting).Should().BeTrue();

            client.HandleExited(exiting, "shell_exited");
            await host.CloseRequested.WaitAsync(TimeSpan.FromSeconds(1));

            client.TryAddActiveSession(duplicate).Should().BeFalse();
        }
        finally
        {
            host.AllowClose();
            duplicate.Dispose();
            client.Dispose();
        }
    }

    [Fact]
    public async Task Cleanup_saturation_defers_teardown_without_blocking_or_disposing_on_the_response_path()
    {
        var client = NewClient();
        var presence = NewPresence();
        var blockers = Enumerable.Range(0, AgentTerminalGatewayClient.CleanupWorkerCount)
            .Select(_ => new BlockingCloseTerminalHost())
            .ToArray();
        var overflowHost = new BlockingDisposeTerminalHost();

        try
        {
            client.TryPreparePresenceFence(
                    AgentTerminalGatewayClient.GatewayPresenceFence.From(presence),
                    CancellationToken.None)
                .Should()
                .BeTrue();

            foreach (var blocker in blockers)
            {
                client.QueueCleanup(NewTerminal(presence, blocker), "terminal_presence_fenced", reportClosed: false);
                await blocker.CloseRequested.WaitAsync(TimeSpan.FromSeconds(1));
            }

            foreach (var _ in Enumerable.Range(0, AgentTerminalGatewayClient.CleanupQueueCapacity))
            {
                client.QueueCleanup(NewTerminal(presence), "terminal_presence_fenced", reportClosed: false);
            }

            var enqueueOverflow = Task.Run(() =>
                client.QueueCleanup(NewTerminal(presence, overflowHost), "terminal_presence_fenced", reportClosed: false));

            await enqueueOverflow.WaitAsync(TimeSpan.FromSeconds(1));
            overflowHost.DisposeStarted.IsCompleted.Should().BeFalse();

            foreach (var blocker in blockers)
            {
                blocker.AllowClose();
            }

            await overflowHost.CloseRequested.WaitAsync(TimeSpan.FromSeconds(5));
            await overflowHost.DisposeStarted.WaitAsync(TimeSpan.FromSeconds(5));
            overflowHost.AllowDispose();
            await overflowHost.Disposed.WaitAsync(TimeSpan.FromSeconds(1));
        }
        finally
        {
            overflowHost.AllowDispose();
            foreach (var blocker in blockers)
            {
                blocker.AllowClose();
            }

            client.Dispose();
        }
    }

    [Fact]
    public async Task Output_callback_drops_while_detached_and_never_waits_for_a_writer()
    {
        var presence = NewPresence();
        using var cancellation = new CancellationTokenSource();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(
            new BlockingOutputWriter(),
            presence,
            "1.0",
            TimeSpan.FromSeconds(1));
        writer.Start(cancellation.Token);

        using var terminal = NewTerminal(presence);
        terminal.MarkStarted();

        var output = terminal.WriteOutputAsync("output while detached", CancellationToken.None);

        await output.WaitAsync(TimeSpan.FromSeconds(1));
        terminal.DroppedOutputFrames.Should().Be(1);

        cancellation.Cancel();
        await writer.StopAsync();
    }

    [Fact]
    public async Task Lifecycle_is_sent_through_the_replacement_writer_not_the_stale_writer()
    {
        var presence = NewPresence();
        using var stale = new RecordingWriter();
        using var current = new RecordingWriter();
        using var staleGatewayWriter = new AgentTerminalGatewayClient.GatewayWriter(stale, presence, "1.0");
        using var currentGatewayWriter = new AgentTerminalGatewayClient.GatewayWriter(current, presence, "1.0");
        staleGatewayWriter.Start(CancellationToken.None);
        currentGatewayWriter.Start(CancellationToken.None);

        using var terminal = NewTerminal(presence);
        terminal.Attach(staleGatewayWriter);
        terminal.Detach(staleGatewayWriter);
        terminal.Attach(currentGatewayWriter);

        terminal.TryQueueClosed("operator_closed").Should().BeTrue();

        var frame = await current.Closed.Task.WaitAsync(TimeSpan.FromSeconds(1));
        frame.Closed.SessionId.Should().Be("terminal-1");
        frame.Closed.Generation.Should().Be(7);
        stale.Frames.Should().BeEmpty();

        await staleGatewayWriter.StopAsync();
        await currentGatewayWriter.StopAsync();
    }

    [Fact]
    public async Task Output_backpressure_does_not_block_a_lifecycle_enqueue()
    {
        var presence = NewPresence();
        var stream = new BlockingOutputWriter();
        using var cancellation = new CancellationTokenSource();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(
            stream,
            presence,
            "1.0",
            TimeSpan.FromSeconds(1));
        writer.Start(cancellation.Token);

        using var terminal = NewTerminal(presence);
        terminal.MarkStarted();
        terminal.Attach(writer);
        terminal.TryQueueOpened().Should().BeTrue();

        await terminal.WriteOutputAsync("blocked output", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        await stream.OutputStarted.WaitAsync(TimeSpan.FromSeconds(1));

        terminal.TryQueueOpened().Should().BeTrue();

        cancellation.Cancel();
        await FluentActions.Awaiting(writer.StopAsync)
            .Should()
            .ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Fenced_terminal_cannot_reannounce_or_enqueue_later_output()
    {
        var presence = NewPresence();
        using var stream = new RecordingWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(stream, presence, "1.0");
        writer.Start(CancellationToken.None);

        using var terminal = NewTerminal(presence);
        terminal.MarkStarted();
        terminal.Attach(writer);
        terminal.Owns(AgentTerminalGatewayClient.GatewayPresenceFence.From(presence)).Should().BeTrue();
        terminal.Owns(AgentTerminalGatewayClient.GatewayPresenceFence.From(new GatewayPresenceSession(
            presence.TenantId,
            presence.AgentId,
            presence.ConnectionEpoch + 1,
            Guid.NewGuid()))).Should().BeFalse();
        terminal.Fence();

        terminal.Owns(AgentTerminalGatewayClient.GatewayPresenceFence.From(presence)).Should().BeFalse();
        terminal.TryQueueOpened().Should().BeFalse();
        await terminal.WriteOutputAsync("discarded", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(1));
        stream.Frames.Should().BeEmpty();

        await writer.StopAsync();
    }

    [Fact]
    public async Task Blocked_physical_write_fails_within_the_configured_bound()
    {
        var presence = NewPresence();
        var stream = new BlockingOutputWriter();
        using var writer = new AgentTerminalGatewayClient.GatewayWriter(
            stream,
            presence,
            "1.0",
            TimeSpan.FromMilliseconds(25));
        writer.Start(CancellationToken.None);

        writer.TryQueueOutput("terminal-1", 7, 1, "blocked output").Should().BeTrue();
        await stream.OutputStarted.WaitAsync(TimeSpan.FromSeconds(1));

        await FluentActions.Awaiting(() => writer.Completion)
            .Should()
            .ThrowAsync<IOException>();
    }

    private static AgentTerminalGatewayClient NewClient() =>
        new(
            new GatewayClientOptions(),
            new TerminalHostOptions(),
            ["bash"],
            _ => { });

    private static GatewayPresenceSession NewPresence(ulong connectionEpoch = 4) =>
        new(3, Guid.Parse("9ed6041f-6e52-4ebb-bb56-21d43e183e7d"), connectionEpoch, Guid.Parse("dbdb2f03-b078-4b80-9788-ca7d0dde8ec8"));

    private static AgentTerminalGatewayClient.GatewayTerminalSession NewTerminal(
        GatewayPresenceSession presence,
        ITerminalHostSession? host = null) =>
        new(
            "terminal-1",
            7,
            AgentTerminalGatewayClient.GatewayPresenceFence.From(presence),
            host ?? new TestTerminalHost(),
            _ => { });

    private sealed class GatedOpenedWriter : IClientStreamWriter<AgentTerminalFrame>, IDisposable
    {
        private readonly TaskCompletionSource _allowOpened = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenedStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource OpenedCompleted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<AgentTerminalFrame> Frames { get; } = new();
        public WriteOptions? WriteOptions { get; set; }
        public Task CompleteAsync() => Task.CompletedTask;
        public async Task WriteAsync(AgentTerminalFrame message)
        {
            if (message.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Opened)
            {
                OpenedStarted.TrySetResult();
                await _allowOpened.Task;
            }
            Frames.Enqueue(message.Clone());
            if (message.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Opened) OpenedCompleted.TrySetResult();
            if (message.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output) Output.TrySetResult();
        }
        public void AllowOpened() => _allowOpened.TrySetResult();
        public void Dispose() => AllowOpened();
    }

    private sealed class BlockingOutputWriter : IClientStreamWriter<AgentTerminalFrame>
    {
        private readonly TaskCompletionSource _outputStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _neverComplete = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task OutputStarted => _outputStarted.Task;
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync() => Task.CompletedTask;

        public Task WriteAsync(AgentTerminalFrame message)
        {
            if (message.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output)
            {
                _outputStarted.TrySetResult();
                return _neverComplete.Task;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingWriter : IClientStreamWriter<AgentTerminalFrame>, IDisposable
    {
        public ConcurrentQueue<AgentTerminalFrame> Frames { get; } = new();
        public TaskCompletionSource<AgentTerminalFrame> Closed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<AgentTerminalFrame> Output { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public WriteOptions? WriteOptions { get; set; }

        public Task CompleteAsync() => Task.CompletedTask;

        public Task WriteAsync(AgentTerminalFrame message)
        {
            var copy = message.Clone();
            Frames.Enqueue(copy);
            if (copy.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Output) Output.TrySetResult(copy);
            if (copy.PayloadCase == AgentTerminalFrame.PayloadOneofCase.Closed)
            {
                Closed.TrySetResult(copy);
            }

            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestTerminalHost(string? startupOutput = null) : ITerminalHostSession
    {
        public string Backend => "test";
        public string ShellType => "bash";
        public bool HasExited => false;
        public event Action<string?>? Exited
        {
            add { }
            remove { }
        }

        public Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct) =>
            startupOutput is null ? Task.CompletedTask : onOutput(new TerminalOutputChunk { Direction = "stdout", Data = startupOutput });

        public Task WriteInputAsync(string data, CancellationToken ct) => Task.CompletedTask;

        public Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct) =>
            Task.FromResult(TerminalResizeResult.Applied(cols, rows));

        public Task RequestCloseAsync(string? reason, CancellationToken ct) => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class BlockingCloseTerminalHost : ITerminalHostSession
    {
        private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _allowClose = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Backend => "test";
        public string ShellType => "bash";
        public bool HasExited => false;
        public Task CloseRequested => _closeRequested.Task;
        public event Action<string?>? Exited
        {
            add { }
            remove { }
        }

        public Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct) => Task.CompletedTask;

        public Task WriteInputAsync(string data, CancellationToken ct) => Task.CompletedTask;

        public Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct) =>
            Task.FromResult(TerminalResizeResult.Applied(cols, rows));

        public Task RequestCloseAsync(string? reason, CancellationToken ct)
        {
            _closeRequested.TrySetResult();
            return _allowClose.Task;
        }

        public void AllowClose() => _allowClose.TrySetResult();

        public void Dispose()
        {
        }
    }

    private sealed class BlockingDisposeTerminalHost : ITerminalHostSession
    {
        private readonly TaskCompletionSource _closeRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim _allowDispose = new();
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Backend => "test";
        public string ShellType => "bash";
        public bool HasExited => false;
        public Task CloseRequested => _closeRequested.Task;
        public Task DisposeStarted => _disposeStarted.Task;
        public Task Disposed => _disposed.Task;
        public event Action<string?>? Exited
        {
            add { }
            remove { }
        }

        public Task StartAsync(Func<TerminalOutputChunk, Task> onOutput, CancellationToken ct) => Task.CompletedTask;

        public Task WriteInputAsync(string data, CancellationToken ct) => Task.CompletedTask;

        public Task<TerminalResizeResult> ResizeAsync(int cols, int rows, CancellationToken ct) =>
            Task.FromResult(TerminalResizeResult.Applied(cols, rows));

        public Task RequestCloseAsync(string? reason, CancellationToken ct)
        {
            _closeRequested.TrySetResult();
            return Task.CompletedTask;
        }

        public void AllowDispose() => _allowDispose.Set();

        public void Dispose()
        {
            _disposeStarted.TrySetResult();
            _allowDispose.Wait();
            _disposed.TrySetResult();
        }
    }
}
