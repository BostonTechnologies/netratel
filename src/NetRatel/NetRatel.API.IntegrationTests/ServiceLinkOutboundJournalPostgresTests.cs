using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
public sealed class ServiceLinkOutboundJournalPostgresTests
{
    [Fact]
    public async Task Concurrent_public_resume_rolls_back_losing_outbound_insert_and_recovers_exact_operation_after_lost_responses_and_restart()
    {
        // This uses actual PostgreSQL and the actual published peer fixture. It
        // depends on the companion Web-discovery repair being published and
        // selected by that fixture; do not bypass the known old metadata 404.
        // Journal transaction proof is separate from full two-product acceptance.
        var barrier = new OutboundInsertBarrier();
        await using var pair = await ServiceLinkPair.CreateAsync(true, barrier);
        await pair.PrepareAsync();
        await pair.ReviewAsync();
        await pair.ApproveAsync();
        await pair.ResumeAsync(pair.Initiator); // Real exchange, before the first outbound ack.
        var prepared = await ReadAttemptAsync(pair);
        Assert.NotNull(prepared.ProtectedOutboundCredential);
        Assert.False(prepared.LocalPreparedAcknowledged);
        Assert.Empty(await ReadJournalAsync(pair));
        var principalId = prepared.InboundPrincipalId;
        barrier.Arm(prepared.LinkId!);
        pair.ResponderProxy.LoseNextCompletedResponse("/ack");
        var path = $"/api/v1/admin/service-links/links/{prepared.LinkId}/resume";
        var firstRequest = pair.Initiator.PostAsJsonAsync(path, new ServiceLinkAdminAction());
        var secondRequest = pair.Initiator.PostAsJsonAsync(path, new ServiceLinkAdminAction());
        try
        {
            await barrier.BothArrived.WaitAsync(TimeSpan.FromSeconds(10));
            var proposed = barrier.Proposals;
            Assert.Equal(2, proposed.Length);
            Assert.NotEqual(proposed[0].ContextId, proposed[1].ContextId);
            Assert.NotEqual(proposed[0].OperationId, proposed[1].OperationId);
            Assert.Equal(prepared.Revision, proposed[0].OriginalAttemptRevision);
            Assert.Equal(prepared.Revision, proposed[1].OriginalAttemptRevision);

            barrier.Release(0);
            var winnerRequest = await Task.WhenAny(firstRequest, secondRequest).WaitAsync(TimeSpan.FromSeconds(25));
            using var firstLost = await winnerRequest;
            Assert.Equal(HttpStatusCode.BadGateway, firstLost.StatusCode);
            Assert.Equal(1, pair.ResponderProxy.LostResponses);
            var pending = Assert.Single(await ReadJournalAsync(pair));
            Assert.Equal(proposed[0].OperationId, pending.OperationId);
            Assert.False(pending.Completed);
            Assert.NotNull(pending.ProtectedRequestJson);
            Assert.Equal(prepared.Revision + 1, (await ReadAttemptAsync(pair)).Revision);

            // The winner's SaveChanges transaction has committed before any
            // peer HTTP. Only now let the stale competing context save. The
            // retry must use the winner's durable operation, not its own ID.
            pair.ResponderProxy.LoseNextCompletedResponse("/ack");
            barrier.Release(1);
            var loserRequest = ReferenceEquals(winnerRequest, firstRequest) ? secondRequest : firstRequest;
            using var secondLost = await loserRequest.WaitAsync(TimeSpan.FromSeconds(25));
            Assert.Equal(HttpStatusCode.BadGateway, secondLost.StatusCode);
            Assert.Equal(2, pair.ResponderProxy.LostResponses);
            Assert.Equal(1, barrier.ConcurrencyFailures);
            Assert.Equal(proposed[1].OperationId, barrier.RolledBackOperationId);
            var afterRetry = Assert.Single(await ReadJournalAsync(pair));
            Assert.Equal(pending.OperationId, afterRetry.OperationId);
            Assert.Equal(pending.RequestFingerprint, afterRetry.RequestFingerprint);
            Assert.True(string.Equals(pending.ProtectedRequestJson, afterRetry.ProtectedRequestJson, StringComparison.Ordinal),
                "Retry replaced the encrypted durable request instead of reusing the winning operation.");
            Assert.False(afterRetry.Completed);
            Assert.False((await ReadAttemptAsync(pair)).LocalPreparedAcknowledged);
            Assert.Matches("^[0-9a-f]{48}$", pending.OperationId);
            Assert.Equal(1, await pair.RatelDesk.CountAsync("ServiceLinkOperations", "NOT \"Outbound\" AND \"Kind\" = 'ack'"));
            Assert.Equal(1, await pair.RatelDesk.CountAsync("ServiceLinkOperations", $"\"OperationId\" = '{pending.OperationId}'"));

            barrier.Disarm();
            await pair.NetRatel.RestartAsync();
            await pair.RatelDesk.RestartAsync();
            using var recovered = await pair.Initiator.PostAsJsonAsync(path, new ServiceLinkAdminAction());
            Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
            var completed = Assert.Single(await ReadJournalAsync(pair));
            Assert.Equal(pending.OperationId, completed.OperationId);
            Assert.Equal(pending.RequestFingerprint, completed.RequestFingerprint);
            Assert.True(completed.Completed);
            Assert.Null(completed.ProtectedRequestJson);
            var final = await ReadAttemptAsync(pair);
            Assert.True(final.LocalPreparedAcknowledged);
            Assert.Equal(principalId, final.InboundPrincipalId);
            Assert.False(final.LocalInboundActive);
            Assert.False(final.LocalBusinessSenderEnabled);
            await using var scope = pair.NetRatel.Services.CreateAsyncScope();
            Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServicePrincipalRegistration>().CountAsync());
            Assert.Equal(1, await pair.RatelDesk.CountAsync("ServiceLinkOperations", "NOT \"Outbound\" AND \"Kind\" = 'ack'"));
            Assert.Equal(1, await pair.RatelDesk.CountAsync("ServicePrincipalRegistrations"));
        }
        finally
        {
            barrier.ReleaseAll();
            // Always drain the real requests before disposing their Kestrel host;
            // a failed assertion must not leave a blocked interceptor behind.
            try
            {
                var responses = await Task.WhenAll(firstRequest, secondRequest).WaitAsync(TimeSpan.FromSeconds(30));
                foreach (var response in responses) response.Dispose();
            }
            catch (Exception error) when (error is HttpRequestException or OperationCanceledException or TimeoutException) { }
        }
    }

    private static async Task<ServiceLinkAttempt> ReadAttemptAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
    }

    private static async Task<ServiceLinkOperation[]> ReadJournalAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkOperation>()
            .AsNoTracking().Where(o => o.Outbound && o.Kind == OutboundInsertBarrier.Kind).ToArrayAsync();
    }

    private sealed class OutboundInsertBarrier : SaveChangesInterceptor
    {
        public const string Kind = "ack-prepared";
        private readonly object sync = new();
        private readonly TaskCompletionSource<bool> arrived = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool>[] releases =
        [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        private readonly List<Proposal> proposals = [];
        private string? linkId;
        private int concurrencyFailures;
        public Task BothArrived => arrived.Task;
        public int ConcurrencyFailures => Volatile.Read(ref concurrencyFailures);
        public string? RolledBackOperationId { get; private set; }
        public Proposal[] Proposals { get { lock (sync) return proposals.ToArray(); } }
        public void Arm(string link) => linkId = link;
        public void Disarm() => linkId = null;
        public void Release(int index) => releases[index].TrySetResult(true);
        public void ReleaseAll() { Disarm(); foreach (var release in releases) release.TrySetResult(true); }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context;
            if (db is null || linkId is null) return result;
            var operation = db.ChangeTracker.Entries<ServiceLinkOperation>()
                .SingleOrDefault(e => e.State == EntityState.Added && e.Entity.Outbound && e.Entity.Kind == Kind && e.Entity.LinkId == linkId);
            if (operation is null) return result;
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            Assert.Null(db.Database.CurrentTransaction);
            var attempt = db.ChangeTracker.Entries<ServiceLinkAttempt>().Single(e => e.Entity.LinkId == linkId);
            int index;
            lock (sync)
            {
                index = proposals.Count;
                if (index >= 2) return result;
                proposals.Add(new(db.ContextId.InstanceId, operation.Entity.OperationId, attempt.Property(a => a.Revision).OriginalValue));
                if (proposals.Count == 2) arrived.TrySetResult(true);
            }
            // SavingChanges intercepts before SQL/automatic transaction start.
            // Both gates are bounded independently of the production HTTP deadline.
            await releases[index].Task.WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
            return result;
        }

        public override ValueTask<InterceptionResult> ThrowingConcurrencyExceptionAsync(
            ConcurrencyExceptionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            var losing = eventData.Context?.ChangeTracker.Entries<ServiceLinkOperation>()
                .SingleOrDefault(e => e.State == EntityState.Added && e.Entity.Outbound && e.Entity.Kind == Kind && e.Entity.LinkId == linkId);
            if (losing is not null)
            {
                RolledBackOperationId = losing.Entity.OperationId;
                Interlocked.Increment(ref concurrencyFailures);
            }
            return ValueTask.FromResult(result); // Observe actual CAS failure; never suppress it.
        }

        public sealed record Proposal(Guid ContextId, string OperationId, long OriginalAttemptRevision);
    }
}
