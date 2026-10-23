using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Application.Events;
using NetRatel.Application.Notifications;
using NetRatel.Infrastructure.Events;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.Persistence;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class OutboxProcessorPostgresTests(PostgreSqlPersistenceFixture fixture)
{
    [Fact]
    public async Task Disable_of_selected_candidate_prevents_its_later_claim_and_publication()
    {
        var first = Message();
        var second = Message();
        second.OccurredUtc = first.OccurredUtc.AddSeconds(1);
        var connection = await SeedAsync(first, second);
        var published = new List<Guid>();
        var publisher = new Publisher(async (envelope, ct) =>
        {
            published.Add(envelope.Id);
            if (envelope.Id == first.Id) await DisableAsync(connection, second.Id, ct);
        });
        await using var provider = Provider(connection, publisher);
        using var processor = Processor(provider);

        await processor.ProcessBatchAsync(CancellationToken.None);
        await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(new[] { first.Id }, published);
        await using var verify = Database(connection);
        Assert.Equal(OutboxStatuses.Published, (await verify.OutboxMessages.SingleAsync(row => row.Id == first.Id)).Status);
        AssertDisabled(await verify.OutboxMessages.SingleAsync(row => row.Id == second.Id), second);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disable_during_admitted_publication_survives_late_success_or_failure(bool failPublication)
    {
        var message = Message();
        message.Attempts = 19;
        message.LastError = "previous publication failure";
        var connection = await SeedAsync(message);
        await using var disabler = Database(connection);
        var beforeClaim = await disabler.OutboxMessages.SingleAsync();
        Assert.Null(beforeClaim.LockOwner);
        Assert.Null(beforeClaim.LockedUntilUtc);
        var deliveries = 0;
        var publisher = new Publisher(async (envelope, ct) =>
        {
            Assert.Equal(message.Id, envelope.Id);
            deliveries++;
            await using (var observer = Database(connection))
            {
                var admitted = await observer.OutboxMessages.SingleAsync(row => row.Id == message.Id, ct);
                Assert.Equal(OutboxStatuses.Pending, admitted.Status);
                Assert.NotNull(admitted.LockOwner);
                Assert.NotNull(admitted.LockedUntilUtc);
            }
            // The disabler retains its real pre-claim snapshot with null lease fields.
            await new NetRatelNotificationService(disabler,
                new NetRatelNotificationDisplaySanitizer(new Names())).DisableAsync(message.Id, ct);
            if (failPublication) throw new InvalidOperationException("controlled in-flight publication failure");
        });
        await using var provider = Provider(connection, publisher);
        using var processor = Processor(provider);

        await processor.ProcessBatchAsync(CancellationToken.None);
        await processor.ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, deliveries);
        await using var verify = Database(connection);
        AssertDisabled(await verify.OutboxMessages.SingleAsync(), message);
    }

    [Fact]
    public async Task Competing_workers_with_the_same_selected_candidate_admit_one_publication()
    {
        var message = Message();
        var connection = await SeedAsync(message);
        var selection = new ConcurrentSelection();
        var deliveries = 0;
        var publisher = new Publisher((_, _) =>
        {
            Interlocked.Increment(ref deliveries);
            return Task.CompletedTask;
        });
        await using var provider = Provider(connection, publisher, selection);
        using var first = Processor(provider);
        using var second = Processor(provider);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));

        await Task.WhenAll(first.ProcessBatchAsync(budget.Token), second.ProcessBatchAsync(budget.Token));

        Assert.Equal(2, selection.CandidatesSelected);
        Assert.Equal(1, deliveries);
        await using var verify = Database(connection);
        var stored = await verify.OutboxMessages.SingleAsync();
        Assert.Equal(OutboxStatuses.Published, stored.Status);
        Assert.Null(stored.LockOwner);
        Assert.Null(stored.LockedUntilUtc);
    }

    private async Task<string> SeedAsync(params OutboxMessage[] messages)
    {
        var connection = await fixture.CreateDatabaseAsync();
        await using var db = Database(connection);
        await db.Database.MigrateAsync();
        db.OutboxMessages.AddRange(messages);
        await db.SaveChangesAsync();
        return connection;
    }

    private static async Task DisableAsync(string connection, Guid id, CancellationToken ct)
    {
        await using var db = Database(connection);
        await new NetRatelNotificationService(db, new NetRatelNotificationDisplaySanitizer(new Names())).DisableAsync(id, ct);
    }

    private static void AssertDisabled(OutboxMessage actual, OutboxMessage original)
    {
        Assert.Equal(OutboxStatuses.Disabled, actual.Status);
        Assert.Equal(original.Attempts, actual.Attempts);
        Assert.Equal(original.LastError, actual.LastError);
        Assert.Equal(original.NextAttemptUtc, actual.NextAttemptUtc);
        Assert.Null(actual.LockOwner);
        Assert.Null(actual.LockedUntilUtc);
    }

    private static OutboxMessage Message() => new()
    {
        Id = Guid.NewGuid(), Type = "DomainEvent.Fixture.Completed", Source = "owned-test-fixture",
        CorrelationId = Guid.NewGuid().ToString("N"), PayloadJson = "{}", Status = OutboxStatuses.Pending,
        OccurredUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z"),
        NextAttemptUtc = DateTimeOffset.Parse("2026-01-01T00:00:00Z")
    };

    private static OrchestratorDbContext Database(string connection) =>
        new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options);

    private static ServiceProvider Provider(string connection, IEventPublisher publisher, IInterceptor? interceptor = null) =>
        new ServiceCollection().AddDbContext<OrchestratorDbContext>(options =>
        {
            options.UseNpgsql(connection);
            if (interceptor is not null) options.AddInterceptors(interceptor);
        }).AddSingleton(publisher).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

    private static OutboxProcessor Processor(ServiceProvider provider) => new(provider, NullLogger<OutboxProcessor>.Instance);

    private sealed class Publisher(Func<OutboxEnvelope, CancellationToken, Task> publish) : IEventPublisher
    {
        public Task PublishAsync(OutboxEnvelope envelope, CancellationToken ct) => publish(envelope, ct);
    }

    private sealed class Names : IClientDisplayNameResolver
    {
        public IReadOnlyDictionary<string, string> ResolveDisplayNames(IEnumerable<string> identities) => new Dictionary<string, string>();
    }

    private sealed class ConcurrentSelection : DbCommandInterceptor
    {
        private readonly TaskCompletionSource _selected = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _candidatesSelected;
        public int CandidatesSelected => Volatile.Read(ref _candidatesSelected);

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("\"OutboxMessages\"", StringComparison.Ordinal) &&
                result.FieldCount == 1 && result.GetName(0) == "Id")
            {
                if (Interlocked.Increment(ref _candidatesSelected) == 2) _selected.TrySetResult();
                await _selected.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
