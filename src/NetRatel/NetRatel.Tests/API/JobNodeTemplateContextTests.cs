using System.Text.Json;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class JobNodeTemplateContextTests
{
    [Fact]
    public void Array_looking_parameter_strings_remain_one_literal_and_only_real_arrays_bind_multiple_operands()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new JobDefinitionDetails(new(41, "Literal filter", "/", null, 7, "", now, now, null),
            [new(1, 41, "targetTag", "string", true, null, null, null)], []);
        var stringInput = JsonSerializer.Serialize(new { targetTag = "[\"TagA\",\"TagB\"]" });
        var arrayInput = JsonSerializer.Serialize(new { targetTag = new[] { "TagA", "TagB" } });
        JobAggregateOrchestrator.TargetParameters(definition, stringInput)["targetTag"].Should().Equal("[\"TagA\",\"TagB\"]");
        JobAggregateOrchestrator.TargetParameters(definition, arrayInput)["targetTag"].Should().Equal("TagA", "TagB");
        var expression = JobNodeFilterParser.Parse("tags: @option.targetTag@");
        var bound = JobNodeFilterParser.Bind(expression, JobAggregateOrchestrator.TargetParameters(definition, stringInput));
        bound.Clauses[0].Alternatives.SelectMany(term => term.Operands).Should().ContainSingle().Which.Value.Should().Be("[\"TagA\",\"TagB\"]");
        Assert.Throws<JobTargetValidationException>(() => JobAggregateOrchestrator.TargetParameters(definition,
            "{\"targetTag\":\"A\",\"TARGETTAG\":\"B\"}")).Code.Should().Be("target_parameter_ambiguous");
        Assert.Throws<JobTargetValidationException>(() => JobAggregateOrchestrator.TargetParameters(definition,
            "{\"input\":{\"targetTag\":\"A\",\"targetTag\":\"B\"}}")).Code.Should().Be("target_parameter_ambiguous");
        JobAggregateOrchestrator.TargetParameters(definition, "{\"targetTag\":\"outer\",\"input\":{\"TARGETTAG\":\"nested\"}}")
            ["targetTag"].Should().Equal("nested");
    }
    [Fact]
    public async Task Punctuation_bearing_node_metadata_binds_as_a_library_parameter_value_without_changing_payload_structure()
    {
        var agent = Guid.NewGuid();
        const string display = "O'Neil \"north\" \\ lab; $(touch forbidden)";
        var node = new JobTargetNode(agent, "lab-node-01", display, ["QA+Ops", "quotes: \"x\""]);
        var inputs = JobNodeTemplateContext.Bind(new Dictionary<string, object?>(), node);
        var step = new JobStepInfo(42, 41, 1, JobStepKind.LibraryScript, "bash", null, 91,
            JsonSerializer.Serialize(new ExecLibraryScriptPayload { ScriptId = 91, Parameters = new() { ["Label"] = "@node.displayName@" } }), true);
        var now = DateTimeOffset.UtcNow;
        var run = new JobRunInfo(101, 41, 7, "", "aggregate-node:100", JobRunState.Running, 1, now, null, null, null, null, null, agent);
        var bridge = new JobTaskBridge(new SyntheticScript(), NullLogger<JobTaskBridge>.Instance);
        var result = await bridge.BuildGatewayInvocationAsync(run, step, inputs, JobExecutionRuntimePolicy.FromValues(60, 0, 60), CancellationToken.None);
        result.Success.Should().BeTrue();
        var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(result.PayloadJson!)!;
        payload.Parameters.Should().ContainSingle().Which.Should().Be(new KeyValuePair<string, string>("Label", display));
        payload.ScriptId.Should().Be(91); payload.ScriptContent.Should().Be("printf '%s' \"$1\"");
    }

    [Fact]
    public void Node_context_is_not_a_caller_parameter_and_commands_use_explicit_shell_literals()
    {
        var node = new JobTargetNode(Guid.NewGuid(), "lab-node-01", "O'Neil; $(echo marker)", ["QA"]);
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.Bind(new Dictionary<string, object?> { ["NODE.id"] = "forged" }, node));
        var bound = JobNodeTemplateContext.Bind(new Dictionary<string, object?>(), node);
        JobNodeTemplateContext.ResolveCommand("printf '%s' @node.displayName@", ShellExecutor.Bash, bound)
            .Should().Be("printf '%s' 'O'\"'\"'Neil; $(echo marker)'");
        JobNodeTemplateContext.ResolveCommand("Write-Output @node.displayName@", ShellExecutor.Pwsh, bound)
            .Should().Be("Write-Output 'O''Neil; $(echo marker)'");
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("echo \"@node.displayName@\"", ShellExecutor.Bash, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("echo @node.displayName@", ShellExecutor.Cmd, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("printf '%s' \\@node.displayName@", ShellExecutor.Bash, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("Write-Output `@node.displayName@", ShellExecutor.Pwsh, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("cat <<END\n@node.displayName@\nEND\n", ShellExecutor.Bash, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("# @node.displayName@\nprintf SAFE", ShellExecutor.Bash, bound));
        Assert.Throws<ArgumentException>(() => JobNodeTemplateContext.ResolveCommand("printf SAFE # @node.displayName@", ShellExecutor.Bash, bound));
        JobNodeTemplateContext.ResolveLiteral("@node.id@", new Dictionary<string, object?> { ["node.id"] = "forged" }).Should().Be("@node.id@");
    }

    private sealed class SyntheticScript : IScriptService
    {
        private readonly ScriptInfo _script = new(91, "Synthetic parameter echo", "/", null, "printf '%s' \"$1\"", null, "bash", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1);
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult<ScriptInfo?>(id == 91 ? _script : null);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptParamInfo>>([new(1, 91, "Label", "string", false, null, null, null)]);
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptInfo>>([_script]);
        public Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? raw, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
