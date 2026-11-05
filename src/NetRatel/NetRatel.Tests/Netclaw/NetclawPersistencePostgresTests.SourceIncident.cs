using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Application.RatelDesk;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed partial class NetclawPersistencePostgresTests
{
    private const string IncidentPublisher = "synthetic-incident-publisher";
    private static readonly JsonSerializerOptions IncidentJson = new(JsonSerializerDefaults.Web);

    // Native lifecycle, source/current-grant admission, runtime/checkpoints, AI policy, final
    // enrichment and receiver preparation/receipt persistence are real. Published configuration
    // is seeded; pairing, outbound credential resolution and remote transport are synthetic.
    [Fact]
    public async Task Native_request_failure_freezes_one_enriched_incident_and_recovers_its_original_receipt_after_restart()
    {
        const string failureReason = "Synthetic request was rejected before any job run existed.";
        var options = await DatabaseAsync();
        await using var connection = new OrchestratorDbContext(options);
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connection.Database.GetConnectionString()).Options;
        var clock = new RecoveryClock();
        var daemon = new Daemon();
        var remote = new IncidentReceiver(clock, options);
        var protection = new EphemeralDataProtectionProvider();
        var reader = new IncidentReaderCounter();
        await using var original = IncidentProvider(options, identityOptions, clock, daemon, remote, reader, protection);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var ct = deadline.Token;
        Guid versionId;
        Guid flowId;
        Guid incidentNodeId;
        Guid aiNodeId;
        var agentId = Guid.NewGuid();
        await using (var scope = original.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync(ct);
            identity.ApplicationPrincipals.Add(new() { Id = IncidentPublisher,
                ExternalIssuer = "https://issuer.example.test", ExternalSubject = IncidentPublisher });
            var role = new AccessRole { Name = "Synthetic source incident operator" };
            foreach (var permission in new[] { NetRatelPermissions.FlowRead, NetRatelPermissions.FlowEdit,
                NetRatelPermissions.FlowPublish, NetRatelPermissions.FlowExecute, NetRatelPermissions.ClientManagement,
                NetRatelPermissions.TelemetryRead, NetRatelPermissions.SecretUse, NetRatelPermissions.JobManagement,
                NetRatelPermissions.IntegrationManagement })
                role.Permissions.Add(new() { Permission = permission });
            identity.AccessRoles.Add(role);
            identity.PrincipalRoleAssignments.Add(new() { PrincipalId = IncidentPublisher, RoleId = role.Id, TenantId = 17 });
            await identity.SaveChangesAsync(ct);
            var authority = scope.ServiceProvider.GetRequiredService<NetclawAuthority>();
            Assert.False(await authority.CanInvokeAsync(17, IncidentPublisher, null, true, ct));
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent { Id = agentId, TenantId = 17, Name = "Synthetic request target",
                CreatedAtUtc = clock.GetUtcNow(), LastSeenUtc = clock.GetUtcNow() });
            // Capture the native submitted-definition sensitivity policy. No job run is admitted;
            // an absent definition would correctly withhold its unclassified diagnostics.
            db.Jobs.Add(new() { Id = 4242, TenantId = 17, AgentId = agentId, Name = "Synthetic request definition",
                CreatedAtUtc = clock.GetUtcNow(), UpdatedAtUtc = clock.GetUtcNow() });
            await db.SaveChangesAsync(ct);
            var paired = await scope.ServiceProvider.GetRequiredService<NetclawProfileService>()
                .PairAsync(17, new("https://netclaw.example.test", "synthetic-code"), IncidentPublisher, ct);
            Assert.True(paired.Success);
            Assert.True(await scope.ServiceProvider.GetRequiredService<IRatelDeskConnectorStore>()
                .SaveAsync(remote.FixtureConnector, 0, ct));
            var graph = IncidentGraph(paired.Connection.Id, paired.Connection.Revision, remote.ConnectorId);
            Assert.True(FlowGraphV2Validator.ValidatePublished(graph).Valid);
            aiNodeId = graph.Nodes.Single(node => node.Kind == FlowNodeKind.InvestigateWithAi).Id;
            incidentNodeId = graph.Nodes.Single(node => node.Kind == FlowNodeKind.CreateIncident).Id;
            var at = clock.GetUtcNow().AddMinutes(-5);
            var graphJson = FlowGraphSerialization.Serialize(graph);
            flowId = Guid.NewGuid(); versionId = Guid.NewGuid();
            var definition = new FlowDefinitionRecord { Id = flowId, TenantId = 17, Name = "Synthetic source incident",
                Revision = 1, Enabled = true, EnabledSinceUtc = at, DraftJson = graphJson,
                CreatedAtUtc = at, UpdatedAtUtc = at };
            db.FlowDefinitions.Add(definition);
            await db.SaveChangesAsync(ct);
            db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 17, VersionNumber = 1,
                GraphJson = graphJson, ConfigurationHash = FlowGraphSerialization.Hash(graph),
                PublishedBy = IncidentPublisher, PublishedAtUtc = at,
                ExecutionAuthorityJson = JsonSerializer.Serialize(new FlowExecutionAuthorityDto(IncidentPublisher), IncidentJson) });
            await db.SaveChangesAsync(ct);
            definition.PublishedVersionId = versionId; definition.PublishedVersionNumber = 1;
            await db.SaveChangesAsync(ct);
        }
        FlowExecutionEventAdmissionRequest admission;
        FlowExecutionEventDto fact;
        string sourcePayload;
        await using (var scope = original.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<IRequestService>();
            var request = await service.CreateAsync(new("synthetic-gateway", "", "4242", "{}", 17, agentId), ct);
            await service.UpdateAsync(new(request.Id, null, null, null, null, "Failed",
                failureReason, null, null, null), ct);
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var nativeId = request.Id.ToString(CultureInfo.InvariantCulture);
            var source = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionEventLimits.OutboxType && row.EntityId == nativeId, ct);
            sourcePayload = source.PayloadJson;
            fact = JsonSerializer.Deserialize(sourcePayload, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal(FlowExecutionSourceKind.Request, fact.SourceKind);
            Assert.Equal(FlowExecutionOutcome.Failed, fact.Outcome);
            Assert.Equal(FlowExecutionEventProvenance.RequestStateCommit, fact.Provenance);
            Assert.Equal("Synthetic request definition", fact.Definition.Name);
            Assert.Equal(failureReason, fact.Reason);
            var selections = await db.OutboxMessages.AsNoTracking().Where(row =>
                row.Type == FlowExecutionSubscriptionLimits.OutboxType).Select(row => row.PayloadJson).ToArrayAsync(ct);
            var selection = Assert.Single(selections.Select(value => JsonSerializer.Deserialize(value,
                FlowExecutionSubscriptionJsonContext.Default.FlowExecutionSubscriptionDeliveryDto)!),
                item => item.FlowVersionId == versionId && item.SourceEventId == fact.EventId);
            admission = new(17, versionId, fact.EventId, [fact.EventId], selection.DeliveryId);
            Assert.Equal(0, await db.JobRuns.CountAsync(ct));
            Assert.Equal(0, await db.MonitoringOccurrences.CountAsync(ct));
        }
        clock.Advance(TimeSpan.FromSeconds(1));
        var ingress = original.GetRequiredService<IFlowExecutionEventIngress>();
        var accepted = await ingress.EnqueueExecutionAsync(admission, ct);
        Assert.Equal(FlowIngressDisposition.Enqueued, accepted.Disposition);
        var runId = accepted.RunId!.Value;
        Assert.True(await original.GetRequiredService<FlowRunProcessor>().ProcessOneAsync(ct));
        await using (var db = new OrchestratorDbContext(options))
        {
            Assert.Equal(FlowRunStatus.WaitingForAi, (await db.FlowRuns.SingleAsync(row => row.Id == runId, ct)).Status);
            Assert.Equal(0, await db.Set<FlowReceiverEvidenceRecord>().CountAsync(ct));
        }

        var lifecycle = new WorkerLifecycle();
        using (var worker = new NetclawInvestigationWorker(original.GetRequiredService<IServiceScopeFactory>(),
            daemon, clock, lifecycle))
        {
            await worker.StartAsync(ct);
            try
            {
                await lifecycle.Ready.Task.WaitAsync(ct);
                while (true)
                {
                    await using var db = new OrchestratorDbContext(options);
                    var resultJson = await db.Set<AiInvestigationTurnResultRecord>().AsNoTracking()
                        .Select(row => row.ResultJson).SingleOrDefaultAsync(ct);
                    if (resultJson is not null)
                    {
                        var result = JsonSerializer.Deserialize<FlowAiResultDto>(resultJson, IncidentJson)!;
                        Assert.Equal(FlowAiResultStatus.Unsupported, result.Status);
                        Assert.Equal(runId, result.FlowRunId);
                        Assert.Equal(aiNodeId, result.ActionNodeId);
                        Assert.Null(result.Summary); Assert.Null(result.Evidence);
                        break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(25), ct);
                }
            }
            finally { await worker.StopAsync(default); }
        }
        Assert.Equal(0, daemon.Sends);
        clock.Advance(TimeSpan.FromSeconds(6));
        using var crashed = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var active = original.GetRequiredService<FlowRunProcessor>().ProcessOneAsync(crashed.Token);
        if (await Task.WhenAny(active, remote.Committed.Task).WaitAsync(ct) == active)
        {
            var claimed = await active;
            await using var inspect = new OrchestratorDbContext(options);
            var run = await inspect.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct);
            var actions = await inspect.FlowActions.AsNoTracking().Where(row => row.RunId == runId)
                .Select(row => new { row.NodeId, row.Status, row.Code }).ToArrayAsync(ct);
            Assert.Fail($"Worker ended before the receiver POST: claimed={claimed}, now={clock.GetUtcNow():O}, " +
                $"status={run.Status}, code={run.Code}, due={run.NextAttemptAtUtc:O}, " +
                $"actions={JsonSerializer.Serialize(actions, IncidentJson)}");
        }
        await remote.Committed.Task.WaitAsync(ct);
        string frozenDraft;
        string frozenPrepared;
        string frozenReceiver;
        string frozenEvent;
        await using (var db = new OrchestratorDbContext(options))
        {
            var action = await db.FlowActions.AsNoTracking().SingleAsync(row => row.RunId == runId && row.NodeId == incidentNodeId, ct);
            var evidence = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleAsync(ct);
            Assert.Equal(FlowActionStatus.Dispatching, action.Status);
            Assert.True(evidence.MayHaveCommitted);
            Assert.Null(action.ReceiptJson); Assert.Null(evidence.FullReceiptJson);
            frozenDraft = action.DraftJson; frozenPrepared = action.PreparedJson!; frozenReceiver = evidence.PreparationJson;
            frozenEvent = (await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct)).EventJson;
            var draft = JsonSerializer.Deserialize<FlowIncidentActionDraft>(frozenDraft, IncidentJson)!;
            Assert.Null(draft.Event.Data);
            Assert.Equal(fact.EventId, draft.Event.ExecutionEvent!.EventId);
            Assert.Contains("Original execution facts", draft.Fields.Description);
            Assert.Contains(failureReason, draft.Fields.Description);
            Assert.Contains("Mapped before investigation", draft.Fields.Description);
            Assert.Contains("Outcome: Unsupported", draft.Fields.Description);
            Assert.DoesNotContain("Recovered (", draft.Fields.Description);
            var prepared = JsonSerializer.Deserialize<FlowIncidentActionRequest>(frozenPrepared, IncidentJson)!;
            var wire = JsonSerializer.Deserialize<RatelDeskCreateIncidentDto>(remote.ExactBody!, IncidentJson)!;
            Assert.Equal(prepared.Fields.Description, wire.Description);
            Assert.Contains("Original execution facts", wire.Description);
            Assert.Contains("Outcome: Unsupported", wire.Description);
            Assert.Contains("Synthetic request target", wire.Description);
            Assert.Contains("Synthetic request definition", wire.Description);
            Assert.Contains("NetRatel execution occurrence", wire.Description);
            Assert.DoesNotContain("NetRatel monitoring occurrence", wire.Description);
            Assert.Contains("/requests", wire.Description);
            Assert.True(wire.Description.Length <= RatelDeskConnectorLimits.MaximumDescriptionCharacters);
            var aiRow = await db.AiInvestigations.SingleAsync(ct);
            await InvestigationStore.StageTurnResultAsync(db, aiRow, FlowAiResultStatus.Completed,
                clock.GetUtcNow(), ct, summary: "Late synthetic findings cannot replace the saved unsupported result.");
            var edited = await db.FlowDefinitions.SingleAsync(row => row.Id == flowId, ct);
            edited.Name = "Edited synthetic draft after first send"; edited.Revision++;
            var editedGraph = JsonSerializer.Deserialize<FlowGraphDto>(edited.DraftJson, IncidentJson)!;
            edited.DraftJson = JsonSerializer.Serialize(editedGraph with
            {
                Nodes = editedGraph.Nodes.Select(node => node.Kind == FlowNodeKind.MapIncident
                    ? node with { Mapping = new("Later draft title", "Later draft must not change the original frozen payload.") }
                    : node).ToArray()
            }, IncidentJson);
            await db.SaveChangesAsync(ct);
            Assert.Equal(1, reader.Reads);
        }
        // End the old lease before cancelling its blocked transport. Its later completion cannot
        // save a receipt; a new provider must reconcile the receiver's original committed identity.
        clock.Advance(FlowLimits.LeaseDuration + TimeSpan.FromSeconds(1));
        crashed.Cancel();
        await active.WaitAsync(ct);
        await original.DisposeAsync();
        await using var restarted = IncidentProvider(options, identityOptions, clock, daemon, remote, reader, protection);
        var duplicate = await restarted.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(admission, ct);
        Assert.Equal(FlowIngressDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(runId, duplicate.RunId);
        Assert.True(await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync(ct));

        string receiptBytes;
        await using (var db = new OrchestratorDbContext(options))
        {
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct);
            var action = await db.FlowActions.AsNoTracking().SingleAsync(row => row.RunId == runId && row.NodeId == incidentNodeId, ct);
            var evidence = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleAsync(ct);
            Assert.Equal(FlowRunStatus.Succeeded, run.Status);
            Assert.Equal(FlowActionStatus.Succeeded, action.Status);
            Assert.Equal(frozenDraft, action.DraftJson);
            Assert.Equal(frozenPrepared, action.PreparedJson);
            Assert.Equal(frozenReceiver, evidence.PreparationJson);
            Assert.Equal(frozenEvent, run.EventJson);
            Assert.False(evidence.MayHaveCommitted);
            receiptBytes = evidence.FullReceiptJson!;
            var receipt = JsonSerializer.Deserialize<RatelDeskVerifiedReceipt>(receiptBytes, IncidentJson)!;
            Assert.Equal(remote.Receipt!.ExactAcceptedBodyJson, receipt.ExactAcceptedBodyJson);
            Assert.Equal(remote.Receipt.IncidentId, JsonSerializer.Deserialize<FlowActionReceiptDto>(action.ReceiptJson!, IncidentJson)!.IncidentId);
            Assert.Equal(sourcePayload, (await db.OutboxMessages.AsNoTracking().SingleAsync(row => row.Id == fact.EventId, ct)).PayloadJson);
            Assert.Equal(1, await db.FlowRuns.CountAsync(ct));
            Assert.Equal(2, await db.FlowActions.CountAsync(ct));
            Assert.Equal(0, await db.JobRuns.CountAsync(ct));
            Assert.Equal(0, await db.MonitoringOccurrences.CountAsync(ct));
            Assert.Equal(1, await db.Set<AiInvestigationTurnResultRecord>().CountAsync(ct));
            var originalAiResult = JsonSerializer.Deserialize<FlowAiResultDto>((await db.Set<AiInvestigationTurnResultRecord>()
                .AsNoTracking().SingleAsync(ct)).ResultJson, IncidentJson)!;
            Assert.Equal(FlowAiResultStatus.Unsupported, originalAiResult.Status);
            Assert.Null(originalAiResult.Summary);
        }
        Assert.Equal(1, remote.Creates);
        Assert.Equal(1, remote.Lookups);
        Assert.Equal(1, reader.Reads);
        Assert.Equal(0, daemon.Sends);
        Assert.Equal(FlowIngressDisposition.Duplicate, (await restarted.GetRequiredService<IFlowExecutionEventIngress>()
            .EnqueueExecutionAsync(admission, ct)).Disposition);
        Assert.False(await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync(ct));
        await using (var db = new OrchestratorDbContext(options))
            Assert.Equal(receiptBytes, (await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleAsync(ct)).FullReceiptJson);
        Assert.Equal(1, remote.Creates); Assert.Equal(1, remote.Lookups);
    }

    private static ServiceProvider IncidentProvider(DbContextOptions<OrchestratorDbContext> options,
        DbContextOptions<NetRatelIdentityDbContext> identityOptions, RecoveryClock clock, Daemon daemon,
        IncidentReceiver remote, IncidentReaderCounter reader, IDataProtectionProvider protection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(options); services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton(identityOptions); services.AddScoped<NetRatelIdentityDbContext>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDataProtectionProvider>(protection);
        services.AddSingleton<INetclawPairingService, Pairing>();
        services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        services.AddScoped<NetclawAuthority>(); services.AddScoped<NetclawProfileService>();
        services.AddScoped<InvestigationStore>(); services.AddScoped<NetclawFlowConnector>();
        services.AddScoped<IFlowInvestigationToolPolicy, NativeFlowInvestigationToolPolicy>();
        services.AddNetRatelFlows();
        services.AddScoped<IRequestService, RequestService>();
        services.AddSingleton<IFlowReceiverEvidenceStore>(provider => provider.GetRequiredService<FlowPersistenceService>());
        services.AddSingleton<IRatelDeskReceiverFingerprint, RatelDeskReceiverFingerprint>();
        services.AddSingleton<ReceiverPreparationBuilder>();
        services.AddScoped<IRatelDeskConnectorAuthorization, RatelDeskConnectorAuthorization>();
        services.AddScoped<RatelDeskConnectorStore>();
        services.AddScoped<IRatelDeskConnectorStore>(provider => provider.GetRequiredService<RatelDeskConnectorStore>());
        services.AddScoped<IRatelDeskConnectorBindingStore>(provider => provider.GetRequiredService<RatelDeskConnectorStore>());
        services.AddSingleton<IRatelDeskOutboundBindingResolver>(remote);
        services.AddSingleton<IRatelDeskReceiverTransport>(remote);
        services.AddScoped<IFlowReceiverDispatcher, RatelDeskFlowReceiverAdapter>();
        services.AddScoped(provider =>
        {
            Interlocked.Increment(ref reader.Reads);
            return new FlowIncidentEvidenceReader(provider.GetRequiredService<OrchestratorDbContext>());
        });
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static FlowGraphDto IncidentGraph(Guid aiConnectorId, long aiRevision, Guid incidentConnectorId)
    {
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Request admission failed", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
        var mapping = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.MapIncident, "Map before investigation", new(200, 0),
            Mapping: new("Synthetic request failure", "Mapped before investigation: {targetName}, request {executionId}: {reason}"));
        var ai = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "Investigate request", new(400, 0),
            ConnectorId: aiConnectorId, ConnectorRevision: aiRevision) { AiSettings = new() };
        var incident = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.CreateIncident, "Create enriched incident", new(600, 0),
            ConnectorId: incidentConnectorId, ConnectorRevision: 1);
        var edges = new List<FlowEdgeDto>
        {
            new(trigger.Id, FlowNodeCatalogue.Event, mapping.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
            new(mapping.Id, FlowNodeCatalogue.Result, ai.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }
        };
        edges.AddRange(FlowNodeCatalogue.Find(ai.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => new FlowEdgeDto(ai.Id, port.Id, incident.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        return new(2, [trigger, mapping, ai, incident], edges, new());
    }

    private sealed class IncidentReaderCounter { public int Reads; }

    private sealed class IncidentReceiver(RecoveryClock clock, DbContextOptions<OrchestratorDbContext> options) :
        IRatelDeskOutboundBindingResolver, IRatelDeskReceiverTransport
    {
        public Guid ConnectorId { get; } = Guid.NewGuid();
        private readonly Guid sourceNamespace = Guid.NewGuid();
        private readonly string receiverId = Guid.NewGuid().ToString("D");
        private readonly string credentialId = Guid.NewGuid().ToString("D");
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Creates { get; private set; }
        public int Lookups { get; private set; }
        public RatelDeskVerifiedReceipt? Receipt { get; private set; }
        public string? ExactBody => committedPreparation?.ExactCreateBodyJson;
        private RatelDeskReceiverPreparationV2? committedPreparation;

        public RatelDeskConnectorState FixtureConnector => new(ConnectorId, 17, 1, 1, IncidentPublisher,
            new("Synthetic receiver", "https://receiver.example.test/help", "organization-17", "customer-17", null,
                [], new(), true), "synthetic-protected-binding", 1)
            { Authentication = new(RatelDeskAuthenticationMode.PairedSystem, credentialId) };
        public Task<RatelDeskSemanticPeer> CaptureAsync(RatelDeskConnectorState current, RatelDeskConnectorAuthentication authentication,
            Guid source, CancellationToken ct) => Task.FromResult(new RatelDeskSemanticPeer(RatelDeskAuthenticationMode.PairedSystem,
                17, ConnectorId, credentialId, 1, new string('a', 64), receiverId, "organization-17",
                current.Configuration.Origin, "https://issuer.example.test/services", "rateldesk-api",
                "https://receiver.example.test/help/connect/token", "synthetic-client", "paired", source, sourceNamespace,
                "organization-17", "customer-17", null, []));
        public Task<string> GetBearerAsync(RatelDeskConnectorState current, RatelDeskSemanticPeer peer, string scope, CancellationToken ct) =>
            Task.FromResult("synthetic-receiver-bearer");
        public Task<RatelDeskVerifiedCapability> CapabilitiesAsync(RatelDeskSemanticPeer peer, string bearer, CancellationToken ct) =>
            Task.FromResult(new RatelDeskVerifiedCapability(ReceiverWireValidation.Contract, peer.ReceiverInstanceId,
                peer.SourceInstanceId, peer.SourceNamespaceId,
                new(ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CapabilitiesPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.CreatePath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.ReceiptPath),
                    ReceiverWireValidation.Endpoint(peer.ApiBaseUrl, ReceiverWireValidation.TargetsPath)),
                ReceiverWireValidation.MinimumRetention, ReceiverWireValidation.MaximumReplay, clock.GetUtcNow()));
        public Task ValidateTargetsAsync(RatelDeskSemanticPeer peer, RatelDeskVerifiedCapability capability, string bearer, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<RatelDeskReceiverObservation> LookupAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        {
            Lookups++;
            Assert.NotNull(committedPreparation);
            Assert.Equal(committedPreparation.ReceiverIdempotencyKey, prepared.ReceiverIdempotencyKey);
            Assert.Equal(committedPreparation.ExactCreateBodyJson, prepared.ExactCreateBodyJson);
            Assert.Equal(committedPreparation.ReceiverFingerprint, prepared.ReceiverFingerprint);
            return Task.FromResult(new RatelDeskReceiverObservation(RatelDeskReceiverObservationKind.Committed,
                "synthetic-receiver-committed", Receipt));
        }
        public async Task<RatelDeskReceiverObservation> CreateAsync(RatelDeskReceiverPreparationV2 prepared, string bearer, CancellationToken ct)
        {
            await using (var db = new OrchestratorDbContext(options))
            {
                var persisted = await db.Set<FlowReceiverEvidenceRecord>().AsNoTracking().SingleAsync(ct);
                Assert.True(persisted.MayHaveCommitted);
                Assert.Null(persisted.FullReceiptJson);
            }
            Creates++;
            Assert.Null(committedPreparation);
            committedPreparation = prepared;
            const string incident = "synthetic-native-request-incident";
            const string tracking = "SYNTHETIC-request";
            const string location = "/help/api/v1/incidents/synthetic-native-request-incident";
            var raw = JsonSerializer.SerializeToUtf8Bytes(new
            {
                id = incident, trackingId = tracking, organizationId = prepared.Peer.OrganizationId, customerId = prepared.Peer.CustomerId,
                integrationReceipt = new
                {
                    contractVersion = ReceiverWireValidation.Contract, receiverInstanceId = prepared.Peer.ReceiverInstanceId,
                    sourceNamespaceId = prepared.Peer.SourceNamespaceId.ToString("D"), sourceInstanceId = prepared.Peer.SourceInstanceId.ToString("D"),
                    key = prepared.ReceiverIdempotencyKey, fingerprint = prepared.ReceiverFingerprint, outcome = "committed",
                    incidentId = incident, trackingId = tracking, organizationId = prepared.Peer.OrganizationId,
                    customerId = prepared.Peer.CustomerId, committedAtUtc = clock.GetUtcNow(), location
                }
            });
            Receipt = ReceiverWireValidation.Receipt(raw, location, prepared);
            Committed.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            throw new InvalidOperationException("The synthetic committed response was intentionally lost.");
        }
    }
}
