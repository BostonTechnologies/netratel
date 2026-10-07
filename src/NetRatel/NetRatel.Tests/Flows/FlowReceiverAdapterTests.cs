using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Application.RatelDesk;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Flows;

// Isolated component regressions. These doubles do not establish published-peer or physical acceptance.
public sealed class FlowReceiverAdapterTests
{
    [Fact]
    public async Task Authority_revoked_during_lookup_does_not_accept_the_returned_committed_receipt()
    {
        var h = new Harness(); h.Transport.Lookup = new(RatelDeskReceiverObservationKind.Committed, "verified", h.Receipt);
        h.Transport.BeforeLookupReturn = () => h.Authority.EventAllowed = false;
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId,
            h.Evidence with { MayHaveCommitted = true }, h.Admit, default);
        (result.Kind).Should().Be(RatelDeskReceiverObservationKind.AuthenticationRejected);
        (result.Receipt).Should().BeNull();
        (h.Transport.Operations).Should().Equal(new[] { "lookup" });
        (h.Store.MarkCalls).Should().Be(0);
    }

    [Fact]
    public async Task Fifth_ambiguous_send_reconciles_original_receipt_before_capability_or_target_checks()
    {
        var h = new Harness(); h.Transport.Lookup = new(RatelDeskReceiverObservationKind.Committed, "verified", h.Receipt);
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId,
            h.Evidence with { MayHaveCommitted = true, Attempts = FlowLimits.MaximumActionAttempts }, h.Admit, default);
        (result.Kind).Should().Be(RatelDeskReceiverObservationKind.Committed);
        (result.Receipt).Should().BeSameAs(h.Receipt);
        (h.Transport.Operations).Should().Equal(new[] { "lookup" });
        (h.Store.MarkCalls).Should().Be(0);
    }

    [Fact]
    public async Task Missing_receipt_at_fifth_send_cap_does_not_permit_sixth_post()
    {
        var h = new Harness(); h.Transport.Lookup = new(RatelDeskReceiverObservationKind.Missing, "missing");
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId,
            h.Evidence with { MayHaveCommitted = true, Attempts = FlowLimits.MaximumActionAttempts }, h.Admit, default);
        (result.Kind).Should().Be(RatelDeskReceiverObservationKind.Gone);
        (h.Transport.Operations).Should().Equal(new[] { "lookup" });
        (h.Store.MarkCalls).Should().Be(0);
    }

    [Theory]
    [InlineData(RatelDeskReceiverObservationKind.TransientReadFailure)]
    [InlineData(RatelDeskReceiverObservationKind.RateLimited)]
    [InlineData(RatelDeskReceiverObservationKind.Gone)]
    [InlineData(RatelDeskReceiverObservationKind.AuthenticationRejected)]
    public async Task An_unresolved_prior_send_does_not_post_without_a_verified_missing_lookup(RatelDeskReceiverObservationKind lookup)
    {
        var h = new Harness(); h.Transport.Lookup = new(lookup, "read-unavailable");
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId,
            h.Evidence with { MayHaveCommitted = true }, h.Admit, default);
        (result.Kind).Should().Be(lookup);
        (h.Transport.Operations).Should().Equal(new[] { "lookup" });
        (h.Store.MarkCalls).Should().Be(0);
    }

    [Fact]
    public async Task Replay_keeps_exact_original_key_and_body_and_commits_uncertainty_before_http()
    {
        var h = new Harness(); h.Transport.Lookup = new(RatelDeskReceiverObservationKind.Missing, "missing");
        h.Transport.BeforeCreate = prepared =>
        {
            (h.Store.Marked).Should().BeTrue();
            (h.Store.MarkCalls).Should().Be(1);
            (prepared).Should().BeSameAs(h.Evidence.Prepared);
            (prepared.ReceiverIdempotencyKey).Should().Be(h.Evidence.Prepared.ReceiverIdempotencyKey);
            (prepared.ExactCreateBodyJson).Should().Be(h.Evidence.Prepared.ExactCreateBodyJson);
        };
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId,
            h.Evidence with { MayHaveCommitted = true }, h.Admit, default);
        (result.Kind).Should().Be(RatelDeskReceiverObservationKind.PossibleCommit);
        (h.Transport.Operations).Should().Equal(new[] { "lookup", "capability", "targets", "create" });
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, true)]
    public async Task Current_guard_can_stop_post_before_or_after_durable_marker(int deniedGate, bool markerCommitted)
    {
        var h = new Harness(); h.DenyAtGate = deniedGate;
        var result = await h.Adapter.DispatchReceiverAsync(h.Lease, h.NodeId, h.Evidence, h.Admit, default);
        (result.Kind).Should().Be(RatelDeskReceiverObservationKind.AuthenticationRejected);
        (h.Store.Marked).Should().Be(markerCommitted);
        (h.Transport.Operations).Should().NotContain("create");
        var classified = ReceiverDispatchPolicy.Complete(h.Evidence with { MayHaveCommitted = h.Store.Marked }, result, h.Clock.GetUtcNow());
        (classified.Kind).Should().Be(markerCommitted ? FlowIncidentActionResultKind.DeliveryUnknown : FlowIncidentActionResultKind.Failed);
    }

    [Fact]
    public async Task Read_only_preparation_rate_limit_schedules_retry_without_marking_a_post()
    {
        var h = new Harness(); h.Transport.CapabilityError = new RatelDeskReceiverReadException("receiver-rate-limited", 429, TimeSpan.FromSeconds(17));
        var result = await h.Adapter.PrepareReceiverAsync(h.Draft, h.Clock.GetUtcNow(), default);
        (result.Status).Should().Be(FlowIncidentPreparationStatus.Unavailable);
        (result.RetryAfter).Should().Be(TimeSpan.FromSeconds(17));
        (result.Action).Should().BeNull(); (result.Evidence).Should().BeNull();
        (h.Store.MarkCalls).Should().Be(0);
        (h.Transport.Operations).Should().Equal(new[] { "capability" });
    }

    private sealed class Harness
    {
        public FlowRunLease Lease { get; } = FlowTestData.Lease("receiver-test", 90);
        public Guid NodeId => Draft.ActionNodeId;
        public FlowIncidentActionDraft Draft { get; }
        public RatelDeskDispatchEvidence Evidence { get; }
        public RatelDeskVerifiedReceipt Receipt { get; }
        public FixedClock Clock { get; }
        public MarkerStore Store { get; } = new();
        public Authorization Authority { get; } = new();
        public Transport Transport { get; }
        public RatelDeskFlowReceiverAdapter Adapter { get; }
        public int DenyAtGate { get; set; }
        private int _gates;

        public Harness()
        {
            Clock = new(Lease.Event.OccurredAtUtc);
            var node = Lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.CreateIncident);
            var mapping = Lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.MapIncident).Mapping!;
            Draft = new(Lease.Event.TenantId, Lease.RunId, node.Id, node.ConnectorId!.Value, node.ConnectorRevision!.Value,
                Lease.SourceInstanceId, FlowActionKeys.Create(Lease, node.Id), Lease.Event, FlowPureEvaluation.Map(mapping, Lease.Event.Data));
            var action = FlowTestData.Prepared(Draft, safeReplay: true);
            var peer = new RatelDeskSemanticPeer(RatelDeskAuthenticationMode.ManualApiBearer, 17, Draft.ConnectorId,
                null, null, null, Guid.NewGuid().ToString("D"), "organization-17", "https://api.example.test/help",
                null, null, null, null, null, Draft.SourceInstanceId, Guid.NewGuid(), "organization-17", "customer-17", null, []);
            var capability = new RatelDeskVerifiedCapability(ReceiverWireValidation.Contract, peer.ReceiverInstanceId,
                peer.SourceInstanceId, peer.SourceNamespaceId,
                new(ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CapabilitiesPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CreatePath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.ReceiptPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.TargetsPath)),
                ReceiverWireValidation.MinimumRetention, ReceiverWireValidation.MaximumReplay, Clock.GetUtcNow());
            var builder = new ReceiverPreparationBuilder(new RatelDeskReceiverFingerprint());
            var prepared = builder.Build(Draft, action, peer, capability, Clock.GetUtcNow(), null);
            Evidence = new(prepared, false, 1, null);
            Receipt = new(ReceiverWireValidation.Contract, peer.ReceiverInstanceId, peer.SourceNamespaceId, peer.SourceInstanceId,
                prepared.ReceiverIdempotencyKey, prepared.ReceiverFingerprint, "committed", "incident-test", "INC-test",
                peer.OrganizationId, peer.CustomerId, Clock.GetUtcNow(), "/help/api/v1/incidents/incident-test", "{}");
            var connector = new RatelDeskConnectorState(Draft.ConnectorId, 17, 1, 1, "test-owner",
                new("Test", peer.ApiBaseUrl, peer.OrganizationId, peer.CustomerId, null, [], new(), true), "protected-unit-value", 1);
            Transport = new(capability);
            Adapter = new(new ConnectorStore(connector), new BindingStore(), Authority,
                new Credentials(peer), Transport, builder, Store, Clock);
        }
        public Task<FlowDispatchDecision> Admit(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var denied = ++_gates == DenyAtGate;
            return Task.FromResult(new FlowDispatchDecision(!denied, denied ? "current-test-denial" : "current-test-allow"));
        }
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class ConnectorStore(RatelDeskConnectorState connector) : IRatelDeskConnectorStore
    {
        public Task<RatelDeskConnectorState?> GetAsync(int tenantId, Guid id, CancellationToken ct) => Task.FromResult<RatelDeskConnectorState?>(
            tenantId == connector.TenantId && id == connector.Id ? connector : null);
        public Task<IReadOnlyList<RatelDeskConnectorState>> ListAsync(int tenantId, CancellationToken ct) => Task.FromResult<IReadOnlyList<RatelDeskConnectorState>>([connector]);
        public Task<bool> SaveAsync(RatelDeskConnectorState state, long expected, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class BindingStore : IRatelDeskConnectorBindingStore
    { public Task<RatelDeskConnectorAuthentication> GetAuthenticationAsync(int tenantId, Guid id, CancellationToken ct) => Task.FromResult(new RatelDeskConnectorAuthentication(RatelDeskAuthenticationMode.ManualApiBearer, null)); }
    private sealed class Authorization : IRatelDeskConnectorAuthorization
    {
        public bool EventAllowed { get; set; } = true;
        public Task<bool> CanManageAsync(System.Security.Claims.ClaimsPrincipal actor, int tenantId, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> CanExecuteAsync(string principalId, string? credentialId, int tenantId, CancellationToken ct) => Task.FromResult(principalId == "test-owner" || EventAllowed);
    }
    private sealed class Credentials(RatelDeskSemanticPeer peer) : IRatelDeskOutboundBindingResolver
    {
        public Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState connector, RatelDeskConnectorAuthentication authentication, Guid source, CancellationToken ct) => Task.FromResult(peer);
        public Task<string> GetBearerAsync(RatelDeskConnectorState current, RatelDeskSemanticPeer captured, string scope, CancellationToken ct) => Task.FromResult("unit-only-bearer");
    }
    private sealed class Transport(RatelDeskVerifiedCapability capability) : IRatelDeskReceiverTransport
    {
        public List<string> Operations { get; } = [];
        public RatelDeskReceiverObservation Lookup { get; set; } = new(RatelDeskReceiverObservationKind.Missing, "missing");
        public Action<RatelDeskReceiverPreparationV2>? BeforeCreate { get; set; }
        public Action? BeforeLookupReturn { get; set; }
        public Exception? CapabilityError { get; set; }
        public Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer, string bearer, CancellationToken ct)
        { Operations.Add("capability"); return CapabilityError is null ? Task.FromResult(capability) : Task.FromException<RatelDeskVerifiedCapability>(CapabilityError); }
        public Task ValidateTargetsAsync(RatelDeskSemanticPeer peer, RatelDeskVerifiedCapability current, string bearer, CancellationToken ct)
        { Operations.Add("targets"); return Task.CompletedTask; }
        public Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { Operations.Add("lookup"); BeforeLookupReturn?.Invoke(); return Task.FromResult(Lookup); }
        public Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        { Operations.Add("create"); BeforeCreate?.Invoke(prepared); return Task.FromResult(new RatelDeskReceiverObservation(RatelDeskReceiverObservationKind.PossibleCommit, "response-lost")); }
    }
    private sealed class MarkerStore : IFlowReceiverEvidenceStore
    {
        public bool Marked { get; private set; }
        public int MarkCalls { get; private set; }
        public Task<bool> MarkPostAttemptAsync(FlowRunLease lease, Guid node, CancellationToken ct)
        { MarkCalls++; Marked = true; return Task.FromResult(true); }
        public Task<DateTimeOffset?> GetOriginalCreatedAtAsync(FlowRunLease lease, CancellationToken ct) => throw new NotSupportedException();
        public Task<RatelDeskDispatchEvidence?> ReadAsync(FlowRunLease lease, Guid node, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SavePreparedAsync(FlowRunLease lease, Guid node, RatelDeskReceiverPreparationV2 evidence, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SavePreparedActionAsync(FlowRunLease lease, Guid node, FlowIncidentActionRequest action, RatelDeskReceiverPreparationV2 evidence, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SchedulePreparationRetryAsync(FlowRunLease lease, Guid node, TimeSpan? retryAfter, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> SaveReceiptAsync(FlowRunLease lease, Guid node, RatelDeskVerifiedReceipt receipt, CancellationToken ct) => throw new NotSupportedException();
    }
}
