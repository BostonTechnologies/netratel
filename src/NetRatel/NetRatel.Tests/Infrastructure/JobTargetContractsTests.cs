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
