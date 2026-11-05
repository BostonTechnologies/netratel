using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;

namespace NetRatel.API.Services.Flows;

/// <summary>Adapts durable flow actions to canonical pinned execution services; it is never used by pure preview.</summary>
public sealed class FlowExecutionActionDispatcher(OrchestratorDbContext db, IFlowExecutionDefinitionPins pins,
    IAkkaJobAuthorityService jobs, IKeyedScriptExecutionService scripts, IJobRunService runs,
    IExecutionAdmissionStore admissions, FlowExecutionAuthority authority) : IFlowExecutionActionDispatcher
{
    private sealed record ScriptSubmission(KeyedScriptAdmissionRequest Request, string PayloadJson, TaskOutputPolicy OutputPolicy);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FlowExecutionActionResult> DispatchAsync(FlowExecutionActionRequest request, CancellationToken ct = default)
    {
        try
        {
            var node = Node(request);
            var identity = Identity(request, node.Kind == FlowNodeKind.RerunOriginalExecution);
            if (node.Kind == FlowNodeKind.RerunOriginalExecution) return await RerunAsync(request, identity, ct);
            var pin = await pins.ResolveAsync(identity.TenantId, identity.FlowVersionId, identity.NodeId, ct);
            if (pin is null || pin.Kind != node.Kind) return Result(FlowExecutionActionDisposition.Unsupported, "execution-pin-unavailable");
            if (node.Kind == FlowNodeKind.RunJob && pin.Job is { } job)
            {
                var timeout = node.ExecutionAction!.TimeoutMinutes * 60;
                var run = await jobs.StartKeyedAsync(new(identity, job, new("flow-action", Inputs(request.InputsJson),
                    null, ExpectedRuntimeSeconds: timeout, GraceSeconds: 0, HardTimeoutSeconds: timeout,
                    Environment: node.ExecutionAction.Environment)) { FlowLease = request.Lease }, ct);
                return await ObserveAsync(request, new(request.ActionKey, ExecutionAdmissionKind.Job,
                    run.Id.ToString(CultureInfo.InvariantCulture)), ct);
            }
            if (node.Kind == FlowNodeKind.RunLibraryScript && pin.Script is { } script)
            {
                var target = pin.Target?.AgentId ?? request.Lease.Event.ExecutionEvent?.Target.AgentId ??
                    (request.Lease.Event.ExecutionEvent is null ? request.Lease.Event.Data.AgentId : null);
                if (target is null || target == Guid.Empty) return Result(FlowExecutionActionDisposition.Unsupported, "script-target-unavailable");
                var accepted = await scripts.StartKeyedAsync(new(identity, script, target.Value, Parameters(request.InputsJson),
                    node.ExecutionAction!.Environment, node.ExecutionAction.TimeoutMinutes * 60, node.ExecutionAction.WorkingDirectory)
                    { FlowLease = request.Lease }, ct);
                return await ScriptResultAsync(request, accepted.Binding, accepted.Activity, accepted.DispatchUncertain,
                    accepted.AuthorityDenied, ct);
            }
            return Result(FlowExecutionActionDisposition.Unsupported, "execution-node-unsupported");
        }
        catch (ExecutionAdmissionBudgetExhaustedException) { return Result(FlowExecutionActionDisposition.Denied, "rerun-attempts-exhausted"); }
        catch (ExecutionAdmissionDeniedException) { return Result(FlowExecutionActionDisposition.Denied, "execution-authority-denied"); }
        catch (NotSupportedException) { return Result(FlowExecutionActionDisposition.Unsupported, "execution-source-unsupported"); }
        catch (FlowExecutionPinException error) { return Result(FlowExecutionActionDisposition.Unsupported, error.Code); }
        catch (ArgumentException) { return Result(FlowExecutionActionDisposition.Failed, "execution-input-invalid"); }
        catch (JsonException) { return Result(FlowExecutionActionDisposition.Failed, "execution-input-invalid"); }
    }

    public async Task<FlowExecutionActionResult> ObserveAsync(FlowExecutionActionRequest request,
        FlowExecutionActionReceipt receipt, CancellationToken ct = default)
    {
        try { return await ObserveCoreAsync(request, receipt, ct); }
        catch (ExecutionAdmissionDeniedException) { return Result(FlowExecutionActionDisposition.Denied, "execution-observation-denied"); }
    }

    private async Task<FlowExecutionActionResult> ObserveCoreAsync(FlowExecutionActionRequest request,
        FlowExecutionActionReceipt receipt, CancellationToken ct)
    {
        var node = Node(request);
        var expected = Identity(request, node.Kind == FlowNodeKind.RerunOriginalExecution);
        var binding = await admissions.FindByExecutionAsync(receipt.Kind, receipt.ExecutionId, ct);
        if (binding is null || binding.Identity != expected || receipt.ActionKey != request.ActionKey)
            return Result(FlowExecutionActionDisposition.Unknown, "execution-receipt-unavailable");
        if (binding.Kind == ExecutionAdmissionKind.LibraryScript)
        {
            var submission = JsonSerializer.Deserialize<ScriptSubmission>(binding.PayloadJson, Json)
                ?? throw new InvalidOperationException("The protected script submission is unavailable.");
            await authority.RequireScriptAsync(expected, submission.Request.Snapshot.Script.Id, submission.Request.AgentId, ct);
            return await ScriptResultAsync(request, binding, await runs.GetActivityByRequestIdAsync(binding.ExecutionId, ct),
                binding.DispatchStartedAtUtc is not null && binding.DispatchAcceptedAtUtc is null,
                binding.AuthorityDeniedAtUtc is not null, ct);
        }
        var submitted = JsonSerializer.Deserialize<KeyedJobAdmissionRequest>(binding.PayloadJson)
            ?? throw new InvalidOperationException("The protected job submission is unavailable.");
        await authority.RequireAllowedAsync(expected, submitted.Snapshot.Definition.Job, ct);
        var id = ulong.Parse(binding.ExecutionId, CultureInfo.InvariantCulture);
        var details = await runs.GetDetailsAsync(id, ct);
        if (details is null) return new(FlowExecutionActionDisposition.Pending, "job-reserved", receipt);
        if (details.Run.TenantId != expected.TenantId || details.Run.AgentId != submitted.Snapshot.Definition.Job.AgentId ||
            details.Run.JobId != submitted.Snapshot.Definition.Job.Id || details.Run.StartedBy != submitted.Request.StartedBy)
            return Result(FlowExecutionActionDisposition.Unknown, "job-receipt-binding-mismatch");
        var disposition = details.Run.Status switch
        {
            JobRunState.Pending => FlowExecutionActionDisposition.Pending,
            JobRunState.Running => FlowExecutionActionDisposition.Running,
            JobRunState.Succeeded => FlowExecutionActionDisposition.Succeeded,
            JobRunState.Failed => FlowExecutionActionDisposition.Failed,
            JobRunState.TimedOut => FlowExecutionActionDisposition.TimedOut,
            JobRunState.Cancelled => FlowExecutionActionDisposition.Cancelled,
            _ => FlowExecutionActionDisposition.Unknown
        };
        if (disposition is not (FlowExecutionActionDisposition.Pending or FlowExecutionActionDisposition.Running))
        {
            var control = await db.Set<JobRunControlRecord>().AsNoTracking().SingleOrDefaultAsync(control => control.RunId == checked((long)id), ct);
            if (control?.TerminalReadyAtUtc is null) return new(FlowExecutionActionDisposition.Pending, "job-terminal-projection-pending", receipt);
            if (control.TerminalResultHash != JobRunProjectionHash.Compute(details))
                return new(FlowExecutionActionDisposition.Unknown, "job-terminal-projection-mismatch", receipt);
        }
        if (binding.AuthorityDeniedAtUtc is not null && details.Run.Status == JobRunState.Failed)
            disposition = FlowExecutionActionDisposition.Denied;
        var terminal = await TerminalAsync(expected.TenantId, FlowExecutionSourceKind.Job, binding.ExecutionId, ct);
        var definition = new FlowExecutionDefinitionDto(submitted.Snapshot.Definition.Job.Id.ToString(CultureInfo.InvariantCulture),
            submitted.Snapshot.Definition.Job.Name, submitted.Snapshot.ComputeDigest(), submitted.Snapshot.ComputeDigest());
        var output = new FlowExecutionActionOutput(definition, new(details.Run.AgentId, details.Run.TargetDisplayName, details.Run.TargetHostName),
            binding.ExecutionId, binding.ExecutionId, details.Run.StartedAtUtc, details.Run.CompletedAtUtc,
            Summary: Bound(details.Run.Error), Data: JsonSerializer.SerializeToElement(new
            { status = details.Run.Status.ToString(), steps = details.Steps.Count, completed = details.Steps.Count(step => step.Status == JobStepRunState.Succeeded) }),
            OutputAvailable: true, References: terminal?.Results);
        return new(disposition, "job-" + disposition.ToString().ToLowerInvariant(), Receipt(binding), output, terminal);
    }

    private async Task<FlowExecutionActionResult> ScriptResultAsync(FlowExecutionActionRequest request,
        ExecutionAdmissionBinding binding, JobTaskActivityInfo? activity, bool uncertain, bool denied, CancellationToken ct)
    {
        var receipt = Receipt(binding);
        if (activity is null) return new(uncertain ? FlowExecutionActionDisposition.Unknown : FlowExecutionActionDisposition.Pending,
            uncertain ? "script-dispatch-unknown" : "script-reserved", receipt);
        var native = FlowExecutionOutcomes.FromAuthoritativeStatus(activity.Status);
        var disposition = native switch
        {
            FlowExecutionOutcome.Succeeded => FlowExecutionActionDisposition.Succeeded,
            FlowExecutionOutcome.Failed => FlowExecutionActionDisposition.Failed,
            FlowExecutionOutcome.TimedOut => FlowExecutionActionDisposition.TimedOut,
            FlowExecutionOutcome.Cancelled => FlowExecutionActionDisposition.Cancelled,
            FlowExecutionOutcome.Unknown => FlowExecutionActionDisposition.Unknown,
            FlowExecutionOutcome.Denied => FlowExecutionActionDisposition.Denied,
            FlowExecutionOutcome.Unsupported => FlowExecutionActionDisposition.Unsupported,
            _ => denied ? FlowExecutionActionDisposition.Denied : uncertain ? FlowExecutionActionDisposition.Unknown :
                activity.Status.Equals("AwaitingApproval", StringComparison.OrdinalIgnoreCase) ? FlowExecutionActionDisposition.AwaitingApproval :
                activity.Status.Equals("Running", StringComparison.OrdinalIgnoreCase) ? FlowExecutionActionDisposition.Running :
                FlowExecutionActionDisposition.Pending
        };
        if (denied && native == FlowExecutionOutcome.Failed) disposition = FlowExecutionActionDisposition.Denied;
        var submitted = JsonSerializer.Deserialize<ScriptSubmission>(binding.PayloadJson, Json)!;
        if (activity.TenantId != binding.Identity.TenantId || activity.AgentId != submitted.Request.AgentId ||
            !string.Equals(activity.ScriptContentHash, submitted.Request.Snapshot.ContentHash, StringComparison.OrdinalIgnoreCase))
            return new(FlowExecutionActionDisposition.Unknown, "script-receipt-binding-mismatch", receipt);
        var script = submitted.Request.Snapshot;
        var definition = new FlowExecutionDefinitionDto(script.Script.Id.ToString(CultureInfo.InvariantCulture), script.Script.Name,
            script.Script.SourceRevision.ToString(CultureInfo.InvariantCulture), script.ContentHash);
        JsonElement? data = null;
        try { if (activity.ResultJson is not null) data = JsonSerializer.Deserialize<JsonElement>(activity.ResultJson); }
        catch (JsonException) { }
        var terminal = await TerminalAsync(binding.Identity.TenantId, FlowExecutionSourceKind.Script, binding.ExecutionId, ct);
        return new(disposition, "script-" + disposition.ToString().ToLowerInvariant(), receipt,
            new(definition, new(activity.AgentId, activity.TargetDisplayName, activity.TargetHostName), binding.ExecutionId,
                binding.ExecutionId, activity.StartedAtUtc, activity.CompletedAtUtc, activity.ExitCode,
                Summary: Bound(activity.Error), StandardOutput: Text(data, "stdout"), StandardError: Text(data, "stderr"),
                Data: Bounded(data), OutputAvailable: activity.ResultJson is not null,
                Truncated: activity.ResultJson is not null && Encoding.UTF8.GetByteCount(activity.ResultJson) > 16384, References: terminal?.Results), terminal);
    }

    private async Task<FlowExecutionActionResult> RerunAsync(FlowExecutionActionRequest request, ExecutionAdmissionIdentity identity, CancellationToken ct)
    {
        var source = request.Lease.Event.ExecutionEvent;
        var replay = source?.Replay;
        if (source?.Outcome == FlowExecutionOutcome.Unknown)
            return Result(FlowExecutionActionDisposition.Unknown, "original-outcome-unknown");
        if (source is null || replay is null || !replay.Supported || request.OriginalReplay != replay)
            return Result(FlowExecutionActionDisposition.Unsupported, "original-execution-not-replayable");
        if (source.Outcome is not (FlowExecutionOutcome.Failed or FlowExecutionOutcome.TimedOut))
            return Result(FlowExecutionActionDisposition.Unsupported, "original-outcome-not-recoverable");
        if (identity.Causation!.RecoveryAttempt > (Node(request).Rerun?.MaximumAttempts ?? 1))
            return Result(FlowExecutionActionDisposition.Denied, "rerun-attempts-exhausted");
        if (replay.SourceKind == FlowExecutionSourceKind.Job &&
            replay.InputsReference == "job-run:" + replay.SourceExecutionId &&
            ulong.TryParse(replay.SourceExecutionId, NumberStyles.None, CultureInfo.InvariantCulture, out var originalId))
        {
            var original = await runs.GetAsync(originalId, ct);
            var snapshot = await runs.GetPinnedExecutionSnapshotAsync(originalId, ct);
            if (original?.TenantId != identity.TenantId || snapshot is null || original.AgentId != replay.Target.AgentId ||
                snapshot.Definition.Job.AgentId != replay.Target.AgentId || snapshot.Definition.Job.Id.ToString(CultureInfo.InvariantCulture) != replay.Definition.Id ||
                snapshot.ComputeDigest() != replay.Definition.Revision || snapshot.ComputeDigest() != replay.Definition.ContentHash)
                return Result(FlowExecutionActionDisposition.Unsupported, "original-job-pin-unavailable");
            await authority.RequireAllowedAsync(identity, snapshot.Definition.Job, ct);
            var seconds = (Node(request).Rerun?.TimeoutMinutes ?? 60) * 60;
            var run = await jobs.StartKeyedAsync(new(identity, snapshot, new("flow-rerun",
                await runs.GetExecutionInputsAsync(originalId, ct), null, ExpectedRuntimeSeconds: seconds, GraceSeconds: 0,
                HardTimeoutSeconds: seconds, Environment: original.Environment)) { FlowLease = request.Lease }, ct);
            return await ObserveAsync(request, new(request.ActionKey, ExecutionAdmissionKind.Job,
                run.Id.ToString(CultureInfo.InvariantCulture), replay.SourceExecutionId, identity.Causation.RecoveryAttempt), ct);
        }
        if (replay.SourceKind == FlowExecutionSourceKind.Script &&
            replay.InputsReference == "script-execution:" + replay.SourceExecutionId)
        {
            var binding = await admissions.FindByExecutionAsync(ExecutionAdmissionKind.LibraryScript, replay.SourceExecutionId, ct);
            if (binding?.Identity.TenantId != identity.TenantId)
                return Result(FlowExecutionActionDisposition.Unsupported, "original-script-inputs-unavailable");
            var submitted = JsonSerializer.Deserialize<ScriptSubmission>(binding.PayloadJson, Json)!;
            var pin = submitted.Request.Snapshot;
            if (submitted.Request.AgentId != replay.Target.AgentId || pin.Script.Id.ToString(CultureInfo.InvariantCulture) != replay.Definition.Id ||
                pin.Script.SourceRevision.ToString(CultureInfo.InvariantCulture) != replay.Definition.Revision ||
                !string.Equals(pin.ContentHash, replay.Definition.ContentHash, StringComparison.OrdinalIgnoreCase))
                return Result(FlowExecutionActionDisposition.Unsupported, "original-script-pin-unavailable");
            var accepted = await scripts.StartKeyedAsync(submitted.Request with { Identity = identity, FlowLease = request.Lease }, ct);
            return await ScriptResultAsync(request, accepted.Binding, accepted.Activity, accepted.DispatchUncertain, accepted.AuthorityDenied, ct);
        }
        return Result(FlowExecutionActionDisposition.Unsupported, "original-request-not-replayable");
    }

    private async Task<FlowExecutionEventDto?> TerminalAsync(int tenantId, FlowExecutionSourceKind kind, string id, CancellationToken ct)
    {
        var candidates = await db.OutboxMessages.AsNoTracking().Where(row => row.Type == FlowExecutionEventLimits.OutboxType &&
            row.TenantId == tenantId.ToString(CultureInfo.InvariantCulture) && row.EntityId == id)
            .OrderByDescending(row => row.OccurredUtc).Take(8).Select(row => row.PayloadJson).ToArrayAsync(ct);
        foreach (var json in candidates)
        {
            var value = JsonSerializer.Deserialize<FlowExecutionEventDto>(json, Json);
            if (value?.TenantId == tenantId && value.SourceKind == kind && value.ExecutionId == id &&
                FlowExecutionEventValidator.Validate(value).Valid) return value;
        }
        return null;
    }
    private static FlowNodeDto Node(FlowExecutionActionRequest request) =>
        request.Lease.Version.Graph.Nodes.SingleOrDefault(node => node.Id == request.NodeId)
        ?? throw new ArgumentException("The immutable action node is unavailable.");
    private static ExecutionAdmissionIdentity Identity(FlowExecutionActionRequest request, bool rerun)
    {
        var lease = request.Lease; var source = lease.Event.ExecutionEvent; var cause = source?.Causation;
        var recovery = rerun ? request.RecoveryAttempt > 0 ? request.RecoveryAttempt : (cause?.RecoveryAttempt ?? 0) + 1 : request.RecoveryAttempt;
        var identity = new ExecutionAdmissionIdentity(request.ActionKey, lease.Event.TenantId, lease.Event.Authority.PrincipalId,
            lease.Version.Id, lease.RunId, request.NodeId, request.NodeAttempt, lease.Event.Authority.IntegrationCredentialId)
        {
            Causation = new(cause?.RootOccurrenceId ?? lease.Event.OccurrenceId.ToString("N"), source?.EventId,
                (cause?.HopCount ?? 0) + 1, recovery, rerun ? source?.Replay?.SourceExecutionId : null)
        };
        identity.Validate(); return identity;
    }
    private static FlowExecutionActionReceipt Receipt(ExecutionAdmissionBinding binding) =>
        new(binding.Identity.ActionKey, binding.Kind, binding.ExecutionId, binding.Identity.Causation?.SourceExecutionId,
            binding.Identity.Causation?.RecoveryAttempt ?? 0);
    private static FlowExecutionActionResult Result(FlowExecutionActionDisposition disposition, string code) => new(disposition, code);
    private static string Inputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "{}";
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object || Encoding.UTF8.GetByteCount(json) > 131072)
            throw new ArgumentException("Resolved execution inputs must be a bounded object.");
        return json;
    }
    private static Dictionary<string, string> Parameters(string? json)
    {
        using var document = JsonDocument.Parse(Inputs(json));
        return document.RootElement.EnumerateObject().ToDictionary(property => property.Name,
            property => property.Value.ValueKind == JsonValueKind.String ? property.Value.GetString()! : property.Value.GetRawText(), StringComparer.Ordinal);
    }
    private static string? Text(JsonElement? data, string property) => data is { ValueKind: JsonValueKind.Object } element &&
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? Bound(value.GetString()) : null;
    private static JsonElement? Bounded(JsonElement? data) => data is { } value && Encoding.UTF8.GetByteCount(value.GetRawText()) <= 16384 ? value : null;
    private static string? Bound(string? value)
    {
        var safe = value is null ? null : OperatorOutputRedactor.Redact(value);
        if (safe is null) return null;
        var length = Math.Min(safe.Length, 8192);
        while (Encoding.UTF8.GetByteCount(safe.AsSpan(0, length)) > 8192) length--;
        if (length > 0 && char.IsHighSurrogate(safe[length - 1])) length--;
        return safe[..length];
    }
}
