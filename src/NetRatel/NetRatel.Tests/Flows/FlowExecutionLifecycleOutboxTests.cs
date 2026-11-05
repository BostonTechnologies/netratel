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
    [InlineData("TimedOut", FlowExecutionOutcome.TimedOut)]
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
    }

    [Theory]
    [InlineData("Completed", 1, "{\"exitCode\":1}", FlowExecutionOutcome.Failed)]
    [InlineData("Completed", 0, "{\"exitCode\":0}", FlowExecutionOutcome.Succeeded)]
    [InlineData("Completed", null, "{}", FlowExecutionOutcome.Unknown)]
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

    [Fact]
    public async Task Caller_claimed_execution_cannot_manufacture_parent_or_replay_authority()
    {
        await using var db = Memory();
        var agent = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.JobRuns.Add(new() { Id = 42, JobId = 41, TenantId = 7, AgentId = agent, StartedBy = "synthetic-source",
            Status = (int)JobRunState.Failed, CreatedAtUtc = now });
        await db.SaveChangesAsync();
        var requests = new RequestService(db, new EphemeralDataProtectionProvider());
        var request = await requests.CreateAsync(new("synthetic-source", string.Empty, "41", null, 7, agent));
        await requests.UpdateAsync(new(request.Id, null, null, null, "42", "Failed", null, null, null, null));
        var fact = Read(await db.OutboxMessages.SingleAsync(x => x.Type == FlowExecutionEventLimits.OutboxType));
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
}
