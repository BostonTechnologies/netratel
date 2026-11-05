using System.Text.Json;
using AwesomeAssertions;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Application.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowValueWireTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Absent_and_captured_null_literals_remain_distinct_after_wire_round_trip()
    {
        var missing = new FlowInputBindingDto(FlowInputSource.Literal, FlowDataType.Json);
        var capturedNull = missing with { Literal = JsonSerializer.SerializeToElement<object?>(null) };
        var missingJson = JsonSerializer.Serialize(missing, Json);
        var nullJson = JsonSerializer.Serialize(capturedNull, Json);
        Assert.DoesNotContain("literal", missingJson);
        Assert.Contains("\"literal\":null", nullJson);
        Assert.Null(JsonSerializer.Deserialize<FlowInputBindingDto>(missingJson, Json)!.Literal);
        Assert.Equal(JsonValueKind.Null, JsonSerializer.Deserialize<FlowInputBindingDto>(nullJson, Json)!.Literal!.Value.ValueKind);

        var eventBinding = new FlowInputBindingDto(FlowInputSource.Event, FlowDataType.Text, "event.executionId");
        var restoredEvent = JsonSerializer.Deserialize<FlowInputBindingDto>(JsonSerializer.Serialize(eventBinding, Json), Json)!;
        Assert.Null(restoredEvent.Literal);
    }

    [Fact]
    public void Captured_null_snapshot_keeps_its_value_and_state_after_history_serialization()
    {
        var snapshot = new FlowTraceSnapshotDto(FlowTraceDataState.Null) { Value = JsonSerializer.SerializeToElement<object?>(null) };
        var serialized = JsonSerializer.Serialize(snapshot, Json);
        var restored = JsonSerializer.Deserialize<FlowTraceSnapshotDto>(serialized, Json)!;
        Assert.Equal(FlowTraceDataState.Null, restored.State);
        Assert.Equal(JsonValueKind.Null, restored.Value!.Value.ValueKind);
        Assert.Equal(serialized, JsonSerializer.Serialize(restored, Json));
    }

    [Fact]
    public void Source_reference_truncation_is_omitted_for_historical_alerts_and_cannot_masquerade_as_alert_data()
    {
        var original = FlowTestData.Lease("Synthetic legacy envelope", 90).Event;
        var serialized = JsonSerializer.Serialize(original, Json);
        serialized.Should().NotContain("sourceEventIdsTruncated");
        JsonSerializer.Serialize(original with { SourceEventIdsTruncated = false }, Json).Should().Be(serialized);
        FlowContractValidation.ValidEnvelope(original).Should().BeTrue();
        FlowContractValidation.ValidEnvelope(original with { SourceEventIdsTruncated = true }).Should().BeFalse();
    }

    [Fact]
    public void Canonical_JSON_retains_decimal_precision_and_bounds_exponent_expansion_and_duplicate_keys()
    {
        using var left = JsonDocument.Parse("""{"longer":9007199254740993.00,"a":{"longer":1e2,"b":0.100}}""");
        using var right = JsonDocument.Parse("""{"a":{"b":0.1,"longer":100},"longer":9007199254740993}""");
        FlowJsonCanonical.SerializeValue(left.RootElement).Should().Be(FlowJsonCanonical.SerializeValue(right.RootElement));
        FlowJsonCanonical.SerializeValue(left.RootElement).Should().Contain("9007199254740993");
        using var strings = JsonDocument.Parse("""{"id":"9007199254740993","parameter":"1e3"}""");
        FlowJsonCanonical.SerializeValue(strings.RootElement).Should().Be("""{"id":"9007199254740993","parameter":"1e3"}""");
        using var huge = JsonDocument.Parse("1e9999");
        FlowJsonValueBounds.Valid(huge.RootElement, 4096).Should().BeFalse();
        using var duplicate = JsonDocument.Parse("""{"value":1,"value":2}""");
        FlowJsonValueBounds.Valid(duplicate.RootElement, 4096).Should().BeFalse();
    }
}
