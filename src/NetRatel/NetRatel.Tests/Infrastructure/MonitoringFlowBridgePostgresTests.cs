using System.Collections.Immutable;
using System.Text.Json;
using Akka.Actor;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NetRatel.API.Services.Monitoring;
using NetRatel.Akka.Monitoring;
using NetRatel.Application.Flows;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Application.Telemetry;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Tests.Flows;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class MonitoringFlowBridgePostgresTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task AdmittedActorRaiseExecutesActualVeloxOnceAndPersistsMatchingIncidentReceipt()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        rig.Ingress.Enqueues.Should().Be(1);
        var run = (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Single();
        run.Status.Should().Be(FlowRunStatus.Queued);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Sends.Should().Be(1);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var state = (await rig.Store.LoadSeriesAsync(rig.Key, default))!;
        state.Occurrence!.OccurrenceId.Should().Be(firing.Occurrence!.OccurrenceId);
        state.Occurrence.FlowOutcome!.FlowRunId.Should().Be(run.Id);
        state.Occurrence.FlowOutcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Succeeded);
        state.Occurrence.FlowOutcome.Receipt.Should().Be(new MonitoringIncidentReceiptDto("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge"));
        rig.Dispatcher.LastRequest!.Event.Authority.Should().Be(rig.Authority);
        rig.Dispatcher.LastRequest.Event.Data.ClientName.Should().Be(rig.Client.AgentId.ToString("D"));
        await rig.RecordAsync(4);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedRunWithLostHandoffReceiptRecoversByLookupOnlyAcrossProviderRestart(bool knownMarker)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        var admitted = await rig.Ingress.EnqueueAsync(MonitoringFlowEventFactory.Create(lease.Intent)!);
        admitted.Disposition.Should().Be(FlowIngressDisposition.Enqueued);
        if (knownMarker) (await rig.Store.MarkOutboxHandedOffAsync(lease, admitted.RunId!.Value, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        var completed = (await rig.Ingress.GetOutcomeAsync(rig.Client.TenantId, admitted.RunId!.Value))!;
        completed.Status.Should().Be(FlowRunStatus.Succeeded);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        if (!knownMarker)
            (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
                new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, rig.Clock.GetUtcNow(), "lost_handoff_marker"), "lost_handoff_marker"), default)).Should().BeTrue();
        // The known case crashes after recording its real run ID and before
        // releasing its lease. The unknown case lost that marker completely.
        await using var restarted = rig.NewProvider();
        var restartedIngress = restarted.GetRequiredService<CountingIngress>();
        var restartedStore = restarted.GetRequiredService<IMonitoringStore>();
        var receipt = (await restartedStore.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        receipt.FlowRunId.Should().Be(knownMarker ? admitted.RunId : null);
        (await restarted.GetRequiredService<MonitoringFlowOutboxProcessor>().ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        restartedIngress.Enqueues.Should().Be(0);
        restartedIngress.Finds.Should().Be(knownMarker ? 0 : 1);
        restartedIngress.Gets.Should().Be(knownMarker ? 1 : 0);
        var state = (await restartedStore.LoadSeriesAsync(rig.Key, default))!;
        state.Occurrence!.FlowOutcome!.FlowRunId.Should().Be(admitted.RunId);
        state.Occurrence.FlowOutcome.Receipt!.IncidentId.Should().Be("bridge-incident");
        state.Occurrence.FlowOutcome.OccurredAtUtc.Should().Be(rig.Clock.GetUtcNow());
        state.Occurrence.FlowOutcome.OccurredAtUtc.Should().BeAfter(completed.CompletedAtUtc!.Value);
        (await restarted.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();
    }

    [Theory]
    [InlineData("clear")]
    [InlineData("bypass")]
    [InlineData("principal")]
    [InlineData("credential")]
    [InlineData("credential-owner")]
    [InlineData("credential-expiry")]
    [InlineData("credential-grants")]
    public async Task RealCurrentClearBypassAndExecutionGrantsAreRecheckedAfterActionPreparation(string change)
    {
        await using var rig = await Rig.CreateAsync(postgres);
        var firing = await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        await rig.Bridge.ProcessLeaseAsync(lease, default);
        rig.Dispatcher.BeforePrepare = async () =>
        {
            if (change == "clear")
            {
                var command = new MonitoringOperatorCommand(rig.Key, firing.Occurrence!.OccurrenceId,
                    Guid.Parse(rig.Authority.PrincipalId), "clear during connector preparation");
                (await rig.Actor.Ask<MonitoringStoreWriteResult>(new ClearMonitoringOccurrence(command))).Disposition.Should().Be(MonitoringStoreWriteDisposition.Stored);
            }
            else if (change == "bypass")
            {
                var bypass = new MonitoringBypassDto(Guid.NewGuid(), rig.Client.TenantId, rig.Rule.RuleId, rig.Client.AgentId, "cpu",
                    Guid.Parse(rig.Authority.PrincipalId), "suppress during connector preparation", rig.Clock.GetUtcNow(), rig.Clock.GetUtcNow().AddMinutes(1));
                (await rig.Configuration.SaveBypassAsync(new(bypass, 1), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
            }
            else
            {
                await using var scope = rig.Provider.CreateAsyncScope();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                if (change == "principal")
                {
                    var roleId = await identity.PrincipalRoleAssignments.Where(row => row.PrincipalId == rig.Authority.PrincipalId)
                        .Select(row => row.RoleId).SingleAsync();
                    identity.AccessRolePermissions.Remove(await identity.AccessRolePermissions.SingleAsync(row => row.RoleId == roleId && row.Permission == NetRatelPermissions.FlowExecute));
                }
                else
                {
                    var credential = await identity.IntegrationCredentials.Include(item => item.Grants).SingleAsync();
                    if (change == "credential-owner")
                    {
                        var otherPrincipal = ApplicationPrincipal.CreateId();
                        identity.ApplicationPrincipals.Add(new() { Id = otherPrincipal });
                        credential.OwnerPrincipalId = otherPrincipal;
                    }
                    else if (change == "credential-expiry") credential.ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
                    else if (change == "credential-grants")
                        credential.Grants.Remove(credential.Grants.Single(grant => grant.Permission == NetRatelPermissions.FlowExecute));
                    else credential.RevokedAtUtc = DateTimeOffset.UtcNow;
                }
                await identity.SaveChangesAsync();
            }
        };
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        rig.Dispatcher.Preparations.Should().Be(1);
        rig.Dispatcher.Sends.Should().Be(0);
        var run = (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Single();
        run.Status.Should().Be(FlowRunStatus.Failed);
        rig.Clock.Advance(TimeSpan.FromSeconds(5));
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!.FlowRunId.Should().Be(run.Id);
        await using var verify = rig.Provider.CreateAsyncScope();
        (await verify.ServiceProvider.GetRequiredService<OrchestratorDbContext>().FlowActions.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RepeatedUnknownReceiptObservationsPreserveHistoryAndLaterVerifiedSuccessReconciles()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var lease = await rig.ClaimAsync();
        var admitted = await rig.Ingress.EnqueueAsync(MonitoringFlowEventFactory.Create(lease.Intent)!);
        var runId = admitted.RunId!.Value;
        (await rig.Store.MarkOutboxHandedOffAsync(lease, runId, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeTrue();
        // The remote effect and FlowRun succeeded, but its monitoring receipt
        // has not yet been verified after the handoff response was interrupted.
        var unknown = new MonitoringFlowOutcomeDto(runId, MonitoringFlowOutcomeKind.DeliveryUnknown,
            rig.Clock.GetUtcNow(), "receipt_verification_unknown");
        (await rig.Store.CompleteOutboxAsync(new(lease, MonitoringOutboxStatus.DeliveryUnknown,
            unknown, unknown.Code), default)).Should().BeTrue();
        var receipt = (await rig.Store.ListUnsettledFlowRunsAsync(rig.Client.TenantId, 4, default)).Single();
        var before = await CaptureAsync();
        for (var observation = 0; observation < 3; observation++)
        {
            rig.Clock.Advance(TimeSpan.FromSeconds(5));
            (await rig.Store.ReconcileOutboxOutcomeAsync(receipt,
                unknown with { OccurredAtUtc = rig.Clock.GetUtcNow() }, default)).Should().BeTrue();
            (await CaptureAsync()).Should().Be(before);
        }
        foreach (var invalidTime in new[] { default(DateTimeOffset), rig.Clock.GetUtcNow().AddSeconds(1) })
        {
            await Assert.ThrowsAsync<ArgumentException>(() => rig.Store.ReconcileOutboxOutcomeAsync(receipt,
                unknown with { OccurredAtUtc = invalidTime }, default));
            (await CaptureAsync()).Should().Be(before);
        }

        (await rig.Bridge.ReconcileReceiptAsync(receipt, default)).Should().BeTrue();
        var after = await CaptureAsync();
        after.StateRevision.Should().Be(before.StateRevision + 1);
        after.LeaseFence.Should().Be(before.LeaseFence + 1);
        after.Status.Should().Be(MonitoringOutboxStatus.Completed);
        var outcome = (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!;
        outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Succeeded);
        outcome.FlowRunId.Should().Be(runId);
        outcome.Receipt.Should().Be(new MonitoringIncidentReceiptDto("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge"));
        rig.Ingress.Enqueues.Should().Be(1);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        await rig.AssertOneEffectAsync();

        async Task<DurableReceiptSnapshot> CaptureAsync()
        {
            await using var scope = rig.Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var series = await db.MonitoringSeries.AsNoTracking().SingleAsync();
            var occurrence = await db.MonitoringOccurrences.AsNoTracking().SingleAsync();
            var outbox = await db.MonitoringFlowOutbox.AsNoTracking().SingleAsync();
            return new(series.StateRevision, series.StateJson, series.UpdatedAtUtc, occurrence.OccurrenceJson,
                outbox.LeaseFence, outbox.OutcomeJson, outbox.Status);
        }
    }

    [Fact]
    public async Task DisabledImmutablePinProducesItsRealFailedRunAndCannotResumeAfterEnable()
    {
        await using var rig = await Rig.CreateAsync(postgres);
        await rig.FireAsync();
        var definition = (await rig.Definitions.GetAsync(rig.Client.TenantId, rig.Version.FlowId))!;
        var disabled = (await rig.Definitions.SetEnabledAsync(rig.Client.TenantId, definition.Id, new(definition.Revision, false), rig.Authority.PrincipalId)).Definition!;
        (await rig.Provider.GetRequiredService<IMonitoringPublishedFlowProvider>().IsPublishedAsync(rig.Client.TenantId, rig.Version.Id, default)).Should().BeTrue();
        (await rig.Provider.GetRequiredService<IMonitoringPublishedFlowProvider>().ListPublishedAsync(rig.Client.TenantId, 200, default)).Should().BeEmpty();
        var lease = await rig.ClaimAsync();
        (await rig.Bridge.ProcessLeaseAsync(lease, default)).Should().BeTrue();
        var outcome = (await rig.Store.LoadSeriesAsync(rig.Key, default))!.Occurrence!.FlowOutcome!;
        outcome.FlowRunId.Should().NotBeNull(); outcome.Outcome.Should().Be(MonitoringFlowOutcomeKind.Failed);
        (await rig.Ingress.GetOutcomeAsync(rig.Client.TenantId, outcome.FlowRunId!.Value))!.Status.Should().Be(FlowRunStatus.Failed);
        await rig.Definitions.SetEnabledAsync(rig.Client.TenantId, definition.Id, new(disabled.Revision, true), rig.Authority.PrincipalId);
        (await rig.Provider.GetRequiredService<FlowRunProcessor>().ProcessOneAsync()).Should().BeFalse();
        rig.Dispatcher.Sends.Should().Be(0);
        (await rig.Definitions.GetRunsAsync(rig.Client.TenantId, rig.Version.FlowId)).Should().ContainSingle();
    }

    private sealed record DurableReceiptSnapshot(ulong StateRevision, string StateJson, DateTimeOffset UpdatedAtUtc,
        string OccurrenceJson, long LeaseFence, string? OutcomeJson, MonitoringOutboxStatus Status);

    private sealed class Rig : IAsyncDisposable
    {
        public required string Connection { get; init; }
        public Clock Clock { get; } = new();
        public ClientKey Client { get; } = new(17, Guid.NewGuid());
        public FlowExecutionAuthorityDto Authority { get; } = new(ApplicationPrincipal.CreateId(), ApplicationPrincipal.CreateId());
        public EvidenceSource Evidence { get; } = new();
        public Dispatcher Dispatcher { get; } = new();
        public ServiceProvider Provider { get; private set; } = null!;
        public ActorSystem System { get; private set; } = null!;
        public IActorRef Actor { get; private set; } = null!;
        public FlowVersionDto Version { get; private set; } = null!;
        public MonitoringRuleDto Rule { get; private set; } = null!;
        public IMonitoringStore Store => Provider.GetRequiredService<IMonitoringStore>();
        public IMonitoringConfigurationStore Configuration => Provider.GetRequiredService<IMonitoringConfigurationStore>();
        public IFlowDefinitionService Definitions => Provider.GetRequiredService<IFlowDefinitionService>();
        public CountingIngress Ingress => Provider.GetRequiredService<CountingIngress>();
        public MonitoringFlowOutboxProcessor Bridge => Provider.GetRequiredService<MonitoringFlowOutboxProcessor>();
        public MonitoringSeriesKey Key => new(Client.TenantId, Rule.RuleId, Client.AgentId, "cpu");

        public static async Task<Rig> CreateAsync(PostgreSqlPersistenceFixture postgres)
        {
            var rig = new Rig { Connection = await postgres.CreateDatabaseAsync() };
            rig.Provider = rig.NewProvider();
            await using (var scope = rig.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                db.Tenants.Add(new() { Id = rig.Client.TenantId, Name = "Bridge tenant" });
                db.Agents.Add(new() { Id = rig.Client.AgentId, TenantId = rig.Client.TenantId, CreatedAtUtc = rig.Clock.GetUtcNow() });
                await db.SaveChangesAsync();
                var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
                await identity.Database.MigrateAsync();
                identity.ApplicationPrincipals.Add(new() { Id = rig.Authority.PrincipalId });
                var role = new AccessRole { Name = "Exact tenant flow execution" };
                role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowRead });
                role.Permissions.Add(new() { RoleId = role.Id, Permission = NetRatelPermissions.FlowExecute });
                identity.AccessRoles.Add(role);
                identity.PrincipalRoleAssignments.Add(new() { PrincipalId = rig.Authority.PrincipalId, RoleId = role.Id, TenantId = rig.Client.TenantId });
                identity.IntegrationCredentials.Add(new()
                {
                    Id = rig.Authority.IntegrationCredentialId!, PublicId = "bridge-fixture-public-id",
                    OwnerPrincipalId = rig.Authority.PrincipalId, Name = "Pinned bridge credential", Purpose = IntegrationCredentialPurpose.Api,
                    TokenPrefix = "nrt_ic_fixture", SecretHash = "deterministic-test-only", ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(2),
                    Grants = [new() { CredentialId = rig.Authority.IntegrationCredentialId!, TenantId = rig.Client.TenantId, Permission = NetRatelPermissions.FlowRead },
                        new() { CredentialId = rig.Authority.IntegrationCredentialId!, TenantId = rig.Client.TenantId, Permission = NetRatelPermissions.FlowExecute }]
                });
                await identity.SaveChangesAsync();
            }
            var flow = (await rig.Definitions.CreateAsync(rig.Client.TenantId, new("Actual bridge flow"), rig.Authority.PrincipalId)).Definition!;
            var draft = (await rig.Definitions.SaveDraftAsync(rig.Client.TenantId, flow.Id,
                new(flow.Revision, flow.Name, FlowTestData.Graph()), rig.Authority.PrincipalId)).Definition!;
            var published = await rig.Definitions.PublishAsync(rig.Client.TenantId, flow.Id, new(draft.Revision), rig.Authority);
            published.Disposition.Should().Be(FlowWriteDisposition.Stored); rig.Version = published.Version!;
            var epoch = await rig.Provider.GetRequiredService<IClientConnectionEpochStore>().AllocateAsync(rig.Client, 0, default);
            rig.Evidence.Current = new(rig.Client, Guid.NewGuid(), epoch, Guid.NewGuid());
            rig.Rule = new(rig.Client.TenantId, Guid.NewGuid(), 1, 1, "Bridge CPU threshold", true, MonitoringSeverity.Critical,
                new(MonitoringTargetMode.Selected, [rig.Client.AgentId], []),
                new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 90, 75, null, null, []),
                TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1), TimeSpan.FromMinutes(5), rig.Version.Id,
                rig.Authority.PrincipalId, rig.Authority.IntegrationCredentialId);
            (await rig.Configuration.SaveRuleAsync(new(rig.Rule, 0, Guid.Parse(rig.Authority.PrincipalId), "Configure actual bridge proof"), default)).Disposition.Should().Be(MonitoringConfigurationWriteDisposition.Stored);
            rig.System = ActorSystem.Create("bridge-pg-" + Guid.NewGuid().ToString("N"));
            rig.Actor = rig.System.ActorOf(ClientMonitoringRouterActor.Props(rig.Store, rig.Configuration,
                rig.Provider.GetRequiredService<IMonitoringClientDirectory>(), rig.Clock));
            (await rig.Actor.Ask<MonitoringInputResult>(new BeginMonitoringStream(rig.Evidence.Current))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
            return rig;
        }

        public ServiceProvider NewProvider()
        {
            var services = new ServiceCollection().AddLogging();
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
            services.AddDbContext<OrchestratorDbContext>(options => options.UseNpgsql(Connection));
            services.AddDbContext<NetRatelIdentityDbContext>(options => options.UseNpgsql(Connection));
            services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
            services.AddSingleton(Evidence);
            services.AddSingleton<IMonitoringClientDirectory, Directory>();
            services.AddNetRatelClientServicesPersistence().AddNetRatelMonitoringPersistence().AddNetRatelFlows();
            services.AddSingleton<IFlowConnectorCatalog, Catalog>();
            services.AddSingleton<IFlowIncidentActionDispatcher>(Dispatcher);
            services.AddMonitoringFlowBridge();
            services.AddSingleton<CountingIngress>(provider => new(provider.GetRequiredService<FlowPersistenceService>()));
            services.Replace(ServiceDescriptor.Singleton<IFlowEventIngress>(provider => provider.GetRequiredService<CountingIngress>()));
            return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        }

        public async Task<MonitoringSeriesState> FireAsync()
        {
            for (ulong sequence = 1; sequence <= 3; sequence++) await RecordAsync(sequence);
            var state = (await Store.LoadSeriesAsync(Key, default))!;
            state.Phase.Should().Be(MonitoringPhase.Firing);
            return state;
        }
        public async Task RecordAsync(ulong sequence)
        {
            Clock.Advance(TimeSpan.FromSeconds(1));
            var fence = Evidence.Current!;
            var telemetry = new MonitoringTelemetryInput(fence, new(Client, fence.ConnectionEpoch, sequence,
                Clock.GetUtcNow(), Clock.GetUtcNow(), new(95, null, null), null, [], [], null, IsAuthoritative: true));
            (await Actor.Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(telemetry))).Disposition.Should().Be(MonitoringInputDisposition.Accepted);
        }
        public async Task<MonitoringOutboxLease> ClaimAsync() =>
            (await Store.ClaimOutboxAsync(new(Client.TenantId, Guid.NewGuid(), 4, TimeSpan.FromMinutes(1)), default)).Single();
        public async Task AssertOneEffectAsync()
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.MonitoringEvents.CountAsync()).Should().Be(1);
            (await db.MonitoringFlowOutbox.CountAsync()).Should().Be(1);
            (await db.FlowRuns.CountAsync()).Should().Be(1);
            var action = await db.FlowActions.SingleAsync();
            action.Status.Should().Be(FlowActionStatus.Succeeded);
            JsonSerializer.Deserialize<FlowActionReceiptDto>(action.ReceiptJson!, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.IncidentId.Should().Be("bridge-incident");
            Dispatcher.Sends.Should().Be(1);
        }
        public async ValueTask DisposeAsync()
        {
            if (System is not null) await System.Terminate();
            await Provider.DisposeAsync();
        }
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 2, 20, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
    private sealed class EvidenceSource { public MonitoringEvidenceFence? Current { get; set; } }
    private sealed class Directory(IMonitoringAgentEligibility eligibility, EvidenceSource source) : IMonitoringClientDirectory
    {
        public Task<ImmutableArray<Guid>> GetEligibleAgentsAsync(int tenantId, CancellationToken cancellationToken) => eligibility.GetEligibleAgentsAsync(tenantId, cancellationToken);
        public Task<bool> IsEligibleAsync(ClientKey client, CancellationToken cancellationToken) => eligibility.IsEligibleAsync(client, cancellationToken);
        public Task<MonitoringEvidenceFence?> GetCurrentEvidenceAsync(ClientKey client, CancellationToken cancellationToken) =>
            Task.FromResult(source.Current?.Client == client ? source.Current : null);
    }
    private sealed class Catalog : IFlowConnectorCatalog
    {
        public Task<FlowConnectorReferenceDto?> GetAsync(int tenantId, Guid connectorId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            Task.FromResult<FlowConnectorReferenceDto?>(tenantId == 17 && connectorId == FlowTestData.ConnectorId
                ? new(connectorId, tenantId, "Deterministic in-process receiver", true, true, Revision: 1) : null);
        public async Task<IReadOnlyList<FlowConnectorReferenceDto>> ListAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken cancellationToken = default) =>
            (await GetAsync(tenantId, FlowTestData.ConnectorId, authority, cancellationToken)) is { } item ? [item] : [];
    }
    private sealed class Dispatcher : IFlowIncidentActionDispatcher
    {
        public int Preparations { get; private set; }
        public int Sends { get; private set; }
        public FlowIncidentActionRequest? LastRequest { get; private set; }
        public Func<Task>? BeforePrepare { get; set; }
        public async Task<FlowIncidentPreparationResult> PrepareAsync(FlowIncidentActionDraft draft, CancellationToken cancellationToken = default)
        {
            Preparations++;
            if (BeforePrepare is not null) await BeforePrepare();
            return new(FlowIncidentPreparationStatus.Ready, FlowTestData.Prepared(draft));
        }
        public Task<FlowIncidentActionResult> DispatchAsync(FlowIncidentActionRequest action, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Sends++; LastRequest = action;
            return Task.FromResult(new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "incident-created",
                new("bridge-incident", "bridge-tracking", "https://fixture.example/incidents/bridge")));
        }
    }
    private sealed class CountingIngress(IFlowEventIngress inner) : IFlowEventIngress
    {
        public int Enqueues { get; private set; }
        public int Finds { get; private set; }
        public int Gets { get; private set; }
        public Task<FlowIngressResult> EnqueueAsync(FlowEventEnvelope input, CancellationToken cancellationToken = default)
        { Enqueues++; return inner.EnqueueAsync(input, cancellationToken); }
        public Task<FlowRunSummaryDto?> GetOutcomeAsync(int tenantId, Guid runId, CancellationToken cancellationToken = default)
        { Gets++; return inner.GetOutcomeAsync(tenantId, runId, cancellationToken); }
        public Task<FlowRunSummaryDto?> FindOutcomeAsync(int tenantId, Guid eventId, Guid flowVersionId, CancellationToken cancellationToken = default)
        { Finds++; return inner.FindOutcomeAsync(tenantId, eventId, flowVersionId, cancellationToken); }
    }
}
