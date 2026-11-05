using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.Application.Jobs;
using NetRatel.Application.Flows;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Services.Flows;

/// <summary>
/// Canonical library-script admission reuses the existing task payload and command authority.
/// A durable send claim is never replayed after an ambiguous process or transport boundary.
/// </summary>
public sealed class KeyedScriptExecutionService(OrchestratorDbContext db, IExecutionAdmissionStore admissions,
    IJobRunService runs, FlowExecutionAuthority authority, IAgentCommandAuthorityDispatcher commands, TimeProvider clock, IScriptExecutionReplayStore? replayPins = null)
    : IKeyedScriptExecutionService
{
    private sealed record Submission(KeyedScriptAdmissionRequest Request, string PayloadJson, TaskOutputPolicy OutputPolicy);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<KeyedScriptAdmissionResult> StartKeyedAsync(KeyedScriptAdmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        request.Identity.Validate();
        Validate(request);
        await authority.RequireScriptAsync(request.Identity, request.Snapshot.Script.Id, request.AgentId, cancellationToken);
        var parameters = Parameters(request);
        var prepared = await AgentTaskEndpoints.BuildPayloadAsync(new()
        {
            TenantId = request.Identity.TenantId, AgentId = request.AgentId, ScriptId = checked((int)request.Snapshot.Script.Id),
            TaskType = TaskKinds.ExecLibraryScript, Parameters = parameters, TimeoutSeconds = request.TimeoutSeconds,
            WorkingDirectory = request.WorkingDirectory, Environment = request.Environment
        }, TaskKinds.ExecLibraryScript, new PinnedScript(request.Snapshot), cancellationToken);
        if (prepared.Error is not null || prepared.Value is null || prepared.OutputPolicy is null)
            throw new InvalidOperationException("The canonical library-script payload is unavailable.");
        var invocation = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(prepared.Value)!;
        var outputPolicy = prepared.OutputPolicy with
        { Binding = request.Identity.ActionKey.ToString("N"), ScriptContentHash = request.Snapshot.ContentHash };
        invocation.OutputPolicy = outputPolicy;
        var normalized = request with { Parameters = parameters };
        return await AdmitPreparedAsync(new(normalized, JsonSerializer.Serialize(invocation), outputPolicy),
            request.FlowLease, cancellationToken);
    }

    public async Task<KeyedScriptAdmissionResult> StartReplayAsync(ExecutionAdmissionIdentity identity, FlowRunLease lease,
        FlowExecutionReplayDescriptorDto replay, CancellationToken cancellationToken = default)
    {
        identity.Validate();
        if (lease.Event.ExecutionEvent is not { Outcome: FlowExecutionOutcome.Failed or FlowExecutionOutcome.TimedOut } source ||
            source.Replay != replay || !replay.Supported || replay.SourceKind != FlowExecutionSourceKind.Script ||
            replay.InputsReference != "script-execution:" + replay.SourceExecutionId ||
            identity.Causation?.SourceExecutionId != replay.SourceExecutionId || identity.Causation.RecoveryAttempt < 1)
            throw new ExecutionAdmissionDeniedException("The original script replay is not a trusted recoverable execution.");
        var original = replayPins is null ? null : await replayPins.ResolveAsync(identity.TenantId, replay.SourceExecutionId, cancellationToken);
        if (original is null)
        {
            var prior = await admissions.FindByExecutionAsync(ExecutionAdmissionKind.LibraryScript, replay.SourceExecutionId, cancellationToken);
            if (prior?.Identity.TenantId != identity.TenantId)
                throw new NotSupportedException("The protected original script replay material is unavailable.");
            var priorSubmission = JsonSerializer.Deserialize<Submission>(prior.PayloadJson, Json)
                ?? throw new NotSupportedException("The protected original script replay material is unavailable.");
            original = new(identity.TenantId, priorSubmission.Request.AgentId, priorSubmission.Request.Snapshot,
                priorSubmission.Request.Environment, priorSubmission.PayloadJson, priorSubmission.OutputPolicy);
        }
        original.Validate();
        var pin = original.Script;
        if (original.AgentId != replay.Target.AgentId || pin.Script.Id.ToString(CultureInfo.InvariantCulture) != replay.Definition.Id ||
            pin.Script.SourceRevision.ToString(CultureInfo.InvariantCulture) != replay.Definition.Revision ||
            !string.Equals(pin.ContentHash, replay.Definition.ContentHash, StringComparison.OrdinalIgnoreCase))
            throw new ExecutionAdmissionDeniedException("The original script replay descriptor differs from its protected material.");
        await authority.RequireScriptAsync(identity, pin.Script.Id, original.AgentId, cancellationToken);
        var invocation = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(original.NativePayloadJson)!;
        var outputPolicy = original.OutputPolicy with { Binding = identity.ActionKey.ToString("N") };
        invocation.OutputPolicy = outputPolicy;
        var request = new KeyedScriptAdmissionRequest(identity, pin, original.AgentId, invocation.Parameters ?? [],
            original.Environment, invocation.TimeoutSeconds ?? 3600, invocation.WorkingDirectory);
        Validate(request);
        // Preserve the original native shell, parameter presence and runtime options exactly.
        // Only the output binding changes because this is one deliberate new execution.
        return await AdmitPreparedAsync(new(request, JsonSerializer.Serialize(invocation), outputPolicy), lease, cancellationToken);
    }

    /// <summary>Retries only a provably unstarted protected admission, preserving its exact native payload and key.</summary>
    public async Task<KeyedScriptAdmissionResult> ResumeAsync(ExecutionAdmissionIdentity identity, string executionId,
        FlowRunLease lease, CancellationToken cancellationToken = default)
    {
        identity.Validate();
        var binding = await admissions.FindByExecutionAsync(ExecutionAdmissionKind.LibraryScript, executionId, cancellationToken);
        if (binding is null || binding.Identity != identity || binding.Kind != ExecutionAdmissionKind.LibraryScript)
            throw new ExecutionAdmissionDeniedException("The protected script execution binding is unavailable.");
        var submitted = JsonSerializer.Deserialize<Submission>(binding.PayloadJson, Json)
            ?? throw new InvalidOperationException("The protected script admission is unavailable.");
        if (submitted.Request.Identity != identity)
            throw new ExecutionAdmissionDeniedException("The protected script execution identity differs.");
        Validate(submitted.Request);
        await authority.RequireScriptAsync(identity, submitted.Request.Snapshot.Script.Id, submitted.Request.AgentId, cancellationToken);
        return await AdmitPreparedAsync(submitted, lease, cancellationToken);
    }

    private async Task<KeyedScriptAdmissionResult> AdmitPreparedAsync(Submission proposal, FlowRunLease? lease,
        CancellationToken cancellationToken)
    {
        var request = proposal.Request;
        var invocation = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(proposal.PayloadJson)
            ?? throw new InvalidOperationException("The pinned canonical script payload is unavailable.");
        var binding = await admissions.ReserveAsync(new(request.Identity, ExecutionAdmissionKind.LibraryScript,
            JsonSerializer.Serialize(proposal, Json)),
            () => Guid.NewGuid().ToString("N"), cancellationToken);
        var submitted = JsonSerializer.Deserialize<Submission>(binding.PayloadJson, Json)
            ?? throw new InvalidOperationException("The protected script admission is unavailable.");
        if (submitted.Request.Identity != binding.Identity || binding.Kind != ExecutionAdmissionKind.LibraryScript)
            throw new InvalidOperationException("The protected script admission identity differs.");
        Validate(submitted.Request);
        var activity = await runs.GetActivityByRequestIdAsync(binding.ExecutionId, cancellationToken);
        if (activity is not null && (activity.TenantId != binding.Identity.TenantId ||
            activity.AgentId != submitted.Request.AgentId || activity.TaskType != TaskKinds.ExecLibraryScript ||
            !string.Equals(activity.ScriptContentHash, submitted.Request.Snapshot.ContentHash, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("The existing execution differs from its protected admission.");
        if (activity is not null)
        {
            var classification = await db.JobTaskActivities.AsNoTracking().Where(row => row.RequestId == binding.ExecutionId)
                .Select(row => row.OutputProtectionJson).SingleAsync(cancellationToken);
            if (classification != JsonSerializer.Serialize(submitted.OutputPolicy))
                throw new InvalidOperationException("The canonical output classification differs from its protected admission.");
        }
        if (binding.DispatchStartedAtUtc is not null)
            return new(binding, activity, DispatchUncertain: binding.DispatchAcceptedAtUtc is null && binding.AuthorityDeniedAtUtc is null,
                AuthorityDenied: binding.AuthorityDeniedAtUtc is not null);
        var client = new ClientKey(binding.Identity.TenantId, submitted.Request.AgentId);
        if (!commands.IsAvailable(client)) return new(binding, activity);
        var claimed = await admissions.TryBeginDispatchAsync(binding, async ct =>
        {
            await authority.RequireFirstDispatchAsync(binding.Identity, lease, ct);
            await authority.RequireScriptAsync(binding.Identity, submitted.Request.Snapshot.Script.Id, submitted.Request.AgentId, ct);
            var target = await db.Agents.AsNoTracking().SingleAsync(agent => agent.Id == client.AgentId &&
                agent.TenantId == client.TenantId, ct);
            var created = await runs.CreateTaskActivityAsync(new(binding.ExecutionId, null, null, string.Empty,
                binding.Identity.TenantId, TaskKinds.ExecLibraryScript, "Pending", null, clock.GetUtcNow(), null,
                AgentId: submitted.Request.AgentId, Environment: submitted.Request.Environment,
                TargetDisplayName: target.FriendlyName ?? target.Name, TargetHostName: target.Name,
                ScriptName: submitted.Request.Snapshot.Script.Name, ScriptType: invocation.ScriptType.ToString(),
                ScriptContentHash: submitted.Request.Snapshot.ContentHash, OutputPolicy: submitted.OutputPolicy), ct);
            if (created.TenantId != binding.Identity.TenantId || created.AgentId != submitted.Request.AgentId ||
                created.TaskType != TaskKinds.ExecLibraryScript || created.ScriptContentHash != submitted.Request.Snapshot.ContentHash)
                throw new InvalidOperationException("The canonical activity does not match its protected admission.");
        }, async ct =>
        {
            await authority.RequireScriptAsync(binding.Identity, submitted.Request.Snapshot.Script.Id, submitted.Request.AgentId, ct);
            await authority.RequireFirstDispatchAsync(binding.Identity, lease, ct);
        }, cancellationToken);
        if (!claimed)
        {
            var existing = await admissions.FindByExecutionAsync(binding.Kind, binding.ExecutionId, cancellationToken) ?? binding;
            return new(existing, await runs.GetActivityByRequestIdAsync(binding.ExecutionId, cancellationToken),
                DispatchUncertain: existing.DispatchAcceptedAtUtc is null && existing.AuthorityDeniedAtUtc is null,
                AuthorityDenied: existing.AuthorityDeniedAtUtc is not null);
        }
        try
        {
            await authority.RequireScriptAsync(binding.Identity, submitted.Request.Snapshot.Script.Id, submitted.Request.AgentId, cancellationToken);
            await authority.RequireFirstDispatchAsync(binding.Identity, lease, cancellationToken);
        }
        catch (ExecutionAdmissionDeniedException)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await admissions.RecordAuthorityDeniedAsync(binding.Kind, binding.ExecutionId, cancellationToken);
            activity = await runs.UpdateTaskActivityStatusAsync(new(binding.ExecutionId, "Failed",
                "Execution admission authority was revoked before dispatch.", clock.GetUtcNow()), cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(binding, activity, AuthorityDenied: true);
        }
        try
        {
            await commands.DispatchAsync(client, binding.ExecutionId, "flow-action:" + binding.Identity.ActionKey.ToString("N"),
                TaskKinds.ExecLibraryScript, submitted.PayloadJson, (int)submitted.Request.Environment, cancellationToken);
            await admissions.RecordDispatchAcceptedAsync(binding, cancellationToken);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            // A partial command lifecycle or transport send cannot prove nonacceptance.
            // The committed request identity remains observable; it is never blindly dispatched again.
            return new(binding, await runs.GetActivityByRequestIdAsync(binding.ExecutionId, cancellationToken), DispatchUncertain: true);
        }
        var accepted = await admissions.FindByExecutionAsync(binding.Kind, binding.ExecutionId, cancellationToken) ?? binding;
        return new(accepted, await runs.GetActivityByRequestIdAsync(binding.ExecutionId, cancellationToken));
    }

    private static void Validate(KeyedScriptAdmissionRequest request)
    {
        var script = request.Snapshot.Script;
        if (script.Id is 0 or > int.MaxValue || script.SourceRevision <= 0 || request.AgentId == Guid.Empty ||
            request.TimeoutSeconds is < 1 or > 86400 || request.WorkingDirectory?.Length > 1024 ||
            !Enum.IsDefined(request.Environment) || request.Parameters.Count > 128 ||
            request.Snapshot.Parameters.Count > 128 ||
            request.Snapshot.Parameters.Any(parameter => parameter.ScriptId != script.Id) ||
            !string.Equals(request.Snapshot.ContentHash,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script.Content))), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The immutable library script source, target or runtime bounds are invalid.");
    }

    private static Dictionary<string, string> Parameters(KeyedScriptAdmissionRequest request)
    {
        if (request.Parameters.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.Parameters.Count)
            throw new ArgumentException("Script parameter names must be unique.");
        var selected = new Dictionary<string, string>(request.Parameters, StringComparer.OrdinalIgnoreCase);
        var known = request.Snapshot.Parameters.Select(parameter => parameter.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (selected.Keys.Any(name => !known.Contains(name)))
            throw new ArgumentException("An input is not present in the pinned script parameter schema.");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var parameter in request.Snapshot.Parameters.OrderBy(parameter => parameter.Name, StringComparer.Ordinal))
        {
            var value = selected.GetValueOrDefault(parameter.Name) ?? parameter.Default;
            if (value is null)
            {
                if (parameter.Required) throw new ArgumentException("A required pinned script input is missing.");
                continue;
            }
            if (value.Length > 16384) throw new ArgumentException("A pinned script input exceeds its bound.");
            parameters.Add(parameter.Name, value);
        }
        return parameters;
    }

    private sealed class PinnedScript(PinnedJobScriptSnapshot pin) : IScriptService
    {
        public Task<ScriptInfo?> GetAsync(ulong id, CancellationToken ct = default) => Task.FromResult(id == pin.Script.Id ? pin.Script : null);
        public Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong id, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ScriptParamInfo>>(id == pin.Script.Id ? pin.Parameters : []);
        public Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ScriptInfo>>([pin.Script]);
        public Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> DeleteAsync(ulong id, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ScriptInfo?> ParseManifestAsync(ulong id, string? raw, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
