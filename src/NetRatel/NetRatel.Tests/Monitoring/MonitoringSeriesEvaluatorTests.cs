using System.Collections.Immutable;
using NetRatel.Application.Monitoring;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Services;

using Xunit;
using System.Text.Json;

namespace NetRatel.Tests.Monitoring;

public sealed class MonitoringSeriesEvaluatorTests
{
[Fact]
public void Sustained()
{
    var s = new Scenario(action: true); s.Fire();
    Check(s.State.Phase == MonitoringPhase.Firing && s.Raises == 1 && s.Outbox.Count == 1);
    var occurrence = s.State.Occurrence!;
    for (var at = 80; at <= 200; at += 20) s.Observe(at, 95);
    Check(s.Raises == 1 && s.Outbox.Count == 1 && s.State.Occurrence!.OccurrenceId == occurrence.OccurrenceId);
    s.Recover(220); Check(s.State.Phase == MonitoringPhase.Resolved && s.Resolves == 1);
    s.Fire(270); Check(s.Raises == 2 && s.Outbox.Count == 2 && s.State.Occurrence!.OccurrenceId != occurrence.OccurrenceId);
}
[Fact]
public void Hysteresis()
{
    var s = new Scenario(); s.Observe(0, 95); s.Observe(20, 85);
    Check(s.State.Phase == MonitoringPhase.Healthy);
    s.Fire(40); var occurrence = s.State.Occurrence!.OccurrenceId;
    s.Observe(120, 70); Check(s.State.Phase == MonitoringPhase.Recovering);
    s.Observe(140, 85); Check(s.State.Phase == MonitoringPhase.Firing && s.Resolves == 0);
    Check(s.State.Occurrence!.OccurrenceId == occurrence);
    s.Recover(160); Check(s.Resolves == 1);
}
[Fact]
public void Unknown()
{
    var s = new Scenario(); s.Observe(0, 95); s.Observe(20, 95); s.Observe(25, 0, complete: false);
    Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown && s.State.WindowStartedAtUtc is null);
    s.Fire(40); Check(s.State.Occurrence!.RaisedAtUtc == s.Clock.At(100));
    var id = s.State.Occurrence.OccurrenceId;
    s.Observe(120, 0, complete: false); Check(s.State.Phase == MonitoringPhase.Firing && s.State.Occurrence.OccurrenceId == id && s.Resolves == 0);
}
[Fact]
public void UnknownRecovery()
{
    var s = new Scenario(); s.Fire(); s.Observe(80, 70); s.Observe(100, 70); s.Observe(105, 0, supported: false);
    Check(s.State.Phase == MonitoringPhase.Firing && s.State.WindowStartedAtUtc is null);
    s.Recover(120); Check(s.State.Occurrence!.EndedAtUtc == s.Clock.At(150));
}
[Fact]
public void NoTimerTransitions()
{
    var s = new Scenario(); s.Observe(0, 95); s.Clock.Set(70); s.Apply(s.Evaluator.Refresh(s.State, s.Rule, [], s.StreamId));
    Check(s.Raises == 0 && s.State.Phase == MonitoringPhase.Healthy);
    s.Fire(80); s.Observe(160, 70); s.Clock.Set(200); s.Apply(s.Evaluator.Refresh(s.State, s.Rule, [], s.StreamId));
    Check(s.Resolves == 0 && s.State.Phase == MonitoringPhase.Firing);
}
[Fact]
public void Gap()
{
    var s = new Scenario(); s.Observe(0, 95); s.Observe(20, 95); s.Observe(60, 95);
    Check(s.Raises == 0 && s.State.WindowStartedAtUtc == s.Clock.At(60));
    s.Observe(80, 95); s.Observe(100, 95); s.Observe(120, 95); Check(s.Raises == 1);
}
[Fact]
public void Restart()
{
    var s = new Scenario(); s.Observe(0, 95); s.Observe(20, 95); s.Clock.Set(80);
    s.Apply(s.Evaluator.ResumeAfterRestart(s.State, s.Rule)); Check(s.State.WindowStartedAtUtc is null);
    s.Fire(80); Check(s.State.Occurrence!.RaisedAtUtc == s.Clock.At(140));
    var occurrence = s.State.Occurrence.OccurrenceId;
    s.Clock.Set(160); s.Apply(s.Evaluator.ResumeAfterRestart(s.State, s.Rule)); s.Observe(160, 95);
    Check(s.Raises == 1 && s.State.Occurrence.OccurrenceId == occurrence);
}
[Fact]
public void Epoch()
{
    var s = new Scenario(); s.Observe(0, 95); s.Observe(20, 95); s.Epoch = 2; s.Sequence = 0; s.Observe(40, 95);
    Check(s.State.WindowStartedAtUtc == s.Clock.At(40));
    s.Observe(60, 95); s.Observe(80, 95); s.Observe(100, 95); var occurrence = s.State.Occurrence!.OccurrenceId;
    var stale = s.Observation(110, 95) with { Cursor = new(1, 1000) };
    var prior = s.State; var result = s.Evaluator.Evaluate(prior, s.Rule, stale, 2, s.StreamId, []);
    Check(result.Disposition == MonitoringEvaluationDisposition.DuplicateOrStaleCursor && result.State == prior);
    s.Epoch = 3; s.Sequence = 0; s.Observe(120, 95); Check(s.State.Occurrence!.OccurrenceId == occurrence && s.Raises == 1);
}
[Fact]
public void CursorFence()
{
    var s = new Scenario(); s.Fire(); var state = s.State; var sample = s.Observation(70, 95) with { Cursor = state.Cursor! };
    var result = s.Evaluator.Evaluate(state, s.Rule, sample, s.Epoch, s.StreamId, []); Check(result.State == state && result.Events.Length == 0);
    result = s.Evaluator.Evaluate(state, s.Rule, sample with { Cursor = new(s.Epoch, state.Cursor!.Sequence - 1) }, s.Epoch, s.StreamId, []);
    Check(result.State == state);
    result = s.Evaluator.Evaluate(state, s.Rule, sample with { Series = state.Series with { TenantId = 999 } }, s.Epoch, s.StreamId, []);
    Check(result.Disposition == MonitoringEvaluationDisposition.WrongSeries && result.State == state);
}
[Fact]
public void EvidenceStream()
{
    var pending = new Scenario(); pending.Observe(0, 95); pending.Observe(20, 95); var oldStream = pending.StreamId;
    pending.StreamId = Guid.NewGuid();
    var boundary = pending.Evaluator.BeginEvidenceStream(pending.State, pending.Rule, pending.StreamId); pending.Apply(boundary);
    Check(boundary.Events.Length == 0 && boundary.Outbox.Length == 0 && pending.State.WindowStartedAtUtc is null);
    pending.Observe(40, 95);
    Check(pending.Raises == 0 && pending.State.WindowStartedAtUtc == pending.Clock.At(40) && pending.State.EvidenceStreamId == pending.StreamId);
    var stale = pending.Observation(50, 95) with { EvidenceStreamId = oldStream };
    Check(pending.Evaluator.Evaluate(pending.State, pending.Rule, stale, pending.Epoch, pending.StreamId, []).State == pending.State);
    pending.Observe(60, 95); pending.Observe(80, 95); pending.Observe(100, 95);
    Check(pending.Raises == 1 && pending.State.Occurrence!.RaisedAtUtc == pending.Clock.At(100));
    var occurrence = pending.State.Occurrence!.OccurrenceId; var raisedEvent = pending.State.Occurrence.RaisedEventId;
    pending.Observe(120, 70); pending.Observe(140, 70); pending.StreamId = Guid.NewGuid();
    boundary = pending.Evaluator.BeginEvidenceStream(pending.State, pending.Rule, pending.StreamId); pending.Apply(boundary);
    Check(boundary.Events.Length == 0 && boundary.Outbox.Length == 0 && pending.State.Occurrence.OccurrenceId == occurrence);
    pending.Observe(145, 70);
    Check(pending.Resolves == 0 && pending.State.WindowStartedAtUtc == pending.Clock.At(145) && pending.State.Occurrence.OccurrenceId == occurrence);
    pending.Observe(165, 70); pending.Observe(175, 70);
    Check(pending.Resolves == 1 && pending.Raises == 1 && pending.State.Occurrence.OccurrenceId == occurrence && pending.State.Occurrence.RaisedEventId == raisedEvent);
}
[Fact]
public void DefinitionEvidenceFence()
{
    var s = new Scenario(); s.Clock.Set(100);
    s.ReplaceState(s.Evaluator.CreateInitial(s.State.Series, s.Rule));
    Check(s.State.NotBeforeObservedAtUtc == s.Clock.At(100) && s.State.NotBeforeReceivedAtUtc == s.Clock.At(100));
    s.Clock.Set(110); var cached = s.Observation(110, 95) with { ObservedAtUtc = s.Clock.At(99) };
    s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, cached, s.Epoch, s.StreamId, []));
    Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown && s.State.WindowStartedAtUtc is null);
    s.Fire(120); Check(s.Raises == 1 && s.State.Occurrence!.RaisedAtUtc == s.Clock.At(180));
    var updated = s.Rule with { Revision = 2, EvaluationRevision = 2, Condition = s.Rule.Condition with { BreachThreshold = 91 } };
    s.Clock.Set(190); s.Apply(s.Evaluator.ResetDefinition(s.State, updated, MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow, Guid.NewGuid(), "edited threshold"));
    s.Rule = updated; s.Clock.Set(200); cached = s.Observation(200, 95) with { ObservedAtUtc = s.Clock.At(189) };
    s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, cached, s.Epoch, s.StreamId, []));
    Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown && s.State.WindowStartedAtUtc is null && s.Raises == 1);
    s.Fire(210); Check(s.Raises == 2 && s.State.Occurrence!.RaisedAtUtc == s.Clock.At(270));
}
[Fact]
public void ClockValidation()
{
    var s = new Scenario(); s.Observe(0, 95);
    s.Clock.Set(30); s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, s.Observation(30, 95) with { ObservedAtUtc = s.Clock.At(1) }, s.Epoch, s.StreamId, []));
    Check(s.State.LatestEvidence!.UnknownReason == "stale" && s.State.WindowStartedAtUtc is null);
    s.Observe(40, 95);
    s.Clock.Set(50); s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, s.Observation(50, 70) with { ObservedAtUtc = s.Clock.At(51) }, s.Epoch, s.StreamId, []));
    Check(s.State.LatestEvidence!.UnknownReason == "future_clock");
    s.Clock.Set(60); s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, s.Observation(60, 70) with { ReceivedAtUtc = s.Clock.At(61) }, s.Epoch, s.StreamId, []));
    Check(s.State.LatestEvidence!.UnknownReason == "future_clock");
    s.Clock.Set(70); s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, s.Observation(70, 70) with { ObservedAtUtc = s.Clock.At(51) }, s.Epoch, s.StreamId, []));
    Check(s.State.LatestEvidence!.UnknownReason == "reordered_clock");
}
[Fact]
public void Clear()
{
    var s = new Scenario(action: true); s.Fire(); var first = s.State.Occurrence!.OccurrenceId;
    s.Clock.Set(70); Throws<ArgumentException>(() => s.Evaluator.Clear(s.State, s.Rule, Guid.NewGuid(), " "));
    s.Apply(s.Evaluator.Clear(s.State, s.Rule, Guid.NewGuid(), "operator validated maintenance"));
    Check(s.State.Phase == MonitoringPhase.Cleared && s.State.Occurrence!.ClosureDisposition == MonitoringClosureDisposition.ManuallyCleared && s.Audits.Count == 1);
    var cleared = s.State; var duplicate = s.Observation(80, 95) with { Cursor = cleared.Cursor! };
    Check(s.Evaluator.Evaluate(cleared, s.Rule, duplicate, s.Epoch, s.StreamId, []).State == cleared);
    s.Clock.Set(80); s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, s.Observation(80, 95) with { ObservedAtUtc = s.Clock.At(65) }, s.Epoch, s.StreamId, []));
    Check(s.State.LatestEvidence!.UnknownReason == "before_evaluation_fence" && s.Raises == 1);
    s.Fire(90); Check(s.Raises == 2 && s.State.Occurrence!.OccurrenceId != first && s.State.Occurrence.RaisedAtUtc == s.Clock.At(150));
}
[Fact]
public void AckAndActionEdit()
{
    var s = new Scenario(action: true); s.Fire(); var episode = s.State.Occurrence!; var actor = Guid.NewGuid();
    s.Clock.Set(70); s.Apply(s.Evaluator.Acknowledge(s.State, s.Rule, actor));
    s.Rule = s.Rule with { Revision = 2, Name = "renamed", PublishedFlowVersionId = Guid.NewGuid() };
    s.Observe(80, 95);
    Check(s.Raises == 1 && s.Outbox.Count == 1 && s.State.Occurrence!.AcknowledgedBy == actor);
    Check(s.State.Occurrence!.PinnedRule == episode.PinnedRule && s.State.Occurrence.RaisedEventId == episode.RaisedEventId);
    s.Recover(100); s.Fire(150); Check(s.State.Occurrence!.PinnedRule.PublishedFlowVersionId == s.Rule.PublishedFlowVersionId && s.Outbox.Count == 2);
}
[Fact]
public void DisplayOnly()
{
    var s = new Scenario(); s.Fire(); Check(s.Outbox.Count == 0);
    s.Rule = s.Rule with { Revision = 2, PublishedFlowVersionId = Guid.NewGuid(), ExecutionPrincipalId = "configuring-operator" };
    s.Observe(80, 95); Check(s.Outbox.Count == 0 && s.State.Occurrence!.FlowDispatchDisposition == MonitoringFlowDispatchDisposition.NoFlowSelected);
}
[Fact]
public void SilenceExpiry()
{
    var s = new Scenario(action: true); s.Bypasses = [s.Bypass(80)]; s.Fire(); Check(s.Raises == 1 && s.Outbox.Count == 0);
    s.Clock.Set(80); s.Apply(s.Evaluator.Refresh(s.State, s.Rule, s.Bypasses, s.StreamId)); Check(s.Outbox.Count == 1);
    var intent = s.Outbox.Single(); var state = s.State;
    s.Apply(s.Evaluator.Refresh(s.State, s.Rule, s.Bypasses, s.StreamId)); Check(s.Outbox.Count == 1 && s.State == state);
    s.Apply(s.Evaluator.RecordFlowOutcome(s.State, intent.OccurrenceId, intent.EventId, new(Guid.NewGuid(), MonitoringFlowOutcomeKind.Succeeded, s.Clock.GetUtcNow())));
    s.Observe(90, 95); Check(s.Outbox.Count == 1);
}
[Fact]
public void NoBacklog()
{
    var s = new Scenario(action: true); s.Bypasses = [s.Bypass(205)]; s.Fire(); var old = s.State.Occurrence!.OccurrenceId;
    s.Recover(80); s.Fire(130); var current = s.State.Occurrence!.OccurrenceId;
    Check(s.Raises == 2 && s.Outbox.Count == 0 && old != current);
    s.Clock.Set(205); s.Apply(s.Evaluator.Refresh(s.State, s.Rule, s.Bypasses, s.StreamId));
    Check(s.Outbox.Count == 1 && s.Outbox[0].OccurrenceId == current);
}
[Fact]
public void OverlappingSilences()
{
    var s = new Scenario(action: true); s.Bypasses = [s.Bypass(80), s.Bypass(100)]; s.Fire();
    s.Observe(80, 95); Check(s.State.Suppressed && s.Outbox.Count == 0);
    s.Clock.Set(100); s.Apply(s.Evaluator.Refresh(s.State, s.Rule, s.Bypasses, s.StreamId)); Check(s.Outbox.Count == 1);
}
[Fact]
public void StaleSilence()
{
    var s = new Scenario(action: true); s.Bypasses = [s.Bypass(90)]; s.Fire(); s.Clock.Set(90);
    s.Apply(s.Evaluator.Refresh(s.State, s.Rule, s.Bypasses, s.StreamId)); Check(s.Outbox.Count == 0 && s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown);
    s.Observe(100, 95); Check(s.Outbox.Count == 1 && s.Raises == 1);
    var recovering = new Scenario(action: true); recovering.Bypasses = [recovering.Bypass(90)]; recovering.Fire(); recovering.Observe(80, 70);
    recovering.Clock.Set(90); recovering.Apply(recovering.Evaluator.Refresh(recovering.State, recovering.Rule, recovering.Bypasses, recovering.StreamId)); Check(recovering.Outbox.Count == 0);
    recovering.Observe(100, 70); recovering.Observe(110, 70); Check(recovering.Resolves == 1 && recovering.Outbox.Count == 0);
}
[Fact]
public void DispatchRecheck()
{
    var s = new Scenario(action: true); s.Fire(); var intent = s.Outbox.Single();
    Check(s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, []));
    Check(!s.Evaluator.CanDispatch(s.State, s.Rule, intent, Guid.NewGuid(), []));
    Check(!s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, [s.Bypass(100)]));
    s.Clock.Set(70); s.Apply(s.Evaluator.Clear(s.State, s.Rule, Guid.NewGuid(), "stop unstarted delivery"));
    Check(!s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, []));
    s.Apply(s.Evaluator.RecordFlowOutcome(s.State, intent.OccurrenceId, intent.EventId, new(Guid.NewGuid(), MonitoringFlowOutcomeKind.Succeeded, s.Clock.GetUtcNow())));
    Check(s.State.Occurrence!.FlowDispatchDisposition == MonitoringFlowDispatchDisposition.Completed);
    Throws<InvalidOperationException>(() => s.Evaluator.RecordFlowOutcome(s.State, intent.OccurrenceId, intent.EventId, new(Guid.NewGuid(), MonitoringFlowOutcomeKind.Failed, s.Clock.GetUtcNow(), "permanent_error")));
}
[Fact]
public void DisableAndRemove()
{
    var s = new Scenario(action: true); s.Fire(); s.Clock.Set(70); s.Rule = s.Rule with { Enabled = false, Revision = 2 };
    s.Apply(s.Evaluator.SetApplicability(s.State, s.Rule, true, Guid.NewGuid(), "planned disable"));
    Check(s.State.Phase == MonitoringPhase.Suspended && s.Resolves == 0 && s.Events.Last().Kind == MonitoringEventKind.AlertSuspended);
    s.Rule = s.Rule with { Enabled = true, Revision = 3 }; s.Clock.Set(80);
    s.Apply(s.Evaluator.SetApplicability(s.State, s.Rule, true, Guid.NewGuid(), "resume")); s.Fire(90); Check(s.Raises == 2);
    s.Clock.Set(160); s.Apply(s.Evaluator.SetApplicability(s.State, s.Rule, false, Guid.NewGuid(), "removed from group"));
    Check(s.State.Phase == MonitoringPhase.NotApplicable && s.State.Occurrence!.ClosureDisposition == MonitoringClosureDisposition.TargetRemoved && s.Resolves == 0);
}
[Fact]
public void DefinitionReset()
{
    var s = new Scenario(action: true); s.Fire(); var updated = s.Rule with { Revision = 2, EvaluationRevision = 2, Condition = s.Rule.Condition with { BreachThreshold = 92 } };
    Throws<InvalidOperationException>(() => s.Evaluator.Evaluate(s.State, updated with { EvaluationRevision = s.Rule.EvaluationRevision }, s.Observation(70, 95), s.Epoch, s.StreamId, []));
    Throws<InvalidOperationException>(() => s.Evaluator.Evaluate(s.State, updated, s.Observation(70, 95), s.Epoch, s.StreamId, []));
    s.Clock.Set(70); s.Apply(s.Evaluator.ResetDefinition(s.State, updated, MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow, Guid.NewGuid(), "changed threshold"));
    s.Rule = updated; Check(s.Resolves == 0 && s.Events.Last().Kind == MonitoringEventKind.AlertSuspended && s.State.EvaluationRevision == 2);
    s.Fire(80); Check(s.Raises == 2);
    var moved = s.Rule with { Revision = 3, EvaluationRevision = 3, Condition = new(MonitoringMetricKind.DiskFreePercent, MonitoringNumericUnit.Percent, 10, 15, "/", null, []) };
    s.Clock.Set(150); s.Apply(s.Evaluator.ResetDefinition(s.State, moved, MonitoringConditionResetPolicy.SuspendOccurrenceAndRequireNewWindow, Guid.NewGuid(), "different metric scope"));
    Check(s.State.Phase == MonitoringPhase.NotApplicable && s.State.Occurrence!.ClosureDisposition == MonitoringClosureDisposition.ConfigurationChanged);
}
[Fact]
public void IndependentDisk()
{
    var condition = new MonitoringConditionDto(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.GiB, 5, 8, null, null, []);
    var c = new Scenario(condition: condition, resource: MonitoringSeriesEvaluator.DiskResourceKey("c:/")); var d = new Scenario(condition: condition, resource: MonitoringSeriesEvaluator.DiskResourceKey("D:/"));
    foreach (var at in new[] { 0, 20, 40, 60 }) { c.Observe(at, 4 * Math.Pow(1024, 3)); d.Observe(at, 10 * Math.Pow(1024, 3)); }
    Check(c.Raises == 1 && d.Raises == 0 && c.State.Series.ResourceKey != d.State.Series.ResourceKey);
    Check(MonitoringContractValidator.ToCanonicalBytes(1, MonitoringNumericUnit.GiB) == 1073741824d);
    Check(MonitoringSeriesEvaluator.DiskResourceKey("c:/") == MonitoringSeriesEvaluator.DiskResourceKey("C:\\"));
    Check(MonitoringSeriesEvaluator.DiskResourceKey("/mount/volume/") == "disk:/mount/volume");
    Check(MonitoringContractValidator.EvaluationFingerprint(c.Rule) == MonitoringContractValidator.EvaluationFingerprint(c.Rule with
        { Condition = c.Rule.Condition with { Unit = MonitoringNumericUnit.MiB, BreachThreshold = 5120, RecoveryThreshold = 8192 } }));
}
[Fact]
public void DiskPrecision()
{
    var condition = new MonitoringConditionDto(MonitoringMetricKind.DiskFreeSpace, MonitoringNumericUnit.GiB, 5, 8, null, null, []);
    var s = new Scenario(condition: condition, resource: "disk:/");
    var rounded = s.Observation(0, 4.99 * Math.Pow(1024, 3)) with { NumericResolution = 0.1 * Math.Pow(1024, 3) };
    s.Apply(s.Evaluator.Evaluate(s.State, s.Rule, rounded, s.Epoch, s.StreamId, []));
    Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown && s.State.LatestEvidence!.UnknownReason == "numeric_uncertainty");
    foreach (var at in new[] { 20, 40, 60, 80 }) s.Observe(at, 4.99 * Math.Pow(1024, 3));
    Check(s.Raises == 1);
}
[Fact]
public void ServiceEvidence()
{
    var condition = new MonitoringConditionDto(MonitoringMetricKind.ServiceExpectedState, null, null, null, "Spooler", ClientServicePlatform.Windows, [ClientServiceState.Running]);
    var s = new Scenario(condition: condition, resource: MonitoringSeriesEvaluator.ServiceResourceKey("spooler", ClientServicePlatform.Windows));
    Check(MonitoringContractValidator.EvaluationFingerprint(s.Rule) == MonitoringContractValidator.EvaluationFingerprint(s.Rule with
        { Condition = s.Rule.Condition with { ResourceName = "spooler" } }));
    s.Service(0, ClientServiceState.Missing, authoritative: false); Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown);
    s.Service(20, ClientServiceState.Missing, authoritative: true, complete: false); Check(s.Raises == 0 && s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown);
    s.Service(40, ClientServiceState.Stopped, watch: 1, currentWatch: 2); Check(s.State.LatestEvidence!.UnknownReason == "stale_watch_policy");
    foreach (var at in new[] { 60, 80, 100, 120 }) s.Service(at, ClientServiceState.Missing, authoritative: true);
    Check(s.Raises == 1 && s.State.LatestEvidence!.ServiceState == ClientServiceState.Missing);
    s.Service(140, ClientServiceState.Unsupported); Check(s.Resolves == 0);
    s.Service(160, (ClientServiceState)999); Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown);
}
[Fact]
public void ExpectedStopped()
{
    var condition = new MonitoringConditionDto(MonitoringMetricKind.ServiceExpectedState, null, null, null, "oneshot.service", ClientServicePlatform.LinuxSystemd, [ClientServiceState.Stopped, ClientServiceState.Running]);
    var s = new Scenario(condition: condition, resource: MonitoringSeriesEvaluator.ServiceResourceKey("oneshot.service", ClientServicePlatform.LinuxSystemd));
    foreach (var at in new[] { 0, 20, 40, 60 }) s.Service(at, ClientServiceState.Stopped);
    Check(s.Raises == 0 && s.State.Phase == MonitoringPhase.Healthy);
    Check(MonitoringSeriesEvaluator.ServiceResourceKey("Foo.service", ClientServicePlatform.LinuxSystemd) != MonitoringSeriesEvaluator.ServiceResourceKey("foo.service", ClientServicePlatform.LinuxSystemd));
}
[Fact]
public void InvalidNumbers()
{
    foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1d, 101d })
    {
        var s = new Scenario(); s.Fire(); s.Observe(80, invalid);
        Check(s.State.EvidenceQuality == MonitoringEvidenceQuality.Unknown && s.Resolves == 0 && s.State.Phase == MonitoringPhase.Firing);
    }
}
[Fact]
public void Targets()
{
    var a = Guid.NewGuid(); var b = Guid.NewGuid(); var foreign = Guid.NewGuid(); var g1 = Guid.NewGuid(); var g2 = Guid.NewGuid();
    var agents = new Dictionary<Guid, int> { [a] = 1, [b] = 1, [foreign] = 2 };
    var groups = new Dictionary<Guid, MonitoringGroupDto> { [g1] = new(1, g1, 1, "one", [a, b]), [g2] = new(1, g2, 1, "two", [b]) };
    var authorized = new HashSet<Guid> { a, b };
    var resolved = MonitoringTargetResolver.Resolve(1, new(MonitoringTargetMode.Selected, [a], [g1, g2]), agents, groups, authorized);
    Check(resolved.Length == 2);
    Throws<ArgumentException>(() => MonitoringTargetResolver.Resolve(1, new(MonitoringTargetMode.Selected, [foreign], []), agents, groups, authorized));
    groups[g2] = groups[g2] with { TenantId = 2 };
    Throws<ArgumentException>(() => MonitoringTargetResolver.Resolve(1, new(MonitoringTargetMode.Selected, [], [g2]), agents, groups, authorized));
    Throws<UnauthorizedAccessException>(() => MonitoringTargetResolver.Resolve(1, new(MonitoringTargetMode.Selected, [b], []), agents, groups, new HashSet<Guid> { a }));
}
[Fact]
public void FutureTargets()
{
    var a = Guid.NewGuid(); var b = Guid.NewGuid(); var agents = new Dictionary<Guid, int> { [a] = 1 }; var authority = new HashSet<Guid> { a, b };
    var selector = new MonitoringTargetSelectionDto(MonitoringTargetMode.AllEligible, [], []);
    Check(MonitoringTargetResolver.Resolve(1, selector, agents, new Dictionary<Guid, MonitoringGroupDto>(), authority).Length == 1);
    agents[b] = 1; Check(MonitoringTargetResolver.Resolve(1, selector, agents, new Dictionary<Guid, MonitoringGroupDto>(), authority).Length == 2);
}
[Fact]
public void DefinitionBounds()
{
    var rule = new Scenario().Rule;
    foreach (var invalid in new[] { rule with { Name = "" }, rule with { BreachHold = TimeSpan.Zero }, rule with { FreshnessBudget = TimeSpan.FromHours(2) },
        rule with { Condition = rule.Condition with { BreachThreshold = double.NaN } }, rule with { Condition = rule.Condition with { RecoveryThreshold = 99 } },
        rule with { PublishedFlowVersionId = Guid.Empty }, rule with { Targets = new(MonitoringTargetMode.Selected, [], []) } })
        Check(!MonitoringContractValidator.TryValidateRule(invalid, out _));
    var agents = Enumerable.Range(0, 4097).Select(_ => Guid.NewGuid()).ToDictionary(id => id, _ => 1);
    Throws<ArgumentException>(() => MonitoringTargetResolver.Resolve(1, new(MonitoringTargetMode.AllEligible, [], []), agents,
        new Dictionary<Guid, MonitoringGroupDto>(), agents.Keys.ToHashSet()));
}
[Fact]
public void CommitIntent()
{
    var s = new Scenario(action: true); s.Observe(0, 95); s.Observe(20, 95); s.Observe(40, 95); var before = s.State;
    var result = s.Step(60, 95); Check(result.ExpectedStateRevision == before.StateRevision && result.State.StateRevision == before.StateRevision + 1);
    Check(result.Events.Length == 1 && result.Outbox.Length == 1 && result.Events[0].EventId == result.Outbox[0].EventId);
    Check(result.Outbox[0].PinnedRule == result.Events[0].PinnedRule && result.Outbox[0].PinnedEvidence == result.Events[0].Evidence);
    Check(before.Occurrence is null && before.Phase == MonitoringPhase.Pending);
}
[Fact]
public void BypassAudit()
{
    var s = new Scenario(action: true); s.Fire(); var episode = s.State.Occurrence!.OccurrenceId; var bypass = s.Bypass(200);
    var result = s.Evaluator.AuditBypass(s.State, s.Rule, bypass, false); s.Apply(result);
    Check(s.Audits.Single().Operation == "create_bypass" && s.State.Occurrence!.OccurrenceId == episode && s.State.Phase == MonitoringPhase.Firing);
    s.Bypasses = [bypass]; s.Observe(80, 70); Check(s.State.Suppressed && s.State.Phase == MonitoringPhase.Recovering);
    s.Observe(100, 70); s.Observe(110, 70); Check(s.Resolves == 1 && s.State.Suppressed);
    Throws<ArgumentException>(() => s.Evaluator.AuditBypass(s.State, s.Rule, bypass with { TenantId = 2 }, false));
}
[Fact]
public void FlowOutcomes()
{
    foreach (var kind in new[] { MonitoringFlowOutcomeKind.Skipped, MonitoringFlowOutcomeKind.Failed, MonitoringFlowOutcomeKind.Succeeded })
    {
        var s = new Scenario(action: true); s.Fire(); var occurrence = s.State.Occurrence!;
        var outcome = new MonitoringFlowOutcomeDto(Guid.NewGuid(), kind, s.Clock.GetUtcNow(), kind == MonitoringFlowOutcomeKind.Failed ? "permanent_error" : kind == MonitoringFlowOutcomeKind.Skipped ? "condition_false" : null);
        s.Apply(s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, outcome));
        Check(s.State.Occurrence!.FlowOutcome == outcome && s.State.Occurrence.FlowDispatchDisposition == (kind == MonitoringFlowOutcomeKind.Failed ? MonitoringFlowDispatchDisposition.Failed : MonitoringFlowDispatchDisposition.Completed));
        s.Observe(80, 95); Check(s.Outbox.Count == 1 && s.Raises == 1);
        Throws<InvalidOperationException>(() => s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, outcome with { Outcome = MonitoringFlowOutcomeKind.DeliveryUnknown }));
    }
}

[Fact]
public void GroupBypassUsesActualTenantMembership()
{
    var selected = new Scenario(action: true);
    var groupId = Guid.NewGuid();
    var bypass = selected.Bypass(100) with { AgentId = null, GroupId = groupId };
    selected.Bypasses = [bypass];
    selected.Groups = [new(1, groupId, 1, "maintenance", [selected.State.Series.AgentId])];
    selected.Fire();
    Assert.True(selected.State.Suppressed);
    Assert.Empty(selected.Outbox);
    selected.Clock.Set(101);
    var expired = selected.Evaluator.Refresh(selected.State, selected.Rule, selected.Bypasses, selected.StreamId, selected.Groups);
    Assert.Empty(expired.Outbox); // stale samples cannot release a silenced occurrence.

    var other = new Scenario(action: true);
    other.Bypasses = [bypass with { RuleId = other.Rule.RuleId, ResourceKey = other.State.Series.ResourceKey }];
    other.Groups = selected.Groups;
    other.Fire();
    Assert.False(other.State.Suppressed);
    Assert.Single(other.Outbox);
}
[Fact]
public void GroupBypassRequiresCurrentUnambiguousTenantMembership()
{
    var s = new Scenario(action: true);
    var group = new MonitoringGroupDto(1, Guid.NewGuid(), 1, "group", [s.State.Series.AgentId]);
    var bypass = s.Bypass(200) with { GroupId = group.GroupId };
    s.Fire();
    var intent = Assert.Single(s.Outbox);
    Assert.Throws<ArgumentException>(() => s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, [bypass]));
    Assert.Throws<ArgumentException>(() => s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, [bypass], [group with { TenantId = 2 }]));
    Assert.Throws<ArgumentException>(() => s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, [bypass], [group, group]));
    Assert.False(s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, [bypass], [group]));
}
[Fact]
public void FlowAuthorityIsRequiredAndPinnedAtOccurrence()
{
    var s = new Scenario(action: true);
    Assert.False(MonitoringContractValidator.TryValidateRule(s.Rule with { ExecutionPrincipalId = null }, out _));
    Assert.False(MonitoringContractValidator.TryValidateRule(s.Rule with { PublishedFlowVersionId = null }, out _));
    s.Rule = s.Rule with { ExecutionPrincipalId = "operator-a", ExecutionCredentialId = "credential-a" };
    s.Fire();
    var intent = Assert.Single(s.Outbox);
    s.Rule = s.Rule with { Revision = 2, PublishedFlowVersionId = Guid.NewGuid(), ExecutionPrincipalId = "operator-b", ExecutionCredentialId = null };
    s.Observe(80, 95);
    Assert.Equal("operator-a", s.State.Occurrence!.PinnedRule.ExecutionPrincipalId);
    Assert.Equal("credential-a", intent.PinnedRule.ExecutionCredentialId);
    Assert.True(s.Evaluator.CanDispatch(s.State, s.Rule, intent, s.StreamId, []));
    Assert.False(s.Evaluator.CanDispatch(s.State, s.Rule, intent with { PinnedRule = s.Rule }, s.StreamId, []));
    s.Recover(100); s.Fire(150);
    Assert.Equal("operator-b", s.State.Occurrence!.PinnedRule.ExecutionPrincipalId);
}
[Fact]
public void IndependentlyPersistedStateAndOutboxKeepStructuralPinnedIdentity()
{
    var s = new Scenario(action: true);
    s.Rule = s.Rule with { Targets = new(MonitoringTargetMode.Selected, [s.State.Series.AgentId], []) };
    s.ReplaceState(s.Evaluator.CreateInitial(s.State.Series, s.Rule) with
        { NotBeforeObservedAtUtc = s.Clock.At(-1), NotBeforeReceivedAtUtc = s.Clock.At(-1) });
    s.Fire();
    var state = JsonSerializer.Deserialize<MonitoringSeriesState>(JsonSerializer.Serialize(s.State))!;
    var intent = JsonSerializer.Deserialize<MonitoringOutboxIntent>(JsonSerializer.Serialize(Assert.Single(s.Outbox)))!;
    Assert.True(s.Evaluator.CanDispatch(state, s.Rule, intent, s.StreamId, []));
    Assert.False(s.Evaluator.CanDispatch(state, s.Rule, intent with
        { PinnedRule = intent.PinnedRule with { ExecutionPrincipalId = "different-operator" } }, s.StreamId, []));
    Assert.False(s.Evaluator.CanDispatch(state, s.Rule, intent with
        { PinnedRule = intent.PinnedRule with { Targets = new(MonitoringTargetMode.AllEligible, [], []) } }, s.StreamId, []));
}
[Fact]
public void AuthoritativeTargetRevocationSuspendsWithoutFabricatedHumanAudit()
{
    var s = new Scenario(action: true); s.Fire(); var occurrence = s.State.Occurrence!.OccurrenceId;
    s.Clock.Set(70);
    var removed = s.Evaluator.SetAuthoritativeApplicability(s.State, s.Rule, false);
    Assert.Equal(MonitoringPhase.NotApplicable, removed.State.Phase);
    Assert.Equal(MonitoringClosureDisposition.TargetRemoved, Assert.Single(removed.Events).ClosureDisposition);
    Assert.Equal(occurrence, removed.State.Occurrence!.OccurrenceId);
    Assert.Empty(removed.Audits);
    Assert.Empty(s.Evaluator.SetAuthoritativeApplicability(removed.State, s.Rule, false).Events);
    s.Clock.Set(80);
    var resumed = s.Evaluator.SetAuthoritativeApplicability(removed.State, s.Rule, true);
    Assert.Equal(MonitoringPhase.Healthy, resumed.State.Phase);
    Assert.Equal(s.Clock.At(80), resumed.State.NotBeforeObservedAtUtc);
    Assert.Empty(resumed.Events);
    Assert.Empty(resumed.Audits);
}
[Fact]
public void PreRunFailuresNeedSafeCodeAndUnknownReceiptCanAcquireVerifiedRunIdentity()
{
    var s = new Scenario(action: true); s.Fire(); var occurrence = s.State.Occurrence!;
    foreach (var invalid in new[] {
        new MonitoringFlowOutcomeDto(null, MonitoringFlowOutcomeKind.Succeeded, s.Clock.GetUtcNow(), "missing_run"),
        new MonitoringFlowOutcomeDto(null, MonitoringFlowOutcomeKind.Skipped, s.Clock.GetUtcNow(), "missing_run"),
        new MonitoringFlowOutcomeDto(null, MonitoringFlowOutcomeKind.Failed, s.Clock.GetUtcNow()),
        new MonitoringFlowOutcomeDto(Guid.Empty, MonitoringFlowOutcomeKind.Failed, s.Clock.GetUtcNow(), "missing_run") })
        Assert.Throws<ArgumentException>(() => s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, invalid));
    s.Apply(s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId,
        new(null, MonitoringFlowOutcomeKind.DeliveryUnknown, s.Clock.GetUtcNow(), "ingress_unconfirmed")));
    var actual = new MonitoringFlowOutcomeDto(Guid.NewGuid(), MonitoringFlowOutcomeKind.Failed, s.Clock.GetUtcNow(), "flow_disabled");
    s.Apply(s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, actual));
    Assert.Equal(occurrence.OccurrenceId, s.State.Occurrence!.OccurrenceId);
    Assert.Equal(actual, s.State.Occurrence.FlowOutcome);
    var failed = new Scenario(action: true); failed.Fire(); var failedOccurrence = failed.State.Occurrence!;
    failed.Apply(failed.Evaluator.RecordFlowOutcome(failed.State, failedOccurrence.OccurrenceId, failedOccurrence.RaisedEventId,
        new(null, MonitoringFlowOutcomeKind.Failed, failed.Clock.GetUtcNow(), "missing_version")));
    Assert.Equal(MonitoringFlowDispatchDisposition.Failed, failed.State.Occurrence!.FlowDispatchDisposition);
}
[Fact]
public void OnlyVerifiedSuccessfulOutcomeMayCarryBoundedSafeIncidentReceipt()
{
    var s = new Scenario(action: true); s.Fire(); var occurrence = s.State.Occurrence!;
    var receipt = new MonitoringIncidentReceiptDto("incident-123", "NR-123", "https://desk.example/incidents/incident-123");
    var success = new MonitoringFlowOutcomeDto(Guid.NewGuid(), MonitoringFlowOutcomeKind.Succeeded, s.Clock.GetUtcNow(), Receipt: receipt);
    foreach (var invalid in new[] {
        success with { Outcome = MonitoringFlowOutcomeKind.Failed },
        success with { Outcome = MonitoringFlowOutcomeKind.DeliveryUnknown },
        success with { Receipt = receipt with { IncidentId = new string('x', 257) } },
        success with { Receipt = receipt with { TrackingNumber = new string('x', 257) } },
        success with { Receipt = receipt with { IncidentUrl = "http://desk.example/incidents/1" } },
        success with { Receipt = receipt with { IncidentUrl = "https://user:secret@desk.example/incidents/1" } },
        success with { Receipt = receipt with { IncidentUrl = "javascript:alert(1)" } },
        success with { Receipt = receipt with { IncidentUrl = "https://desk.example/" + new string('x', 2048) } } })
        Assert.Throws<ArgumentException>(() => s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, invalid));
    s.Apply(s.Evaluator.RecordFlowOutcome(s.State, occurrence.OccurrenceId, occurrence.RaisedEventId, success));
    Assert.Equal(receipt, s.State.Occurrence!.FlowOutcome!.Receipt);
}
[Fact]
public void SelectedServiceEvidenceRequiresKnownNonzeroCurrentWatchRevision()
{
    var s = new Scenario(condition: new(MonitoringMetricKind.ServiceExpectedState, null, null, null, "Spooler",
        ClientServicePlatform.Windows, [ClientServiceState.Running]), resource: MonitoringSeriesEvaluator.ServiceResourceKey("Spooler", ClientServicePlatform.Windows));
    var observation = s.Observation(0, null) with { ServiceState = ClientServiceState.Missing, AuthoritativeMissing = true };
    var missing = s.Evaluator.Evaluate(s.State, s.Rule, observation, s.Epoch, s.StreamId, []);
    Assert.Equal("stale_watch_policy", missing.State.LatestEvidence!.UnknownReason);
    var zero = s.Evaluator.Evaluate(s.State, s.Rule, observation with { ServiceWatchPolicyRevision = 0, CurrentServiceWatchPolicyRevision = 0 }, s.Epoch, s.StreamId, []);
    Assert.Equal("stale_watch_policy", zero.State.LatestEvidence!.UnknownReason);
}
[Fact]
public async Task UnavailablePublishedFlowProviderRejectsSelectionsAndListsNothing()
{
    var provider = new UnavailableMonitoringPublishedFlowProvider();
    Assert.False(await provider.IsPublishedAsync(1, Guid.NewGuid(), CancellationToken.None));
    Assert.Empty(await provider.ListPublishedAsync(1, 10, CancellationToken.None));
    await Assert.ThrowsAsync<ArgumentException>(() => provider.ListPublishedAsync(1, 201, CancellationToken.None));
    await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.ListPublishedAsync(1, 10, new CancellationToken(true)));
}

static void Check(bool predicate, string? message = null) => Assert.True(predicate, message);
static void Throws<T>(Action action) where T : Exception
{
    Assert.Throws<T>(action);
}

sealed class ManualClock : TimeProvider
{
    private readonly DateTimeOffset _start = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    private DateTimeOffset _now = new(2026, 10, 2, 10, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public DateTimeOffset At(int seconds) => _start.AddSeconds(seconds);
    public void Set(int seconds) => _now = At(seconds);
}
sealed class Scenario
{
    public ManualClock Clock { get; } = new();
    public MonitoringSeriesEvaluator Evaluator { get; }
    public MonitoringRuleDto Rule { get; set; }
    public MonitoringSeriesState State { get; private set; }
    public long Epoch { get; set; } = 1;
    public Guid StreamId { get; set; } = Guid.NewGuid();
    public ulong Sequence { get; set; }
    public IReadOnlyList<MonitoringBypassDto> Bypasses { get; set; } = [];
    public IReadOnlyList<MonitoringGroupDto> Groups { get; set; } = [];
    public List<MonitoringEventIntent> Events { get; } = [];
    public List<MonitoringOutboxIntent> Outbox { get; } = [];
    public List<MonitoringAuditIntent> Audits { get; } = [];
    public int Raises => Events.Count(item => item.Kind == MonitoringEventKind.AlertRaised);
    public int Resolves => Events.Count(item => item.Kind == MonitoringEventKind.AlertResolved);
    public Scenario(bool action = false, MonitoringConditionDto? condition = null, string resource = "cpu")
    {
        Evaluator = new(Clock);
        Rule = new(1, Guid.NewGuid(), 1, 1, "rule", true, MonitoringSeverity.Warning, new(MonitoringTargetMode.AllEligible, [], []),
            condition ?? new(MonitoringMetricKind.CpuUsagePercent, MonitoringNumericUnit.Percent, 90, 80, null, null, []),
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(20), action ? Guid.NewGuid() : null, action ? "configuring-operator" : null);
        Clock.Set(-1);
        State = Evaluator.CreateInitial(new(1, Rule.RuleId, Guid.NewGuid(), resource), Rule);
        Clock.Set(0);
    }
    public MonitoringObservationDto Observation(int at, double? value)
    {
        var observation = new MonitoringObservationDto(State.Series, new(Epoch, ++Sequence), StreamId, Clock.At(at), Clock.At(at), true, true, value);
        return Rule.Condition.Kind is MonitoringMetricKind.DiskFreePercent or MonitoringMetricKind.DiskFreeSpace
            ? observation with { DiskCollection = new(Guid.NewGuid(), Clock.At(at), new string('A', 64)) }
            : observation;
    }
    public MonitoringEvaluationResult Step(int at, double value, bool complete = true, bool supported = true)
    {
        Clock.Set(at); var observation = Observation(at, value) with { Complete = complete, Supported = supported };
        var result = Evaluator.Evaluate(State, Rule, observation, Epoch, StreamId, Bypasses, groups: Groups); Apply(result); return result;
    }
    public void Observe(int at, double value, bool complete = true, bool supported = true) => Step(at, value, complete, supported);
    public void Service(int at, ClientServiceState state, bool authoritative = false, bool complete = true, ulong watch = 1, ulong currentWatch = 1)
    {
        Clock.Set(at); var observation = Observation(at, null) with { ServiceState = state, AuthoritativeMissing = authoritative, Complete = complete,
            ServiceWatchPolicyRevision = watch, CurrentServiceWatchPolicyRevision = currentWatch };
        Apply(Evaluator.Evaluate(State, Rule, observation, Epoch, StreamId, Bypasses, groups: Groups));
    }
    public void Fire(int start = 0) { foreach (var at in new[] { start, start + 20, start + 40, start + 60 }) Observe(at, 95); }
    public void Recover(int start) { foreach (var at in new[] { start, start + 20, start + 30 }) Observe(at, 70); }
    public void Apply(MonitoringEvaluationResult result) { State = result.State; Events.AddRange(result.Events); Outbox.AddRange(result.Outbox); Audits.AddRange(result.Audits); }
    public void ReplaceState(MonitoringSeriesState state) => State = state;
    public MonitoringBypassDto Bypass(int expires) => new(Guid.NewGuid(), Rule.TenantId, Rule.RuleId, State.Series.AgentId, State.Series.ResourceKey,
        Guid.NewGuid(), "maintenance", Clock.At(-1), Clock.At(expires));
}

}
