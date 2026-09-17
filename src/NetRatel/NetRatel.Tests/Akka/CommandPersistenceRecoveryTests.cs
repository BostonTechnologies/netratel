using Akka.Actor;
using Akka.Pattern;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Akka.Commands;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Akka;

public sealed class CommandPersistenceRecoveryTests
{
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    [Fact]
    public async Task Authoritative_receipt_timestamp_survives_recovery_and_duplicate_delivery()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CommandPersistenceStoreTests.CreateLifecycle(CommandLifecycleStatus.Completed)
            .Select(item => item with { IsAuthoritative = true, Source = "akka", StatusTimestamp = item.RequestTimestamp.AddSeconds(-10) }).ToArray();
        var system = ActorSystem.Create($"receipt-recovery-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(CommandActor.Props(lifecycle[0].Command, store));
            foreach (var item in lifecycle)
                (await actor.Ask<CommandMessageResult>(new RecordCommandLifecycleEvent(item))).Disposition.Should().Be(CommandMessageDisposition.Accepted);
            await actor.GracefulStop(TimeSpan.FromSeconds(3));
            var recovered = system.ActorOf(CommandActor.Props(lifecycle[0].Command, store));
            var duplicate = await recovered.Ask<CommandMessageResult>(new RecordCommandLifecycleEvent(
                lifecycle[^1] with { StatusTimestamp = lifecycle[0].RequestTimestamp.AddDays(1) }));
            duplicate.Disposition.Should().Be(CommandMessageDisposition.Duplicate);
            duplicate.CurrentStatusTimestamp.Should().Be(lifecycle[0].RequestTimestamp);
            var persisted = await store.ReplayAsync(lifecycle[0].Command, CancellationToken.None);
            persisted.Should().HaveCount(lifecycle.Length);
            persisted.Should().OnlyContain(item => item.StatusTimestamp == lifecycle[0].RequestTimestamp);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task RestartedCommandActor_ReconstructsStateFromDurableHistory()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CommandPersistenceStoreTests.CreateLifecycle(CommandLifecycleStatus.Completed);
        var command = lifecycle[0].Command;
        var firstSystem = ActorSystem.Create($"persistent-command-first-{Guid.NewGuid():N}");

        var firstActor = firstSystem.ActorOf(CommandActor.Props(command, store));
        foreach (var lifecycleEvent in lifecycle)
        {
            var result = await firstActor.Ask<CommandMessageResult>(
                new RecordCommandLifecycleEvent(lifecycleEvent));
            result.Disposition.Should().Be(CommandMessageDisposition.Accepted);
        }

        await firstActor.GracefulStop(TimeSpan.FromSeconds(3));
        await firstSystem.Terminate();

        var recoveredSystem = ActorSystem.Create($"persistent-command-recovered-{Guid.NewGuid():N}");
        try
        {
            var recoveredActor = recoveredSystem.ActorOf(CommandActor.Props(command, store));
            var state = await recoveredActor.Ask<CommandShadowState>(new GetCommandShadowState(command));

            state.CurrentStatus.Should().Be(CommandLifecycleStatus.Completed);
            state.History.Select(item => item.Status).Should().Equal(lifecycle.Select(item => item.Status));
            state.CorrelationId.Should().Be(lifecycle[0].CorrelationId);
            state.IsAuthoritative.Should().BeFalse();
            var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);
            diagnostics.RecoverySuccessCount.Should().BeGreaterThanOrEqualTo(2);
            diagnostics.ReplayCount.Should().BeGreaterThanOrEqualTo(2);
        }
        finally
        {
            await recoveredSystem.Terminate();
        }
    }

    [Fact]
    public async Task DuplicateAfterRecovery_IsIdempotentAndDoesNotExtendHistory()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CommandPersistenceStoreTests.CreateLifecycle(CommandLifecycleStatus.Started);
        foreach (var lifecycleEvent in lifecycle)
        {
            await store.RecordAsync(lifecycleEvent, CancellationToken.None);
        }

        var system = ActorSystem.Create($"persistent-command-duplicate-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(CommandActor.Props(lifecycle[0].Command, store));
            var result = await actor.Ask<CommandMessageResult>(
                new RecordCommandLifecycleEvent(lifecycle[^1]));
            var state = await actor.Ask<CommandShadowState>(new GetCommandShadowState(lifecycle[0].Command));

            result.Disposition.Should().Be(CommandMessageDisposition.Duplicate);
            state.History.Should().HaveCount(4);
            var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);
            diagnostics.DuplicateDetectionCount.Should().Be(1);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task PersistentRouter_CompletesItsFirstRoutedRecord()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var client = new ClientKey(19, Guid.NewGuid());
        var command = new CommandKey(client.TenantId, $"routed-{Guid.NewGuid():N}");
        var system = ActorSystem.Create($"persistent-command-router-{Guid.NewGuid():N}");

        try
        {
            var router = system.ActorOf(ClientCommandRouterActor.Props(store));
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
                var result = await router.Ask<CommandMessageResult>(
                    new RecordCommandLifecycleEvent(new CommandLifecycleEvent(
                        client,
                        command.CommandId,
                        "router-regression",
                        requestedAt,
                        requestedAt.AddSeconds((long)order),
                        Version: order,
                        Sequence: order,
                        Status: status,
                        Source: "akka-dev-canary",
                        IsAuthoritative: true)));

                result.Disposition.Should().Be(CommandMessageDisposition.Accepted);
            }
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static ServiceProvider CreateProvider()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseInMemoryDatabase(databaseName, DatabaseRoot));
        services.AddNetRatelCommandPersistence();
        return services.BuildServiceProvider();
    }
}
