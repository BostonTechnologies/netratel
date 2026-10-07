using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

// Isolated store regressions; PostgreSQL locks/triggers and published-peer acceptance remain separate checks.
public sealed class FlowReceiverEvidenceTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Returned_fifth_send_ambiguity_completes_run_then_claims_exactly_one_read_only_reconciliation(bool receiptAvailable)
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var action = await db.FlowActions.SingleAsync(row => row.RunId == h.Lease.RunId);
            var evidence = await db.Set<FlowReceiverEvidenceRecord>().SingleAsync(row => row.RunId == h.Lease.RunId);
            // Arrange the durable state immediately after the fifth POST returned an ambiguous response.
            action.Status = FlowActionStatus.Dispatching; action.Attempts = FlowLimits.MaximumActionAttempts;
            evidence.MayHaveCommitted = true; evidence.FirstPostAttemptAtUtc = h.Clock.GetUtcNow();
            evidence.LastPostLeaseFence = h.Lease.Fence;
            await db.SaveChangesAsync();
        }
        var postState = (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!;
        (postState.FinalReconciliationPending).Should().BeTrue();
        var result = ReceiverDispatchPolicy.Complete(postState,
            new(RatelDeskReceiverObservationKind.PossibleCommit, "response-lost"), h.Clock.GetUtcNow());
        (result.Kind).Should().Be(FlowIncidentActionResultKind.RetryableSafe);
        (await h.Store.CompleteActionAsync(h.Lease, h.NodeId, result)).Should().BeTrue();
        (await h.Store.CompleteRunAsync(h.Lease, new(FlowRunStatus.RetryWaiting, "retry-waiting"))).Should().BeTrue();
        ((await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!.Run.Status).Should().Be(FlowRunStatus.RetryWaiting);
        h.Receiver.Response = receiptAvailable ? RatelDeskReceiverObservationKind.Committed : RatelDeskReceiverObservationKind.TransientReadFailure;
        h.Clock.Advance(result.RetryAfter!.Value);
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        var run = (await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!;
        (run.Run.Status).Should().Be(receiptAvailable ? FlowRunStatus.Succeeded : FlowRunStatus.DeliveryUnknown);
        (run.Actions.Single().Attempts).Should().Be(FlowLimits.MaximumActionAttempts);
        (h.Receiver.Lookups).Should().Be(1);
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await using var finalScope = h.Provider.CreateAsyncScope();
        var stored = await finalScope.ServiceProvider.GetRequiredService<OrchestratorDbContext>()
            .Set<FlowReceiverEvidenceRecord>().SingleAsync(row => row.RunId == h.Lease.RunId);
        (stored.FinalReconciliationAttempted).Should().BeTrue();
    }

    [Fact]
    public async Task Authority_revoked_while_receipt_lookup_is_awaited_denies_the_receipt_commit()
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.FlowActions.SingleAsync(row => row.RunId == h.Lease.RunId)).Status = FlowActionStatus.Dispatching;
            (await db.Set<FlowReceiverEvidenceRecord>().SingleAsync(row => row.RunId == h.Lease.RunId)).MayHaveCommitted = true;
            await db.SaveChangesAsync();
        }
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        h.Receiver.BeforeReturn = () => h.Authority.Allowed = false;
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        var run = (await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!;
        (run.Run.Status).Should().Be(FlowRunStatus.DeliveryUnknown);
        (run.Actions.Single().Receipt).Should().BeNull();
        (h.Receiver.Lookups).Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Recovered_fifth_send_can_reconcile_after_disable_but_current_authority_is_still_required(bool authorized)
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var action = await db.FlowActions.SingleAsync(row => row.RunId == h.Lease.RunId);
            var evidence = await db.Set<FlowReceiverEvidenceRecord>().SingleAsync(row => row.RunId == h.Lease.RunId);
            action.Status = FlowActionStatus.Dispatching; action.Attempts = FlowLimits.MaximumActionAttempts;
            evidence.MayHaveCommitted = true; evidence.FirstPostAttemptAtUtc = h.Clock.GetUtcNow();
            (await db.FlowDefinitions.SingleAsync(row => row.Id == h.Lease.Version.FlowId)).Enabled = false;
            await db.SaveChangesAsync();
        }
        h.Authority.Allowed = authorized;
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        var run = (await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!;
        (run.Run.Status).Should().Be(authorized ? FlowRunStatus.Succeeded : FlowRunStatus.DeliveryUnknown);
        (h.Receiver.Lookups).Should().Be(authorized ? 1 : 0);
        (run.Actions.Single().Attempts).Should().Be(FlowLimits.MaximumActionAttempts);
        (run.Actions.Single().IdempotencyKey).Should().Be(h.Draft.IdempotencyKey);
        (run.Actions.Single().Receipt?.IncidentId).Should().Be(authorized ? "incident-test" : null);
    }

    [Fact]
    public async Task Interrupted_atomic_preparation_leaves_neither_V1_prepared_body_nor_V2_sidecar()
    {
        await using var h = await Harness.CreateAsync(prepareAction: false);
        var before = await h.ReadOriginalAsync();
        h.Interruption.FailOneEvidenceSave = true;
        await ((Func<Task>)(() => h.Evidence.SavePreparedActionAsync(h.Lease, h.NodeId,
            FlowTestData.Prepared(h.Draft, safeReplay: true), h.Prepared, default))).Should().ThrowExactlyAsync<DbUpdateException>();
        (await h.ReadOriginalAsync()).Should().Be(before);
        (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default)).Should().BeNull();
        (await h.Evidence.SavePreparedActionAsync(h.Lease, h.NodeId,
            FlowTestData.Prepared(h.Draft, safeReplay: true), h.Prepared, default)).Should().BeTrue();
        ((await h.ReadOriginalAsync()).PreparedJson).Should().NotBeNull();
        (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default)).Should().NotBeNull();
    }

    [Fact]
    public async Task Adding_receiver_evidence_preserves_original_V1_event_draft_prepared_bytes_and_hashes()
    {
        await using var h = await Harness.CreateAsync();
        var before = await h.ReadOriginalAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        (await h.ReadOriginalAsync()).Should().Be(before);
        var read = (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!;
        (read).Should().NotBeNull();
        (read.Prepared.EvidenceFingerprint).Should().Be(h.Prepared.EvidenceFingerprint);
        (read.Prepared.ExactCreateBodyJson).Should().Be(h.Prepared.ExactCreateBodyJson);
        (read.Attempts).Should().Be(0);
        (read.MayHaveCommitted).Should().BeFalse();
    }

    [Fact]
    public async Task A_self_consistent_new_sidecar_cannot_replace_the_original_wire_key_or_body()
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        var before = await h.ReadOriginalAsync();
        foreach (var changed in new[]
        {
            h.Prepared with { ReceiverIdempotencyKey = new string('a', 64) },
            h.Prepared with { ExactCreateBodyJson = "{}" },
            h.Prepared with { Peer = h.Prepared.Peer with { CustomerId = "foreign-customer" } }
        })
        {
            var selfConsistent = changed with { EvidenceFingerprint = ReceiverPreparationBuilder.EvidenceHash(changed) };
            (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, selfConsistent, default)).Should().BeFalse();
        }
        (await h.ReadOriginalAsync()).Should().Be(before);
        ((await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!.Prepared.EvidenceFingerprint).Should().Be(h.Prepared.EvidenceFingerprint);
    }

    [Fact]
    public async Task A_stale_or_cross_tenant_lease_cannot_read_or_replace_receiver_evidence()
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        foreach (var wrong in new[]
        {
            h.Lease with { Token = Guid.NewGuid() },
            h.Lease with { Fence = h.Lease.Fence + 1 },
            h.Lease with { Event = h.Lease.Event with { TenantId = 18 } }
        })
        {
            (await h.Evidence.ReadAsync(wrong, h.NodeId, default)).Should().BeNull();
            (await h.Evidence.SavePreparedAsync(wrong, h.NodeId, h.Prepared, default)).Should().BeFalse();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Original_run_age_or_attempt_budget_preserves_uncertain_retry_as_unknown(bool exhaustedAge)
    {
        await using var h = await Harness.CreateAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.SingleAsync(row => row.Id == h.Lease.RunId);
            var action = await db.FlowActions.SingleAsync(row => row.RunId == h.Lease.RunId);
            var evidence = await db.Set<FlowReceiverEvidenceRecord>().SingleAsync(row => row.RunId == h.Lease.RunId);
            action.Status = FlowActionStatus.RetryWaiting; action.Attempts = 1;
            evidence.MayHaveCommitted = true; evidence.FirstPostAttemptAtUtc = h.Clock.GetUtcNow();
            run.Status = FlowRunStatus.RetryWaiting; run.LeaseExpiresAtUtc = null;
            if (exhaustedAge) h.Clock.Advance(FlowLimits.MaximumRetryAge + TimeSpan.FromSeconds(1));
            else run.Attempts = FlowLimits.MaximumActionAttempts * 2;
            await db.SaveChangesAsync();
        }
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull();
        ((await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!.Run.Status).Should().Be(FlowRunStatus.DeliveryUnknown);
        ((await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!.Actions.Single().Status).Should().Be(FlowActionStatus.DeliveryUnknown);
        h.Clock.Advance(FlowLimits.HistoryRetention + TimeSpan.FromDays(1));
        (await h.Store.PruneHistoryAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Preparation_retry_keeps_original_action_and_consumes_no_post_attempt()
    {
        await using var h = await Harness.CreateAsync(prepareAction: false);
        (await h.Evidence.SchedulePreparationRetryAsync(h.Lease, h.NodeId, TimeSpan.FromSeconds(17), default)).Should().BeTrue();
        var action = (await h.Definitions.GetRunByIdAsync(17, h.Lease.RunId))!.Actions.Single();
        (action.Status).Should().Be(FlowActionStatus.RetryWaiting);
        (action.Attempts).Should().Be(0);
        (action.IdempotencyKey).Should().Be(h.Draft.IdempotencyKey);
        (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default)).Should().BeNull();
        (await h.Evidence.SchedulePreparationRetryAsync(h.Lease, h.NodeId, TimeSpan.FromHours(2), default)).Should().BeFalse();
    }

    private sealed record OriginalBytes(string EventJson, string EventFingerprint, string DraftJson,
        string? PreparedJson, string? SemanticFingerprint, string IdempotencyKey);
    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan age) => _now += age;
    }
    private sealed class PreparationInterruption : SaveChangesInterceptor
    {
        public bool FailOneEvidenceSave { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailOneEvidenceSave && eventData.Context!.ChangeTracker.Entries<FlowReceiverEvidenceRecord>()
                    .Any(entry => entry.State == EntityState.Added))
            {
                FailOneEvidenceSave = false;
                throw new DbUpdateException("injected-before-atomic-preparation-save");
            }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class Authority : IFlowExecutionAuthorityVerifier
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default) => Task.FromResult(Allowed);
    }
    private sealed class SuppressedGuard : IFlowDispatchGuard
    { public Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken ct = default) => Task.FromResult(new FlowDispatchDecision(false, "test-occurrence-cleared")); }
    private sealed class OneActionRuntime : IFlowRuntimeAdapter
    {
        public async Task<FlowRuntimeResult> ExecuteAsync(FlowRunLease lease,
            Func<FlowIncidentActionDraft, CancellationToken, Task<FlowIncidentActionResult>> execute, CancellationToken ct = default)
        {
            var node = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.CreateIncident);
            var mapping = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.MapIncident).Mapping!;
            var draft = new FlowIncidentActionDraft(lease.Event.TenantId, lease.RunId, node.Id, node.ConnectorId!.Value,
                node.ConnectorRevision!.Value, lease.SourceInstanceId, FlowActionKeys.Create(lease, node.Id), lease.Event,
                FlowPureEvaluation.Map(mapping, lease.Event.Data));
            var result = await execute(draft, ct);
            return new(result.Kind == FlowIncidentActionResultKind.Succeeded ? FlowRunStatus.Succeeded : FlowRunStatus.Failed, "test-runtime-completed");
        }
    }
    private sealed class ReceiptReceiver(RatelDeskReceiverPreparationV2 prepared, TimeProvider clock) : IFlowReceiverDispatcher
    {
        public int Lookups { get; private set; }
        public Action? BeforeReturn { get; set; }
        public RatelDeskReceiverObservationKind Response { get; set; } = RatelDeskReceiverObservationKind.Committed;
        public Task<FlowReceiverPreparationResult> PrepareReceiverAsync(FlowIncidentActionDraft draft, DateTimeOffset createdAt, CancellationToken ct) => throw new NotSupportedException();
        public Task<RatelDeskReceiverObservation> DispatchReceiverAsync(FlowRunLease lease, Guid nodeId,
            RatelDeskDispatchEvidence evidence, Func<CancellationToken, Task<FlowDispatchDecision>> recheckGuard, CancellationToken ct)
        {
            Lookups++; (evidence.MayHaveCommitted).Should().BeTrue();
            (evidence.Prepared.EvidenceFingerprint).Should().Be(prepared.EvidenceFingerprint);
            if (Response != RatelDeskReceiverObservationKind.Committed)
                return Task.FromResult(new RatelDeskReceiverObservation(Response, "test-lookup-unavailable"));
            var location = "/help/api/v1/incidents/incident-test";
            var body = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = "incident-test", trackingId = "INC-test", organizationId = prepared.Peer.OrganizationId, customerId = prepared.Peer.CustomerId,
                integrationReceipt = new
                {
                    contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = prepared.Peer.ReceiverInstanceId,
                    sourceNamespaceId = prepared.Peer.SourceNamespaceId.ToString("D"), sourceInstanceId = prepared.Peer.SourceInstanceId.ToString("D"),
                    key = prepared.ReceiverIdempotencyKey, fingerprint = prepared.ReceiverFingerprint, outcome = "committed",
                    incidentId = "incident-test", trackingId = "INC-test", organizationId = prepared.Peer.OrganizationId,
                    customerId = prepared.Peer.CustomerId, committedAtUtc = clock.GetUtcNow(), location
                }
            });
            var receipt = ReceiverWireValidation.Receipt(body, location, evidence.Prepared);
            BeforeReturn?.Invoke();
            return Task.FromResult(new RatelDeskReceiverObservation(RatelDeskReceiverObservationKind.Committed, "test-verified", receipt));
        }
    }
    private sealed class Harness : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public required ServiceProvider Provider { get; init; }
        public required FlowRunLease Lease { get; init; }
        public required FlowIncidentActionDraft Draft { get; init; }
        public required RatelDeskReceiverPreparationV2 Prepared { get; init; }
        public required Clock Clock { get; init; }
        public required PreparationInterruption Interruption { get; init; }
        public required Authority Authority { get; init; }
        public required ReceiptReceiver Receiver { get; init; }
        public Guid NodeId => Draft.ActionNodeId;
        public IFlowExecutionStore Store => Provider.GetRequiredService<FlowPersistenceService>();
        public IFlowDefinitionService Definitions => Provider.GetRequiredService<FlowPersistenceService>();
        public IFlowReceiverEvidenceStore Evidence => Provider.GetRequiredService<FlowPersistenceService>();

        public static async Task<Harness> CreateAsync(bool prepareAction = true)
        {
            var lease = FlowTestData.Lease("receiver-evidence", 91); var clock = new Clock(lease.Event.OccurredAtUtc);
            var node = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.CreateIncident);
            var map = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.MapIncident).Mapping!;
            var draft = new FlowIncidentActionDraft(17, lease.RunId, node.Id, node.ConnectorId!.Value, node.ConnectorRevision!.Value,
                lease.SourceInstanceId, FlowActionKeys.Create(lease, node.Id), lease.Event, FlowPureEvaluation.Map(map, lease.Event.Data));
            var action = FlowTestData.Prepared(draft, safeReplay: true);
            var peer = new RatelDeskSemanticPeer(RatelDeskAuthenticationMode.ManualApiBearer, 17, draft.ConnectorId,
                null, null, null, Guid.NewGuid().ToString("D"), "organization-17", "https://api.example.test/help",
                null, null, null, null, null, lease.SourceInstanceId, Guid.NewGuid(), "organization-17", "customer-17", null, []);
            var capability = new RatelDeskVerifiedCapability(ReceiverWireValidation.Contract, peer.ReceiverInstanceId,
                peer.SourceInstanceId, peer.SourceNamespaceId,
                new(ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CapabilitiesPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CreatePath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.ReceiptPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.TargetsPath)),
                ReceiverWireValidation.MinimumRetention, ReceiverWireValidation.MaximumReplay, clock.GetUtcNow());
            var fingerprint = new RatelDeskReceiverFingerprint();
            var prepared = new ReceiverPreparationBuilder(fingerprint).Build(draft, action, peer, capability, clock.GetUtcNow(), null);
            var services = new ServiceCollection(); services.AddLogging();
            var databaseName = "receiver-evidence-" + Guid.NewGuid().ToString("N");
            var interruption = new PreparationInterruption();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName).AddInterceptors(interruption));
            services.AddSingleton<TimeProvider>(clock); services.AddSingleton<FlowPersistenceService>();
            services.AddSingleton<IRatelDeskReceiverFingerprint>(fingerprint);
            services.AddSingleton<IFlowExecutionStore>(provider => provider.GetRequiredService<FlowPersistenceService>());
            services.AddSingleton<IFlowDefinitionService>(provider => provider.GetRequiredService<FlowPersistenceService>());
            services.AddSingleton<IFlowReceiverEvidenceStore>(provider => provider.GetRequiredService<FlowPersistenceService>());
            var authority = new Authority(); var receiver = new ReceiptReceiver(prepared, clock);
            services.AddSingleton<IFlowExecutionAuthorityVerifier>(authority); services.AddSingleton<IFlowDispatchGuard, SuppressedGuard>();
            services.AddSingleton<IFlowReceiverDispatcher>(receiver); services.AddSingleton<IFlowRuntimeAdapter, OneActionRuntime>();
            services.AddSingleton<FlowRunProcessor>();
            var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            var h = new Harness { Provider = provider, Lease = lease, Draft = draft, Prepared = prepared, Clock = clock,
                Interruption = interruption, Authority = authority, Receiver = receiver };
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                db.Tenants.Add(new Tenant { Id = 17, Name = "Evidence fixture" });
                db.FlowRuntimeIdentity.Add(new() { SourceInstanceId = lease.SourceInstanceId });
                db.FlowDefinitions.Add(new() { Id = lease.Version.FlowId, TenantId = 17, Name = "Evidence fixture", Enabled = true,
                    Revision = 1, DraftJson = JsonSerializer.Serialize(lease.Version.Graph, Json) });
                db.FlowVersions.Add(new() { Id = lease.Version.Id, FlowId = lease.Version.FlowId, TenantId = 17, VersionNumber = 1,
                    GraphJson = JsonSerializer.Serialize(lease.Version.Graph, Json), ConfigurationHash = lease.Version.ConfigurationHash,
                    PublishedBy = lease.Event.Authority.PrincipalId, PublishedAtUtc = clock.GetUtcNow() });
                db.FlowRuns.Add(new() { Id = lease.RunId, FlowId = lease.Version.FlowId, FlowVersionId = lease.Version.Id, TenantId = 17,
                    EventId = lease.Event.EventId, OccurrenceId = lease.Event.OccurrenceId, EventJson = JsonSerializer.Serialize(lease.Event, Json),
                    EventFingerprint = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(lease.Event, Json)),
                    Status = FlowRunStatus.Running, Fence = lease.Fence, LeaseToken = lease.Token, LeaseOwner = lease.WorkerId,
                    LeaseExpiresAtUtc = lease.ExpiresAtUtc, CreatedAtUtc = clock.GetUtcNow(), Attempts = lease.Attempts });
                await db.SaveChangesAsync();
            }
            (await h.Store.GetOrCreateActionAsync(lease, draft)).Should().NotBeNull();
            if (prepareAction) (await h.Store.SavePreparedActionAsync(lease, node.Id, action)).Should().BeTrue();
            return h;
        }
        public async Task<OriginalBytes> ReadOriginalAsync()
        {
            await using var scope = Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == Lease.RunId);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(row => row.RunId == Lease.RunId);
            return new(run.EventJson, run.EventFingerprint, action.DraftJson, action.PreparedJson, action.SemanticFingerprint, action.IdempotencyKey);
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
