using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NetRatel.API.Services.AgentDirectory;
using NetRatel.Shared;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Persistence;
using NetRatel.API.Services.Orchestration;
using NetRatel.Akka.Observability;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.Application.Agents;
using NetRatel.Application.Fanout;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public interface IAkkaJobAuthorityService
{
    Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken);
    Task<JobRunInfo> StartManagedAsync(ulong jobId, RunJobRequest request, int recordedRequestId, ClaimsPrincipal principal, CancellationToken cancellationToken)
        => throw new InvalidOperationException("This job authority does not support managed invocation.");
    Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken);
    Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken);
    Task RecoverAsync(ulong jobRunId, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Runtime owner for a job run. It records every transition through
/// the Akka job router before projecting it into the existing PostgreSQL job
/// read model, and dispatches only to a fenced AgentJobGateway session.
/// </summary>
public sealed class AkkaJobAuthorityService(
    IJobDefinitionService jobDefinitions,
    IJobRunService jobRuns,
    JobTaskBridge taskBridge,
    IAgentJobGatewaySessionRegistry sessions,
    IJobRuntimeRouter jobRouter,
    IRealtimeFanoutSink fanout,
    JobAuthorityIdGenerator ids,
    IHostEnvironment environment,
    ManagedOrchestrationInvocationGuard? managedGuard = null,
    OrchestratorDbContext? db = null,
    TimeProvider? clock = null,
    NetRatelAkkaOptions? options = null) : IAkkaJobAuthorityService
{
    private const string Authority = "akka";
    private const string Feature = "jobs";
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly TimeSpan _undispatchedRecoveryGrace = (options ?? new NetRatelAkkaOptions()).AskTimeout;
    private IJobRunOwner? _owner;
    private OwnerKey? _acceptedPhysicalOwner;
    private JobRunMutationBoundary? _boundary;
    private readonly List<(IJobObservation Observation, JobMessageResult Result)> _committedFanout = [];
    private readonly Dictionary<ulong, long> _fixtureSequences = [];
    private readonly Dictionary<(ulong Run, ulong Step, ulong Sequence), string> _fixtureReceipts = [];

    private async Task<T> OwnAsync<T>(ulong runId, Func<CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        // Nonpersisting authority adapters retain the existing unit-fixture path.
        // Production DI supplies the scoped PostgreSQL context and actor router.
        if (db is null) return await operation(ct);
        return await jobRouter.ExecuteOwnedAsync(runId, async (owner, ownedCt) =>
        {
            await using var boundary = await JobRunMutationBoundary.AcquireAsync(db, runId, _clock, ownedCt);
            _owner = owner;
            _boundary = boundary;
            try
            {
                _acceptedPhysicalOwner = null;
                await boundary.BeginAsync(ownedCt);
                await owner.ReloadAsync(boundary.Observations, ownedCt);
                var result = await operation(ownedCt);
                await RequirePhysicalMutationDeadlineAsync(ownedCt);
                await boundary.CommitAsync(ownedCt);
                FlushCommittedFanout();
                return result;
            }
            finally { _committedFanout.Clear(); _owner = null; _boundary = null; _acceptedPhysicalOwner = null; }
        }, ct);
    }

    private async Task CommitBeforeTransportAsync(CancellationToken ct)
    {
        await RequirePhysicalMutationDeadlineAsync(ct);
        if (_boundary is not null) await _boundary.CommitAsync(ct);
        _acceptedPhysicalOwner = null;
        FlushCommittedFanout();
    }

    private async Task BeginAfterTransportAsync(CancellationToken ct)
    {
        if (_boundary is not null) await _boundary.BeginAsync(ct);
    }

    private async Task RequirePhysicalMutationDeadlineAsync(CancellationToken ct)
    {
        if (db is not null && _acceptedPhysicalOwner is { } owner &&
            !await ClientConnectionEpochStore.LockEffectiveOwnerAsync(db, owner, _clock, ct))
            throw new AgentJobGatewaySessionUnavailableException(owner.Client);
    }

    private sealed class JobTransitionRejectedException(JobMessageResult result)
        : InvalidOperationException($"Job authority lifecycle transition was rejected: {result.Disposition}.");

    public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
    {
        if (request.StartedBy?.Trim().StartsWith("service:", StringComparison.Ordinal) == true)
            throw new InvalidOperationException("Managed service identity requires the recorded ingress entry point.");
        return StartCoreAsync(jobId, request, null, null, cancellationToken);
    }

    public Task<JobRunInfo> StartManagedAsync(ulong jobId, RunJobRequest request, int recordedRequestId, ClaimsPrincipal principal, CancellationToken cancellationToken)
        => StartCoreAsync(jobId, request, recordedRequestId, principal, cancellationToken);

    private async Task<JobRunInfo> StartCoreAsync(ulong jobId, RunJobRequest request, int? recordedRequestId, ClaimsPrincipal? principal, CancellationToken cancellationToken)
    {
        var runId = ids.Next();
        if (db is not null && recordedRequestId is { } requestId)
        {
            var executionId = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking()
                .Where(x => x.RequestId == requestId).Select(x => x.ExecutionId).SingleAsync(cancellationToken);
            if (executionId is not null && !ulong.TryParse(executionId, out runId))
                throw new InvalidOperationException("The recorded run intent identifier is invalid.");
        }
        return await OwnAsync(runId, ct => StartOwnedAsync(jobId, request, recordedRequestId, principal, runId, ct), cancellationToken);
    }

    private async Task<JobRunInfo> StartOwnedAsync(ulong jobId, RunJobRequest request, int? recordedRequestId, ClaimsPrincipal? principal, ulong runId, CancellationToken cancellationToken)
    {
        var definition = await jobDefinitions.GetDetailsAsync(jobId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job {jobId} was not found.");
        jobRuns.BindSubmittedDefinition(runId, definition);
        if (recordedRequestId is { } ingressId)
        {
            if (managedGuard is null || principal is null) throw new InvalidOperationException("Managed invocation authority is unavailable.");
            await managedGuard.AuthorizeStartAsync(ingressId, definition.Job, principal, cancellationToken, runId);
        }
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
        var prepared = recordedRequestId is not null ? await jobRuns.GetAsync(runId, cancellationToken) : null;
        if (prepared is not null)
        {
            var control = await db!.Set<JobRunControlRecord>().SingleAsync(x => x.RunId == checked((long)runId), cancellationToken);
            if (prepared.Status != JobRunState.Pending || control.DispatchPreparedAtUtc is not null ||
                prepared.JobId != jobId || prepared.TenantId != client.TenantId || prepared.AgentId != client.AgentId ||
                prepared.StartedBy != request.StartedBy)
                throw new InvalidOperationException("The recorded run intent has already progressed or differs from this invocation.");
            if (control.CancellationRequestedAtUtc is not null)
                return await CancelUndispatchedRunAsync(prepared, control, cancellationToken);
        }
        if (!sessions.IsAvailable(client))
        {
            NetRatelAkkaTelemetry.RecordAuthorityFailure(Feature, Authority, environment.EnvironmentName);
            throw new AgentJobGatewaySessionUnavailableException(client);
        }
        // Owner reservation cannot accept a run. Validate the exact registered
        // physical owner inside the existing intent transaction before writes.
        _ = await RequireDispatchOwnerAsync(client, cancellationToken);
        var preparedOptions = prepared is null ? null : await jobRuns.GetExecutionOptionsAsync(prepared.Id, cancellationToken);
        var createdAt = prepared?.CreatedAtUtc ?? _clock.GetUtcNow();
        var runtimePolicy = prepared is null ? JobExecutionRuntimePolicy.FromJobAndRequest(definition.Job, request)
            : JobExecutionRuntimePolicy.FromOptions(preparedOptions);
        var executionEnvironment = prepared is null ? request.Environment ?? JobExecutionContextPolicy.FromJob(definition.Job)
            : prepared.Environment ?? JobExecutionContextPolicy.FromOptions(preparedOptions);
        if (executionEnvironment is { } selected && !Enum.IsDefined(selected))
            throw new InvalidOperationException("The selected execution environment is invalid.");
        var target = db is null ? null : await db.Agents.AsNoTracking()
            .SingleAsync(x => x.Id == agentId && x.TenantId == client.TenantId, cancellationToken);
        var presentation = target is null ? null : AgentDirectoryPresentation.Create(target.TenantId, target.Id, target.Name,
            target.IsEnabled, target.DeviceInfoJson, string.Empty, target.Tags, target.FriendlyName);
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
            prepared?.InputsJson ?? request.InputsJson,
            JobExecutionContextPolicy.MergeOptionsJson(JobExecutionRuntimePolicy.MergeOptionsJson(preparedOptions ?? definition.Job.OptionsJson, runtimePolicy), executionEnvironment), agentId,
            executionEnvironment, presentation?.DisplayName, presentation?.HostName, target?.Name), cancellationToken).ConfigureAwait(false);

        if (recordedRequestId is { } bindingRequestId) await managedGuard!.BindRunAsync(bindingRequestId, run, cancellationToken);
        if (db is not null && prepared is null)
        {
            db.Set<JobRunControlRecord>().Add(new() { RunId = checked((long)run.Id) });
            await db.SaveChangesAsync(cancellationToken);
        }

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
        catch (JobTransitionRejectedException)
        {
            await FailBeforeDispatchAsync(run, cancellationToken).ConfigureAwait(false);
            throw;
        }
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName); // scheduled
        // Pending is a committed, proven-undispatched intent. Recovery can close
        // this stage truthfully; it never starts a replacement physical command.
        await CommitBeforeTransportAsync(cancellationToken);
        await BeginAfterTransportAsync(cancellationToken);
        return await DispatchNextAsync(run.Id, client, nextVersion: 3, nextSequence: 3, cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
        => _ = await OwnAsync(lifecycle.JobRunId, async ct =>
        {
            await RecordLifecycleOwnedAsync(client, lifecycle, ct);
            return true;
        }, cancellationToken);

    private async Task RecordLifecycleOwnedAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
    {
        var run = await jobRuns.GetAsync(lifecycle.JobRunId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Job run {lifecycle.JobRunId} was not found.");
        RequireRunTarget(run, client);
        if (db is not null)
        {
            if (lifecycle.AuthenticatedOwner is not { } owner || owner.Client != client ||
                !await ClientConnectionEpochStore.LockEffectiveOwnerAsync(db, owner, _clock, cancellationToken))
                throw new AgentJobGatewaySessionUnavailableException(client);
            _acceptedPhysicalOwner = owner;
            if (await IsCommittedLifecycleRetryAsync(lifecycle, cancellationToken)) return;
            var dispatch = await db.Set<JobRunControlRecord>().AsNoTracking()
                .SingleAsync(x => x.RunId == checked((long)run.Id), cancellationToken);
            if (dispatch.DispatchOwnerConnectionId != owner.ConnectionId || dispatch.DispatchOwnerEpoch != owner.Epoch)
                throw new InvalidOperationException("The job lifecycle differs from its accepted dispatch owner.");
        }
        if (db is null && await IsCommittedLifecycleRetryAsync(lifecycle, cancellationToken)) return;
        if (IsTerminal(run.Status)) throw new InvalidOperationException("A terminal job run cannot accept new native lifecycle evidence.");
        if (lifecycle.LifecycleSequence == 0 || lifecycle.Version == 0)
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
        if (stepRun.JobStepId != lifecycle.JobStepId || lifecycle.CorrelationId != $"akka-job-{run.Id}")
            throw new InvalidOperationException("The job lifecycle definition or correlation does not match the dispatch.");
        var taskActivity = details.Activities.SingleOrDefault(x => x.RequestId == lifecycle.RequestId);
        if (taskActivity is null || taskActivity.CreatedAtUtc.UtcTicks / 10 != lifecycle.RequestedAtUtc.UtcTicks / 10)
            throw new InvalidOperationException("The job lifecycle request timestamp does not match the dispatch.");
        if (IsTerminal(stepRun.Status) ||
            lifecycle.Status == JobGatewayLifecycleStatus.Accepted && stepRun.Status != JobStepRunState.Pending ||
            lifecycle.Status == JobGatewayLifecycleStatus.Started && stepRun.StartedAtUtc is not null ||
            lifecycle.Status == JobGatewayLifecycleStatus.Progress && stepRun.StartedAtUtc is null && lifecycle.StartedAtUtc is null)
            throw new InvalidOperationException("The job lifecycle does not follow the recorded native execution state.");
        await PrepareLifecycleReceiptAsync(lifecycle, cancellationToken);
        await AppendLifecycleOutputAsync(run, stepRun, lifecycle, cancellationToken);
        NetRatelAkkaTelemetry.RecordAuthorityRequest(Feature, Authority, environment.EnvironmentName);
        using var activity = NetRatelAkkaTelemetry.StartAuthorityActivity(Feature, Authority, lifecycle.Status.ToString(), environment.EnvironmentName);

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
                await FinishRunAsync(run, stepRun, lifecycle, HasResultFlag(lifecycle.ResultJson, "outcomeUnknown") ? JobRunState.Unknown : HasResultFlag(lifecycle.ResultJson, "timedOut") ? JobRunState.TimedOut : JobRunState.Failed,
                    HasResultFlag(lifecycle.ResultJson, "outcomeUnknown") ? JobStepRunState.Unknown : HasResultFlag(lifecycle.ResultJson, "timedOut") ? JobStepRunState.TimedOut : JobStepRunState.Failed, cancellationToken).ConfigureAwait(false);
                break;
            case JobGatewayLifecycleStatus.Cancelled:
                await FinishRunAsync(run, stepRun, lifecycle, JobRunState.Cancelled, JobStepRunState.Cancelled, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new InvalidOperationException($"Unsupported agent job lifecycle status '{lifecycle.Status}'.");
        }

        NetRatelAkkaTelemetry.RecordAuthorityEvent(Feature, Authority, environment.EnvironmentName);
    }

    public async Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
        => await OwnAsync(jobRunId, ct => CancelOwnedAsync(jobRunId, reason, ct), cancellationToken);

    private async Task<bool> CancelOwnedAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
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
        var provenUndispatched = false;
        OwnerKey? cancellationOwner = null;
        if (db is not null)
        {
            var control = (await EnsureControlAsync(run.Id, cancellationToken))!;
            provenUndispatched = IsProvenUndispatched(run, control);
            if (control.DispatchOwnerConnectionId is { } connection && control.DispatchOwnerEpoch is > 0)
                cancellationOwner = new OwnerKey(client, connection, control.DispatchOwnerEpoch.Value);
            if (control.CancellationRequestedAtUtc is null)
            {
                control.CancellationRequestedAtUtc = _clock.GetUtcNow();
                control.CancellationReason = string.IsNullOrWhiteSpace(reason) ? "operator-cancelled" : reason[..Math.Min(reason.Length, 256)];
                control.Revision++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        // This acknowledges persisted intent. Native terminal evidence or the
        // immutable dispatch reconciliation boundary closes an unknown outcome.
        await CommitBeforeTransportAsync(cancellationToken);
        // A recorded Pending ingress has no native command to cancel. Persist
        // intent without enqueueing IO that could race its original start call.
        if (provenUndispatched) return true;
        // Intent is durable regardless of physical availability. Historical
        // unknown owner stays unknown; never route cancel to a new owner.
        if (sessions.IsAvailable(client) && (db is null || cancellationOwner is not null))
        {
            try
            {
                if (db is null)
                    await sessions.CancelAsync(client, jobRunId, reason, cancellationToken).ConfigureAwait(false);
                else
                    await sessions.CancelAsync(cancellationOwner!.Value, jobRunId, reason, cancellationToken).ConfigureAwait(false);
                await BeginAfterTransportAsync(cancellationToken);
                if (db is not null)
                {
                    var control = await db.Set<JobRunControlRecord>().SingleAsync(x => x.RunId == checked((long)run.Id), cancellationToken);
                    control.CancellationEnqueuedAtUtc = _clock.GetUtcNow();
                    control.Revision++;
                    await db.SaveChangesAsync(cancellationToken);
                }
            }
            catch (AgentJobGatewaySessionUnavailableException) { }
        }
        return true;
    }

    private async Task<JobRunInfo> DispatchNextAsync(ulong runId, ClientKey client, ulong nextVersion, ulong nextSequence, CancellationToken cancellationToken)
    {
        var details = await jobRuns.GetDetailsAsync(runId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job run {runId} was not found.");
        var run = details.Run;
        var dispatchControl = await EnsureControlAsync(run.Id, cancellationToken);
        if (dispatchControl?.CancellationRequestedAtUtc is not null && IsProvenUndispatched(run, dispatchControl))
            return await CancelUndispatchedRunAsync(run, dispatchControl, cancellationToken);
        var next = details.Steps.OrderBy(step => step.Ordinal).FirstOrDefault(step => step.Status == JobStepRunState.Pending);
        if (next is null)
        {
            var completedAt = _clock.GetUtcNow();
            var completed = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
                run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Succeeded, run.CurrentStepOrdinal,
                run.CreatedAtUtc, run.StartedAtUtc, completedAt, null, run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken).ConfigureAwait(false);
            await RecordRunAsync(completed, JobRunState.Succeeded, completed.CurrentStepOrdinal, completed.CreatedAtUtc, completed.StartedAtUtc, completed.CompletedAtUtc, checked((long)nextSequence), cancellationToken).ConfigureAwait(false);
            await MarkTerminalReadyAsync(completed.Id, cancellationToken);
            NetRatelAkkaTelemetry.JobAuthorityCompleted(Authority, environment.EnvironmentName);
            NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
            return completed;
        }

        var definition = await jobDefinitions.GetDetailsAsync(run.JobId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Job definition {run.JobId} was not found.");
        if (run.StartedBy.StartsWith("service:", StringComparison.Ordinal))
        {
            if (managedGuard is null) throw new InvalidOperationException("Managed invocation authority is unavailable.");
            try { await managedGuard.AuthorizeDispatchAsync(run, definition.Job, cancellationToken); }
            catch (ManagedOrchestrationGrantUnavailableException)
            {
                await FailBeforeDispatchAsync(run, cancellationToken, "The managed invocation grant became unavailable before dispatch.");
                await CommitBeforeTransportAsync(cancellationToken);
                throw;
            }
        }

        var step = definition.Steps.SingleOrDefault(item => item.Id == next.JobStepId)
            ?? throw new InvalidOperationException($"Job step {next.JobStepId} was not found.");
        var invocation = await taskBridge.BuildGatewayInvocationAsync(
            run,
            step,
            JobTemplateHelper.ParseInputs(await jobRuns.GetExecutionInputsAsync(run.Id, cancellationToken)),
            JobExecutionRuntimePolicy.FromOptions(await jobRuns.GetExecutionOptionsAsync(run.Id, cancellationToken)),
            cancellationToken).ConfigureAwait(false);
        if (!invocation.Success || string.IsNullOrWhiteSpace(invocation.TaskType) || invocation.PayloadJson is null)
        {
            await FailBeforeDispatchAsync(run, cancellationToken, invocation.Error ?? "The job step could not be translated for gateway execution.");
            return (await jobRuns.GetAsync(run.Id, cancellationToken))!;
        }

        // Preparation reads above do not authorize dispatch. The exact durable
        // owner FOR SHARE is held through this transaction's accepted intent.
        OwnerKey? dispatchOwner;
        try { dispatchOwner = await RequireDispatchOwnerAsync(client, cancellationToken); }
        catch (AgentJobGatewaySessionUnavailableException)
        {
            await FailBeforeDispatchAsync(run, cancellationToken, "The agent job gateway became unavailable before dispatch.");
            return (await jobRuns.GetAsync(run.Id, cancellationToken))!;
        }
        var now = _clock.GetUtcNow();
        var running = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy, JobRunState.Running, next.Ordinal,
            run.CreatedAtUtc, run.StartedAtUtc, null, null, run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken).ConfigureAwait(false);
        try
        {
            await RecordRunAsync(running, JobRunState.Running, next.Ordinal, running.CreatedAtUtc, running.StartedAtUtc, null, checked((long)(nextSequence - 1)), cancellationToken).ConfigureAwait(false);
        }
        catch (JobTransitionRejectedException)
        {
            await FailBeforeDispatchAsync(running, cancellationToken).ConfigureAwait(false);
            throw;
        }
        var activity = await jobRuns.CreateTaskActivityAsync(new CreateJobTaskActivityCommand(
            Guid.NewGuid().ToString("N"), run.Id, step.Id, run.ClientIdentity, run.TenantId, invocation.TaskType, "Pending", null, now, null, run.AgentId,
            Environment: run.Environment, TargetDisplayName: run.TargetDisplayName, TargetHostName: run.TargetHostName, TargetName: run.TargetName,
            ScriptName: invocation.ScriptName, ScriptType: invocation.ScriptType, CommandLabel: invocation.CommandLabel, ScriptContentHash: invocation.ScriptContentHash), cancellationToken).ConfigureAwait(false);
        var pendingStep = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
            next.Id, run.Id, step.Id, JobStepRunState.Pending, next.Ordinal, activity.RequestId, null, null, null), cancellationToken).ConfigureAwait(false);
        if (db is not null)
        {
            var control = await db.Set<JobRunControlRecord>().SingleAsync(x => x.RunId == checked((long)run.Id), cancellationToken);
            control.DispatchPreparedAtUtc = now;
            control.DispatchEnqueuedAtUtc = null;
            control.DispatchOwnerConnectionId = dispatchOwner!.Value.ConnectionId;
            control.DispatchOwnerEpoch = dispatchOwner.Value.Epoch;
            // Conservative, immutable dispatch-time reconciliation boundary.
            // Native payload timeout still starts at physical process execution.
            control.NativeDeadlineUtc = now.Add(JobExecutionRuntimePolicy.FromOptions(await jobRuns.GetExecutionOptionsAsync(run.Id, cancellationToken)).HardTimeout);
            control.Revision++;
            await db.SaveChangesAsync(cancellationToken);
        }
        await CommitBeforeTransportAsync(cancellationToken);
        try
        {
            if (run.StartedBy.StartsWith("service:", StringComparison.Ordinal))
                await managedGuard!.AuthorizeDispatchAsync(run, definition.Job, cancellationToken);
            await sessions.DispatchAsync(client, new JobGatewayStepDispatch(
                run.Id, step.Id, next.Id, next.Ordinal, activity.RequestId, $"akka-job-{run.Id}", invocation.TaskType, invocation.PayloadJson,
                Environment: (int)(run.Environment ?? ClientEnvironment.None), nextVersion, nextSequence, now, ExpectedOwner: dispatchOwner), cancellationToken).ConfigureAwait(false);
            await BeginAfterTransportAsync(cancellationToken);
            if (db is not null)
            {
                var control = await db.Set<JobRunControlRecord>().SingleAsync(x => x.RunId == checked((long)run.Id), cancellationToken);
                control.DispatchEnqueuedAtUtc = _clock.GetUtcNow();
                control.Revision++;
                await db.SaveChangesAsync(cancellationToken);
            }
        }
        catch (ManagedOrchestrationGrantUnavailableException)
        {
            await BeginAfterTransportAsync(cancellationToken);
            await FailBeforeDispatchAsync(running, cancellationToken, "The managed invocation grant became unavailable before dispatch.");
            await CommitBeforeTransportAsync(cancellationToken);
            throw;
        }
        catch (AgentJobGatewaySessionUnavailableException)
        {
            await BeginAfterTransportAsync(cancellationToken);
            await FailUndispatchedRunAsync(running, pendingStep, activity, nextSequence, cancellationToken).ConfigureAwait(false);
            await CommitBeforeTransportAsync(cancellationToken);
            throw;
        }

        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(1);
        return running;
    }

    private async Task<OwnerKey?> RequireDispatchOwnerAsync(ClientKey client, CancellationToken ct)
    {
        // The existing db-null path is a nonpersisting unit fixture. Production
        // DI supplies the same context used by JobRunMutationBoundary.
        if (db is null) return null;
        var owner = sessions.GetRegisteredOwner(client);
        if (owner is null || owner.Value.Client != client ||
            !await ClientConnectionEpochStore.LockEffectiveOwnerAsync(db, owner.Value, _clock, ct))
            throw new AgentJobGatewaySessionUnavailableException(client);
        _acceptedPhysicalOwner = owner;
        return owner;
    }

    private static bool IsProvenUndispatched(JobRunInfo run, JobRunControlRecord control) =>
        run.Status == JobRunState.Pending && control.DispatchPreparedAtUtc is null &&
        control.DispatchEnqueuedAtUtc is null && control.NativeDeadlineUtc is null;

    private async Task<JobRunInfo> CancelUndispatchedRunAsync(JobRunInfo run, JobRunControlRecord control, CancellationToken ct)
    {
        if (!IsProvenUndispatched(run, control) || control.CancellationRequestedAtUtc is null)
            throw new InvalidOperationException("Cancellation cannot prove this run was never dispatched.");
        const string error = "Cancellation was recorded before physical dispatch; no native command was started.";
        var now = _clock.GetUtcNow();
        var sequence = await NextOwnedSequenceAsync(run.Id, ct);
        if (sequence == 1)
            await RecordRunAsync(run, JobRunState.Pending, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, null, sequence++, ct);
        var details = await jobRuns.GetDetailsAsync(run.Id, ct)
            ?? throw new InvalidOperationException("The recorded cancelled run intent disappeared.");
        foreach (var step in details.Steps.Where(x => !IsTerminal(x.Status) && x.JobStepId is not null))
        {
            var closed = await jobRuns.UpsertStepRunAsync(new(step.Id, run.Id, step.JobStepId!.Value,
                JobStepRunState.Skipped, step.Ordinal, step.TaskRequestId, error, step.StartedAtUtc, now), ct);
            await RecordStepAsync(run, closed, sequence++, ct);
        }
        foreach (var activity in details.Activities.Where(x => !IsTerminalTaskActivity(x.Status)))
            await jobRuns.UpdateTaskActivityStatusAsync(new(activity.RequestId, "Cancelled", error, now), ct);
        var terminal = await jobRuns.UpsertRunAsync(new(run.Id, run.JobId, run.TenantId, run.ClientIdentity,
            run.StartedBy, JobRunState.Cancelled, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, now,
            error, run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), ct);
        await RecordRunAsync(terminal, terminal.Status, terminal.CurrentStepOrdinal, terminal.CreatedAtUtc,
            terminal.StartedAtUtc, terminal.CompletedAtUtc, sequence, ct);
        await MarkTerminalReadyAsync(run.Id, ct);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
        return terminal;
    }

    private async Task FailUndispatchedRunAsync(
        JobRunInfo running,
        JobStepRunInfo pendingStep,
        JobTaskActivityInfo activity,
        ulong nextSequence,
        CancellationToken cancellationToken)
    {
        const string error = "The agent job gateway became unavailable before dispatch.";
        var completedAt = _clock.GetUtcNow();
        var jobStepId = pendingStep.JobStepId
            ?? throw new InvalidOperationException("A dispatched job step must retain its definition identifier.");
        var failedStep = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
            pendingStep.Id, running.Id, jobStepId, JobStepRunState.Skipped, pendingStep.Ordinal,
            activity.RequestId, error, pendingStep.StartedAtUtc, completedAt), cancellationToken).ConfigureAwait(false);
        var failedRun = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            running.Id, running.JobId, running.TenantId, running.ClientIdentity, running.StartedBy,
            JobRunState.Failed, failedStep.Ordinal, running.CreatedAtUtc, running.StartedAtUtc,
            completedAt, error, running.InputsJson, running.OptionsJson, running.AgentId, running.Environment, running.TargetDisplayName, running.TargetHostName, running.TargetName), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            activity.RequestId, "Failed", error, completedAt), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(failedRun, failedStep, checked((long)nextSequence), cancellationToken).ConfigureAwait(false);
        await SkipRemainingStepsAsync(running, failedStep.Id, error, completedAt, cancellationToken);
        await RecordRunAsync(failedRun, JobRunState.Failed, failedRun.CurrentStepOrdinal, failedRun.CreatedAtUtc,
            failedRun.StartedAtUtc, failedRun.CompletedAtUtc, checked((long)nextSequence + 1), cancellationToken).ConfigureAwait(false);
        await MarkTerminalReadyAsync(failedRun.Id, cancellationToken);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task FailBeforeDispatchAsync(JobRunInfo run, CancellationToken cancellationToken, string error = "The job authority rejected the pre-dispatch lifecycle transition.")
    {
        var completedAt = _clock.GetUtcNow();
        var details = await jobRuns.GetDetailsAsync(run.Id, cancellationToken).ConfigureAwait(false);
        var closedSteps = new List<JobStepRunInfo>();
        if (details is not null)
        {
            foreach (var step in details.Steps.Where(step => !IsTerminal(step.Status) && step.JobStepId is not null))
            {
                var stepId = step.JobStepId ?? throw new InvalidOperationException("The nonterminal job step has no definition identifier.");
                closedSteps.Add(await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(
                    step.Id, step.JobRunId, stepId, JobStepRunState.Skipped, step.Ordinal,
                    step.TaskRequestId, error, step.StartedAtUtc, completedAt), cancellationToken).ConfigureAwait(false));
            }

            foreach (var activity in details.Activities.Where(activity => !IsTerminalTaskActivity(activity.Status)))
            {
                await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
                    activity.RequestId, "Failed", error, completedAt), cancellationToken).ConfigureAwait(false);
            }
        }

        var failed = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(
            run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            JobRunState.Failed, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc,
            completedAt, error, run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken).ConfigureAwait(false);
        if (db is not null)
        {
            var sequence = await NextOwnedSequenceAsync(run.Id, cancellationToken);
            foreach (var step in closedSteps)
                await RecordStepAsync(failed, step, sequence++, cancellationToken);
            await RecordRunAsync(failed, failed.Status, failed.CurrentStepOrdinal, failed.CreatedAtUtc, failed.StartedAtUtc,
                failed.CompletedAtUtc, sequence, cancellationToken);
            await MarkTerminalReadyAsync(failed.Id, cancellationToken);
        }
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task UpdateRunningAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken)
    {
        var now = LifecycleReceiptTime(lifecycle);
        var accepted = lifecycle.Status == JobGatewayLifecycleStatus.Accepted;
        var started = stepRun.StartedAtUtc ?? (lifecycle.Status == JobGatewayLifecycleStatus.Started || lifecycle.StartedAtUtc is not null ? now : (DateTimeOffset?)null);
        var step = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            accepted ? JobStepRunState.Pending : JobStepRunState.Running, stepRun.Ordinal, stepRun.TaskRequestId, null, started, null), cancellationToken).ConfigureAwait(false);
        var running = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            JobRunState.Running, stepRun.Ordinal, run.CreatedAtUtc, run.StartedAtUtc ?? started, null, null, run.InputsJson, run.OptionsJson, run.AgentId,
            run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new(lifecycle.RequestId, accepted ? "Accepted" : "Running", null, null,
            StartedAtUtc: started), cancellationToken);
        await RecordStepAsync(running, step, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
        if (run.StartedAtUtc != running.StartedAtUtc)
            await RecordRunAsync(running, running.Status, running.CurrentStepOrdinal, running.CreatedAtUtc, running.StartedAtUtc,
                null, checked((long)lifecycle.LifecycleSequence), cancellationToken);
    }

    private async Task CompleteStepAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, ClientKey client, CancellationToken cancellationToken)
    {
        var now = LifecycleReceiptTime(lifecycle);
        var started = stepRun.StartedAtUtc ?? (lifecycle.StartedAtUtc is not null ? now : (DateTimeOffset?)null);
        var completed = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            JobStepRunState.Succeeded, stepRun.Ordinal, stepRun.TaskRequestId, null, started, now), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            lifecycle.RequestId, "Completed", null, now, lifecycle.ResultJson, StartedAtUtc: started, ExitCode: lifecycle.ExitCode), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(run, completed, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
        var control = await EnsureControlAsync(run.Id, cancellationToken);
        var details = await jobRuns.GetDetailsAsync(run.Id, cancellationToken);
        if (control?.CancellationRequestedAtUtc is not null && details!.Steps.Any(x => x.Status == JobStepRunState.Pending))
        {
            const string reason = "Cancellation was requested after the current command completed; remaining steps were not dispatched.";
            await SkipRemainingStepsAsync(run, completed.Id, reason, now, cancellationToken);
            var cancelled = await jobRuns.UpsertRunAsync(new(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
                JobRunState.Cancelled, completed.Ordinal, run.CreatedAtUtc, run.StartedAtUtc ?? started, now, reason,
                run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken);
            await RecordRunAsync(cancelled, cancelled.Status, cancelled.CurrentStepOrdinal, cancelled.CreatedAtUtc,
                cancelled.StartedAtUtc, cancelled.CompletedAtUtc, 0, cancellationToken);
            await MarkTerminalReadyAsync(run.Id, cancellationToken);
            NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
            return;
        }
        await DispatchNextAsync(run.Id, client, lifecycle.Version + 2, lifecycle.LifecycleSequence + 2, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishRunAsync(JobRunInfo run, JobStepRunInfo stepRun, JobLifecycleUpdateEnvelope lifecycle, JobRunState runState, JobStepRunState stepState, CancellationToken cancellationToken, string? terminalReason = null)
    {
        var now = LifecycleReceiptTime(lifecycle);
        var started = stepRun.StartedAtUtc ?? (lifecycle.StartedAtUtc is not null ? now : (DateTimeOffset?)null);
        var summary = terminalReason ?? TaskResultSummary.Failure(lifecycle.ResultJson);
        var step = await jobRuns.UpsertStepRunAsync(new UpsertJobStepRunCommand(stepRun.Id, run.Id, stepRun.JobStepId ?? lifecycle.JobStepId,
            stepState, stepRun.Ordinal, stepRun.TaskRequestId, summary, started, now), cancellationToken).ConfigureAwait(false);
        await jobRuns.UpdateTaskActivityStatusAsync(new UpdateJobTaskActivityStatusCommand(
            lifecycle.RequestId, runState.ToString(), summary, now, lifecycle.ResultJson, StartedAtUtc: started, ExitCode: lifecycle.ExitCode), cancellationToken).ConfigureAwait(false);
        await RecordStepAsync(run, step, checked((long)lifecycle.LifecycleSequence), cancellationToken).ConfigureAwait(false);
        await SkipRemainingStepsAsync(run, step.Id, summary, now, cancellationToken);
        var terminal = await jobRuns.UpsertRunAsync(new UpsertJobRunCommand(run.Id, run.JobId, run.TenantId, run.ClientIdentity, run.StartedBy,
            runState, step.Ordinal, run.CreatedAtUtc, run.StartedAtUtc ?? started, now, summary, run.InputsJson, run.OptionsJson, run.AgentId,
            run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), cancellationToken).ConfigureAwait(false);
        await RecordRunAsync(terminal, runState, terminal.CurrentStepOrdinal, terminal.CreatedAtUtc, terminal.StartedAtUtc, terminal.CompletedAtUtc, checked((long)lifecycle.LifecycleSequence + 1), cancellationToken).ConfigureAwait(false);
        await MarkTerminalReadyAsync(terminal.Id, cancellationToken);
        NetRatelAkkaTelemetry.SetJobsAuthorityRunning(0);
    }

    private async Task SkipRemainingStepsAsync(JobRunInfo run, ulong completedStepId, string reason, DateTimeOffset completedAt, CancellationToken ct)
    {
        var details = await jobRuns.GetDetailsAsync(run.Id, ct) ?? throw new InvalidOperationException("The job projection disappeared.");
        foreach (var step in details.Steps.Where(x => x.Id != completedStepId && !IsTerminal(x.Status) && x.JobStepId is not null))
        {
            // Pending work without a dispatch request never acquired a physical outcome.
            var closed = await jobRuns.UpsertStepRunAsync(new(step.Id, run.Id, step.JobStepId!.Value,
                step.TaskRequestId is null ? JobStepRunState.Skipped : JobStepRunState.Unknown, step.Ordinal,
                step.TaskRequestId, reason, step.StartedAtUtc, completedAt), ct);
            await RecordStepAsync(run, closed, 0, ct);
            if (step.TaskRequestId is { } requestId)
                await jobRuns.UpdateTaskActivityStatusAsync(new(requestId, "Unknown", reason, completedAt), ct);
        }
    }

    private DateTimeOffset LifecycleReceiptTime(JobLifecycleUpdateEnvelope lifecycle) => lifecycle.ReceivedAtUtc ?? _clock.GetUtcNow();

    private static bool HasResultFlag(string? resultJson, string name)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return false;
        try
        {
            using var document = JsonDocument.Parse(resultJson);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static string LifecyclePayloadHash(JobLifecycleUpdateEnvelope lifecycle) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(lifecycle with { ReceivedAtUtc = null })))).ToLowerInvariant();

    private async Task<bool> IsCommittedLifecycleRetryAsync(JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct)
    {
        var hash = LifecyclePayloadHash(lifecycle);
        if (db is null)
        {
            if (!_fixtureReceipts.TryGetValue((lifecycle.JobRunId, lifecycle.JobStepRunId, lifecycle.LifecycleSequence), out var existing)) return false;
            if (existing != hash) throw new InvalidOperationException("The job lifecycle retry differs from its committed evidence.");
            return true;
        }
        var receipt = await db.Set<JobGatewayLifecycleReceiptRecord>().AsNoTracking().SingleOrDefaultAsync(x =>
            x.JobRunId == checked((long)lifecycle.JobRunId) && x.JobStepRunId == checked((long)lifecycle.JobStepRunId) &&
            x.LifecycleSequence == checked((long)lifecycle.LifecycleSequence), ct);
        if (receipt is null) return false;
        if (receipt.PayloadHash != hash || receipt.Version != checked((long)lifecycle.Version))
            throw new InvalidOperationException("The job lifecycle retry differs from its committed evidence.");
        return true;
    }

    private async Task PrepareLifecycleReceiptAsync(JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct)
    {
        var hash = LifecyclePayloadHash(lifecycle);
        if (db is null)
        {
            _fixtureReceipts[(lifecycle.JobRunId, lifecycle.JobStepRunId, lifecycle.LifecycleSequence)] = hash;
            return;
        }
        var latest = await db.Set<JobGatewayLifecycleReceiptRecord>().AsNoTracking().Where(x =>
            x.JobRunId == checked((long)lifecycle.JobRunId) && x.JobStepRunId == checked((long)lifecycle.JobStepRunId))
            .OrderByDescending(x => x.LifecycleSequence).FirstOrDefaultAsync(ct);
        if (latest is not null && (checked((long)lifecycle.LifecycleSequence) != latest.LifecycleSequence + 1 || checked((long)lifecycle.Version) != latest.Version + 1))
            throw new InvalidOperationException("The job lifecycle version or sequence is not the next recorded evidence.");
        db.Set<JobGatewayLifecycleReceiptRecord>().Add(new()
        {
            JobRunId = checked((long)lifecycle.JobRunId), JobStepRunId = checked((long)lifecycle.JobStepRunId),
            LifecycleSequence = checked((long)lifecycle.LifecycleSequence), Version = checked((long)lifecycle.Version),
            PayloadHash = hash, ReceivedAtUtc = LifecycleReceiptTime(lifecycle), RequestedAtUtc = lifecycle.RequestedAtUtc,
            ClientReportedAtUtc = lifecycle.StatusAtUtc, ClientStartedAtUtc = lifecycle.StartedAtUtc, LifecycleStatus = (int)lifecycle.Status
        });
        await db.SaveChangesAsync(ct);
    }

    private Task AppendLifecycleOutputAsync(JobRunInfo run, JobStepRunInfo step, JobLifecycleUpdateEnvelope lifecycle, CancellationToken ct) =>
        TaskLifecycleOutput.AppendAsync(jobRuns, new ClientKey(run.TenantId!.Value, run.AgentId!.Value), lifecycle.RequestId,
            lifecycle.ResultJson, LifecycleReceiptTime(lifecycle), ct);

    private static string ObservationClientIdentity(JobRunInfo run)
    {
        // The frozen observation contract keeps a required display identity;
        // current execution authority remains the run's tenant and Agent target.
        if (!string.IsNullOrWhiteSpace(run.ClientIdentity)) return run.ClientIdentity;
        if (run.TenantId is not { } tenantId || tenantId <= 0 ||
            run.AgentId is not { } agentId || agentId == Guid.Empty)
            throw new InvalidOperationException("A current tenant and Agent target are required for a job observation.");
        return FormattableString.Invariant($"agent:{tenantId}:{agentId:D}");
    }

    private async Task RecordRunAsync(JobRunInfo run, JobRunState status, int currentOrdinal, DateTimeOffset createdAtUtc, DateTimeOffset? startedAtUtc, DateTimeOffset? completedAtUtc, long sourceEventId, CancellationToken cancellationToken)
    {
        sourceEventId = await NextOwnedSequenceAsync(run.Id, cancellationToken);
        var observation = new JobRunObservation(
            sourceEventId, run.Id, run.JobId, run.TenantId, ObservationClientIdentity(run), run.StartedBy, status, currentOrdinal,
            createdAtUtc, startedAtUtc, completedAtUtc, _clock.GetUtcNow(), $"akka-job-authority:{run.Id}", true);
        var result = _owner is null ? await jobRouter.RecordAsync(new RecordJobObservation(observation), cancellationToken).ConfigureAwait(false)
            : await _owner.RecordAsync(observation, _boundary!.Observations, cancellationToken);
        RequireAccepted(result);
        if (db is null) _fixtureSequences[run.Id] = sourceEventId;
        PublishFanout(observation, result);
    }

    private async Task RecordStepAsync(JobRunInfo run, JobStepRunInfo step, long sourceEventId, CancellationToken cancellationToken)
    {
        sourceEventId = await NextOwnedSequenceAsync(run.Id, cancellationToken);
        var observation = new JobStepObservation(
            sourceEventId, run.Id, run.JobId, run.TenantId, ObservationClientIdentity(run), step.Id, step.JobStepId,
            step.Status, step.Ordinal, step.TaskRequestId, step.StartedAtUtc, step.CompletedAtUtc, _clock.GetUtcNow(),
            $"akka-job-authority:{run.Id}", true);
        var result = _owner is null ? await jobRouter.RecordAsync(new RecordJobObservation(observation), cancellationToken).ConfigureAwait(false)
            : await _owner.RecordAsync(observation, _boundary!.Observations, cancellationToken);
        RequireAccepted(result);
        if (db is null) _fixtureSequences[run.Id] = sourceEventId;
        PublishFanout(observation, result);
    }

    private void PublishFanout(IJobObservation observation, JobMessageResult result)
    {
        if (result.Disposition != JobMessageDisposition.Accepted)
        {
            return;
        }
        if (_owner is not null)
        {
            _committedFanout.Add((observation, result));
            return;
        }

        EnqueueFanout(observation, result);
    }

    private void FlushCommittedFanout()
    {
        foreach (var (observation, result) in _committedFanout) EnqueueFanout(observation, result);
        _committedFanout.Clear();
    }

    private void EnqueueFanout(IJobObservation observation, JobMessageResult result)
    {

        var envelope = RealtimeFanoutEnvelopeFactory.FromJob(observation, result);
        if (envelope is not null)
        {
            fanout.TryEnqueueBestEffort(envelope);
        }
    }

    private static void RequireRunTarget(JobRunInfo run, ClientKey client)
    {
        if (run.TenantId != client.TenantId || run.AgentId != client.AgentId)
        {
            throw new InvalidOperationException("The job lifecycle Agent does not own this run.");
        }
    }

    private static void RequireAccepted(JobMessageResult result)
    {
        if (result.Disposition is not JobMessageDisposition.Accepted and not JobMessageDisposition.Duplicate)
        {
            throw new JobTransitionRejectedException(result);
        }
    }

    private static bool IsTerminal(JobRunState state) =>
        state is JobRunState.Succeeded or JobRunState.Failed or JobRunState.Cancelled or JobRunState.TimedOut or JobRunState.Unknown;

    private static bool IsTerminal(JobStepRunState state) =>
        state is JobStepRunState.Succeeded or JobStepRunState.Failed or JobStepRunState.Skipped or JobStepRunState.Cancelled or JobStepRunState.TimedOut or JobStepRunState.Unknown;

    private static bool IsTerminalTaskActivity(string? status) =>
        string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Cancelled", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "TimedOut", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Unknown", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(status, "Skipped", StringComparison.OrdinalIgnoreCase);

    private async Task MarkTerminalReadyAsync(ulong runId, CancellationToken ct)
    {
        if (db is null) return;
        var details = await jobRuns.GetDetailsAsync(runId, ct) ?? throw new InvalidOperationException("The terminal projection disappeared.");
        var control = (await EnsureControlAsync(runId, ct))!;
        control.TerminalReadyAtUtc = _clock.GetUtcNow();
        control.TerminalResultHash = OrchestrationCallbackProjection.ResultHash(details);
        control.Revision++;
        await db.SaveChangesAsync(ct);
    }

    private async Task<JobRunControlRecord?> EnsureControlAsync(ulong runId, CancellationToken ct)
    {
        if (db is null) return null;
        var id = checked((long)runId);
        var control = await db.Set<JobRunControlRecord>().SingleOrDefaultAsync(x => x.RunId == id, ct);
        if (control is not null) return control;
        // Deployed ordinary runs predate this journal. Preserve their native
        // receipt/cancellation path without guessing an original deadline.
        control = new() { RunId = id };
        db.Set<JobRunControlRecord>().Add(control);
        await db.SaveChangesAsync(ct);
        return control;
    }

    private async Task<long> NextOwnedSequenceAsync(ulong runId, CancellationToken ct) =>
        db is null ? _fixtureSequences.GetValueOrDefault(runId) + 1 : (await db.JobShadowObservations.Where(x => x.JobRunId == runId)
            .MaxAsync(x => (long?)x.SourceEventId, ct) ?? 0) + 1;

    public async Task RecoverAsync(ulong jobRunId, CancellationToken cancellationToken)
        => _ = await OwnAsync(jobRunId, async ct =>
        {
            if (db is null) return false;
            var details = await jobRuns.GetDetailsAsync(jobRunId, ct);
            var control = await db.Set<JobRunControlRecord>().SingleOrDefaultAsync(x => x.RunId == checked((long)jobRunId), ct);
            if (details is null || control is null || IsTerminal(details.Run.Status)) return false;
            var run = details.Run;
            var now = _clock.GetUtcNow();
            var provenUndispatched = run.Status == JobRunState.Pending && control.DispatchPreparedAtUtc is null &&
                now >= run.CreatedAtUtc.Add(_undispatchedRecoveryGrace);
            var deadlineExpired = control.NativeDeadlineUtc is { } originalDeadline && now >= originalDeadline;
            var legacyCancellation = run.Status == JobRunState.Running && control.CancellationRequestedAtUtc is not null &&
                control.DispatchPreparedAtUtc is null && control.NativeDeadlineUtc is null;
            if (!provenUndispatched && !deadlineExpired && !legacyCancellation)
            {
                if (control.CancellationRequestedAtUtc is not null)
                    await CancelOwnedAsync(run.Id, control.CancellationReason ?? "operator-cancelled", ct);
                return false;
            }
            var status = provenUndispatched ? (control.CancellationRequestedAtUtc is not null ? JobRunState.Cancelled : JobRunState.Failed) : JobRunState.Unknown;
            var error = provenUndispatched ? "The process stopped before physical dispatch; the recorded run was not replayed." :
                legacyCancellation ? "Cancellation was durably requested for a historical run with no recorded dispatch deadline; the physical outcome is unknown and no dispatch was replayed." :
                "The original conservative dispatch reconciliation deadline elapsed without a durable native terminal receipt; the physical outcome is unknown and no dispatch was replayed.";
            var sequence = await NextOwnedSequenceAsync(run.Id, ct);
            if (sequence == 1)
                await RecordRunAsync(run, JobRunState.Pending, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, null, sequence++, ct);
            foreach (var step in details.Steps.Where(x => !IsTerminal(x.Status) && x.JobStepId is not null))
            {
                var closed = await jobRuns.UpsertStepRunAsync(new(step.Id, run.Id, step.JobStepId!.Value,
                    step.TaskRequestId is null || provenUndispatched ? JobStepRunState.Skipped : JobStepRunState.Unknown, step.Ordinal, step.TaskRequestId, error, step.StartedAtUtc, now), ct);
                await RecordStepAsync(run, closed, sequence++, ct);
            }
            foreach (var activity in details.Activities.Where(x => !IsTerminalTaskActivity(x.Status)))
                await jobRuns.UpdateTaskActivityStatusAsync(new(activity.RequestId, status == JobRunState.Cancelled ? "Cancelled" : provenUndispatched ? "Failed" : "Unknown", error, now), ct);
            var terminal = await jobRuns.UpsertRunAsync(new(run.Id, run.JobId, run.TenantId, run.ClientIdentity,
                run.StartedBy, status, run.CurrentStepOrdinal, run.CreatedAtUtc, run.StartedAtUtc, now, error, run.InputsJson, run.OptionsJson, run.AgentId, run.Environment, run.TargetDisplayName, run.TargetHostName, run.TargetName), ct);
            await RecordRunAsync(terminal, status, terminal.CurrentStepOrdinal, terminal.CreatedAtUtc, terminal.StartedAtUtc, now, sequence, ct);
            await MarkTerminalReadyAsync(run.Id, ct);
            return true;
        }, cancellationToken);
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
    int? ExitCode,
    OwnerKey? AuthenticatedOwner = null,
    DateTimeOffset? ReceivedAtUtc = null,
    DateTimeOffset? StartedAtUtc = null);

public enum JobGatewayLifecycleStatus { Accepted = 0, Started = 1, Progress = 2, Completed = 3, Failed = 4, Cancelled = 5 }

public sealed class JobAuthorityIdGenerator
{
    private long _next = DateTimeOffset.UtcNow.UtcTicks;
    public ulong Next() => checked((ulong)Interlocked.Increment(ref _next));
}
