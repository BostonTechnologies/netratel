using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class McpOperatorJobStartEndpointTests
{
    [Fact]
    public async Task Equal_step_ordinals_keep_native_output_isolated_and_cross_node_step_ids_are_rejected()
    {
        await using var app = await BuildAsync("node_logs", persistedOutput: true);
        var secondAgent = Guid.NewGuid();
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Agents.Add(new() { Id = secondAgent, TenantId = 42, Name = "lab-node-02", IsEnabled = true, CreatedAtUtc = now });
            db.McpOperatorTargetProfiles.Add(new() { AgentId = secondAgent, TenantId = 42,
                Classification = NetRatel.Application.Operations.McpOperatorTargetClassification.ManagedStandard, TagsJson = "[]", Version = 1, UpdatedAtUtc = now });
            var policy = await db.McpOperatorPolicies.AsNoTracking().SingleAsync();
            policy.Id = Guid.NewGuid(); policy.AgentId = secondAgent;
            db.McpOperatorPolicies.Add(policy);
            var plan = new JobResolvedTargetPlan(1, 42, JobId, new string('A', 64),
                new() { Source = JobTargetSource.ExplicitNodes, AgentIds = [AgentId, secondAgent] }, null, [],
                [new(AgentId, "lab-node-01", null, []), new(secondAgent, "lab-node-02", null, [])], new(2));
            var definition = await db.Jobs.SingleAsync(); definition.AgentId = null; definition.TargetJson = JsonSerializer.Serialize(plan.Target);
            var parent = await db.JobRuns.SingleAsync(); parent.AgentId = null; parent.IsAggregate = true;
            parent.TargetPlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)); parent.TargetPlanDigest = plan.ComputeDigest(); parent.DefinitionDigest = plan.DefinitionDigest;
            await db.SaveChangesAsync();
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            foreach (var (agent, nativeId, stepId, ordinal, message) in new[]
            {
                (AgentId, 2001UL, 3001UL, 1, "OUTPUT_FOR_LAB_NODE_01"),
                (secondAgent, 2002UL, 3002UL, 2, "OUTPUT_FOR_LAB_NODE_02")
            })
            {
                await runs.UpsertRunAsync(new(nativeId, JobId, 42, "", "output-fixture", JobRunState.Succeeded, 1,
                    now, now, now, null, "{}", null, agent));
                (await db.JobRuns.SingleAsync(run => run.Id == (long)nativeId)).ParentRunId = RunId;
                db.JobRunNodes.Add(new() { ParentRunId = RunId, TenantId = 42, AgentId = agent, Ordinal = ordinal,
                    NativeRunId = (long)nativeId, State = JobRunNodeState.Succeeded,
                    TargetJson = JsonSerializer.Serialize(plan.Nodes.Single(node => node.AgentId == agent)), StartedAtUtc = now, CompletedAtUtc = now });
                var requestId = $"native-node-{nativeId}";
                await runs.UpsertStepRunAsync(new(stepId, nativeId, 701, JobStepRunState.Succeeded, 1, requestId, null, now, now));
                var outputPolicy = TaskOutputPolicy.Create([]);
                var activity = await runs.CreateTaskActivityAsync(new(requestId, nativeId, 701, "", 42,
                    TaskKinds.ExecLibraryScript, "Completed", null, now, now, agent, OutputPolicy: outputPolicy));
                var redactor = new TaskOutputRedactor(outputPolicy, new Dictionary<string, string>());
                await runs.AppendTaskLogAsync(new(requestId, activity.Id, "", 42, "stdout", redactor.Redact(message)!, 1, now, outputPolicy.Binding));
            }
            await db.SaveChangesAsync();
        }
        using var client = app.GetTestClient();
        var root = $"/api/v2/mcp/operator/agents/42/{AgentId:D}/jobs/runs/{RunId}/nodes";
        using var first = await client.GetAsync($"{root}/{AgentId:D}/steps/3001/logs");
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var firstText = await first.Content.ReadAsStringAsync();
        firstText.Should().Contain("OUTPUT_FOR_LAB_NODE_01").And.NotContain("OUTPUT_FOR_LAB_NODE_02");
        using var second = await client.GetAsync($"{root}/{secondAgent:D}/steps/3002/logs");
        second.StatusCode.Should().Be(HttpStatusCode.OK, await second.Content.ReadAsStringAsync());
        (await second.Content.ReadAsStringAsync()).Should().Contain("OUTPUT_FOR_LAB_NODE_02").And.NotContain("OUTPUT_FOR_LAB_NODE_01");
        using var substituted = await client.GetAsync($"{root}/{AgentId:D}/steps/3002/logs");
        substituted.StatusCode.Should().Be(HttpStatusCode.NotFound);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var selectedPolicy = await db.McpOperatorPolicies.SingleAsync(row => row.AgentId == secondAgent);
            selectedPolicy.ConstraintsJson = JsonSerializer.Serialize(new NetRatel.Application.Operations.McpOperatorConstraints(MaxOutputBytes: 8),
                NetRatel.Application.Operations.McpOperatorJsonContext.Default.McpOperatorConstraints);
            await db.SaveChangesAsync();
        }
        using var bounded = await client.GetAsync($"{root}/{secondAgent:D}/steps/3002/logs?maximumBytes=65536");
        bounded.StatusCode.Should().Be(HttpStatusCode.OK, await bounded.Content.ReadAsStringAsync());
        using var boundedJson = JsonDocument.Parse(await bounded.Content.ReadAsStringAsync());
        var page = boundedJson.RootElement.GetProperty("output");
        page.GetProperty("logs")[0].GetProperty("message").GetString().Should().Be("OUTPUT_F");
        page.GetProperty("truncatedLogIds").GetArrayLength().Should().Be(1);
        page.GetProperty("nextCursor").GetString().Should().Contain(":");
    }
}
