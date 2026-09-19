using Akka.Actor;
using FluentAssertions;
using NetRatel.Akka.Commands;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class CommandActorTests : IAsyncLifetime
{
    private ActorSystem _system = null!;

    public ValueTask InitializeAsync()
    {
        _system = ActorSystem.Create($"command-tests-{Guid.NewGuid():N}");
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _system.Terminate();
    }

    [Fact]
    public async Task Authoritative_receipt_time_is_monotonic_and_duplicate_returns_the_committed_time()
    {
        var client = new ClientKey(41, Guid.NewGuid());
        var key = new CommandKey(client.TenantId, "receipt-time");
        var actor = _system.ActorOf(CommandActor.Props(key));
        var requestedAt = DateTimeOffset.Parse("2026-09-06T09:15:36Z");
        var statuses = new[] { CommandLifecycleStatus.Created, CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Started, CommandLifecycleStatus.Cancelled };
        var offsets = new[] { -5, 0, 10, 5, -20 };
        RecordCommandLifecycleEvent? last = null;
        for (var index = 0; index < statuses.Length; index++)
        {
            var order = (ulong)index + 1;
            last = new(new(client, key.CommandId, "receipt-correlation", requestedAt, requestedAt.AddSeconds(offsets[index]),
                order, order, statuses[index], "akka", true));
            var result = await actor.Ask<CommandMessageResult>(last);
            result.Disposition.Should().Be(CommandMessageDisposition.Accepted);
            result.CurrentStatusTimestamp.Should().Be(requestedAt.AddSeconds(index < 2 ? 0 : 10));
        }
        var duplicate = await actor.Ask<CommandMessageResult>(last! with { Event = last!.Event with { StatusTimestamp = requestedAt.AddDays(1) } });
        duplicate.Disposition.Should().Be(CommandMessageDisposition.Duplicate);
        duplicate.CurrentStatusTimestamp.Should().Be(requestedAt.AddSeconds(10));
        var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(key));
        state.RequestTimestamp.Should().Be(requestedAt);
        state.History.Select(item => item.StatusTimestamp).Should().Equal(requestedAt, requestedAt,
            requestedAt.AddSeconds(10), requestedAt.AddSeconds(10), requestedAt.AddSeconds(10));
    }

    [Fact]
    public async Task Shadow_timestamp_semantics_are_preserved()
    {
        var client = new ClientKey(41, Guid.NewGuid());
        var key = new CommandKey(client.TenantId, "shadow-time");
        var actor = _system.ActorOf(CommandActor.Props(key));
        var requestedAt = DateTimeOffset.Parse("2026-09-06T09:15:36Z");
        var record = new RecordCommandLifecycleEvent(new(client, key.CommandId, "shadow-correlation",
            requestedAt, requestedAt.AddSeconds(-5), 1, 1, CommandLifecycleStatus.Created));
        var result = await actor.Ask<CommandMessageResult>(record);
        result.CurrentStatusTimestamp.Should().Be(record.Event.StatusTimestamp);
        (await actor.Ask<CommandShadowState>(new GetCommandShadowState(key))).History.Single().StatusTimestamp.Should().Be(record.Event.StatusTimestamp);
    }

    [Theory]
    [InlineData(CommandLifecycleStatus.Completed)]
    [InlineData(CommandLifecycleStatus.Failed)]
    [InlineData(CommandLifecycleStatus.Cancelled)]
    public async Task LegalLifecycle_ReachesEachSupportedTerminalState(
        CommandLifecycleStatus terminalStatus)
    {
        var client = new ClientKey(41, Guid.NewGuid());
        var command = new CommandKey(client.TenantId, $"command-{Guid.NewGuid():N}");
        var actor = _system.ActorOf(CommandActor.Props(command));
        var requestTimestamp = DateTimeOffset.UtcNow;
        var statuses = new[]
        {
            CommandLifecycleStatus.Created,
            CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted,
            CommandLifecycleStatus.Started,
            terminalStatus
        };

        for (var index = 0; index < statuses.Length; index++)
        {
            var version = checked((ulong)index + 1);
            var result = await actor.Ask<CommandMessageResult>(CreateRecord(
                client,
                command.CommandId,
                requestTimestamp,
                statuses[index],
                version,
                version));

            result.Disposition.Should().Be(CommandMessageDisposition.Accepted);
        }

        var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(command));
        state.CurrentStatus.Should().Be(terminalStatus);
        state.History.Select(entry => entry.Status).Should().Equal(statuses);
        state.History.Should().HaveCount(5);
        state.Source.Should().Be("akka-shadow");
        state.IsAuthoritative.Should().BeFalse();
    }

    [Theory]
    [InlineData(CommandLifecycleStatus.Cancelled)]
    [InlineData(CommandLifecycleStatus.Failed)]
    public async Task Accepted_command_can_cancel_or_fail_before_execution_without_claiming_started(CommandLifecycleStatus terminalStatus)
    {
        var client = new ClientKey(41, Guid.NewGuid());
        var command = new CommandKey(client.TenantId, $"queued-{Guid.NewGuid():N}");
        var actor = _system.ActorOf(CommandActor.Props(command));
        var timestamp = DateTimeOffset.UtcNow;
        var statuses = new[] { CommandLifecycleStatus.Created, CommandLifecycleStatus.Dispatched, CommandLifecycleStatus.Accepted, terminalStatus };
        for (var index = 0; index < statuses.Length; index++)
        {
            var sequence = (ulong)index + 1;
            (await actor.Ask<CommandMessageResult>(CreateRecord(client, command.CommandId, timestamp, statuses[index], sequence, sequence)))
                .Disposition.Should().Be(CommandMessageDisposition.Accepted);
        }
        var duplicate = await actor.Ask<CommandMessageResult>(CreateRecord(client, command.CommandId, timestamp, terminalStatus, 4, 4));
        duplicate.Disposition.Should().Be(CommandMessageDisposition.Duplicate);
        var lateStart = await actor.Ask<CommandMessageResult>(CreateRecord(client, command.CommandId, timestamp, CommandLifecycleStatus.Started, 5, 5));
        lateStart.Disposition.Should().Be(CommandMessageDisposition.InvalidTransition);
        (await actor.Ask<CommandShadowState>(new GetCommandShadowState(command))).History.Select(entry => entry.Status).Should().Equal(statuses);
        CommandLifecycleRules.IsValidTransition(CommandLifecycleStatus.Accepted, CommandLifecycleStatus.Completed).Should().BeFalse();
    }

    [Fact]
    public async Task InvalidTransitionsAndStaleEvents_AreRejectedWithoutMutatingState()
    {
        var client = new ClientKey(42, Guid.NewGuid());
        var command = new CommandKey(client.TenantId, "opaque-command-id");
        var actor = _system.ActorOf(CommandActor.Props(command));
        var requestTimestamp = DateTimeOffset.UtcNow;

        var outOfOrder = await actor.Ask<CommandMessageResult>(CreateRecord(
            client,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Started,
            version: 1,
            sequence: 1));
        await actor.Ask<CommandMessageResult>(CreateRecord(
            client,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Created,
            version: 2,
            sequence: 2));
        var duplicate = await actor.Ask<CommandMessageResult>(CreateRecord(
            client,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Created,
            version: 2,
            sequence: 2));
        var stale = await actor.Ask<CommandMessageResult>(CreateRecord(
            client,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Dispatched,
            version: 3,
            sequence: 1));

        outOfOrder.Disposition.Should().Be(CommandMessageDisposition.InvalidTransition);
        duplicate.Disposition.Should().Be(CommandMessageDisposition.Duplicate);
        stale.Disposition.Should().Be(CommandMessageDisposition.StaleEvent);
        var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(command));
        state.CurrentStatus.Should().Be(CommandLifecycleStatus.Created);
        state.History.Should().ContainSingle();
    }

    [Fact]
    public async Task FirstCreatedEvent_BindsClientAndCorrelationIdentity()
    {
        var firstClient = new ClientKey(43, Guid.NewGuid());
        var otherClient = new ClientKey(43, Guid.NewGuid());
        var command = new CommandKey(firstClient.TenantId, "shared-command");
        var actor = _system.ActorOf(CommandActor.Props(command));
        var requestTimestamp = DateTimeOffset.UtcNow;

        await actor.Ask<CommandMessageResult>(CreateRecord(
            firstClient,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Created,
            version: 1,
            sequence: 1));
        var wrongClient = await actor.Ask<CommandMessageResult>(CreateRecord(
            otherClient,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Dispatched,
            version: 2,
            sequence: 2));
        var wrongCorrelation = await actor.Ask<CommandMessageResult>(CreateRecord(
            firstClient,
            command.CommandId,
            requestTimestamp,
            CommandLifecycleStatus.Dispatched,
            version: 2,
            sequence: 2,
            correlationId: "different-correlation"));

        wrongClient.Disposition.Should().Be(CommandMessageDisposition.IdentityMismatch);
        wrongCorrelation.Disposition.Should().Be(CommandMessageDisposition.IdentityMismatch);
        var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(command));
        state.Client.Should().Be(firstClient);
        state.CurrentStatus.Should().Be(CommandLifecycleStatus.Created);
    }

    [Fact]
    public async Task AuthoritativeLifecycle_IsRecordedAsAkkaAuthorityAndCannotMixWithShadow()
    {
        var client = new ClientKey(43, Guid.NewGuid());
        var command = new CommandKey(client.TenantId, "authoritative-command");
        var actor = _system.ActorOf(CommandActor.Props(command));
        var requestedAt = DateTimeOffset.UtcNow;

        foreach (var (status, order) in new[]
                 {
                     (CommandLifecycleStatus.Created, 1UL),
                     (CommandLifecycleStatus.Dispatched, 2UL),
                     (CommandLifecycleStatus.Accepted, 3UL),
                     (CommandLifecycleStatus.Started, 4UL),
                     (CommandLifecycleStatus.Completed, 5UL)
                 })
        {
            var result = await actor.Ask<CommandMessageResult>(CreateRecord(
                client, command.CommandId, requestedAt, status, order, order, authoritative: true));
            result.Disposition.Should().Be(CommandMessageDisposition.Accepted);
        }

        var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(command));
        state.Source.Should().Be("akka");
        state.IsAuthoritative.Should().BeTrue();

        var mixed = await actor.Ask<CommandMessageResult>(CreateRecord(
            client, command.CommandId, requestedAt, CommandLifecycleStatus.Completed, 6, 6));
        mixed.Disposition.Should().Be(CommandMessageDisposition.IdentityMismatch);
    }

    [Fact]
    public async Task Router_IsolatesCommandsAndReportsBoundedDiagnostics()
    {
        var completedClient = new ClientKey(44, Guid.NewGuid());
        var activeClient = new ClientKey(44, Guid.NewGuid());
        var completedCommand = $"completed-{Guid.NewGuid():N}";
        var activeCommand = $"active-{Guid.NewGuid():N}";
        var requestTimestamp = DateTimeOffset.UtcNow;
        var router = _system.ActorOf(ClientCommandRouterActor.Props());

        await ProgressAsync(
            router,
            completedClient,
            completedCommand,
            requestTimestamp,
            CommandLifecycleStatus.Completed);
        await router.Ask<CommandMessageResult>(CreateRecord(
            activeClient,
            activeCommand,
            requestTimestamp,
            CommandLifecycleStatus.Created,
            version: 1,
            sequence: 1));
        await router.Ask<CommandMessageResult>(CreateRecord(
            activeClient,
            activeCommand,
            requestTimestamp,
            CommandLifecycleStatus.Started,
            version: 2,
            sequence: 2));
        await router.Ask<CommandMessageResult>(CreateRecord(
            activeClient,
            activeCommand,
            requestTimestamp,
            CommandLifecycleStatus.Dispatched,
            version: 2,
            sequence: 0));

        var completed = await router.Ask<CommandShadowState>(
            new GetCommandShadowState(new CommandKey(completedClient.TenantId, completedCommand)));
        var active = await router.Ask<CommandShadowState>(
            new GetCommandShadowState(new CommandKey(activeClient.TenantId, activeCommand)));
        var diagnostics = await router.Ask<ClientCommandRouteStatus>(new ProbeClientCommandRoute());

        completed.CurrentStatus.Should().Be(CommandLifecycleStatus.Completed);
        active.CurrentStatus.Should().Be(CommandLifecycleStatus.Created);
        diagnostics.ActiveCommands.Should().Be(1);
        diagnostics.CompletedCommands.Should().Be(1);
        diagnostics.FailedCommands.Should().Be(0);
        diagnostics.InvalidTransitions.Should().Be(1);
        diagnostics.StaleEvents.Should().Be(1);
        diagnostics.Authority.Should().Be("unavailable");
    }

    private static async Task ProgressAsync(
        IActorRef actor,
        ClientKey client,
        string commandId,
        DateTimeOffset requestTimestamp,
        CommandLifecycleStatus terminalStatus)
    {
        var statuses = new[]
        {
            CommandLifecycleStatus.Created,
            CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted,
            CommandLifecycleStatus.Started,
            terminalStatus
        };

        for (var index = 0; index < statuses.Length; index++)
        {
            var order = checked((ulong)index + 1);
            await actor.Ask<CommandMessageResult>(CreateRecord(
                client,
                commandId,
                requestTimestamp,
                statuses[index],
                order,
                order));
        }
    }

    private static RecordCommandLifecycleEvent CreateRecord(
        ClientKey client,
        string commandId,
        DateTimeOffset requestTimestamp,
        CommandLifecycleStatus status,
        ulong version,
        ulong sequence,
        string correlationId = "netratel-task-test",
        bool authoritative = false) =>
        new(new CommandLifecycleEvent(
            client,
            commandId,
            correlationId,
            requestTimestamp,
            requestTimestamp.AddSeconds(checked((long)version)),
            version,
            sequence,
            status,
            IsAuthoritative: authoritative));
}
