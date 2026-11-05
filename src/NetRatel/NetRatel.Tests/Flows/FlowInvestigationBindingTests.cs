using System.Text.Json;
using NetRatel.Application.Flows;
using NetRatel.Application.Netclaw;
using NetRatel.Infrastructure.Netclaw;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Netclaw;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowInvestigationBindingTests
{
    [Fact]
    public void Saved_source_turn_uses_independent_message_identity_and_no_monitoring_container()
    {
        var saved = Create();
        Assert.Null(saved.Draft.Event.Data);
        Assert.NotEqual(saved.Draft.Event.EventId, saved.Row.ActiveMessageId);
        Assert.True(FlowInvestigationBinding.MatchesExecution(saved.Row, saved.Action, saved.Draft, saved.Profile, "source-session"));
        saved.Action.Status = FlowActionStatus.Dispatching;
        saved.Action.AiResultJson = null;
        Assert.True(FlowInvestigationBinding.MatchesExecution(saved.Row, saved.Action, saved.Draft, saved.Profile, "source-session"));
    }

    [Theory]
    [InlineData("node")]
    [InlineData("run")]
    [InlineData("tenant")]
    [InlineData("publisher")]
    [InlineData("credential")]
    [InlineData("message")]
    [InlineData("target")]
    [InlineData("extra-target")]
    [InlineData("monitoring-occurrence")]
    [InlineData("event-reference")]
    [InlineData("profile")]
    [InlineData("session")]
    [InlineData("completed")]
    [InlineData("foreign-investigation")]
    [InlineData("result-node")]
    [InlineData("foreign-version")]
    [InlineData("source-outcome")]
    [InlineData("missing-result")]
    public void A_saved_turn_cannot_admit_another_scope_or_a_completed_result(string fault)
    {
        var saved = Create();
        var draft = saved.Draft;
        var profile = saved.Profile;
        var session = "source-session";
        var source = draft.Event.ExecutionEvent ?? throw new InvalidOperationException("Missing source.");
        switch (fault)
        {
            case "node": saved.Action.NodeId = Guid.NewGuid(); break;
            case "run": saved.Row.FlowRunId = Guid.NewGuid(); break;
            case "tenant": saved.Row.TenantId = 18; break;
            case "publisher": saved.Row.CreatedBy = "another-publisher"; break;
            case "credential": saved.Row.ActiveCredentialId = "another-credential"; break;
            case "message": saved.Row.ActiveMessageId = draft.Event.EventId; break;
            case "target": saved.Row.AgentIds = [Guid.NewGuid()]; break;
            case "extra-target": saved.Row.AgentIds = [saved.Row.AgentIds[0], Guid.NewGuid()]; break;
            case "monitoring-occurrence": saved.Row.OccurrenceId = draft.Event.OccurrenceId; break;
            case "event-reference": draft = draft with { Event = draft.Event with { EventId = Guid.NewGuid() } }; break;
            case "profile": profile = profile with { Revision = profile.Revision + 1 }; break;
            case "session": session = "another-session"; break;
            case "completed": saved.Action.AiResultJson = JsonSerializer.Serialize(saved.Result with { Status = FlowAiResultStatus.Completed }); break;
            case "foreign-investigation": saved.Action.AiResultJson = JsonSerializer.Serialize(saved.Result with { InvestigationId = Guid.NewGuid() }); break;
            case "result-node": saved.Action.AiResultJson = JsonSerializer.Serialize(saved.Result with { ActionNodeId = Guid.NewGuid() }); break;
            case "foreign-version": saved.Action.AiResultJson = JsonSerializer.Serialize(saved.Result with { FlowVersionId = Guid.NewGuid() }); break;
            case "source-outcome": draft = draft with { Event = draft.Event with { ExecutionEvent = source with { RawStatus = "Running" } } }; break;
            case "missing-result": saved.Action.AiResultJson = null; break;
            default: throw new ArgumentOutOfRangeException(nameof(fault));
        }
        Assert.False(FlowInvestigationBinding.MatchesExecution(saved.Row, saved.Action, draft, profile, session));
    }

    [Fact]
    public async Task Current_native_policy_does_not_call_the_client_or_claim_a_restricted_turn()
    {
        var saved = Create();
        var client = new UncalledClient();
        var result = await new NativeFlowInvestigationToolPolicy().EnforceDiagnosticsOnlyAsync(client, saved.Profile,
            "source-session", saved.Row.Id, saved.Result.MessageId, saved.Draft);
        Assert.False(result.Allowed);
        Assert.Equal(NativeFlowInvestigationToolPolicy.UnavailableCode, result.Code);
        Assert.Equal(0, client.Calls);
    }

    private sealed record Saved(AiInvestigationRecord Row, FlowActionRecord Action, FlowIncidentActionDraft Draft,
        NetclawRuntimeProfile Profile, FlowAiResultDto Result);

    private static Saved Create()
    {
        var at = DateTimeOffset.Parse("2026-11-05T12:00:00Z");
        var agentId = Guid.NewGuid();
        var source = new FlowExecutionEventDto(Guid.NewGuid(), Guid.NewGuid(), 17, FlowExecutionSourceKind.Job,
            new("42", "Synthetic job", "7"), "9007199254740993", "9007199254740993", new(agentId),
            FlowExecutionOutcome.Failed, "Failed", at, at) { CompletedAtUtc = at, Provenance = "job-terminal-ready" };
        var runId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var nodeId = Guid.NewGuid();
        var authority = new FlowExecutionAuthorityDto("source-publisher", "source-credential");
        // A real source envelope has no monitoring payload; do not construct fake alert context.
        var envelope = JsonSerializer.Deserialize<FlowEventEnvelope>(JsonSerializer.Serialize(new
        {
            tenantId = 17, eventId = source.EventId, occurrenceId = source.OccurrenceId, flowVersionId = versionId,
            occurredAtUtc = at, authority, executionEvent = source
        }), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidOperationException("Missing test envelope.");
        var profile = new NetclawRuntimeProfile(17, Guid.NewGuid(), 7, "https://daemon.example.test/hub/session",
            "synthetic-device-token", false, "synthetic-profile");
        var draft = new FlowIncidentActionDraft(17, runId, nodeId, profile.Id, profile.Revision, Guid.NewGuid(),
            "flow:synthetic:source:investigation:1", envelope, new("Investigate original failure", "Original facts"))
            { Kind = FlowActionKind.NetclawInvestigation, AiDeadlineAtUtc = at.AddHours(1) };
        var messageId = FlowAiCorrelation.MessageId(draft);
        var row = new AiInvestigationRecord
        {
            Id = Guid.NewGuid(), TenantId = 17, RequestId = Guid.NewGuid(), FlowRunId = runId,
            SourceActionKey = draft.IdempotencyKey, CreatedBy = authority.PrincipalId, ActiveCredentialId = authority.IntegrationCredentialId,
            AgentIds = [agentId], Automated = true, ActiveMessageId = messageId,
            State = InvestigationState.Processing, RemoteSessionId = "source-session", ProfileFingerprint = profile.ProfileFingerprint
        };
        var result = new FlowAiResultDto(17, runId, versionId, nodeId, row.Id, messageId,
            FlowAiResultStatus.Queued, at);
        var action = new FlowActionRecord
        {
            TenantId = 17, RunId = runId, NodeId = nodeId, IdempotencyKey = draft.IdempotencyKey,
            Status = FlowActionStatus.WaitingForAi, DraftJson = JsonSerializer.Serialize(draft),
            AiResultJson = JsonSerializer.Serialize(result)
        };
        return new(row, action, draft, profile, result);
    }

    private sealed class UncalledClient : IAiAssistantChatClient
    {
        public int Calls { get; private set; }
        public bool IsConnected => false;
        public Task<SessionEnsureResult> ConnectAsync(string? sessionId, Func<JsonElement, Task> output, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("The unsupported policy must not connect."); }
        public Task SendAsync(string sessionId, string text, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("The unsupported policy must not send."); }
        public Task RespondAsync(string sessionId, string callId, string key, CancellationToken ct)
        { Calls++; throw new InvalidOperationException("The unsupported policy must not approve."); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
