using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionContinuationPostgresTests(PostgreSqlPersistenceFixture postgres)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task A_yielded_native_continuation_uses_real_source_fact_selection_without_a_worker_lease()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var fixture = await SeedAsync(ct);
        await using var db = new OrchestratorDbContext(fixture.Options);
        var access = new Access();
        var verifier = new Verifier();
        var authority = Authority(db, access, verifier);
        await using var callerTransaction = await db.Database.BeginTransactionAsync(ct);

        await authority.RequireContinuationAsync(fixture.Identity, ct);

        Assert.Same(callerTransaction, db.Database.CurrentTransaction);
        var run = await db.FlowRuns.AsNoTracking().SingleAsync(ct);
        Assert.Equal(FlowRunStatus.WaitingForExecution, run.Status);
        Assert.Null(run.LeaseToken);
        Assert.Null(run.LeaseExpiresAtUtc);
        Assert.Empty(await db.JobTaskActivities.AsNoTracking().ToArrayAsync(ct));
        await callerTransaction.RollbackAsync(ct);
    }

    [Fact]
    public async Task Native_source_read_and_configuring_principal_revocation_deny_a_later_step()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var fixture = await SeedAsync(ct);
        var access = new Access();
        var verifier = new Verifier();
        await using (var current = new OrchestratorDbContext(fixture.Options))
            await Authority(current, access, verifier).RequireContinuationAsync(fixture.Identity, ct);
        access.Allowed = false;
        await using (var denied = new OrchestratorDbContext(fixture.Options))
            await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
                Authority(denied, access, verifier).RequireContinuationAsync(fixture.Identity, ct));
        access.Allowed = true;
        verifier.Allowed = false;
        await using var revoked = new OrchestratorDbContext(fixture.Options);
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            Authority(revoked, access, verifier).RequireContinuationAsync(fixture.Identity, ct));
    }

    [Fact]
    public async Task Another_action_key_or_identity_cannot_borrow_the_accepted_source_run()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var fixture = await SeedAsync(ct);
        var identities = new[]
        {
            fixture.Identity with { ActionKey = Guid.NewGuid() },
            fixture.Identity with { NodeId = Guid.NewGuid() },
            fixture.Identity with { FlowVersionId = Guid.NewGuid() },
            fixture.Identity with { TenantId = 8 },
            fixture.Identity with { PrincipalId = "another-operator" },
            fixture.Identity with { NodeAttempt = 4 }
        };
        foreach (var identity in identities)
        {
            await using var db = new OrchestratorDbContext(fixture.Options);
            await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
                Authority(db, new Access(), new Verifier()).RequireContinuationAsync(identity, ct));
        }
        await using var valid = new OrchestratorDbContext(fixture.Options);
        await Authority(valid, new Access(), new Verifier()).RequireContinuationAsync(fixture.Identity, ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disabled_or_reenabled_flow_cannot_resurrect_an_old_native_source(bool reenable)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var fixture = await SeedAsync(ct);
        await using (var configure = new OrchestratorDbContext(fixture.Options))
        {
            var definition = await configure.FlowDefinitions.SingleAsync(ct);
            definition.Enabled = false;
            await configure.SaveChangesAsync(ct);
            if (reenable)
            {
                definition.Enabled = true;
                definition.EnabledSinceUtc = fixture.Input.OccurredAtUtc.AddTicks(10);
                await configure.SaveChangesAsync(ct);
            }
        }
        await using var db = new OrchestratorDbContext(fixture.Options);

        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            Authority(db, new Access(), new Verifier()).RequireContinuationAsync(fixture.Identity, ct));

        var run = await db.FlowRuns.AsNoTracking().SingleAsync(ct);
        Assert.Equal(FlowRunStatus.WaitingForExecution, run.Status);
        Assert.Equal(fixture.Input.SourceAdmissionKey, run.EventId);
        Assert.Empty(await db.JobTaskActivities.AsNoTracking().ToArrayAsync(ct));
    }

    [Fact]
    public async Task A_terminal_flow_run_cannot_dispatch_another_native_step()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = deadline.Token;
        var fixture = await SeedAsync(ct);
        await using (var finish = new OrchestratorDbContext(fixture.Options))
        {
            var run = await finish.FlowRuns.SingleAsync(ct);
            run.Status = FlowRunStatus.Succeeded; run.CompletedAtUtc = DateTimeOffset.UtcNow;
            await finish.SaveChangesAsync(ct);
        }
        await using var db = new OrchestratorDbContext(fixture.Options);
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() =>
            Authority(db, new Access(), new Verifier()).RequireContinuationAsync(fixture.Identity, ct));
    }

    private async Task<Fixture> SeedAsync(CancellationToken ct)
    {
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync(ct);
        var active = DateTimeOffset.UtcNow.AddMinutes(-1);
        var flowId = Guid.NewGuid(); var versionId = Guid.NewGuid(); var triggerId = Guid.NewGuid();
        var nodeId = Guid.NewGuid(); var finishId = Guid.NewGuid(); var sourceInstance = Guid.NewGuid();
        var settings = new FlowExecutionTriggerSettingsDto([FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant);
        var graph = new FlowGraphDto(2,
            [new(triggerId, FlowNodeKind.ExecutionEvent, "Synthetic request failure", new(0, 0)) { ExecutionTrigger = settings },
             new(nodeId, FlowNodeKind.RunJob, "Pinned synthetic action", new(250, 0))
             { ExecutionAction = new(FlowExecutionSourceKind.Job, new("41", "Synthetic bound job", "revision-1", new string('a', 64)))
                { Target = new(Guid.NewGuid()), PinnedSnapshotReference = "protected-version:" + versionId.ToString("N"), Parameters = [] } },
             new(finishId, FlowNodeKind.Finish, "Finish", new(500, 0))],
            [new(triggerId, FlowNodeCatalogue.Event, nodeId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
             ..FlowNodeCatalogue.Find(FlowNodeKind.RunJob)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                .Select(port => new FlowEdgeDto(nodeId, port.Id, finishId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() })], new());
        Assert.True(FlowGraphV2Validator.ValidatePublished(graph).Valid);
        var graphJson = FlowGraphSerialization.Serialize(graph);
        var principal = new FlowExecutionAuthorityDto("synthetic-operator");
        db.Tenants.Add(new() { Id = 7, Name = "Synthetic tenant", CreatedAtUtc = active, UpdatedAtUtc = active });
        var definition = new FlowDefinitionRecord { Id = flowId, TenantId = 7, Name = "Synthetic continuation",
            Enabled = true, EnabledSinceUtc = active, Revision = 1, DraftJson = graphJson,
            CreatedAtUtc = active, UpdatedAtUtc = active };
        db.FlowDefinitions.Add(definition);
        db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 7, VersionNumber = 1,
            GraphJson = graphJson, ConfigurationHash = FlowGraphSerialization.Hash(graph),
            ExecutionAuthorityJson = JsonSerializer.Serialize(principal, Json), PublishedBy = principal.PrincipalId, PublishedAtUtc = active });
        db.FlowRuntimeIdentity.Add(new() { Id = 1, SourceInstanceId = sourceInstance });
        await db.SaveChangesAsync(ct);
        definition.PublishedVersionId = versionId; definition.PublishedVersionNumber = 1;
        await db.SaveChangesAsync(ct);
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-continuation", string.Empty, "missing-definition", null, 7, null), ct);
        await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", null, null, null, null), ct);
        var sourceRow = await db.OutboxMessages.AsNoTracking().SingleAsync(row => row.Type == FlowExecutionEventLimits.OutboxType, ct);
        var fact = JsonSerializer.Deserialize(sourceRow.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        var selection = ExecutionLifecycleSubscriptions.ReadDelivery((await db.OutboxMessages.AsNoTracking()
            .SingleAsync(row => row.Type == FlowExecutionSubscriptionLimits.OutboxType, ct)).PayloadJson)!;
        var aggregate = FlowExecutionEventMatching.Aggregate(versionId, triggerId, settings, [fact]);
        Assert.Equal(selection.AdmissionKey, aggregate.AdmissionKey);
        var input = new FlowEventEnvelope(7, fact.EventId, fact.OccurrenceId, versionId, fact.OccurredAtUtc, null, principal)
            { ExecutionEvent = fact, SourceAdmissionKey = aggregate.AdmissionKey, SourceDeliveryId = selection.DeliveryId, SourceEventIds = aggregate.ContributingEventIds };
        Assert.True(FlowContractValidation.ValidEnvelope(input));
        var runId = Guid.NewGuid();
        db.FlowRuns.Add(new() { Id = runId, TenantId = 7, FlowId = flowId, FlowVersionId = versionId,
            EventId = aggregate.AdmissionKey, OccurrenceId = input.OccurrenceId, EventJson = JsonSerializer.Serialize(input, Json),
            EventFingerprint = FlowContractValidation.Hash(JsonSerializer.SerializeToUtf8Bytes(input, Json)),
            Status = FlowRunStatus.WaitingForExecution, CreatedAtUtc = fact.ObservedAtUtc, Fence = 1 });
        await db.SaveChangesAsync(ct);
        var identity = new ExecutionAdmissionIdentity(FlowActionKeys.CreateExecution(7, sourceInstance,
            input.OccurrenceId, input.EventId, versionId, nodeId), 7, principal.PrincipalId, versionId, runId, nodeId, 1);
        return new(options, identity, input);
    }

    private static FlowExecutionAuthority Authority(OrchestratorDbContext db, Access access, Verifier verifier) =>
        new(db, verifier, access, new NoFirstDispatch(), TimeProvider.System,
            new FlowExecutionEventAdmission(verifier, access, TimeProvider.System));

    private sealed record Fixture(DbContextOptions<OrchestratorDbContext> Options,
        ExecutionAdmissionIdentity Identity, FlowEventEnvelope Input);
    private sealed class NoFirstDispatch : IFlowTransactionAdmission
    {
        public Task<FlowDispatchDecision> CanStartActionAsync(OrchestratorDbContext db, FlowRunLease lease, CancellationToken ct = default)
            => throw new InvalidOperationException("A native continuation must not manufacture or reuse a worker lease.");
    }
    private sealed class Verifier : IFlowExecutionAuthorityVerifier
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(int tenant, FlowExecutionAuthorityDto authority, CancellationToken ct = default) =>
            Task.FromResult(Allowed && tenant == 7 && authority == new FlowExecutionAuthorityDto("synthetic-operator"));
    }
    private sealed class Access : IEffectiveAccessService
    {
        public bool Allowed { get; set; } = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenant, CancellationToken ct = default) =>
            Task.FromResult(Allowed && tenant == 7 && permission == NetRatelPermissions.JobManagement &&
                principal.FindFirst("netratel_principal_id")?.Value == "synthetic-operator" && !principal.IsInRole("Operator"));
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenant, CancellationToken ct = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
