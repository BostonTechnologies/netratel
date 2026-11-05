using System.Text;
using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Collects bounded simulated data. It has no service, runtime or dispatch dependency.</summary>
internal sealed class FlowPreviewTrace(FlowGraphDto graph)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly List<FlowNodeTraceDto> _steps = [];
    private int _remainingBytes = FlowTraceLimits.MaximumSnapshotTotalBytes;
    private bool _truncated;

    public void Add(FlowNodeDto node, object? input, object? output, FlowTraceStepStatus status, string outcome,
        FlowEdgeDto? selectedEdge = null, bool outputUnavailable = false, string? errorCode = null)
    {
        if (_steps.Count >= FlowTraceLimits.MaximumSteps) { _truncated = true; return; }
        _steps.Add(new(_steps.Count, node.Id, node.Name, status, outcome, FlowTraceProvenance.Simulated,
            Capture(input), outputUnavailable ? new(FlowTraceDataState.Missing) : Capture(output))
        {
            SelectedPort = selectedEdge?.SourcePort,
            EdgeId = selectedEdge?.Id,
            NextNodeId = selectedEdge?.TargetNodeId,
            ErrorCode = errorCode
        });
    }

    private FlowTraceSnapshotDto Capture(object? value)
    {
        var element = JsonSerializer.SerializeToElement(value, Json);
        var text = element.GetRawText();
        var bytes = Encoding.UTF8.GetByteCount(text);
        var empty = element.ValueKind switch
        {
            JsonValueKind.String => element.GetString()?.Length == 0,
            JsonValueKind.Array => element.GetArrayLength() == 0,
            JsonValueKind.Object => !element.EnumerateObject().Any(),
            _ => false
        };
        FlowTraceSnapshotDto snapshot;
        if (bytes > FlowTraceLimits.MaximumSnapshotBytes || bytes + 128 > _remainingBytes)
        {
            _truncated = true;
            var length = Math.Min(text.Length, FlowTraceLimits.MaximumExcerptLength);
            if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
            snapshot = new(FlowTraceDataState.Truncated)
            {
                OriginalUtf8Bytes = bytes,
                Excerpt = _remainingBytes > 2048 ? text[..length] : null
            };
        }
        else snapshot = new(element.ValueKind == JsonValueKind.Null ? FlowTraceDataState.Null
            : empty ? FlowTraceDataState.Empty : FlowTraceDataState.Present) { Value = element, OriginalUtf8Bytes = bytes };
        _remainingBytes = Math.Max(0, _remainingBytes - JsonSerializer.SerializeToUtf8Bytes(snapshot, Json).Length);
        return snapshot;
    }

    public FlowTraceDto Build()
    {
        var hash = FlowGraphSerialization.Hash(graph);
        var trace = new FlowTraceDto(graph.SchemaVersion, hash, FlowTraceProvenance.Simulated, _steps.ToArray(), _truncated);
        while (JsonSerializer.SerializeToUtf8Bytes(trace, Json).Length > FlowTraceLimits.MaximumTraceBytes && _steps.Count > 0)
        {
            _steps.RemoveAt(_steps.Count - 1);
            trace = trace with { Steps = _steps.ToArray(), Truncated = true };
        }
        return trace;
    }
}
