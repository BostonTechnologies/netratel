using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Akka.Actor;
using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Akka.Jobs;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.API;

/// <summary>Real PostgreSQL claims and native actor/receipt boundaries; synthetic gateway, no deployed OS processes.</summary>
public sealed partial class JobAggregatePostgresTests(PostgreSqlPersistenceFixture postgres) : IClassFixture<PostgreSqlPersistenceFixture>
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(100)]
    public async Task Five_nodes_own_complete_ordered_workflows_and_refill_only_freed_slots(int threads)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), threads, ct);
        var parent = await h.StartAsync(ct);
        parent.AgentId.Should().BeNull(); parent.IsAggregate.Should().BeTrue(); parent.TargetCount.Should().Be(5);
        h.Gateway.Dispatches.Should().BeEmpty(); // Admission commits before the first coordinator turn.
        await h.FillAsync(parent.Id, ct);
        h.Gateway.Active.Count.Should().Be(Math.Min(threads, 5));
        while (h.Gateway.Active.Count > 0)
        {
            var selected = h.Gateway.Active.Keys.Min();
            var first = h.Gateway.Active[selected]; first.Ordinal.Should().Be(1);
            await h.ReceiptAsync(selected, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
            h.Gateway.Active[selected].Ordinal.Should().Be(2);
            var before = h.Gateway.Dispatches.Count;
            await h.FillAsync(parent.Id, ct);
            h.Gateway.Dispatches.Count.Should().Be(before); // A node's first step cannot free its slot.
            await h.ReceiptAsync(selected, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
            await h.FillAsync(parent.Id, ct);
        }
        var final = await h.ReadAsync(parent.Id, ct);
        final.Status.Should().Be(JobRunState.Succeeded); final.ExecutionPolicy!.ThreadCount.Should().Be(threads);
        final.EffectiveConcurrency.Should().Be(Math.Min(threads, 5)); final.NodeCounts!.Succeeded.Should().Be(5);
        h.Gateway.Peak.Should().Be(Math.Min(threads, 5));
        var dispatches = h.Gateway.Dispatches.ToArray();
        dispatches.Select(item => item.Client.AgentId).Distinct().Should().HaveCount(5);
        foreach (var node in dispatches.GroupBy(item => item.Dispatch.JobRunId))
            node.Select(item => item.Dispatch.Ordinal).Should().Equal(1, 2);
        if (threads == 1)
            dispatches.Chunk(2).All(pair => pair[0].Client == pair[1].Client).Should().BeTrue();
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Set<JobRunNodeRecord>().Where(node => node.ParentRunId == checked((long)parent.Id)).Select(node => node.NativeRunId).Distinct().CountAsync(ct)).Should().Be(5);
        (await scope.ServiceProvider.GetRequiredService<IJobRunService>().ListAsync(ct)).Should().ContainSingle(run => run.Id == parent.Id);
        var control = await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)parent.Id), ct);
        var details = (await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetDetailsAsync(parent.Id, ct))!;
        control.TerminalResultHash.Should().Be(JobRunProjectionHash.ComputeAggregate(details,
            await JobRunNodeProjection.ReadAsync(db, checked((long)parent.Id), ct)));
    }

    [Theory]
    [InlineData(JobNodeFailurePolicy.StopRemaining)]
    [InlineData(JobNodeFailurePolicy.ContinueRemaining)]
    public async Task Terminal_failure_serializes_with_claims_and_drains_both_existing_slots(JobNodeFailurePolicy policy)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct, policy);
        var parent = await h.StartAsync(ct); await h.FillAsync(parent.Id, ct);
        var initial = h.Gateway.Active.Keys.Order().ToArray(); initial.Should().HaveCount(2);
        await h.ReceiptAsync(initial[0], JobGatewayLifecycleStatus.Failed, "{\"error\":\"synthetic node failure\"}", ct);
        if (policy == JobNodeFailurePolicy.StopRemaining)
            (await h.NodesAsync(parent.Id, ct)).Count(node => node.State == JobRunNodeState.Skipped &&
                node.Reason == JobRunNodeReasons.StoppedAfterNodeFailure).Should().Be(3); // Same native terminal transaction, before a parent turn.
        await h.FillAsync(parent.Id, ct);
        (await h.ReadAsync(parent.Id, ct)).Status.Should().Be(JobRunState.Running);
        h.Gateway.Active.Should().ContainKey(initial[1]);
        if (policy == JobNodeFailurePolicy.StopRemaining)
        {
            h.Gateway.Dispatches.Should().HaveCount(2);
            var state = await h.NodesAsync(parent.Id, ct);
            state.Count(node => node.State == JobRunNodeState.Skipped && node.Reason == JobRunNodeReasons.StoppedAfterNodeFailure).Should().Be(3);
        }
        else h.Gateway.Active.Count.Should().Be(2); // The freed node slot refills while the second original workflow runs.
        while (h.Gateway.Active.Count > 0)
        {
            await h.SucceedNodeAsync(h.Gateway.Active.Keys.Min(), ct);
            await h.FillAsync(parent.Id, ct);
        }
        (await h.ReadAsync(parent.Id, ct)).Status.Should().Be(JobRunState.Failed);
        h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Count().Should().Be(policy == JobNodeFailurePolicy.StopRemaining ? 2 : 5);
    }

    [Fact]
    public async Task Unknown_execution_closes_continue_policy_and_cancel_or_action_deadline_waits_for_truthful_receipts()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct, JobNodeFailurePolicy.ContinueRemaining);
        await using (var source = h.Services.CreateAsyncScope())
        {
            var db = source.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.JobParameters.AddRange(new JobParameterDefinition { Id = 44, JobId = 41, Name = "phrase", Type = "secret" },
                new JobParameterDefinition { Id = 45, JobId = 41, Name = "exitPhrase", Type = "secret" });
            await db.SaveChangesAsync(ct);
        }
        var unknown = await h.StartAsync(ct, new("synthetic-manager", "{\"phrase\":\"outcomeUnknown\",\"exitPhrase\":\"exitCode\"}", null)); await h.FillAsync(unknown.Id, ct);
        var original = h.Gateway.Active.Keys.Order().ToArray();
        await h.ReceiptAsync(original[0], JobGatewayLifecycleStatus.Failed, "{\"outcomeUnknown\":true}", ct);
        (await h.ReadAsync(original[0], ct)).Status.Should().Be(JobRunState.Unknown); // A secret equal to the native flag key cannot erase its authority.
        (await h.NodesAsync(unknown.Id, ct)).Count(node => node.State == JobRunNodeState.Skipped).Should().Be(3);
        await h.FillAsync(unknown.Id, ct);
        (await h.NodesAsync(unknown.Id, ct)).Count(node => node.State == JobRunNodeState.Skipped &&
            node.Reason == JobRunNodeReasons.StoppedAfterUnknownExecution).Should().Be(3);
        await h.SucceedNodeAsync(original[1], ct); await h.FillAsync(unknown.Id, ct);
        (await h.ReadAsync(original[1], ct)).Status.Should().Be(JobRunState.Succeeded); // Redacting an exitCode key cannot change typed native success.
        (await h.ReadAsync(unknown.Id, ct)).Status.Should().Be(JobRunState.Unknown);
        h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Should().HaveCount(2);

        var cancelled = await h.StartAsync(ct); await h.FillAsync(cancelled.Id, ct);
        var active = h.Gateway.Active.Keys.ToArray();
        await h.CancelAsync(cancelled.Id, ct); await h.FillAsync(cancelled.Id, ct);
        h.Gateway.Cancellations.Should().Contain(active);
        (await h.ReadAsync(cancelled.Id, ct)).Status.Should().Be(JobRunState.Running);
        (await h.NodesAsync(cancelled.Id, ct)).Count(node => node.State == JobRunNodeState.Cancelled).Should().Be(3);
        foreach (var child in active) await h.ReceiptAsync(child, JobGatewayLifecycleStatus.Cancelled, "{}", ct);
        await h.FillAsync(cancelled.Id, ct);
        (await h.ReadAsync(cancelled.Id, ct)).Status.Should().Be(JobRunState.Cancelled);

        var timedOut = await h.StartAsync(ct); await h.FillAsync(timedOut.Id, ct);
        var timedChildren = h.Gateway.Active.Keys.ToArray();
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)timedOut.Id), ct))
                .ActionDeadlineAtUtc = h.Clock.GetUtcNow().AddSeconds(1);
            await db.SaveChangesAsync(ct);
        }
        h.Clock.UtcNow = h.Clock.GetUtcNow().AddSeconds(2);
        await h.FillAsync(timedOut.Id, ct);
        (await h.ReadAsync(timedOut.Id, ct)).Status.Should().Be(JobRunState.Running);
        (await h.NodesAsync(timedOut.Id, ct)).Count(node => node.State == JobRunNodeState.TimedOut).Should().Be(3);
        foreach (var child in timedChildren) await h.ReceiptAsync(child, JobGatewayLifecycleStatus.Cancelled, "{}", ct);
        await h.FillAsync(timedOut.Id, ct);
        (await h.ReadAsync(timedOut.Id, ct)).Status.Should().Be(JobRunState.TimedOut);

        // Every slot is already active and on its final step when the whole-action
        // deadline wins. Authentic successful native receipts retain their facts.
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Jobs.SingleAsync(ct)).TargetJson = JsonSerializer.Serialize(new JobTargetDefinition
                { Source = JobTargetSource.ExplicitNodes, AgentIds = h.AgentIds.Take(2).ToArray() });
            await db.SaveChangesAsync(ct);
        }
        var allActive = await h.StartAsync(ct); await h.FillAsync(allActive.Id, ct);
        var allActiveChildren = h.Gateway.Active.Keys.ToArray();
        foreach (var child in allActiveChildren) await h.ReceiptAsync(child, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Set<JobAggregateControlRecord>().SingleAsync(row => row.ParentRunId == checked((long)allActive.Id), ct))
                .ActionDeadlineAtUtc = h.Clock.GetUtcNow().AddSeconds(1);
            await db.SaveChangesAsync(ct);
        }
        h.Clock.UtcNow = h.Clock.GetUtcNow().AddSeconds(2);
        await h.FillAsync(allActive.Id, ct);
        foreach (var child in allActiveChildren)
        {
            await h.ReceiptAsync(child, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
            (await h.ReadAsync(child, ct)).Status.Should().Be(JobRunState.Succeeded);
        }
        await h.FillAsync(allActive.Id, ct);
        (await h.NodesAsync(allActive.Id, ct)).Should().OnlyContain(node => node.State == JobRunNodeState.Succeeded);
        (await h.ReadAsync(allActive.Id, ct)).Status.Should().Be(JobRunState.TimedOut);
    }

    [Fact]
    public async Task Legacy_singleton_node_context_is_protected_once_and_reserved_inputs_reject_before_acceptance()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 1, ct);
        var agent = h.AgentIds[0];
        const string display = "Synthetic O'Neil; $(printf ignored)";
        await using (var setup = h.Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var job = await db.Jobs.SingleAsync(ct); job.TargetJson = null; job.ExecutionPolicyJson = null; job.AgentId = agent;
            var target = await db.Agents.SingleAsync(row => row.Id == agent, ct);
            target.FriendlyName = display; target.Tags = ["FrozenTag"];
            (await db.JobSteps.SingleAsync(step => step.Ordinal == 1, ct)).Command = "printf '%s' @node.id@";
            (await db.JobSteps.SingleAsync(step => step.Ordinal == 2, ct)).Command = "printf '%s' @node.displayName@ @node.hostname@ @node.tags@";
            await db.SaveChangesAsync(ct);
        }
        await Assert.ThrowsAsync<ArgumentException>(() => h.StartAsync(ct,
            new("synthetic-manager", "{\"node.id\":\"forged\"}", null)));
        await using (var proof = h.Services.CreateAsyncScope())
            (await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().JobRuns.CountAsync(ct)).Should().Be(0);
        var run = await h.StartAsync(ct);
        run.IsAggregate.Should().BeFalse(); run.AgentId.Should().Be(agent);
        h.Gateway.Active[run.Id].PayloadJson.Should().Contain(agent.ToString("D"));
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var target = await db.Agents.SingleAsync(row => row.Id == agent, ct);
            target.FriendlyName = "Mutable replacement"; target.Tags = ["ReplacementTag"];
            await db.SaveChangesAsync(ct);
        }
        await h.ReceiptAsync(run.Id, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
        var second = JsonSerializer.Deserialize<NetRatel.Shared.Contracts.Tasks.ExecShellCommandPayload>(h.Gateway.Active[run.Id].PayloadJson!)!;
        second.Command.Should().Contain("Synthetic O'\"'\"'Neil; $(printf ignored)").And.Contain("FrozenTag").And.NotContain("Replacement");
        second.Command.Should().Contain(" '' "); // Unreported hostnames remain empty.
        await using (var reload = h.Services.CreateAsyncScope())
        {
            var context = await reload.ServiceProvider.GetRequiredService<IJobRunService>().GetNodeExecutionContextAsync(run.Id, ct);
            context!.AgentId.Should().Be(agent); context.DisplayName.Should().Be(display);
            context.Hostname.Should().BeEmpty(); context.Tags.Should().Equal("FrozenTag");
        }
        await h.SucceedNodeAsync(run.Id, ct);
        (await h.ReadAsync(run.Id, ct)).Status.Should().Be(JobRunState.Succeeded);
        h.Gateway.Dispatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task Concurrent_turns_and_recovery_keep_one_native_identity_and_never_replay_ambiguous_dispatch()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct, JobNodeFailurePolicy.ContinueRemaining);
        var parent = await h.StartAsync(ct);
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => h.PumpAsync(parent.Id, ct)));
        h.Gateway.Active.Count.Should().Be(2); h.Gateway.Peak.Should().Be(2);
        var ids = h.Gateway.Active.Keys.ToArray();
        await h.PumpAsync(parent.Id, ct); // A new scope/owner resumes the queue, not admitted native IO.
        h.Gateway.Dispatches.Should().HaveCount(2);
        h.Clock.UtcNow = h.Clock.GetUtcNow().AddSeconds(61);
        foreach (var child in ids) await h.RecoverNativeAsync(child, ct);
        await h.FillAsync(parent.Id, ct);
        (await h.ReadAsync(parent.Id, ct)).Status.Should().Be(JobRunState.Unknown);
        (await h.NodesAsync(parent.Id, ct)).Count(node => node.Reason == JobRunNodeReasons.StoppedAfterUnknownExecution).Should().Be(3);
        await h.FillAsync(parent.Id, ct);
        h.Gateway.Dispatches.Should().HaveCount(2);
    }

    [Fact]
    public async Task Crash_after_claim_commit_before_native_start_retains_the_identity_and_never_replays_that_claim()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 1, ct);
        var parent = await h.StartAsync(ct);
        h.ClaimCrash.Arm();
        await Assert.ThrowsAsync<IOException>(() => h.PumpAsync(parent.Id, ct));
        var claimed = (await h.NodesAsync(parent.Id, ct)).Single(node => node.State == JobRunNodeState.Claimed);
        claimed.NativeRunId.Should().NotBeNull(); h.Gateway.Dispatches.Should().BeEmpty();
        await h.PumpAsync(parent.Id, ct); // A new owner observes an already claimed native pending identity.
        h.Gateway.Dispatches.Should().BeEmpty();
        h.Clock.UtcNow = h.Clock.GetUtcNow().AddSeconds(61);
        await h.RecoverNativeAsync(checked((ulong)claimed.NativeRunId!.Value), ct); await h.FillAsync(parent.Id, ct);
        var recovered = (await h.NodesAsync(parent.Id, ct)).Single(node => node.AgentId == claimed.AgentId);
        recovered.NativeRunId.Should().Be(claimed.NativeRunId); recovered.State.Should().Be(JobRunNodeState.Failed);
        recovered.Reason.Should().Contain("not replayed");
        (await h.ReadAsync(parent.Id, ct)).Status.Should().Be(JobRunState.Failed);
        (await h.NodesAsync(parent.Id, ct)).Count(node => node.State == JobRunNodeState.Skipped).Should().Be(4);
        h.Gateway.Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task Equal_step_ordinals_on_two_nodes_read_isolated_forward_pages_and_keep_retained_native_history()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct);
        var parent = await h.StartAsync(ct); await h.FillAsync(parent.Id, ct);
        var active = h.Gateway.Active.OrderBy(item => item.Key).ToArray();
        var a = active[0].Value; var b = active[1].Value;
        a.Ordinal.Should().Be(1); b.Ordinal.Should().Be(1); a.JobStepRunId.Should().NotBe(b.JobStepRunId);
        await using var scope = h.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
        var first = await db.JobTaskActivities.SingleAsync(item => item.RequestId == a.RequestId, ct);
        var second = await db.JobTaskActivities.SingleAsync(item => item.RequestId == b.RequestId, ct);
        db.JobTaskLogs.AddRange(new JobTaskLogRecord { RequestId = a.RequestId, JobTaskActivityId = first.Id, TenantId = 7,
            ClientIdentity = "synthetic-a", Sequence = 7, Stream = "stdout", Message = "node-a-one", TimestampUtc = h.Clock.GetUtcNow() },
            new JobTaskLogRecord { RequestId = a.RequestId, JobTaskActivityId = first.Id, TenantId = 7,
                ClientIdentity = "synthetic-a", Sequence = 7, Stream = "stderr", Message = "node-a-two", TimestampUtc = h.Clock.GetUtcNow() });
        second.ResultJson = "{\"data\":\"synthetic-node-b-result-only\"}";
        await db.SaveChangesAsync(ct);
        var firstPage = await runs.GetForwardLogsPageAsync(a.RequestId, null, 1, 65536, ct);
        var nextPage = await runs.GetForwardLogsPageAsync(a.RequestId, firstPage.NextCursor, 1, 65536, ct);
        firstPage.Logs.Single().Message.Should().Be("node-a-one"); nextPage.Logs.Single().Message.Should().Be("node-a-two");
        var fallback = await runs.GetForwardLogsPageAsync(b.RequestId, null, 500, 65536, ct);
        fallback.Plain.Should().Contain("synthetic-node-b-result-only"); fallback.Logs.Should().BeEmpty();
        fallback.FullOutputUrl.Should().Contain(b.RequestId);
        var agentA = h.Gateway.Dispatches.Single(item => item.Dispatch.JobRunId == a.JobRunId).Client.AgentId;
        var agentB = h.Gateway.Dispatches.Single(item => item.Dispatch.JobRunId == b.JobRunId).Client.AgentId;
        (await NetRatel.API.Endpoints.JobRunEndpoints.FindNodeExecutionAsync(db, parent, agentA, ct)).Should().Be(a.JobRunId);
        var nodeB = await NetRatel.API.Endpoints.JobRunEndpoints.FindNodeExecutionAsync(db, parent, agentB, ct);
        nodeB.Should().Be(b.JobRunId);
        (await runs.GetDetailsAsync(nodeB!.Value, ct))!.Steps.Should().NotContain(step => step.Id == a.JobStepRunId);
        await Assert.ThrowsAsync<JobAggregateHistoryRetainedException>(() => runs.DeleteAsync(parent.Id, ct));
        await Assert.ThrowsAsync<JobAggregateHistoryRetainedException>(() => runs.DeleteAsync(a.JobRunId, ct));
        (await db.JobRuns.CountAsync(ct)).Should().Be(3);
    }

    [Fact]
    public async Task New_group_admission_sees_changes_existing_plan_and_review_digest_do_not_expand()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 1, ct);
        var group = Guid.NewGuid();
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.ExecutionGroups.Add(new() { Id = group, TenantId = 7, Name = "Five test systems", NormalizedName = "FIVE TEST SYSTEMS",
                Members = h.AgentIds.Take(4).Select(agent => new ExecutionGroupMemberRecord { GroupId = group, TenantId = 7, AgentId = agent }).ToList() });
            var job = await db.Jobs.SingleAsync(ct); job.AgentId = null;
            job.TargetJson = JsonSerializer.Serialize(new JobTargetDefinition { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = group });
            await db.SaveChangesAsync(ct);
        }
        var preview = await h.PlanAsync(ct); var original = await h.StartAsync(ct);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            db.ExecutionGroupMembers.Add(new() { GroupId = group, TenantId = 7, AgentId = h.AgentIds[4] });
            (await db.ExecutionGroups.SingleAsync(ct)).Revision++;
            await db.SaveChangesAsync(ct);
        }
        (await h.ReadAsync(original.Id, ct)).TargetCount.Should().Be(1);
        await using (var frozen = h.Services.CreateAsyncScope())
            (await frozen.ServiceProvider.GetRequiredService<IJobRunService>().GetResolvedTargetPlanAsync(original.Id, ct))!
                .HaGroupSelection!.Candidates.Should().HaveCount(4);
        var stale = await Assert.ThrowsAsync<JobTargetValidationException>(() => h.StartAsync(ct,
            new("synthetic-manager", null, null) { ExpectedTargetPlanDigest = preview.ComputeDigest() }));
        stale.Code.Should().Be("target_review_stale");
        var fresh = await h.StartAsync(ct); fresh.TargetCount.Should().Be(1);
        await using (var current = h.Services.CreateAsyncScope())
            (await current.ServiceProvider.GetRequiredService<IJobRunService>().GetResolvedTargetPlanAsync(fresh.Id, ct))!
                .HaGroupSelection!.Candidates.Should().HaveCount(5);
        await h.FillAsync(original.Id, ct);
        var before = h.Gateway.Dispatches.Count;
        h.Access.Allowed = false;
        // Current permission is checked again on the next ordered step, after accepted admission.
        await h.ReceiptAsync(h.Gateway.Active.Keys.Single(), JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct);
        await h.FillAsync(original.Id, ct);
        h.Gateway.Dispatches.Count.Should().Be(before);
        (await h.ReadAsync(original.Id, ct)).Status.Should().Be(JobRunState.Failed);
    }

    [Fact]
    public async Task Historical_fixed_schedule_retains_native_target_and_one_occurrence_is_idempotent()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 1, ct);
        await using (var scope = h.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); var job = await db.Jobs.SingleAsync(ct);
            job.TargetJson = null; job.AgentId = h.AgentIds[0]; job.ExecutionPolicyJson = null;
            job.OptionsJson = "{\"schedule\":{\"enabled\":true,\"timeZoneId\":\"UTC\",\"timeOfDay\":\"08:00\",\"daysOfWeek\":[\"Monday\"]}}";
            await db.SaveChangesAsync(ct);
        }
        var first = await h.ScheduleAsync("occurrence-a", ct);
        var duplicate = await h.ScheduleAsync("occurrence-a", ct); duplicate.Id.Should().Be(first.Id);
        var next = await h.ScheduleAsync("occurrence-b", ct); next.Id.Should().NotBe(first.Id);
        first.TargetCount.Should().Be(1); await h.FillAsync(first.Id, ct);
        h.Gateway.Dispatches.Should().ContainSingle(); h.Gateway.Dispatches.Single().Client.AgentId.Should().Be(h.AgentIds[0]);
        await using var proof = h.Services.CreateAsyncScope();
        var control = await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<JobAggregateControlRecord>()
            .SingleAsync(row => row.ParentRunId == checked((long)first.Id), ct);
        control.SourceKind.Should().Be("legacy-schedule"); control.PrincipalId.Should().BeNull();
        var runs = proof.ServiceProvider.GetRequiredService<IJobRunService>();
        var originalPin = await runs.GetPinnedExecutionSnapshotAsync(first.Id, ct);
        originalPin!.Definition.Job.Target.Should().BeNull(); originalPin.Definition.Job.AgentId.Should().Be(h.AgentIds[0]);
        originalPin.ComputeDigest().Should().Be(first.DefinitionDigest);
        var wrongTarget = JobTargetDefinition.FromLegacy(h.AgentIds[1]);
        var wrongPlan = new JobResolvedTargetPlan(1, 7, 41, originalPin.ComputeDigest(), wrongTarget, null, [],
            [new(h.AgentIds[1], "", "different native target", [])], new());
        runs.BindPinnedExecutionSnapshot(9001, originalPin);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runs.UpsertRunAsync(new(9001, 41, 7, "", "malformed-parent",
            JobRunState.Pending, 0, h.Clock.GetUtcNow(), null, null, null, null, null)
            { IsAggregate = true, TargetPlan = wrongPlan }, ct));
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Jobs.SingleAsync(ct)).ExecutionPolicyJson = JsonSerializer.Serialize(new JobExecutionPolicy(100));
            await db.SaveChangesAsync(ct);
        }
        var policyOnly = await h.StartAsync(ct);
        policyOnly.AgentId.Should().BeNull(); policyOnly.ExecutionPolicy!.ThreadCount.Should().Be(100);
        policyOnly.EffectiveConcurrency.Should().Be(1);
        await using var protectedProof = h.Services.CreateAsyncScope();
        (await protectedProof.ServiceProvider.GetRequiredService<IJobRunService>().GetPinnedExecutionSnapshotAsync(policyOnly.Id, ct))!
            .Definition.Job.AgentId.Should().Be(h.AgentIds[0]);
    }

    private sealed class AggregateHarness : IAsyncDisposable
    {
        public ServiceProvider Services { get; private set; } = null!;
        public Guid[] AgentIds { get; } = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        public TestClock Clock { get; } = new();
        public AggregateAccess Access { get; } = new();
        public SyntheticGateway Gateway { get; private set; } = null!;
        public ClaimCommitCrash ClaimCrash { get; } = new();
        private ServiceProvider? originalServices;
        public IJobRuntimeRouter NativeRouter => _native;
        public void UseServices(ServiceProvider services) { originalServices = Services; Services = services; }
        private ActorSystem _actors = null!;
        private NativeRouter _native = null!;
        private static readonly ClaimsPrincipal Principal = new(new ClaimsIdentity([new("netratel_principal_id", "synthetic-manager")], "fixture"));

        public static async Task<AggregateHarness> CreateAsync(string connection, int threads, CancellationToken ct,
            JobNodeFailurePolicy failurePolicy = JobNodeFailurePolicy.StopRemaining, bool reviewedMcp = false, bool keyedFlow = false)
        {
            var h = new AggregateHarness(); var services = new ServiceCollection();
            services.AddLogging(); services.AddDbContext<OrchestratorDbContext>(builder => builder.UseNpgsql(connection).AddInterceptors(h.ClaimCrash));
            services.AddSingleton<IDataProtectionProvider>(new EphemeralDataProtectionProvider());
            services.AddSingleton<TimeProvider>(h.Clock); services.AddSingleton(h.Access); services.AddSingleton<IEffectiveAccessService>(h.Access);
            services.AddSingleton(new OwnershipPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(10)));
            services.AddSingleton<IClientConnectionEpochStore, ClientConnectionEpochStore>();
            services.AddSingleton<IHttpContextAccessor>(new FixtureHttpContext { HttpContext = new DefaultHttpContext { User = Principal } });
            services.AddSingleton<IJobAggregateWakeup, NoWakeup>(); services.AddSingleton<JobAuthorityIdGenerator>();
            services.AddOptions<JobAggregateOptions>(); services.AddNetRatelJobObservationPersistence();
            services.AddScoped<IJobDefinitionService, JobDefinitionService>(); services.AddScoped<IJobRunService, JobRunService>();
            services.AddScoped<IScriptService, NoScripts>(); services.AddScoped<IJobExecutionSnapshotCapture, PinnedJobExecutionSnapshotCapture>();
            services.AddScoped<IJobNodeDirectory, JobNodeDirectory>(); services.AddScoped<IJobTargetResolver, JobTargetResolver>();
            services.AddScoped<JobAggregateOrchestrator>(); services.AddScoped<IJobAggregatePump>(provider => provider.GetRequiredService<JobAggregateOrchestrator>());
            services.AddScoped<HaGroupWorkerSelector>();
            services.AddScoped<IAkkaJobAuthorityService>(provider => new AkkaJobAuthorityService(
                provider.GetRequiredService<IJobDefinitionService>(), provider.GetRequiredService<IJobRunService>(),
                new JobTaskBridge(provider.GetRequiredService<IScriptService>(), NullLogger<JobTaskBridge>.Instance), h.Gateway,
                h._native, new RecordingRealtimeFanoutSink(), provider.GetRequiredService<JobAuthorityIdGenerator>(), new FixtureEnvironment(),
                db: provider.GetRequiredService<OrchestratorDbContext>(), clock: h.Clock,
                snapshotCapture: provider.GetRequiredService<IJobExecutionSnapshotCapture>(), aggregates: provider.GetRequiredService<JobAggregateOrchestrator>()));
            if (reviewedMcp) ConfigureReviewedMcp(services, connection);
            if (keyedFlow)
            {
                services.AddScoped<IExecutionAdmissionStore, ExecutionAdmissionStore>();
                services.AddSingleton<IKeyedExecutionAuthority>(new FixtureFlowAuthority(h));
            }
            h.Services = services.BuildServiceProvider(); h.Gateway = new(h);
            h._actors = ActorSystem.Create("aggregate-pg-" + Guid.NewGuid().ToString("N"));
            h._native = new(h._actors.ActorOf(JobCoordinatorActor.Props(h.Services.GetRequiredService<IJobObservationStore>())));
            await using (var scope = h.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>(); await db.Database.MigrateAsync(ct);
                var now = h.Clock.GetUtcNow(); db.Tenants.Add(new() { Id = 7, Name = "Synthetic five-node lab", CreatedAtUtc = now, UpdatedAtUtc = now });
                db.Agents.AddRange(h.AgentIds.Select((agent, index) => new Agent { Id = agent, TenantId = 7, Name = $"lab-node-{index + 1:00}",
                    Tags = ["TestSystem"], CreatedAtUtc = now }));
                db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Synthetic five-node workflow", CreatedAtUtc = now, UpdatedAtUtc = now,
                    TargetJson = JsonSerializer.Serialize(new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = h.AgentIds }),
                    ExecutionPolicyJson = JsonSerializer.Serialize(new JobExecutionPolicy(threads, failurePolicy)),
                    OptionsJson = "{\"executionPolicy\":{\"expectedRuntimeSeconds\":60,\"hardTimeoutSeconds\":60}}" });
                db.JobSteps.AddRange(new JobStepDefinition { Id = 42, JobId = 41, Ordinal = 1, Type = 0, Runner = "bash", Command = "printf '%s\\n' @node.hostname@", Enabled = true },
                    new JobStepDefinition { Id = 43, JobId = 41, Ordinal = 2, Type = 0, Runner = "bash", Command = "printf phase-two", Enabled = true });
                await db.SaveChangesAsync(ct);
            }
            var ownership = h.Services.GetRequiredService<IClientConnectionEpochStore>();
            foreach (var agent in h.AgentIds)
            {
                h.Clock.Reset(); var now = h.Clock.GetUtcNow(); var client = new ClientKey(7, agent);
                var reservation = await ownership.ReserveAsync(new(client, Guid.NewGuid(), Guid.NewGuid(), 0, now, now.AddSeconds(30), now.AddMinutes(10), new("synthetic-gateway", ["presence"], null)), ct);
                reservation.Disposition.Should().Be(OwnershipDisposition.Accepted);
                var committed = await ownership.CommitAsync(reservation.Reservation!, new(reservation.Reservation!.Owner, 1, now), ct);
                committed.Disposition.Should().Be(OwnershipDisposition.Accepted); h.Gateway.Owners[client] = committed.Current!.Owner;
            }
            return h;
        }
        public async Task<JobRunInfo> StartAsync(CancellationToken ct, RunJobRequest? request = null)
        { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>().StartAsync(41, request ?? new("synthetic-manager", null, null), ct); }
        public async Task<JobResolvedTargetPlan> PlanAsync(CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<JobAggregateOrchestrator>().PlanAsync(41, new("synthetic-manager", null, null), ct); }
        public async Task<JobRunInfo> ScheduleAsync(string occurrence, CancellationToken ct)
        {
            await using var scope = Services.CreateAsyncScope();
            using (JobScheduleOccurrenceContext.Enter(occurrence))
                await new HangfireJobDispatcher(scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>()).RunScheduledJobAsync(41, ct);
            var key = "schedule:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("41|" + occurrence)));
            var parent = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<JobAggregateControlRecord>()
                .Where(row => row.AdmissionKey == key).Select(row => row.ParentRunId).SingleAsync(ct);
            return await ReadAsync(checked((ulong)parent), ct);
        }
        public async Task<JobAggregatePumpResult> PumpAsync(ulong parent, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<JobAggregateOrchestrator>().PumpAsync(parent, ct); }
        public async Task FillAsync(ulong parent, CancellationToken ct)
        { for (var turn = 0; turn < 8; turn++) if (!(await PumpAsync(parent, ct)).Immediate) return; throw new InvalidOperationException("The bounded five-node turn sequence did not settle."); }
        public async Task<JobRunInfo> ReadAsync(ulong parent, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return (await scope.ServiceProvider.GetRequiredService<IJobRunService>().GetAsync(parent, ct))!; }
        public async Task<JobRunNodeRecord[]> NodesAsync(ulong parent, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); return await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<JobRunNodeRecord>().AsNoTracking().Where(node => node.ParentRunId == checked((long)parent)).OrderBy(node => node.Ordinal).ToArrayAsync(ct); }
        public async Task CancelAsync(ulong parent, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); (await scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>().CancelAsync(parent, "synthetic-cancel", ct)).Should().BeTrue(); }
        public async Task RecoverNativeAsync(ulong child, CancellationToken ct)
        { await using var scope = Services.CreateAsyncScope(); await scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>().RecoverAsync(child, ct); }
        public async Task SucceedNodeAsync(ulong child, CancellationToken ct)
        { while (Gateway.Active.ContainsKey(child)) await ReceiptAsync(child, JobGatewayLifecycleStatus.Completed, "{\"exitCode\":0}", ct); }
        public async Task ReceiptAsync(ulong child, JobGatewayLifecycleStatus status, string result, CancellationToken ct)
        {
            var dispatch = Gateway.Active[child]; var client = Gateway.Dispatches.Last(item => item.Dispatch.JobRunId == child).Client;
            await using var scope = Services.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<IAkkaJobAuthorityService>().RecordLifecycleAsync(client,
                new(child, dispatch.JobStepId, dispatch.JobStepRunId, dispatch.Ordinal, dispatch.RequestId, dispatch.CorrelationId,
                    status, dispatch.NextVersion, dispatch.NextSequence, dispatch.RequestedAtUtc, Clock.GetUtcNow(), 100, result,
                    status == JobGatewayLifecycleStatus.Failed ? 1 : 0, AuthenticatedOwner: Gateway.Owners[client]), ct);
            if (JobAggregateOrchestrator.IsTerminal((await ReadAsync(child, ct)).Status)) Gateway.Active.TryRemove(child, out _);
        }
        public async ValueTask DisposeAsync() { await _actors.Terminate(); await (originalServices ?? Services).DisposeAsync(); }
    }

    private sealed class SyntheticGateway(AggregateHarness harness) : IAgentJobGatewaySessionRegistry
    {
        public ConcurrentDictionary<ClientKey, OwnerKey> Owners { get; } = new();
        public ConcurrentDictionary<ulong, JobGatewayStepDispatch> Active { get; } = new();
        public ConcurrentQueue<(ClientKey Client, JobGatewayStepDispatch Dispatch)> Dispatches { get; } = new();
        public ConcurrentBag<ulong> Cancellations { get; } = [];
        public int Peak;
        public AgentJobGatewayRegistration Register(ClientKey client, Guid connectionId, ulong epoch, bool provisional = false) => throw new NotSupportedException();
        public bool IsAvailable(ClientKey client) => Owners.ContainsKey(client);
        public OwnerKey? GetRegisteredOwner(ClientKey client) => Owners.TryGetValue(client, out var owner) ? owner : null;
        public async Task DispatchAsync(ClientKey client, JobGatewayStepDispatch dispatch, CancellationToken ct)
        {
            dispatch.ExpectedOwner.Should().Be(Owners[client]);
            await using var scope = harness.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var child = await db.JobRuns.AsNoTracking().SingleAsync(run => run.Id == checked((long)dispatch.JobRunId), ct);
            var node = await db.Set<JobRunNodeRecord>().AsNoTracking().SingleOrDefaultAsync(node => node.NativeRunId == child.Id, ct);
            var control = await db.Set<JobRunControlRecord>().AsNoTracking().SingleAsync(row => row.RunId == child.Id, ct);
            child.ParentRunId.Should().Be(node?.ParentRunId); child.AgentId.Should().Be(client.AgentId);
            control.DispatchPreparedAtUtc.Should().NotBeNull(); control.NativeDeadlineUtc.Should().NotBeNull();
            Active[dispatch.JobRunId] = dispatch; Dispatches.Enqueue((client, dispatch));
            var count = Active.Count; int old;
            do { old = Peak; if (count <= old) break; } while (Interlocked.CompareExchange(ref Peak, count, old) != old);
        }
        public Task CancelAsync(ClientKey client, ulong run, string reason, CancellationToken ct) => CancelAsync(Owners[client], run, reason, ct);
        public async Task CancelAsync(OwnerKey owner, ulong run, string reason, CancellationToken ct)
        {
            await using var scope = harness.Services.CreateAsyncScope();
            (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Set<JobRunControlRecord>()
                .AsNoTracking().SingleAsync(row => row.RunId == checked((long)run), ct)).CancellationRequestedAtUtc.Should().NotBeNull();
            Cancellations.Add(run);
        }
    }
    private sealed class AggregateAccess : IEffectiveAccessService
    {
        public bool Allowed = true;
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenant, CancellationToken cancellationToken = default)
            => Task.FromResult(Allowed && tenant == 7 && principal.FindFirstValue("netratel_principal_id") == "synthetic-manager");
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenant, CancellationToken cancellationToken = default)
            => Task.FromResult(new EffectiveAccessSnapshot("synthetic-manager", false, false, Allowed ? NetRatelPermissions.All : new HashSet<string>()));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class NoWakeup : IJobAggregateWakeup { public Task WakeAsync(ulong id, CancellationToken ct) => Task.CompletedTask; }
    private sealed class ClaimCommitCrash : DbTransactionInterceptor
    {
        private int _armed;
        private int _confirmationArmed;
        private int _parentAdmissionArmed;
        public void Arm() => _armed = 1;
        public void ArmReviewedConfirmation() => _confirmationArmed = 1;
        public void ArmParentAdmission() => _parentAdmissionArmed = 1;
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default)
        {
            if (_parentAdmissionArmed == 1 && eventData.Context?.ChangeTracker.Entries<JobAggregateControlRecord>().Any() == true &&
                Interlocked.Exchange(ref _parentAdmissionArmed, 0) == 1)
                throw new IOException("Synthetic process crash after parent admission committed, before reviewed receipt completion.");
            if (_confirmationArmed == 1 && eventData.Context?.ChangeTracker.Entries<McpReviewedJobAdmissionRecord>().Any() == true &&
                Interlocked.Exchange(ref _confirmationArmed, 0) == 1)
                throw new IOException("Synthetic process crash after confirmation and protected original material committed, before parent creation.");
            if (_armed == 1 && eventData.Context?.ChangeTracker.Entries<JobRunNodeRecord>()
                .Any(entry => entry.Entity.State == JobRunNodeState.Claimed && entry.Entity.NativeRunId is not null) == true &&
                Interlocked.Exchange(ref _armed, 0) == 1)
                throw new IOException("Synthetic process crash after the durable claim committed and before native start.");
            return Task.CompletedTask;
        }
    }
    private sealed class FixtureHttpContext : IHttpContextAccessor { public HttpContext? HttpContext { get; set; } }
    private sealed class NativeRouter(IActorRef region) : IJobRuntimeRouter
    {
        public async Task<T> ExecuteOwnedAsync<T>(ulong id, Func<IJobRunOwner, CancellationToken, Task<T>> action, CancellationToken ct)
            => (T)await region.Ask<object>(new ExecuteOwnedJobRun(id, async (owner, token) => await action(owner, token), ct), TimeSpan.FromSeconds(30), ct);
        public Task<JobMessageResult> RecordAsync(RecordJobObservation message, CancellationToken ct) => region.Ask<JobMessageResult>(message, TimeSpan.FromSeconds(30), ct);
        public Task<JobRunView> GetStateAsync(ulong id, CancellationToken ct) => region.Ask<JobRunView>(new GetJobRunProjection(id), TimeSpan.FromSeconds(30), ct);
        public Task<JobRuntimeStatus> ProbeAsync(CancellationToken ct) => region.Ask<JobRuntimeStatus>(new ProbeJobRuntime(), TimeSpan.FromSeconds(30), ct);
    }
    private sealed class TestClock : TimeProvider
    {
        public DateTimeOffset UtcNow = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => UtcNow;
        public void Reset() { var now = DateTimeOffset.UtcNow; UtcNow = new(now.UtcTicks - now.UtcTicks % 10, TimeSpan.Zero); }
    }
    private sealed class NoScripts : IScriptService
    {
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptInfo>>([]);
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult<ScriptInfo?>(null);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptParamInfo>>([]);
        public Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? raw, CancellationToken ct = default) => throw new NotSupportedException();
    }
    private sealed class FixtureEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
