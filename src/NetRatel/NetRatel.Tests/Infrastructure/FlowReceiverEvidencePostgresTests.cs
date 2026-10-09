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
using NetRatel.Tests.Flows;
using Npgsql;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

// Real PostgreSQL migration/store regressions. Publication policy is an explicit
// test seam. Historical PossibleCommit SQL and constructed receipt DTOs below
// exercise persistence guards; they prove no fresh Monitoring admission, POST,
// remote receiver, JWT, published companion or physical acceptance.
[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowReceiverEvidencePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private const string MigrationId = "20261006195456_AddFlowReceiverEvidence";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Atomic_V2_preparation_rolls_back_on_real_database_constraint_failure_and_retains_original_V1_bytes(bool existingV1Preparation)
    {
        await using var h = await Harness.CreateAsync(postgres);
        if (existingV1Preparation)
            (await h.Store.SavePreparedActionAsync(h.Lease, h.NodeId, h.Request)).Should().BeTrue();
        var before = await h.ReadOriginalAsync();
        var snapshot = await h.ReadSnapshotAsync();

        // The interceptor changes only this newly added row before EF sends its
        // SQL. The actual migration CHECK rejects the insert in the store's real
        // transaction, including any simultaneously written V1 preparation.
        h.InsertFailure.RejectOneEvidenceInsert = true;
        Func<Task> save = async () =>
            await h.Evidence.SavePreparedActionAsync(h.Lease, h.NodeId, h.Request, h.Prepared, default);
        var error = (await save.Should().ThrowAsync<DbUpdateException>()).Which;
        var providerError = error.InnerException.Should().BeOfType<PostgresException>().Which;
        providerError.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        providerError.ConstraintName.Should().Be("CK_FlowReceiverEvidence_V2");
        (await h.ReadSnapshotAsync()).Should().Be(snapshot);
        (await h.ReadOriginalAsync()).Should().Be(before);
        (await h.CountEvidenceAsync()).Should().Be(0);

        (await h.Evidence.SavePreparedActionAsync(h.Lease, h.NodeId, h.Request, h.Prepared, default)).Should().BeTrue();
        var after = await h.ReadOriginalAsync();
        (after with { PreparedJson = before.PreparedJson, SemanticFingerprint = before.SemanticFingerprint }).Should().Be(before);
        after.PreparedJson.Should().Be(await h.CanonicalJsonAsync(JsonSerializer.Serialize(h.Request, Harness.Json)));
        after.SemanticFingerprint.Should().Be(h.Request.SemanticFingerprint);
        if (existingV1Preparation) after.Should().Be(before);
        var stored = await h.ReadEvidenceRowAsync();
        stored.SchemaVersion.Should().Be(2);
        stored.RowVersion.Should().Be(1);
        stored.ReceiverIdempotencyKey.Should().Be(h.Prepared.ReceiverIdempotencyKey);
        stored.EvidenceFingerprint.Should().Be(h.Prepared.EvidenceFingerprint);
        stored.ReceiverFingerprint.Should().Be(h.Prepared.ReceiverFingerprint);
        stored.OriginalCreatedAtUtc.Should().Be(h.Prepared.OriginalActionCreatedAtUtc);
        stored.AutomaticReplayUntilUtc.Should().Be(h.Prepared.AutomaticReplayUntilUtc);
        stored.MayHaveCommitted.Should().BeFalse();
        stored.FirstPostAttemptAtUtc.Should().BeNull();
        (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!.Prepared.Should().BeEquivalentTo(h.Prepared);
        var committed = await h.ReadSnapshotAsync();
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        (await h.ReadSnapshotAsync()).Should().Be(committed);
        (await h.CountEvidenceAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Actual_migration_trigger_rejects_material_immutable_and_monotonic_mutations_with_exact_durable_rows_preserved()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.PrepareAsync();
        // Every material mutation advances RowVersion, so its own trigger arm
        // must reject it. These are material JSONB changes, not key reordering.
        var immutable = new (string Name, string Assignment)[]
        {
            ("tenant", "\"TenantId\" = \"TenantId\" + 1"),
            ("run", "\"RunId\" = '00000000-0000-0000-0000-000000000001'::uuid"),
            ("node", "\"NodeId\" = '00000000-0000-0000-0000-000000000002'::uuid"),
            ("schema", "\"SchemaVersion\" = 3"),
            ("preparation", "\"PreparationJson\" = jsonb_set(\"PreparationJson\", '{schemaVersion}', '3'::jsonb)"),
            ("evidence fingerprint", "\"EvidenceFingerprint\" = left(\"EvidenceFingerprint\",63) || CASE right(\"EvidenceFingerprint\",1) WHEN '0' THEN '1' ELSE '0' END"),
            ("wire key", "\"ReceiverIdempotencyKey\" = 'changed-provider-fixture-key'"),
            ("receiver fingerprint", "\"ReceiverFingerprint\" = left(\"ReceiverFingerprint\",63) || CASE right(\"ReceiverFingerprint\",1) WHEN '0' THEN '1' ELSE '0' END"),
            ("original timestamp", "\"OriginalCreatedAtUtc\" = \"OriginalCreatedAtUtc\" + INTERVAL '1 microsecond'"),
            ("replay horizon", "\"AutomaticReplayUntilUtc\" = \"AutomaticReplayUntilUtc\" - INTERVAL '1 minute'")
        };
        foreach (var mutation in immutable)
            await h.AssertTriggerDeniedAsync(mutation.Assignment + ", \"RowVersion\" = \"RowVersion\" + 1", mutation.Name);
        await h.AssertTriggerDeniedAsync("\"RowVersion\" = \"RowVersion\"", "unchanged version");
        await h.AssertTriggerDeniedAsync("\"RowVersion\" = \"RowVersion\" - 1", "decreased version");

        await h.ArrangeHistoricalPossibleCommitAsync(postAttempts: 1);
        foreach (var mutation in new (string Name, string Assignment)[]
        {
            ("clear first POST time", "\"FirstPostAttemptAtUtc\" = NULL"),
            ("change first POST time", "\"FirstPostAttemptAtUtc\" = \"FirstPostAttemptAtUtc\" + INTERVAL '1 microsecond'"),
            ("clear POST fence", "\"LastPostLeaseFence\" = NULL"),
            ("decrease POST fence", "\"LastPostLeaseFence\" = \"LastPostLeaseFence\" - 1"),
            ("erase uncertainty without receipt", "\"MayHaveCommitted\" = FALSE")
        })
            await h.AssertTriggerDeniedAsync(mutation.Assignment + ", \"RowVersion\" = \"RowVersion\" + 1", mutation.Name);
        (await h.ExecuteEvidenceUpdateAsync("\"FinalReconciliationAttempted\" = TRUE, \"RowVersion\" = \"RowVersion\" + 1")).Should().Be(1);
        await h.AssertTriggerDeniedAsync("\"FinalReconciliationAttempted\" = FALSE, \"RowVersion\" = \"RowVersion\" + 1", "reopen consumed final read");
    }

    [Fact]
    public async Task Historical_uncertainty_can_progress_through_real_lease_fenced_reconciliation_and_constructed_receipt_persistence()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.PrepareAsync();
        var v1 = await h.ReadOriginalAsync();
        await h.ArrangeHistoricalPossibleCommitAsync(postAttempts: 1);
        var uncertain = (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!;
        uncertain.MayHaveCommitted.Should().BeTrue();
        uncertain.Attempts.Should().Be(1);
        uncertain.FirstPostAttemptAtUtc.Should().NotBeNull();
        (await h.Store.StartActionAsync(h.Lease, h.NodeId))!.Status.Should().Be(FlowActionStatus.Dispatching);
        // The body is explicitly constructed fixture data, independently parsed
        // by the production wire validator and revalidated by SaveReceiptAsync.
        var receipt = h.ConstructedReceipt();
        (await h.Evidence.SaveReceiptAsync(h.Lease, h.NodeId, receipt, default)).Should().BeTrue();
        (await h.ReadOriginalAsync()).Should().Be(v1);
        var row = await h.ReadEvidenceRowAsync();
        row.MayHaveCommitted.Should().BeFalse();
        row.FirstPostAttemptAtUtc.Should().Be(uncertain.FirstPostAttemptAtUtc);
        row.LastPostLeaseFence.Should().Be(h.Lease.Fence);
        row.RowVersion.Should().Be(3);
        JsonSerializer.Deserialize<RatelDeskVerifiedReceipt>(row.FullReceiptJson!, Harness.Json)!.ExactAcceptedBodyJson
            .Should().Be(receipt.ExactAcceptedBodyJson);
        var action = (await h.Definitions.GetRunByIdAsync(Harness.TenantId, h.Lease.RunId))!.Actions.Single();
        action.Status.Should().Be(FlowActionStatus.Succeeded);
        action.Attempts.Should().Be(1);
        action.Receipt!.IncidentId.Should().Be("provider-synthetic-incident");
        await h.AssertTriggerDeniedAsync("\"FullReceiptJson\" = '{}'::jsonb, \"RowVersion\" = \"RowVersion\" + 1", "replace full original receipt");
        await h.AssertTriggerDeniedAsync("\"FullReceiptJson\" = NULL, \"RowVersion\" = \"RowVersion\" + 1", "remove full original receipt");
        (await h.Store.CompleteRunAsync(h.Lease, new(FlowRunStatus.Succeeded, "provider-fixture-completed"))).Should().BeTrue();
        (await h.Definitions.GetRunByIdAsync(Harness.TenantId, h.Lease.RunId))!.Run.Status.Should().Be(FlowRunStatus.Succeeded);
    }

    [Fact]
    public async Task Wrong_tenant_token_owner_fence_source_and_genuinely_reclaimed_lease_cannot_mutate_or_read_evidence()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.PrepareAsync();
        var original = await h.ReadOriginalAsync();
        foreach (var wrong in new[]
        {
            h.Lease with { Event = h.Lease.Event with { TenantId = 18 } },
            h.Lease with { Token = Guid.NewGuid() },
            h.Lease with { WorkerId = Guid.NewGuid() },
            h.Lease with { Fence = h.Lease.Fence + 1 },
            h.Lease with { SourceInstanceId = Guid.NewGuid() }
        })
        {
            var unchanged = await h.ReadSnapshotAsync();
            (await h.Evidence.GetOriginalCreatedAtAsync(wrong, default)).Should().BeNull();
            (await h.Evidence.ReadAsync(wrong, h.NodeId, default)).Should().BeNull();
            (await h.Evidence.SavePreparedActionAsync(wrong, h.NodeId, h.Request, h.Prepared, default)).Should().BeFalse();
            (await h.Evidence.SchedulePreparationRetryAsync(wrong, h.NodeId, TimeSpan.FromSeconds(5), default)).Should().BeFalse();
            (await h.ReadSnapshotAsync()).Should().Be(unchanged);
        }
        var stale = h.Lease;
        h.Clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        await h.ReclaimAsync();
        h.Lease.Fence.Should().BeGreaterThan(stale.Fence);
        h.Lease.Token.Should().NotBe(stale.Token);
        (await h.ReadOriginalAsync()).Should().Be(original);
        var reclaimed = await h.ReadSnapshotAsync();
        (await h.Evidence.ReadAsync(stale, h.NodeId, default)).Should().BeNull();
        (await h.Evidence.SavePreparedAsync(stale, h.NodeId, h.Prepared, default)).Should().BeFalse();
        (await h.Evidence.SchedulePreparationRetryAsync(stale, h.NodeId, TimeSpan.FromSeconds(5), default)).Should().BeFalse();
        (await h.ReadSnapshotAsync()).Should().Be(reclaimed);
        (await h.Evidence.SavePreparedAsync(h.Lease, h.NodeId, h.Prepared, default)).Should().BeTrue();
        await h.ArrangeHistoricalPossibleCommitAsync(postAttempts: 1);
        (await h.Store.StartActionAsync(h.Lease, h.NodeId))!.Status.Should().Be(FlowActionStatus.Dispatching);
        var beforeReceipt = await h.ReadSnapshotAsync();
        (await h.Evidence.SaveReceiptAsync(stale, h.NodeId, h.ConstructedReceipt(), default)).Should().BeFalse();
        (await h.ReadSnapshotAsync()).Should().Be(beforeReceipt);
        (await h.Evidence.SaveReceiptAsync(h.Lease, h.NodeId, h.ConstructedReceipt(), default)).Should().BeTrue();
        (await h.ReadOriginalAsync()).Should().Be(original);
    }

    [Fact]
    public async Task Concurrent_actual_SQL_CAS_consumes_one_final_read_marker_and_cannot_reopen_it()
    {
        await using var h = await Harness.CreateAsync(postgres);
        await h.PrepareAsync();
        await h.ArrangeHistoricalPossibleCommitAsync(FlowLimits.MaximumActionAttempts);
        var before = await h.ReadEvidenceRowAsync();
        var v1 = await h.ReadOriginalAsync();
        var ready1 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready2 = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = h.ConsumeFinalReadAsync(before.RowVersion, ready1, release.Task);
        var second = h.ConsumeFinalReadAsync(before.RowVersion, ready2, release.Task);
        await Task.WhenAll(ready1.Task, ready2.Task);
        release.SetResult(true);
        var writes = await Task.WhenAll(first, second);
        writes.Order().Should().Equal(0, 1);
        var after = await h.ReadEvidenceRowAsync();
        after.FinalReconciliationAttempted.Should().BeTrue();
        after.RowVersion.Should().Be(before.RowVersion + 1);
        after.LastPostLeaseFence.Should().Be(before.LastPostLeaseFence);
        after.FirstPostAttemptAtUtc.Should().Be(before.FirstPostAttemptAtUtc);
        after.PreparationJson.Should().Be(before.PreparationJson);
        (await h.ReadOriginalAsync()).Should().Be(v1);
        (await h.Evidence.ReadAsync(h.Lease, h.NodeId, default))!.FinalReconciliationPending.Should().BeFalse();
        await h.AssertTriggerDeniedAsync("\"FinalReconciliationAttempted\" = FALSE, \"RowVersion\" = \"RowVersion\" + 1", "concurrent final read cannot reopen");
    }

    private sealed record OriginalV1Bytes(string EventJson, string EventFingerprint, string DraftJson,
        string? PreparedJson, string? SemanticFingerprint, string IdempotencyKey, string GraphJson, string ConfigurationHash);

    private sealed class FixtureClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan duration) => now += duration;
        public void CaptureSystemTime() => now = DateTimeOffset.UtcNow;
    }

    private sealed class PublicationPolicySeam : IFlowExecutionAuthorityVerifier
    {
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default) =>
            Task.FromResult(tenantId == Harness.TenantId && authority == FlowTestData.Authority);
    }

    private sealed class ConnectorCatalogSeam : IFlowConnectorCatalog
    {
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken ct = default) =>
            Task.FromResult<FlowConnectorReferenceDto?>(tenantId == Harness.TenantId && connectorId == FlowTestData.ConnectorId
                ? new(connectorId, tenantId, "Provider fixture connector", true, true, null, 1) : null);
        public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default) =>
            (await GetAsync(tenantId, FlowTestData.ConnectorId, authority, ct)) is { } connector ? [connector] : [];
    }

    private sealed class InvalidEvidenceInsert : SaveChangesInterceptor
    {
        public bool RejectOneEvidenceInsert { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var evidence = eventData.Context?.ChangeTracker.Entries<FlowReceiverEvidenceRecord>()
                .SingleOrDefault(entry => entry.State == EntityState.Added);
            if (RejectOneEvidenceInsert && evidence is not null)
            {
                RejectOneEvidenceInsert = false;
                evidence.Entity.SchemaVersion = 1;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public const int TenantId = 17;
        public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public required string Connection { get; init; }
        public FixtureClock Clock { get; } = new();
        public InvalidEvidenceInsert InsertFailure { get; } = new();
        public ServiceProvider Provider { get; private set; } = null!;
        public FlowRunLease Lease { get; private set; } = null!;
        public FlowIncidentActionDraft Draft { get; private set; } = null!;
        public FlowIncidentActionRequest Request { get; private set; } = null!;
        public RatelDeskReceiverPreparationV2 Prepared { get; private set; } = null!;
        public Guid NodeId => Draft.ActionNodeId;
        public IFlowExecutionStore Store => Provider.GetRequiredService<IFlowExecutionStore>();
        public IFlowDefinitionService Definitions => Provider.GetRequiredService<IFlowDefinitionService>();
        public IFlowReceiverEvidenceStore Evidence => Provider.GetRequiredService<IFlowReceiverEvidenceStore>();

        public static async Task<Harness> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var h = new Harness { Connection = await postgres.CreateDatabaseAsync() };
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(h.Connection).AddInterceptors(h.InsertFailure));
            services.AddSingleton<TimeProvider>(h.Clock);
            services.AddNetRatelFlows();
            services.AddSingleton<IFlowExecutionAuthorityVerifier, PublicationPolicySeam>();
            services.AddSingleton<IFlowConnectorCatalog, ConnectorCatalogSeam>();
            services.AddSingleton<IRatelDeskReceiverFingerprint, RatelDeskReceiverFingerprint>();
            services.AddSingleton<IFlowReceiverEvidenceStore>(provider => provider.GetRequiredService<FlowPersistenceService>());
            h.Provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
            await using (var scope = h.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                (await db.Database.GetAppliedMigrationsAsync()).Should().Contain(MigrationId);
                (await db.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
                db.Tenants.AddRange(new Tenant { Id = TenantId, Name = "Receiver provider fixture" }, new Tenant { Id = 18, Name = "Foreign tenant" });
                await db.SaveChangesAsync();
            }
            h.Clock.CaptureSystemTime();
            var created = (await h.Definitions.CreateAsync(TenantId, new("Receiver provider fixture"), "fixture-editor")).Definition!;
            var saved = (await h.Definitions.SaveDraftAsync(TenantId, created.Id, new(created.Revision, created.Name, FlowTestData.Graph()), "fixture-editor")).Definition!;
            var publication = await h.Definitions.PublishAsync(TenantId, saved.Id, new(saved.Revision), FlowTestData.Authority);
            publication.Disposition.Should().Be(FlowWriteDisposition.Stored);
            var version = publication.Version!;
            var input = new FlowEventEnvelope(TenantId, Guid.NewGuid(), Guid.NewGuid(), version.Id, h.Clock.GetUtcNow(),
                FlowTestData.EventData("receiver-provider-fixture", 90) with { ObservedAtUtc = h.Clock.GetUtcNow() }, FlowTestData.Authority);
            var queued = await h.Provider.GetRequiredService<IFlowEventIngress>().EnqueueAsync(input);
            queued.Disposition.Should().Be(FlowIngressDisposition.Enqueued);
            h.Lease = (await h.Store.ClaimAsync(Guid.NewGuid()))!;
            h.Lease.RunId.Should().Be(queued.RunId!.Value);
            h.Draft = BuildDraft(h.Lease);
            (await h.Store.GetOrCreateActionAsync(h.Lease, h.Draft)).Should().NotBeNull();
            h.Request = FlowTestData.Prepared(h.Draft, safeReplay: true);
            var original = (await h.Evidence.GetOriginalCreatedAtAsync(h.Lease, default))!.Value;
            var peer = new RatelDeskSemanticPeer(RatelDeskAuthenticationMode.PairedSystem, TenantId, h.Draft.ConnectorId,
                Guid.NewGuid().ToString("D"), 1, new string('a', 64), Guid.NewGuid().ToString("D"), "organization-17", "https://api.example.test/help",
                "https://issuer.example.test/services", "rateldesk-api", "https://api.example.test/help/connect/token", "paired-test-client", "paired", h.Lease.SourceInstanceId, Guid.NewGuid(), "organization-17", "customer-17", null, []);
            var capability = new RatelDeskVerifiedCapability(ReceiverWireValidation.Contract, peer.ReceiverInstanceId, peer.SourceInstanceId,
                peer.SourceNamespaceId, new(ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CapabilitiesPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CreatePath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.ReceiptPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.TargetsPath)),
                ReceiverWireValidation.MinimumRetention, ReceiverWireValidation.MaximumReplay, h.Clock.GetUtcNow());
            h.Prepared = new ReceiverPreparationBuilder(h.Provider.GetRequiredService<IRatelDeskReceiverFingerprint>())
                .Build(h.Draft, h.Request, peer, capability, original, null);
            return h;
        }

        public async Task PrepareAsync() =>
            (await Evidence.SavePreparedActionAsync(Lease, NodeId, Request, Prepared, default)).Should().BeTrue();

        public async Task ReclaimAsync()
        {
            Lease = (await Store.ClaimAsync(Guid.NewGuid()))!;
            Draft = BuildDraft(Lease);
            (await Store.GetOrCreateActionAsync(Lease, Draft)).Should().NotBeNull();
        }

        private static FlowIncidentActionDraft BuildDraft(FlowRunLease lease)
        {
            var node = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.CreateIncident);
            var mapping = lease.Version.Graph.Nodes.Single(item => item.Kind == FlowNodeKind.MapIncident).Mapping!;
            return new(lease.Event.TenantId, lease.RunId, node.Id, node.ConnectorId!.Value, node.ConnectorRevision!.Value,
                lease.SourceInstanceId, FlowActionKeys.Create(lease, node.Id), lease.Event, FlowPureEvaluation.Map(mapping, lease.Event.Data));
        }

        public async Task<OriginalV1Bytes> ReadOriginalAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == Lease.RunId);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(row => row.RunId == Lease.RunId && row.NodeId == NodeId);
            var version = await db.FlowVersions.AsNoTracking().SingleAsync(row => row.Id == Lease.Version.Id);
            return new(run.EventJson, run.EventFingerprint, action.DraftJson, action.PreparedJson, action.SemanticFingerprint,
                action.IdempotencyKey, version.GraphJson, version.ConfigurationHash);
        }

        public async Task<FlowReceiverEvidenceRecord> ReadEvidenceRowAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<FlowReceiverEvidenceRecord>()
                .AsNoTracking().SingleAsync(row => row.TenantId == TenantId && row.RunId == Lease.RunId && row.NodeId == NodeId);
        }

        public async Task<int> CountEvidenceAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<FlowReceiverEvidenceRecord>()
                .CountAsync(row => row.TenantId == TenantId && row.RunId == Lease.RunId && row.NodeId == NodeId);
        }

        public async Task<string> CanonicalJsonAsync(string raw)
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("SELECT @json::jsonb::text", connection);
            command.Parameters.AddWithValue("json", raw);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task<string> ReadSnapshotAsync()
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT json_build_object('run',to_jsonb(r),'action',to_jsonb(a),'evidence',to_jsonb(e))::text
                FROM "FlowRuns" r JOIN "FlowActions" a ON a."RunId"=r."Id" AND a."TenantId"=r."TenantId"
                LEFT JOIN "FlowReceiverEvidence" e ON e."RunId"=a."RunId" AND e."NodeId"=a."NodeId"
                WHERE r."Id"=@run AND r."TenantId"=@tenant AND a."NodeId"=@node
                """, connection);
            BindIdentity(command);
            return (string)(await command.ExecuteScalarAsync())!;
        }

        public async Task AssertTriggerDeniedAsync(string assignments, string because)
        {
            var before = await ReadSnapshotAsync();
            // Standalone autocommit connection per probe: a 23514 cannot poison
            // subsequent probes with a retained failed PostgreSQL transaction.
            Func<Task> mutation = async () => await ExecuteEvidenceUpdateAsync(assignments);
            var error = (await mutation.Should().ThrowAsync<PostgresException>(because)).Which;
            error.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            error.MessageText.Should().Be("receiver-evidence-immutable-input-conflict");
            (await ReadSnapshotAsync()).Should().Be(before, because);
        }

        public async Task<int> ExecuteEvidenceUpdateAsync(string fixedAssignments)
        {
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync();
            // Assignments are fixed source literals from this test, never input.
            await using var command = new NpgsqlCommand("UPDATE \"FlowReceiverEvidence\" SET " + fixedAssignments +
                " WHERE \"TenantId\"=@tenant AND \"RunId\"=@run AND \"NodeId\"=@node", connection);
            BindIdentity(command);
            return await command.ExecuteNonQueryAsync();
        }

        public async Task ArrangeHistoricalPossibleCommitAsync(int postAttempts)
        {
            // A constructed historical state for the migration guard, not a POST
            // or receipt claim. No Agent, owner, evidence stream or outbox is seeded.
            postAttempts.Should().BeInRange(1, FlowLimits.MaximumActionAttempts);
            await using var connection = new NpgsqlConnection(Connection);
            await connection.OpenAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("""
                UPDATE "FlowActions" SET "Attempts"=@attempts
                WHERE "TenantId"=@tenant AND "RunId"=@run AND "NodeId"=@node;
                UPDATE "FlowReceiverEvidence" SET "MayHaveCommitted"=TRUE,
                    "FirstPostAttemptAtUtc"=@first, "LastPostLeaseFence"=@fence, "RowVersion"="RowVersion"+1
                WHERE "TenantId"=@tenant AND "RunId"=@run AND "NodeId"=@node
                """, connection, transaction);
            BindIdentity(command);
            command.Parameters.AddWithValue("attempts", postAttempts);
            command.Parameters.AddWithValue("first", Clock.GetUtcNow());
            command.Parameters.AddWithValue("fence", Lease.Fence);
            (await command.ExecuteNonQueryAsync()).Should().Be(2);
            await transaction.CommitAsync();
        }

        public async Task<int> ConsumeFinalReadAsync(long expectedVersion, TaskCompletionSource<bool> ready, Task release)
        {
            await using var connection = new NpgsqlConnection(Connection);
            try { await connection.OpenAsync(); }
            finally { ready.TrySetResult(true); }
            await release;
            await using var command = new NpgsqlCommand("""
                UPDATE "FlowReceiverEvidence" SET "FinalReconciliationAttempted"=TRUE,"RowVersion"="RowVersion"+1
                WHERE "TenantId"=@tenant AND "RunId"=@run AND "NodeId"=@node AND "RowVersion"=@version
                    AND "MayHaveCommitted" AND NOT "FinalReconciliationAttempted" AND "LastPostLeaseFence"=@fence
                """, connection);
            BindIdentity(command);
            command.Parameters.AddWithValue("version", expectedVersion);
            command.Parameters.AddWithValue("fence", Lease.Fence);
            return await command.ExecuteNonQueryAsync();
        }

        private void BindIdentity(NpgsqlCommand command)
        {
            command.Parameters.AddWithValue("tenant", TenantId);
            command.Parameters.AddWithValue("run", Lease.RunId);
            command.Parameters.AddWithValue("node", NodeId);
        }

        public RatelDeskVerifiedReceipt ConstructedReceipt()
        {
            const string incident = "provider-synthetic-incident";
            const string tracking = "SYNTHETIC-provider";
            const string location = "/help/api/v1/incidents/provider-synthetic-incident";
            var raw = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = incident, trackingId = tracking, organizationId = Prepared.Peer.OrganizationId, customerId = Prepared.Peer.CustomerId,
                integrationReceipt = new
                {
                    contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = Prepared.Peer.ReceiverInstanceId,
                    sourceNamespaceId = Prepared.Peer.SourceNamespaceId.ToString("D"), sourceInstanceId = Prepared.Peer.SourceInstanceId.ToString("D"),
                    key = Prepared.ReceiverIdempotencyKey, fingerprint = Prepared.ReceiverFingerprint, outcome = "committed",
                    incidentId = incident, trackingId = tracking, organizationId = Prepared.Peer.OrganizationId,
                    customerId = Prepared.Peer.CustomerId, committedAtUtc = Clock.GetUtcNow(), location
                }
            });
            return ReceiverWireValidation.Receipt(raw, location, Prepared);
        }

        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
