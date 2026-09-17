using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public interface IAkkaJobAuthorityService
{
    Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken);
    Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken);
    Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken);
}

/// <summary>
/// DEV-only runtime owner for a job run.  It records every transition through
/// the Akka job router before projecting it into the existing PostgreSQL job
/// read model, and dispatches only to a fenced AgentJobGateway session.
/// </summary>
public sealed class AkkaJobAuthorityService(
    IJobDefinitionService jobDefinitions,
    IJobRunService jobRuns,
    JobTaskBridge taskBridge,
    IAgentJobGatewaySessionRegistry sessions,
    IJobShadowRouter jobRouter,
    JobAuthorityIdGenerator ids,
    NetRatelAkkaMigrationOptions options,
    IHostEnvironment environment) : IAkkaJobAuthorityService
{
    private const string Authority = "akka";
    private const string Feature = "jobs";

    private sealed class JobShadowTransitionRejectedException(JobShadowMessageResult result)
        : InvalidOperationException($"Job authority lifecycle transition was rejected: {result.Disposition}.");

    public async Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
    {
        if (!options.IsJobAuthorityActive)
        {
            throw new InvalidOperationException("The job authority canary is disabled.");
        }

        var definition = await jobDefinitions.GetDetailsAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job {jobId} was not found.");
        if (!definition.Job.TenantId.HasValue)
        {
            throw new InvalidOperationException("Job authority requires a tenant-bound job definition.");
        }

        var unsupportedStep = definition.Steps.FirstOrDefault(step =>
            step.Enabled && step.Type is not (JobStepKind.RunCommand or JobStepKind.LibraryScript));
        if (unsupportedStep is not null)
        {
            throw new InvalidOperationException(
                $"Job authority supports RunCommand and LibraryScript steps only; step {unsupportedStep.Id} is {unsupportedStep.Type}.");
        }

        if (definition.Job.AgentId is not { } agentId)
        {
            throw new InvalidOperationException("Job authority requires a current Agent target. Retarget this legacy job before running it.");
        }

        var clientIdentity = definition.Job.ClientIdentity;
        var client = new ClientKey(definition.Job.TenantId.Value, agentId);
        if (!sessions.IsAvailable(client))
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            throw new AgentJobGatewaySessionUnavailableException(client);
        }

        var createdAt = DateTimeOffset.UtcNow;
        var runId = ids.Next();
        var runtimePolicy = JobExecutionRuntimePolicy.FromJobAndRequest(definition.Job, request);
        var run = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            runId,
            definition.Job.Id,
            definition.Job.TenantId,
            clientIdentity,
            string.IsNullOrWhiteSpace(request.StartedBy) ? "akka:job-authority" : request.StartedBy.Trim(),
            JobRunState.Pending,
            0,
            createdAt,
            null,
            null,
            null,
            request.InputsJson,
            JobExecutionRuntimePolicy.MergeOptionsJson(definition.Job.OptionsJson, runtimePolicy), agentId), cancellationToken).ConfigureAwait(false);

        foreach (var step in definition.Steps.Where(step => step.Enabled).OrderBy(step => step.Ordinal))
        {
            await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
                ids.Next(),
                run.Id,
                step.Id,
                JobStepRunState.Pending,
                step.Ordinal,
                null,
                null,
                null,
                null), cancellationToken).ConfigureAwait(false);
        }

        try
        {
            await RecordRunAsync(run, JobRunState.Pending, 0, createdAt, null, null, sourceEventId: 1, cancellationToken).ConfigureAwait(false); // queued
        }
        catch (JobShadowTransitionRejectedException)
        {
            await FailBeforeDispatchAsync(run, cancellationToken).ConfigureAwait(false);
            throw;
        }
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName); // scheduled
        return await DispatchNextAsync(run.Id, client, nextVersion: 3, nextSequence: 3, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
    {
        var run = await jobRuns.GetAsync(lifecycle.JobRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job run {lifecycle.JobRunId} was not found.");
        RequireRunTarget(run, client);
        if (lifecycle.LifecycleSequence == 0 || lifecycle.Version == 0 || lifecycle.StatusAtUtc < lifecycle.RequestedAtUtc)
        {
            throw new InvalidOperationException("The job lifecycle version, sequence, or timestamps are invalid.");
        }

        var details = await jobRuns.GetDetailsAsync(run.Id, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job run {run.Id} read model disappeared.");
        var stepRun = details.Steps.SingleOrDefault(step => step.Id == lifecycle.JobStepRunId && step.Ordinal == lifecycle.Ordinal)
            ?? throw new InvalidOperationException("The job lifecycle does not match a dispatched step.");
        if (!string.Equals(stepRun.TaskRequestId, lifecycle.RequestId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The job lifecycle request does not match the dispatched step.");
        }

        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, lifecycle.Status.ToString(), fallbackUsed: false, environment.EnvironmentName);

        switch (lifecycle.Status)
        {
            case JobGatewayLifecycleStatus.Accepted:
            case JobGatewayLifecycleStatus.Started:
            case JobGatewayLifecycleStatus.Progress:
                await UpdateRunningAsync(run, stepRun, lifecycle, cancellationToken).ConfigureAwait(false);
                break;
            case JobGatewayLifecycleStatus.Completed:
                await CompleteStepAsync(run, stepRun, lifecycle, client, cancellationToken).ConfigureAwait(false);
                break;
            case JobGatewayLifecycleStatus.Failed:
                await FinishRunAsync(run, stepRun, lifecycle, JobRunState.Failed, JobStepRunState.Failed, cancellationToken).ConfigureAwait(false);
                break;
            case JobGatewayLifecycleStatus.Cancelled:
                await FinishRunAsync(run, stepRun, lifecycle, JobRunState.Cancelled, JobStepRunState.Failed, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported agent job lifecycle status '{lifecycle.Status}'.");
        }

        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
    }

    public async Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
    {
        var run = await jobRuns.GetAsync(jobRunId, cancellationToken).ConfigureAwait(false);
        if (run is null || IsTerminal(run.Status))
        {
            return false;
        }

        if (run.TenantId is not { } tenantId || run.AgentId is not { } agentId)
        {
            throw new InvalidOperationException("The job run has no current Agent target.");
        }
        var client = new ClientKey(tenantId, agentId);
        if (!sessions.IsAvailable(client))
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, fallbackUsed: false, environment.EnvironmentName);
            return false;
        }

        await sessions.CancelAsync(client, jobRunId, reason, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<JobRunInfo> DispatchNextAsync(ulong runId, ClientKey client, ulong nextVersion, ulong nextSequence, CancellationToken cancellationToken)
    {
        var details = await jobRuns.GetDetailsAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job run {runId} was not found.");
        var run = details.Run;
        var definition = await jobDefinitions.GetDetailsAsync(run.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job definition {run.JobId} was not found.");
        var next = details.Steps.OrderBy(step => step.Ordinal).FirstOrDefault(step => step.Status == JobStepRunState.Pending);
        if (next is null)
        {
            var completedAt = DateTimeOffset.UtcNow;
            var completed = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
                run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Succeeded, run.CurrentStepOrdinal,
                run.CreatedAtUtc, run.StartedAtUtc ?? completedAt, completedAt, null, run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
            await RecordRunAsync(completed, JobRunState.Succeeded, completed.CurrentStepOrdinal, completed.CreatedAtUtc, completed.StartedAtUtc, completed.CompletedAtUtc, checked((long)nextSequence), cancellationToken).ConfigureAwait(false);
            NetRatelAkkaTelemetry.JobAuthorityCompleted(Authority, fallbackUsed: false, environment.EnvironmentName);
            NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
            return completed;
        }

        var step = definition.Steps.SingleOrDefault(item => item.Id == next.JobStepId)
            ?? throw new InvalidOperationException($"Job step {next.JobStepId} was not found.");
        var invocation = await taskBridge.BuildGatewayInvocationAsync(
            run,
            step,
            JobTemplateHelper.ParseInputs(run.InputsJson),
            JobExecutionRuntimePolicy.FromJob(definition.Job),
            cancellationToken).ConfigureAwait(false);
        if (!invocation.Success || string.IsNullOrWhiteSpace(invocation.TaskType) || invocation.PayloadJson is null)
        {
            throw new InvalidOperationException(invocation.Error ?? "The job step could not be translated for gateway execution.");
        }

        var now = DateTimeOffset.UtcNow;
        var running = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Running, next.Ordinal,
            run.CreatedAtUtc, run.StartedAtUtc ?? now, null, null, run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
        try
        {
            await RecordRunAsync(running, JobRunState.Running, next.Ordinal, running.CreatedAtUtc, running.StartedAtUtc, null, checked((long)(nextSequence - 1)), cancellationToken).ConfigureAwait(false);
        }
        catch (JobShadowTransitionRejectedException)
        {
            await FailBeforeDispatchAsync(running, cancellationToken).ConfigureAwait(false);
            throw;
        }
        var activity = await jobRuns.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            Guid.NewGuid().ToString("N"), run.Id, step.Id, run.ClientIdentity, run.TenantId, invocation.TaskType, "Pending", null, now, null, run.AgentId), cancellationToken).ConfigureAwait(false);
        var pendingStep = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
            next.Id, run.Id, step.Id, JobStepRunState.Pending, next.Ordinal, activity.RequestId, null, null, null), cancellationToken).ConfigureAwait(false);
        try
        {
            await sessions.DispatchAsync(client, new JobGatewayStepDispatch(
                run.Id, step.Id, next.Id, next.Ordinal, activity.RequestId, $"akka-job-{run.Id}", invocation.TaskType, invocation.PayloadJson,
                Environment: 0, nextVersion, nextSequence, now), cancellationToken).ConfigureAwait(false);
        }
        catch (AgentJobGatewaySessionUnavailableException)
        {
            await FailUndispatchedRunAsync(running, pendingStep, activity, nextSequence, cancellationToken).ConfigureAwait(false);
            throw;
        }

        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(1);
        return running;
    }

    private async Task FailUndispatchedRunAsync(
        JobRunInfo running,
        JobStepRunInfo pendingStep,
        JobTaskActivityInfo activity,
        ulong nextSequence,
        CancellationToken cancellationToken)
    {
        const string error = "The agent job gateway became unavailable before dispatch.";
        var completedAt = DateTimeOffset.UtcNow;
        var jobStepId = pendingStep.JobStepId
            ?? throw new InvalidOperationException("A dispatched job step must retain its definition identifier.");
        var failedStep = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
            pendingStep.Id, running.Id, jobStepId, JobStepRunState.Failed, pendingStep.Ordinal,
            activity.RequestId, error, pendingStep.StartedAtUtc ?? completedAt, completedAt), cancellationToken).ConfigureAwait(false);
        var failedRun = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            running.Id, running.JobId, running.TenantId, running.ClientIdentity, running.StartedBy,
            JobRunState.Failed, failedStep.Ordinal, running.CreatedAtUtc, running.StartedAtUtc ?? completedAt,
            completedAt, error, running.InputsJson, running.OptionsJson, running.AgentId), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            activity.RequestId, "Failed", error, completedAt), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(failedRun, failedStep, checked((long)nextSequence), cancellationToken).ConfigureAwait(false);
        await RecordRunAsync(failedRun, JobRunState.Failed, failedRun.CurrentStepOrdinal, failedRun.CreatedAtUtc,
            failedRun.StartedAtUtc, failedRun.CompletedAtUtc, checked((long)nextSequence + 1), cancellationToken).ConfigureAwait(false);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task FailBeforeDispatchAsync(JobRunInfo run, CancellationToken cancellationToken)
    {
        const string error = "The job authority rejected the pre-dispatch lifecycle transition.";
        var completedAt = DateTimeOffset.UtcNow;
        var details = await jobRuns.GetDetailsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        if (details is not null)
        {
            foreach (var step in details.Steps.Where(step => !IsTerminal(step.Status) && step.JobStepId is not null))
            {
                await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
                    step.Id, step.JobRunId, step.JobStepId.Value, JobStepRunState.Failed, step.Ordinal,
                    step.TaskRequestId, error, step.StartedAtUtc ?? completedAt, completedAt), cancellationToken).ConfigureAwait(false);
            }

            foreach (var activity in details.Activities.Where(activity => !IsTerminalTaskActivity(activity.Status)))
            {
                await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
                    activity.RequestId, "Failed", error, completedAt), cancellationToken).ConfigureAwait(false);
            }
        }

        await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            JobRunState.Failed, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc ?? completedAt,
            completedAt, error, run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task UpdateRunningAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
    {
        var now = lifecycle.StatusAtUtc;
        await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            JobStepRunState.Running, stepRun.Ordinal, stepRun.TaskRequestId, null, stepRun.StartedAtUtc ?? now, null), cancellationToken).ConfigureAwait(false);
        var running = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            JobRunState.Running, stepRun.Ordinal, run.CreatedAtUtc, run.StartedAtUtc ?? now, null, null, run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(running, stepRun with { Status = JobStepRunState.Running, StartedAtUtc = stepRun.StartedAtUtc ?? now }, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
    }

    private async Task CompleteStepAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, ClientKey client, CancellationToken cancellationToken)
    {
        var now = lifecycle.StatusAtUtc;
        var completed = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            JobStepRunState.Succeeded, stepRun.Ordinal, stepRun.TaskRequestId, null, stepRun.StartedAtUtc ?? now, now), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            lifecycle.RequestId,
            "Completed",
            null,
            now,
            lifecycle.ResultJson), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(run, completed, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
        await DispatchNextAsync(run.Id, client, lifecycle.Version + 2, lifecycle.LifecycleSequence + 2, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishRunAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, JobRunState runState, JobStepRunState stepState, CancellationToken cancellationToken)
    {
        var now = lifecycle.StatusAtUtc;
        var step = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            stepState, stepRun.Ordinal, stepRun.TaskRequestId, lifecycle.ResultJson, stepRun.StartedAtUtc ?? now, now), cancellationToken).ConfigureAwait(false);
        var terminal = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            runState, step.Ordinal, run.CreatedAtUtc, run.StartedAtUtc ?? now, now, lifecycle.ResultJson, run.InputsJson, run.OptionsJson, run.AgentId), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            lifecycle.RequestId,
            runState == JobRunState.Cancelled ? "Cancelled" : "Failed",
            TaskResultSummary.Failure(lifecycle.ResultJson),
            now,
            lifecycle.ResultJson), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(terminal, step, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
        await RecordRunAsync(terminal, runState, terminal.CurrentStepOrdinal, terminal.CreatedAtUtc, terminal.StartedAtUtc, terminal.CompletedAtUtc, checked((long)lifecycle.LifecycleSequence + 1), cancellationToken).ConfigureAwait(false);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task RecordRunAsync(JobRunInfo run, JobRunState status, int currentOrdinal, DateTimeOffset createdAtUtc, DateTimeOffset? startedAtUtc, DateTimeOffset? completedAtUtc, long sourceEventId, CancellationToken cancellationToken)
    {
        var result = await jobRouter.RecordAsync(new RecordJobShadowObservation(new JobRunShadowObservation(
            sourceEventId, run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, status, currentOrdinal,
            createdAtUtc, startedAtUtc, completedAtUtc, DateTimeOffset.UtcNow, $"akka-job-authority:{run.Id}", true)), cancellationToken).ConfigureAwait(false);
        RequireAccepted(result);
    }

    private async Task RecordStepAsync(JobRunInfo run, JobStepRunInfo step, long sourceEventId, CancellationToken cancellationToken)
    {
        var result = await jobRouter.RecordAsync(new RecordJobShadowObservation(new JobStepShadowObservation(
            sourceEventId, run.Id, run.JobId, run.TenantId, run.ClientIdentity, step.Id, step.JobStepId,
            step.Status, step.Ordinal, step.TaskRequestId, step.StartedAtUtc, step.CompletedAtUtc, DateTimeOffset.UtcNow,
            $"akka-job-authority:{run.Id}", true)), cancellationToken).ConfigureAwait(false);
        RequireAccepted(result);
    }

    private static void RequireRunTarget(JobRunInfo run, ClientKey client)
    {
        if (run.TenantId != client.TenantId || run.AgentId != client.AgentId)
        {
            throw new InvalidOperationException("The job lifecycle Agent does not own this run.");
        }
    }

    private static void RequireAccepted(JobShadowMessageResult result)
    {
        if (result.Disposition is not JobShadowMessageDisposition.Accepted and not JobShadowMessageDisposition.Duplicate)
        {
            throw new JobShadowTransitionRejectedException(result);
        }
    }

    private static bool IsTerminal(JobRunState state) =>
        state is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut;

    private static bool IsTerminal(JobStepRunState state) =>
        state is JobStepRunState.Succeeded or JobStepRunState.Failed or JobStepRunState.Skipped;

    private static bool IsTerminalTaskActivity(string? status) =>
        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "TimedOut", StringComparison.OrdinalIgnoreCase);
}

public sealed class UnavailableAkkaJobAuthorityService : IAkkaJobAuthorityService
{
    public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
        => Task.FromException<JobRunInfo>(new InvalidOperationException("The Akka job authority is unavailable."));

    public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
        => Task.FromException(new InvalidOperationException("The Akka job authority is unavailable."));

    public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken) => Task.FromResult(false);
}

public sealed record JobLifecycleUpdateEnvelope(
    ulong JobRunId,
    ulong JobStepId,
    ulong JobStepRunId,
    int Ordinal,
    string RequestId,
    string CorrelationId,
    JobGatewayLifecycleStatus Status,
    ulong Version,
    ulong LifecycleSequence,
    DateTimeOffset RequestedAtUtc,
    DateTimeOffset StatusAtUtc,
    double ProgressPercent,
    string? ResultJson,
    int ExitCode);

public enum JobGatewayLifecycleStatus { Accepted, Started, Progress, Completed, Failed, Cancelled }

public sealed class JobAuthorityIdGenerator
{
    private long _next = DateTimeOffset.UtcNow.UtcTicks;
    public ulong Next() => checked((ulong)Interlocked.Increment(ref _next));
}
