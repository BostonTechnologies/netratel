using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Mcp.Core;
using NetRatel.Mcp.Tools;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.Mcp;

public sealed partial class ManagedOperationsMcpTests
{
    [Fact]
    public void Job_target_and_group_contracts_have_identical_complete_HTTP_and_stdio_schemas()
    {
        var http = NetRatelMcpToolDefinitions.CreateForHttp("prod").ToDictionary(tool => tool.ProtocolTool.Name);
        var stdio = NetRatelMcpToolDefinitions.CreateForStdio(new Dictionary<string, Type>
        { ["netratel_config"] = typeof(ReadOnlyTools), ["netratel_remote_support_v2"] = typeof(MutationTools) }, "prod")
            .ToDictionary(tool => tool.ProtocolTool.Name);
        foreach (var name in new[] { "netratel_execution_groups", "netratel_job_targets", "netratel_jobs", "netratel_job_runs" })
        {
            var schema = http[name].ProtocolTool.InputSchema.GetRawText();
            schema.Should().Be(stdio[name].ProtocolTool.InputSchema.GetRawText());
            schema.Should().NotContain("\"$ref\"");
        }
        http["netratel_jobs"].ProtocolTool.InputSchema.GetRawText().Should().Contain("threadCount").And.Contain("nodeFailurePolicy").And.Contain("RuntimeInput");
        http["netratel_job_runs"].ProtocolTool.InputSchema.GetRawText().Should().Contain("runtimeTarget").And.Contain("nodeAgentId").And.Contain("stepRunId");
        ManagedOperationCatalog.Find("netratel_execution_groups", "list")!.Permission.Should().Be("tenant.admin");
        ManagedOperationCatalog.Find("netratel_job_targets", "groups")!.Permission.Should().Be("job.manage");
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.Stdio)]
    [InlineData(NetRatelMcpTransport.StreamableHttp)]
    public async Task Node_output_forwards_exact_ids_and_composite_cursor_with_bounded_policy_budget(NetRatelMcpTransport transport)
    {
        var client = new RecordingClient { AllowDirectReads = true }; var tools = Tools(client, transport);
        var anchor = Guid.NewGuid(); var node = Guid.NewGuid();
        var request = JsonSerializer.SerializeToElement(new { tenantId = 7, agentId = anchor,
            jobRunId = "9007199254740993", nodeAgentId = node, stepRunId = "9007199254740995",
            cursor = "9007199254740997:9007199254740999", limit = 700, maximumBytes = 32000 });
        (await tools.netratel_job_runs("node_logs", request)).Success.Should().BeTrue();
        var call = client.Calls.Single();
        call.Path.Should().Contain($"/runs/9007199254740993/nodes/{node:D}/steps/9007199254740995/logs");
        Uri.UnescapeDataString(call.Path).Should().Contain("cursor=9007199254740997:9007199254740999")
            .And.Contain("limit=700").And.Contain("maximumBytes=32000");
    }

    [Theory]
    [InlineData(NetRatelMcpTransport.Stdio)]
    [InlineData(NetRatelMcpTransport.StreamableHttp)]
    public async Task Target_preview_preserves_literal_operands_and_group_management_requires_separate_confirmation(NetRatelMcpTransport transport)
    {
        var client = new RecordingClient(); var tools = Tools(client, transport);
        const string literal = "TestSystem,!tags:Protected";
        var preview = JsonSerializer.SerializeToElement(new { tenantId = 7, data = new JobTargetPreviewRequest(
            new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.Tags, "Tags") },
            RuntimeTarget: new(JobRuntimeTargetKind.Tags, [literal]), PageSize: 2) }, ManagedOperationCatalog.JsonOptions);
        (await tools.netratel_job_targets("resolve_preview", preview)).Success.Should().BeTrue();
        var sent = client.Calls.Single();
        sent.Path.Should().EndWith("/netratel_job_targets/resolve_preview");
        sent.Body!["request"]!["data"]!["runtimeTarget"]!["values"]![0]!.GetValue<string>().Should().Be(literal);
        var group = JsonSerializer.SerializeToElement(new { tenantId = 7,
            data = new ExecutionGroupWriteDto("Test nodes", null, true, false, [Guid.NewGuid()]) }, ManagedOperationCatalog.JsonOptions);
        (await tools.netratel_execution_groups("create", group)).RequiresConfirmation.Should().BeTrue();
        (await tools.netratel_execution_groups("create", group, true)).Success.Should().BeTrue();
        client.Calls.Last().Body!["confirm"]!.GetValue<bool>().Should().BeTrue();
        client.Calls.Should().NotContain(call => call.Path.Contains("/runs/", StringComparison.Ordinal));
    }
}
