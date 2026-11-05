using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Infrastructure.Notifications;
using NetRatel.Infrastructure.RatelDesk;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.RatelDesk;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionPresentationTests
{
    [Fact]
    public void Independent_execution_AI_turn_is_stable_per_action_and_preserves_legacy_turn_identity()
    {
        const string key = "flow-17/run-1/node-1/attempt-1";
        var message = FlowAiCorrelation.ExecutionMessageId(key);
        Assert.Equal(message, FlowAiCorrelation.ExecutionMessageId(key));
        Assert.NotEqual(message, FlowAiCorrelation.ExecutionMessageId(key + "/rerun"));
        var lease = FlowTestData.Lease("Legacy synthetic worker", 90);
        var node = lease.Version.Graph.Nodes.Single(x => x.Kind == FlowNodeKind.CreateIncident);
        var draft = new FlowIncidentActionDraft(17, lease.RunId, node.Id, node.ConnectorId!.Value, 1,
            lease.SourceInstanceId, key, lease.Event, new("Original alert", "Original description"));
        Assert.Equal(lease.Event.EventId, FlowAiCorrelation.MessageId(draft));
        Assert.DoesNotContain("executionEvent", JsonSerializer.Serialize(lease.Event, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    }

    [Fact]
    public void Execution_source_presentation_and_turn_identity_work_without_any_monitoring_payload()
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var source = new FlowExecutionEventDto(Guid.Parse("e0000000-0000-0000-0000-000000000002"), Guid.NewGuid(), 17,
            FlowExecutionSourceKind.Request, new(null, "Synthetic request"), "9007199254740993", "1",
            new(Guid.NewGuid(), "Synthetic worker"), FlowExecutionOutcome.TimedOut, "TimedOut", at, at);
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        // Read a genuine execution envelope shape with no legacy alert payload; presentation grants no admission.
        var envelope = JsonSerializer.Deserialize<FlowEventEnvelope>(JsonSerializer.Serialize(new
        {
            TenantId = 17, source.EventId, source.OccurrenceId, FlowVersionId = Guid.NewGuid(), source.OccurredAtUtc,
            Authority = new FlowExecutionAuthorityDto("synthetic-principal"), ExecutionEvent = source
        }, options), options)!;
        Assert.Null(envelope.Data);
        var draft = new FlowIncidentActionDraft(17, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(),
            "flow-17/run-1/node-1/attempt-1", envelope, new("Request timed out", "Original request failure evidence."));
        Assert.NotEqual(source.EventId, FlowAiCorrelation.MessageId(draft));
        Assert.Equal(FlowAiCorrelation.ExecutionMessageId(draft.IdempotencyKey), FlowAiCorrelation.MessageId(draft));
        var description = FlowIncidentSourcePresentation.Description(draft);
        Assert.Contains("NetRatel execution occurrence", description);
        Assert.Contains(draft.RunId.ToString("D"), description);
        Assert.DoesNotContain("monitoring occurrence", description);
        Assert.DoesNotContain("Severity:", description);
        Assert.Equal(2, FlowIncidentSourcePresentation.Priority(draft, new()));
        Assert.Equal(1, FlowIncidentSourcePresentation.Priority(draft with { Fields = draft.Fields with { Priority = 1 } }, new()));
        var notification = NotificationMessageFormatter.Format(draft);
        Assert.Contains("Source: Request", notification);
        Assert.Contains("Outcome: TimedOut", notification);
        Assert.Contains("Execution: 9007199254740993", notification);
        Assert.DoesNotContain("Rule:", notification);
    }

    [Fact]
    public void Existing_monitoring_receiver_description_and_priority_are_unchanged()
    {
        var lease = FlowTestData.Lease("Legacy synthetic worker", 90);
        var node = lease.Version.Graph.Nodes.Single(x => x.Kind == FlowNodeKind.CreateIncident);
        var draft = new FlowIncidentActionDraft(17, lease.RunId, node.Id, node.ConnectorId!.Value, 1,
            lease.SourceInstanceId, "legacy-action", lease.Event, new("Alert", "Mapped description"));
        var data = draft.Event.Data;
        var expected = draft.Fields.Description + "\n\nNetRatel monitoring occurrence\n" +
            $"Client: {data.ClientName} ({data.AgentId:D})\nResource: {data.Resource}\n" +
            $"Rule: {data.RuleName} ({data.RuleId:D})\nSeverity: {data.Severity}\n" +
            $"Metric: {data.Metric}\nObserved value: {data.NumericValue?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? data.ServiceState ?? "Unknown"}\n" +
            $"Observed at: {data.ObservedAtUtc:O}\nOccurred at: {draft.Event.OccurredAtUtc:O}\n" +
            $"Source: {draft.SourceInstanceId:D}/{draft.Event.OccurrenceId:D}/{draft.Event.EventId:D}";
        Assert.Equal(expected, FlowIncidentSourcePresentation.Description(draft));
        var priorities = new RatelDeskPriorityMapping();
        Assert.Equal(priorities.Critical, FlowIncidentSourcePresentation.Priority(draft, priorities));
    }
}
