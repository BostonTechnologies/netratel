using System.Text.Json;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

/// <summary>Deterministic schema-two evaluation over supplied DTOs only. It has no store, connector, AI or execution-service seam.</summary>
public static class FlowExecutionPreview
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static FlowDryRunResultDto DryRun(FlowExecutionDryRunRequest request, int tenantId)
    {
        var validation = FlowGraphV2Validator.ValidateComplete(request.Graph);
        if (!validation.Valid) return new(false, false, "invalid-draft", null, [], validation.Issues);
        if (tenantId <= 0) return new(false, false, "preview-tenant-invalid", null, [], [new("preview-tenant-invalid")]);
        var trigger = request.Graph.Nodes.Single(node => FlowNodeCatalogue.IsTrigger(node.Kind));
        var executionTrigger = trigger.Kind == FlowNodeKind.ExecutionEvent;
        if (executionTrigger
            ? request.Input is null || request.Alert is not null || request.Input.TenantId != tenantId ||
                !FlowExecutionEventValidator.Validate(request.Input).Valid
            : request.Alert is null || request.Input is not null || !FlowPureEvaluation.ValidEventData(request.Alert))
            return new(false, false, "preview-source-invalid", null, [], [new("preview-source-invalid", trigger.Id)]);
        var fixtureIssue = ValidateFixtures(request);
        if (fixtureIssue is not null) return new(false, false, fixtureIssue.Code, null, [], [fixtureIssue]);

        var trace = new FlowPreviewTrace(request.Graph);
        var visited = new List<Guid>();
        var outputs = new Dictionary<Guid, JsonElement>();
        var fixtures = (request.Fixtures ?? []).ToDictionary(fixture => fixture.NodeId);
        var nodes = request.Graph.Nodes.ToDictionary(node => node.Id);
        var edges = request.Graph.Edges.ToDictionary(edge => (edge.SourceNodeId, edge.SourcePort));
        FlowIncidentFieldsDto? fields = null;
        IReadOnlyDictionary<string, string>? simulatedAi = null;
        var current = trigger;
        FlowDryRunResultDto Result(string code, bool skipped = false, bool valid = true) =>
            new(valid, skipped, code, fields, visited.ToArray(), []) { Trace = trace.Build() };

        if (executionTrigger && !FlowExecutionEventMatching.Matches(tenantId, trigger.ExecutionTrigger!, request.Input!, DateTimeOffset.MinValue))
        {
            visited.Add(trigger.Id);
            trace.Add(trigger, request.Input, null, FlowTraceStepStatus.Skipped, "event-not-matched", outputUnavailable: true);
            return Result("event-not-matched", skipped: true);
        }

        while (visited.Count < FlowGraphV2Limits.MaximumNodes)
        {
            visited.Add(current.Id);
            string outcome;
            object? output;
            var inputValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var redactedInputs = 0;
            object SnapshotInput()
            {
                if (redactedInputs == 0) return new { request.Input, request.Alert, Inputs = inputValues, Incident = fields };
                return new
                {
                    request.Input, request.Alert, Inputs = inputValues, Incident = fields,
                    UnavailableInputs = new { State = "unavailable", Code = "secret-reference-unavailable", Redacted = true, Count = redactedInputs }
                };
            }
            foreach (var binding in FlowJobTargetBindings.Mappings(current))
            {
                var resolved = FlowValueEvaluation.Resolve(binding.Value, request.Input, request.Alert, outputs);
                if (!resolved.Available || resolved.Value is not { } value)
                {
                    if (resolved.Code == "secret-reference-unavailable" &&
                        (current.Kind is FlowNodeKind.RunLibraryScript or FlowNodeKind.RunJob) && fixtures.ContainsKey(current.Id))
                    {
                        redactedInputs++;
                        continue;
                    }
                    trace.Add(current, SnapshotInput(), null,
                        FlowTraceStepStatus.Unavailable, resolved.Code, outputUnavailable: true, errorCode: resolved.Code);
                    return Result(resolved.Code);
                }
                if (binding.Key == FlowJobTargetBindings.CheckpointKey)
                {
                    try { _ = FlowJobTargetBindings.Literal(current.ExecutionAction!.TargetBinding!, value); }
                    catch (ArgumentException)
                    {
                        trace.Add(current, SnapshotInput(), null, FlowTraceStepStatus.Unavailable,
                            "job-target-input-invalid", outputUnavailable: true, errorCode: "job-target-input-invalid");
                        return Result("job-target-input-invalid");
                    }
                }
                inputValues.Add(binding.Key, value);
            }
            var input = SnapshotInput();

            switch (current.Kind)
            {
                case FlowNodeKind.ExecutionEvent:
                    output = request.Input; outcome = FlowNodeCatalogue.Event;
                    break;
                case FlowNodeKind.AlertRaised:
                    output = request.Alert; outcome = FlowNodeCatalogue.Event;
                    break;
                case FlowNodeKind.Condition:
                    FlowPredicateEvaluation condition;
                    if (current.Predicate is { } predicate)
                        condition = FlowValueEvaluation.Matches(predicate, request.Input, request.Alert, outputs);
                    else if (request.Alert is not null && current.Condition is { } legacy)
                    {
                        var matches = FlowPureEvaluation.Matches(legacy, request.Alert);
                        condition = new(true, matches, matches ? "condition-true" : "condition-false");
                    }
                    else condition = new(false, false, "condition-input-unavailable");
                    if (!condition.Available)
                    {
                        trace.Add(current, input, null, FlowTraceStepStatus.Unavailable, condition.Code,
                            outputUnavailable: true, errorCode: condition.Code);
                        return Result(condition.Code);
                    }
                    output = new { result = condition.Result };
                    outcome = condition.Result ? FlowNodeCatalogue.True : FlowNodeCatalogue.False;
                    break;
                case FlowNodeKind.MapIncident:
                    try { fields = FlowTypedMapping.Map(current.Mapping!, request.Input, request.Alert, inputValues, simulatedAi: simulatedAi); }
                    catch (InvalidOperationException error) when (error.Message is "mapping-template" or "mapping-input-unavailable" or "mapping-output-limit")
                    {
                        trace.Add(current, input, null, FlowTraceStepStatus.Failed, error.Message,
                            outputUnavailable: true, errorCode: error.Message);
                        return Result(error.Message, valid: false);
                    }
                    output = fields; outcome = FlowNodeCatalogue.Result;
                    break;
                case FlowNodeKind.Finish:
                    trace.Add(current, input, new { outcome = "finished" }, FlowTraceStepStatus.Completed, "finished");
                    return Result("inert-preview");
                default:
                    if (!fixtures.TryGetValue(current.Id, out var fixture))
                    {
                        trace.Add(current, input, null, FlowTraceStepStatus.Planned, "sample-result-unavailable", outputUnavailable: true);
                        return Result("sample-result-unavailable");
                    }
                    outcome = fixture.Outcome;
                    var missingOutput = fixture.Output.ValueKind == JsonValueKind.Undefined;
                    output = missingOutput ? null : fixture.Output;
                    if (!missingOutput) outputs[current.Id] = fixture.Output.Clone();
                    edges.TryGetValue((current.Id, outcome), out var fixtureEdge);
                    trace.Add(current, input, output, FlowTraceStepStatus.Completed, outcome, fixtureEdge, missingOutput);
                    if (current.Kind == FlowNodeKind.InvestigateWithAi)
                        simulatedAi = AiSample(fixture);
                    if (fixtureEdge is null) return Result("inert-preview");
                    current = nodes[fixtureEdge.TargetNodeId];
                    continue;
            }
            outputs[current.Id] = JsonSerializer.SerializeToElement(output, Json);
            edges.TryGetValue((current.Id, outcome), out var edge);
            trace.Add(current, input, output, FlowTraceStepStatus.Completed, outcome, edge);
            if (edge is null) return Result("preview-path-unavailable");
            current = nodes[edge.TargetNodeId];
        }
        return Result("preview-step-limit", valid: false);
    }

    private static FlowValidationIssueDto? ValidateFixtures(FlowExecutionDryRunRequest request)
    {
        if (request.Fixtures is null) return null;
        if (request.Fixtures.Count > FlowGraphV2Limits.MaximumNodes) return new("preview-fixture-limit");
        var nodes = request.Graph.Nodes.ToDictionary(node => node.Id);
        var seen = new HashSet<Guid>();
        var total = 0;
        foreach (var fixture in request.Fixtures)
        {
            if (fixture is null || fixture.NodeId == Guid.Empty || !seen.Add(fixture.NodeId) ||
                !nodes.TryGetValue(fixture.NodeId, out var node) ||
                node.Kind is FlowNodeKind.AlertRaised or FlowNodeKind.ExecutionEvent or FlowNodeKind.Condition or FlowNodeKind.MapIncident or FlowNodeKind.Finish)
                return new("preview-fixture-node", fixture?.NodeId);
            if (!IsOutcome(fixture.Outcome)) return new("preview-fixture-outcome", fixture.NodeId);
            if (fixture.Output.ValueKind != JsonValueKind.Undefined)
            {
                try
                {
                    using var document = JsonDocument.Parse(fixture.Output.GetRawText(),
                        new JsonDocumentOptions { MaxDepth = FlowPreviewLimits.MaximumFixtureDepth });
                }
                catch (JsonException) { return new("preview-fixture-depth", fixture.NodeId); }
            }
            var size = JsonSerializer.SerializeToUtf8Bytes(fixture, Json).Length;
            total += size;
            if (size > FlowPreviewLimits.MaximumFixtureBytes || total > FlowPreviewLimits.MaximumFixtureTotalBytes)
                return new("preview-fixture-limit", fixture.NodeId);
            if (!ValidOutput(node.Kind, fixture)) return new("preview-fixture-output", fixture.NodeId);
        }
        return null;
    }

    private static bool IsOutcome(string? outcome) => outcome is FlowNodeCatalogue.Succeeded or FlowNodeCatalogue.Failed or
        FlowNodeCatalogue.TimedOut or FlowNodeCatalogue.Cancelled or FlowNodeCatalogue.Unknown or
        FlowNodeCatalogue.Unsupported or FlowNodeCatalogue.Denied;

    private static bool ValidOutput(FlowNodeKind kind, FlowPreviewFixtureDto fixture)
    {
        var output = fixture.Output;
        if (output.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return true;
        if (output.ValueKind != JsonValueKind.Object) return false;
        if (kind is FlowNodeKind.CreateIncident or FlowNodeKind.ActionGroup) return true;
        foreach (var property in output.EnumerateObject())
        {
            var valid = (kind, property.Name) switch
            {
                (FlowNodeKind.InvestigateWithAi, "summary" or "evidence" or "recommendations" or "status") =>
                    property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                (FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution, "exitCode") =>
                    property.Value.ValueKind == JsonValueKind.Null ||
                    property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetInt32(out _),
                (FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution,
                    "outcome" or "executionId" or "attemptId" or "summary" or "stdout" or "stderr") =>
                    property.Value.ValueKind is JsonValueKind.String or JsonValueKind.Null,
                (FlowNodeKind.RunJob or FlowNodeKind.RunLibraryScript or FlowNodeKind.RerunOriginalExecution, "data") =>
                    property.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array or JsonValueKind.Null,
                _ => false
            };
            if (!valid) return false;
        }
        return !output.TryGetProperty("outcome", out var outcome) ||
            outcome.ValueKind == JsonValueKind.String && outcome.GetString() == fixture.Outcome;
    }

    private static IReadOnlyDictionary<string, string> AiSample(FlowPreviewFixtureDto fixture)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["aiStatus"] = "Simulated " + fixture.Outcome };
        if (fixture.Output.ValueKind != JsonValueKind.Object) return values;
        foreach (var (property, field) in new[] { ("summary", "aiSummary"), ("evidence", "aiEvidence"), ("recommendations", "aiRecommendations") })
            if (fixture.Output.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String)
                values[field] = value.GetString()!;
        return values;
    }
}
