using System.Globalization;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Jobs;
using NetRatel.Shared.Contracts.Tasks;
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

    [Fact]
    public async Task A_failed_canonical_repair_is_suppressed_for_its_ancestor_flow_without_spending_reruns_or_other_subscriptions()
    {
        // Exercise the canonical intent and terminal producer at the ingress boundary; no transport executes.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var settings = new FlowExecutionTriggerSettingsDto([FlowExecutionSourceKind.Request, FlowExecutionSourceKind.Job],
            [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant);
        await using var h = await Harness.CreateAsync(postgres, settings, includeJobRepair: true);
        var original = await h.FailRequestAsync();
        var ingress = h.Provider.GetRequiredService<IFlowExecutionEventIngress>();
        var admitted = await ingress.EnqueueExecutionAsync(original.Admission, ct);
        Assert.Equal(FlowIngressDisposition.Enqueued, admitted.Disposition);
        var ancestor = (await h.Provider.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid(), ct))!;
        Assert.Equal(admitted.RunId, ancestor.RunId);
        var independentVersion = await h.AddSubscriptionAsync(settings);
        var allocations = 0;
        FlowExecutionEventDto repairFailure;
        FlowExecutionSubscriptionDeliveryDto[] deliveries;
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var keys = scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>();
            var identity = new ExecutionAdmissionIdentity(FlowActionKeys.CreateExecution(ancestor, h.RepairNodeId), 17,
                AccessPolicy.PrincipalId, ancestor.Version.Id, ancestor.RunId, h.RepairNodeId, 1)
            { Causation = new(original.Fact.Causation!.RootOccurrenceId, original.Fact.EventId, 1, 0) };
            var submission = new KeyedJobAdmissionRequest(identity, h.RepairSnapshot!, new("synthetic-flow-repair", "{}", null));
            var binding = await new ExecutionAdmissionStore(db, keys, TimeProvider.System).ReserveAsync(
                new(identity, ExecutionAdmissionKind.Job, JsonSerializer.Serialize(submission, Json)),
                () => { allocations++; return "9007199254740993"; }, ct);
            var runId = ulong.Parse(binding.ExecutionId, CultureInfo.InvariantCulture);
            var runs = new JobRunService(db, keys);
            runs.BindPinnedExecutionSnapshot(runId, h.RepairSnapshot!);
            var now = DateTimeOffset.UtcNow;
            await runs.UpsertRunAsync(new(runId, 41, 17, h.RepairAgentId.ToString("D"), "synthetic-flow-repair",
                JobRunState.Running, 1, now, now, null, null, "{}", null, h.RepairAgentId), ct);
            await runs.UpsertStepRunAsync(new(301, runId, 42, JobStepRunState.Failed, 1, null,
                "Synthetic permitted repair failed.", now, now), ct);
            await runs.UpsertRunAsync(new(runId, 41, 17, h.RepairAgentId.ToString("D"), "synthetic-flow-repair",
                JobRunState.Failed, 1, now, now, now, "Synthetic permitted repair failed.", "{}", null, h.RepairAgentId), ct);
            var details = (await runs.GetDetailsAsync(runId, ct))!;
            await using var ownerTransaction = await db.Database.BeginTransactionAsync(ct);
            var resultHash = JobRunProjectionHash.Compute(details);
            db.Set<JobRunControlRecord>().Add(new() { RunId = checked((long)runId),
                TerminalReadyAtUtc = now, TerminalResultHash = resultHash });
            await ExecutionLifecycleOutbox.RecordJobAsync(db, details, resultHash, now, ct, h.RepairSnapshot);
            await db.SaveChangesAsync(ct);
            await ownerTransaction.CommitAsync(ct);
            repairFailure = JsonSerializer.Deserialize((await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionEventLimits.OutboxType && row.EntityId == binding.ExecutionId, ct)).PayloadJson,
                FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal(FlowExecutionOutcome.Failed, repairFailure.Outcome);
            Assert.Equal(0, repairFailure.Causation!.RecoveryAttempt);
            Assert.Equal(ancestor.RunId, repairFailure.Causation.FlowRunId);
            Assert.Equal(h.RepairNodeId, repairFailure.Causation.NodeId);
            Assert.Equal(original.Fact.EventId, repairFailure.Causation.ParentEventId);
            deliveries = (await db.OutboxMessages.AsNoTracking().Where(row =>
                row.Type == FlowExecutionSubscriptionLimits.OutboxType).Select(row => row.PayloadJson).ToArrayAsync(ct))
                .Select(ParseDelivery).Where(delivery => delivery.SourceEventId == repairFailure.EventId).ToArray();
        }
        FlowExecutionEventAdmissionRequest Selected(Guid version)
        {
            var delivery = Assert.Single(deliveries, item => item.FlowVersionId == version);
            return new(17, version, repairFailure.EventId, [repairFailure.EventId], delivery.DeliveryId);
        }
        var suppressed = await ingress.EnqueueExecutionAsync(Selected(h.VersionId), ct);
        Assert.Equal(FlowIngressDisposition.Disabled, suppressed.Disposition);
        Assert.Equal("execution-self-caused-event-suppressed", suppressed.Code);
        var independent = await ingress.EnqueueExecutionAsync(Selected(independentVersion), ct);
        Assert.Equal(FlowIngressDisposition.Enqueued, independent.Disposition);
        Assert.NotEqual(suppressed.RunId, independent.RunId);
        var duplicate = await ingress.EnqueueExecutionAsync(Selected(h.VersionId), ct);
        Assert.Equal(FlowIngressDisposition.Duplicate, duplicate.Disposition);
        Assert.Equal(suppressed.RunId, duplicate.RunId);
        var next = (await h.Provider.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid(), ct))!;
        Assert.Equal(independent.RunId, next.RunId);
        Assert.True((await h.AdmitAsync(next)).Allowed);
        await using var verification = h.Provider.CreateAsyncScope();
        var verify = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(FlowRunStatus.Failed, (await verify.FlowRuns.SingleAsync(row => row.Id == suppressed.RunId, ct)).Status);
        Assert.Equal(1, allocations);
        var onlyAdmission = Assert.Single(await verify.Set<ExecutionAdmissionRecord>().AsNoTracking().ToArrayAsync(ct));
        Assert.Null(JsonSerializer.Deserialize<ExecutionAdmissionCausation>(onlyAdmission.CausationJson!)!.SourceExecutionId);
        Assert.Equal(1, await verify.JobRuns.CountAsync(ct));
        Assert.Equal(0, await verify.FlowActions.CountAsync(ct));
        Assert.Equal(2, await verify.OutboxProcessedEvents.CountAsync(row => row.EventId == repairFailure.EventId, ct));
    }


    [Fact]
    public async Task Large_script_request_aggregate_waits_for_parent_and_has_one_representative_for_first_or_last_claim()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var settings = new FlowExecutionTriggerSettingsDto(
            [FlowExecutionSourceKind.Script, FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Succeeded], FlowExecutionScope.AllInTenant);
        await using var h = await Harness.CreateAsync(postgres, settings);
        const long parentId = 9007199254740993;
        var nativeAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var target = Guid.NewGuid();
        FlowExecutionEventDto[] facts;
        FlowExecutionSubscriptionDeliveryDto[] deliveries;
        Guid triggerId;
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var version = await db.FlowVersions.AsNoTracking().SingleAsync(x => x.Id == h.VersionId, ct);
            triggerId = JsonSerializer.Deserialize<FlowGraphDto>(version.GraphJson, Json)!.Nodes
                .Single(node => node.Kind == FlowNodeKind.ExecutionEvent).Id;
            db.Agents.Add(new() { Id = target, TenantId = 17, Name = "Synthetic group target", CreatedAtUtc = nativeAt.AddMinutes(-1) });
            db.Jobs.Add(new() { Id = 41, TenantId = 17, AgentId = target, Name = "Synthetic correlated parent",
                CreatedAtUtc = nativeAt.AddMinutes(-1), UpdatedAtUtc = nativeAt });
            db.JobRuns.Add(new() { Id = parentId, JobId = 41, TenantId = 17, AgentId = target, StartedBy = "synthetic-source",
                Status = (int)JobRunState.Running, CreatedAtUtc = nativeAt.AddMinutes(-1), StartedAtUtc = nativeAt.AddMinutes(-1) });
            db.Set<JobRunControlRecord>().Add(new() { RunId = parentId });
            await db.SaveChangesAsync(ct);

            // Real activity transitions below create the facts/intents. Select adversarial source IDs:
            // the smallest event has a delivery ID after more than a full bound of other same-kind events.
            var candidates = Enumerable.Range(0, 256).Select(index =>
            {
                var row = new JobTaskActivityRecord { Id = index + 100, RequestId = $"synthetic/script/𝄞/{index:D3}",
                    JobRunId = parentId, TenantId = 17, AgentId = target, TaskType = TaskKinds.ExecLibraryScript,
                    ScriptName = "Synthetic submitted script", Status = "Completed", ExitCode = 0,
                    CreatedAtUtc = nativeAt.AddMinutes(-1), StartedAtUtc = nativeAt.AddMinutes(-1), CompletedAtUtc = nativeAt,
                    OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64))) };
                var fact = ExecutionLifecycleOutbox.ScriptEvent(row, "Pending", nativeAt)!;
                return (Row: row, Fact: fact, Delivery: ExecutionLifecycleSubscriptions.DeliveryId(h.VersionId, triggerId, fact.EventId));
            }).ToArray();
            var peers = FlowExecutionEventLimits.MaximumReferences + 2;
            var adversarial = candidates.OrderBy(item => item.Fact.EventId).Select(item => new
            {
                First = item,
                Others = candidates.Where(other => other.Fact.EventId.CompareTo(item.Fact.EventId) > 0 &&
                    other.Delivery.CompareTo(item.Delivery) < 0).Take(peers).ToArray()
            }).First(group => group.Others.Length == peers);
            var activities = adversarial.Others.Select(item => item.Row).Prepend(adversarial.First.Row).ToArray();
            foreach (var row in activities) { row.Status = "Pending"; row.ExitCode = null; row.CompletedAtUtc = null; }
            db.JobTaskActivities.AddRange(activities);
            await db.SaveChangesAsync(ct);
            foreach (var row in activities) { row.Status = "Completed"; row.ExitCode = 0; row.CompletedAtUtc = nativeAt; }
            await db.SaveChangesAsync(ct);
            facts = (await db.OutboxMessages.AsNoTracking().Where(x => x.Type == FlowExecutionEventLimits.OutboxType)
                .Select(x => x.PayloadJson).ToArrayAsync(ct))
                .Select(json => JsonSerializer.Deserialize(json, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!)
                .OrderBy(fact => fact.EventId).ToArray();
            Assert.Equal(peers + 1, facts.Length);
            Assert.Equal(adversarial.First.Fact.EventId, facts[0].EventId);
            foreach (var fact in facts)
            {
                Assert.Equal(FlowExecutionSourceKind.Script, fact.SourceKind);
                Assert.Equal(FlowExecutionOutcome.Succeeded, fact.Outcome);
                Assert.Equal("Completed", fact.RawStatus);
                Assert.Equal(parentId.ToString(CultureInfo.InvariantCulture), fact.ParentExecutionId);
                Assert.Contains(fact.ExecutionId, activities.Select(row => row.RequestId));
            }
            deliveries = (await db.OutboxMessages.AsNoTracking().Where(x => x.Type == FlowExecutionSubscriptionLimits.OutboxType)
                .Select(x => x.PayloadJson).ToArrayAsync(ct)).Select(ParseDelivery).ToArray();
        }
        FlowExecutionEventAdmissionRequest Claim(FlowExecutionEventDto fact)
        {
            var delivery = Assert.Single(deliveries, item => item.SourceEventId == fact.EventId);
            return new(17, h.VersionId, fact.EventId, [fact.EventId], delivery.DeliveryId);
        }
        var ingress = h.Provider.GetRequiredService<IFlowExecutionEventIngress>();
        var pending = await ingress.EnqueueExecutionAsync(Claim(facts[^1]), ct);
        Assert.Equal(FlowIngressDisposition.CapacityExceeded, pending.Disposition);
        Assert.Equal("execution-parent-result-pending", pending.Code);
        await using (var scope = h.Provider.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            Assert.Equal(0, await db.FlowRuns.CountAsync(ct));
            var parent = await db.JobRuns.SingleAsync(x => x.Id == parentId, ct);
            parent.Status = (int)JobRunState.Succeeded; parent.CompletedAtUtc = nativeAt;
            await db.SaveChangesAsync(ct);
            var details = (await new JobRunService(db, scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>())
                .GetDetailsAsync((ulong)parentId, ct))!;
            var control = await db.Set<JobRunControlRecord>().SingleAsync(x => x.RunId == parentId, ct);
            control.TerminalReadyAtUtc = DateTimeOffset.UtcNow;
            control.TerminalResultHash = JobRunProjectionHash.Compute(details);
            await ExecutionLifecycleOutbox.RecordJobAsync(db, details, control.TerminalResultHash, control.TerminalReadyAtUtc.Value, ct);
            await db.SaveChangesAsync(ct);
            var aggregates = new List<FlowExecutionEventAggregateDto>();
            foreach (var fact in new[] { facts[0], facts[^1] })
            {
                var delivery = Assert.Single(deliveries, item => item.SourceEventId == fact.EventId);
                var selected = await ExecutionLifecycleSubscriptionWorker.SelectContributorsAsync(db, delivery, DateTimeOffset.UtcNow, ct);
                Assert.True(selected.Truncated);
                var ids = selected.EventIds;
                Assert.Equal(FlowExecutionEventLimits.MaximumReferences, ids.Count);
                Assert.Contains(fact.EventId, ids);
                Assert.Contains(facts[0].EventId, ids);
                aggregates.Add(FlowExecutionEventMatching.Aggregate(h.VersionId, triggerId, settings,
                    ids.Select(id => facts.Single(item => item.EventId == id)).ToArray()));
            }
            Assert.Equal(aggregates[0].AdmissionKey, aggregates[1].AdmissionKey);
            Assert.Equal(facts[0].EventId, aggregates[0].Representative.EventId);
            Assert.Equal(aggregates[0].Representative.EventId, aggregates[1].Representative.EventId);
        }
        // The trusted ingress must gather the complete canonical group even when a caller supplies one ID.
        var accepted = await ingress.EnqueueExecutionAsync(Claim(facts[^1]), ct);
        Assert.Equal(FlowIngressDisposition.Enqueued, accepted.Disposition);
        string frozen;
        await using (var scope = h.Provider.CreateAsyncScope())
            frozen = (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().FlowRuns.AsNoTracking().SingleAsync(ct)).EventJson;
        using (var acceptedEnvelope = JsonDocument.Parse(frozen))
        {
            Assert.True(acceptedEnvelope.RootElement.GetProperty("sourceEventIdsTruncated").GetBoolean());
            Assert.Equal(FlowExecutionEventLimits.MaximumReferences, acceptedEnvelope.RootElement.GetProperty("sourceEventIds").GetArrayLength());
        }
        foreach (var fact in facts)
        {
            var duplicate = await ingress.EnqueueExecutionAsync(Claim(fact), ct);
            Assert.Equal(FlowIngressDisposition.Duplicate, duplicate.Disposition);
            Assert.Equal(accepted.RunId, duplicate.RunId);
        }
        var lease = (await h.Provider.GetRequiredService<IFlowExecutionStore>().ClaimAsync(Guid.NewGuid(), ct))!;
        Assert.Equal(facts[0].EventId, lease.Event.ExecutionEvent!.EventId);
        Assert.Equal(facts[0].ExecutionId, lease.Event.ExecutionEvent.ExecutionId);
        Assert.True((await h.AdmitAsync(lease)).Allowed);
        h.Grants.ScriptRead = false;
        var revoked = await h.AdmitAsync(lease);
        Assert.False(revoked.Allowed); Assert.Equal("execution-source-read-denied", revoked.Code);
        Assert.True(h.Grants.ScriptReadCalls > 0);
        Assert.Equal(AccessPolicy.PrincipalId, h.Grants.ScriptPrincipalObserved);
        Assert.Equal(0, h.Grants.MonitoringReadCalls);
        await using var final = h.Provider.CreateAsyncScope();
        var verify = final.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        Assert.Equal(frozen, (await verify.FlowRuns.AsNoTracking().SingleAsync(ct)).EventJson);
        var consumer = "flow-source:" + accepted.RunId!.Value.ToString("N", CultureInfo.InvariantCulture);
        Assert.Equal(facts.Length, await verify.OutboxProcessedEvents.CountAsync(x => x.ConsumerName == consumer, ct));
        Assert.Equal(0, await verify.FlowActions.CountAsync(ct));
    }

    private static FlowExecutionSubscriptionDeliveryDto ParseDelivery(string json) =>
        JsonSerializer.Deserialize(json, FlowExecutionSubscriptionJsonContext.Default.FlowExecutionSubscriptionDeliveryDto)!;
    private sealed record Selected(int RequestId, FlowExecutionEventDto Fact, string SourcePayload,
        FlowExecutionEventAdmissionRequest Admission);

    private sealed class AccessPolicy : IEffectiveAccessService, IFlowExecutionAuthorityVerifier
    {
        public const string PrincipalId = "synthetic-source-publisher";
        public bool NativeRead { get; set; } = true;
        public bool ScriptRead { get; set; } = true;
        public int ScriptReadCalls { get; private set; }
        public string? ScriptPrincipalObserved { get; private set; }
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
            if (permission == NetRatelPermissions.ScriptExecute) { ScriptReadCalls++; ScriptPrincipalObserved = configuredPrincipal; }
            return Task.FromResult(tenantId == 17 && configuredPrincipal == PrincipalId &&
                permission != NetRatelPermissions.MonitoringRead &&
                (permission != NetRatelPermissions.JobManagement || NativeRead) &&
                (permission != NetRatelPermissions.ScriptExecute || ScriptRead));
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
        public Guid VersionId { get; private set; }
        public Guid RepairAgentId { get; private set; }
        public Guid RepairNodeId { get; private set; }
        public PinnedJobExecutionSnapshot? RepairSnapshot { get; private set; }

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
        public static async Task<Harness> CreateAsync(PostgreSqlPersistenceFixture fixture, FlowExecutionTriggerSettingsDto? settings = null,
            bool includeJobRepair = false)
        {
            var harness = new Harness(await fixture.CreateDatabaseAsync());
            await using (var scope = harness.Provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
                await db.Database.MigrateAsync();
                db.Tenants.AddRange(new Tenant { Id = 17, Name = "Synthetic source tenant" },
                    new Tenant { Id = 18, Name = "Separate synthetic tenant" });
                if (includeJobRepair)
                {
                    harness.RepairAgentId = Guid.NewGuid();
                    db.Agents.Add(new() { Id = harness.RepairAgentId, TenantId = 17,
                        Name = "Synthetic configured repair target", CreatedAtUtc = DateTimeOffset.UtcNow });
                    db.Jobs.Add(new() { Id = 41, TenantId = 17, AgentId = harness.RepairAgentId,
                        Name = "Synthetic permitted repair", CreatedAtUtc = DateTimeOffset.UtcNow, UpdatedAtUtc = DateTimeOffset.UtcNow });
                    db.JobSteps.Add(new() { Id = 42, JobId = 41, Ordinal = 1, Type = 0, Runner = "bash", Command = "false", Enabled = true });
                }
                await db.SaveChangesAsync();
            }
            harness.VersionId = await harness.AddSubscriptionAsync(settings, includeJobRepair);
            return harness;
        }
        public async Task<Guid> AddSubscriptionAsync(FlowExecutionTriggerSettingsDto? settings = null, bool includeJobRepair = false)
        {
            await using var scope = Provider.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var versionId = Guid.NewGuid();
            var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Synthetic request failure", new(0, 0))
            { ExecutionTrigger = settings ?? new([FlowExecutionSourceKind.Request], [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant) };
            var finish = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Record original failure", new(360, 0));
            var graph = new FlowGraphDto(2, [trigger, finish],
                [new(trigger.Id, FlowNodeCatalogue.Event, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }], new());
            string? protectedPins = null;
            if (includeJobRepair)
            {
                var repair = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunJob, "Synthetic permitted repair", new(180, 0))
                { ExecutionAction = new(FlowExecutionSourceKind.Job, new("41")) { Target = new(RepairAgentId) } };
                RepairNodeId = repair.Id;
                graph = graph with { Nodes = [trigger, repair, finish], Edges =
                    [new(trigger.Id, FlowNodeCatalogue.Event, repair.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() },
                    .. FlowNodeCatalogue.Find(repair.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                        .Select(port => new FlowEdgeDto(repair.Id, port.Id, finish.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() })] };
                var scripts = new ScriptService(db);
                var snapshots = new PinnedJobExecutionSnapshotCapture(db, new JobDefinitionService(db), scripts);
                var pins = new FlowExecutionDefinitionPins(db, snapshots, scripts, Grants, Grants,
                    scope.ServiceProvider.GetRequiredService<IDataProtectionProvider>());
                var capture = await pins.CaptureAsync(17, new(AccessPolicy.PrincipalId), versionId, graph, default);
                graph = capture.Graph;
                protectedPins = capture.ProtectedPinsJson;
                RepairSnapshot = await snapshots.CaptureAsync(41, 17, default);
                Assert.Equal(RepairSnapshot.ComputeDigest(), graph.Nodes.Single(node => node.Id == RepairNodeId).ExecutionAction!.Definition.ContentHash);
            }
            var valid = FlowGraphV2Validator.ValidatePublished(graph);
            Assert.True(valid.Valid, string.Join(", ", valid.Issues.Select(issue => issue.Code)));
            var time = DateTimeOffset.UtcNow.AddMinutes(-5);
            var json = FlowGraphSerialization.Serialize(graph);
            var flow = new FlowDefinitionRecord { Id = Guid.NewGuid(), TenantId = 17, Name = "Synthetic source flow",
                Revision = 1, Enabled = true, EnabledSinceUtc = time, DraftJson = json, CreatedAtUtc = time, UpdatedAtUtc = time };
            var version = new FlowVersionRecord { Id = versionId, FlowId = flow.Id, TenantId = 17, VersionNumber = 1,
                GraphJson = json, ConfigurationHash = FlowGraphSerialization.Hash(graph),
                PublishedBy = AccessPolicy.PrincipalId, PublishedAtUtc = time,
                ExecutionAuthorityJson = JsonSerializer.Serialize(new FlowExecutionAuthorityDto(AccessPolicy.PrincipalId), Json),
                ProtectedExecutionPinsJson = protectedPins };
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
