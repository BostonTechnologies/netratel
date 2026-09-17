using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime.Shadow;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Terminal;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTerminalGatewayServiceTests
{
    [Theory]
    [InlineData(8UL)]
    [InlineData(1UL)]
    public Task Client_ReopensTerminalAfterServerRestartResetsPresenceEpoch(ulong previousEpoch) =>
        ExerciseClientAcrossServerRestartAsync(previousEpoch, failFirstChild: false);

    [Fact]
    public Task Client_ReopensTerminalWhenPreviousChildFailedBeforeItsPresenceOwnerWasCancelled() =>
        ExerciseClientAcrossServerRestartAsync(8, failFirstChild: true);

    private static async Task ExerciseClientAcrossServerRestartAsync(ulong previousEpoch, bool failFirstChild)
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var key = new ClientKey(tenantId, agentId);
        var previousPresence = new GatewayPresenceSession(tenantId, agentId, previousEpoch, Guid.NewGuid());
        var replacementPresence = new GatewayPresenceSession(tenantId, agentId, 1, Guid.NewGuid());
        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var previousRegistry = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var previousStore = new RecordingTerminalSessionStore();
        using var previousHost = await BuildHostAsync(tenantId, agentId,
            new CurrentPresenceRouter(key, previousPresence.ConnectionId, previousEpoch), previousRegistry, previousStore);
        IHost currentHost = previousHost;
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failChannelCreation = failFirstChild;
        using var client = new NetRatel.Client.Service.Gateway.AgentTerminalGatewayClient(
            new GatewayClientOptions
            {
                Endpoint = "https://gateway.test",
                TerminalGatewayEnabled = true,
                TerminalAuthorityEnabled = true
            },
            new TerminalHostOptions { BackendPreference = TerminalBackendPreference.Redirected },
            [shell],
            message =>
            {
                if (message.StartsWith("Terminal gateway admitted.", StringComparison.Ordinal))
                    admitted.TrySetResult();
            },
            createChannel: _ =>
            {
                if (failChannelCreation)
                {
                    failChannelCreation = false;
                    throw new InvalidOperationException("Test channel creation failed before parent cancellation.");
                }

                return GrpcChannel.ForAddress("http://localhost",
                    new GrpcChannelOptions { HttpHandler = currentHost.GetTestServer().CreateHandler() });
            });

        using (var previousOwner = new CancellationTokenSource())
        {
            var previousRun = client.RunForPresenceSessionAsync(previousPresence, "test-token", previousOwner.Token);
            try
            {
                if (failFirstChild)
                {
                    await FluentActions.Awaiting(() => previousRun.WaitAsync(deadline.Token))
                        .Should().ThrowAsync<InvalidOperationException>()
                        .WithMessage("Test channel creation failed before parent cancellation.");
                    previousOwner.IsCancellationRequested.Should().BeFalse();
                    previousRegistry.GetAvailability(key).Should().BeNull();
                }
                else
                {
                    await admitted.Task.WaitAsync(deadline.Token);
                    previousRegistry.GetAvailability(key)!.ConnectionEpoch.Should().Be(previousEpoch);
                    await ExerciseShellLifecycleAsync(previousRegistry, previousStore, key, shell, deadline.Token);
                }
            }
            finally
            {
                previousOwner.Cancel();
                if (!failFirstChild)
                    await previousRun.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        await previousHost.StopAsync(deadline.Token);
        var replacementRegistry = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var replacementStore = new RecordingTerminalSessionStore();
        using var replacementHost = await BuildHostAsync(tenantId, agentId,
            new CurrentPresenceRouter(key, replacementPresence.ConnectionId, 1), replacementRegistry, replacementStore);
        currentHost = replacementHost;
        admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var replacementOwner = new CancellationTokenSource();
        var replacementRun = client.RunForPresenceSessionAsync(replacementPresence, "test-token", replacementOwner.Token);
        try
        {
            await admitted.Task.WaitAsync(deadline.Token);
            var availability = replacementRegistry.GetAvailability(key);
            availability.Should().NotBeNull();
            availability!.ConnectionEpoch.Should().Be(1);
            availability.ConnectionId.Should().Be(replacementPresence.ConnectionId);
            await ExerciseShellLifecycleAsync(replacementRegistry, replacementStore, key, shell, deadline.Token);
        }
        finally
        {
            replacementOwner.Cancel();
            await replacementRun.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task ExerciseShellLifecycleAsync(
        AgentTerminalSessionRegistry terminals,
        RecordingTerminalSessionStore store,
        ClientKey client,
        string shell,
        CancellationToken cancellationToken)
    {
        var session = await terminals.OpenAsync(client, shell, null, 100, 30, cancellationToken);
        await store.WaitForOpenedAsync(session.SessionId, cancellationToken);
        terminals.Get(session.SessionId)!.State.Should().Be("opened");
        using var output = terminals.Subscribe(session.SessionId, session.Generation);
        var suffix = Guid.NewGuid().ToString("N");
        var marker = "netratel-terminal-" + suffix;
        // Split the Unix marker in the command so echoed input cannot satisfy the output assertion.
        var command = shell == "cmd"
            ? $"echo {marker}\r\n"
            : $"printf '%s%s\\n' 'netratel-terminal-' '{suffix}'\n";
        await terminals.SendInputAsync(session.SessionId, session.Generation, Encoding.UTF8.GetBytes(command), cancellationToken);
        var received = new StringBuilder();
        while (!received.ToString().Contains(marker, StringComparison.Ordinal))
        {
            var chunk = await output.Reader.ReadAsync(cancellationToken);
            received.Append(Encoding.UTF8.GetString(chunk.Span));
        }

        await terminals.CloseAsync(session.SessionId, session.Generation, "test_completed", cancellationToken);
        while (await output.Reader.WaitToReadAsync(cancellationToken))
        {
            while (output.Reader.TryRead(out var trailing))
                received.Append(Encoding.UTF8.GetString(trailing.Span));
        }
        await output.Reader.Completion.WaitAsync(cancellationToken);
        terminals.Get(session.SessionId)!.State.Should().Be("closed");
    }

    [Theory]
    [InlineData(4UL)]
    [InlineData(5UL)]
    public async Task Connect_RejectsHelloRacingAfterCurrentRegistrationWithoutChangingItsSessions(ulong candidateEpoch)
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var key = new ClientKey(tenantId, agentId);
        var oldConnection = Guid.NewGuid();
        var currentConnection = Guid.NewGuid();
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        AgentTerminalGatewayRegistration? current = null;
        GatewayTerminalSession? opened = null;
        var presence = new InterleavingPresenceRouter(key, oldConnection, candidateEpoch, async () =>
        {
            current = terminals.Register(key, currentConnection, 5, ["bash"]);
            opened = await terminals.OpenAsync(key, "bash", null, 100, 30, CancellationToken.None);
            await current.Reader.ReadAsync();
            terminals.TryReceiveOpened(key, current.RegistrationId,
                new TerminalSessionOpened { SessionId = opened.SessionId, Generation = opened.Generation }).Should().BeTrue();
        });
        try
        {
            using var host = await BuildHostAsync(tenantId, agentId, presence, terminals);
            using var channel = GrpcChannel.ForAddress("http://localhost",
                new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
            using var call = new AgentTerminalGateway.AgentTerminalGatewayClient(channel).Connect();
            await call.RequestStream.WriteAsync(Hello(tenantId, agentId, oldConnection, candidateEpoch));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var receive = () => call.ResponseStream.MoveNext(deadline.Token);
            var failure = await receive.Should().ThrowAsync<RpcException>();
            failure.Which.StatusCode.Should().Be(StatusCode.Aborted);
            failure.Which.Status.Detail.Should().NotContain(agentId.ToString()).And.NotContain(oldConnection.ToString());
            current!.IsCurrent.Should().BeTrue();
            current.CompletionToken.IsCancellationRequested.Should().BeFalse();
            terminals.GetAvailability(key)!.RegistrationId.Should().Be(current.RegistrationId);
            terminals.Get(opened!.SessionId)!.State.Should().Be("opened");
            current.Reader.TryRead(out _).Should().BeFalse("rejection cannot enqueue Close or replay Start");
            await terminals.SendInputAsync(opened.SessionId, opened.Generation, "still-current"u8.ToArray(), deadline.Token);
            (await current.Reader.ReadAsync(deadline.Token)).Input.Content.ToStringUtf8().Should().Be("still-current");
        }
        finally
        {
            current?.Dispose();
        }
    }

    [Fact]
    public async Task Connect_RevalidatesPresenceBeforeMakingTheProvisionalTransportAvailable()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var key = new ClientKey(tenantId, agentId);
        var connectionId = Guid.NewGuid();
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var presence = new InterleavingPresenceRouter(key, connectionId, 4, () => Task.CompletedTask,
            () => terminals.GetAvailability(key).Should().BeNull(), advanceAfterFirstRead: true);
        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentTerminalGateway.AgentTerminalGatewayClient(channel).Connect();
        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, 4));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var receive = () => call.ResponseStream.MoveNext(deadline.Token);
        (await receive.Should().ThrowAsync<RpcException>()).Which.StatusCode.Should().Be(StatusCode.Aborted);
        presence.Reads.Should().Be(2);
        terminals.GetAvailability(key).Should().BeNull();
    }

    [Fact]
    public async Task Connect_PersistsOnlyOneOpenedTransitionForSequentialDuplicateOpenedFrames()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new CurrentPresenceRouter(new ClientKey(tenantId, agentId), connectionId, connectionEpoch);
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var durableSessions = new RecordingTerminalSessionStore();

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals, durableSessions);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await ReadNextAsync(call.ResponseStream)).PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);

        var opening = await terminals.OpenAsync(new ClientKey(tenantId, agentId), "bash", null, 100, 30, CancellationToken.None);
        var start = await ReadNextAsync(call.ResponseStream);
        start.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
        start.Start.SessionId.Should().Be(opening.SessionId);

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }
            }));
        using (var openedTransitionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await durableSessions.WaitForOpenedAsync(opening.SessionId, openedTransitionTimeout.Token);
        }

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 2,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }
            }));
        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();

        durableSessions.MarkedTransitions.Should().ContainSingle(transition =>
            transition.SessionId == opening.SessionId &&
            transition.State == McpOperatorTerminalSessionState.Opened);
    }

    [Fact]
    public async Task Connect_DoesNotPersistFailedAfterTheSameSessionHasClosed()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new CurrentPresenceRouter(new ClientKey(tenantId, agentId), connectionId, connectionEpoch);
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var durableSessions = new RecordingTerminalSessionStore();

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals, durableSessions);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await ReadNextAsync(call.ResponseStream)).PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);

        var opening = await terminals.OpenAsync(new ClientKey(tenantId, agentId), "bash", null, 100, 30, CancellationToken.None);
        var start = await ReadNextAsync(call.ResponseStream);
        start.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
        start.Start.SessionId.Should().Be(opening.SessionId);

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }
            }));
        using (var openedTransitionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        {
            await durableSessions.WaitForOpenedAsync(opening.SessionId, openedTransitionTimeout.Token);
        }

        await terminals.CloseAsync(opening.SessionId, opening.Generation, "operator-cancelled", CancellationToken.None);
        var close = await ReadNextAsync(call.ResponseStream);
        close.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
        close.Close.SessionId.Should().Be(opening.SessionId);

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 2,
            new AgentTerminalFrame
            {
                Closed = new TerminalSessionClosed
                {
                    SessionId = opening.SessionId,
                    Generation = opening.Generation,
                    Reason = "operator-cancelled"
                }
            }));
        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 3,
            new AgentTerminalFrame
            {
                Failed = new TerminalSessionFailed
                {
                    SessionId = opening.SessionId,
                    Generation = opening.Generation,
                    Code = "late-agent-failure",
                    Message = "the close acknowledgement already won"
                }
            }));
        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();

        durableSessions.MarkedTransitions.Should().ContainSingle(transition =>
            transition.SessionId == opening.SessionId &&
            transition.State == McpOperatorTerminalSessionState.Closed);
        durableSessions.MarkedTransitions.Should().NotContain(transition =>
            transition.SessionId == opening.SessionId &&
            transition.State == McpOperatorTerminalSessionState.Failed);
    }

    [Fact]
    public async Task Connect_AbortsWithoutRecoveryAdmissionOrPersistenceWhenPresenceChangesAfterDurableLookup()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var clientKey = new ClientKey(tenantId, agentId);
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new SwitchablePresenceRouter(clientKey, connectionId, connectionEpoch);
        var sessionId = Guid.NewGuid().ToString("N");
        var recovery = new BlockingTerminalRecovery(
            new McpOperatorTerminalRecovery(
                sessionId,
                tenantId,
                agentId,
                1,
                "bash",
                100,
                30,
                DateTimeOffset.UtcNow,
                false,
                null),
            () => presence.Fence(Guid.NewGuid(), connectionEpoch + 1));
        await using var recoveryServices = new ServiceCollection()
            .AddSingleton(recovery)
            .AddScoped<IMcpOperatorTerminalSessionRecovery>(provider => provider.GetRequiredService<BlockingTerminalRecovery>())
            .BuildServiceProvider();
        var terminals = new AgentTerminalSessionRegistry(
            TimeProvider.System,
            NullShadowFanoutSink.Instance,
            scopeFactory: recoveryServices.GetRequiredService<IServiceScopeFactory>());
        var durableSessions = new RecordingTerminalSessionStore();

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals, durableSessions);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await ReadNextAsync(call.ResponseStream)).PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);
        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = sessionId, Generation = 1 }
            }));

        await recovery.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        recovery.Continue.TrySetResult(true);

        var receiveUntilTerminal = async () =>
        {
            while (await call.ResponseStream.MoveNext(CancellationToken.None))
            {
            }
        };
        var aborted = await receiveUntilTerminal.Should().ThrowAsync<RpcException>();
        aborted.Which.StatusCode.Should().Be(StatusCode.Aborted);
        terminals.Get(sessionId).Should().BeNull("the post-lookup presence check runs before recovery installs a local session");
        durableSessions.MarkedTransitions.Should().BeEmpty("an admission failure must not create a durable lifecycle transition");
    }

    [Fact]
    public async Task Connect_DoesNotPersistLateOpenedForALocallyClosingSession_AndReplaysItsExactClose()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new CurrentPresenceRouter(new ClientKey(tenantId, agentId), connectionId, connectionEpoch);
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var durableSessions = new RecordingTerminalSessionStore();

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals, durableSessions);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await ReadNextAsync(call.ResponseStream)).PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);

        var opening = await terminals.OpenAsync(new ClientKey(tenantId, agentId), "bash", null, 100, 30, CancellationToken.None);
        var start = await ReadNextAsync(call.ResponseStream);
        start.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
        start.Start.SessionId.Should().Be(opening.SessionId);

        await terminals.CloseAsync(opening.SessionId, opening.Generation, "operator-cancelled", CancellationToken.None);
        var initialClose = await ReadNextAsync(call.ResponseStream);
        initialClose.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
        initialClose.Close.SessionId.Should().Be(opening.SessionId);
        initialClose.Close.Generation.Should().Be(opening.Generation);
        initialClose.Close.Reason.Should().Be("operator-cancelled");

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = opening.SessionId, Generation = opening.Generation }
            }));

        var replayedClose = await ReadNextAsync(call.ResponseStream);
        replayedClose.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
        replayedClose.Close.SessionId.Should().Be(opening.SessionId);
        replayedClose.Close.Generation.Should().Be(opening.Generation);
        replayedClose.Close.Reason.Should().Be("operator-cancelled");

        terminals.Get(opening.SessionId)!.State.Should().Be("closing");
        durableSessions.MarkedTransitions.Should().NotContain(transition =>
            transition.State == McpOperatorTerminalSessionState.Opened);

        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task Connect_ReconcilesAnUnknownOpenedFrame_AndKeepsTheTransportUsable()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new CurrentPresenceRouter(new ClientKey(tenantId, agentId), connectionId, connectionEpoch);
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 1,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = "stale-terminal", Generation = 9 }
            }));

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var rejected = call.ResponseStream.Current;
        rejected.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
        rejected.Close.SessionId.Should().Be("stale-terminal");
        rejected.Close.Generation.Should().Be(9);
        rejected.Close.Reason.Should().Be("terminal_session_rejected");

        var opened = await terminals.OpenAsync(new ClientKey(tenantId, agentId), "bash", null, 100, 30, CancellationToken.None);
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var start = call.ResponseStream.Current;
        start.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
        start.Start.SessionId.Should().Be(opened.SessionId);

        await call.RequestStream.WriteAsync(Frame(
            tenantId,
            agentId,
            connectionId,
            connectionEpoch,
            sequence: 2,
            new AgentTerminalFrame
            {
                Opened = new TerminalSessionOpened { SessionId = opened.SessionId, Generation = opened.Generation }
            }));
        await call.RequestStream.CompleteAsync();

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();
        terminals.Get(opened.SessionId)!.State.Should().Be("suspended", "stream completion releases the transport only after the valid second frame was accepted");
    }

    [Fact]
    public async Task Connect_ReconcilesRepeatedUnknownOpenedFrames_AndRemainsUsableAcrossTwentyFiveLifecycles()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const ulong connectionEpoch = 4;
        var presence = new CurrentPresenceRouter(new ClientKey(tenantId, agentId), connectionId, connectionEpoch);
        var terminals = new AgentTerminalSessionRegistry(TimeProvider.System, NullShadowFanoutSink.Instance);
        var durableSessions = new RecordingTerminalSessionStore();

        using var host = await BuildHostAsync(tenantId, agentId, presence, terminals, durableSessions);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentTerminalGateway.AgentTerminalGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(Hello(tenantId, agentId, connectionId, connectionEpoch));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Accepted);

        ulong sequence = 1;
        string? previousSessionId = null;
        for (var cycle = 1; cycle <= 25; cycle++)
        {
            var staleSessionId = $"stale-terminal-{cycle}";
            await call.RequestStream.WriteAsync(Frame(
                tenantId,
                agentId,
                connectionId,
                connectionEpoch,
                sequence++,
                new AgentTerminalFrame
                {
                    Opened = new TerminalSessionOpened { SessionId = staleSessionId, Generation = 9 }
                }));

            (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
            var rejected = call.ResponseStream.Current;
            rejected.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
            rejected.Close.SessionId.Should().Be(staleSessionId);
            rejected.Close.Reason.Should().Be("terminal_session_rejected");

            if (previousSessionId is not null)
                terminals.Get(previousSessionId)!.State.Should().Be("closed", "the ordered next frame is only rejected after the preceding close frame was processed");

            var opened = await terminals.OpenAsync(new ClientKey(tenantId, agentId), "bash", null, 100, 30, CancellationToken.None);
            (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
            call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Start);
            call.ResponseStream.Current.Start.SessionId.Should().Be(opened.SessionId);

            await call.RequestStream.WriteAsync(Frame(
                tenantId,
                agentId,
                connectionId,
                connectionEpoch,
                sequence++,
                new AgentTerminalFrame
                {
                    Opened = new TerminalSessionOpened { SessionId = opened.SessionId, Generation = opened.Generation }
                }));

            using (var openedTransitionTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                var persistedOpened = await durableSessions.WaitForOpenedAsync(opened.SessionId, openedTransitionTimeout.Token);
                persistedOpened.State.Should().Be(McpOperatorTerminalSessionState.Opened);
            }

            await terminals.CloseAsync(opened.SessionId, opened.Generation, "cycle-complete", CancellationToken.None);
            (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
            call.ResponseStream.Current.PayloadCase.Should().Be(GatewayTerminalFrame.PayloadOneofCase.Close);
            call.ResponseStream.Current.Close.SessionId.Should().Be(opened.SessionId);

            await call.RequestStream.WriteAsync(Frame(
                tenantId,
                agentId,
                connectionId,
                connectionEpoch,
                sequence++,
                new AgentTerminalFrame
                {
                    Closed = new TerminalSessionClosed { SessionId = opened.SessionId, Generation = opened.Generation, Reason = "cycle-complete" }
                }));

            previousSessionId = opened.SessionId;
        }

        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();
        terminals.Get(previousSessionId!)!.State.Should().Be("closed", "the completed request stream processes its final close frame before it ends");
    }

    private static AgentTerminalFrame Hello(int tenantId, Guid agentId, Guid connectionId, ulong epoch) =>
        Frame(tenantId, agentId, connectionId, epoch, 0, new AgentTerminalFrame
        {
            Hello = new AgentTerminalHello
            {
                Capabilities = { AgentTerminalSessionRegistry.IdempotentCloseCapability },
                TerminalCapability = new TerminalCapability { Supported = true, AvailableShells = { "bash" } }
            }
        });

    private static AgentTerminalFrame Frame(
        int tenantId,
        Guid agentId,
        Guid connectionId,
        ulong epoch,
        ulong sequence,
        AgentTerminalFrame frame)
    {
        frame.ProtocolVersion = "1.0";
        frame.TenantId = tenantId;
        frame.ClientId = agentId.ToString("D");
        frame.ConnectionId = connectionId.ToString("D");
        frame.ConnectionEpoch = epoch;
        frame.Sequence = sequence;
        return frame;
    }

    private static async Task<IHost> BuildHostAsync(
        int tenantId,
        Guid agentId,
        IClientPresenceRouter presence,
        IAgentTerminalSessionRegistry terminals,
        IMcpOperatorTerminalSessionStore? durableSessions = null)
    {
        var terminalSessions = durableSessions ?? new RecordingTerminalSessionStore();
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("AgentGatewayAccess", policy =>
                        policy.RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _)));
                });
                services.AddGrpc();
                services.AddSingleton(presence);
                services.AddSingleton(terminals);
                services.AddSingleton<IMcpOperatorTerminalSessionStore>(terminalSessions);
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    TerminalGatewayEnabled = true,
                    TerminalAuthorityEnabled = true
                });
                services.AddSingleton(NullLogger<AgentTerminalGatewayService>.Instance);
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var id = agentId.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("role", "agent"),
                        new Claim("sub", id),
                        new Claim("agent_id", id),
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim("scope", "netratel:connect")
                    ], "TerminalGatewayTest"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<AgentTerminalGatewayService>());
            });
        });
        return await builder.StartAsync();
    }

    private static async Task<GatewayTerminalFrame> ReadNextAsync(IAsyncStreamReader<GatewayTerminalFrame> stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await stream.MoveNext(timeout.Token)).Should().BeTrue();
        return stream.Current;
    }

    private sealed class InterleavingPresenceRouter(
        ClientKey expectedClient,
        Guid connectionId,
        ulong epoch,
        Func<Task> beforeFirstReturns,
        Action? duringSecondRead = null,
        bool advanceAfterFirstRead = false) : IClientPresenceRouter
    {
        private int _reads;
        public int Reads => _reads;
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public async Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            var read = Interlocked.Increment(ref _reads);
            if (read == 1) await beforeFirstReturns();
            else duringSecondRead?.Invoke();
            return new ClientPresenceSnapshot(client,
                client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                checked((long)epoch) + (advanceAfterFirstRead && read > 1 ? 1 : 0), connectionId,
                0, DateTimeOffset.UtcNow, "test", [], null, "akka", true);
        }
    }

    private sealed class CurrentPresenceRouter(ClientKey expectedClient, Guid connectionId, ulong epoch) : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(new ClientPresenceSnapshot(
                client,
                client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                checked((long)epoch),
                connectionId,
                0,
                DateTimeOffset.UtcNow,
                "test",
                [],
                null,
                "akka",
                true));
    }

    private sealed class SwitchablePresenceRouter(ClientKey expectedClient, Guid connectionId, ulong epoch) : IClientPresenceRouter
    {
        private readonly object _sync = new();
        private Guid _connectionId = connectionId;
        private long _epoch = checked((long)epoch);

        public void Fence(Guid connectionId, ulong epoch)
        {
            lock (_sync)
            {
                _connectionId = connectionId;
                _epoch = checked((long)epoch);
            }
        }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                return Task.FromResult(new ClientPresenceSnapshot(
                    client,
                    client == expectedClient ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                    _epoch,
                    _connectionId,
                    0,
                    DateTimeOffset.UtcNow,
                    "test",
                    [],
                    null,
                    "akka",
                    true));
            }
        }
    }

    private sealed class BlockingTerminalRecovery(McpOperatorTerminalRecovery recovered, Action onDurableLookupCompleted)
        : IMcpOperatorTerminalSessionRecovery
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
            var matchingRecovery = recovered.TenantId == tenantId && recovered.AgentId == agentId &&
                                   recovered.SessionId == sessionId && recovered.Generation == generation
                ? recovered
                : null;
            onDurableLookupCompleted();
            return matchingRecovery;
        }
    }

    private sealed class RecordingTerminalSessionStore : IMcpOperatorTerminalSessionStore
    {
        private readonly ConcurrentDictionary<string, TaskCompletionSource<TerminalTransition>> _openedTransitions = new(StringComparer.Ordinal);

        public ConcurrentQueue<TerminalTransition> MarkedTransitions { get; } = [];

        public Task<TerminalTransition> WaitForOpenedAsync(string sessionId, CancellationToken cancellationToken)
        {
            var existing = MarkedTransitions.FirstOrDefault(transition =>
                transition.SessionId == sessionId && transition.State == McpOperatorTerminalSessionState.Opened);
            if (existing is not null)
            {
                return Task.FromResult(existing);
            }

            var completion = _openedTransitions.GetOrAdd(
                sessionId,
                static _ => new TaskCompletionSource<TerminalTransition>(TaskCreationOptions.RunContinuationsAsynchronously));
            existing = MarkedTransitions.FirstOrDefault(transition =>
                transition.SessionId == sessionId && transition.State == McpOperatorTerminalSessionState.Opened);
            if (existing is not null)
            {
                completion.TrySetResult(existing);
            }

            return completion.Task.WaitAsync(cancellationToken);
        }

        public Task<McpOperatorTerminalSessionLease> CreateOrGetAsync(McpOperatorTerminalSessionCreateRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorTerminalSessionLease?> GetAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorTerminalSessionLease?> GetOwnedAsync(
            string sessionId,
            int tenantId,
            Guid agentId,
            McpOperatorPrincipal principal,
            string mcpResource,
            string mcpInstance,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorTerminalSessionLease?> TouchAsync(string sessionId, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorTerminalSessionLease?> RequestCloseAsync(string sessionId, string reason, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<McpOperatorTerminalSessionLease?> MarkTerminalAsync(
            string sessionId,
            McpOperatorTerminalSessionState state,
            string? reason,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            var transition = new TerminalTransition(sessionId, state, reason);
            MarkedTransitions.Enqueue(transition);
            if (state == McpOperatorTerminalSessionState.Opened && _openedTransitions.TryGetValue(sessionId, out var completion))
            {
                completion.TrySetResult(transition);
            }

            return Task.FromResult<McpOperatorTerminalSessionLease?>(null);
        }

        public Task<IReadOnlyList<McpOperatorTerminalSessionLease>> ClaimDueClosesAsync(DateTimeOffset now, int maximum, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed record TerminalTransition(string SessionId, McpOperatorTerminalSessionState State, string? Reason);
}
