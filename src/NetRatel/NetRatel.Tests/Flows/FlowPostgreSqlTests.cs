using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Flows;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowPostgreSqlTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Migration_CAS_tenant_fences_and_database_immutable_versions_preserve_published_semantics()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await using (var scope = h.Provider.CreateAsyncScope())
            scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Database.HasPendingModelChanges().Should().BeFalse();
        var published = await h.PublishAsync();
        var current = (await h.Definitions.GetAsync(17, published.FlowId))!;
        var edit = current.Draft with { Nodes = current.Draft.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident ? node with { Mapping = new("changed", "draft only") } : node).ToArray() };
        var writes = await Task.WhenAll(Enumerable.Range(0, 8).Select(index => h.Definitions.SaveDraftAsync(17, current.Id, new(current.Revision, $"edit-{index}", edit), "editor")));
        writes.Count(write => write.Disposition == FlowWriteDisposition.Stored).Should().Be(1);
        writes.Count(write => write.Disposition == FlowWriteDisposition.Conflict).Should().Be(7);
        (await h.Definitions.GetVersionsAsync(17, current.Id)).Single().Graph.Should().BeEquivalentTo(published.Graph);
        (await h.Definitions.GetAsync(18, current.Id)).Should().BeNull();
        (await h.Definitions.GetVersionsAsync(18, current.Id)).Should().BeEmpty();
        (await h.Definitions.GetVersionAsync(17, published.Id))!.FlowId.Should().Be(current.Id);
        (await h.Definitions.GetVersionAsync(18, published.Id)).Should().BeNull();
        (await h.Definitions.SaveDraftAsync(18, current.Id, new(current.Revision, "foreign", edit), "editor")).Disposition.Should().Be(FlowWriteDisposition.NotFound);
        await using var connection = new NpgsqlConnection(h.Connection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("UPDATE \"FlowVersions\" SET \"ConfigurationHash\" = repeat('0',64) WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", published.Id);
        var error = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
    }

    [Fact]
    public async Task Concurrent_duplicate_ingress_has_one_real_run_and_rejects_changed_payload_and_foreign_version()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var input = h.Event(version);
        await using var second = h.NewProvider(); var ingress2 = second.GetRequiredService<IFlowEventIngress>();
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(index => (index % 2 == 0 ? h.Ingress : ingress2).EnqueueAsync(input)));
        results.Count(result => result.Disposition == FlowIngressDisposition.Enqueued).Should().Be(1);
        results.Count(result => result.Disposition == FlowIngressDisposition.Duplicate).Should().Be(11);
        results.Select(result => result.RunId).Distinct().Should().ContainSingle();
        (await h.Ingress.EnqueueAsync(input with { Data = input.Data with { NumericValue = 99 } })).Disposition.Should().Be(FlowIngressDisposition.Conflict);
        (await h.Ingress.EnqueueAsync(input with { TenantId = 18 })).Disposition.Should().Be(FlowIngressDisposition.Invalid);
        (await h.Ingress.GetOutcomeAsync(18, results[0].RunId!.Value)).Should().BeNull();
        (await h.Ingress.FindOutcomeAsync(17, input.EventId, version.Id))!.Id.Should().Be(results[0].RunId!.Value);
        (await h.Ingress.FindOutcomeAsync(18, input.EventId, version.Id)).Should().BeNull();
        (await h.Ingress.FindOutcomeAsync(17, input.EventId, Guid.NewGuid())).Should().BeNull();
        (await h.Definitions.GetRunByIdAsync(17, results[0].RunId!.Value))!.Run.FlowId.Should().Be(version.FlowId);
        (await h.Definitions.GetRunByIdAsync(18, results[0].RunId!.Value)).Should().BeNull();
        var claims = await Task.WhenAll(h.Store.ClaimAsync(Guid.NewGuid()), second.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid()));
        claims.Count(lease => lease is not null).Should().Be(1);
    }

    [Fact]
    public async Task Disabled_ingress_persists_terminal_history_and_cannot_resurrect_after_enable()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync();
        var definition = (await h.Definitions.GetAsync(17, version.FlowId))!;
        var disabled = (await h.Definitions.SetEnabledAsync(17, definition.Id, new(definition.Revision, false), "editor")).Definition!;
        var input = h.Event(version); var result = await h.Ingress.EnqueueAsync(input);
        result.Disposition.Should().Be(FlowIngressDisposition.Disabled); result.RunId.Should().NotBeNull();
        (await h.Ingress.GetOutcomeAsync(17, result.RunId!.Value))!.Status.Should().Be(FlowRunStatus.Failed);
        await h.Definitions.SetEnabledAsync(17, definition.Id, new(disabled.Revision, true), "editor");
        (await h.Ingress.EnqueueAsync(input)).Disposition.Should().Be(FlowIngressDisposition.Duplicate);
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull(); h.Dispatcher.Sends.Should().Be(0);
    }

    [Fact]
    public async Task Publish_cannot_silently_replace_a_selected_connector_configuration_revision()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var created = (await h.Definitions.CreateAsync(17, new("pinned selection"), "editor")).Definition!;
        var saved = (await h.Definitions.SaveDraftAsync(17, created.Id, new(created.Revision, created.Name, FlowTestData.Graph()), "editor")).Definition!;
        h.Catalog.Revision = 2;
        var result = await h.Definitions.PublishAsync(17, saved.Id, new(saved.Revision), FlowTestData.Authority);
        result.Disposition.Should().Be(FlowWriteDisposition.Conflict); result.Code.Should().Be("connector-revision-conflict");
        (await h.Definitions.GetVersionsAsync(17, created.Id)).Should().BeEmpty(); h.Dispatcher.Sends.Should().Be(0);
    }

    [Fact]
    public async Task Durable_send_fence_rejects_modified_lease_graph_instance_identity_and_mapping_payload()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease);
        (await h.Store.GetOrCreateActionAsync(lease, draft with { Fields = new("unreviewed title", "unreviewed payload") })).Should().BeNull();
        var graph = version.Graph with { Nodes = version.Graph.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident ? node with { Mapping = new("tampered", "tampered") } : node).ToArray() };
        var tampered = lease with { Version = version with { Graph = graph, ConfigurationHash = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web))) } };
        (await h.Store.GetOrCreateActionAsync(tampered, Draft(tampered))).Should().BeNull();
        var otherInstance = lease with { SourceInstanceId = Guid.NewGuid() };
        (await h.Store.GetOrCreateActionAsync(otherInstance, Draft(otherInstance))).Should().BeNull();
        (await h.Store.GetOrCreateActionAsync(lease, draft)).Should().NotBeNull();
    }

    [Fact]
    public async Task Remote_commit_before_receipt_becomes_DeliveryUnknown_and_never_blindly_sends_again()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); await h.Ingress.EnqueueAsync(h.Event(version));
        var first = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(first);
        await h.Store.GetOrCreateActionAsync(first, draft); var prepared = FlowTestData.Prepared(draft);
        (await h.Store.SavePreparedActionAsync(first, draft.ActionNodeId, prepared)).Should().BeTrue();
        (await h.Store.StartActionAsync(first, draft.ActionNodeId))!.Status.Should().Be(FlowActionStatus.Dispatching);
        // The deterministic receiver committed, but the sender crashed before recording its receipt.
        await h.Dispatcher.DispatchAsync(prepared); h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        await using var restarted = h.NewProvider(); var store = restarted.GetRequiredService<IFlowExecutionStore>();
        var reclaimed = (await store.ClaimAsync(Guid.NewGuid()))!;
        reclaimed.Fence.Should().BeGreaterThan(first.Fence); reclaimed.SourceInstanceId.Should().Be(first.SourceInstanceId);
        var recovered = (await store.GetOrCreateActionAsync(reclaimed, Draft(reclaimed)))!;
        recovered.Status.Should().Be(FlowActionStatus.DeliveryUnknown); recovered.Request.Should().BeEquivalentTo(prepared);
        (await store.CompleteActionAsync(first, draft.ActionNodeId, new(FlowIncidentActionResultKind.Succeeded, "created", new("too-late")))).Should().BeFalse();
        (await store.CompleteRunAsync(reclaimed, new(FlowRunStatus.DeliveryUnknown, "delivery-unknown"))).Should().BeTrue();
        (await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        h.Dispatcher.Sends.Should().Be(1);
    }

    [Fact]
    public async Task Persisted_receipt_after_crash_finishes_run_without_replaying_even_after_current_grants_are_revoked()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease); var request = FlowTestData.Prepared(draft);
        await h.Store.GetOrCreateActionAsync(lease, draft); await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, request); await h.Store.StartActionAsync(lease, draft.ActionNodeId);
        var receipt = await h.Dispatcher.DispatchAsync(request);
        (await h.Store.CompleteActionAsync(lease, draft.ActionNodeId, receipt)).Should().BeTrue();
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1)); h.Authority.Allowed = false; h.Guard.Allowed = false;
        await using var restarted = h.NewProvider();
        (await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.Succeeded);
        h.Dispatcher.Sends.Should().Be(1); h.Dispatcher.Preparations.Should().Be(0);
        (await h.Definitions.GetRunAsync(17, version.FlowId, queued.RunId.Value))!.Actions.Single().Receipt.Should().Be(receipt.Receipt);
    }

    [Fact]
    public async Task Cancellation_after_a_committed_action_receipt_cannot_overwrite_verified_success()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        using var cancelled = new CancellationTokenSource(); h.Dispatcher.AfterSend = cancelled.Cancel;
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync(cancelled.Token)).Should().BeTrue();
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.Succeeded);
        (await h.Definitions.GetRunByIdAsync(17, queued.RunId.Value))!.Actions.Single().Receipt.Should().NotBeNull();
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull(); h.Dispatcher.Sends.Should().Be(1);
    }

    [Fact]
    public async Task Verified_safe_replay_reclaims_the_same_key_and_exact_payload_while_stale_worker_cannot_commit()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); await h.Ingress.EnqueueAsync(h.Event(version));
        var old = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(old); var request = FlowTestData.Prepared(draft, safeReplay: true);
        await h.Store.GetOrCreateActionAsync(old, draft); await h.Store.SavePreparedActionAsync(old, draft.ActionNodeId, request); await h.Store.StartActionAsync(old, draft.ActionNodeId);
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1)); await using var restarted = h.NewProvider();
        var store = restarted.GetRequiredService<IFlowExecutionStore>(); var current = (await store.ClaimAsync(Guid.NewGuid()))!;
        await store.GetOrCreateActionAsync(current, Draft(current)); var retry = (await store.StartActionAsync(current, draft.ActionNodeId))!;
        retry.Request.Should().BeEquivalentTo(request); retry.Attempts.Should().Be(2);
        (await store.CompleteRunAsync(old, new(FlowRunStatus.Failed, "stale"))).Should().BeFalse();
        (await store.CompleteActionAsync(old, draft.ActionNodeId, new(FlowIncidentActionResultKind.Succeeded, "created", new("stale")))).Should().BeFalse();
        (await store.CompleteActionAsync(current, draft.ActionNodeId, new(FlowIncidentActionResultKind.Succeeded, "created", new("original-key-result")))).Should().BeTrue();
        (await store.CompleteRunAsync(current, new(FlowRunStatus.Succeeded, "completed"))).Should().BeTrue();
    }

    [Fact]
    public async Task Last_allowed_safe_replay_send_with_lost_receipt_remains_unknown_at_the_action_attempt_cap()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease); var request = FlowTestData.Prepared(draft, safeReplay: true);
        await h.Store.GetOrCreateActionAsync(lease, draft); await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, request);
        for (var attempt = 1; attempt < FlowLimits.MaximumActionAttempts; attempt++)
        {
            (await h.Store.StartActionAsync(lease, draft.ActionNodeId))!.Attempts.Should().Be(attempt);
            await h.Store.CompleteActionAsync(lease, draft.ActionNodeId, new(FlowIncidentActionResultKind.RetryableSafe, "known-no-effect", RetryAfter: TimeSpan.Zero));
            await h.Store.CompleteRunAsync(lease, new(FlowRunStatus.RetryWaiting, "retry-waiting"));
            lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; draft = Draft(lease); await h.Store.GetOrCreateActionAsync(lease, draft);
        }
        (await h.Store.StartActionAsync(lease, draft.ActionNodeId))!.Attempts.Should().Be(FlowLimits.MaximumActionAttempts);
        await h.Dispatcher.DispatchAsync(request); // remote commit followed by sender crash, with no local receipt
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1)); await using var restarted = h.NewProvider(); var store = restarted.GetRequiredService<IFlowExecutionStore>();
        var reclaimed = (await store.ClaimAsync(Guid.NewGuid()))!; await store.GetOrCreateActionAsync(reclaimed, Draft(reclaimed));
        var capped = (await store.StartActionAsync(reclaimed, draft.ActionNodeId))!;
        capped.Status.Should().Be(FlowActionStatus.DeliveryUnknown); capped.Request.Should().BeEquivalentTo(request);
        await store.CompleteRunAsync(reclaimed, new(FlowRunStatus.DeliveryUnknown, "delivery-unknown"));
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.DeliveryUnknown);
        (await store.ClaimAsync(Guid.NewGuid())).Should().BeNull(); h.Dispatcher.Sends.Should().Be(1);
    }

    [Theory]
    [InlineData(FlowIncidentActionResultKind.Unavailable)]
    [InlineData(FlowIncidentActionResultKind.Failed)]
    [InlineData(FlowIncidentActionResultKind.RetryableSafe)]
    public async Task A_current_non_success_cannot_prove_an_interrupted_prior_safe_replay_send_did_not_commit(FlowIncidentActionResultKind currentResult)
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease); var request = FlowTestData.Prepared(draft, safeReplay: true);
        await h.Store.GetOrCreateActionAsync(lease, draft); await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, request); await h.Store.StartActionAsync(lease, draft.ActionNodeId);
        await h.Dispatcher.DispatchAsync(request); // committed earlier send, but receipt was lost
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1)); h.Dispatcher.Returns = new(currentResult, "current-connector-rejection");
        await using var restarted = h.NewProvider(); await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync();
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.DeliveryUnknown);
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Theory]
    [InlineData("grant")]
    [InlineData("suppress")]
    [InlineData("disable")]
    [InlineData("expire")]
    public async Task A_change_during_preparation_is_rechecked_before_any_irreversible_dispatch(string change)
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        h.Dispatcher.BeforePrepare = async () =>
        {
            if (change == "grant") h.Authority.Allowed = false;
            if (change == "suppress") h.Guard.Allowed = false;
            if (change == "expire") h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
            if (change == "disable") { var current = (await h.Definitions.GetAsync(17, version.FlowId))!; await h.Definitions.SetEnabledAsync(17, current.Id, new(current.Revision, false), "editor"); }
        };
        await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync();
        h.Dispatcher.Sends.Should().Be(0);
        if (change != "expire") (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.Failed);
        else (await h.Store.ClaimAsync(Guid.NewGuid())).Should().NotBeNull("an expired worker cannot commit a terminal outcome using its old fence");
    }

    [Fact]
    public async Task Prepared_payload_and_receipt_are_database_immutable_and_unknown_history_is_retained()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease); var request = FlowTestData.Prepared(draft);
        await h.Store.GetOrCreateActionAsync(lease, draft); await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, request);
        var changed = request with { Target = request.Target with { CustomerId = "other-customer" } }; changed = changed with { SemanticFingerprint = FlowContractValidation.Fingerprint(changed) };
        (await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, changed)).Should().BeFalse();
        await using (var connection = new NpgsqlConnection(h.Connection))
        {
            await connection.OpenAsync(); await using var command = new NpgsqlCommand("UPDATE \"FlowActions\" SET \"PreparedJson\" = '{}'::jsonb WHERE \"RunId\" = @id", connection);
            command.Parameters.AddWithValue("id", lease.RunId);
            (await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        }
        await h.Store.StartActionAsync(lease, draft.ActionNodeId); await h.Store.CompleteActionAsync(lease, draft.ActionNodeId, new(FlowIncidentActionResultKind.DeliveryUnknown, "ambiguous"));
        await h.Store.CompleteRunAsync(lease, new(FlowRunStatus.DeliveryUnknown, "ambiguous"));
        h.Clock.Advance(FlowLimits.HistoryRetention + TimeSpan.FromDays(1)); (await h.Store.PruneHistoryAsync()).Should().Be(0);
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(FlowRunStatus.DeliveryUnknown);
    }

    [Fact]
    public async Task History_pruning_cannot_turn_an_expired_event_into_a_fresh_action()
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var input = h.Event(version);
        var queued = await h.Ingress.EnqueueAsync(input); var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!;
        await h.Store.CompleteRunAsync(lease, new(FlowRunStatus.Failed, "fixture-failed"));
        h.Clock.Advance(FlowLimits.HistoryRetention + TimeSpan.FromDays(1)); (await h.Store.PruneHistoryAsync()).Should().Be(1);
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value)).Should().BeNull();
        (await h.Ingress.EnqueueAsync(input)).Code.Should().Be("flow-event-expired");
        (await h.Store.ClaimAsync(Guid.NewGuid())).Should().BeNull(); h.Dispatcher.Sends.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Exhausted_execution_budget_preserves_verified_receipts_and_ambiguous_sends(bool receiptCommitted, bool ageBudget)
    {
        await using var h = await Harness.CreateAsync(postgres); var version = await h.PublishAsync(); var queued = await h.Ingress.EnqueueAsync(h.Event(version));
        var lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!; var draft = Draft(lease); var request = FlowTestData.Prepared(draft);
        await h.Store.GetOrCreateActionAsync(lease, draft); await h.Store.SavePreparedActionAsync(lease, draft.ActionNodeId, request); await h.Store.StartActionAsync(lease, draft.ActionNodeId);
        var result = await h.Dispatcher.DispatchAsync(request);
        if (receiptCommitted) await h.Store.CompleteActionAsync(lease, draft.ActionNodeId, result);
        if (ageBudget) h.Clock.Advance(FlowLimits.MaximumRetryAge + TimeSpan.FromSeconds(1));
        else
        {
            await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.FlowRuns.Where(run => run.Id == lease.RunId).ExecuteUpdateAsync(set => set.SetProperty(run => run.Attempts, FlowLimits.MaximumActionAttempts * 2));
            h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        }
        await using var restarted = h.NewProvider(); (await restarted.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid())).Should().BeNull();
        (await h.Ingress.GetOutcomeAsync(17, queued.RunId!.Value))!.Status.Should().Be(receiptCommitted ? FlowRunStatus.Succeeded : FlowRunStatus.DeliveryUnknown);
        (await h.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse(); h.Dispatcher.Sends.Should().Be(1);
    }

    private static FlowIncidentActionDraft Draft(FlowRunLease lease)
    {
        var node = lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident);
        var mapping = lease.Version.Graph.Nodes.Single(node => node.Kind == FlowNodeKind.MapIncident).Mapping!;
        return new(lease.Event.TenantId, lease.RunId, node.Id, node.ConnectorId!.Value, node.ConnectorRevision!.Value, lease.SourceInstanceId,
            FlowActionKeys.Create(lease, node.Id), lease.Event, FlowPureEvaluation.Map(mapping, lease.Event.Data));
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new DateTimeOffset(2026, 10, 2, 20, 0, 0, TimeSpan.Zero).AddTicks(7);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class Authority : IFlowExecutionAuthorityVerifier
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult(Allowed);
    }
    private sealed class Guard : IFlowDispatchGuard
    {
        public bool Allowed { get; set; } = true;
        public Task<FlowDispatchDecision> CanDispatchAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default) => Task.FromResult(new FlowDispatchDecision(Allowed, Allowed ? "allowed" : "occurrence-suppressed"));
    }
    private sealed class Catalog : IFlowConnectorCatalog
    {
        public bool Available { get; set; } = true;
        public long Revision { get; set; } = 1;
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid id, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => Task.FromResult<FlowConnectorReferenceDto?>(
            id == FlowTestData.ConnectorId ? new(id, tenantId, "Deterministic fixture", true, Available, Available ? null : "receiver-idempotency-unverified", Revision) : null);
        public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) => [(await GetAsync(tenantId, FlowTestData.ConnectorId, authority, cancellationToken))!];
    }
    private sealed class Dispatcher : IFlowIncidentActionDispatcher
    {
        public int Sends { get; private set; }
        public int Preparations { get; private set; }
        public Func<Task>? BeforePrepare { get; set; }
        public Action? AfterSend { get; set; }
        public FlowIncidentActionResult? Returns { get; set; }
        public async Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
        { Preparations++; if (BeforePrepare is not null) await BeforePrepare(); return new(FlowIncidentPreparationStatus.Ready, FlowTestData.Prepared(draft)); }
        public Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default)
        { Sends++; AfterSend?.Invoke(); return Task.FromResult(Returns ?? new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created", new("fixture-incident", "fixture-tracking"))); }
    }
    private sealed class Harness : IAsyncDisposable
    {
        public required string Connection { get; init; }
        public Clock Clock { get; } = new(); public Authority Authority { get; } = new(); public Guard Guard { get; } = new();
        public Catalog Catalog { get; } = new(); public Dispatcher Dispatcher { get; } = new();
        public ServiceProvider Provider { get; private set; } = null!;
        public IFlowDefinitionService Definitions => Provider.GetRequiredService<IFlowDefinitionService>();
        public IFlowEventIngress Ingress => Provider.GetRequiredService<IFlowEventIngress>();
        public IFlowExecutionStore Store => Provider.GetRequiredService<IFlowExecutionStore>();
        public static async Task<Harness> CreateAsync(PostgreSqlPersistenceFixture fixture)
        {
            var h = new Harness { Connection = await fixture.CreateDatabaseAsync() }; h.Provider = h.NewProvider();
            await using var scope = h.Provider.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await db.Database.MigrateAsync(); db.Tenants.AddRange(new Tenant { Id = 17, Name = "Flows fixture" }, new Tenant { Id = 18, Name = "Foreign tenant" }); await db.SaveChangesAsync(); return h;
        }
        public ServiceProvider NewProvider()
        {
            var services = new ServiceCollection(); services.AddLogging(); services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(Connection));
            services.AddSingleton<TimeProvider>(Clock); services.AddNetRatelFlows();
            services.AddSingleton<IFlowExecutionAuthorityVerifier>(Authority); services.AddSingleton<IFlowDispatchGuard>(Guard);
            services.AddSingleton<IFlowConnectorCatalog>(Catalog); services.AddSingleton<IFlowIncidentActionDispatcher>(Dispatcher);
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public async Task<FlowVersionDto> PublishAsync()
        {
            var created = (await Definitions.CreateAsync(17, new("CPU incident flow"), "editor")).Definition!;
            var saved = (await Definitions.SaveDraftAsync(17, created.Id, new(created.Revision, created.Name, FlowTestData.Graph()), "editor")).Definition!;
            var published = await Definitions.PublishAsync(17, saved.Id, new(saved.Revision), FlowTestData.Authority);
            published.Disposition.Should().Be(FlowWriteDisposition.Stored); return published.Version!;
        }
        public FlowEventEnvelope Event(FlowVersionDto version) => new(17, Guid.NewGuid(), Guid.NewGuid(), version.Id, Clock.GetUtcNow(), FlowTestData.EventData("fixture-client", 90), FlowTestData.Authority);
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
