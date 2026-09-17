using FluentAssertions;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AkkaJobAuthorityServiceTests
{
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
            new JobAuthorityIdGenerator(),
            new NetRatelAkkaMigrationOptions { Enabled = true, PresenceAuthorityEnabled = true, JobShadowEnabled = true, JobAuthorityEnabled = true },
            new TestHostEnvironment());

        var action = () => service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        await action.Should().ThrowAsync<AgentJobGatewaySessionUnavailableException>();
        runs.Run!.Status.Should().Be(JobRunState.Failed);
        runs.Run.Error.Should().Be("The agent job gateway became unavailable before dispatch.");
        runs.Step!.Status.Should().Be(JobStepRunState.Failed);
        runs.Activity!.Status.Should().Be("Failed");
    }

    [Fact]
    public async Task StartAsync_WhenInitialShadowTransitionIsRejected_TerminalizesThePersistedRunBeforeDispatch()
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
            new JobAuthorityIdGenerator(),
            new NetRatelAkkaMigrationOptions { Enabled = true, PresenceAuthorityEnabled = true, JobShadowEnabled = true, JobAuthorityEnabled = true },
            new TestHostEnvironment());

        var action = () => service.StartAsync(41, new RunJobRequest("test", null, null), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Job authority lifecycle transition was rejected: *");
        runs.Run!.Status.Should().Be(JobRunState.Failed);
        runs.Run.Error.Should().Be("The job authority rejected the pre-dispatch lifecycle transition.");
        runs.Step!.Status.Should().Be(JobStepRunState.Failed);
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
            new JobAuthorityIdGenerator(),
            new NetRatelAkkaMigrationOptions { Enabled = true, PresenceAuthorityEnabled = true, JobShadowEnabled = true, JobAuthorityEnabled = true },
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
            "test-correlation",
            JobGatewayLifecycleStatus.Failed,
            3,
            3,
            now.AddSeconds(-1),
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
            new JobAuthorityIdGenerator(),
            new NetRatelAkkaMigrationOptions { Enabled = true, PresenceAuthorityEnabled = true, JobShadowEnabled = true, JobAuthorityEnabled = true },
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
            "test-correlation",
            JobGatewayLifecycleStatus.Completed,
            3,
            3,
            now.AddSeconds(-1),
            now,
            100,
            result,
            0), CancellationToken.None);

        runs.Activity!.Status.Should().Be("Completed");
        runs.Activity.Error.Should().BeNull();
        runs.Activity.ResultJson.Should().Be(result);
    }

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
        public AgentJobGatewayRegistration Register(ClientKey _, Guid __, ulong ___, bool provisional = false) => throw new NotSupportedException();
        public bool IsAvailable(ClientKey _) => true;
        public Task DispatchAsync(ClientKey _, JobGatewayStepDispatch __, CancellationToken ___)
        {
            DispatchCount++;
            return Task.CompletedTask;
        }
        public Task CancelAsync(ClientKey _, ulong __, string ___, CancellationToken ____) => Task.CompletedTask;
    }

    private sealed class OneStepDefinitions : IJobDefinitionService
    {
        private readonly JobDefinitionInfo _definition;
        private static readonly JobStepInfo Step = new(42, 41, 1, JobStepKind.RunCommand, "sh", "true", null, null, true);
        public OneStepDefinitions(Guid agentId) => _definition = new(41, "test", "/", null, 7, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, agentId);
        public Task<JobDefinitionDetails?> GetDetailsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<JobDefinitionDetails?>(id == 41 ? new(_definition, [], [Step]) : null);
        public Task<IReadOnlyList<JobDefinitionInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobDefinitionInfo>>([_definition]);
        public Task<JobDefinitionInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult(id == 41 ? _definition : null);
        public Task<JobDefinitionInfo> CreateAsync(CreateJobDefinitionCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobDefinitionInfo?> UpdateAsync(UpdateJobDefinitionCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobDefinitionInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobParameterInfo>> ListParamsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobParameterInfo>>([]);
        public Task<JobParameterInfo?> AddParamAsync(AddJobParameterCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobParameterInfo?> UpdateParamAsync(UpdateJobParameterCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> DeleteParamAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<JobStepInfo>> ListStepsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobStepInfo>>([Step]);
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
        public JobRunInfo? Run { get; private set; }
        public JobStepRunInfo? Step { get; private set; }
        public JobTaskActivityInfo? Activity { get; private set; }
        public Task<JobRunInfo> UpsertRunAsync(UpsertJobRunCommand c, CancellationToken ct = default) => Task.FromResult(Run = new(c.RunId, c.JobId, c.TenantId, c.ClientIdentity, c.StartedBy, c.Status, c.CurrentStepOrdinal, c.CreatedAtUtc, c.StartedAtUtc, c.CompletedAtUtc, c.Error, c.InputsJson, c.OptionsJson, c.AgentId));
        public Task<JobStepRunInfo> UpsertStepRunAsync(UpsertJobStepRunCommand c, CancellationToken ct = default) => Task.FromResult(Step = new(c.StepRunId, c.JobRunId, c.JobStepId, c.Status, c.Ordinal, c.TaskRequestId, c.Error, c.StartedAtUtc, c.CompletedAtUtc));
        public Task<JobTaskActivityInfo> CreateTaskActivityAsync(CreateJobTaskActivityCommand c, CancellationToken ct = default) => Task.FromResult(Activity = new(1, c.RequestId, c.JobRunId, c.JobStepId, c.ClientIdentity, c.TenantId, c.TaskType, c.Status, c.Error, c.CreatedAtUtc, c.CompletedAtUtc, c.AgentId));
        public Task<JobTaskActivityInfo?> UpdateTaskActivityStatusAsync(UpdateJobTaskActivityStatusCommand c, CancellationToken ct = default)
        {
            Activity = Activity! with
            {
                Status = c.Status,
                Error = c.Error ?? Activity.Error,
                CompletedAtUtc = c.CompletedAtUtc,
                ResultJson = c.ResultJson ?? Activity.ResultJson
            };
            return Task.FromResult<JobTaskActivityInfo?>(Activity);
        }
        public Task<JobRunInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult(Run?.Id == id ? Run : null);
        public Task<JobRunDetails?> GetDetailsAsync(ulong id, CancellationToken ct = default) => Task.FromResult<JobRunDetails?>(Run?.Id == id && Step is not null ? new(Run, [Step], Activity is null ? [] : [Activity]) : Run?.Id == id ? new(Run, [], []) : null);
        public Task<IReadOnlyList<JobRunInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobRunInfo>>(Run is null ? [] : [Run]);
        public Task DeleteAsync(ulong id, CancellationToken ct = default) => Task.CompletedTask;
        public Task<JobTaskActivityInfo?> GetActivityByIdAsync(ulong id, CancellationToken ct = default) => Task.FromResult(Activity);
        public Task<JobTaskActivityInfo?> GetActivityByRequestIdAsync(string id, CancellationToken ct = default) => Task.FromResult(Activity);
        public Task<IReadOnlyList<JobTaskActivityInfo>> ListTaskActivitiesAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskActivityInfo>>(Activity is null ? [] : [Activity]);
        public Task<IReadOnlyList<JobTaskLogInfo>> GetLogsByRequestIdAsync(string id, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<JobTaskLogInfo>>([]);
        public Task<JobTaskActivityInfo> UpsertTaskActivityAsync(UpsertJobTaskActivityCommand c, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<JobTaskLogInfo> AppendTaskLogAsync(AppendJobTaskLogCommand c, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class AcceptingRouter : IJobShadowRouter
    {
        public Task<JobShadowMessageResult> RecordAsync(RecordJobShadowObservation m, CancellationToken ct) => Task.FromResult(new JobShadowMessageResult(m.Observation.JobRunId, JobShadowMessageDisposition.Accepted, null, 0, JobCommandCorrelationStatus.NotProvided, null));
        public Task<JobRunShadowState> GetStateAsync(ulong id, CancellationToken ct) => throw new NotSupportedException();
        public Task<JobShadowRouteStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RejectingRouter : IJobShadowRouter
    {
        public Task<JobShadowMessageResult> RecordAsync(RecordJobShadowObservation m, CancellationToken ct) => Task.FromResult(new JobShadowMessageResult(m.Observation.JobRunId, JobShadowMessageDisposition.InvalidTransition, null, 0, JobCommandCorrelationStatus.NotProvided, null));
        public Task<JobRunShadowState> GetStateAsync(ulong id, CancellationToken ct) => throw new NotSupportedException();
        public Task<JobShadowRouteStatus> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Development;
        public string ApplicationName { get; set; } = "NetRatel.Tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
