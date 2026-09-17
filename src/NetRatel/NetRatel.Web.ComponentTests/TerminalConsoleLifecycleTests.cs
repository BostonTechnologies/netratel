using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Shared.Contracts.Terminals;
using NetRatel.Web.Components.Pages.Terminal;
using NetRatel.Web.Services.Terminal;
using Xunit;

namespace NetRatel.Web.ComponentTests;

public sealed class TerminalConsoleLifecycleTests
{
    [Fact]
    public async Task Detach_disposes_late_old_channel_and_does_not_replay_waiting_input()
    {
        var terminals = new DelayedInputTerminalService();
        var console = new TerminalConsole();
        SetProperty(console, nameof(TerminalConsole.SessionId), "old");
        SetProperty(console, nameof(TerminalConsole.IsInputEnabled), true);
        SetInjectedProperty(console, "Term", terminals);
        SetInjectedProperty<ILogger<TerminalConsole>>(console, "Logger", NullLogger<TerminalConsole>.Instance);

        var inputMethod = typeof(TerminalConsole).GetMethod("HandleInputAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var pendingInput = (Task)inputMethod.Invoke(console, ["old-command\r"])!;
        await terminals.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var detach = console.DetachInputAsync();
        SetProperty(console, nameof(TerminalConsole.SessionId), "new");
        var oldChannel = new TrackingInputChannel();
        terminals.CompleteOpen(oldChannel);

        await Task.WhenAll(pendingInput, detach).WaitAsync(TimeSpan.FromSeconds(5));
        oldChannel.DisposeCount.Should().Be(1);
        oldChannel.Sent.Should().BeEmpty();
        terminals.ResetOpen();
        var newChannel = new TrackingInputChannel();
        terminals.CompleteOpen(newChannel);
        await ((Task)inputMethod.Invoke(console, ["fresh-command\r"])!).WaitAsync(TimeSpan.FromSeconds(5));
        newChannel.Sent.Should().ContainSingle().Which.Should().Be("fresh-command\r");
        oldChannel.Sent.Should().BeEmpty();
        await console.DisposeAsync();
        newChannel.DisposeCount.Should().Be(1);
    }

    [Fact]
    public async Task DisposeAsync_DisposesAnInputChannelThatCompletesAfterShutdownBegins()
    {
        var terminalService = new DelayedInputTerminalService();
        var console = new TerminalConsole();
        SetProperty(console, nameof(TerminalConsole.SessionId), "session-1");
        SetProperty(console, nameof(TerminalConsole.IsInputEnabled), true);
        SetInjectedProperty(console, "Term", terminalService);
        SetInjectedProperty<ILogger<TerminalConsole>>(console, "Logger", NullLogger<TerminalConsole>.Instance);

        var ensureTask = EnsureInputChannelAsync(console);
        await terminalService.OpenStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var disposeTask = console.DisposeAsync().AsTask();
        await Task.Yield();
        disposeTask.IsCompleted.Should().BeFalse("disposal joins an admitted input-channel initializer");

        var channel = new TrackingInputChannel();
        terminalService.CompleteOpen(channel);

        (await ensureTask.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeNull();
        await disposeTask.WaitAsync(TimeSpan.FromSeconds(5));
        channel.DisposeCount.Should().Be(1);
    }

    private static Task<ITerminalInputChannel?> EnsureInputChannelAsync(TerminalConsole console)
    {
        var method = typeof(TerminalConsole).GetMethod(
            "EnsureInputChannelAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("TerminalConsole input-channel initializer was not found.");

        return (Task<ITerminalInputChannel?>)(method.Invoke(console, [false])
            ?? throw new InvalidOperationException("TerminalConsole input-channel initializer returned null."));
    }

    private static void SetInjectedProperty<T>(TerminalConsole console, string name, T value)
    {
        SetProperty(console, name, value, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    private static void SetProperty<T>(
        TerminalConsole console,
        string name,
        T value,
        BindingFlags bindingFlags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    {
        var property = typeof(TerminalConsole).GetProperty(name, bindingFlags)
            ?? throw new InvalidOperationException($"TerminalConsole injected property '{name}' was not found.");
        property.SetValue(console, value);
    }

    private sealed class DelayedInputTerminalService : ITerminalService
    {
        private TaskCompletionSource<ITerminalInputChannel> _open = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> OpenStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EnsureSubscribedAsync(CancellationToken ct = default) => throw new NotSupportedException();

        public Task<TerminalOpenResponse> OpenSessionAsync(string clientIdentityHex, OpenTerminalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalOpenResponse> OpenGatewaySessionAsync(int tenantId, Guid agentId, OpenTerminalRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalActionResponse> CloseAsync(string sessionId, string? reason = null, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalActionResponse> SendInputAsync(string sessionId, string data, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async Task<ITerminalInputChannel> OpenInputChannelAsync(
            string sessionId,
            Action<TerminalInputChannelStatus>? onStatus = null,
            CancellationToken ct = default)
        {
            OpenStarted.TrySetResult(true);
            return await _open.Task.ConfigureAwait(false);
        }

        public Task<TerminalActionResponse> ResizeAsync(string sessionId, int cols, int rows, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<TerminalStreamMessage> StreamSessionAsync(
            string sessionId,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<IReadOnlyList<TerminalSessionDto>> GetSessionsAsync(string clientIdentityHex, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalSessionDto?> GetSessionAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TerminalSessionDto?> GetGatewaySessionAsync(string sessionId, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public void CompleteOpen(ITerminalInputChannel channel) => _open.TrySetResult(channel);
        public void ResetOpen() => _open = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class TrackingInputChannel : ITerminalInputChannel
    {
        private int _disposeCount;

        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public List<string> Sent { get; } = [];

        public ValueTask SendAsync(string data, CancellationToken ct = default)
        {
            Sent.Add(data);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendImmediateAsync(string data, CancellationToken ct = default) => SendAsync(data, ct);

        public ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
