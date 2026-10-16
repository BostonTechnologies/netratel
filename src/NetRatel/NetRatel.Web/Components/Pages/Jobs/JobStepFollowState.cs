using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Web.Components.Pages.Jobs;

/// <summary>Presentation-only follow decisions. Stream ordering remains owned by the stream service.</summary>
public sealed class JobStepFollowState
{
    private ulong? _runId;
    private bool _initialized;
    private (ulong Id, string? Request, long? Started)? _attempt;
    public bool Following { get; private set; }
    public ulong? TargetId => _attempt?.Id;

    public void Reset()
    {
        _runId = null;
        _initialized = false;
        _attempt = null;
        Following = false;
    }
    public void Pause() => Following = false;
    public ulong? Resume(JobRunDto run, IReadOnlyList<JobStepRunDto> steps)
    {
        Following = true;
        return Reconcile(run, steps, true);
    }
    public ulong? Reconcile(JobRunDto run, IReadOnlyList<JobStepRunDto> steps, bool force = false)
    {
        if (_runId != run.Id) { Reset(); _runId = run.Id; }
        var initial = !_initialized;
        if (initial) { Following = !Terminal(run.Status); _initialized = true; }
        if (!initial && !Following) return null;
        var ordered = steps.Where(step => step.JobRunId == run.Id).OrderBy(step => step.Ordinal).ThenBy(step => step.Id).ToList();
        var running = ordered.Where(step => step.Status == JobStepRunStatusDto.Running).ToList();
        var target = running.FirstOrDefault(step => step.Id == TargetId) ?? running.FirstOrDefault();
        if (target is null && Terminal(run.Status))
            target = ordered.LastOrDefault(step => step.Status is JobStepRunStatusDto.Failed or JobStepRunStatusDto.TimedOut or JobStepRunStatusDto.Cancelled);
        target ??= ordered.FirstOrDefault(step => step.Id == TargetId);
        // A gap retains output. A terminal snapshot may skip the last step's running snapshot entirely.
        if (target is null || Terminal(run.Status))
            target = (Terminal(run.Status) ? ordered.LastOrDefault(step => step.Status is JobStepRunStatusDto.Failed or JobStepRunStatusDto.TimedOut or JobStepRunStatusDto.Cancelled) : null)
                ?? ordered.LastOrDefault(step => step.Status != JobStepRunStatusDto.Skipped && (step.StartedAtMicros.HasValue || step.Status is JobStepRunStatusDto.Succeeded or JobStepRunStatusDto.Unknown))
                ?? target;
        if (target is null) return null;
        var attempt = (target.Id, target.TaskRequestId, target.StartedAtMicros);
        if (!force && _attempt == attempt) return null;
        _attempt = attempt;
        return target.Id;
    }
    private static bool Terminal(JobRunStatusDto status) => status is JobRunStatusDto.Succeeded or JobRunStatusDto.Failed or JobRunStatusDto.Cancelled or JobRunStatusDto.TimedOut or JobRunStatusDto.Unknown;
}
