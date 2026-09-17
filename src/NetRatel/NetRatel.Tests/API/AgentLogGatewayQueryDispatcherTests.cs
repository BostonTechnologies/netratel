using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentLogGatewayQueryDispatcherTests
{
    [Fact]
    public async Task QueryAsync_returns_invalid_cursor_from_the_runtime_registry()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var sessions = new AgentLogGatewaySessionRegistry();
        sessions.Register(client, Guid.NewGuid(), 3, new AgentLogHello
        {
            Capabilities = { "log-gateway" },
            Sources = { new LogSourceDescriptor { SourceId = "netratel-runtime", Kind = "runtime", DisplayName = "Runtime", Platform = "linux", Available = true, SupportsHistory = true, SupportsLive = true, SupportsPaging = true } }
        });
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);

        var page = await dispatcher.QueryAsync(client, new GatewayLogPageRequest("netratel-runtime", "invalid", 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);

        page.ErrorCode.Should().Be("invalid_cursor");
    }

    [Fact]
    public async Task QueryAsync_dispatches_only_to_current_transport_and_completes_the_matching_request()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var sessions = new AgentLogGatewaySessionRegistry();
        using var state = sessions.Register(client, connection, 3, Hello());
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);
        using var registration = dispatcher.Register(state);

        var pending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, ["Error"], null, "gateway", ["Example.Provider"], [42], ["System"]), LogQueryOperation.History, CancellationToken.None);
        var request = await registration.Reader.ReadAsync(CancellationToken.None);
        request.Query.Should().NotBeNull();
        request.Query.Providers.Should().ContainSingle().Which.Should().Be("Example.Provider");
        request.Query.EventIds.Should().ContainSingle().Which.Should().Be(42);
        request.Query.Categories.Should().ContainSingle().Which.Should().Be("System");
        registration.TryComplete(new LogQueryResult
        {
            RequestId = request.Query.RequestId,
            Records =
            {
                new AgentLogRecord
                {
                    Cursor = "journal-opaque-cursor",
                    RecordSequence = 1,
                    TimestampUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Severity = "Information",
                    SourceId = "linux-journal-system",
                    Message = "token=client-token-value",
                    StructuredProperties = { ["api-key"] = "api-key-value" }
                }
            }
        }).Should().BeTrue();

        var page = await pending;
        page.Records.Should().ContainSingle(record => record.Cursor == "journal-opaque-cursor");
        page.Records.Single().Message.Should().Contain("[REDACTED]").And.NotContain("client-token-value");
        page.Records.Single().StructuredProperties!["api-key"].Should().Be("[REDACTED]");
    }

    [Fact]
    public async Task Register_RejectsAStaleFence_AndAnOldDisposeCannotRemoveTheCurrentTransport()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var oldConnection = Guid.NewGuid();
        var currentConnection = Guid.NewGuid();
        var sessions = new AgentLogGatewaySessionRegistry();
        using var oldState = sessions.Register(client, oldConnection, 4, Hello());
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);
        using var oldRegistration = dispatcher.Register(oldState);

        using var currentState = sessions.Register(client, currentConnection, 5, Hello());
        using var currentRegistration = dispatcher.Register(currentState);

        Action staleRegistration = () => dispatcher.Register(oldState);
        staleRegistration.Should().Throw<AgentGatewayRegistrationFencedException>();
        oldRegistration.Dispose();

        var pending = dispatcher.QueryAsync(
            client,
            new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null),
            LogQueryOperation.History,
            CancellationToken.None);
        var request = await currentRegistration.Reader.ReadAsync(CancellationToken.None);
        currentRegistration.TryComplete(new LogQueryResult
        {
            RequestId = request.Query.RequestId,
            Records =
            {
                new AgentLogRecord
                {
                    Cursor = "current-cursor",
                    RecordSequence = 1,
                    TimestampUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                    Severity = "Information",
                    SourceId = "linux-journal-system",
                    Message = "current transport only"
                }
            }
        }).Should().BeTrue();

        (await pending).Records.Should().ContainSingle(record => record.Cursor == "current-cursor");
    }

    [Fact]
    public async Task ExactReconnect_OldCompletionCannotCompleteReplacementQuery()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var connection = Guid.NewGuid();
        var sessions = new AgentLogGatewaySessionRegistry();
        using var oldState = sessions.Register(client, connection, 5, Hello());
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);
        using var old = dispatcher.Register(oldState);
        var oldPending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        await old.Reader.ReadAsync();

        using var currentState = sessions.Register(client, connection, 5, Hello());
        using var current = dispatcher.Register(currentState);
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => oldPending);
        old.Dispose();
        old.Dispose();
        current.IsCurrent.Should().BeTrue();

        var pending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        var request = await current.Reader.ReadAsync();
        var result = new LogQueryResult { RequestId = request.Query.RequestId };
        old.TryComplete(result).Should().BeFalse();
        pending.IsCompleted.Should().BeFalse();
        current.TryComplete(result).Should().BeTrue();
        (await pending).Records.Should().BeEmpty();
    }

    [Fact]
    public async Task QueryReconnect_WithSameStateOwnerRejectsOldExactQueryHandle()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var sessions = new AgentLogGatewaySessionRegistry();
        using var state = sessions.Register(client, Guid.NewGuid(), 5, Hello());
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);
        using var old = dispatcher.Register(state);
        using var current = dispatcher.Register(state);
        state.IsCurrent.Should().BeTrue();
        old.IsCurrent.Should().BeFalse();
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        current.RegistrationId.Should().NotBe(old.RegistrationId);
        old.Dispose();
        var pending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        var request = await current.Reader.ReadAsync();
        var result = new LogQueryResult { RequestId = request.Query.RequestId };
        old.TryComplete(result).Should().BeFalse();
        pending.IsCompleted.Should().BeFalse();
        current.TryComplete(result).Should().BeTrue();
        await pending;
    }

    [Fact]
    public async Task ProvisionalQueryRegistration_CannotDispatchBeforeStateActivation()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var sessions = new AgentLogGatewaySessionRegistry();
        using var state = sessions.Register(client, Guid.NewGuid(), 5, Hello(), provisional: true);
        var dispatcher = new AgentLogGatewayQueryDispatcher(sessions);
        using var query = dispatcher.Register(state, provisional: true);
        query.TryActivate().Should().BeTrue();
        var page = await dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        page.Records.Should().BeEmpty();
        query.Reader.TryRead(out _).Should().BeFalse();
        state.TryActivate().Should().BeTrue();
        var pending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        var request = await query.Reader.ReadAsync();
        query.TryComplete(new LogQueryResult { RequestId = request.Query.RequestId }).Should().BeTrue();
        await pending;
    }

    [Fact]
    public async Task QueryResult_CompletedBeforeReplacementCannotEscapeAfterOwnershipChanges()
    {
        var client = new ClientKey(7, Guid.NewGuid());
        var ownerGate = new object();
        var current = true;
        var replaceAfterCompletion = false;
        using var lifetime = new CancellationTokenSource();
        using var state = new AgentLogRegistration(client, Guid.NewGuid(), 5, lifetime.Token,
            () => { lock (ownerGate) return current; },
            () => true,
            lifetime.Cancel,
            _ => false,
            () => [new GatewayLogSourceDescriptorDto("linux-journal-system", "journal", "Journal", "linux", true, null, true, true, true, [])],
            action =>
            {
                lock (ownerGate)
                {
                    var accepted = action();
                    // Hold the owner gate through completion and replacement. The
                    // asynchronous result continuation must see the new owner.
                    if (replaceAfterCompletion) current = false;
                    return accepted;
                }
            });
        var dispatcher = new AgentLogGatewayQueryDispatcher(new AgentLogGatewaySessionRegistry());
        using var registration = dispatcher.Register(state);
        var pending = dispatcher.QueryAsync(client, new GatewayLogPageRequest("linux-journal-system", null, 25, null, null, null, null, null), LogQueryOperation.History, CancellationToken.None);
        var request = await registration.Reader.ReadAsync();
        replaceAfterCompletion = true;
        registration.TryComplete(new LogQueryResult { RequestId = request.Query.RequestId }).Should().BeTrue();
        lifetime.IsCancellationRequested.Should().BeFalse();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    private static AgentLogHello Hello()
    {
        var hello = new AgentLogHello { Capabilities = { "log-gateway" } };
        hello.Sources.Add(new LogSourceDescriptor { SourceId = "linux-journal-system", Kind = "journal", DisplayName = "System Journal", Platform = "linux", Available = true, SupportsHistory = true, SupportsLive = true, SupportsPaging = true });
        return hello;
    }
}
