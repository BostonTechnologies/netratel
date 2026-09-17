using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class CommandPersistenceStoreTests
{
    private static readonly InMemoryDatabaseRoot DatabaseRoot = new();

    [Fact]
    public async Task CommandInbox_DetectsDuplicatesAndTracksCorrelation()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycleEvent = CreateEvent(CommandLifecycleStatus.Created, 1);

        var first = await store.RecordAsync(lifecycleEvent, CancellationToken.None);
        var duplicate = await store.RecordAsync(lifecycleEvent, CancellationToken.None);

        first.Disposition.Should().Be(CommandPersistenceWriteDisposition.Stored);
        duplicate.Disposition.Should().Be(CommandPersistenceWriteDisposition.Duplicate);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var receipt = await db.CommandInboxReceipts.SingleAsync();
        receipt.CorrelationId.Should().Be(lifecycleEvent.CorrelationId);
        receipt.DuplicateCount.Should().Be(1);
        (await db.CommandIntentEvents.CountAsync()).Should().Be(1);
        (await db.CommandOutbox.CountAsync()).Should().Be(1);

        var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);
        diagnostics.InboxDepth.Should().Be(1);
        diagnostics.OutboxDepth.Should().Be(1);
        diagnostics.DuplicateDetectionCount.Should().Be(1);
        diagnostics.Authority.Should().Be("unavailable");
    }

    [Fact]
    public async Task DurableHistory_ReplaysEveryLifecycleStateInOrder()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CreateLifecycle(CommandLifecycleStatus.Completed);

        foreach (var lifecycleEvent in lifecycle)
        {
            var result = await store.RecordAsync(lifecycleEvent, CancellationToken.None);
            result.Disposition.Should().Be(CommandPersistenceWriteDisposition.Stored);
        }

        var replay = await store.ReplayAsync(lifecycle[0].Command, CancellationToken.None);

        replay.Select(item => item.Status).Should().Equal(
            CommandLifecycleStatus.Created,
            CommandLifecycleStatus.Dispatched,
            CommandLifecycleStatus.Accepted,
            CommandLifecycleStatus.Started,
            CommandLifecycleStatus.Completed);
        replay.Select(item => item.CorrelationId).Should().OnlyContain(
            correlationId => correlationId == lifecycle[0].CorrelationId);
        var diagnostics = await store.GetDiagnosticsAsync(CancellationToken.None);
        diagnostics.InboxDepth.Should().Be(5);
        diagnostics.OutboxDepth.Should().Be(0);
        diagnostics.ReplayCount.Should().Be(1);
        diagnostics.LastReplayAtUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task CommandOutbox_SurvivesStoreRestartAndExposesPendingIntentForReplay()
    {
        var databaseName = Guid.NewGuid().ToString("N");
        var databaseRoot = DatabaseRoot;
        var lifecycle = CreateLifecycle(CommandLifecycleStatus.Started);

        await using (var firstProvider = CreateProvider(databaseName, databaseRoot))
        {
            var firstStore = firstProvider.GetRequiredService<ICommandPersistenceStore>();
            foreach (var lifecycleEvent in lifecycle)
            {
                await firstStore.RecordAsync(lifecycleEvent, CancellationToken.None);
            }
        }

        await using var recoveredProvider = CreateProvider(databaseName, databaseRoot);
        var recoveredStore = recoveredProvider.GetRequiredService<ICommandPersistenceStore>();
        var pending = await recoveredStore.ReadPendingOutboxAsync(10, CancellationToken.None);
        var replay = await recoveredStore.ReplayAsync(lifecycle[0].Command, CancellationToken.None);

        pending.Should().ContainSingle();
        pending[0].CurrentStatus.Should().Be(CommandLifecycleStatus.Started);
        pending[0].ObservedDispatchCount.Should().Be(1);
        pending[0].Mode.Should().Be("shadow-only");
        pending[0].IsAuthoritative.Should().BeFalse();
        replay.Select(item => item.Status).Should().Equal(lifecycle.Select(item => item.Status));
    }

    [Fact]
    public async Task Store_PersistsAuthoritativeCommandIntent()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CreateLifecycle(CommandLifecycleStatus.Completed)
            .Select(item => item with { Source = "akka", IsAuthoritative = true })
            .ToArray();
        CommandPersistenceWriteResult? result = null;
        foreach (var lifecycleEvent in lifecycle)
        {
            result = await store.RecordAsync(lifecycleEvent, CancellationToken.None);
        }

        var pending = await store.ReadPendingOutboxAsync(10, CancellationToken.None);
        var replay = await store.ReplayAsync(lifecycle[0].Command, CancellationToken.None);

        result!.Disposition.Should().Be(CommandPersistenceWriteDisposition.Stored);
        pending.Should().BeEmpty();
        replay.Select(item => item.Status).Should().Equal(lifecycle.Select(item => item.Status));
        replay.Should().OnlyContain(item => item.IsAuthoritative && item.Source == "akka");
    }

    [Theory]
    [InlineData(CommandLifecycleStatus.Cancelled)]
    [InlineData(CommandLifecycleStatus.Failed)]
    public async Task Store_PersistsTerminalAcknowledgementForAnAcceptedButUnstartedCommand(CommandLifecycleStatus terminalStatus)
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CreateLifecycle(terminalStatus)
            .Where(item => item.Status != CommandLifecycleStatus.Started)
            .Select((item, index) => item with { Source = "akka", IsAuthoritative = true, Version = (ulong)index + 1, Sequence = (ulong)index + 1 })
            .ToArray();
        foreach (var lifecycleEvent in lifecycle)
            (await store.RecordAsync(lifecycleEvent, CancellationToken.None)).Disposition.Should().Be(CommandPersistenceWriteDisposition.Stored);
        var replay = await store.ReplayAsync(lifecycle[0].Command, CancellationToken.None);
        replay.Select(item => item.Status).Should().Equal(lifecycle.Select(item => item.Status));
        (await store.ReadPendingOutboxAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task BoundedReplay_ReportsOverflowAndPreservesExistingFullReplay()
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CreateLifecycle(CommandLifecycleStatus.Completed);
        foreach (var item in lifecycle)
            await store.RecordAsync(item, CancellationToken.None);

        var limited = await store.ReplayBoundedAsync(lifecycle[0].Command, 2, CancellationToken.None);
        var complete = await store.ReplayBoundedAsync(lifecycle[0].Command, lifecycle.Length, CancellationToken.None);
        var original = await store.ReplayAsync(lifecycle[0].Command, CancellationToken.None);

        limited.Events.Should().HaveCount(2);
        limited.IsComplete.Should().BeFalse();
        complete.Events.Should().Equal(lifecycle);
        complete.IsComplete.Should().BeTrue();
        original.Should().Equal(lifecycle);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("18446744073709551616")]
    public async Task BoundedReplay_RejectsUnrepresentableHistoryWithoutTruncatingItsIdentity(string invalid)
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var lifecycle = CreateLifecycle(CommandLifecycleStatus.Completed);
        foreach (var item in lifecycle)
            await store.RecordAsync(item, CancellationToken.None);
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var terminal = await db.CommandIntentEvents.SingleAsync(item => item.Status == CommandLifecycleStatus.Completed);
        terminal.Sequence = decimal.Parse(invalid, System.Globalization.CultureInfo.InvariantCulture);
        await db.SaveChangesAsync();

        var result = await store.ReplayBoundedAsync(lifecycle[0].Command, 8, CancellationToken.None);

        result.IsComplete.Should().BeFalse();
        result.Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65)]
    public async Task BoundedReplay_RejectsUnboundedReadSizes(int maximum)
    {
        await using var provider = CreateProvider();
        var store = provider.GetRequiredService<ICommandPersistenceStore>();
        var read = () => store.ReplayBoundedAsync(new CommandKey(42, "test-command"), maximum, CancellationToken.None);
        await read.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    private static ServiceProvider CreateProvider(
        string? databaseName = null,
        InMemoryDatabaseRoot? databaseRoot = null)
    {
        var sharedDatabaseName = databaseName ?? Guid.NewGuid().ToString("N");
        var sharedDatabaseRoot = databaseRoot ?? DatabaseRoot;
        var services = new ServiceCollection();
        services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseInMemoryDatabase(
                sharedDatabaseName,
                sharedDatabaseRoot));
        services.AddNetRatelCommandPersistence();
        return services.BuildServiceProvider();
    }

    internal static CommandLifecycleEvent[] CreateLifecycle(CommandLifecycleStatus finalStatus)
    {
        var statuses = finalStatus is CommandLifecycleStatus.Completed or
            CommandLifecycleStatus.Failed or CommandLifecycleStatus.Cancelled
            ? new[]
            {
                CommandLifecycleStatus.Created,
                CommandLifecycleStatus.Dispatched,
                CommandLifecycleStatus.Accepted,
                CommandLifecycleStatus.Started,
                finalStatus
            }
            : new[]
            {
                CommandLifecycleStatus.Created,
                CommandLifecycleStatus.Dispatched,
                CommandLifecycleStatus.Accepted,
                CommandLifecycleStatus.Started
            };

        var client = new ClientKey(520, Guid.NewGuid());
        var commandId = $"command-{Guid.NewGuid():N}";
        var correlationId = $"correlation-{Guid.NewGuid():N}";
        var requestTimestamp = DateTimeOffset.UtcNow;
        return statuses.Select((status, index) =>
            new CommandLifecycleEvent(
                client,
                commandId,
                correlationId,
                requestTimestamp,
                requestTimestamp.AddSeconds(index + 1),
                checked((ulong)index + 1),
                checked((ulong)index + 1),
                status)).ToArray();
    }

    private static CommandLifecycleEvent CreateEvent(CommandLifecycleStatus status, ulong order)
    {
        var requestTimestamp = DateTimeOffset.UtcNow;
        return new(
            new(520, Guid.NewGuid()),
            $"command-{Guid.NewGuid():N}",
            $"correlation-{Guid.NewGuid():N}",
            requestTimestamp,
            requestTimestamp.AddSeconds(1),
            order,
            order,
            status);
    }
}
