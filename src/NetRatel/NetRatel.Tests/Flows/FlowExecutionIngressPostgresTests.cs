using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Requests;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionIngressPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Real_request_failure_and_selected_intent_survive_duplicate_handoff_and_provider_restart()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var selected = await h.FailRequestAsync();
        await using var restarted = h.NewProvider();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(index =>
            (index % 2 == 0 ? h.Provider : restarted).GetRequiredService<IFlowExecutionEventIngress>()
                .EnqueueExecutionAsync(selected.Admission)));
        Assert.Equal(1, results.Count(result => result.Disposition == FlowIngressDisposition.Enqueued));
        Assert.Equal(5, results.Count(result => result.Disposition == FlowIngressDisposition.Duplicate));
        var runId = Assert.Single(results.Select(result => result.RunId).Distinct());
        Assert.NotNull(runId);
        var lease = await restarted.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid());
        Assert.NotNull(lease); Assert.Equal(runId.Value, lease.RunId);
        Assert.Null(lease.Event.Data);
        Assert.Equal(selected.Fact.EventId, lease.Event.EventId);
        Assert.Equal(selected.Fact.EventId, lease.Event.ExecutionEvent!.EventId);
        Assert.Equal(FlowExecutionSourceKind.Request, lease.Event.ExecutionEvent.SourceKind);
        Assert.Equal(FlowExecutionOutcome.Failed, lease.Event.ExecutionEvent.Outcome);
        Assert.Equal(selected.RequestId.ToString(CultureInfo.InvariantCulture), lease.Event.ExecutionEvent.ExecutionId);
        Assert.True(FlowContractValidation.ValidEnvelope(lease.Event));
        var admitted = await h.AdmitAsync(lease);
        Assert.True(admitted.Allowed, admitted.Code);

        await using var scope = restarted.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var run = await db.FlowRuns.SingleAsync();
        Assert.NotNull(lease.Event.SourceAdmissionKey);
        Assert.Equal(lease.Event.SourceAdmissionKey.Value, run.EventId);
        Assert.NotEqual(selected.Fact.EventId, run.EventId);
        Assert.Equal(selected.SourcePayload, (await db.OutboxMessages.SingleAsync(row => row.Id == selected.Fact.EventId)).PayloadJson);
        Assert.Single(await db.OutboxProcessedEvents.Where(row => row.EventId == selected.Fact.EventId).ToArrayAsync());
        using var envelope = JsonDocument.Parse(run.EventJson);
        Assert.False(envelope.RootElement.TryGetProperty("data", out _));
        Assert.Equal(0, await db.JobRuns.CountAsync());
        Assert.Equal(0, await db.FlowActions.CountAsync());
        Assert.Equal(0, h.Grants.MonitoringReadCalls);
        Assert.Equal(AccessPolicy.PrincipalId, h.Grants.NativePrincipalObserved);
        Assert.NotEqual("synthetic-gateway", h.Grants.NativePrincipalObserved);
    }

    [Fact]
    public async Task Current_native_permission_revocation_blocks_unstarted_source_admission_without_monitoring_access()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var selected = await h.FailRequestAsync();
        var queued = await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(selected.Admission);
        Assert.Equal(FlowIngressDisposition.Enqueued, queued.Disposition);
        var lease = (await h.Provider.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid()))!;
        Assert.True((await h.AdmitAsync(lease)).Allowed);
        h.Grants.NativeRead = false;
        var denied = await h.AdmitAsync(lease);
        Assert.False(denied.Allowed); Assert.Equal("execution-source-read-denied", denied.Code);
        var duplicate = await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(selected.Admission);
        Assert.Equal(FlowIngressDisposition.Duplicate, duplicate.Disposition); Assert.Equal(queued.RunId, duplicate.RunId);
        var fresh = await h.FailRequestAsync();
        var rejected = await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(fresh.Admission);
        Assert.Equal(FlowIngressDisposition.Disabled, rejected.Disposition);
        Assert.Equal("execution-source-read-denied", rejected.Code);
        await using var scope = h.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, await db.FlowActions.CountAsync()); Assert.Equal(0, await db.JobRuns.CountAsync());
        Assert.Equal(0, h.Grants.MonitoringReadCalls);
    }

    [Fact]
    public async Task Changed_enable_interval_rejects_old_selection_without_rewriting_the_accepted_envelope()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var selected = await h.FailRequestAsync();
        await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(selected.Admission);
        var lease = (await h.Provider.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid()))!;
        Assert.True((await h.AdmitAsync(lease)).Allowed);
        string before;
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            before = (await db.FlowRuns.SingleAsync()).EventJson;
            var definition = await db.FlowDefinitions.SingleAsync(row => row.Id == h.FlowId);
            definition.EnabledSinceUtc = selected.Fact.OccurredAtUtc.AddSeconds(1);
            definition.Revision++;
            await db.SaveChangesAsync();
        }
        Assert.False((await h.AdmitAsync(lease)).Allowed);
        await using var verification = h.Provider.CreateAsyncScope();
        var verify = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(before, (await verify.FlowRuns.SingleAsync()).EventJson);
        Assert.Equal(selected.SourcePayload, (await verify.OutboxMessages.SingleAsync(row => row.Id == selected.Fact.EventId)).PayloadJson);
        Assert.Equal(0, await verify.FlowActions.CountAsync());
    }

    [Theory]
    [InlineData("foreign-tenant")]
    [InlineData("missing-fact")]
    [InlineData("wrong-selection")]
    public async Task Fact_and_selection_identifiers_cannot_bypass_tenant_or_immutable_delivery_proof(string shape)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var selected = await h.FailRequestAsync();
        var forged = shape switch
        {
            "foreign-tenant" => selected.Admission with { TenantId = 18 },
            "missing-fact" => selected.Admission with { ContributingEventIds = [Guid.NewGuid()] },
            _ => selected.Admission with { DeliveryId = Guid.NewGuid() }
        };
        var result = await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(forged);
        Assert.Equal(FlowIngressDisposition.Invalid, result.Disposition);
        Assert.Null(result.RunId);
        await using var scope = h.Provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, await db.FlowRuns.CountAsync()); Assert.Equal(0, await db.FlowActions.CountAsync());
        Assert.Equal(selected.SourcePayload, (await db.OutboxMessages.SingleAsync(row => row.Id == selected.Fact.EventId)).PayloadJson);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Existing_facts_and_deliveries_cannot_be_cross_paired(bool otherSubscription)
    {
        await using var h = await Harness.CreateAsync(postgres);
        var otherVersion = await h.AddSubscriptionAsync();
        var first = await h.FailRequestAsync();
        FlowExecutionEventAdmissionRequest crossed;
        if (otherSubscription)
        {
            await using var scope = h.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var rows = await db.OutboxMessages.AsNoTracking().Where(row => row.Type == FlowExecutionSubscriptionLimits.OutboxType)
                .Select(row => row.PayloadJson).ToArrayAsync();
            var delivery = Assert.Single(rows.Select(ParseDelivery), item => item.FlowVersionId == otherVersion &&
                item.SourceEventId == first.Fact.EventId);
            crossed = first.Admission with { DeliveryId = delivery.DeliveryId };
        }
        else
        {
            var second = await h.FailRequestAsync();
            crossed = first.Admission with { SourceEventId = second.Fact.EventId,
                ContributingEventIds = [second.Fact.EventId] };
        }
        var result = await h.Provider.GetRequiredService<IFlowExecutionEventIngress>().EnqueueExecutionAsync(crossed);
        Assert.Equal(FlowIngressDisposition.Invalid, result.Disposition);
        Assert.Null(result.RunId);
        await using var verification = h.Provider.CreateAsyncScope();
        var verify = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(0, await verify.FlowRuns.CountAsync());
        Assert.Equal(first.SourcePayload, (await verify.OutboxMessages.SingleAsync(row => row.Id == first.Fact.EventId)).PayloadJson);
    }

    [Fact]
    public async Task Separate_subscriptions_admit_independent_runs_and_receipts_for_the_same_native_fact()
    {
        await using var h = await Harness.CreateAsync(postgres);
        var otherVersion = await h.AddSubscriptionAsync();
        var selected = await h.FailRequestAsync();
        FlowExecutionSubscriptionDeliveryDto other;
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var rows = await db.OutboxMessages.AsNoTracking().Where(row => row.Type == FlowExecutionSubscriptionLimits.OutboxType)
                .Select(row => row.PayloadJson).ToArrayAsync();
            other = Assert.Single(rows.Select(ParseDelivery), item => item.FlowVersionId == otherVersion);
        }
        var ingress = h.Provider.GetRequiredService<IFlowExecutionEventIngress>();
        var first = await ingress.EnqueueExecutionAsync(selected.Admission);
        var second = await ingress.EnqueueExecutionAsync(new(17, otherVersion, selected.Fact.EventId,
            [selected.Fact.EventId], other.DeliveryId));
        Assert.Equal(FlowIngressDisposition.Enqueued, first.Disposition);
        Assert.Equal(FlowIngressDisposition.Enqueued, second.Disposition);
        Assert.NotEqual(first.RunId, second.RunId);
        await using var verification = h.Provider.CreateAsyncScope();
        var verify = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(2, await verify.FlowRuns.CountAsync());
        var receipts = await verify.OutboxProcessedEvents.Where(row => row.EventId == selected.Fact.EventId).ToArrayAsync();
        Assert.Equal(2, receipts.Length); Assert.Equal(2, receipts.Select(row => row.ConsumerName).Distinct().Count());
        Assert.Equal(0, await verify.JobRuns.CountAsync()); Assert.Equal(0, await verify.FlowActions.CountAsync());
    }

    private static FlowExecutionSubscriptionDeliveryDto ParseDelivery(string json) =>
        JsonSerializer.Deserialize(json, FlowExecutionSubscriptionJsonContext.Default.FlowExecutionSubscriptionDeliveryDto)!;
    private sealed record Selected(int RequestId, FlowExecutionEventDto Fact, string SourcePayload,
        FlowExecutionEventAdmissionRequest Admission);

    private sealed class AccessPolicy : IEffectiveAccessService, IFlowExecutionAuthorityVerifier
    {
        public const string PrincipalId = "synthetic-source-publisher";
        public bool NativeRead { get; set; } = true;
        public int MonitoringReadCalls { get; private set; }
        public string? NativePrincipalObserved { get; private set; }
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == 17 && authority.PrincipalId == PrincipalId && authority.IntegrationCredentialId is null);
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId,
            CancellationToken cancellationToken = default)
        {
            if (permission == NetRatelPermissions.MonitoringRead) MonitoringReadCalls++;
            var configuredPrincipal = principal.FindFirstValue("netratel_principal_id");
            if (permission == NetRatelPermissions.JobManagement) NativePrincipalObserved = configuredPrincipal;
            return Task.FromResult(tenantId == 17 && configuredPrincipal == PrincipalId &&
                permission != NetRatelPermissions.MonitoringRead &&
                (permission != NetRatelPermissions.JobManagement || NativeRead));
        }
        public Task<int[]?> GetAuthorizedTenantIdsAsync(ClaimsPrincipal principal, string permission,
            CancellationToken cancellationToken = default) => Task.FromResult<int[]?>([17]);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(string connection) { Connection = connection; Provider = NewProvider(); }
        private string Connection { get; }
        public AccessPolicy Grants { get; } = new();
        public ServiceProvider Provider { get; }
        public Guid FlowId { get; private set; }
        private Guid VersionId { get; set; }

        public ServiceProvider NewProvider()
        {
            var services = new ServiceCollection();
            services.AddLogging(); services.AddDataProtection();
            services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(Connection));
            services.AddSingleton<IEffectiveAccessService>(Grants);
            services.AddSingleton<IFlowExecutionAuthorityVerifier>(Grants);
            services.AddNetRatelFlows(); services.AddScoped<IRequestService, RequestService>();
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        }
        public static async Task<Harness> CreateAsync(PostgreSqlPersistenceFixture fixture)
        {
            var harness = new Harness(await fixture.CreateDatabaseAsync());
            await using (var scope = harness.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                db.Tenants.AddRange(new Tenant { Id = 17, Name = "Synthetic source tenant" },
                    new Tenant { Id = 18, Name = "Separate synthetic tenant" });
                await db.SaveChangesAsync();
            }
            harness.VersionId = await harness.AddSubscriptionAsync();
            return harness;
        }
        public async Task<Guid> AddSubscriptionAsync()
        {
            var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic request failure", new(0, 0))
            { ExecutionTrigger = new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
            var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Record original failure", new(360, 0));
            var graph = new FlowGraphDto(2, [trigger, finish],
                [new(trigger.Id, FlowNodeCatalogue.Event, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
            var valid = FlowGraphV2Validator.ValidatePublished(graph);
            Assert.True(valid.Valid, string.Join(", ", valid.Issues.Select(issue => issue.Code)));
            var time = DateTimeOffset.UtcNow.AddMinutes(-5);
            var json = FlowGraphSerialization.Serialize(graph);
            var flow = new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = 17, Name = "Synthetic source flow",
                Revision = 1, Enabled = true, EnabledSinceUtc = time, DraftJson = json, CreatedAtUtc = time, UpdatedAtUtc = time };
            var version = new FlowVersionRecord { Id = Guid.NewGuid(), FlowId = flow.Id, TenantId = 17, VersionNumber = 1,
                GraphJson = json, ConfigurationHash = FlowGraphSerialization.Hash(graph),
                PublishedBy = AccessPolicy.PrincipalId, PublishedAtUtc = time,
                ExecutionAuthorityJson = JsonSerializer.Serialize(new FlowExecutionAuthorityDto(AccessPolicy.PrincipalId), Json) };
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.FlowDefinitions.Add(flow); await db.SaveChangesAsync();
            db.FlowVersions.Add(version); await db.SaveChangesAsync();
            flow.PublishedVersionId = version.Id; flow.PublishedVersionNumber = 1;
            await db.SaveChangesAsync();
            if (FlowId == Guid.Empty) FlowId = flow.Id;
            return version.Id;
        }
        public async Task<Selected> FailRequestAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var requests = scope.ServiceProvider.GetRequiredService<IRequestService>();
            var created = await requests.CreateAsync(new("synthetic-gateway", "", "4242", "{}", 17, Guid.NewGuid()));
            var failed = await requests.UpdateAsync(new(created.Id, null, null, null, null, "Failed",
                "Synthetic admission was rejected before a job run existed.", null, null, null));
            Assert.NotNull(failed);
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var requestId = created.Id.ToString(CultureInfo.InvariantCulture);
            var row = await db.OutboxMessages.AsNoTracking().SingleAsync(item =>
                item.Type == FlowExecutionEventLimits.OutboxType && item.EntityId == requestId);
            var fact = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            var intentRows = await db.OutboxMessages.AsNoTracking().Where(item =>
                item.Type == FlowExecutionSubscriptionLimits.OutboxType).Select(item => item.PayloadJson).ToArrayAsync();
            var intent = Assert.Single(intentRows.Select(ParseDelivery),
                item => item.FlowVersionId == VersionId && item.SourceEventId == fact.EventId);
            return new(created.Id, fact, row.PayloadJson, new(17, VersionId, fact.EventId, [fact.EventId], intent.DeliveryId));
        }
        public async Task<FlowDispatchDecision> AdmitAsync(FlowRunLease lease)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync();
            return await scope.ServiceProvider.GetRequiredService<IFlowTransactionAdmission>().CanStartActionAsync(db, lease);
        }
        public ValueTask DisposeAsync() => Provider.DisposeAsync();
    }
}
