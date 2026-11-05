using System.Text.Json;

namespace NetRatel.Shared.Contracts.Flows;

public sealed record FlowTemplateDescriptorDto(string Id, string Name, string Description, int SchemaVersion,
    IReadOnlyList<string> RequiredConfiguration);

/// <summary>Pure configurable draft graphs. Creating a template never publishes, enables or executes it.</summary>
public static class FlowOverhaulTemplates
{
    public const string AlertIncident = "alert-incident";
    public const string ExecutionFailureIncident = "execution-failure-incident";
    public const string ScriptRecovery = "script-recovery";
    public const string JobSuccessScript = "job-success-script";
    public const string JobSuccessNotification = "job-success-notification";
    public const string RequestTimeoutUnknown = "request-timeout-unknown";

    public static IReadOnlyList<FlowTemplateDescriptorDto> Descriptors { get; } = Array.AsReadOnly<FlowTemplateDescriptorDto>(
    [
        new(AlertIncident, "Alert to incident", "The existing alert mapping and RatelDesk path.", 1,
            ["RatelDesk connection"]),
        new(ExecutionFailureIncident, "Job or request failure to incident",
            "Filter a failure, investigate, enrich and raise an incident with the actual investigation disposition.", 2,
            ["Execution scope", "AI connection and supported investigation policy", "RatelDesk connection"]),
        new(ScriptRecovery, "Library script repair and linked rerun",
            "Choose the source scripts and permitted repair; verify the original execution's real rerun result.", 2,
            ["Source library scripts", "Pinned repair script", "RatelDesk connection"]),
        new(JobSuccessScript, "Job success to follow-up script",
            "Run a pinned library script after an authorized job succeeds.", 2,
            ["Job scope", "Pinned follow-up script", "RatelDesk connection for unsuccessful follow-up"]),
        new(JobSuccessNotification, "Job success to notification",
            "Deliver through one or more configured notification destinations.", 2,
            ["Job scope", "Notification destinations"]),
        new(RequestTimeoutUnknown, "Request timeout and unknown outcome",
            "Keep authoritative timeout separate from an unconfirmed result; neither path reruns the request.", 2,
            ["Request scope", "RatelDesk connection"])
    ]);

    public static FlowGraphDto CreateDraft(string templateId) => templateId switch
    {
        AlertIncident => FlowGraphTemplates.IncidentFromAlert(),
        ExecutionFailureIncident => FailureIncident(),
        ScriptRecovery => Recovery(),
        JobSuccessScript => SuccessScript(),
        JobSuccessNotification => SuccessNotification(),
        RequestTimeoutUnknown => TimeoutAndUnknown(),
        _ => throw new ArgumentException("The flow template is unsupported.", nameof(templateId))
    };

    private static FlowGraphDto FailureIncident()
    {
        var source = Trigger("Job or request failed", [FlowExecutionSourceKind.Job, FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.Failed]);
        var filter = Condition("Confirm the failed outcome", FlowNodeCatalogue.Failed, new(340, 100));
        var ai = Node(FlowNodeKind.InvestigateWithAi, "Investigate original failure", 660, 100);
        var mapping = Map("Enrich original execution evidence", 980, 100);
        var incident = Node(FlowNodeKind.CreateIncident, "RatelDesk: Create incident", 1300, 100);
        var finish = Node(FlowNodeKind.Finish, "Filtered out", 660, 500);
        return Graph([source, filter, ai, mapping, incident, finish],
            [Edge(source, FlowNodeCatalogue.Event, filter),
             Edge(filter, FlowNodeCatalogue.True, ai), Edge(filter, FlowNodeCatalogue.False, finish),
             .. Outcomes(ai, mapping), Edge(mapping, FlowNodeCatalogue.Result, incident)]);
    }

    private static FlowGraphDto Recovery()
    {
        var source = Trigger("Selected library script failed", [FlowExecutionSourceKind.Script],
            [FlowExecutionOutcome.Failed, FlowExecutionOutcome.TimedOut], selected: true);
        var repair = Node(FlowNodeKind.RunLibraryScript, "Choose a permitted repair script", 340, 100);
        var rerun = Node(FlowNodeKind.RerunOriginalExecution, "Rerun original execution once", 660, 100) with { Rerun = new() };
        var recovered = Node(FlowNodeKind.Finish, "Verified rerun succeeded", 980, 100);
        var mapping = Map("Preserve unresolved recovery evidence", 980, 500);
        var incident = Node(FlowNodeKind.CreateIncident, "RatelDesk: Create incident", 1300, 500);
        return Graph([source, repair, rerun, recovered, mapping, incident],
            [Edge(source, FlowNodeCatalogue.Event, repair),
             .. Outcomes(repair, mapping).Select(edge => edge.SourcePort == FlowNodeCatalogue.Succeeded
                 ? edge with { TargetNodeId = rerun.Id } : edge),
             .. Outcomes(rerun, mapping).Select(edge => edge.SourcePort == FlowNodeCatalogue.Succeeded
                 ? edge with { TargetNodeId = recovered.Id } : edge),
             Edge(mapping, FlowNodeCatalogue.Result, incident)]);
    }

    private static FlowGraphDto SuccessScript()
    {
        var source = Trigger("Job succeeded", [FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Succeeded]);
        var script = Node(FlowNodeKind.RunLibraryScript, "Choose a follow-up library script", 340, 100);
        var finish = Node(FlowNodeKind.Finish, "Follow-up succeeded", 660, 100);
        var mapping = Map("Preserve unsuccessful follow-up evidence", 660, 500);
        var incident = Node(FlowNodeKind.CreateIncident, "RatelDesk: Create incident", 980, 500);
        return Graph([source, script, finish, mapping, incident],
            [Edge(source, FlowNodeCatalogue.Event, script),
             .. Outcomes(script, mapping).Select(edge => edge.SourcePort == FlowNodeCatalogue.Succeeded
                 ? edge with { TargetNodeId = finish.Id } : edge),
             Edge(mapping, FlowNodeCatalogue.Result, incident)]);
    }

    private static FlowGraphDto SuccessNotification()
    {
        var source = Trigger("Job succeeded", [FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Succeeded]);
        var notification = Node(FlowNodeKind.ActionGroup, "Configure success notifications", 660, 100);
        var mapping = Map("Map job success notification", 340, 100);
        return Graph([source, mapping, notification],
            [Edge(source, FlowNodeCatalogue.Event, mapping), Edge(mapping, FlowNodeCatalogue.Result, notification)]);
    }

    private static FlowGraphDto TimeoutAndUnknown()
    {
        var source = Trigger("Request timed out or remains unknown", [FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.TimedOut, FlowExecutionOutcome.Unknown]);
        var condition = Condition("Authoritative timeout", FlowNodeCatalogue.TimedOut, new(340, 100));
        var timeout = Map("Map authoritative timeout", 660, 100) with
        {
            Mapping = new("Request timeout: {definitionName}", "The source recorded an authoritative timeout.\nExecution: {executionId}\nReason: {reason}")
        };
        var unknown = Map("Map unconfirmed outcome", 660, 500) with
        {
            Mapping = new("Unconfirmed request outcome: {definitionName}", "The outcome is Unknown, not a definitive failure or timeout. No rerun was attempted.\nExecution: {executionId}\nReason: {reason}")
        };
        var incident = Node(FlowNodeKind.CreateIncident, "RatelDesk: Create incident", 980, 300);
        return Graph([source, condition, timeout, unknown, incident],
            [Edge(source, FlowNodeCatalogue.Event, condition),
             Edge(condition, FlowNodeCatalogue.True, timeout), Edge(condition, FlowNodeCatalogue.False, unknown),
             Edge(timeout, FlowNodeCatalogue.Result, incident), Edge(unknown, FlowNodeCatalogue.Result, incident)]);
    }

    private static FlowNodeDto Trigger(string name, IReadOnlyList<FlowExecutionSourceKind> sources,
        IReadOnlyList<FlowExecutionOutcome> outcomes, bool selected = false) =>
        Node(FlowNodeKind.ExecutionEvent, name, 20, 100) with
        {
            ExecutionTrigger = new(sources, outcomes, selected ? FlowExecutionScope.SelectedDefinitions : FlowExecutionScope.AllInTenant)
            { DefinitionReferences = selected ? [] : null }
        };
    private static FlowNodeDto Condition(string name, string outcome, FlowPositionDto position) =>
        new(Guid.NewGuid(), FlowNodeKind.Condition, name, position)
        {
            Predicate = new(new(FlowInputSource.Event, FlowDataType.Text, "event.outcome"),
                FlowComparison.Equal, JsonSerializer.SerializeToElement(outcome))
        };
    private static FlowNodeDto Map(string name, double x, double y) =>
        new(Guid.NewGuid(), FlowNodeKind.MapIncident, name, new(x, y),
            Mapping: new("{definitionName}: {outcome}", "Source: {sourceKind}\nExecution: {executionId}\nAttempt: {attemptId}\nTarget: {targetName}\nOriginal status: {rawStatus}\nReason: {reason}"));
    private static FlowNodeDto Node(FlowNodeKind kind, string name, double x, double y) =>
        new(Guid.NewGuid(), kind, name, new(x, y));
    private static FlowEdgeDto Edge(FlowNodeDto from, string outcome, FlowNodeDto to) =>
        new(from.Id, outcome, to.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() };
    private static IEnumerable<FlowEdgeDto> Outcomes(FlowNodeDto from, FlowNodeDto to) =>
        FlowNodeCatalogue.Find(from.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
            .Select(port => Edge(from, port.Id, to));
    private static FlowGraphDto Graph(IReadOnlyList<FlowNodeDto> nodes, IReadOnlyList<FlowEdgeDto> edges) =>
        new(FlowGraphV2Limits.SchemaVersion, nodes, edges, new());
}
