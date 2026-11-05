using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionSecretReferenceTests
{
    [Fact]
    public void A_native_reference_cannot_be_replaced_by_checkpoint_input_or_case_aliases()
    {
        var (node, pin) = Definition();
        Assert.Throws<ArgumentException>(() => FlowExecutionSecretReferences.MergeInputs(node, pin, """{"VAULT":"inline-value"}"""));
        var alias = node with { Inputs = new Dictionary<string, FlowInputBindingDto>(node.Inputs!)
            { ["VAULT"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "OTHER_REFERENCE") } };
        var error = Assert.Throws<FlowExecutionPinException>(() => FlowExecutionSecretReferences.Validate(alias, pin));
        error.Code.Should().Be("secret-reference-parameter-conflict");
    }

    [Fact]
    public void Pure_evaluation_does_not_resolve_native_reference_values()
    {
        var (node, pin) = Definition();
        var binding = node.Inputs!["vault"];
        var resolved = FlowValueEvaluation.Resolve(binding, null, null, new Dictionary<Guid, JsonElement>());
        resolved.Available.Should().BeFalse();
        resolved.Code.Should().Be("secret-reference-unavailable");
        var merged = JsonDocument.Parse(FlowExecutionSecretReferences.MergeInputs(node, pin, """{"count":0,"enabled":false,"text":""}"""));
        using (merged)
        {
            merged.RootElement.GetProperty("vault").GetString().Should().Be("SYNTHETIC_FLOW_TOKEN");
            merged.RootElement.GetProperty("count").GetInt32().Should().Be(0);
            merged.RootElement.GetProperty("enabled").GetBoolean().Should().BeFalse();
            merged.RootElement.GetProperty("text").GetString().Should().Be("");
        }
    }

    private static (FlowNodeDto Node, FlowPinnedExecutionDefinition Pin) Definition()
    {
        var now = DateTimeOffset.UtcNow; var nodeId = Guid.NewGuid();
        var script = new ScriptInfo(1, "synthetic-reference", "/", null, "printf reference-only", null, "bash", now, now, 1);
        var scriptPin = new PinnedJobScriptSnapshot(script,
            [new ScriptParamInfo(1, 1, "vault", "secret_reference", true, null, null, null)],
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content))));
        var definition = new FlowExecutionDefinitionDto("1", script.Name, "1", scriptPin.ContentHash);
        var node = new FlowNodeDto(nodeId, FlowNodeKind.RunLibraryScript, "Synthetic reference", new(0, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Script, definition),
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["vault"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "SYNTHETIC_FLOW_TOKEN") }
        };
        return (node, new(nodeId, node.Kind, definition, null, Script: scriptPin));
    }
}
