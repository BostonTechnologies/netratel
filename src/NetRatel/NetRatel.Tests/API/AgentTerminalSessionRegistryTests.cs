using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTerminalSessionRegistryTests
{
    [Fact]
    public async Task Replay_SequentialInputThenReadAndRetry_PreservesOutputBetweenToolCalls()
    {
        using var fixture = await OpenReplayFixtureAsync();
        await fixture.Registry.SendInputAsync(fixture.Session.SessionId, 1, "printf marker\n"u8.ToArray(), CancellationToken.None);
        (await fixture.Registration.Reader.ReadAsync()).Input.Content.ToStringUtf8().Should().Be("printf marker\n");
        await fixture.OutputAsync(1, "marker\n");
        await fixture.OutputAsync(2, "prompt");

        var first = await fixture.ReadAsync(0, records: 1);
        first.Records.Select(record => Encoding.UTF8.GetString(record.Content.Span)).Should().Equal("marker\n");
        first.NextSequence.Should().Be(1);
        first.HasMore.Should().BeTrue();
        first.Gap.Should().BeFalse();
        (await fixture.ReadAsync(0, records: 1)).Should().BeEquivalentTo(first, "retrying a lost HTTP response must not consume output");
        var second = await fixture.ReadAsync(first.NextSequence);
        second.Records.Should().ContainSingle().Which.Sequence.Should().Be(2);
        await fixture.OutputAsync(3, "between calls");
        (await fixture.ReadAsync(second.NextSequence)).Records.Should().ContainSingle().Which.Sequence.Should().Be(3);
    }

    [Fact]
    public async Task Replay_ConcurrentWaitingReaders_BothObserveOutputWithoutAttachmentRace()
    {
        using var fixture = await OpenReplayFixtureAsync();
        var first = fixture.ReadAsync(0);
        var second = fixture.ReadAsync(0);
        first.IsCompleted.Should().BeFalse();
        second.IsCompleted.Should().BeFalse();
        await fixture.OutputAsync(1, "wakeup");
        var windows = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        windows.Should().OnlyContain(window => window.NextSequence == 1 && window.Records.Count == 1);
        (await fixture.ReadAsync(0)).Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Replay_TimeoutAndCancellation_DoNotConsumeLaterOutput()
    {
        var clock = new ManualTimeProvider();
        using var fixture = await OpenReplayFixtureAsync(clock);
        var pending = fixture.ReadAsync(0);
        pending.IsCompleted.Should().BeFalse();
        clock.Advance(TimeSpan.FromSeconds(15));
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Records.Should().BeEmpty();
        using var cancellation = new CancellationTokenSource();
        var cancelled = fixture.Registry.ReadOutputWindowAsync(fixture.Session.SessionId, 1, 0, 10, 1024, TimeSpan.FromSeconds(15), cancellation.Token);
        cancellation.Cancel();
        await FluentActions.Awaiting(() => cancelled).Should().ThrowAsync<OperationCanceledException>();
        await fixture.OutputAsync(1, "after timeout");
        (await fixture.ReadAsync(0)).Records.Should().ContainSingle();
    }

    [Fact]
    public async Task Replay_BoundedHistory_ReportsEvictionAndRetainsFinalOutputThroughClose()
    {
        using var fixture = await OpenReplayFixtureAsync();
        for (ulong sequence = 1; sequence <= 70; sequence++) await fixture.OutputAsync(sequence, "data");
        var window = await fixture.ReadAsync(0, records: 100);
        window.Records.Should().HaveCount(64);
        window.Records[0].Sequence.Should().Be(7);
        window.NextSequence.Should().Be(70);
        window.Gap.Should().BeTrue();
        window.HasMore.Should().BeFalse();
        var pending = fixture.ReadAsync(70);
        pending.IsCompleted.Should().BeFalse();
        fixture.Close();
        (await pending.WaitAsync(TimeSpan.FromSeconds(5))).Completed.Should().BeTrue();
        var final = await fixture.ReadAsync(69);
        final.Completed.Should().BeTrue();
        final.Records.Should().ContainSingle().Which.Sequence.Should().Be(70);
    }

    [Fact]
    public async Task Replay_ByteBound_PaginatesWithoutDroppingTheNextRecord()
    {
        using var fixture = await OpenReplayFixtureAsync();
        await fixture.OutputAsync(1, "one");
        await fixture.OutputAsync(2, "two");
        var first = await fixture.ReadAsync(0, bytes: 3);
        first.NextSequence.Should().Be(1);
        first.HasMore.Should().BeTrue();
        var second = await fixture.ReadAsync(first.NextSequence, bytes: 3);
        second.NextSequence.Should().Be(2);
        second.HasMore.Should().BeFalse();
        var limited = await FluentActions.Awaiting(() => fixture.ReadAsync(0, bytes: 2)).Should().ThrowAsync<TerminalGatewayActionException>();
        limited.Which.Code.Should().Be("terminal_output_limit_exceeded");
        var invalid = await FluentActions.Awaiting(() => fixture.ReadAsync(3)).Should().ThrowAsync<TerminalGatewayActionException>();
        invalid.Which.Code.Should().Be("terminal_output_cursor_invalid");
    }

    [Fact]
    public async Task Replay_SequenceAndGenerationFences_RejectDuplicateOrForeignOutput()
    {
        using var fixture = await OpenReplayFixtureAsync();
        await fixture.OutputAsync(2, "accepted");
        foreach (var sequence in new[] { 1UL, 2UL })
            (await fixture.Registry.TryReceiveOutputAsync(fixture.Client, new TerminalOutput
            {
                SessionId = fixture.Session.SessionId, Generation = 1, SessionSequence = sequence,
                Content = Google.Protobuf.ByteString.CopyFromUtf8("duplicate")
            }, CancellationToken.None)).Should().BeFalse();
        var window = await fixture.ReadAsync(0);
        window.Records.Should().ContainSingle();
        window.Gap.Should().BeTrue("missing agent output sequences must be visible too");
        await FluentActions.Awaiting(() => fixture.Registry.ReadOutputWindowAsync(fixture.Session.SessionId, 2, 0, 10, 1024, TimeSpan.Zero, CancellationToken.None))
            .Should().ThrowAsync<TerminalGatewayActionException>();
    }

    private static async Task<ReplayFixture> OpenReplayFixtureAsync(TimeProvider? clock = null)
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(clock ?? TimeProvider.System, new ThrowingShadowFanoutSink());
        var registration = registry.Register(client, Guid.NewGuid(), 4, ["sh"]);
        var session = await registry.OpenAsync(client, "sh", "/tmp", 100, 30, CancellationToken.None);
        await registration.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = session.SessionId, Generation = session.Generation }).Should().BeTrue();
        return new(client, registry, registration, session);
    }

    private sealed class ReplayFixture(ClientKey client, AgentTerminalSessionRegistry registry, AgentTerminalGatewayRegistration registration, GatewayTerminalSession session) : IDisposable
    {
        public ClientKey Client => client;
        public AgentTerminalSessionRegistry Registry => registry;
        public AgentTerminalGatewayRegistration Registration => registration;
        public GatewayTerminalSession Session => session;
        public async Task OutputAsync(ulong sequence, string content) =>
            (await registry.TryReceiveOutputAsync(client, new TerminalOutput
            {
                SessionId = session.SessionId, Generation = session.Generation, SessionSequence = sequence,
                Content = Google.Protobuf.ByteString.CopyFromUtf8(content)
            }, CancellationToken.None)).Should().BeTrue();
        public Task<GatewayTerminalOutputWindow> ReadAsync(ulong after, int records = 10, int bytes = 1024) =>
            registry.ReadOutputWindowAsync(session.SessionId, session.Generation, after, records, bytes, TimeSpan.FromSeconds(15), CancellationToken.None);
        public void Close() => registry.TryReceiveClosed(client, new TerminalSessionClosed { SessionId = session.SessionId, Generation = session.Generation, Reason = "test complete" });
        public void Dispose() { Close(); registration.Dispose(); }
    }

    [Theory]
    [InlineData(4UL)]
    [InlineData(5UL)]
    public async Task Register_RejectsStaleOrAmbiguousCandidateWithoutMutatingCurrentTransport(ulong candidateEpoch)
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var current = registry.Register(client, Guid.NewGuid(), 5, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await current.Reader.ReadAsync();
        registry.TryReceiveOpened(client, current.RegistrationId,
            new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        var register = () => registry.Register(client, Guid.NewGuid(), candidateEpoch, ["sh"]);
        register.Should().Throw<AgentGatewayRegistrationFencedException>();

        current.IsCurrent.Should().BeTrue();
        current.CompletionToken.IsCancellationRequested.Should().BeFalse();
        registry.GetAvailability(client)!.RegistrationId.Should().Be(current.RegistrationId);
        registry.Get(opening.SessionId)!.State.Should().Be("opened");
        current.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ProvisionalRegistration_StaysUnavailableUntilExactActivation_AndCannotActivateAfterReplacement()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.RegisterProvisional(client, connection, 5, ["bash"]);
        registry.GetAvailability(client).Should().BeNull();
        var open = () => registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await open.Should().ThrowAsync<AgentTerminalSessionUnavailableException>();
        using var second = registry.RegisterProvisional(client, connection, 5, ["bash"]);
        first.CompletionToken.IsCancellationRequested.Should().BeTrue();
        first.TryActivate().Should().BeFalse();
        first.Dispose();
        second.IsCurrent.Should().BeTrue();
        second.TryActivate().Should().BeTrue();
        registry.GetAvailability(client)!.RegistrationId.Should().Be(second.RegistrationId);
    }

    [Fact]
    public async Task OpenAsync_RequiresAnAdmittedTransport_ThenRoutesTheLifecycleToThatTransport()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());

        var unavailable = () => registry.OpenAsync(client, "bash", null, 120, 32, CancellationToken.None);
        var error = await unavailable.Should().ThrowAsync<AgentTerminalSessionUnavailableException>();
        error.Which.Code.Should().Be("terminal_transport_unavailable");

        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash", "sh"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 120, 32, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();
        start.Start.SessionId.Should().Be(opening.SessionId);
        start.Start.ShellType.Should().Be("bash");

        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        var active = registry.Get(opening.SessionId);
        active.Should().NotBeNull();
        active!.State.Should().Be("opened");

        await registry.SendInputAsync(opening.SessionId, opening.Generation, "echo ok"u8.ToArray(), CancellationToken.None);
        (await registration.Reader.ReadAsync()).Input.Content.ToStringUtf8().Should().Be("echo ok");
        var resizeTask = registry.ResizeAsync(opening.SessionId, opening.Generation, 140, 40, CancellationToken.None);
        var resize = (await registration.Reader.ReadAsync()).Resize;
        resize.Columns.Should().Be(140);
        registry.TryReceiveResizeApplied(client, new TerminalResizeApplied
        {
            SessionId = opening.SessionId,
            Generation = opening.Generation,
            SessionSequence = resize.SessionSequence,
            Result = "applied",
            RequestedColumns = 140,
            RequestedRows = 40,
            AppliedColumns = 138,
            AppliedRows = 39
        }).Should().BeTrue();
        registry.Get(opening.SessionId)!.Columns.Should().Be(138);
        registry.Get(opening.SessionId)!.Rows.Should().Be(39);
        await resizeTask;

        await registry.CloseAsync(opening.SessionId, opening.Generation, "test", CancellationToken.None);
        (await registration.Reader.ReadAsync()).Close.Reason.Should().Be("test");
        await registry.CloseAsync(opening.SessionId, opening.Generation, "retry", CancellationToken.None);
        (await registration.Reader.ReadAsync()).Close.Reason.Should().Be("test", "an idempotent retry must preserve the exact original close request");
        registry.TryReceiveClosed(client, new TerminalSessionClosed { SessionId = opening.SessionId, Generation = opening.Generation, Reason = "test" }).Should().BeTrue();
        registry.TryReceiveClosed(client, new TerminalSessionClosed { SessionId = opening.SessionId, Generation = opening.Generation, Reason = "duplicate" }).Should().BeTrue("duplicate close acknowledgements are idempotent tombstone events");
        registry.Get(opening.SessionId)!.State.Should().Be("closed");
    }

    [Fact]
    public async Task OpenAsync_RejectsShellsOutsideTheVerifiedTransportInventory()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);

        var action = () => registry.OpenAsync(client, "powershell", null, 120, 32, CancellationToken.None);

        var error = await action.Should().ThrowAsync<TerminalGatewayActionException>();
        error.Which.Code.Should().Be("terminal_shell_unavailable");
    }

    [Fact]
    public async Task InputBeforeOpened_IsRejectedAsOpening()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 120, 32, CancellationToken.None);
        await registration.Reader.ReadAsync();

        var action = () => registry.SendInputAsync(opening.SessionId, opening.Generation, "ls"u8.ToArray(), CancellationToken.None);

        var error = await action.Should().ThrowAsync<TerminalGatewayActionException>();
        error.Which.Code.Should().Be("terminal_opening");
    }

    [Fact]
    public async Task OutputPressure_DropsBoundedStdoutWithoutBlockingLifecycleFrames()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await registration.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        _ = registry.Subscribe(opening.SessionId, opening.Generation);

        for (ulong sequence = 1; sequence <= 64; sequence++)
        {
            (await registry.TryReceiveOutputAsync(client, new TerminalOutput
            {
                SessionId = opening.SessionId,
                Generation = opening.Generation,
                SessionSequence = sequence,
                Content = Google.Protobuf.ByteString.CopyFromUtf8(sequence.ToString())
            }, CancellationToken.None)).Should().BeTrue();
        }

        var overflow = registry.TryReceiveOutputAsync(client, new TerminalOutput
        {
            SessionId = opening.SessionId,
            Generation = opening.Generation,
            SessionSequence = 65,
            Content = Google.Protobuf.ByteString.CopyFromUtf8("65")
        }, CancellationToken.None);
        (await overflow).Should().BeTrue();
        registry.Get(opening.SessionId)!.DroppedOutputFrames.Should().BeGreaterThan(0);

        registry.TryReceiveClosed(client, new TerminalSessionClosed
        {
            SessionId = opening.SessionId,
            Generation = opening.Generation,
            Reason = "closed-after-output-pressure"
        }).Should().BeTrue("a full browser-output queue cannot hold the agent lifecycle control plane");
        registry.Get(opening.SessionId)!.State.Should().Be("closed");
    }

    [Fact]
    public async Task DetachedOutputSubscriber_DoesNotPreventANewBrowserAttachmentFromReceivingOutput()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await registration.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        using var detachedBrowser = registry.Subscribe(opening.SessionId, opening.Generation);

        for (ulong sequence = 1; sequence <= 64; sequence++)
        {
            (await registry.TryReceiveOutputAsync(client, new TerminalOutput
            {
                SessionId = opening.SessionId,
                Generation = opening.Generation,
                SessionSequence = sequence,
                Content = Google.Protobuf.ByteString.CopyFromUtf8(sequence.ToString())
            }, CancellationToken.None)).Should().BeTrue();
        }

        using var reattachedBrowser = registry.Subscribe(opening.SessionId, opening.Generation);
        (await registry.TryReceiveOutputAsync(client, new TerminalOutput
        {
            SessionId = opening.SessionId,
            Generation = opening.Generation,
            SessionSequence = 65,
            Content = Google.Protobuf.ByteString.CopyFromUtf8("after-reattach")
        }, CancellationToken.None)).Should().BeTrue();

        Encoding.UTF8.GetString((await reattachedBrowser.Reader.ReadAsync()).Span).Should().Be("after-reattach");
        registry.Get(opening.SessionId)!.DroppedOutputFrames.Should().BeGreaterThan(0, "the detached browser is independently bounded");
    }

    [Fact]
    public async Task SamePresenceFence_ReconnectsWithoutFailingAnOpenSession()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await first.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        first.Dispose();

        registry.Get(opening.SessionId)!.State.Should().Be("suspended");
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("opened");
    }

    [Fact]
    public async Task SamePresenceFence_WithoutAReannouncementExpiresTheSuspendedSessionAndQueuesCleanup()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var clock = new ManualTimeProvider();
        var registry = new AgentTerminalSessionRegistry(clock, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await first.Reader.ReadAsync();
        first.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        first.Dispose();
        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        registry.Get(opening.SessionId)!.State.Should().Be("suspended");
        clock.TimerCount.Should().BeGreaterThan(0, "suspending the session installs its reconnect deadline before returning");

        clock.Advance(TimeSpan.FromSeconds(45));

        // The cleanup frame signals that the timer continuation completed its
        // state transition, independently of how many thread-pool turns it took.
        var close = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        registry.Get(opening.SessionId)!.State.Should().Be("closing");
        close.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
        close.Close.SessionId.Should().Be(opening.SessionId);
        close.Close.Generation.Should().Be(opening.Generation);
        close.Close.Reason.Should().Be("terminal_reconnect_timeout");
    }

    [Fact]
    public async Task SamePresenceFence_CapabilityDowngradeFailsClosingSessionWithoutReplayingItsClose()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await first.Reader.ReadAsync();
        first.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        await registry.CloseAsync(opening.SessionId, opening.Generation, "operator-cancelled", CancellationToken.None);
        var close = await first.Reader.ReadAsync();
        close.Close.Reason.Should().Be("operator-cancelled");
        first.MarkWritten(close);
        first.Dispose();

        using var downgraded = registry.Register(client, connectionId, 4, ["bash"]);

        registry.Get(opening.SessionId)!.State.Should().Be("failed");
        registry.Get(opening.SessionId)!.FailureCode.Should().Be("terminal_close_retry_unsupported");
        downgraded.Reader.TryRead(out _).Should().BeFalse("a downgraded client cannot safely receive a duplicate close decision");
    }

    [Fact]
    public async Task SamePresenceFence_ReplaysTheExactOpeningStartAfterTransportLoss()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", "/srv/netratel", 100, 30, CancellationToken.None);
        var firstStart = await first.Reader.ReadAsync();
        firstStart.Start.SessionId.Should().Be(opening.SessionId);
        first.Dispose();

        registry.Get(opening.SessionId)!.State.Should().Be("suspended");
        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var replay = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        replay.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
        replay.Start.SessionId.Should().Be(opening.SessionId);
        replay.Start.Generation.Should().Be(opening.Generation);
        replay.Start.ShellType.Should().Be("bash");
        replay.Start.WorkingDirectory.Should().Be("/srv/netratel");
        replay.Start.Columns.Should().Be(100);
        replay.Start.Rows.Should().Be(30);

        replacement.MarkWritten(replay);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("opened");
    }

    [Fact]
    public async Task InputAfterTransportLoss_ReturnsReconnectingInsteadOfChannelClosed()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();
        registration.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        registration.Dispose();

        var action = () => registry.SendInputAsync(opening.SessionId, opening.Generation, "pwd"u8.ToArray(), CancellationToken.None);

        var error = await action.Should().ThrowAsync<TerminalGatewayActionException>();
        error.Which.Code.Should().Be("terminal_transport_reconnecting");
        registry.Get(opening.SessionId)!.State.Should().Be("suspended");
    }

    [Fact]
    public async Task InputBackpressure_DoesNotRemoveAHealthyTransport()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();
        registration.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        // The transport reserves control capacity inside its bounded writer.
        // Saturating browser input is retryable pressure, not evidence that the
        // admitted gRPC registration died.
        for (var index = 0; index < 48; index++)
        {
            await registry.SendInputAsync(opening.SessionId, opening.Generation, "x"u8.ToArray(), CancellationToken.None);
        }

        var saturated = () => registry.SendInputAsync(opening.SessionId, opening.Generation, "y"u8.ToArray(), CancellationToken.None);
        var error = await saturated.Should().ThrowAsync<TerminalGatewayActionException>();
        error.Which.Code.Should().Be("terminal_transport_backpressured");
        registry.GetAvailability(client).Should().NotBeNull();
        registry.Get(opening.SessionId)!.State.Should().Be("opened");

        var delivered = await registration.Reader.ReadAsync();
        delivered.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Input);
        registration.MarkWritten(delivered);
        await registry.SendInputAsync(opening.SessionId, opening.Generation, "z"u8.ToArray(), CancellationToken.None);
        registry.GetAvailability(client).Should().NotBeNull("input pressure must not force a reconnect storm");
    }

    [Fact]
    public async Task UnknownReannouncement_QueuesOneBoundedRejectionAfterTheControlReserveIsFull()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, connectionId, 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();
        registration.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        // Forty-eight input frames fill the browser-input allotment. Sixteen
        // control frames then consume the reserved capacity, forcing the
        // seventeenth stale PTY close into its bounded retry queue.
        for (var index = 0; index < 48; index++)
        {
            await registry.SendInputAsync(opening.SessionId, opening.Generation, "x"u8.ToArray(), CancellationToken.None);
        }

        var rejection = (IAgentTerminalSessionRejectionRegistry)registry;
        for (var index = 1; index <= 17; index++)
        {
            (await rejection.TryRejectOpenedAsync(
                client,
                connectionId,
                4,
                new TerminalSessionOpened { SessionId = $"stale-terminal-{index}", Generation = 9 },
                CancellationToken.None)).Should().BeTrue();
        }

        // A repeat of the queued reannouncement is coalesced instead of
        // consuming another bounded slot or emitting a duplicate Close.
        (await rejection.TryRejectOpenedAsync(
            client,
            connectionId,
            4,
            new TerminalSessionOpened { SessionId = "stale-terminal-17", Generation = 9 },
            CancellationToken.None)).Should().BeTrue();

        var rejectedIds = new List<string>();
        for (var index = 0; index < 65; index++)
        {
            var frame = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            registration.MarkWritten(frame);
            if (frame.PayloadCase == GatewayTerminalFrame.PayloadOneofCase.Close)
            {
                rejectedIds.Add(frame.Close.SessionId);
            }
        }

        rejectedIds.Should().Contain("stale-terminal-17");
        rejectedIds.Count(id => id == "stale-terminal-17").Should().Be(1);
        registry.GetAvailability(client).Should().NotBeNull();
        registry.Get(opening.SessionId)!.State.Should().Be("opened");
    }

    [Fact]
    public async Task UnknownReannouncement_BackpressuresOneOverflowUntilWriterProgressWithoutReorderingFrames()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, connectionId, 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();
        registration.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        for (var index = 0; index < 48; index++)
        {
            await registry.SendInputAsync(opening.SessionId, opening.Generation, "x"u8.ToArray(), CancellationToken.None);
        }

        var rejection = (IAgentTerminalSessionRejectionRegistry)registry;
        // Sixteen Close frames occupy the control reserve and the following
        // sixty-four occupy the bounded FIFO retry queue.
        for (var index = 1; index <= 80; index++)
        {
            (await rejection.TryRejectOpenedAsync(
                client,
                connectionId,
                4,
                new TerminalSessionOpened { SessionId = $"overflow-stale-{index}", Generation = 9 },
                CancellationToken.None)).Should().BeTrue();
        }

        var overflow = rejection.TryRejectOpenedAsync(
            client,
            connectionId,
            4,
            new TerminalSessionOpened { SessionId = "overflow-stale-81", Generation = 9 },
            CancellationToken.None);
        await Task.Yield();
        overflow.IsCompleted.Should().BeFalse("the bounded queue must retain only one backpressured inbound reannouncement");

        var sequences = new List<ulong>();
        var closedIds = new List<string>();
        var first = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        sequences.Add(first.Sequence);
        registration.MarkWritten(first);
        (await overflow.WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();

        for (var index = 0; index < 128; index++)
        {
            var frame = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            sequences.Add(frame.Sequence);
            registration.MarkWritten(frame);
            if (frame.PayloadCase == GatewayTerminalFrame.PayloadOneofCase.Close)
            {
                closedIds.Add(frame.Close.SessionId);
            }
        }

        sequences.Should().BeInAscendingOrder("every producer serializes sequence allocation with channel insertion");
        closedIds.Should().Contain(Enumerable.Range(1, 81).Select(index => $"overflow-stale-{index}"));
        closedIds.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task StartWrittenAfterFastOpenedAcknowledgement_DoesNotOverwriteOpened()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await registration.Reader.ReadAsync();

        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        registration.MarkWritten(start);

        registry.Get(opening.SessionId)!.State.Should().Be("opened");
    }

    [Fact]
    public async Task OpeningTimeout_QueuesAndReplaysTheExactCompensatingClose()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var clock = new ManualTimeProvider();
        var registry = new AgentTerminalSessionRegistry(clock, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", "/srv/netratel", 100, 30, CancellationToken.None);
        var start = await first.Reader.ReadAsync();
        first.MarkWritten(start);
        await clock.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(15));

        clock.Advance(TimeSpan.FromSeconds(15));

        var compensatingClose = await first.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        compensatingClose.Close.SessionId.Should().Be(opening.SessionId);
        compensatingClose.Close.Generation.Should().Be(opening.Generation);
        registry.Get(opening.SessionId)!.State.Should().Be("closing");
        compensatingClose.Close.Reason.Should().Be("terminal_open_timeout");

        first.Dispose();
        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var replay = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        replay.Close.SessionId.Should().Be(opening.SessionId);
        replay.Close.Generation.Should().Be(opening.Generation);
        replay.Close.Reason.Should().Be("terminal_open_timeout");

        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        var lateOpenedClose = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        lateOpenedClose.Close.Reason.Should().Be("terminal_open_timeout");
        registry.TryReceiveClosed(client, new TerminalSessionClosed { SessionId = opening.SessionId, Generation = opening.Generation, Reason = "timeout-cleaned" }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("closed");
    }

    [Fact]
    public async Task TimeoutFailure_AcceptsTheDelayedCloseAcknowledgementInsteadOfPersistingAConflictingFailure()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var clock = new ManualTimeProvider();
        var registry = new AgentTerminalSessionRegistry(clock, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        using var output = registry.Subscribe(opening.SessionId, opening.Generation);
        var start = await registration.Reader.ReadAsync();
        registration.MarkWritten(start);
        await clock.WaitForTimerCreatedAsync(TimeSpan.FromSeconds(15));

        clock.Advance(TimeSpan.FromSeconds(15));

        var compensatingClose = await registration.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        registry.Get(opening.SessionId)!.State.Should().Be("closing");
        compensatingClose.Close.Reason.Should().Be("terminal_open_timeout");
        registration.MarkWritten(compensatingClose);
        await clock.WaitForTimerCreatedAsync(TimeSpan.FromMinutes(2));

        clock.Advance(TimeSpan.FromMinutes(2));

        await output.Reader.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        registry.Get(opening.SessionId)!.State.Should().Be("failed");
        registry.Get(opening.SessionId)!.FailureCode.Should().Be("terminal_open_timeout");
        registry.TryReceiveClosed(client, new TerminalSessionClosed
        {
            SessionId = opening.SessionId,
            Generation = opening.Generation,
            Reason = "late-cleanup-ack"
        }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("closed");
    }

    [Fact]
    public async Task LateDisposalOfOldRegistration_DoesNotRemoveReplacement()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"]);
        using var replacement = registry.Register(client, connectionId, 4, ["bash"]);

        first.Dispose();

        registry.GetAvailability(client).Should().NotBeNull();
        registry.GetAvailability(client)!.RegistrationId.Should().Be(replacement.RegistrationId);
    }

    [Fact]
    public async Task UnknownReannouncement_ReceivesAnExactCloseWithoutChangingOtherTerminalSessions()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, connectionId, 4, ["bash"]);
        var active = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await registration.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = active.SessionId, Generation = active.Generation }).Should().BeTrue();

        var rejected = await ((IAgentTerminalSessionRejectionRegistry)registry).TryRejectOpenedAsync(
            client,
            connectionId,
            4,
            new TerminalSessionOpened { SessionId = "stale-terminal", Generation = 9 },
            CancellationToken.None);

        rejected.Should().BeTrue();
        var close = await registration.Reader.ReadAsync();
        close.Close.SessionId.Should().Be("stale-terminal");
        close.Close.Generation.Should().Be(9);
        close.Close.Reason.Should().Be("terminal_session_rejected");
        registry.Get(active.SessionId)!.State.Should().Be("opened");

        var wrongFence = await ((IAgentTerminalSessionRejectionRegistry)registry).TryRejectOpenedAsync(
            client,
            Guid.NewGuid(),
            5,
            new TerminalSessionOpened { SessionId = "wrong-fence", Generation = 1 },
            CancellationToken.None);
        wrongFence.Should().BeFalse();
        registration.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task ClosingSession_ReplaysItsExactPendingCloseAfterReconnectAndLateOpened()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await first.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        await registry.CloseAsync(opening.SessionId, opening.Generation, "expired", CancellationToken.None);
        (await first.Reader.ReadAsync()).Close.Reason.Should().Be("expired");
        first.Dispose();

        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        var replay = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        replay.Close.Reason.Should().Be("expired");

        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("closing");
        var lateOpenedReplay = await replacement.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        lateOpenedReplay.Close.Reason.Should().Be("expired");

        await registry.CloseAsync(opening.SessionId, opening.Generation, "expired-retry", CancellationToken.None);
        (await replacement.Reader.ReadAsync()).Close.Reason.Should().Be("expired");
        registry.TryReceiveClosed(client, new TerminalSessionClosed { SessionId = opening.SessionId, Generation = opening.Generation, Reason = "expired" }).Should().BeTrue();
        registry.Get(opening.SessionId)!.State.Should().Be("closed");
    }

    [Fact]
    public async Task ClosingSession_WithoutIdempotentCloseCapability_DoesNotRepeatTheCloseFrame()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await registration.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        await registry.CloseAsync(opening.SessionId, opening.Generation, "first", CancellationToken.None);
        (await registration.Reader.ReadAsync()).Close.Reason.Should().Be("first");
        await registry.CloseAsync(opening.SessionId, opening.Generation, "retry", CancellationToken.None);

        registration.Reader.TryRead(out _).Should().BeFalse();
        registry.Get(opening.SessionId)!.State.Should().Be("failed");
        registry.Get(opening.SessionId)!.FailureCode.Should().Be("terminal_close_retry_unsupported");
    }

    [Fact]
    public async Task ReplacedPresenceFence_FencesThePriorSessionAndRejectsFurtherControl()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, Guid.NewGuid(), 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        await first.Reader.ReadAsync();
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();

        using var replacement = registry.Register(client, Guid.NewGuid(), 5, ["bash"]);

        var fenced = registry.Get(opening.SessionId)!;
        fenced.State.Should().Be("failed");
        fenced.FailureCode.Should().Be("terminal_presence_fence_replaced");
        var action = () => registry.ResizeAsync(opening.SessionId, opening.Generation, 120, 40, CancellationToken.None);
        var error = await action.Should().ThrowAsync<TerminalGatewayActionException>();
        error.Which.Code.Should().Be("terminal_session_failed");
    }

    [Fact]
    public async Task NewPresenceFence_AfterTheOldTransportDisposed_CannotReviveItsSuspendedSession()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var firstConnectionId = Guid.NewGuid();
        var registry = new AgentTerminalSessionRegistry(TimeProvider.System, new ThrowingShadowFanoutSink());
        using var first = registry.Register(client, firstConnectionId, 4, ["bash"]);
        var opening = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var start = await first.Reader.ReadAsync();
        first.MarkWritten(start);
        registry.TryReceiveOpened(client, new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeTrue();
        first.Dispose();
        registry.Get(opening.SessionId)!.State.Should().Be("suspended");

        var replacementConnectionId = Guid.NewGuid();
        using var replacement = registry.Register(client, replacementConnectionId, 5, ["bash"]);
        var bound = (IAgentTerminalRegistrationBoundRegistry)registry;
        bound.TryReceiveOpened(
            client,
            replacement.RegistrationId,
            new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }).Should().BeFalse();
        registry.Get(opening.SessionId)!.State.Should().Be("failed");
        registry.Get(opening.SessionId)!.FailureCode.Should().Be("terminal_presence_fence_replaced");

        (await bound.TryRejectOpenedAsync(
            client,
            replacement.RegistrationId,
            replacementConnectionId,
            5,
            new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation },
            CancellationToken.None)).Should().BeTrue();
        var staleClose = await replacement.Reader.ReadAsync();
        staleClose.Close.SessionId.Should().Be(opening.SessionId);
        staleClose.Close.Reason.Should().Be("terminal_session_rejected");
        replacement.MarkWritten(staleClose);

        var fresh = await registry.OpenAsync(client, "bash", null, 100, 30, CancellationToken.None);
        var freshStart = await replacement.Reader.ReadAsync();
        freshStart.Start.SessionId.Should().Be(fresh.SessionId);
    }

    [Fact]
    public async Task Recovery_RehydratesOnlyTheDurableLeaseThatTheAgentReannounces()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var sessionId = Guid.NewGuid().ToString("N");
        await using var services = BuildRecoveryServices();
        await SeedRecoveryLeaseAsync(services, client, sessionId, expired: false);
        var registry = new AgentTerminalSessionRegistry(
            TimeProvider.System,
            new ThrowingShadowFanoutSink(),
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);

        var accepted = await ((IAgentTerminalSessionRecoveryRegistry)registry).TryRecoverOpenedAsync(
            client,
            new TerminalSessionOpened { SessionId = sessionId, Generation = 1 },
            CancellationToken.None);

        accepted.Should().BeTrue();
        var recovered = registry.Get(sessionId);
        recovered.Should().NotBeNull();
        recovered!.State.Should().Be("opened");
        recovered.ShellType.Should().Be("bash");
        registration.Reader.TryRead(out _).Should().BeFalse("a recovered open must not dispatch a second PTY start");
    }

    [Fact]
    public async Task Recovery_ExpiresTheLeaseBeforeRehydrationAndQueuesAnIdempotentClose()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var sessionId = Guid.NewGuid().ToString("N");
        await using var services = BuildRecoveryServices();
        await SeedRecoveryLeaseAsync(services, client, sessionId, expired: true);
        var registry = new AgentTerminalSessionRegistry(
            TimeProvider.System,
            new ThrowingShadowFanoutSink(),
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);

        var accepted = await ((IAgentTerminalSessionRecoveryRegistry)registry).TryRecoverOpenedAsync(
            client,
            new TerminalSessionOpened { SessionId = sessionId, Generation = 1 },
            CancellationToken.None);

        accepted.Should().BeTrue();
        registry.Get(sessionId)!.State.Should().Be("closing");
        var close = await registration.Reader.ReadAsync();
        close.Close.SessionId.Should().Be(sessionId);
        close.Close.Reason.Should().Be("terminal_policy_lease_expired");
        await using var verifyScope = services.CreateAsyncScope();
        var persisted = await verifyScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .McpOperatorTerminalSessions.SingleAsync(record => record.SessionId == sessionId);
        persisted.State.Should().Be(McpOperatorTerminalSessionState.Closing);
        persisted.CloseReason.Should().Be("terminal_policy_lease_expired");
    }

    [Fact]
    public async Task BoundRecovery_DoesNotLetAnOldRegistrationAdoptOnItsReplacement()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var connectionId = Guid.NewGuid();
        var sessionId = Guid.NewGuid().ToString("N");
        var recovery = new BlockingRecovery(new McpOperatorTerminalRecovery(
            sessionId,
            client.TenantId,
            client.AgentId,
            1,
            "bash",
            100,
            30,
            DateTimeOffset.UtcNow,
            CloseRequested: false,
            CloseReason: null));
        await using var services = new ServiceCollection()
            .AddSingleton(recovery)
            .AddScoped<IMcpOperatorTerminalSessionRecovery>(provider => provider.GetRequiredService<BlockingRecovery>())
            .BuildServiceProvider();
        var registry = new AgentTerminalSessionRegistry(
            TimeProvider.System,
            new ThrowingShadowFanoutSink(),
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        var bound = (IAgentTerminalRegistrationBoundRegistry)registry;
        using var first = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);

        var staleRecovery = bound.TryRecoverOpenedAsync(
            client,
            first.RegistrationId,
            new TerminalSessionOpened { SessionId = sessionId, Generation = 1 },
            CancellationToken.None);
        await recovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        using var replacement = registry.Register(client, connectionId, 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);
        recovery.Continue.TrySetResult(true);

        (await staleRecovery).Should().BeFalse("an old stream must not adopt through its same-fence replacement");
        registry.Get(sessionId).Should().BeNull();

        (await bound.TryRecoverOpenedAsync(
            client,
            replacement.RegistrationId,
            new TerminalSessionOpened { SessionId = sessionId, Generation = 1 },
            CancellationToken.None)).Should().BeTrue();
        registry.Get(sessionId)!.State.Should().Be("opened");
    }

    [Fact]
    public async Task BoundRecovery_DoesNotAdoptWhenItsPostLookupAdmissionFenceHasChanged()
    {
        var client = new ClientKey(3, Guid.NewGuid());
        var sessionId = Guid.NewGuid().ToString("N");
        var recovery = new BlockingRecovery(new McpOperatorTerminalRecovery(
            sessionId,
            client.TenantId,
            client.AgentId,
            1,
            "bash",
            100,
            30,
            DateTimeOffset.UtcNow,
            CloseRequested: false,
            CloseReason: null));
        await using var services = new ServiceCollection()
            .AddSingleton(recovery)
            .AddScoped<IMcpOperatorTerminalSessionRecovery>(provider => provider.GetRequiredService<BlockingRecovery>())
            .BuildServiceProvider();
        var registry = new AgentTerminalSessionRegistry(
            TimeProvider.System,
            new ThrowingShadowFanoutSink(),
            scopeFactory: services.GetRequiredService<IServiceScopeFactory>());
        var bound = (IAgentTerminalRegistrationBoundRecoveryAdmissionRegistry)registry;
        using var registration = registry.Register(client, Guid.NewGuid(), 4, ["bash"], [AgentTerminalSessionRegistry.IdempotentCloseCapability]);

        var recoveryTask = bound.TryRecoverOpenedAsync(
            client,
            registration.RegistrationId,
            new TerminalSessionOpened { SessionId = sessionId, Generation = 1 },
            static _ => Task.FromException(new InvalidOperationException("presence-fence-replaced")),
            CancellationToken.None);
        await recovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        recovery.Continue.TrySetResult(true);

        var recover = async () => await recoveryTask;
        await recover.Should().ThrowAsync<InvalidOperationException>();
        registry.Get(sessionId).Should().BeNull("the post-lookup admission guard runs before the recovered lease can be installed");
    }

    private static ServiceProvider BuildRecoveryServices()
    {
        var databaseName = $"terminal-recovery-{Guid.NewGuid():N}";
        return new ServiceCollection()
            .AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName))
            .AddScoped<IMcpOperatorTerminalSessionRecovery, McpOperatorTerminalSessionRecoveryService>()
            .BuildServiceProvider();
    }

    private static async Task SeedRecoveryLeaseAsync(ServiceProvider services, ClientKey client, string sessionId, bool expired)
    {
        var now = DateTimeOffset.UtcNow;
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.McpOperatorTerminalSessions.Add(new McpOperatorTerminalSessionRecord
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            TenantId = client.TenantId,
            AgentId = client.AgentId,
            Generation = 1,
            Subject = "operator@example.test",
            ClientId = "operator-client",
            McpResource = "https://mcp.dev.example/mcp",
            McpInstance = "prod",
            PolicyId = Guid.NewGuid(),
            PolicyVersion = 1,
            AcceptedAuditId = Guid.NewGuid(),
            ShellType = "bash",
            Columns = 120,
            Rows = 32,
            EffectiveConstraintsJson = "{}",
            State = McpOperatorTerminalSessionState.Opened,
            CreatedAtUtc = now.AddMinutes(-1),
            LastActivityAtUtc = now.AddMinutes(-1),
            IdleExpiresAtUtc = expired ? now.AddSeconds(-1) : now.AddMinutes(5),
            ExpiresAtUtc = expired ? now.AddSeconds(-1) : now.AddMinutes(10),
            Version = 1
        });
        await db.SaveChangesAsync();
    }

    private sealed class BlockingRecovery(McpOperatorTerminalRecovery recovered) : IMcpOperatorTerminalSessionRecovery
    {
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<McpOperatorTerminalRecovery?> TryRecoverAsync(
            int tenantId,
            Guid agentId,
            string sessionId,
            ulong generation,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult(true);
            await Continue.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            return recovered.TenantId == tenantId && recovered.AgentId == agentId &&
                   recovered.SessionId == sessionId && recovered.Generation == generation
                ? recovered
                : null;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly object _sync = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly Dictionary<TimeSpan, TaskCompletionSource> _createdTimers = [];
        private DateTimeOffset _utcNow = DateTimeOffset.Parse("2026-09-04T10:00:00Z");

        public int TimerCount
        {
            get
            {
                lock (_sync)
                {
                    return _timers.Count;
                }
            }
        }

        public Task WaitForTimerCreatedAsync(TimeSpan dueTime)
        {
            lock (_sync)
            {
                return TimerCreated(dueTime).Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        private TaskCompletionSource TimerCreated(TimeSpan dueTime)
        {
            if (!_createdTimers.TryGetValue(dueTime, out var created))
            {
                created = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _createdTimers.Add(dueTime, created);
            }

            return created;
        }

        public override DateTimeOffset GetUtcNow()
        {
            lock (_sync)
            {
                return _utcNow;
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, dueTime, period);
            lock (_sync)
            {
                _timers.Add(timer);
                TimerCreated(dueTime).TrySetResult();
            }

            return timer;
        }

        public void Advance(TimeSpan duration)
        {
            List<(TimerCallback Callback, object? State)> callbacks = [];
            lock (_sync)
            {
                _utcNow += duration;
                foreach (var timer in _timers.ToArray())
                {
                    if (timer.TryFire(_utcNow, out var callback))
                    {
                        callbacks.Add(callback);
                    }
                }
            }

            foreach (var (callback, state) in callbacks)
            {
                callback(state);
            }
        }

        private void Remove(ManualTimer timer)
        {
            lock (_sync)
            {
                _timers.Remove(timer);
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private DateTimeOffset _next = owner.GetUtcNow() + dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed)
                {
                    return false;
                }

                _period = period;
                _next = owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                owner.Remove(this);
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public bool TryFire(DateTimeOffset now, out (TimerCallback Callback, object? State) callbackAndState)
            {
                if (_disposed || now < _next)
                {
                    callbackAndState = default;
                    return false;
                }

                callbackAndState = (callback, state);
                if (_period == Timeout.InfiniteTimeSpan)
                {
                    _disposed = true;
                }
                else
                {
                    _next = now + _period;
                }

                return true;
            }
        }
    }
}
