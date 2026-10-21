using AwesomeAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AkkaJobAuthorityServiceTests
{
    [Fact]
    public async Task StartAsync_WithNoEnabledSteps_CompletesWithoutDispatch()
    {
        var agentId = Guid.NewGuid();
        var runs = new InMemoryRuns();
        var gateway = new AvailableGateway();
        var service = CreateAuthority(agentId, runs, new OneStepDefinitions(agentId, stepCount: 0), gateway);

        var result = await service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        result.Status.Should().Be(JobRunState.Succeeded);
        runs.Run!.Status.Should().Be(JobRunState.Succeeded);
        runs.Steps.Should().BeEmpty();
        gateway.DispatchCount.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reviewed_job_keeps_confirmed_scripts_between_steps_or_fails_closed_when_snapshot_is_missing(bool loseSnapshot)
    {
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        const string content = "printf 'reviewed-marker'";
        var contentHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        var script = new ScriptInfo(91, "reviewed-script", "/", "fixture", content, null, "bash", now, now, 3);
        var steps = new[]
        {
            new JobStepInfo(42, 41, 1, JobStepKind.LibraryScript, null, null, 91, null, true),
            new JobStepInfo(43, 41, 2, JobStepKind.LibraryScript, null, null, 91, null, true)
        };
        var snapshot = new McpOperatorJobExecutionSnapshot(
            new JobDefinitionDetails(new JobDefinitionInfo(41, "reviewed-job", "/", null, 7, string.Empty, now, now, null, agentId), [], steps),
            2, "operator", "client", "https://mcp.example.test/mcp", "prod", new string('A', 64), [],
            [new McpOperatorScriptExecutionSnapshot(script, [new ScriptParamInfo(1, 91, "credential", "secret", false, null, null, null)], 3, contentHash,
                "/tmp/reviewed-script", 15)]);
        var runs = new InMemoryRuns();
        var gateway = new AvailableGateway();
        // The current definition is now a different shell job and the current
        // script no longer exists. Only the reviewed snapshot may be dispatched.
        var service = CreateAuthority(agentId, runs, new OneStepDefinitions(agentId), gateway);

        await service.StartReviewedMcpAsync(snapshot, new RunJobRequest("mcp-reviewed:operator", "{\"credential\":\"synthetic-only\"}", null), CancellationToken.None);

        gateway.DispatchCount.Should().Be(1);
        var first = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(gateway.LastDispatch!.PayloadJson)!;
        first.ScriptContent.Should().Be(content);
        first.WorkingDirectory.Should().Be("/tmp/reviewed-script");
        first.TimeoutSeconds.Should().Be(15);
        first.OutputPolicy!.SecretParameters.Should().Contain("credential");
        if (loseSnapshot) runs.McpSnapshot = null;
        await service.RecordLifecycleAsync(new ClientKey(7, agentId), Lifecycle(runs, JobGatewayLifecycleStatus.Completed, 3,
            "{\"exitCode\":0}", 0), CancellationToken.None);

        if (loseSnapshot)
        {
            gateway.DispatchCount.Should().Be(1);
            runs.Run!.Status.Should().Be(JobRunState.Failed);
            runs.Run.Error.Should().Contain("snapshot is unavailable");
        }
        else
        {
            gateway.DispatchCount.Should().Be(2);
            var second = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(gateway.LastDispatch!.PayloadJson)!;
            second.ScriptContent.Should().Be(content);
            second.WorkingDirectory.Should().Be("/tmp/reviewed-script");
            second.TimeoutSeconds.Should().Be(15);
            second.OutputPolicy!.SecretParameters.Should().Contain("credential");
            gateway.LastDispatch.JobStepId.Should().Be(43);
        }
    }

    [Theory]
    [InlineData("outcomeUnknown", "Unknown physical outcome; native termination could not be confirmed.")]
    [InlineData("timedOut", "Execution timed out.")]
    [InlineData("cancelled", "Execution cancelled.")]
    public void Confirmed_outcome_reason_precedes_earlier_stderr_warning(string flag, string expected)
    {
        var result = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["stderr"] = new[] { "Earlier harmless warning." },
            [flag] = true
        });
        TaskResultSummary.Failure(result).Should().Be(expected);
    }

    [Fact]
    public async Task AcceptedDurableTransitions_ArePublishedThroughTheCurrentSignalRFanout()
    {
        var fanout = new RecordingRealtimeFanoutSink();
        var service = new AkkaJobAuthorityService(
            new OneStepDefinitions(Guid.NewGuid()),
            new InMemoryRuns(),
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            new AvailableGateway(),
            new AcceptingRouter(),
            fanout,
            new JobAuthorityIdGenerator(),
            new TestHostEnvironment());

        await service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        fanout.Envelopes.Should().NotBeEmpty();
        fanout.Envelopes.Should().OnlyContain(envelope =>
            envelope.IsValid &&
            !envelope.IsAuthoritative &&
            envelope.Category == NetRatel.Application.Fanout.RealtimeFanoutCategory.Job);
    }

    [Fact]
    public async Task StartAsync_WhenGatewayDisappearsDuringDispatch_TerminalizesThePersistedRun()
    {
        var agentId = Guid.NewGuid();
        var client = new ClientKey(7, agentId);
        var runs = new InMemoryRuns();
        var service = new AkkaJobAuthorityService(
            new OneStepDefinitions(agentId),
            runs,
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            new VanishingGateway(client),
            new AcceptingRouter(),
            new RecordingRealtimeFanoutSink(),
            new JobAuthorityIdGenerator(),
            new TestHostEnvironment());

        var action = () => service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        await action.Should().ThrowAsync<AgentJobGatewaySessionUnavailableException>();
        runs.Run!.Status.Should().Be(JobRunState.Failed);
        runs.Run.Error.Should().Be("The agent job gateway became unavailable before dispatch.");
        runs.Step!.Status.Should().Be(JobStepRunState.Skipped);
        runs.Activity!.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task StartAsync_WhenInitialJobTransitionIsRejected_TerminalizesThePersistedRunBeforeDispatch()
    {
        var agentId = Guid.NewGuid();
        var runs = new InMemoryRuns();
        var gateway = new AvailableGateway();
        var service = new AkkaJobAuthorityService(
            new OneStepDefinitions(agentId),
            runs,
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            gateway,
            new RejectingRouter(),
            new RecordingRealtimeFanoutSink(),
            new JobAuthorityIdGenerator(),
            new TestHostEnvironment());

        var action = () => service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Job authority lifecycle transition was rejected: *");
        runs.Run!.Status.Should().Be(JobRunState.Failed);
        runs.Run.Error.Should().Be("The job authority rejected the pre-dispatch lifecycle transition.");
        runs.Step!.Status.Should().Be(JobStepRunState.Skipped);
        gateway.DispatchCount.Should().Be(0);
    }

    [Fact]
    public async Task RecordLifecycleAsync_PersistsTerminalResultSeparatelyFromFailureSummary()
    {
        var agentId = Guid.NewGuid();
        var client = new ClientKey(7, agentId);
        var runs = new InMemoryRuns();
        var service = new AkkaJobAuthorityService(
            new OneStepDefinitions(agentId),
            runs,
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            new AvailableGateway(),
            new AcceptingRouter(),
            new RecordingRealtimeFanoutSink(),
            new JobAuthorityIdGenerator(),
            new TestHostEnvironment());
        await service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);
        var run = runs.Run!;
        var step = runs.Step!;
        var activity = runs.Activity!;
        const string result = "{\"stderr\":[\"permission denied\"],\"exitCode\":1}";
        var now = DateTimeOffset.UtcNow;

        await service.RecordLifecycleAsync(client, new JobLifecycleUpdateEnvelope(
            run.Id,
            step.JobStepId ?? 0,
            step.Id,
            step.Ordinal,
            activity.RequestId,
             $"akka-job-{run.Id}",
            JobGatewayLifecycleStatus.Failed,
            3,
            3,
            activity.CreatedAtUtc,
            now,
            0,
            result,
            1), CancellationToken.None);

        runs.Activity!.Status.Should().Be("Failed");
        runs.Activity.Error.Should().Be("permission denied");
        runs.Activity.ResultJson.Should().Be(result);
    }

    [Fact]
    public async Task RecordLifecycleAsync_PersistsCompletedResultWithoutInventingAnError()
    {
        var agentId = Guid.NewGuid();
        var client = new ClientKey(7, agentId);
        var runs = new InMemoryRuns();
        var service = new AkkaJobAuthorityService(
            new OneStepDefinitions(agentId),
            runs,
            new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            new AvailableGateway(),
            new AcceptingRouter(),
            new RecordingRealtimeFanoutSink(),
            new JobAuthorityIdGenerator(),
            new TestHostEnvironment());
        await service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);
        var run = runs.Run!;
        var step = runs.Step!;
        var activity = runs.Activity!;
        const string result = "{\"stdout\":[\"completed\"],\"exitCode\":0}";
        var now = DateTimeOffset.UtcNow;

        await service.RecordLifecycleAsync(client, new JobLifecycleUpdateEnvelope(
            run.Id,
            step.JobStepId ?? 0,
            step.Id,
            step.Ordinal,
            activity.RequestId,
             $"akka-job-{run.Id}",
            JobGatewayLifecycleStatus.Completed,
            3,
            3,
            activity.CreatedAtUtc,
            now,
            100,
            result,
            0), CancellationToken.None);

        runs.Activity!.Status.Should().Be("Completed");
        runs.Activity.Error.Should().BeNull();
        runs.Activity.ResultJson.Should().Be(result);
    }

    [Fact]
    public async Task AcceptedAndStarted_AdvanceActivityWithoutInventingDispatchStart_DespiteAgentClockSkew()
    {
        var agent = Guid.NewGuid(); var runs = new InMemoryRuns(); var service = CreateAuthority(agent, runs);
        await service.StartAsync(41, new("test", null, null), CancellationToken.None);
        runs.Run!.StartedAtUtc.Should().BeNull(); runs.Step!.StartedAtUtc.Should().BeNull();
        var accepted = Lifecycle(runs, JobGatewayLifecycleStatus.Accepted, 3) with { StatusAtUtc = DateTimeOffset.UtcNow.AddHours(-4) };
        await service.RecordLifecycleAsync(new(7, agent), accepted, CancellationToken.None);
        runs.Activity!.Status.Should().Be("Accepted"); runs.Activity.StartedAtUtc.Should().BeNull(); runs.Step!.Status.Should().Be(JobStepRunState.Pending);
        await service.RecordLifecycleAsync(new(7, agent), Lifecycle(runs, JobGatewayLifecycleStatus.Started, 4), CancellationToken.None);
        runs.Activity.Status.Should().Be("Running"); runs.Activity.StartedAtUtc.Should().NotBeNull(); runs.Run.StartedAtUtc.Should().NotBeNull();
    }

    [Theory]
    [InlineData(JobGatewayLifecycleStatus.Failed, JobRunState.Failed, JobStepRunState.Failed, "Failed")]
    [InlineData(JobGatewayLifecycleStatus.Cancelled, JobRunState.Cancelled, JobStepRunState.Cancelled, "Cancelled")]
    public async Task NativeTerminalBeforeStart_PreservesNullEvidenceAndSkipsLaterSteps(JobGatewayLifecycleStatus status, JobRunState runStatus, JobStepRunState stepStatus, string activityStatus)
    {
        var agent = Guid.NewGuid(); var runs = new InMemoryRuns(); var service = CreateAuthority(agent, runs, new OneStepDefinitions(agent, 2));
        await service.StartAsync(41, new("test", null, null), CancellationToken.None);
        await service.RecordLifecycleAsync(new(7, agent), Lifecycle(runs, status, 3, "{\"error\":\"validation rejected\"}", null), CancellationToken.None);
        runs.Run!.Status.Should().Be(runStatus); runs.Run.StartedAtUtc.Should().BeNull();
        runs.Steps.OrderBy(x => x.Ordinal).Select(x => x.Status).Should().Equal(stepStatus, JobStepRunState.Skipped);
        runs.Steps.Should().OnlyContain(x => x.StartedAtUtc == null);
        runs.Activity!.Status.Should().Be(activityStatus); runs.Activity.ExitCode.Should().BeNull(); runs.Activity.StartedAtUtc.Should().BeNull();
    }

    [Fact]
    public async Task PersistedRuntimeOverridesAndEnvironment_AreUsedForThePhysicalDispatch()
    {
        var agent = Guid.NewGuid(); var runs = new InMemoryRuns(); var gateway = new AvailableGateway();
        var service = CreateAuthority(agent, runs, new OneStepDefinitions(agent, optionsJson: "{\"executionEnvironment\":2,\"executionPolicy\":{\"expectedRuntimeSeconds\":30,\"hardTimeoutSeconds\":50}}"), gateway);
        await service.StartAsync(41, new("test", null, null, 70, 10, 90), CancellationToken.None);
        using var payload = System.Text.Json.JsonDocument.Parse(gateway.LastDispatch!.PayloadJson);
        payload.RootElement.GetProperty("TimeoutSeconds").GetInt32().Should().Be(90);
        gateway.LastDispatch.Environment.Should().Be(2); runs.Run!.Environment.Should().Be(NetRatel.Shared.ClientEnvironment.Prod);
        runs.Activity!.Environment.Should().Be(NetRatel.Shared.ClientEnvironment.Prod);
    }

    [Fact]
    public async Task ExactLifecycleRetries_DoNotDuplicateOutputOrDispatchAndChangedEvidenceIsRejected()
    {
        var agent = Guid.NewGuid(); var runs = new InMemoryRuns(); var gateway = new AvailableGateway(); var service = CreateAuthority(agent, runs, gateway: gateway);
        await service.StartAsync(41, new("test", null, null), CancellationToken.None);
        await service.RecordLifecycleAsync(new(7, agent), Lifecycle(runs, JobGatewayLifecycleStatus.Started, 3), CancellationToken.None);
        var progress = Lifecycle(runs, JobGatewayLifecycleStatus.Progress, 4, "{\"stdout\":[\"first line\"],\"stderr\":[],\"outputSequence\":1}") ;
        await service.RecordLifecycleAsync(new(7, agent), progress, CancellationToken.None);
        await service.RecordLifecycleAsync(new(7, agent), progress with { ReceivedAtUtc = DateTimeOffset.UtcNow.AddSeconds(5) }, CancellationToken.None);
        runs.Logs.Should().ContainSingle().Which.Message.Should().Be("first line");
        var changed = () => service.RecordLifecycleAsync(new(7, agent), progress with { ResultJson = "{}" }, CancellationToken.None);
        await changed.Should().ThrowAsync<InvalidOperationException>().WithMessage("*retry differs*");
        var terminal = Lifecycle(runs, JobGatewayLifecycleStatus.Completed, 5, "{\"stdout\":[\"first line\"],\"exitCode\":0,\"outputStreamed\":true}", 0);
        await service.RecordLifecycleAsync(new(7, agent), terminal, CancellationToken.None);
        await service.RecordLifecycleAsync(new(7, agent), terminal, CancellationToken.None);
        runs.Logs.Should().ContainSingle(); gateway.DispatchCount.Should().Be(1); runs.Activity!.ExitCode.Should().Be(0);
    }

    private static AkkaJobAuthorityService CreateAuthority(Guid agent, InMemoryRuns runs, IJobDefinitionService? definitions = null, AvailableGateway? gateway = null) =>
        new(definitions ?? new OneStepDefinitions(agent), runs, new JobTaskBridge(new EmptyScripts(), NullLogger<JobTaskBridge>.Instance),
            gateway ?? new AvailableGateway(), new AcceptingRouter(), new RecordingRealtimeFanoutSink(), new JobAuthorityIdGenerator(), new TestHostEnvironment());

    private static JobLifecycleUpdateEnvelope Lifecycle(InMemoryRuns runs, JobGatewayLifecycleStatus status, ulong sequence, string? result = null, int? exitCode = null) =>
        new(runs.Run!.Id, runs.Step!.JobStepId!.Value, runs.Step.Id, runs.Step.Ordinal, runs.Activity!.RequestId,
             $"akka-job-{runs.Run.Id}", status, sequence, sequence, runs.Activity.CreatedAtUtc, DateTimeOffset.UtcNow, 0, result, exitCode);

    private sealed class VanishingGateway(ClientKey client) : IAgentJobGatewaySessionRegistry
    {
        public AgentJobGatewayRegistration Register(ClientKey _, Guid __, ulong ___, bool provisional = false) => throw new NotSupportedException();
        public bool IsAvailable(ClientKey candidate) => candidate == client;
        public Task DispatchAsync(ClientKey _, JobGatewayStepDispatch __, CancellationToken ___) => throw new AgentJobGatewaySessionUnavailableException(client);
        public Task CancelAsync(ClientKey _, ulong __, string ___, CancellationToken ____) => Task.CompletedTask;
    }

    private sealed class AvailableGateway : IAgentJobGatewaySessionRegistry
    {
        public int DispatchCount { get; private set; }
        public JobGatewayStepDispatch? LastDispatch { get; private set; }
        public AgentJobGatewayRegistration Register(ClientKey _, Guid __, ulong ___, bool provisional = false) => throw new NotSupportedException();
        public bool IsAvailable(ClientKey _) => true;
        public Task DispatchAsync(ClientKey _, JobGatewayStepDispatch dispatch, CancellationToken ___)
        {
            LastDispatch = dispatch;
            DispatchCount++;
            return Task.CompletedTask;
        }
        public Task CancelAsync(ClientKey _, ulong __, string ___, CancellationToken ____) => Task.CompletedTask;
    }

    private sealed class OneStepDefinitions : IJobDefinitionService
    {
        private readonly JobDefinitionInfo _definition;
        private readonly IReadOnlyList<JobStepInfo> _steps;
        private static readonly JobStepInfo Step = new(42, 41, 1, JobStepKind.RunCommand, "sh", "true", null, null, true);
        public OneStepDefinitions(Guid agentId, int stepCount = 1, string? optionsJson = null)
        {
            _definition = new(41, "test", "/", null, 7, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, optionsJson, agentId);
            _steps = Enumerable.Range(1, stepCount).Select(i => Step with { Id = (ulong)(41 + i), Ordinal = i }).ToList();
        }
        public Task<JobDefinitionDetails?> GetDetailsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<JobDefinitionDetails?>(id == 41 ? new(_definition, [], _steps) : null);
        public Task<IReadOnlyList<JobDefinitionInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobDefinitionInfo>>([_definition]);
        public Task<JobDefinitionInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult(id == 41 ? _definition : null);
        public Task<JobDefinitionInfo> CreateAsync(CreateJobDefinitionCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobDefinitionInfo?> UpdateAsync(UpdateJobDefinitionCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobDefinitionInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobParameterInfo>> ListParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobParameterInfo>>([]);
        public Task<JobParameterInfo?> AddParamAsync(AddJobParameterCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobParameterInfo?> UpdateParamAsync(UpdateJobParameterCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteParamAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobStepInfo>> ListStepsAsync(ulong id, CancellationToken ct = default) => Task.FromResult(_steps);
        public Task<JobStepInfo?> AddStepAsync(AddJobStepCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobStepInfo?> UpdateStepAsync(UpdateJobStepCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobStepInfo?> ReorderStepAsync(ulong id, int ordinal, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteStepAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class EmptyScripts : IScriptService
    {
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptInfo>>([]);
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult<ScriptInfo?>(null);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptParamInfo>>([]);
        public Task<ScriptInfo> CreateAsync(CreateScriptCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? raw, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class InMemoryRuns : IJobRunService
    {
        public McpOperatorJobExecutionSnapshot? McpSnapshot { get; set; }
        public void BindMcpExecutionSnapshot(ulong runId, McpOperatorJobExecutionSnapshot snapshot) => McpSnapshot = snapshot;
        public Task<McpOperatorJobExecutionSnapshot?> GetMcpExecutionSnapshotAsync(ulong runId, CancellationToken ct = default) => Task.FromResult(McpSnapshot);
        public async Task<string?> GetExecutionOptionsAsync(ulong id, CancellationToken ct = default) => (await GetAsync(id, ct))?.OptionsJson;
        public async Task<string?> GetExecutionInputsAsync(ulong id, CancellationToken ct = default) => (await GetAsync(id, ct))?.InputsJson;
        public JobRunInfo? Run { get; private set; }
        public List<JobStepRunInfo> Steps { get; } = [];
        public List<JobTaskLogInfo> Logs { get; } = [];
        public JobStepRunInfo? Step => Steps.OrderBy(x => x.Ordinal).FirstOrDefault();
        public JobTaskActivityInfo? Activity { get; private set; }
        public Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand c, CancellationToken ct = default) => Task.FromResult(Run = new(c.RunId, c.JobId, c.TenantId, c.ClientIdentity, c.StartedBy, c.Status, c.CurrentStepOrdinal, c.CreatedAtUtc, c.StartedAtUtc, c.CompletedAtUtc, c.Error, c.InputsJson, c.OptionsJson, c.AgentId, c.Environment, c.TargetDisplayName, c.TargetHostName, c.TargetName));
        public Task<JobStepRunInfo> UpsertStepRunAsync(UpsertJobStepRunCommand c, CancellationToken ct = default)
        {
            var step = new JobStepRunInfo(c.StepRunId, c.JobRunId, c.JobStepId, c.Status, c.Ordinal, c.TaskRequestId, c.Error, c.StartedAtUtc, c.CompletedAtUtc);
            Steps.RemoveAll(x => x.Id == c.StepRunId); Steps.Add(step); return Task.FromResult(step);
        }
        public Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand c, CancellationToken ct = default) => Task.FromResult(Activity = new(1, c.RequestId, c.JobRunId, c.JobStepId, c.ClientIdentity, c.TenantId, c.TaskType, c.Status, c.Error, c.CreatedAtUtc, c.CompletedAtUtc, c.AgentId, c.ResultJson, c.Environment, c.StartedAtUtc, c.ExitCode, c.TargetDisplayName, c.TargetHostName, c.TargetName, c.ScriptName, c.ScriptType, c.CommandLabel, c.ScriptContentHash));
        public Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand c, CancellationToken ct = default)
        {
            Activity = Activity! with
            {
                Status = c.Status,
                Error = c.Error ?? Activity.Error,
                CompletedAtUtc = c.CompletedAtUtc,
                ResultJson = c.ResultJson ?? Activity.ResultJson,
                StartedAtUtc = c.StartedAtUtc ?? Activity.StartedAtUtc,
                ExitCode = c.ExitCode ?? Activity.ExitCode
            };
            return Task.FromResult<JobTaskActivityInfo?>(Activity);
        }
        public Task<JobRunInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult(Run?.Id == id ? Run : null);
        public Task<JobRunDetails?> GetDetailsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<JobRunDetails?>(Run?.Id == id && Step is not null ? new(Run, Steps.ToArray(), Activity is null ? [] : [Activity]) : Run?.Id == id ? new(Run, [], []) : null);
        public Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobRunInfo>>(Run is null ? [] : [Run]);
        public Task DeleteAsync(ulong id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong id, CancellationToken ct = default) => Task.FromResult(Activity);
        public Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string id, CancellationToken ct = default) => Task.FromResult(Activity);
        public Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskActivityInfo>>(Activity is null ? [] : [Activity]);
        public Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskLogInfo>>(Logs.Where(x => x.RequestId == id).ToList());
        public Task<JobTaskActivityInfo> UpsertTaskActivityAsync(UpsertJobTaskActivityCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand c, CancellationToken ct = default)
        {
            var existing = Logs.FirstOrDefault(x => x.RequestId == c.RequestId && x.Sequence == c.Sequence && x.Stream == c.Stream);
            if (existing is not null) return Task.FromResult(existing);
            var log = new JobTaskLogInfo(Logs.Count + 1, c.RequestId, c.JobTaskActivityId, c.ClientIdentity, c.TenantId, c.Stream, c.Message, c.Sequence, c.TimestampUtc);
            Logs.Add(log); return Task.FromResult(log);
        }
    }

    private sealed class AcceptingRouter : IJobRuntimeRouter
    {
        public Task<JobMessageResult> RecordAsync(RecordJobObservation m, CancellationToken ct) => Task.FromResult(new JobMessageResult(m.Observation.JobRunId, JobMessageDisposition.Accepted, null, 0, JobCommandCorrelationStatus.NotProvided, null));
        public Task<JobRunView> GetStateAsync(ulong id, CancellationToken ct) => throw new NotSupportedException();
        public Task<JobRuntimeStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RejectingRouter : IJobRuntimeRouter
    {
        public Task<JobMessageResult> RecordAsync(RecordJobObservation m, CancellationToken ct) => Task.FromResult(new JobMessageResult(m.Observation.JobRunId, JobMessageDisposition.InvalidTransition, null, 0, JobCommandCorrelationStatus.NotProvided, null));
        public Task<JobRunView> GetStateAsync(ulong id, CancellationToken ct) => throw new NotSupportedException();
        public Task<JobRuntimeStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingRealtimeFanoutSink : IRealtimeFanoutSink
    {
        public List<NetRatel.Application.Fanout.RealtimeFanoutEnvelope> Envelopes { get; } = [];

        public bool TryEnqueue(NetRatel.Application.Fanout.RealtimeFanoutEnvelope envelope)
        {
            Envelopes.Add(envelope);
            return true;
        }

        public RealtimeFanoutHealthStatus GetStatus() =>
            new(false, (ulong)Envelopes.Count, 0, 0, 0, 0, 0, 0, 0, 0, null, null, null, null, null);
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
