using NetRatel.Shared.Contracts.Flows;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionEventMatchingTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-05T12:00:00Z");

    [Fact]
    public void Source_qualified_selection_does_not_confuse_job_and_script_numeric_ids()
    {
        var trigger = Combined() with { Scope = FlowExecutionScope.SelectedDefinitions,
            DefinitionReferences = [new(FlowExecutionSourceKind.Job, "42")] };
        Assert.True(FlowExecutionEventMatching.Matches(7, trigger, Event(FlowExecutionSourceKind.Job, "42"), Now.AddMinutes(-1)));
        Assert.False(FlowExecutionEventMatching.Matches(7, trigger, Event(FlowExecutionSourceKind.Script, "42"), Now.AddMinutes(-1)));
        Assert.False(FlowExecutionEventMatching.Matches(8, trigger, Event(FlowExecutionSourceKind.Job, "42"), Now.AddMinutes(-1)));
        Assert.False(FlowExecutionEventMatching.Matches(7, trigger, Event(FlowExecutionSourceKind.Job, "42"), Now.AddMinutes(1)));
    }

    [Fact]
    public void Combined_subscription_is_per_version_attempt_and_outcome_and_keeps_both_sources_in_either_order()
    {
        var version = Guid.NewGuid();
        var node = Guid.NewGuid();
        var trigger = Combined();
        var job = Event(FlowExecutionSourceKind.Job, "42") with { ExecutionId = "9007199254740993", AttemptId = "9007199254740993", CorrelationId = "job-run:9007199254740993" };
        var request = Event(FlowExecutionSourceKind.Request, "42") with { ExecutionId = "71", AttemptId = job.AttemptId, CorrelationId = job.CorrelationId };
        var a = FlowExecutionEventMatching.Aggregate(version, node, trigger, [job, request]);
        var b = FlowExecutionEventMatching.Aggregate(version, node, trigger, [request, job]);
        Assert.Equal(a.AdmissionKey, b.AdmissionKey);
        Assert.Equal(job.EventId, a.Representative.EventId);
        Assert.Equal(a.ContributingEventIds, b.ContributingEventIds);
        Assert.Contains(request.EventId, a.ContributingEventIds);
        Assert.NotEqual(a.AdmissionKey, FlowExecutionEventMatching.AdmissionKey(Guid.NewGuid(), node, trigger, job));
        Assert.NotEqual(a.AdmissionKey, FlowExecutionEventMatching.AdmissionKey(version, node, trigger,
            job with { AttemptId = "9007199254740994", ExecutionId = "9007199254740994", CorrelationId = "job-run:9007199254740994" }));
        Assert.NotEqual(a.AdmissionKey, FlowExecutionEventMatching.AdmissionKey(version, node, trigger,
            job with { Outcome = FlowExecutionOutcome.Unknown, RawStatus = "Unknown" }));
        Assert.NotEqual(FlowExecutionEventMatching.AdmissionKey(version, node, trigger with { AggregateCorrelatedEvents = false }, job),
            FlowExecutionEventMatching.AdmissionKey(version, node, trigger with { AggregateCorrelatedEvents = false }, request));
    }

    [Fact]
    public void Request_only_failure_matches_immediately_and_request_kind_and_target_filters_are_exact()
    {
        var agent = Guid.NewGuid();
        var request = Event(FlowExecutionSourceKind.Request, null) with
        { RequestKind = "synthetic-source", CorrelationId = "request:71", Target = new(agent) };
        var trigger = Combined() with { AgentIds = [agent], RequestKinds = ["synthetic-source"] };
        Assert.True(FlowExecutionEventMatching.Matches(7, trigger, request, Now.AddMinutes(-1)));
        var aggregate = FlowExecutionEventMatching.Aggregate(Guid.NewGuid(), Guid.NewGuid(), trigger, [request]);
        Assert.Equal(request.EventId, aggregate.Representative.EventId);
        Assert.Single(aggregate.ContributingEventIds);
        Assert.False(FlowExecutionEventMatching.Matches(7, trigger, request with { RequestKind = "different-source" }, Now.AddMinutes(-1)));
        Assert.False(FlowExecutionEventMatching.Matches(7, trigger, request with { Target = new(Guid.NewGuid()) }, Now.AddMinutes(-1)));
    }

    private static FlowExecutionTriggerSettingsDto Combined() => new(
        [FlowExecutionSourceKind.Job, FlowExecutionSourceKind.Script, FlowExecutionSourceKind.Request],
        [FlowExecutionOutcome.Failed, FlowExecutionOutcome.Unknown], FlowExecutionScope.AllInTenant);

    private static FlowExecutionEventDto Event(FlowExecutionSourceKind source, string? definition) =>
        new(Guid.NewGuid(), Guid.NewGuid(), 7, source, new(definition), "71", "71", new(null),
            FlowExecutionOutcome.Failed, "Failed", Now, Now);
}
