using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Orchestration;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Tests.Infrastructure;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowExecutionLifecycleOutboxTests
{
    [Theory]
    [InlineData("Success", FlowExecutionOutcome.Succeeded)]
    [InlineData("Completed", FlowExecutionOutcome.Succeeded)]
    [InlineData("Failed", FlowExecutionOutcome.Failed)]
    [InlineData("TimedOut", FlowExecutionOutcome.Unknown)]
    [InlineData("Unknown", FlowExecutionOutcome.Unknown)]
    public async Task Request_only_terminal_transition_is_durable_without_a_job(string status, FlowExecutionOutcome expected)
    {
        await using var db = Memory();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var row = await requests.CreateAsync(new("synthetic-source", string.Empty, "missing-definition", null, 7, null));
        await requests.UpdateAsync(new(row.Id, null, null, null, null, status, "synthetic result", null, null, null));
        var saved = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        var value = Read(saved);
        Assert.Equal(FlowExecutionSourceKind.Request, value.SourceKind);
        Assert.Equal(expected, value.Outcome);
        Assert.Equal(status, value.RawStatus);
        Assert.Equal("synthetic-source", value.RequestKind);
        Assert.Equal("request:" + row.Id, value.CorrelationId);
        Assert.Equal(row.Id.ToString(), value.ExecutionId);
        Assert.Null(value.ParentExecutionId);
        Assert.False(value.Replay!.Supported);

        await requests.UpdateAsync(new(row.Id, null, null, null, null, status, "diagnostic edit", null, null, null));
        Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
    }

    [Fact]
    public async Task Script_terminal_fact_keeps_submission_identity_after_later_edits_and_has_no_inline_output()
    {
        await using var db = Memory();
        var now = DateTimeOffset.UtcNow;
        var activity = new JobTaskActivityRecord
        {
            Id = 20, RequestId = "synthetic-script-attempt", TenantId = 7, AgentId = Guid.NewGuid(),
            TaskType = TaskKinds.ExecLibraryScript, Status = "Pending", ScriptName = "Synthetic diagnosis",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64))),
            CreatedAtUtc = now, StartedAtUtc = now
        };
        db.JobTaskActivities.Add(activity);
        await db.SaveChangesAsync();
        activity.Status = "Completed";
        activity.CompletedAtUtc = now.AddSeconds(1);
        activity.ResultJson = "{\"stdout\":\"synthetic bounded output\"}";
        activity.Error = "Synthetic native script failure.";
        await db.SaveChangesAsync();
        var saved = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync());
        var value = Read(saved);
        Assert.Equal("91", value.Definition.Id);
        Assert.Equal("4", value.Definition.Revision);
        Assert.Equal("Synthetic diagnosis", value.Definition.Name);
        Assert.Equal(new string('A', 64), value.Definition.ContentHash);
        Assert.DoesNotContain("synthetic bounded output", saved.PayloadJson);
        Assert.Equal("Synthetic native script failure.", value.Reason);
        Assert.True(Assert.Single(value.Results!).Available);

        activity.ScriptName = "Renamed after execution";
        await db.SaveChangesAsync();
        Assert.Equal("Synthetic diagnosis", Read(Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync())).Definition.Name);

        var fact = await db.OutboxMessages.SingleAsync();
        fact.PayloadJson = "{}";
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_correlation_uses_the_accepted_parent_before_or_after_terminal_ready(bool terminalReady)
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        const long runId = 9007199254740993;
        var now = DateTimeOffset.UtcNow;
        db.JobRuns.Add(new()
        {
            Id = runId, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)(terminalReady ? JobRunState.Failed : JobRunState.Running), CreatedAtUtc = now
        });
        if (terminalReady) db.Set<JobRunControlRecord>().Add(new()
        { RunId = runId, TerminalReadyAtUtc = now, TerminalResultHash = new string('a', 64) });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var row = await requests.CreateAsync(new("synthetic-source", string.Empty, "41", null, 7, agent));
        await ExecutionLifecycleOutbox.RecordAcceptedRequestJobBindingAsync(db, row,
            new((ulong)runId, 41, 7, string.Empty, "synthetic-source",
                terminalReady ? JobRunState.Failed : JobRunState.Running, 0, now, null, null, null, null, null, agent), default);
        await requests.UpdateAsync(new(row.Id, null, null, null, runId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "Failed", null, null, null, null));
        var fact = Read(Assert.Single(await db.OutboxMessages.AsNoTracking().Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync()));
        Assert.Equal("job-run:" + runId, fact.CorrelationId);
        Assert.Equal(runId.ToString(), fact.AttemptId);
        Assert.Equal(runId.ToString(), fact.ParentExecutionId);
        Assert.Equal(FlowExecutionSourceKind.Request, fact.SourceKind);
        Assert.Equal("Failed", fact.RawStatus);
        Assert.Equal(FlowExecutionOutcome.Failed, fact.Outcome);
        Assert.Empty(await db.OutboxMessages.Where(x => x.Source == "job-terminal-ready").ToArrayAsync());
    }

    [Fact]
    public void Source_bounds_keep_valid_unicode_and_native_identifiers_without_fabricating_start_time()
    {
        var now = DateTimeOffset.UtcNow;
        var script = ExecutionLifecycleOutbox.ScriptEvent(new()
        {
            RequestId = new string('x', 256), TenantId = 7, TaskType = TaskKinds.ExecLibraryScript,
            Status = "Completed", ScriptName = new string('n', 511) + "😀", TargetDisplayName = new string('t', 511) + "😀",
            CompletedAtUtc = now
        }, "Running", now)!;
        Assert.Equal(256, script.ExecutionId.Length);
        Assert.Equal(new string('n', 511), script.Definition.Name);
        Assert.Equal(new string('t', 511), script.Target.DisplayName);
        Assert.True(script.CorrelationId!.Length <= FlowExecutionEventLimits.MaximumIdentifierLength);
        Assert.True(FlowExecutionEventValidator.Validate(script).Valid);
        var skewed = ExecutionLifecycleOutbox.RequestEvent(new()
        {
            Id = 1, TargetTenantId = 7, SourceSystem = "synthetic-source", JobDefinitionId = "missing-definition",
            Status = "Failed", CreatedAtUtc = now.AddMinutes(1), UpdatedAtUtc = now
        }, "New", now)!;
        Assert.Null(skewed.StartedAtUtc);
        Assert.Equal(now, skewed.CompletedAtUtc);
        Assert.True(FlowExecutionEventValidator.Validate(skewed).Valid);
        var late = ExecutionLifecycleOutbox.RequestEvent(new()
        {
            Id = 2, TargetTenantId = 7, SourceSystem = "synthetic-source", JobDefinitionId = "missing-definition",
            Status = "Failed", CreatedAtUtc = now.AddHours(-2), UpdatedAtUtc = now.AddHours(-1)
        }, "New", now)!;
        var subscription = new FlowExecutionTriggerSettingsDto([FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.Failed], FlowExecutionScope.AllInTenant);
        Assert.Equal(now, late.OccurredAtUtc);
        Assert.Equal(now.AddHours(-1), late.CompletedAtUtc);
        Assert.True(FlowExecutionEventMatching.Matches(7, subscription, late, now.AddMinutes(-1)));
        Assert.False(FlowExecutionEventMatching.Matches(7, subscription,
            late with { OccurredAtUtc = now.AddHours(-1), ObservedAtUtc = now.AddHours(-1) }, now.AddMinutes(-1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Canonical_denial_and_causation_require_recorded_admission_evidence(bool denied)
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var flowRun = Guid.NewGuid();
        var node = Guid.NewGuid();
        var parentEvent = Guid.NewGuid();
        db.JobRuns.Add(new()
        {
            Id = 42, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)JobRunState.Failed, CreatedAtUtc = now, Error = "execution-admission-denied: untrusted text"
        });
        db.Set<ExecutionAdmissionRecord>().Add(new()
        {
            ActionKey = Guid.NewGuid(), TenantId = 7, PrincipalId = "synthetic-configuring-operator",
            FlowVersionId = Guid.NewGuid(), FlowRunId = flowRun, NodeId = node, NodeAttempt = 1,
            Kind = (int)ExecutionAdmissionKind.Job, ExecutionId = "42", PayloadHash = new string('a', 64),
            ProtectedPayloadJson = "synthetic protected reference", CreatedAtUtc = now,
            AuthorityDeniedAtUtc = denied ? now : null,
            CausationJson = JsonSerializer.Serialize(new ExecutionAdmissionCausation("original-occurrence", parentEvent, 2, 1, "17"))
        });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var row = await requests.CreateAsync(new("synthetic-source", string.Empty, "41", null, 7, agent));
        await ExecutionLifecycleOutbox.RecordAcceptedRequestJobBindingAsync(db, row,
            new(42, 41, 7, string.Empty, "synthetic-source", JobRunState.Failed, 0, now, null, null, null, null, null, agent), default);
        await requests.UpdateAsync(new(row.Id, null, null, null, "42", "Failed", null, null, null, null));
        var fact = Read(Assert.Single(await db.OutboxMessages.Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync()));
        Assert.Equal("Failed", fact.RawStatus);
        Assert.Equal(denied ? FlowExecutionOutcome.Denied : FlowExecutionOutcome.Failed, fact.Outcome);
        Assert.Equal("original-occurrence", fact.Causation!.RootOccurrenceId);
        Assert.Equal(parentEvent, fact.Causation.ParentEventId);
        Assert.Equal(flowRun, fact.Causation.FlowRunId);
        Assert.Equal(node, fact.Causation.NodeId);
        Assert.Equal(2, fact.Causation.HopCount);
        Assert.Equal(1, fact.Causation.RecoveryAttempt);
        Assert.Equal("42", fact.ParentExecutionId);
        Assert.Equal("42", fact.RootExecutionId);
    }

    [Theory]
    [InlineData("Completed", 1, "{\"exitCode\":1}", FlowExecutionOutcome.Failed)]
    [InlineData("Completed", 0, "{\"exitCode\":0}", FlowExecutionOutcome.Succeeded)]
    [InlineData("Completed", null, "{}", FlowExecutionOutcome.Unknown)]
    [InlineData("Completed", 0, "{invalid", FlowExecutionOutcome.Unknown)]
    [InlineData("Failed", 1, "{invalid", FlowExecutionOutcome.Unknown)]
    [InlineData("TimedOut", -1, "{invalid", FlowExecutionOutcome.TimedOut)]
    [InlineData("Cancelled", null, "{invalid", FlowExecutionOutcome.Cancelled)]
    [InlineData("Unknown", null, "{invalid", FlowExecutionOutcome.Unknown)]
    [InlineData("Failed", -1, "{\"exitCode\":-1,\"timedOut\":true}", FlowExecutionOutcome.TimedOut)]
    [InlineData("Failed", -1, "{\"exitCode\":-1,\"timedOut\":true,\"outcomeUnknown\":true}", FlowExecutionOutcome.Unknown)]
    [InlineData("Failed", null, "{\"cancelled\":true}", FlowExecutionOutcome.Cancelled)]
    public void Script_terminal_classification_retains_raw_status_and_uses_native_result_evidence(string status,
        int? exitCode, string result, FlowExecutionOutcome expected)
    {
        var now = DateTimeOffset.UtcNow;
        var fact = ExecutionLifecycleOutbox.ScriptEvent(new()
        {
            RequestId = "synthetic-script", TenantId = 7, TaskType = TaskKinds.ExecLibraryScript, Status = status,
            CreatedAtUtc = now.AddMinutes(-1), CompletedAtUtc = now, ExitCode = exitCode, ResultJson = result
        }, "Processing", now)!;
        Assert.Equal(expected, fact.Outcome);
        Assert.Equal(status, fact.RawStatus);
        Assert.True(FlowExecutionEventValidator.Validate(fact).Valid);
        Assert.Null(ExecutionLifecycleOutbox.ScriptEvent(new()
        {
            RequestId = "still-running", TenantId = 7, TaskType = TaskKinds.ExecLibraryScript, Status = "Processing",
            ResultJson = "{\"timedOut\":true}", CreatedAtUtc = now
        }, "Pending", now));
    }

    [Fact]
    public async Task Submitted_request_reserves_one_identity_pins_protected_inputs_and_admission_failure_needs_no_job()
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Jobs.Add(new() { Id = 41, TenantId = 7, AgentId = agent, Name = "Synthetic approved job",
            CreatedAtUtc = now, UpdatedAtUtc = now });
        db.JobParameters.Add(new() { Id = 1, JobId = 41, Name = "token", Type = "secret", Required = true });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "41",
            "{\"token\":\"synthetic-original\"}", 7, agent));
        await ExecutionLifecycleOutbox.RecordSubmittedRequestIntentAsync(db, request, default);
        await db.SaveChangesAsync();
        var material = await requests.GetExecutionMaterialAsync(request.Id);
        Assert.Equal("{\"token\":\"synthetic-original\"}", material!.InputsJson);
        Assert.DoesNotContain("synthetic-original", request.JobInputs!);
        var allocations = 0;
        ulong Allocate() { allocations++; return 9007199254740993; }
        var first = await ExecutionLifecycleOutbox.ReserveCanonicalRequestRunAsync(db, request.Id, Allocate, default);
        var retry = await ExecutionLifecycleOutbox.ReserveCanonicalRequestRunAsync(db, request.Id, Allocate, default);
        Assert.Equal(first, retry);
        Assert.Equal(1, allocations);
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.UpdateAsync(new(request.Id, null, null,
            null, null, null, null, null, "{\"token\":\"changed\"}", null)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => requests.UpdateAsync(new(request.Id, "another-source",
            null, null, null, null, null, null, null, null)));
        await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", "Admission was rejected: synthetic-original", null, null, null));
        var fact = Read(await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(FlowExecutionSourceKind.Request, fact.SourceKind);
        Assert.Equal(FlowExecutionSourceKind.Job, fact.DefinitionSourceKind);
        Assert.Equal("41", fact.Definition.Id);
        Assert.Null(fact.ParentExecutionId);
        Assert.False(fact.Replay!.Supported);
        Assert.NotNull(fact.Reason);
        Assert.DoesNotContain("synthetic-original", fact.Reason!);
        Assert.Empty(await db.JobRuns.ToArrayAsync());
        var selected = new FlowExecutionTriggerSettingsDto([FlowExecutionSourceKind.Request],
            [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
            { DefinitionReferences = [new(FlowExecutionSourceKind.Job, "41")] };
        Assert.True(FlowExecutionEventMatching.Matches(7, selected, fact, now.AddMinutes(-1)));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ExecutionLifecycleOutbox.ReserveCanonicalRequestRunAsync(db, request.Id, Allocate, default));
        Assert.Equal(1, allocations);
    }

    [Theory]
    [InlineData("Failed", FlowExecutionOutcome.Failed)]
    [InlineData("TimedOut", FlowExecutionOutcome.Unknown)]
    public async Task Caller_claimed_execution_cannot_manufacture_parent_timeout_or_replay_authority(string rawStatus, FlowExecutionOutcome expected)
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.JobRuns.Add(new() { Id = 42, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)(rawStatus == "TimedOut" ? JobRunState.TimedOut : JobRunState.Failed), CreatedAtUtc = now });
        db.Set<JobRunControlRecord>().Add(new() { RunId = 42, TerminalReadyAtUtc = now, TerminalResultHash = new string('a', 64) });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "41", null, 7, agent));
        await requests.UpdateAsync(new(request.Id, null, null, null, "42", rawStatus, null, null, null, null));
        var fact = Read(await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(expected, fact.Outcome);
        Assert.Equal("request:" + request.Id, fact.CorrelationId);
        Assert.Equal(request.Id.ToString(), fact.AttemptId);
        Assert.Null(fact.ParentExecutionId);
        Assert.Null(fact.DefinitionSourceKind);
        Assert.False(fact.Replay!.Supported);
    }

    [Fact]
    public async Task Canonical_request_acceptance_is_idempotent_and_does_not_retimestamp_terminal_facts()
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Jobs.Add(new() { Id = 41, TenantId = 7, AgentId = agent, Name = "Synthetic approved job",
            CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "041", null, 7, agent));
        await ExecutionLifecycleOutbox.RecordSubmittedRequestIntentAsync(db, request, default);
        await db.SaveChangesAsync();
        await ExecutionLifecycleOutbox.ReserveCanonicalRequestRunAsync(db, request.Id, () => 42, default);
        db.JobRuns.Add(new() { Id = 42, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)JobRunState.Running, CreatedAtUtc = now });
        var run = new JobRunInfo(42, 41, 7, string.Empty, "synthetic-source",
            JobRunState.Running, 0, now, null, null, null, null, null, agent);
        await ExecutionLifecycleOutbox.RecordCanonicalRequestBindingAsync(db, request.Id, run, now, default);
        await db.SaveChangesAsync();
        await ExecutionLifecycleOutbox.RecordCanonicalRequestBindingAsync(db, request.Id, run, now.AddMinutes(1), default);
        await db.SaveChangesAsync();
        Assert.Equal(now, (await requests.GetAsync(request.Id))!.UpdatedAtUtc);
        var terminal = (await requests.UpdateAsync(new(request.Id, null, null, null, null, "Failed", null, null, null, null)))!;
        var savedPayload = (await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType)).PayloadJson;
        var fact = JsonSerializer.Deserialize(savedPayload, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        await ExecutionLifecycleOutbox.RecordCanonicalRequestBindingAsync(db, request.Id, run, now.AddMinutes(2), default);
        await db.SaveChangesAsync();
        var afterRetry = (await requests.GetAsync(request.Id))!;
        Assert.Equal("Failed", afterRetry.Status);
        Assert.Equal(terminal.UpdatedAtUtc, afterRetry.UpdatedAtUtc);
        Assert.Equal(savedPayload, (await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType)).PayloadJson);
        Assert.Equal("job-run:42", fact.CorrelationId);
    }

    [Fact]
    public async Task Request_timeout_requires_accepted_terminal_ready_proof_and_later_verification_is_idempotent()
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.JobRuns.Add(new() { Id = 42, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)JobRunState.TimedOut, CreatedAtUtc = now, CompletedAtUtc = now });
        var control = new JobRunControlRecord { RunId = 42 };
        db.Set<JobRunControlRecord>().Add(control);
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "41", null, 7, agent));
        var run = new JobRunInfo(42, 41, 7, string.Empty, "synthetic-source", JobRunState.TimedOut, 0,
            now, null, now, "Authoritative native deadline.", null, null, agent);
        await ExecutionLifecycleOutbox.RecordAcceptedRequestJobBindingAsync(db, request, run, default);
        await db.SaveChangesAsync();
        var claimed = (await requests.UpdateAsync(new(request.Id, null, null, null, "42", "TimedOut",
            "Unproved timeout claim.", null, null, null)))!;
        var unproved = Read(await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(FlowExecutionOutcome.Unknown, unproved.Outcome);
        Assert.Equal("TimedOut", unproved.RawStatus);
        Assert.Equal("request_timeout_evidence_unavailable", unproved.ReasonCode);
        var details = new JobRunDetails(run, [], []);
        var resultHash = JobRunProjectionHash.Compute(details);
        control.TerminalReadyAtUtc = now;
        control.TerminalResultHash = resultHash;
        await db.SaveChangesAsync();
        await ExecutionLifecycleOutbox.ProjectAcceptedRequestTerminalsAsync(db, details, resultHash, requests, default);
        await db.SaveChangesAsync();
        var facts = (await db.OutboxMessages.Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync())
            .Select(Read).ToArray();
        Assert.Equal(2, facts.Length);
        var verified = Assert.Single(facts, fact => fact.Outcome == FlowExecutionOutcome.TimedOut);
        Assert.NotEqual(unproved.EventId, verified.EventId);
        Assert.Equal(unproved.OccurrenceId, verified.OccurrenceId);
        Assert.Equal("TimedOut", verified.RawStatus);
        Assert.Contains(verified.Results!, value => value.Kind == "job-result" && value.ContentHash == resultHash);
        await ExecutionLifecycleOutbox.ProjectAcceptedRequestTerminalsAsync(db, details, resultHash, requests, default);
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.OutboxMessages.CountAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(claimed.UpdatedAtUtc, (await requests.GetAsync(request.Id))!.UpdatedAtUtc);
    }

    [Fact]
    public async Task Late_script_result_writes_append_actual_terminal_evidence_without_mutating_earlier_facts()
    {
        await using var db = Memory();
        var now = DateTimeOffset.UtcNow;
        const string requestId = "synthetic-late-script";
        db.JobTaskActivities.Add(new()
        {
            Id = 20, RequestId = requestId, TenantId = 7, AgentId = Guid.NewGuid(),
            TaskType = TaskKinds.ExecLibraryScript, Status = "Completed", ScriptName = "Synthetic pinned script",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64))),
            CreatedAtUtc = now.AddMinutes(-1), CompletedAtUtc = now
        });
        await db.SaveChangesAsync();
        var first = await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType);
        var originalPayload = first.PayloadJson;
        Assert.Equal(FlowExecutionOutcome.Unknown, Read(first).Outcome);
        Assert.False(Assert.Single(Read(first).Results!).Available);
        var jobs = new JobRunService(db, new EphemeralDataProtectionProvider());
        const string terminalBody = "{\"exitCode\":7,\"stdout\":\"synthetic terminal output\"}";
        await jobs.UpdateTaskActivityStatusAsync(new(requestId, "Completed", null, now, terminalBody, ExitCode: 7));
        var facts = (await db.OutboxMessages.Where(x => x.Type == FlowExecutionEventLimits.OutboxType).ToArrayAsync())
            .Select(Read).ToArray();
        Assert.Equal(2, facts.Length);
        var failed = Assert.Single(facts, fact => fact.Outcome == FlowExecutionOutcome.Failed);
        Assert.Equal("Completed", failed.RawStatus);
        Assert.Equal(7, failed.ExitCode);
        Assert.True(Assert.Single(failed.Results!).Available);
        Assert.NotEqual(Read(first).EventId, failed.EventId);
        await jobs.UpdateTaskActivityStatusAsync(new(requestId, "Completed", null, now, terminalBody, ExitCode: 7));
        Assert.Equal(2, await db.OutboxMessages.CountAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        await jobs.UpdateTaskActivityStatusAsync(new(requestId, "Completed", null, now,
            "{\"exitCode\":7,\"stdout\":\"synthetic final diagnostics\"}", ExitCode: 7));
        Assert.Equal(3, await db.OutboxMessages.CountAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(originalPayload, (await db.OutboxMessages.SingleAsync(x => x.Id == first.Id)).PayloadJson);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Linked_top_level_reruns_keep_the_trusted_original_parent_and_root(bool script)
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var execution = script ? "synthetic-script-rerun" : "42";
        var original = script ? new string('父', 256) : "9007199254740993";
        var root = script ? new string('源', 256) : "9007199254740001";
        var occurrence = script ? new string('起', 128) : "synthetic-original-occurrence";
        var cause = new ExecutionAdmissionCausation(occurrence, Guid.NewGuid(), 1, 1, original)
            { RootExecutionId = root };
        var admission = new ExecutionAdmissionRecord
        {
            ActionKey = Guid.NewGuid(), TenantId = 7, PrincipalId = "synthetic-configuring-operator",
            FlowVersionId = Guid.NewGuid(), FlowRunId = Guid.NewGuid(), NodeId = Guid.NewGuid(), NodeAttempt = 1,
            Kind = script ? (int)ExecutionAdmissionKind.LibraryScript : (int)ExecutionAdmissionKind.Job,
            ExecutionId = execution, PayloadHash = new string('a', 64), ProtectedPayloadJson = "synthetic protected material",
            CausationJson = JsonSerializer.Serialize(cause), CreatedAtUtc = now
        };
        db.Set<ExecutionAdmissionRecord>().Add(admission);
        // Canonical admission commits before native terminal activity can arrive.
        await db.SaveChangesAsync();
        if (script)
        {
            db.JobTaskActivities.Add(new() { Id = 20, RequestId = execution, TenantId = 7, AgentId = agent,
                TaskType = TaskKinds.ExecLibraryScript, Status = "Completed", ExitCode = 7, ResultJson = "{\"exitCode\":7}",
                CreatedAtUtc = now, CompletedAtUtc = now, ScriptName = "Synthetic pinned script",
                OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64))) });
        }
        else db.JobRuns.Add(new() { Id = 42, JobId = 41, TenantId = 7, AgentId = agent, CreatedAtUtc = now,
            CompletedAtUtc = now, Status = (int)JobRunState.Failed, StartedBy = "synthetic-source" });
        await db.SaveChangesAsync();
        if (!script)
        {
            var details = new JobRunDetails(new(42, 41, 7, string.Empty, "synthetic-source", JobRunState.Failed, 0,
                now, null, now, null, null, null, agent), [], []);
            await ExecutionLifecycleOutbox.RecordJobAsync(db, details, JobRunProjectionHash.Compute(details), now, default);
            await db.SaveChangesAsync();
        }
        var value = Read(await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
        Assert.Equal(original, value.ParentExecutionId);
        Assert.Equal(root, value.RootExecutionId);
        Assert.Equal(occurrence, value.Causation!.RootOccurrenceId);
        Assert.Equal(cause.ParentEventId, value.Causation.ParentEventId);
        Assert.Equal(admission.FlowRunId, value.Causation.FlowRunId);
        Assert.True(FlowExecutionEventValidator.Validate(value).Valid);
        var legacy = "{\"RootOccurrenceId\":\"legacy\",\"ParentEventId\":null,\"HopCount\":0,\"RecoveryAttempt\":0,\"SourceExecutionId\":null}";
        Assert.Equal(legacy, JsonSerializer.Serialize(JsonSerializer.Deserialize<ExecutionAdmissionCausation>(legacy)));
    }

    private static FlowExecutionEventDto Read(OutboxMessage row) =>
        JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
    private static OrchestratorDbContext Memory() => new(new DbContextOptionsBuilder<OrchestratorDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);
}

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class FlowExecutionLifecycleOutboxPostgreSqlTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Request_state_and_fact_roll_back_together_and_direct_projection_keeps_one_terminal_event()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var connection = await postgres.CreateDatabaseAsync(ct);
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(connection).Options;
        var keys = new EphemeralDataProtectionProvider();
        int id;
        await using (var db = new OrchestratorDbContext(options))
        {
            await db.Database.MigrateAsync(ct);
            var requests = new RequestService(db, keys);
            id = (await requests.CreateAsync(new("synthetic-source", string.Empty, "missing-definition", null, 7, null), ct)).Id;
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                await requests.UpdateAsync(new(id, null, null, null, null, "Failed", null, null, null, null), ct);
                Assert.Single(await db.OutboxMessages.ToArrayAsync(ct));
                await transaction.RollbackAsync(ct);
            }
        }

        await using (var db = new OrchestratorDbContext(options))
        {
            var requests = new RequestService(db, keys);
            Assert.Equal("New", (await db.Requests.SingleAsync(ct)).Status);
            Assert.Empty(await db.OutboxMessages.ToArrayAsync(ct));
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Failed, "Synthetic failure", null, DateTimeOffset.UtcNow, ct));
        }

        await using (var db = new OrchestratorDbContext(options))
        {
            var requests = new RequestService(db, keys);
            Assert.Equal("Failed", (await db.Requests.AsNoTracking().SingleAsync(ct)).Status);
            var fact = Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync(ct));
            var value = JsonSerializer.Deserialize(fact.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            Assert.Equal(FlowExecutionOutcome.Failed, value.Outcome);
            Assert.True(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Failed, "Duplicate callback", null, DateTimeOffset.UtcNow, ct));
            Assert.False(await OrchestrationCallbackProjection.ProjectRequestStatusAsync(db, requests, id,
                JobRunState.Running, null, null, DateTimeOffset.UtcNow, ct));
            Assert.Single(await db.OutboxMessages.AsNoTracking().ToArrayAsync(ct));
            Assert.Equal("Failed", (await db.Requests.AsNoTracking().SingleAsync(ct)).Status);
        }
    }
    [Fact]
    public async Task Large_source_presentation_is_visibly_bounded_without_losing_protected_replay_or_causal_identity()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(await postgres.CreateDatabaseAsync(ct)).Options;
        var keys = new EphemeralDataProtectionProvider();
        var requestId = new string('原', 256);
        var parentId = new string('先', 256);
        var rootId = new string('始', 256);
        var occurrence = new string('起', 128);
        var definitionName = new string('名', 512);
        var targetName = new string('機', 512);
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using var db = new OrchestratorDbContext(options);
        await db.Database.MigrateAsync(ct);
        db.Agents.Add(new() { Id = agent, TenantId = 7, Name = "synthetic-host", CreatedAtUtc = now });
        db.JobTaskActivities.Add(new()
        {
            RequestId = requestId, TenantId = 7, AgentId = agent, TaskType = TaskKinds.ExecLibraryScript,
            Status = "Running", CreatedAtUtc = now, ScriptName = definitionName,
            TargetDisplayName = targetName, TargetHostName = "synthetic-host.example.test",
            OutputProtectionJson = JsonSerializer.Serialize(TaskOutputPolicy.Create([], 91, 4, new string('A', 64)))
        });
        await db.SaveChangesAsync(ct);
        var identity = new ExecutionAdmissionIdentity(Guid.NewGuid(), 7, "synthetic-operator",
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1)
        {
            Causation = new(occurrence, Guid.NewGuid(), 1, 1, parentId) { RootExecutionId = rootId }
        };
        var admission = await new ExecutionAdmissionStore(db, keys, TimeProvider.System).ReserveAsync(
            new(identity, ExecutionAdmissionKind.LibraryScript, "{\"syntheticPinnedSource\":\"protected fixture\"}"),
            () => requestId, ct);
        var result = "{\"exitCode\":7,\"stderr\":\"Synthetic definitive failure\"}";
        await new JobRunService(db, keys).UpdateTaskActivityStatusAsync(
            new(requestId, "Completed", new string('錯', 512), now, result, ExitCode: 7), ct);

        db.ChangeTracker.Clear();
        var native = await db.JobTaskActivities.AsNoTracking().SingleAsync(ct);
        Assert.Equal("Completed", native.Status);
        Assert.Equal(7, native.ExitCode);
        Assert.Equal(definitionName, native.ScriptName);
        Assert.Equal(targetName, native.TargetDisplayName);
        var raw = ExecutionLifecycleOutbox.ScriptEvent(native, "Running", now)!;
        Assert.Contains("event_byte_limit", FlowExecutionEventValidator.Validate(raw).Issues);
        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType, ct);
        var value = JsonSerializer.Deserialize(row.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
        Assert.True(FlowExecutionEventValidator.Validate(value).Valid);
        Assert.True(Encoding.UTF8.GetByteCount(row.PayloadJson) <= FlowExecutionEventLimits.MaximumEnvelopeBytes - 256);
        Assert.Equal(requestId, value.ExecutionId);
        Assert.Equal(requestId, value.AttemptId);
        Assert.Equal(parentId, value.ParentExecutionId);
        Assert.Equal(rootId, value.RootExecutionId);
        Assert.Equal(occurrence, value.Causation!.RootOccurrenceId);
        Assert.Equal(identity.FlowRunId, value.Causation.FlowRunId);
        Assert.Equal(identity.NodeId, value.Causation.NodeId);
        Assert.Equal(FlowExecutionOutcome.Failed, value.Outcome);
        Assert.Equal("Completed", value.RawStatus);
        Assert.Equal(7, value.ExitCode);
        Assert.StartsWith("錯", value.Reason);
        Assert.Contains("Presentation truncated", value.Reason);
        Assert.StartsWith("名", value.Definition.Name);
        Assert.EndsWith("…", value.Definition.Name);
        Assert.StartsWith("機", value.Target.DisplayName);
        Assert.EndsWith("…", value.Target.DisplayName);
        Assert.True(value.Replay!.Supported);
        Assert.Equal(FlowExecutionSourceKind.Script, value.Replay.SourceKind);
        Assert.Equal("91", value.Replay.Definition.Id);
        Assert.Equal("4", value.Replay.Definition.Revision);
        Assert.Equal(new string('A', 64), value.Replay.Definition.ContentHash);
        Assert.Equal(agent, value.Replay.Target.AgentId);
        Assert.Equal(requestId, value.Replay.SourceExecutionId);
        Assert.Equal(requestId, value.Replay.SourceAttemptId);
        Assert.Equal("script-execution:" + requestId, value.Replay.InputsReference);
        Assert.Equal(requestId, Assert.Single(value.Results!).Id);
        Assert.NotNull(Assert.Single(value.Results!).ContentHash);
        Assert.Equal(admission.ExecutionId, (await db.Set<ExecutionAdmissionRecord>().SingleAsync(ct)).ExecutionId);
    }

}
