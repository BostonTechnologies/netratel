using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.Netclaw;

public sealed partial class NetclawPersistencePostgresTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Saved_source_worker_checks_transactional_admission_then_completes_without_an_AI_send(bool admitted)
    {
        var options = await DatabaseAsync();
        await using var connection = new OrchestratorDbContext(options);
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseNpgsql(connection.Database.GetConnectionString()).Options;
        var daemon = new Daemon();
        var sourceAdmission = new SavedSourceAdmission(admitted);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton(options);
        services.AddScoped<OrchestratorDbContext>();
        services.AddSingleton(identityOptions);
        services.AddScoped<NetRatelIdentityDbContext>();
        services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
        services.AddSingleton<INetclawPairingService, Pairing>();
        services.AddSingleton<IAiAssistantChatClientFactory>(daemon);
        services.AddScoped<NetclawProfileService>();
        services.AddScoped<InvestigationStore>();
        services.AddScoped<NetclawAuthority>();
        services.AddScoped<IEffectiveAccessService, EffectiveAccessService>();
        services.AddScoped<IFlowInvestigationToolPolicy, NativeFlowInvestigationToolPolicy>();
        services.AddSingleton<IFlowExecutionEventAdmission>(sourceAdmission);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var at = DateTimeOffset.UtcNow;
        var agentId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 17, FlowExecutionSourceKind.Job,
            new("42", "Synthetic failed job", "7"), "9007199254740993", "9007199254740993", new(agentId),
            FlowExecutionOutcome.Failed, "Failed", at, at) { CompletedAtUtc = at, Provenance = "job-terminal-ready" };
        Guid investigationId;
        Guid messageId;
        string originalEventJson;
        await using (var scope = provider.CreateAsyncScope())
        {
            var identity = scope.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
            await identity.Database.MigrateAsync();
            identity.ApplicationPrincipals.Add(new() { Id = "source-publisher", ExternalIssuer = "https://issuer.example.test",
                ExternalSubject = "source-publisher" });
            var role = new AccessRole { Name = "Synthetic source investigator" };
            foreach (var permission in new[] { NetRatelPermissions.FlowExecute, NetRatelPermissions.ClientManagement,
                NetRatelPermissions.TelemetryRead, NetRatelPermissions.SecretUse, NetRatelPermissions.JobManagement })
                role.Permissions.Add(new() { Permission = permission });
            identity.AccessRoles.Add(role);
            identity.PrincipalRoleAssignments.Add(new() { PrincipalId = "source-publisher", RoleId = role.Id, TenantId = 17 });
            await identity.SaveChangesAsync();
            var authority = scope.ServiceProvider.GetRequiredService<NetclawAuthority>();
            Assert.True(await authority.CanInvestigateExecutionAsync(17, "source-publisher", null, [FlowExecutionSourceKind.Job], default));
            Assert.False(await authority.CanInvokeAsync(17, "source-publisher", null, true, default));
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.Agents.Add(new Agent { Id = agentId, TenantId = 17, Name = "synthetic-source-worker",
                CreatedAtUtc = at, LastSeenUtc = at });
            await db.SaveChangesAsync();
            var paired = await scope.ServiceProvider.GetRequiredService<NetclawProfileService>()
                .PairAsync(17, new("https://netclaw.example", "synthetic-code"), "connection-owner", default);
            Assert.True(paired.Success);
            var publishedAuthority = new FlowExecutionAuthorityDto("source-publisher");
            var envelope = JsonSerializer.Deserialize<FlowEventEnvelope>(JsonSerializer.Serialize(new
            {
                tenantId = 17, eventId = source.EventId, occurrenceId = source.OccurrenceId, flowVersionId = versionId,
                occurredAtUtc = source.OccurredAtUtc, authority = publishedAuthority, executionEvent = source
            }), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Missing source envelope.");
            Assert.Null(envelope.Data);
            var draft = new FlowIncidentActionDraft(17, runId, nodeId, paired.Connection.Id, paired.Connection.Revision,
                Guid.NewGuid(), "flow:synthetic:saved-source:1", envelope, new("Original failed job", "Original failure facts"))
                { Kind = FlowActionKind.NetclawInvestigation, AiSettings = new(), AiDeadlineAtUtc = at.AddMinutes(15) };
            messageId = FlowAiCorrelation.MessageId(draft);
            Assert.NotEqual(source.EventId, messageId);
            var flowId = Guid.NewGuid();
            var triggerId = Guid.NewGuid();
            var finishId = Guid.NewGuid();
            var graph = new FlowGraphDto(FlowGraphV2Limits.SchemaVersion,
            [
                new(triggerId, FlowNodeKind.ExecutionEvent, "Execution event", new(0, 0))
                    { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) },
                new(nodeId, FlowNodeKind.InvestigateWithAi, "Investigate original", new(200, 0), ConnectorId: draft.ConnectorId,
                    ConnectorRevision: draft.ConnectorRevision) { AiSettings = new() },
                new(finishId, FlowNodeKind.Finish, "Finish explicitly", new(400, 0))
            ],
            [
                new(triggerId, FlowNodeCatalogue.Event, nodeId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
                new(nodeId, FlowNodeCatalogue.Unsupported, finishId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
                new(nodeId, FlowNodeCatalogue.Failed, finishId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
                new(nodeId, FlowNodeCatalogue.Succeeded, finishId, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }
            ], new(0, 0, 1));
            var graphJson = JsonSerializer.Serialize(graph, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            db.FlowDefinitions.Add(new() { Id = flowId, TenantId = 17, Name = "Synthetic source recovery", Enabled = true,
                PublishedVersionId = versionId, DraftJson = graphJson, CreatedAtUtc = at, UpdatedAtUtc = at });
            db.FlowVersions.Add(new() { Id = versionId, FlowId = flowId, TenantId = 17, VersionNumber = 1,
                GraphJson = graphJson, PublishedBy = "source-publisher", PublishedAtUtc = at,
                ExecutionAuthorityJson = JsonSerializer.Serialize(publishedAuthority),
                ConfigurationHash = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(graphJson)) });
            originalEventJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            db.FlowRuns.Add(new() { Id = runId, FlowId = flowId, FlowVersionId = versionId, TenantId = 17,
                EventId = source.EventId, OccurrenceId = source.OccurrenceId, EventJson = originalEventJson,
                EventFingerprint = FlowContractValidation.Hash(Encoding.UTF8.GetBytes(originalEventJson)),
                Status = FlowRunStatus.WaitingForAi, Fence = 9, CreatedAtUtc = at });
            await db.SaveChangesAsync();
            var store = scope.ServiceProvider.GetRequiredService<InvestigationStore>();
            var investigation = await store.CreateAsync(17, new(Guid.NewGuid(), "Synthetic source investigation", [agentId]),
                "source-publisher", default, sourceActionKey: draft.IdempotencyKey, flowRunId: runId);
            investigationId = investigation.Id;
            await store.AcceptMessageAsync(17, investigationId, new(messageId,
                FlowExecutionEvidenceFormatter.InvestigationQuestion(source, 17)), "source-publisher", null, true, default);
            db.FlowActions.Add(new() { TenantId = 17, RunId = runId, NodeId = nodeId, IdempotencyKey = draft.IdempotencyKey,
                Status = FlowActionStatus.WaitingForAi, DraftJson = JsonSerializer.Serialize(draft),
                AiResultJson = JsonSerializer.Serialize(new FlowAiResultDto(17, runId, versionId, nodeId, investigationId,
                    messageId, FlowAiResultStatus.Queued, at)) });
            await db.SaveChangesAsync();
            Assert.Empty(await db.MonitoringOccurrences.AsNoTracking().ToListAsync());
            Assert.Empty(await db.MonitoringFlowOutbox.AsNoTracking().ToListAsync());
        }
        var lifecycle = new WorkerLifecycle();
        using var worker = new NetclawInvestigationWorker(provider.GetRequiredService<IServiceScopeFactory>(), daemon,
            TimeProvider.System, lifecycle);
        await worker.StartAsync(default);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try
        {
            await lifecycle.Ready.Task.WaitAsync(deadline.Token);
            await sourceAdmission.Called.Task.WaitAsync(deadline.Token);
            FlowAiResultDto result;
            while (true)
            {
                await using var db = new OrchestratorDbContext(options);
                var saved = await db.Set<AiInvestigationTurnResultRecord>().AsNoTracking()
                    .SingleOrDefaultAsync(row => row.TenantId == 17 && row.InvestigationId == investigationId &&
                        row.MessageId == messageId, deadline.Token);
                if (saved is not null)
                {
                    result = JsonSerializer.Deserialize<FlowAiResultDto>(saved.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                        ?? throw new InvalidOperationException("Missing terminal AI result.");
                    break;
                }
                await Task.Delay(TimeSpan.FromMilliseconds(25), deadline.Token);
            }
            Assert.Equal(admitted ? FlowAiResultStatus.Unsupported : FlowAiResultStatus.Failed, result.Status);
            Assert.Equal(investigationId, result.InvestigationId);
            Assert.Equal(messageId, result.MessageId);
            Assert.Equal(nodeId, result.ActionNodeId);
            Assert.NotNull(result.CompletedAtUtc);
            Assert.Null(result.Summary);
            Assert.Null(result.Evidence);
            Assert.Null(result.Recommendations);
            Assert.Equal(0, daemon.Sends);
            await using (var db = new OrchestratorDbContext(options))
            {
                var row = await db.AiInvestigations.AsNoTracking().SingleAsync(item => item.Id == investigationId);
                Assert.Equal(InvestigationState.Idle, row.State);
                Assert.Null(row.OccurrenceId);
                Assert.Equal("source-publisher", row.CreatedBy);
                Assert.Empty(await db.Set<AiInvestigationContextRecord>().AsNoTracking().ToListAsync());
                var stopped = await db.AiInvestigationEvents.AsNoTracking().SingleAsync(item =>
                    item.InvestigationId == investigationId && item.Type == "dispatch_not_sent");
                Assert.Contains(admitted ? NativeFlowInvestigationToolPolicy.UnavailableCode : "source admission", stopped.Text);
                var persistedEvent = await db.FlowRuns.AsNoTracking().Where(item => item.Id == runId).Select(item => item.EventJson).SingleAsync();
                Assert.True(JsonElement.DeepEquals(JsonSerializer.Deserialize<JsonElement>(originalEventJson),
                    JsonSerializer.Deserialize<JsonElement>(persistedEvent)));
                await InvestigationStore.StageTurnResultAsync(db, row, FlowAiResultStatus.Completed, DateTimeOffset.UtcNow,
                    default, summary: "Late synthetic text must not replace the committed unavailable result.");
                await db.SaveChangesAsync();
                var immutable = await db.Set<AiInvestigationTurnResultRecord>().AsNoTracking().SingleAsync();
                Assert.Equal(result.Status, JsonSerializer.Deserialize<FlowAiResultDto>(immutable.ResultJson,
                    new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Status);
            }
        }
        finally { await worker.StopAsync(default); }
        Assert.Equal(1, sourceAdmission.Calls);
        Assert.Equal(0, daemon.Sends);
    }

    private sealed class SavedSourceAdmission(bool allowed) : IFlowExecutionEventAdmission
    {
        public int Calls;
        public TaskCompletionSource Called { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<FlowDispatchDecision> LockAndAdmitInvestigationAsync(OrchestratorDbContext db,
            FlowEventEnvelope input, Guid runId, CancellationToken cancellationToken = default)
        {
            try
            {
                Assert.NotNull(db.Database.CurrentTransaction);
                var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.TenantId == input.TenantId &&
                    row.Id == runId, cancellationToken);
                Assert.Equal(FlowRunStatus.WaitingForAi, run.Status);
                Assert.Null(run.LeaseToken);
                Assert.Null(run.LeaseExpiresAtUtc);
                Assert.Equal(input.EventId, run.EventId);
                Assert.NotNull(input.ExecutionEvent);
                Assert.Null(input.Data);
                Interlocked.Increment(ref Calls);
                Called.TrySetResult();
                return new(allowed, allowed ? "synthetic-source-admitted" : "synthetic-publisher-revoked");
            }
            catch (Exception error) { Called.TrySetException(error); throw; }
        }
    }
}
