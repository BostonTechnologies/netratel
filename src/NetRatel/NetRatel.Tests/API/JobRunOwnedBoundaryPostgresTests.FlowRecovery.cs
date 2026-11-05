using System.Globalization;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.API.Services.Flows;
using NetRatel.Application.Flows;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class JobRunOwnedBoundaryPostgresTests
{
    private const string RecoveryOperator = "synthetic-flow-recovery-operator";

    // The native job owner/actors, immutable lifecycle facts/selections, current identity grants,
    // Velox driver, checkpoint/action stores and canonical admissions are real. The investigation
    // result and command transports are synthetic; no external AI policy/model or native process
    // execution is claimed by this regression.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Native_job_failure_recovers_through_saved_investigation_pinned_repair_and_exactly_one_verified_original_rerun(bool truncateOutput)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var ct = deadline.Token;
        await using var h = await Harness.CreateAsync(await postgres.CreateDatabaseAsync(ct), ct, retainFlowRecovery: true);
        var published = await PrepareNativeRecoveryAsync(h, ct);
        await AdvanceRecoveryClockAsync(h, ct);
        var original = await h.StartAsync(ct);
        var originalPayload = JsonSerializer.Deserialize<ExecShellCommandPayload>(h.Gateway.LastDispatchPayload!)!;
        await h.LifecycleAsync(original.Id, JobGatewayLifecycleStatus.Started, 3, null, false, ct);
        await h.LifecycleAsync(original.Id, JobGatewayLifecycleStatus.Failed, 4,
            """{"stderr":["synthetic native original failed"],"exitCode":1}""", false, ct);
        FlowExecutionEventDto failure;
        FlowExecutionSubscriptionDeliveryDto selected;
        string originalSourcePayload;
        await using (var source = h.Services.CreateAsyncScope())
        {
            var db = source.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var nativeId = original.Id.ToString(CultureInfo.InvariantCulture);
            var fact = await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionEventLimits.OutboxType && row.Source == "job-terminal-ready" && row.EntityId == nativeId, ct);
            originalSourcePayload = fact.PayloadJson;
            failure = JsonSerializer.Deserialize(fact.PayloadJson, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)!;
            failure.Outcome.Should().Be(FlowExecutionOutcome.Failed);
            failure.Replay!.Supported.Should().BeTrue();
            failure.Replay.SourceKind.Should().Be(FlowExecutionSourceKind.Job);
            selected = ExecutionLifecycleSubscriptions.ReadDelivery((await db.OutboxMessages.AsNoTracking().SingleAsync(row =>
                row.Type == FlowExecutionSubscriptionLimits.OutboxType, ct)).PayloadJson)!;
            await source.ServiceProvider.GetRequiredService<IScriptService>().UpdateAsync(new(published.RepairScript.Id, null, null,
                null, "printf edited-repair-after-publication", null, null, published.RepairScript.SourceRevision), ct);
            await source.ServiceProvider.GetRequiredService<NetRatel.Application.Jobs.IJobDefinitionService>().UpdateStepAsync(
                new(42, null, null, "printf edited-job-after-original-failure", null, null, null), ct);
            Assert.Empty(await db.MonitoringOccurrences.ToArrayAsync(ct));
        }
        var admission = new FlowExecutionEventAdmissionRequest(7, published.Version.Id, failure.EventId,
            [failure.EventId], selected.DeliveryId);
        var store = h.Services.GetRequiredService<FlowPersistenceService>();
        var ingress = await store.EnqueueExecutionAsync(admission, ct);
        ingress.Disposition.Should().Be(FlowIngressDisposition.Enqueued);
        var runId = ingress.RunId!.Value;
        var first = (await store.ClaimAsync(Guid.NewGuid(), ct))!;
        first.RunId.Should().Be(runId);
        first.Event.Data.Should().BeNull();
        first.Event.ExecutionEvent!.EventId.Should().Be(failure.EventId);
        first.Event.SourceAdmissionKey.Should().Be(selected.AdmissionKey);
        var investigationCalls = 0;
        async Task<FlowIncidentActionResult> Investigation(FlowIncidentActionDraft draft, CancellationToken token)
        {
            investigationCalls++;
            draft.Kind.Should().Be(FlowActionKind.NetclawInvestigation);
            draft.ActionNodeId.Should().Be(published.InvestigationId);
            draft.Event.Data.Should().BeNull();
            JsonSerializer.Serialize(draft.Event.ExecutionEvent, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto)
                .Should().Be(JsonSerializer.Serialize(failure, FlowExecutionEventJsonContext.Default.FlowExecutionEventDto));
            (await store.GetOrCreateActionAsync(first, draft, token))!.Status.Should().Be(FlowActionStatus.Pending);
            (await store.StartActionAsync(first, draft.ActionNodeId, token))!.Status.Should().Be(FlowActionStatus.Dispatching);
            var now = h.Clock.GetUtcNow();
            var result = new FlowIncidentActionResult(FlowIncidentActionResultKind.Succeeded, "synthetic-investigation-completed")
            {
                AiResult = new(7, runId, published.Version.Id, draft.ActionNodeId, Guid.NewGuid(),
                    FlowAiCorrelation.MessageId(draft), FlowAiResultStatus.Completed, now, now,
                    "Synthetic provider disposition selects the configured repair.",
                    "Synthetic fixture result; no external model or diagnostic tool was invoked.",
                    "Run only the already published repair action, then verify the linked original rerun.")
            };
            Assert.True(await store.CompleteActionAsync(first, draft.ActionNodeId, result, token));
            return result;
        }
        var pendingRepair = await RecoveryDriver(h, store).ExecuteAsync(first, Investigation, ct);
        pendingRepair.Status.Should().Be(FlowRunStatus.WaitingForExecution);
        investigationCalls.Should().Be(1);
        h.RepairCommands.Dispatches.Should().Be(1);
        h.Gateway.Dispatches.Should().Be(1);
        FlowNodeCheckpointDto repairCheckpoint;
        await using (var inspect = h.Services.CreateAsyncScope())
        {
            var db = inspect.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct);
            run.Status.Should().Be(FlowRunStatus.WaitingForExecution);
            run.LeaseToken.Should().BeNull();
            repairCheckpoint = JsonSerializer.Deserialize<FlowContinuationDto>(run.ContinuationJson!, StepJson)!.Node!;
            repairCheckpoint.NodeId.Should().Be(published.RepairId);
            repairCheckpoint.AcceptedExecutionId.Should().NotBeNullOrEmpty();
            repairCheckpoint.Investigation!.Status.Should().Be(FlowAiResultStatus.Completed);
            repairCheckpoint.Inputs.Should().BeEmpty();
            run.ContinuationJson.Should().NotContain("SYNTHETIC_FLOW_TOKEN");
            var ai = await db.FlowActions.AsNoTracking().SingleAsync(row => row.RunId == runId, ct);
            ai.Status.Should().Be(FlowActionStatus.Succeeded);
            JsonSerializer.Deserialize<FlowAiResultDto>(ai.AiResultJson!, StepJson)!.Summary.Should().Contain("Synthetic provider disposition");
            var actual = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(h.RepairCommands.Payload)!;
            actual.ScriptContent.Should().Be(published.RepairScript.Content).And.NotContain("edited-repair");
            actual.Parameters!["vault"].Should().Be("SYNTHETIC_FLOW_TOKEN");
            actual.OutputPolicy!.SecretReferenceParameters.Should().Equal("vault");
            var protectedSubmission = await inspect.ServiceProvider.GetRequiredService<NetRatel.Application.Jobs.IExecutionAdmissionStore>()
                .FindByExecutionAsync(NetRatel.Application.Jobs.ExecutionAdmissionKind.LibraryScript, repairCheckpoint.AcceptedExecutionId!, ct);
            protectedSubmission!.PayloadJson.Should().Contain("SYNTHETIC_FLOW_TOKEN");
            (await db.Set<ExecutionAdmissionRecord>().AsNoTracking().SingleAsync(ct)).ProtectedPayloadJson.Should().NotContain("SYNTHETIC_FLOW_TOKEN");
            await inspect.ServiceProvider.GetRequiredService<NetRatel.Application.Jobs.IJobRunService>().UpdateTaskActivityStatusAsync(new(
                repairCheckpoint.AcceptedExecutionId!, "Completed", null, h.Clock.GetUtcNow(),
                ResultJson: """{"stdout":["synthetic permitted repair completed"],"exitCode":0}""", ExitCode: 0), ct);
        }
        await AdvanceRecoveryClockAsync(h, ct);
        var restartedStore = new FlowPersistenceService(h.Services.GetRequiredService<IServiceScopeFactory>(), h.Clock);
        var second = (await restartedStore.ClaimAsync(Guid.NewGuid(), ct))!;
        second.Fence.Should().BeGreaterThan(first.Fence);
        second.Continuation!.Node!.AcceptedExecutionId.Should().Be(repairCheckpoint.AcceptedExecutionId);
        var pendingRerun = await RecoveryDriver(h, restartedStore).ExecuteAsync(second, NoRepeatedInvestigation, ct);
        pendingRerun.Status.Should().Be(FlowRunStatus.WaitingForExecution);
        investigationCalls.Should().Be(1);
        h.RepairCommands.Dispatches.Should().Be(1);
        h.Gateway.Dispatches.Should().Be(2);
        var rerunId = h.Gateway.LastDispatchRunId;
        rerunId.Should().NotBe(original.Id);
        var actualRerunPayload = JsonSerializer.Deserialize<ExecShellCommandPayload>(h.Gateway.LastDispatchPayload!)!;
        actualRerunPayload.Command.Should().Be(originalPayload.Command).And.NotContain("edited-job");
        actualRerunPayload.Preferred.Should().Be(originalPayload.Preferred);
        actualRerunPayload.TimeoutSeconds.Should().Be(originalPayload.TimeoutSeconds);
        await using (var inspect = h.Services.CreateAsyncScope())
        {
            var db = inspect.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct);
            var checkpoint = JsonSerializer.Deserialize<FlowContinuationDto>(run.ContinuationJson!, StepJson)!.Node!;
            checkpoint.NodeId.Should().Be(published.RerunId);
            checkpoint.AcceptedExecutionId.Should().Be(rerunId.ToString(CultureInfo.InvariantCulture));
            var receipt = checkpoint.AcceptedReceipt!.Value.Deserialize<FlowExecutionActionReceipt>(StepJson)!;
            receipt.SourceExecutionId.Should().Be(original.Id.ToString(CultureInfo.InvariantCulture));
            receipt.RecoveryAttempt.Should().Be(1);
            var attempts = FlowRecoveryEvidence.FromOutputs(second, checkpoint.Outputs,
                checkpoint.Trace!.Steps.OrderBy(step => step.Sequence).Select(step => step.NodeId).ToArray());
            FlowExecutionEvidenceFormatter.RecoveryDisposition(failure, runId, attempts).Should().NotStartWith("Recovered");
            checkpoint.Trace.Steps.Should().NotContain(step => step.NodeId == published.RerunId);
            run.ContinuationJson.Should().NotContain("SYNTHETIC_FLOW_TOKEN");
            Assert.Equal(2, await db.JobRuns.CountAsync(ct));
        }
        // A fresh worker can only observe the pending native attempt, never allocate a replacement.
        await AdvanceRecoveryClockAsync(h, ct);
        var third = (await restartedStore.ClaimAsync(Guid.NewGuid(), ct))!;
        third.Continuation!.Node!.AcceptedExecutionId.Should().Be(rerunId.ToString(CultureInfo.InvariantCulture));
        (await RecoveryDriver(h, restartedStore).ExecuteAsync(third, NoRepeatedInvestigation, ct)).Status
            .Should().Be(FlowRunStatus.WaitingForExecution);
        h.Gateway.Dispatches.Should().Be(2);
        await h.LifecycleAsync(rerunId, JobGatewayLifecycleStatus.Started, 3, null, true, ct);
        var nativeStdout = "synthetic original rerun verified" + (truncateOutput ? new string('界', 4000) : "");
        await h.LifecycleAsync(rerunId, JobGatewayLifecycleStatus.Completed, 4,
            JsonSerializer.Serialize(new { stdout = new[] { nativeStdout }, exitCode = 0 }), true, ct);
        await h.RecoverAsync(rerunId, false, ct);
        await AdvanceRecoveryClockAsync(h, ct);
        var final = (await restartedStore.ClaimAsync(Guid.NewGuid(), ct))!;
        var recovered = await RecoveryDriver(h, restartedStore).ExecuteAsync(final, NoRepeatedInvestigation, ct);
        recovered.Status.Should().Be(FlowRunStatus.Succeeded);
        Assert.True(await restartedStore.CompleteRunAsync(final, recovered, ct));
        await using (var inspect = h.Services.CreateAsyncScope())
        {
            var db = inspect.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var run = await db.FlowRuns.AsNoTracking().SingleAsync(row => row.Id == runId, ct);
            run.Status.Should().Be(FlowRunStatus.Succeeded);
            var checkpoint = JsonSerializer.Deserialize<FlowContinuationDto>(run.ContinuationJson!, StepJson)!.Node!;
            checkpoint.NodeId.Should().Be(published.RecoveredId);
            var order = checkpoint.Trace!.Steps.OrderBy(step => step.Sequence).Select(step => step.NodeId).ToArray();
            order.Should().Equal(published.TriggerId, published.InvestigationId, published.RepairId, published.RerunId, published.RecoveredId);
            var attempts = FlowRecoveryEvidence.FromOutputs(final, checkpoint.Outputs, order);
            attempts.Should().HaveCount(2);
            var rerun = Assert.Single(attempts, attempt => attempt.IsOriginalRerun);
            rerun.TerminalEvent!.Outcome.Should().Be(FlowExecutionOutcome.Succeeded);
            rerun.TerminalEvent.ExecutionId.Should().Be(rerunId.ToString(CultureInfo.InvariantCulture));
            rerun.TerminalEvent.ParentExecutionId.Should().Be(original.Id.ToString(CultureInfo.InvariantCulture));
            rerun.TerminalEvent.Causation!.RecoveryAttempt.Should().Be(1);
            var nativeDetails = (await inspect.ServiceProvider.GetRequiredService<NetRatel.Application.Jobs.IJobRunService>()
                .GetDetailsAsync(rerunId, ct))!;
            var nativeStep = Assert.Single(nativeDetails.Steps);
            var nativeActivity = Assert.Single(nativeDetails.Activities);
            var recorded = checkpoint.Outputs[published.RerunId];
            recorded.GetProperty("exitCode").GetInt32().Should().Be(0);
            var excerpt = recorded.GetProperty("stdout").GetString()!;
            excerpt.Should().StartWith("synthetic original rerun verified");
            System.Text.Encoding.UTF8.GetByteCount(excerpt).Should().BeLessThanOrEqualTo(8192);
            if (!truncateOutput) excerpt.Should().Be(nativeStdout);
            else excerpt.Length.Should().BeLessThan(nativeStdout.Length);
            recorded.TryGetProperty("stderr", out _).Should().BeFalse();
            recorded.GetProperty("truncated").GetBoolean().Should().Be(truncateOutput);
            var structured = recorded.GetProperty("data");
            System.Text.Encoding.UTF8.GetByteCount(structured.GetRawText()).Should().BeLessThanOrEqualTo(FlowGraphV2Limits.MaximumBindingBytes);
            structured.GetProperty("stepOutcomesIncluded").GetInt32().Should().Be(1);
            structured.GetProperty("stepOutcomesTruncated").GetBoolean().Should().BeFalse();
            var stepOutput = Assert.Single(structured.GetProperty("stepOutcomes").EnumerateArray());
            stepOutput.GetProperty("id").ValueKind.Should().Be(JsonValueKind.String);
            stepOutput.GetProperty("id").GetString().Should().Be(nativeStep.Id.ToString(CultureInfo.InvariantCulture));
            stepOutput.GetProperty("definitionStepId").GetString().Should().Be(nativeStep.JobStepId!.Value.ToString(CultureInfo.InvariantCulture));
            stepOutput.GetProperty("executionId").GetString().Should().Be(nativeActivity.RequestId);
            stepOutput.GetProperty("outcome").GetString().Should().Be("succeeded");
            stepOutput.GetProperty("rawStatus").GetString().Should().Be(nativeStep.Status.ToString());
            stepOutput.GetProperty("exitCode").GetInt32().Should().Be(nativeActivity.ExitCode);
            stepOutput.GetProperty("startedAtUtc").GetDateTimeOffset().Should().Be(nativeStep.StartedAtUtc!.Value);
            stepOutput.GetProperty("completedAtUtc").GetDateTimeOffset().Should().Be(nativeStep.CompletedAtUtc!.Value);
            stepOutput.GetProperty("outputState").GetString().Should().Be("recorded");
            stepOutput.GetProperty("standardOutputState").GetString().Should().Be(truncateOutput ? "truncated" : "recorded");
            stepOutput.GetProperty("standardErrorState").GetString().Should().Be("missing");
            stepOutput.GetProperty("outputTruncated").GetBoolean().Should().Be(truncateOutput);
            recorded.GetProperty("references").Deserialize<IReadOnlyList<FlowExecutionDataReferenceDto>>(StepJson)
                .Should().Equal(rerun.TerminalEvent.Results!);
            FlowExecutionEvidenceFormatter.RecoveryDisposition(failure, runId, attempts)
                .Should().Be("Recovered (linked rerun terminal success verified)");
            run.ContinuationJson.Should().NotContain("SYNTHETIC_FLOW_TOKEN");
            var originalRow = await db.OutboxMessages.AsNoTracking().SingleAsync(row => row.Id == failure.EventId, ct);
            originalRow.PayloadJson.Should().Be(originalSourcePayload);
            (await db.Set<ExecutionAdmissionRecord>().CountAsync(ct)).Should().Be(2);
            Assert.Empty(await db.MonitoringOccurrences.ToArrayAsync(ct));
        }
        var duplicate = await restartedStore.EnqueueExecutionAsync(admission, ct);
        duplicate.Disposition.Should().Be(FlowIngressDisposition.Duplicate);
        duplicate.RunId.Should().Be(runId);
        Assert.Null(await restartedStore.ClaimAsync(Guid.NewGuid(), ct));
        (await h.DetailsAsync(original.Id, ct)).Run.Status.Should().Be(NetRatel.Shared.Contracts.Jobs.JobRunState.Failed);
        (await h.DetailsAsync(rerunId, ct)).Run.Status.Should().Be(NetRatel.Shared.Contracts.Jobs.JobRunState.Succeeded);
        h.Gateway.Dispatches.Should().Be(2);
        h.RepairCommands.Dispatches.Should().Be(1);
        investigationCalls.Should().Be(1);
    }

    private static FlowGraphV2Runtime RecoveryDriver(Harness h, FlowPersistenceService store) =>
        new(store, new VeloxFlowRuntimeAdapter(), h.Services.GetRequiredService<IServiceScopeFactory>(), h.Clock);
    private static Task<FlowIncidentActionResult> NoRepeatedInvestigation(FlowIncidentActionDraft draft, CancellationToken ct) =>
        throw new InvalidOperationException("A restarted driver must consume its saved investigation disposition.");
    private static async Task AdvanceRecoveryClockAsync(Harness h, CancellationToken ct)
    {
        var next = h.Clock.GetUtcNow().AddSeconds(6);
        if (next < DateTimeOffset.UtcNow.AddSeconds(1)) next = DateTimeOffset.UtcNow.AddSeconds(1);
        await h.AdvanceWithPresenceHeartbeatsAsync(next, ct);
    }
    private sealed record NativeRecoveryGraph(FlowVersionDto Version, ScriptInfo RepairScript, Guid TriggerId,
        Guid InvestigationId, Guid RepairId, Guid RerunId, Guid RecoveredId);

    private static async Task<NativeRecoveryGraph> PrepareNativeRecoveryAsync(Harness h, CancellationToken ct)
    {
        await using var setup = h.Services.CreateAsyncScope();
        var identity = setup.ServiceProvider.GetRequiredService<NetRatelIdentityDbContext>();
        identity.ApplicationPrincipals.Add(new() { Id = RecoveryOperator,
            ExternalIssuer = "https://issuer.example.test", ExternalSubject = RecoveryOperator });
        var role = new AccessRole { Name = "Synthetic native recovery operator" };
        foreach (var permission in new[] { NetRatelPermissions.FlowRead, NetRatelPermissions.FlowEdit, NetRatelPermissions.FlowPublish,
            NetRatelPermissions.FlowExecute, NetRatelPermissions.JobManagement, NetRatelPermissions.ScriptExecute })
            role.Permissions.Add(new() { Permission = permission });
        identity.AccessRoles.Add(role);
        identity.PrincipalRoleAssignments.Add(new() { PrincipalId = RecoveryOperator, RoleId = role.Id, TenantId = 7 });
        await identity.SaveChangesAsync(ct);
        var db = setup.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var script = await setup.ServiceProvider.GetRequiredService<IScriptService>().CreateAsync(new(
            "synthetic-configured-repair", "/", "Only this published synthetic repair is permitted.",
            "printf configured-repair", "bash", null), ct);
        db.ScriptParameters.Add(new() { ScriptId = checked((long)script.Id), Name = "vault", Type = "secret_reference", Required = true });
        await db.SaveChangesAsync(ct);
        var trigger = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.ExecutionEvent, "Native original job failed", new(0, 0))
        { ExecutionTrigger = new([FlowExecutionSourceKind.Job], [FlowExecutionOutcome.Failed], FlowExecutionScope.SelectedDefinitions)
            { DefinitionIds = ["41"] } };
        var investigation = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.InvestigateWithAi, "Synthetic investigation disposition",
            new(200, 0), ConnectorId: h.RecoveryConnectorId, ConnectorRevision: 1) { AiSettings = new() };
        var repair = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RunLibraryScript, "Only published repair", new(400, 0))
        {
            ExecutionAction = new(FlowExecutionSourceKind.Script, new(script.Id.ToString(CultureInfo.InvariantCulture), script.Name))
                { Target = new(h.AgentId), TimeoutMinutes = 1, Environment = NetRatel.Shared.ClientEnvironment.Dev },
            Inputs = new Dictionary<string, FlowInputBindingDto>
            { ["vault"] = new(FlowInputSource.SecretReference, FlowDataType.SecretReference, "SYNTHETIC_FLOW_TOKEN") }
        };
        var rerun = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.RerunOriginalExecution, "One original rerun", new(600, 0))
            { Rerun = new(1, 1) };
        var recovered = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Recovered after native terminal success", new(800, 0));
        var unresolved = new FlowNodeDto(Guid.NewGuid(), FlowNodeKind.Finish, "Unresolved explicit outcome", new(800, 200));
        var edges = new List<FlowEdgeDto>
        { new(trigger.Id, FlowNodeCatalogue.Event, investigation.Id, FlowNodeCatalogue.Input) { Id = Guid.NewGuid() } };
        foreach (var (node, next) in new[] { (investigation, repair), (repair, rerun), (rerun, recovered) })
            edges.AddRange(FlowNodeCatalogue.Find(node.Kind)!.Ports.Where(port => port.Direction == FlowPortDirection.Output)
                .Select(port => new FlowEdgeDto(node.Id, port.Id, port.Id == FlowNodeCatalogue.Succeeded ? next.Id : unresolved.Id,
                    FlowNodeCatalogue.Input) { Id = Guid.NewGuid() }));
        var graph = new FlowGraphDto(2, [trigger, investigation, repair, rerun, recovered, unresolved], edges, new());
        var versionId = Guid.NewGuid(); var flowId = Guid.NewGuid();
        var authority = new FlowExecutionAuthorityDto(RecoveryOperator);
        var capture = await setup.ServiceProvider.GetRequiredService<IFlowExecutionDefinitionPins>().CaptureAsync(7, authority, versionId, graph, ct);
        graph = capture.Graph;
        Assert.True(FlowGraphV2Validator.ValidatePublished(graph).Valid);
        var at = h.Clock.GetUtcNow().AddMinutes(-1);
        var graphJson = FlowGraphSerialization.Serialize(graph);
        var definition = new FlowDefinitionRecord { Id = flowId, TenantId = 7, Name = "Synthetic native recovery",
            Revision = 1, Enabled = true, EnabledSinceUtc = at, DraftJson = graphJson, CreatedAtUtc = at, UpdatedAtUtc = at };
        db.FlowDefinitions.Add(definition);
        db.FlowVersions.Add(new() { Id = versionId, TenantId = 7, FlowId = flowId, VersionNumber = 1,
            GraphJson = graphJson, ConfigurationHash = FlowGraphSerialization.Hash(graph),
            PublishedBy = RecoveryOperator, PublishedAtUtc = at, ExecutionAuthorityJson = JsonSerializer.Serialize(authority, StepJson),
            ProtectedExecutionPinsJson = capture.ProtectedPinsJson });
        await db.SaveChangesAsync(ct);
        definition.PublishedVersionId = versionId; definition.PublishedVersionNumber = 1;
        await db.SaveChangesAsync(ct);
        var version = new FlowVersionDto(versionId, flowId, 7, 1, graph,
            FlowGraphSerialization.Hash(graph), RecoveryOperator, at);
        return new(version, script, trigger.Id, investigation.Id, repair.Id, rerun.Id, recovered.Id);
    }
}
