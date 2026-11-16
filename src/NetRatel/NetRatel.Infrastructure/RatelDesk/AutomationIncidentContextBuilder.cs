using System.Globalization;
using System.Text.Json;
using AutomationTrace;
using AutomationTrace.Contracts;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.ServiceIdentity;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Infrastructure.RatelDesk;

/// <summary>Reads exact native records through the authorized/redacted run service, never clipped callbacks or business inputs.</summary>
public sealed class AutomationIncidentContextBuilder(OrchestratorDbContext db, IJobRunService runs,
    IEffectiveAccessService access, IServicePublicSettingsResolver settings)
{
    public async Task<AutomationIncidentContext> BuildAsync(FlowIncidentActionDraft draft, CancellationToken ct)
    {
        var source = draft.Event.ExecutionEvent;
        var origin = source?.OriginContext;
        var execution = source?.SourceKind == FlowExecutionSourceKind.Request ? source.ParentExecutionId : source?.ExecutionId;
        var bindingExecution = source?.SourceKind == FlowExecutionSourceKind.Script ? source.RootExecutionId ?? source.ParentExecutionId : execution;
        var binding = bindingExecution is null ? null : await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking()
            .SingleOrDefaultAsync(x => x.TenantId == draft.TenantId && x.ExecutionId == bindingExecution, ct);
        if (binding is null && source?.NativeRequestId is { } requestId && int.TryParse(requestId, out var native))
            binding = await db.Set<ManagedOrchestrationRequestBinding>().AsNoTracking().SingleOrDefaultAsync(x => x.RequestId == native && x.TenantId == draft.TenantId, ct);
        if (binding is not null)
        {
            if (source?.SourceKind != FlowExecutionSourceKind.Script) execution = binding.ExecutionId;
            if (binding.OriginContextJson is null) throw new InvalidOperationException("managed-origin-context-unavailable");
            var trusted = binding.OriginContextJson is null ? null : JsonSerializer.Deserialize<AutomationOriginContext>(binding.OriginContextJson, AutomationTraceValidation.Json);
            if (JsonSerializer.Serialize(origin, AutomationTraceValidation.Json) != JsonSerializer.Serialize(trusted, AutomationTraceValidation.Json))
                throw new InvalidOperationException("source-origin-binding-changed");
        }
        var outcome = source?.Outcome.ToString() ?? "Failed";
        IncidentTargetTotals? totals = source?.Aggregate is { } aggregate ? new(aggregate.TargetCount,
            aggregate.Counts.Succeeded, aggregate.Counts.Failed, aggregate.Counts.Skipped, aggregate.Counts.Cancelled,
            aggregate.Counts.TimedOut, Math.Max(aggregate.Counts.Unknown, aggregate.TargetCount - aggregate.Counts.Succeeded -
                aggregate.Counts.Failed - aggregate.Counts.Skipped - aggregate.Counts.Cancelled - aggregate.Counts.TimedOut)) : null;
        var context = new AutomationIncidentContext(AutomationTraceContract.ContextVersion,
            source?.SourceKind.ToString().ToLowerInvariant() ?? "monitoring", origin, source?.NativeRequestId,
            source?.Definition.Id is { } definitionId ? new(definitionId, source.Definition.Name) :
                draft.Event.Data is { } monitoring ? new(monitoring.RuleId.ToString("D"), Bound(monitoring.RuleName, 256)) : null,
            execution, draft.RunId.ToString("D"), draft.ActionNodeId.ToString("D"), outcome,
            source?.StartedAtUtc, source?.CompletedAtUtc, totals, [], totals?.Total ?? 0, "unavailable", [])
        { Supplement = draft.Fields.PresentationSupplement ?? (source is null
            ? new(FlowIncidentSourcePresentation.Description(draft), null, null) : null) };
        var nativeJobRun = source?.SourceKind == FlowExecutionSourceKind.Script ? source.ParentExecutionId : execution;
        if (long.TryParse(nativeJobRun, NumberStyles.None, CultureInfo.InvariantCulture, out var nativeJobRunId) && nativeJobRunId > 0)
        {
            var job = await db.JobRuns.AsNoTracking().Where(x => x.Id == nativeJobRunId && x.TenantId == draft.TenantId)
                .Select(x => new { x.Id, x.JobId }).SingleOrDefaultAsync(ct);
            if (job is not null) context = context with { JobExecution = new(job.JobId.ToString(CultureInfo.InvariantCulture), job.Id.ToString(CultureInfo.InvariantCulture)) };
        }
        var web = (await settings.ResolveAsync(ct)).Identity.WebBaseUrl.TrimEnd('/');
        var links = new List<AutomationLink>();
        if (Uri.TryCreate(web, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0)
        {
            if (context.NativeRequestId is { } request) links.Add(new("native-request", "Request " + request, web + "/requests?search=" + request));
            if (context.JobExecution is { } job)
                links.Add(new("job-run", "Job execution", web + "/jobs/" + job.DefinitionId + "/runs/" + job.RunId));
            links.Add(new("flow-run", "Flow run", web + "/flows?tenantId=" + draft.TenantId.ToString(CultureInfo.InvariantCulture) + "&run=" + draft.RunId.ToString("D")));
        }
        context = context with { Links = links };
        if (source is null) return context;
        if (!await access.AuthorizeAsync(FlowAuthorityPrincipal.Create(draft.Event.Authority), NetRatelPermissions.JobManagement, draft.TenantId, ct))
            return context with { EvidenceState = "redacted" };
        if (source.SourceKind == FlowExecutionSourceKind.Script)
        {
            var activity = await runs.GetActivityByRequestIdAsync(source.ExecutionId, ct);
            if (activity?.TenantId != draft.TenantId) return context;
            if (source.ParentExecutionId != activity.JobRunId?.ToString(CultureInfo.InvariantCulture))
                throw new InvalidOperationException("incident-script-binding-invalid");
            var error = ErrorExcerpt(activity.ResultJson) ?? activity.Error;
            var state = DiagnosticState(error);
            var step = new IncidentFailedStep(activity.Id.ToString(CultureInfo.InvariantCulture), null,
                Bound(activity.ScriptName ?? activity.CommandLabel, 256), Bound(activity.TaskType, 128), source.Outcome.ToString(),
                activity.ExitCode, Bound(activity.Error, 512), Bound(error, 512), state);
            var target = new IncidentTargetEvidence(activity.AgentId?.ToString("D") ?? activity.RequestId,
                Bound(activity.TargetDisplayName ?? activity.TargetName, 256), Bound(activity.TargetHostName, 256),
                activity.JobRunId?.ToString(CultureInfo.InvariantCulture), source.Outcome.ToString(), [step], 0);
            context = context with { Targets = [target], OmittedTargets = 0, EvidenceState = state };
            if (!AutomationTraceValidation.Context(context)) throw new InvalidOperationException("incident-context-invalid");
            return context;
        }
        if (!ulong.TryParse(execution, NumberStyles.None, CultureInfo.InvariantCulture, out var runId)) return context;
        var parent = await runs.GetIncidentEvidenceAsync(runId, ct);
        if (parent?.Run.TenantId != draft.TenantId) return context;
        var targets = new List<IncidentTargetEvidence>();
        if (parent.Run.IsAggregate)
        {
            var rows = await db.Set<JobRunNodeRecord>().AsNoTracking().Where(x => x.ParentRunId == checked((long)runId) && x.TenantId == draft.TenantId)
                .OrderBy(x => x.State == JobRunNodeState.Failed ? 0 : x.State == JobRunNodeState.TimedOut ? 1 : 2)
                .ThenBy(x => x.Ordinal).Take(AutomationTraceContract.MaximumTargets).ToArrayAsync(ct);
            foreach (var row in rows)
            {
                var target = JsonSerializer.Deserialize<JobTargetNode>(row.TargetJson, AutomationTraceValidation.Json);
                var child = row.NativeRunId is { } childId ? await runs.GetIncidentEvidenceAsync(checked((ulong)childId), ct) : null;
                if (child is not null && (child.Run.TenantId != draft.TenantId || child.Run.ParentRunId != runId))
                    throw new InvalidOperationException("incident-child-binding-invalid");
                targets.Add(Evidence(child, row.AgentId.ToString("D"), target?.DisplayName, target?.Hostname, row.State.ToString()));
            }
        }
        else targets.Add(Evidence(parent, parent.Run.AgentId?.ToString("D") ?? execution!, parent.Run.TargetDisplayName,
            parent.Run.TargetHostName, parent.Run.Status.ToString()));
        context = context with { Targets = targets, OmittedTargets = Math.Max(0, (totals?.Total ?? targets.Count) - targets.Count),
            EvidenceState = targets.Any(x => x.FailedSteps.Any(s => s.DiagnosticState == "redacted")) ? "redacted" :
                ((totals?.Total ?? targets.Count) > targets.Count || targets.Any(x => x.OmittedSteps > 0 || x.FailedSteps.Any(s => s.DiagnosticState == "truncated"))) ? "truncated" :
                targets.Any(x => x.FailedSteps.Count == 0 && x.Outcome != "Succeeded" || x.FailedSteps.Any(s => s.DiagnosticState == "unavailable")) ? "unavailable" : "available" };
        // Bound the complete exported context, including UTF-8 and JSON encoding expansion.
        while (JsonSerializer.SerializeToUtf8Bytes(context, AutomationTraceValidation.Json).Length > AutomationTraceContract.MaximumContextBytes && context.Targets.Count > 0)
            context = context with { Targets = context.Targets.Take(context.Targets.Count - 1).ToArray(), OmittedTargets = context.OmittedTargets + 1, EvidenceState = "truncated" };
        if (!AutomationTraceValidation.Context(context)) throw new InvalidOperationException("incident-context-invalid");
        return context;
    }
    public static IncidentTargetEvidence Evidence(JobRunDetails? detail, string id, string? name, string? hostname, string outcome)
    {
        outcome = FlowExecutionOutcomes.FromAuthoritativeStatus(outcome)?.ToString() ?? "Unknown";
        var failures = detail?.Activities.Where(x => x.ExitCode is not (null or 0) ||
            FlowExecutionOutcomes.FromAuthoritativeStatus(x.Status) is FlowExecutionOutcome.Failed or FlowExecutionOutcome.TimedOut or FlowExecutionOutcome.Cancelled or FlowExecutionOutcome.Unknown)
            .OrderBy(x => detail.Steps.FirstOrDefault(s => s.TaskRequestId == x.RequestId)?.Ordinal ?? int.MaxValue).ThenBy(x => x.Id).ToArray() ?? [];
        var steps = failures.Take(AutomationTraceContract.MaximumStepsPerTarget).Select(activity =>
        {
            var step = detail!.Steps.FirstOrDefault(x => x.TaskRequestId == activity.RequestId);
            var error = ErrorExcerpt(activity.ResultJson) ?? activity.Error;
            var diagnosticState = DiagnosticState(error);
            return new IncidentFailedStep(activity.Id.ToString(CultureInfo.InvariantCulture), step?.Ordinal,
                Bound(activity.ScriptName ?? activity.CommandLabel, 256), Bound(activity.TaskType, 128),
                activity.ExitCode is not (null or 0) ? "Failed" : FlowExecutionOutcomes.FromAuthoritativeStatus(activity.Status)?.ToString() ?? "Unknown", activity.ExitCode,
                Bound(activity.Error, 512), Bound(error, 512), diagnosticState);
        }).ToArray();
        var missingActivities = detail?.Steps.Where(step => step.Status is JobStepRunState.Failed or JobStepRunState.Cancelled or JobStepRunState.TimedOut or JobStepRunState.Unknown &&
            !failures.Any(activity => activity.RequestId == step.TaskRequestId)).Select(step =>
                new IncidentFailedStep(step.Id.ToString(CultureInfo.InvariantCulture), step.Ordinal, null, null,
                    step.Status.ToString(), null, Bound(step.Error, 512), null, "unavailable")).ToArray() ?? [];
        var selected = steps.Concat(missingActivities).OrderBy(step => step.Ordinal ?? int.MaxValue).ThenBy(step => step.Id, StringComparer.Ordinal)
            .Take(AutomationTraceContract.MaximumStepsPerTarget).ToArray();
        return new(id, Bound(name, 256), Bound(hostname, 256), detail?.Run.Id.ToString(CultureInfo.InvariantCulture), outcome,
            selected, Math.Max(0, (detail?.FailedActivityCount ?? failures.Length) + missingActivities.Length - selected.Length));
    }
    private static string DiagnosticState(string? error) => error is null ? "unavailable" :
        error.Contains(NetRatel.Shared.Security.TaskOutputRedactor.Mask, StringComparison.Ordinal) ? "redacted" :
        error.Length > 512 ? "truncated" : "available";
    private static string? ErrorExcerpt(string? safeResult)
    {
        if (safeResult is null || safeResult.Length > 262_144) return null;
        try
        {
            using var result = JsonDocument.Parse(safeResult, new JsonDocumentOptions { MaxDepth = 16 });
            if (!result.RootElement.TryGetProperty("stderr", out var stderr)) return null;
            return stderr.ValueKind == JsonValueKind.String ? stderr.GetString() : stderr.ValueKind == JsonValueKind.Array
                ? string.Join("\n", stderr.EnumerateArray().Take(8).Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString())) : null;
        }
        catch (JsonException) { return null; }
    }
    private static string? Bound(string? value, int length) => value is null || value.Length <= length ? value : value[..length];
}
