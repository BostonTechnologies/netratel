using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowValueEvaluationTests
{
    [Fact]
    public void Event_values_keep_decimal_identifiers_as_text_and_expose_named_canonical_outcomes()
    {
        var input = Event();
        var id = Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.executionId"), input);
        id.Available.Should().BeTrue();
        id.Value!.Value.GetString().Should().Be("9007199254740993");
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.outcome"), input).Value!.Value.GetString().Should().Be("timed_out");
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.rawStatus"), input).Value!.Value.GetString().Should().Be("Timeout");
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.definition.name"), input).Value!.Value.GetString().Should().Be("Synthetic inventory");
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.sourceKind"), input).Value!.Value.GetString().Should().Be("request");
    }

    [Fact]
    public void Missing_outputs_null_observations_and_empty_strings_remain_distinct_and_no_alert_is_fabricated()
    {
        var input = Event() with { Reason = "" };
        var missing = Resolve(new(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", Guid.NewGuid()), input);
        missing.Available.Should().BeFalse();
        missing.Value.Should().BeNull();
        var capturedNull = Resolve(new(FlowInputSource.Event, FlowDataType.Number, "event.exitCode"), input);
        capturedNull.Available.Should().BeTrue();
        capturedNull.Value!.Value.ValueKind.Should().Be(JsonValueKind.Null);
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.reason"), input).Value!.Value.GetString().Should().BeEmpty();
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "alert.ruleName"), input).Code.Should().Be("event-unavailable");
        FlowValueEvaluation.Matches(new(new(FlowInputSource.Event, FlowDataType.Number, "event.exitCode"),
            FlowComparison.Equal, JsonSerializer.SerializeToElement(0)), input, null, new Dictionary<Guid, JsonElement>())
            .Should().Be(new FlowPredicateEvaluation(false, false, "predicate-null"));
    }

    [Fact]
    public void Typed_node_output_and_predicate_use_exact_numbers_and_ordinal_text()
    {
        var nodeId = Guid.NewGuid();
        var outputs = new Dictionary<Guid, JsonElement>
        {
            [nodeId] = JsonSerializer.SerializeToElement(new { summary = "Fixed", executionId = "9007199254740993", exitCode = 1 })
        };
        var predicate = new FlowPredicateDto(new(FlowInputSource.NodeOutput, FlowDataType.Text, "summary", nodeId),
            FlowComparison.Equal, JsonSerializer.SerializeToElement("fixed"));
        FlowValueEvaluation.Matches(predicate, Event(), null, outputs).Should().Be(new FlowPredicateEvaluation(true, false, "condition-false"));
        predicate = new(new(FlowInputSource.Literal, FlowDataType.Number, Literal: JsonSerializer.SerializeToElement(9007199254740993L)),
            FlowComparison.GreaterThan, JsonSerializer.SerializeToElement(9007199254740992L));
        FlowValueEvaluation.Matches(predicate, Event(), null, outputs).Result.Should().BeTrue();
        var id = FlowValueEvaluation.Resolve(new(FlowInputSource.NodeOutput, FlowDataType.Text, "executionId", nodeId), Event(), null, outputs);
        id.Value!.Value.GetString().Should().Be("9007199254740993");
    }

    [Fact]
    public void Arbitrary_paths_wrong_types_secret_references_and_oversized_values_are_unavailable()
    {
        var input = Event();
        Resolve(new(FlowInputSource.Event, FlowDataType.Text, "event.Replay.InputsReference"), input).Code.Should().Be("event-path");
        Resolve(new(FlowInputSource.Event, FlowDataType.Number, "event.executionId"), input).Code.Should().Be("binding-type");
        Resolve(new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "vault/synthetic"), input).Code.Should().Be("secret-reference-unavailable");
        Resolve(new(FlowInputSource.Literal, FlowDataType.Text,
            Literal: JsonSerializer.SerializeToElement(new string('a', FlowGraphV2Limits.MaximumBindingBytes))), input).Code.Should().Be("binding-value-limit");
        var nodeId = Guid.NewGuid();
        var outputs = new Dictionary<Guid, JsonElement>
        {
            [nodeId] = JsonSerializer.SerializeToElement(new { arbitrary = "hidden", data = new { password = "fixture" } })
        };
        FlowValueEvaluation.Resolve(new(FlowInputSource.NodeOutput, FlowDataType.Text, "arbitrary", nodeId), input, null, outputs)
            .Available.Should().BeFalse();
        FlowValueEvaluation.Resolve(new(FlowInputSource.NodeOutput, FlowDataType.Text, "data.password", nodeId), input, null, outputs)
            .Available.Should().BeFalse();
    }

    private static FlowValueResolution Resolve(FlowInputBindingDto binding, FlowExecutionEventDto input) =>
        FlowValueEvaluation.Resolve(binding, input, null, new Dictionary<Guid, JsonElement>());

    private static FlowExecutionEventDto Event()
    {
        var time = new DateTimeOffset(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        return new(Guid.Parse("0ea9fa44-afec-41fc-b679-3e3134f36dfa"), Guid.Parse("386c2db7-ab0a-4b3a-91bc-c21f11103567"),
            17, FlowExecutionSourceKind.Request, new("42", "Synthetic inventory", "7", new string('a', 64)),
            "9007199254740993", "attempt-2", new(Guid.Parse("02b5f119-6d05-497f-a5d2-e89e230dc0bc"), "Synthetic endpoint"),
            FlowExecutionOutcome.TimedOut, "Timeout", time, time);
    }
}
