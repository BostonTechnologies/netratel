using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobAggregatePostgresTests
{
    [Fact]
    public async Task Keyed_HA_redelivery_keeps_one_worker_original_rerun_keeps_its_choice_and_schema_one_remains_frozen()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct, keyedFlow: true);
        await UseHaGroupAsync(h, 2, ct);
        await using var scope = h.Services.CreateAsyncScope(); var services = scope.ServiceProvider;
        var snapshot = await services.GetRequiredService<IJobExecutionSnapshotCapture>().CaptureAsync(41, 7, ct);
        var authority = services.GetRequiredService<IAkkaJobAuthorityService>();
        ExecutionAdmissionIdentity Identity() => new(Guid.NewGuid(), 7, "synthetic-manager", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var request = new KeyedJobAdmissionRequest(Identity(), snapshot, new("flow-action", null, null));
        var first = await authority.StartKeyedAsync(request, ct);
        (await authority.StartKeyedAsync(request, ct)).Id.Should().Be(first.Id);
        var actual = (await services.GetRequiredService<IJobRunService>().GetResolvedTargetPlanAsync(first.Id, ct))!;
        actual.Nodes.Should().ContainSingle();
        var next = await authority.StartKeyedAsync(request with { Identity = Identity() }, ct);
        (await h.NodesAsync(next.Id, ct)).Single().AgentId.Should().NotBe(actual.Nodes[0].AgentId);
        var rerun = await authority.StartKeyedAsync(request with { Identity = Identity(), TargetPlan = actual }, ct);
        (await h.NodesAsync(rerun.Id, ct)).Single().AgentId.Should().Be(actual.Nodes[0].AgentId);
        await h.FillAsync(first.Id, ct); await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(first.Id, ct);
        (await h.ReadAsync(first.Id, ct)).Status.Should().Be(JobRunState.Succeeded);
        var legacyPool = new JobResolvedTargetPlan(1, 7, 41, snapshot.ComputeDigest(), snapshot.Definition.Job.Target!,
            actual.GroupRevision, actual.Bindings, actual.HaGroupSelection!.Candidates, actual.ExecutionPolicy);
        var historical = await authority.StartKeyedAsync(request with { Identity = Identity(), TargetPlan = legacyPool }, ct);
        historical.TargetCount.Should().Be(2); historical.TargetPlanDigest.Should().Be(legacyPool.ComputeDigest());
        (await h.NodesAsync(historical.Id, ct)).Should().HaveCount(2);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    public async Task One_hundred_HA_admissions_balance_one_worker_per_run_across_independent_scopes(int workers)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 100, ct);
        var groupId = await UseHaGroupAsync(h, workers, ct);
        var preview = await h.PlanAsync(ct);
        preview.Version.Should().Be(2); preview.ExecutionTargetCount.Should().Be(1); preview.Nodes.Should().HaveCount(workers);
        var parents = new List<JobRunInfo>();
        // Independent request scopes contend on the actual PostgreSQL group lock.
        for (var batch = 0; batch < 10; batch++)
            parents.AddRange(await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => h.StartAsync(ct))));
        await using (var proof = h.Services.CreateAsyncScope())
        {
            var db = proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var nodes = await db.JobRunNodes.AsNoTracking().ToArrayAsync(ct);
            nodes.Should().HaveCount(100); nodes.GroupBy(node => node.ParentRunId).Should().OnlyContain(group => group.Count() == 1);
            nodes.GroupBy(node => node.AgentId).Select(group => group.Count()).Should().HaveCount(workers).And.OnlyContain(count => count == 100 / workers);
            (await db.ExecutionGroups.SingleAsync(row => row.Id == groupId, ct)).Revision.Should().Be(1);
            var actual = (await proof.ServiceProvider.GetRequiredService<IJobRunService>().GetResolvedTargetPlanAsync(parents[0].Id, ct))!;
            actual.MatchesAdmissionPlan(preview).Should().BeTrue(); actual.Nodes.Should().ContainSingle();
        }
        parents.Should().OnlyContain(parent => parent.TargetCount == 1 && parent.EffectiveConcurrency == 1);
        await h.FillAsync(parents[0].Id, ct);
        h.Gateway.Active.Should().ContainSingle();
        await h.SucceedNodeAsync(h.Gateway.Active.Keys.Single(), ct); await h.FillAsync(parents[0].Id, ct);
        (await h.ReadAsync(parents[0].Id, ct)).Status.Should().Be(JobRunState.Succeeded);
        h.Gateway.Dispatches.Select(item => item.Dispatch.Ordinal).Should().Equal(1, 2);
        h.Gateway.Dispatches.Select(item => item.Client.AgentId).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task HA_previews_do_not_rotate_and_failed_workers_are_skipped_without_replaying_admitted_work()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct);
        var groupId = await UseHaGroupAsync(h, 2, ct);
        var preview = await h.PlanAsync(ct); await h.PlanAsync(ct);
        await using (var proof = h.Services.CreateAsyncScope())
            (await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().ExecutionGroups.SingleAsync(ct)).LastHaWorkerAgentId.Should().BeNull();
        var first = await h.StartAsync(ct);
        (await h.NodesAsync(first.Id, ct)).Should().ContainSingle().Which.AgentId.Should().Be(h.AgentIds[0]);
        var owners = h.Services.GetRequiredService<IClientConnectionEpochStore>();
        await owners.RetireAsync(h.Gateway.Owners[new(7, h.AgentIds[0])], RetirementReason.ExplicitClose, null, ct);
        var survivor = await h.StartAsync(ct, new("synthetic-manager", null, null) { ExpectedTargetPlanDigest = preview.ComputeDigest() });
        (await h.NodesAsync(survivor.Id, ct)).Should().ContainSingle().Which.AgentId.Should().Be(h.AgentIds[1]);
        (await h.NodesAsync(first.Id, ct)).Should().ContainSingle().Which.AgentId.Should().Be(h.AgentIds[0]);
        await owners.RetireAsync(h.Gateway.Owners[new(7, h.AgentIds[1])], RetirementReason.ExplicitClose, null, ct);
        (await Assert.ThrowsAsync<JobTargetValidationException>(() => h.StartAsync(ct))).Code.Should().Be("ha_group_no_healthy_workers");
        await using var final = h.Services.CreateAsyncScope(); var db = final.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.JobRuns.CountAsync(ct)).Should().Be(2);
        var group = await db.ExecutionGroups.SingleAsync(row => row.Id == groupId, ct);
        group.LastHaWorkerAgentId.Should().Be(h.AgentIds[1]); group.Revision.Should().Be(1);
    }

    [Fact]
    public async Task Scaling_an_HA_pool_balances_new_work_and_schedule_redelivery_does_not_advance_the_cursor()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 2, ct);
        var groupId = await UseHaGroupAsync(h, 2, ct);
        await h.StartAsync(ct); await h.StartAsync(ct);
        await using (var edit = h.Services.CreateAsyncScope())
        {
            var db = edit.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var group = await db.ExecutionGroups.SingleAsync(ct); group.Revision++;
            db.ExecutionGroupMembers.AddRange(h.AgentIds.Skip(2).Take(2).Select(agent => new ExecutionGroupMemberRecord
                { GroupId = groupId, TenantId = 7, AgentId = agent }));
            var job = await db.Jobs.SingleAsync(ct);
            job.OptionsJson = JobScheduleAuthority.Capture("{\"schedule\":{\"enabled\":true,\"timeZoneId\":\"UTC\",\"timeOfDay\":\"08:00\",\"daysOfWeek\":[\"Monday\"]}}",
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "synthetic-manager")], "fixture")));
            await db.SaveChangesAsync(ct);
        }
        var fresh = new List<JobRunInfo>();
        for (var index = 0; index < 100; index++) fresh.Add(await h.StartAsync(ct));
        await using (var proof = h.Services.CreateAsyncScope())
        {
            var ids = fresh.Select(run => checked((long)run.Id)).ToArray();
            var nodes = await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().JobRunNodes.Where(node => ids.Contains(node.ParentRunId)).ToArrayAsync(ct);
            nodes.GroupBy(node => node.AgentId).Select(group => group.Count()).Should().Equal(25, 25, 25, 25);
        }
        var scheduled = await Task.WhenAll(h.ScheduleAsync("same-ha-occurrence", ct), h.ScheduleAsync("same-ha-occurrence", ct));
        scheduled[0].Id.Should().Be(scheduled[1].Id);
        var next = await h.StartAsync(ct);
        (await h.NodesAsync(scheduled[0].Id, ct)).Single().AgentId.Should().NotBe((await h.NodesAsync(next.Id, ct)).Single().AgentId);
    }

    [Fact]
    public async Task Invocation_subsets_run_without_mutating_saved_targets_and_reject_empty_duplicate_or_foreign_ids()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45)); var ct = deadline.Token;
        await using var h = await AggregateHarness.CreateAsync(await postgres.CreateDatabaseAsync(ct), 5, ct);
        foreach (var invalid in new IReadOnlyList<Guid>[] { [], [h.AgentIds[0], h.AgentIds[0]], [Guid.NewGuid()] })
            (await Assert.ThrowsAsync<JobTargetValidationException>(() => h.StartAsync(ct,
                new("synthetic-manager", null, null) { SelectedTargetAgentIds = invalid }))).Code.Should().Be("invocation_targets_invalid");
        var run = await h.StartAsync(ct, new("synthetic-manager", null, null) { SelectedTargetAgentIds = [h.AgentIds[2]] });
        run.TargetCount.Should().Be(1); (await h.NodesAsync(run.Id, ct)).Single().AgentId.Should().Be(h.AgentIds[2]);
        await using var proof = h.Services.CreateAsyncScope();
        (await proof.ServiceProvider.GetRequiredService<IJobDefinitionService>().GetAsync(41, ct))!.Target!.AgentIds.Should().HaveCount(5);
        (await proof.ServiceProvider.GetRequiredService<OrchestratorDbContext>().JobRuns.CountAsync(ct)).Should().Be(1);
    }

    private static async Task<Guid> UseHaGroupAsync(AggregateHarness h, int workers, CancellationToken ct)
    {
        await using var scope = h.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var id = Guid.NewGuid(); var now = h.Clock.GetUtcNow();
        db.ExecutionGroups.Add(new() { Id = id, TenantId = 7, Name = "Production workers", NormalizedName = "PRODUCTION WORKERS",
            CreatedAtUtc = now, UpdatedAtUtc = now, Members = h.AgentIds.Take(workers).Select(agent => new ExecutionGroupMemberRecord { TenantId = 7, AgentId = agent }).ToList() });
        (await db.Jobs.SingleAsync(ct)).TargetJson = JsonSerializer.Serialize(new JobTargetDefinition { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = id });
        await db.SaveChangesAsync(ct); return id;
    }

    // Only the Flow principal/lease boundary is synthetic. The keyed admission,
    // protected plan, PostgreSQL parent/queue and native continuation are real.
    private sealed class FixtureFlowAuthority(AggregateHarness h) : IKeyedExecutionAuthority
    {
        public Task RequireAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo definition, CancellationToken ct)
        {
            if (!h.Access.Allowed || identity.PrincipalId != "synthetic-manager" || identity.TenantId != 7 || definition.TenantId != 7)
                throw new ExecutionAdmissionDeniedException("Synthetic Flow authority withdrawn.");
            return Task.CompletedTask;
        }
        public Task RequireTargetsAllowedAsync(ExecutionAdmissionIdentity identity, JobDefinitionInfo definition, IReadOnlyList<Guid> agents, CancellationToken ct)
        {
            if (agents.Any(agent => !h.AgentIds.Contains(agent))) throw new ExecutionAdmissionDeniedException("Foreign Flow worker.");
            return RequireAllowedAsync(identity, definition, ct);
        }
        public Task RequireFirstDispatchAsync(ExecutionAdmissionIdentity identity, NetRatel.Application.Flows.FlowRunLease? lease, CancellationToken ct) => Task.CompletedTask;
        public Task RequireContinuationAsync(ExecutionAdmissionIdentity identity, CancellationToken ct) => Task.CompletedTask;
    }
}
