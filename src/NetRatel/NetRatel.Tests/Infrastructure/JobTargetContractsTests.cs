using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class JobTargetContractsTests
{
    [Fact]
    public void Schema_one_keeps_its_published_canonical_bytes_and_wire_shape()
    {
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var plan = new JobResolvedTargetPlan(1, 7, 41, new('a', 64), JobTargetDefinition.FromLegacy(id), null, [],
            [new(id, "worker-a", null, [])], new());
        plan.ComputeDigest().Should().Be("32D594CF507581BAEC77377951D3DF9DDF0479F066418EDC71682B7F3EE10E79");
        var json = JsonSerializer.Serialize(plan, JobTargetJsonContext.Default.JobResolvedTargetPlan);
        json.Should().NotContain("InvocationAgentIds").And.NotContain("HaGroupSelection").And.NotContain("GroupName");
        JsonSerializer.Deserialize(json, JobTargetJsonContext.Default.JobResolvedTargetPlan)!.ComputeDigest().Should().Be(plan.ComputeDigest());
    }

    [Fact]
    public void HA_selection_binds_one_worker_to_an_immutable_approved_pool_and_ignores_only_availability_hints()
    {
        var first = new JobTargetNode(Guid.NewGuid(), "worker-a", null, []) { IsOnline = true };
        var second = new JobTargetNode(Guid.NewGuid(), "worker-b", null, []);
        var candidates = new List<JobTargetNode> { first, second };
        var target = new JobTargetDefinition { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = Guid.NewGuid() };
        var pool = new JobResolvedTargetPlan(2, 7, 41, new('a', 64), target, 1, [], candidates, new(100), null, null, "Production workers");
        var actual = pool.SelectHaWorker(second.AgentId);
        pool.ExecutionTargetCount.Should().Be(1); actual.Nodes.Should().ContainSingle().Which.AgentId.Should().Be(second.AgentId);
        actual.NeedsHaWorkerSelection.Should().BeFalse(); actual.MatchesAdmissionPlan(pool).Should().BeTrue();
        actual.ComputeDigest().Should().NotBe(pool.ComputeDigest()); actual.AdmissionPlanDigest.Should().Be(pool.ComputeDigest());
        candidates.Clear(); actual.HaGroupSelection!.Candidates.Should().HaveCount(2);
        var changedHints = new JobResolvedTargetPlan(2, 7, 41, new('a', 64), target, 1, [],
            [first with { IsOnline = false }, second with { IsOnline = true }], new(100), null, null, "Production workers");
        changedHints.ComputeDigest().Should().Be(pool.ComputeDigest());
        var json = JsonSerializer.Serialize(actual, JobTargetJsonContext.Default.JobResolvedTargetPlan);
        var roundTrip = JsonSerializer.Deserialize(json, JobTargetJsonContext.Default.JobResolvedTargetPlan)!;
        roundTrip.ComputeDigest().Should().Be(actual.ComputeDigest()); roundTrip.MatchesAdmissionPlan(pool).Should().BeTrue();
        var tamper = () => new JobResolvedTargetPlan(2, 7, 41, new('a', 64), target, 1, [], [first], new(100), null,
            new(pool.ComputeDigest(), [second]), "Production workers");
        tamper.Should().Throw<ArgumentException>();
        var outside = () => pool.SelectHaWorker(Guid.NewGuid()); outside.Should().Throw<ArgumentException>();
        var historical = new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, 1, [], pool.Nodes, new(100));
        historical.ExecutionTargetCount.Should().Be(2); historical.NeedsHaWorkerSelection.Should().BeFalse();
    }

    [Fact]
    public void Invocation_subset_is_versioned_and_cannot_widen_an_explicit_definition()
    {
        var first = new JobTargetNode(Guid.NewGuid(), "worker-a", null, []);
        var second = Guid.NewGuid();
        var target = new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = [first.AgentId, second] };
        var subset = new JobResolvedTargetPlan(2, 7, 41, new('a', 64), target, null, [], [first], new(), [first.AgentId], null, null);
        subset.Nodes.Should().ContainSingle(); subset.Target.AgentIds.Should().HaveCount(2);
        var legacy = () => new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [], [first], new());
        legacy.Should().Throw<ArgumentException>();
        var foreign = new JobTargetNode(Guid.NewGuid(), "unapproved", null, []);
        var widened = () => new JobResolvedTargetPlan(2, 7, 41, new('a', 64), target, null, [], [foreign], new(), [foreign.AgentId], null, null);
        widened.Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Target_replacement_clears_legacy_authority_while_omission_preserves_it()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var jobs = new JobDefinitionService(db);
        var agentId = Guid.NewGuid();
        var legacy = await jobs.CreateAsync(new("legacy", "/", null, 7, "historical", AgentId: agentId));
        var renamed = await jobs.UpdateAsync(new(legacy.Id, "renamed", null, null, 7, null));
        renamed!.AgentId.Should().Be(agentId);
        renamed.Target.Should().BeNull();

        var runtime = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput,
            RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") };
        var replaced = await jobs.UpdateAsync(new(legacy.Id, null, null, null, 7, null)
            { Target = runtime, ExecutionPolicy = new(100, JobNodeFailurePolicy.ContinueRemaining) });
        replaced!.AgentId.Should().BeNull();
        replaced.ClientIdentity.Should().BeEmpty();
        (await jobs.GetAsync(legacy.Id))!.Target.Should().Be(runtime);
        (await jobs.GetAsync(legacy.Id))!.ExecutionPolicy.Should().Be(new JobExecutionPolicy(100, JobNodeFailurePolicy.ContinueRemaining));
        var oldJson = JsonSerializer.Serialize(legacy);
        oldJson.Should().NotContain("Target").And.NotContain("ExecutionPolicy");
        var conflicting = () => jobs.UpdateAsync(new(legacy.Id, null, null, null, 7, null, AgentId: agentId) { Target = runtime });
        await conflicting.Should().ThrowAsync<ArgumentException>();
        (await jobs.GetAsync(legacy.Id))!.AgentId.Should().BeNull();
    }

    [Fact]
    public void Canonical_plan_copies_deduplicates_orders_and_binds_policy_and_identity()
    {
        var first = Guid.NewGuid(); var second = Guid.NewGuid();
        var tags = new List<string> { "TestSystem", "Linux" };
        var nodes = new List<JobTargetNode> { new(second, "lab-node-02", null, tags), new(first, "lab-node-01", null, ["Linux"]), new(first, "lab-node-01", null, ["Linux"]) };
        var target = new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = [second, first, first] };
        var plan = new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [], nodes, new(2));
        var digest = plan.ComputeDigest();
        nodes.Clear(); tags.Add("after-admission");
        plan.Nodes.Select(node => node.AgentId).Should().Equal(first, second);
        plan.Nodes[1].Tags.Should().NotContain("after-admission");
        plan.ComputeDigest().Should().Be(digest);
        var json = JsonSerializer.Serialize(plan, JobTargetJsonContext.Default.JobResolvedTargetPlan);
        JsonSerializer.Deserialize(json, JobTargetJsonContext.Default.JobResolvedTargetPlan)!.ComputeDigest().Should().Be(digest);
        new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [], plan.Nodes.Reverse().ToArray(), new(2)).ComputeDigest().Should().Be(digest);
        new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [], plan.Nodes, new(1)).ComputeDigest().Should().NotBe(digest);
        var conflicting = () => new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [],
            [plan.Nodes[0], plan.Nodes[0] with { Hostname = "substituted" }], new(2));
        conflicting.Should().Throw<ArgumentException>();
        var widened = () => new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [],
            [.. plan.Nodes, new(Guid.NewGuid(), "unapproved-node", null, [])], new(2));
        widened.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Unreported_hostname_remains_unknown_and_orders_by_separate_directory_labels()
    {
        var unreported = new JobTargetNode(Guid.NewGuid(), string.Empty, "Friendly label", []) { Name = "lab-node-00" };
        var reported = new JobTargetNode(Guid.NewGuid(), "lab-node-01", null, []);
        var target = new JobTargetDefinition { Source = JobTargetSource.ExplicitNodes, AgentIds = [reported.AgentId, unreported.AgentId] };
        var plan = new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [], [reported, unreported], new());
        plan.Nodes[0].AgentId.Should().Be(unreported.AgentId);
        plan.Nodes[0].Hostname.Should().BeEmpty();
        plan.Nodes[0].Name.Should().Be("lab-node-00");
        plan.Nodes[0].DisplayName.Should().Be("Friendly label");
        var roundTrip = JsonSerializer.Deserialize(JsonSerializer.Serialize(plan, JobTargetJsonContext.Default.JobResolvedTargetPlan),
            JobTargetJsonContext.Default.JobResolvedTargetPlan)!;
        roundTrip.ComputeDigest().Should().Be(plan.ComputeDigest());
        roundTrip.Nodes[0].Hostname.Should().BeEmpty();
        var nullHostname = () => new JobResolvedTargetPlan(1, 7, 41, new('a', 64), target, null, [],
            [reported, unreported with { Hostname = null! }], new());
        nullHostname.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Exclusive_sources_and_incomplete_protected_material_fail_without_fallback()
    {
        var conflict = new JobTargetDefinition { Source = JobTargetSource.NodeFilter, Filter = "tags: TestSystem", AgentIds = [Guid.NewGuid()] };
        conflict.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
        var reserved = new JobTargetDefinition { Source = JobTargetSource.RuntimeInput,
            RuntimeInput = new(JobRuntimeTargetKind.ClientId, "node.id") };
        reserved.Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
        var empty = () => new JobResolvedTargetPlan(1, 7, 41, new('a', 64), null!, null, [], [], new());
        empty.Should().Throw<ArgumentException>();
        new JobExecutionPolicy(100).Invoking(value => value.Validate()).Should().NotThrow();
        new JobExecutionPolicy(0).Invoking(value => value.Validate()).Should().Throw<ArgumentException>();
        ((int)JobRunState.Unknown).Should().Be(6);
        ((int)JobStepRunState.Unknown).Should().Be(7);
    }
}
