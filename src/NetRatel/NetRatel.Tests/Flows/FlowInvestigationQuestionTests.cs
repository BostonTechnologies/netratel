using System.Text;
using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowInvestigationQuestionTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void Source_context_includes_only_captured_bound_inputs_separate_from_original_facts()
    {
        var draft = Create() with
        {
            Fields = new("Synthetic failure", "Mapped earlier: investigate the protected diagnostic below."),
            InvestigationInputs = new Dictionary<string, JsonElement>
            {
                ["diagnostic"] = JsonSerializer.SerializeToElement(new { summary = "Synthetic prior command result", exitCode = 7 }, Json),
                ["executionId"] = JsonSerializer.SerializeToElement("9007199254740993"),
                ["empty"] = JsonSerializer.SerializeToElement(""),
                ["capturedNull"] = JsonSerializer.SerializeToElement<string?>(null)
            }
        };
        var before = JsonSerializer.Serialize(draft, Json);
        var question = FlowInvestigationQuestion.Compose(draft);
        Assert.Contains("Original execution facts", question);
        Assert.Contains("Original synthetic failure.", question);
        Assert.Contains("Mapped description snapshot:", question);
        Assert.Contains("Mapped earlier:", question);
        Assert.Contains("Explicitly bound input snapshots", question);
        Assert.Contains("Synthetic prior command result", question);
        Assert.Contains("\"executionId\":\"9007199254740993\"", question);
        Assert.Contains("\"empty\":\"\"", question);
        Assert.Contains("\"capturedNull\":null", question);
        Assert.Equal(before, JsonSerializer.Serialize(draft, Json));
    }

    [Fact]
    public void Captured_JSON_property_order_does_not_change_the_automated_question()
    {
        var draft = Create() with
        {
            InvestigationInputs = new Dictionary<string, JsonElement>
            {
                ["longer"] = JsonSerializer.Deserialize<JsonElement>("{\"zeta\":1,\"a\":2}"),
                ["a"] = JsonSerializer.SerializeToElement("Synthetic prior result")
            }
        };
        var reordered = draft with
        {
            InvestigationInputs = new Dictionary<string, JsonElement>
            {
                ["a"] = JsonSerializer.SerializeToElement("Synthetic prior result"),
                ["longer"] = JsonSerializer.Deserialize<JsonElement>("{\"a\":2,\"zeta\":1}")
            }
        };
        Assert.Equal(FlowInvestigationQuestion.Compose(draft), FlowInvestigationQuestion.Compose(reordered));
    }

    [Fact]
    public void Common_credentials_are_redacted_from_description_and_nested_bound_values()
    {
        var draft = Create() with
        {
            Fields = new("Synthetic failure", "Diagnostic password=synthetic-description-secret"),
            InvestigationInputs = new Dictionary<string, JsonElement>
            {
                ["diagnostic"] = JsonSerializer.SerializeToElement(new
                {
                    api_key = "synthetic-api-secret",
                    nested = new { authorization = "synthetic-authorization-secret", message = "Bearer synthetic-bearer-secret" },
                    safe = "Synthetic useful evidence"
                }, Json),
                ["token"] = JsonSerializer.SerializeToElement("synthetic-token-secret")
            }
        };
        var question = FlowInvestigationQuestion.Compose(draft);
        foreach (var secret in new[] { "synthetic-description-secret", "synthetic-api-secret",
            "synthetic-authorization-secret", "synthetic-bearer-secret", "synthetic-token-secret" })
            Assert.DoesNotContain(secret, question);
        Assert.Contains("[REDACTED]", question);
        Assert.Contains("Synthetic useful evidence", question);
    }

    [Fact]
    public void Additional_context_is_UTF8_bounded_and_reports_display_truncation_without_altering_source()
    {
        var draft = Create() with
        {
            Fields = new("Synthetic failure", string.Concat(Enumerable.Repeat("🧪", 1400))),
            InvestigationInputs = new Dictionary<string, JsonElement>
            {
                ["diagnostic"] = JsonSerializer.SerializeToElement(new string('x', 3000))
            }
        };
        var question = FlowInvestigationQuestion.Compose(draft);
        var original = FlowExecutionEvidenceFormatter.InvestigationQuestion(draft.Event.ExecutionEvent!, draft.TenantId);
        Assert.StartsWith(original, question);
        var additional = question[original.Length..];
        Assert.True(Encoding.UTF8.GetByteCount(additional) <= FlowInvestigationQuestion.MaximumAdditionalContextBytes);
        Assert.Contains("Mapped description snapshot:", additional);
        Assert.Contains("Explicitly bound input snapshots", additional);
        Assert.Contains("[Truncated for AI context", additional);
        Assert.DoesNotContain("�", additional);
        Assert.Equal(3000, draft.InvestigationInputs["diagnostic"].GetString()!.Length);
    }

    [Fact]
    public void Missing_empty_and_oversized_captured_inputs_are_distinct()
    {
        var draft = Create();
        Assert.Contains("Unavailable (no input snapshot captured).", FlowInvestigationQuestion.Compose(draft));
        Assert.Contains("Empty (captured): {}", FlowInvestigationQuestion.Compose(draft with
            { InvestigationInputs = new Dictionary<string, JsonElement>() }));
        var oversized = draft with
        {
            InvestigationInputs = new Dictionary<string, JsonElement>
                { ["diagnostic"] = JsonSerializer.SerializeToElement(new string('x', FlowInvestigationQuestion.MaximumInputsBytes)) }
        };
        Assert.Throws<ArgumentException>(() => FlowInvestigationQuestion.Compose(oversized));
        Assert.Throws<ArgumentException>(() => FlowInvestigationQuestion.Compose(draft with
            { Kind = FlowActionKind.RatelDeskIncident }));
    }

    [Fact]
    public void Input_snapshot_is_null_omitted_for_legacy_alert_drafts()
    {
        var draft = Create();
        draft = draft with { Event = draft.Event with
        {
            ExecutionEvent = null,
            Data = new(Guid.NewGuid(), Guid.NewGuid(), "Synthetic legacy rule", "Synthetic legacy client",
                "cpu", "usage", "error", 95, null, draft.Event.OccurredAtUtc)
        } };
        Assert.DoesNotContain("investigationInputs", JsonSerializer.Serialize(draft, Json));
        Assert.Equal("Investigate the linked monitoring alert for the selected systems. Gather evidence with permitted NetRatel tools, explain the likely cause, and propose remediation. Request operator approval before changing systems.\n\nAlert description:\n" + draft.Fields.Description,
            FlowInvestigationQuestion.Compose(draft));
        Assert.Contains("Explicitly bound input snapshots", FlowInvestigationQuestion.Compose(draft with
        {
            InvestigationInputs = new Dictionary<string, JsonElement>
                { ["priorOutput"] = JsonSerializer.SerializeToElement("Synthetic prior alert output") }
        }));
    }

    private static FlowIncidentActionDraft Create()
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 17, FlowExecutionSourceKind.Job,
            new("42", "Synthetic source job", "7", new string('a', 64)), "9007199254740993", "9007199254740993",
            new(Guid.NewGuid(), "Synthetic target"), FlowExecutionOutcome.Failed, "Failed", at, at)
        {
            StartedAtUtc = at.AddSeconds(-5), CompletedAtUtc = at, Reason = "Original synthetic failure.",
            Provenance = "job-terminal-ready"
        };
        var envelope = new FlowEventEnvelope(17, source.EventId, source.OccurrenceId, Guid.NewGuid(), at,
            new("synthetic-publisher"), null!) { ExecutionEvent = source };
        return new(17, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 7, Guid.NewGuid(), "synthetic:ai:1",
            envelope, new("Synthetic failure", "Mapped before investigation."))
            { Kind = FlowActionKind.NetclawInvestigation };
    }
}
