using System.Data;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Infrastructure.ServiceLinks;
using NetRatel.Shared.ServiceLinks;
using Npgsql;
using Xunit;

namespace NetRatel.API.IntegrationTests.ServiceLinks;

[Collection(ServiceLinkRealPeerCollection.Name)]
[Trait("category", "manual-integration")]
public sealed class ServiceLinkDatabaseConflictPostgresTests
{
    [Fact]
    public async Task Serializable_responder_consent_returns_conflict_and_rolls_back_provisioning_before_fresh_request_retry()
    {
        // Only the competing PostgreSQL transaction is injected. The error is
        // produced by PostgreSQL/EF and passes through the actual HTTP endpoint.
        var contention = new SerializableConsentContention();
        await using var pair = await ServiceLinkPair.CreateAsync(false, contention);
        await pair.PrepareAsync();
        var before = await ReadAttemptAsync(pair);
        var state = QueryHelpers.ParseQuery(new Uri(pair.Start.NavigationUrl).Query)["browser_state"].ToString();
        var request = new ServiceLinkRemoteApproveRequest(pair.Start.AttemptId, pair.NetRatel.TenantId,
            pair.Descriptor.RequestedGrants, state) { SessionBinding = pair.SessionBinding };
        contention.Arm(pair.Start.AttemptId);

        using (var rejected = await pair.NetRatel.Administrator.PostAsJsonAsync("/api/v1/admin/service-links/remote-approve", request))
        {
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
            using var problem = await rejected.Content.ReadFromJsonAsync<JsonDocument>();
            Assert.Equal("service-link-conflict", problem!.RootElement.GetProperty("code").GetString());
            Assert.True(rejected.Headers.CacheControl?.NoStore == true);
        }
        var failure = Assert.IsType<InvalidOperationException>(contention.Failure);
        var update = Assert.IsType<DbUpdateException>(failure.InnerException);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(update.InnerException).SqlState);
        Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(failure));
        Assert.Equal(1, contention.ProvisioningSaves); // No automatic action/HTTP retry.
        var rolledBack = await ReadAttemptAsync(pair);
        Assert.Equal(before.Revision + 1, rolledBack.Revision); // Only the independent writer committed.
        Assert.Equal(before.LifecycleState, rolledBack.LifecycleState);
        Assert.Equal(before.Decision, rolledBack.Decision);
        Assert.Equal(before.DescriptorHash, rolledBack.DescriptorHash);
        Assert.Equal(before.SessionBindingHash, rolledBack.SessionBindingHash);
        Assert.True(string.Equals(before.ProtectedBrowserState, rolledBack.ProtectedBrowserState, StringComparison.Ordinal),
            "Failed consent changed the retained protected browser state.");
        Assert.Null(rolledBack.LinkId);
        Assert.Null(rolledBack.GrantSummaryJson);
        Assert.Null(rolledBack.GrantHash);
        Assert.Null(rolledBack.ConsentId);
        Assert.Null(rolledBack.InboundPrincipalId);
        Assert.True(rolledBack.ProtectedInboundEscrow is null, "Failed consent retained protected inbound escrow.");
        Assert.False(rolledBack.LocalInboundActive);
        Assert.False(rolledBack.LocalBusinessSenderEnabled);
        await AssertAuthorityCountsAsync(pair, 0);

        // A new HTTP scope reruns the exact, still-authorized consent request.
        // It must not save the failed context's pending principal or ciphertext.
        using (var retried = await pair.NetRatel.Administrator.PostAsJsonAsync("/api/v1/admin/service-links/remote-approve", request))
            Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
        Assert.Equal(2, contention.ProvisioningSaves);
        Assert.Equal(2, contention.ContextIds.Distinct().Count());
        var approved = await ReadAttemptAsync(pair);
        Assert.Equal("approved", approved.LifecycleState);
        Assert.NotNull(approved.LinkId);
        Assert.NotNull(approved.GrantHash);
        Assert.NotNull(approved.InboundPrincipalId);
        Assert.False(approved.LocalInboundActive);
        Assert.False(approved.LocalBusinessSenderEnabled);
        await AssertAuthorityCountsAsync(pair, 1);
    }

    [Fact]
    public async Task Serializable_recipient_retries_keep_exact_exchange_and_operation_identity_and_commit_one_verification_receipt()
    {
        var contention = new SerializableConsentContention();
        await using var pair = await ServiceLinkPair.CreateAsync(false, contention);
        await pair.PrepareAsync();
        await pair.ReviewAsync();
        var beforeExchange = await ReadAttemptAsync(pair);
        var exchangePath = ServiceLinkContract.EndpointPath + $"/attempts/{pair.Start.AttemptId}/exchange";
        var observation = pair.NetRatel.Proxy.ObserveNextSuccessfulOperation(exchangePath, 131_072);
        contention.ArmExchange(pair.Start.AttemptId);
        await pair.ApproveAsync();
        await pair.ResumeAsync(pair.Initiator);
        using var accepted = await observation.WaitAsync(TimeSpan.FromSeconds(25));
        AssertObservedSerializationFailure(contention);
        Assert.Equal(2, contention.ProvisioningSaves);
        Assert.Equal(2, contention.ContextIds.Distinct().Count());
        var exchange = accepted.ReadRequest<ServiceLinkExchangeRequest>();
        var exchanged = await ReadAttemptAsync(pair);
        Assert.True(exchanged.ExchangeFingerprint == ServiceLinkCanonicalJson.HashObject(ServiceLinkPayloadNormalization.Exchange(exchange)),
            "Recipient retries changed the accepted exchange fingerprint.");
        Assert.Equal(beforeExchange.InboundPrincipalId, exchanged.InboundPrincipalId);
        Assert.NotNull(exchanged.ProtectedExchangeResponse);
        Assert.False(exchanged.LocalInboundActive);
        Assert.False(exchanged.LocalBusinessSenderEnabled);
        await AssertAuthorityCountsAsync(pair, 1);

        // Retrying the same real exchange uses the retained committed response.
        using (var replay = await pair.NetRatel.Anonymous.PostAsJsonAsync(exchangePath, exchange))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(accepted.ResponseMatches(await replay.Content.ReadAsStringAsync()),
                "The accepted exchange replay changed its finite response.");
        }
        Assert.Equal(2, contention.ProvisioningSaves);
        await pair.FinishAsync();
        await pair.NetRatel.WaitForInitialSensitiveWindowAsync();
        var token = await pair.TokenAsync(true, ServiceLinkContract.VerifyScope);
        ServicePrincipalRegistration principal;
        int receiptsBefore, journalsBefore;
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            principal = await db.Set<ServicePrincipalRegistration>().AsNoTracking().SingleAsync();
            receiptsBefore = await db.Set<ServiceLinkVerificationReceipt>().CountAsync();
            journalsBefore = await db.Set<ServiceLinkOperation>().CountAsync();
        }
        var active = await ReadAttemptAsync(pair);
        var request = new ServiceLinkLifecycleRequest
        {
            OperationId = ServiceLinkValidation.NewId(), AttemptId = active.AttemptId,
            LinkId = active.LinkId!, LinkRevision = active.LinkRevision, GrantHash = active.GrantHash!,
            DirectionId = principal.DirectionId, CredentialRevision = principal.CurrentCredentialRevision
        };
        var verifyPath = ServiceLinkContract.EndpointPath + $"/links/{active.LinkId}/verify";
        contention.ArmVerification(pair.Start.AttemptId);
        string completedResponse;
        using (var message = new HttpRequestMessage(HttpMethod.Post, verifyPath))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Content = JsonContent.Create(ServiceLinkLifecycleProjection.Build("verify", request), options: ServiceLinkCanonicalJson.Json);
            using var response = await pair.NetRatel.Anonymous.SendAsync(message);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            completedResponse = await response.Content.ReadAsStringAsync();
        }
        AssertObservedSerializationFailure(contention);
        Assert.Equal(2, contention.ProvisioningSaves);
        Assert.Equal(2, contention.ContextIds.Distinct().Count());
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(1, await db.Set<ServicePrincipalRegistration>().CountAsync());
            Assert.Equal(1, await db.Set<ServicePrincipalSecret>().CountAsync());
            Assert.Equal(receiptsBefore + 1, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
            Assert.Equal(journalsBefore + 1, await db.Set<ServiceLinkOperation>().CountAsync());
            var operation = await db.Set<ServiceLinkOperation>().AsNoTracking().SingleAsync(o => o.OperationId == request.OperationId);
            Assert.Equal(request.LinkId, operation.LinkId);
            Assert.Equal("verify", operation.Kind);
            Assert.False(operation.Outbound);
            Assert.True(operation.Completed);
            Assert.Equal(ServiceLinkLifecycleProjection.Hash("verify", request), operation.RequestFingerprint);
            Assert.True(string.Equals(completedResponse, operation.ResponseJson, StringComparison.Ordinal),
                "The recipient response differs from its exact durable operation journal.");
        }
        using (var message = new HttpRequestMessage(HttpMethod.Post, verifyPath))
        {
            message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            message.Content = JsonContent.Create(ServiceLinkLifecycleProjection.Build("verify", request), options: ServiceLinkCanonicalJson.Json);
            using var replay = await pair.NetRatel.Anonymous.SendAsync(message);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.True(ServiceLinkCanonicalJson.Canonicalize(completedResponse) ==
                ServiceLinkCanonicalJson.Canonicalize(await replay.Content.ReadAsStringAsync()),
                "The same committed verification operation replay changed its response.");
        }
        Assert.Equal(2, contention.ProvisioningSaves);
        await using (var scope = pair.NetRatel.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(receiptsBefore + 1, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
            Assert.Equal(journalsBefore + 1, await db.Set<ServiceLinkOperation>().CountAsync());
        }
    }

    private static void AssertObservedSerializationFailure(SerializableConsentContention contention)
    {
        var failure = Assert.IsType<InvalidOperationException>(contention.Failure);
        var update = Assert.IsType<DbUpdateException>(failure.InnerException);
        Assert.Equal(PostgresErrorCodes.SerializationFailure, Assert.IsType<PostgresException>(update.InnerException).SqlState);
        Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(failure));
    }

    [Fact]
    public void Aborted_transaction_classification_is_limited_to_known_provider_states_and_known_wrapper_depth()
    {
        static PostgresException Provider(string state) => new("Synthetic classification input.", "ERROR", "ERROR", state);
        foreach (var state in new[] { PostgresErrorCodes.SerializationFailure, PostgresErrorCodes.DeadlockDetected })
        {
            var provider = Provider(state);
            Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(provider));
            Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(new DbUpdateException("Synthetic EF wrapper.", provider)));
            Assert.True(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Synthetic execution wrapper.",
                new DbUpdateException("Synthetic EF wrapper.", provider))));
            Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new HttpRequestException("Unrelated transport wrapper.", provider)));
            Exception tooDeep = provider;
            for (var depth = 0; depth < 4; depth++) tooDeep = new InvalidOperationException("Synthetic over-depth wrapper.", tooDeep);
            Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(tooDeep));
        }
        foreach (var state in new[] { PostgresErrorCodes.UniqueViolation, PostgresErrorCodes.ForeignKeyViolation,
                     PostgresErrorCodes.QueryCanceled, PostgresErrorCodes.AdminShutdown })
        {
            Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(Provider(state)));
            Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Synthetic execution wrapper.",
                new DbUpdateException("Synthetic EF wrapper.", Provider(state)))));
        }
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new InvalidOperationException("Unrelated application failure.")));
        Assert.False(ServiceLinkDatabaseConflict.IsAbortedTransaction(new DbUpdateConcurrencyException("Existing optimistic conflict is separate.")));
    }

    private static async Task<ServiceLinkAttempt> ReadAttemptAsync(ServiceLinkPair pair)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<ServiceLinkAttempt>()
            .AsNoTracking().SingleAsync(a => a.AttemptId == pair.Start.AttemptId);
    }

    private static async Task AssertAuthorityCountsAsync(ServiceLinkPair pair, int expected)
    {
        await using var scope = pair.NetRatel.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(expected, await db.Set<ServicePrincipalRegistration>().CountAsync());
        Assert.Equal(expected, await db.Set<ServicePrincipalSecret>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkOperation>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkVerificationReceipt>().CountAsync());
        Assert.Equal(0, await db.Set<ServiceLinkRotation>().CountAsync());
    }

    private sealed class SerializableConsentContention : SaveChangesInterceptor
    {
        private string? attemptId;
        private int contentionPending;
        private string mode = "consent";
        private int provisioningSaves;
        private readonly System.Collections.Concurrent.ConcurrentQueue<Guid> contextIds = new();
        public Exception? Failure { get; private set; }
        public int ProvisioningSaves => Volatile.Read(ref provisioningSaves);
        public Guid[] ContextIds => contextIds.ToArray();
        public void Arm(string id) => Arm(id, "consent");
        public void ArmExchange(string id) => Arm(id, "exchange");
        public void ArmVerification(string id) => Arm(id, "verification");
        private void Arm(string id, string nextMode)
        {
            attemptId = id; mode = nextMode; Failure = null;
            contextIds.Clear(); Volatile.Write(ref provisioningSaves, 0);
            Volatile.Write(ref contentionPending, 1);
        }
        private bool Eligible(DbContext db) => mode switch
        {
            "consent" => db.ChangeTracker.Entries<ServicePrincipalRegistration>()
                .Any(e => e.State == EntityState.Added && e.Entity.AttemptId == attemptId),
            "exchange" => db.ChangeTracker.Entries<ServiceLinkAttempt>()
                .Any(e => e.Entity.AttemptId == attemptId && e.Entity.ExchangeFingerprint is not null &&
                    e.Property(a => a.ExchangeFingerprint).OriginalValue is null),
            "verification" => db.ChangeTracker.Entries<ServiceLinkVerificationReceipt>()
                .Any(e => e.State == EntityState.Added && e.Entity.AttemptId == attemptId),
            _ => false
        };

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var db = eventData.Context;
            if (db is null || attemptId is null || !Eligible(db)) return result;
            contextIds.Enqueue(db.ContextId.InstanceId);
            Interlocked.Increment(ref provisioningSaves);
            if (Interlocked.Exchange(ref contentionPending, 0) == 0) return result;
            Assert.Equal("Npgsql.EntityFrameworkCore.PostgreSQL", db.Database.ProviderName);
            var transaction = Assert.IsType<NpgsqlTransaction>(db.Database.CurrentTransaction!.GetDbTransaction());
            Assert.Equal(IsolationLevel.Serializable, transaction.IsolationLevel);
            await using (var snapshot = new NpgsqlCommand("SELECT \"Revision\" FROM \"ServiceLinkAttempts\" WHERE \"AttemptId\" = @attempt",
                (NpgsqlConnection)db.Database.GetDbConnection(), transaction) { CommandTimeout = 5 })
            {
                snapshot.Parameters.AddWithValue("attempt", attemptId);
                Assert.NotNull(await snapshot.ExecuteScalarAsync(cancellationToken));
            }
            await using var competing = new NpgsqlConnection(db.Database.GetConnectionString());
            await competing.OpenAsync(cancellationToken);
            await using var update = new NpgsqlCommand(
                "UPDATE \"ServiceLinkAttempts\" SET \"Revision\" = \"Revision\" + 1 WHERE \"AttemptId\" = @attempt", competing)
                { CommandTimeout = 5 };
            update.Parameters.AddWithValue("attempt", attemptId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync(cancellationToken));
            return result; // Never synthesize, catch, suppress or replace the real provider failure.
        }

        public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
        {
            if (eventData.Context is { } db && Eligible(db))
                Failure = eventData.Exception;
            return Task.CompletedTask;
        }
    }
}
