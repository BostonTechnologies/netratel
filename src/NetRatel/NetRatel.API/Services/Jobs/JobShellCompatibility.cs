using System.Text.Json;
using NetRatel.API.Gateway;
using NetRatel.Application.Jobs;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.API.Services.Jobs;

public sealed class JobShellCompatibility(IJobTargetResolver targets, IAgentJobGatewaySessionRegistry sessions,
    IScriptService scripts) : IJobShellCompatibility
{
    private static readonly JsonSerializerOptions Json = JobTaskBridge.DeserializeOptions;

    public async Task RequireDefinitionAsync(JobDefinitionDetails definition, CancellationToken cancellationToken)
        => await VerifyDefinitionAsync(definition, cancellationToken);

    internal async Task<JobShellPreviewDto> VerifyDefinitionAsync(JobDefinitionDetails definition, CancellationToken cancellationToken)
    {
        var job = definition.Job;
        var target = job.Target ?? (job.AgentId is { } agentId ? JobTargetDefinition.FromLegacy(agentId) : null);
        if (job.TenantId is not { } tenantId || target is null) return new(true, false);

        var requirements = new List<Requirement>();
        foreach (var step in ExecutableSteps(definition))
        {
            var scriptId = ScriptId(step);
            var script = scriptId is { } id ? await scripts.GetAsync(id, cancellationToken) : null;
            if (GetRequirement(step, script) is { } requirement) requirements.Add(requirement);
        }
        if (requirements.Count == 0) return new(true, true);

        JobTargetResolution resolved;
        try
        {
            resolved = await targets.ResolveAsync(tenantId, target, JobExecutionContextPolicy.FromJob(job),
                JobAggregateOrchestrator.TargetParameters(definition, null), null, null, cancellationToken);
        }
        catch (JobTargetValidationException)
        {
            // The target resolver owns target validity. An unresolved draft supplies no shell evidence.
            return new(true, false, "The target set remains unverified until it can be resolved.");
        }
        return RequireCompatible(tenantId, target, job.ExecutionPolicy ?? new(), resolved.Nodes, requirements);
    }

    public void RequireAdmission(PinnedJobExecutionSnapshot snapshot, JobResolvedTargetPlan plan)
    {
        var scriptsById = snapshot.Scripts.ToDictionary(item => item.Script.Id, item => item.Script);
        var requirements = ExecutableSteps(snapshot.Definition).Select(step =>
            GetRequirement(step, scriptsById.GetValueOrDefault(ScriptId(step) ?? 0))).OfType<Requirement>().ToArray();
        RequireCompatible(plan.TenantId, plan.Target, plan.ExecutionPolicy,
            plan.HaGroupSelection?.Candidates ?? plan.Nodes, requirements);
    }

    public void RequireDispatch(ClientKey client, OwnerKey owner, int ordinal, string? node, JobGatewayTaskInvocation invocation)
    {
        var inventory = sessions.GetFreshJobShells(client, owner);
        if (inventory is null) return;
        var requirement = invocation.TaskType switch
        {
            TaskKinds.ExecLibraryScript when JsonSerializer.Deserialize<ExecLibraryScriptPayload>(invocation.PayloadJson!, Json) is { } payload =>
                Describe(ordinal, payload.Preferred, payload.ScriptType, payload.ScriptContent),
            TaskKinds.ExecShellCommand when JsonSerializer.Deserialize<ExecShellCommandPayload>(invocation.PayloadJson!, Json) is { } payload =>
                Describe(ordinal, payload.Preferred, null, null),
            _ => null
        };
        if (requirement is not null && !requirement.Keywords.Any(inventory.Contains))
            throw Unavailable(requirement, client.AgentId, node ?? client.AgentId.ToString("D"));
    }

    private JobShellPreviewDto RequireCompatible(int tenantId, JobTargetDefinition target, JobExecutionPolicy policy,
        IReadOnlyList<JobTargetNode> nodes, IReadOnlyList<Requirement> requirements)
    {
        if (requirements.Count == 0) return new(true, true);
        JobTargetValidationException? firstFailure = null;
        var incompatible = 0;
        var known = 0;
        foreach (var node in nodes)
        {
            var inventory = sessions.GetFreshJobShells(new ClientKey(tenantId, node.AgentId));
            if (inventory is null) continue;
            known++;
            var missing = requirements.FirstOrDefault(requirement => !requirement.Keywords.Any(inventory.Contains));
            if (missing is null) continue;
            firstFailure ??= Unavailable(missing, node.AgentId, node.DisplayName ?? node.Hostname);
            if (target.Source == JobTargetSource.ExecutionGroup || policy.NodeFailurePolicy == JobNodeFailurePolicy.StopRemaining)
                throw firstFailure;
            incompatible++;
        }
        if (firstFailure is not null && incompatible == nodes.Count) throw firstFailure;
        if (firstFailure is not null) return new(true, false, firstFailure.Message);
        return known == nodes.Count ? new(true, true) : new(true, false,
            "Some target engines are unverified. NetRatel checks again when the job runs.");
    }

    private static IEnumerable<JobStepInfo> ExecutableSteps(JobDefinitionDetails definition) =>
        definition.Steps.Where(step => step.Enabled && step.Type is JobStepKind.RunCommand or JobStepKind.LibraryScript);

    private static ulong? ScriptId(JobStepInfo step) => step.Type == JobStepKind.LibraryScript
        ? step.ScriptId ?? (Read<ExecLibraryScriptPayload>(step.PayloadJson)?.ScriptId is > 0 and { } id ? (ulong)id : null)
        : null;

    private static Requirement? GetRequirement(JobStepInfo step, ScriptInfo? script)
    {
        var library = step.Type == JobStepKind.LibraryScript ? Read<ExecLibraryScriptPayload>(step.PayloadJson) : null;
        var preference = library?.Preferred ?? (step.Type == JobStepKind.RunCommand
            ? Read<ExecShellCommandPayload>(step.PayloadJson)?.Preferred : null) ?? ShellExecutor.Auto;
        if (!JobStepExecutor.TryResolve(step.Runner, preference, out var executor))
            throw new JobTargetValidationException("shell_executor_conflict", $"Step {step.Ordinal} specifies conflicting execution engines.");

        ScriptType? language = null;
        if (step.Type == JobStepKind.LibraryScript)
        {
            language = library?.ScriptType is { } type && type != default ? type :
                Enum.TryParse<ScriptType>(script?.ScriptType, true, out var declared) ? declared : null;
        }
        return Describe(step.Ordinal, executor, language, script?.Content);
    }

    private static T? Read<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, Json); }
        catch (JsonException)
        {
            if (JobTaskBridge.HasExecutorMetadata(json))
                throw new JobTargetValidationException("shell_executor_conflict", "A step payload has an invalid executor preference.");
            return null;
        }
    }

    private static Requirement? Describe(int ordinal, ShellExecutor executor, ScriptType? language, string? content)
    {
        var required = executor;
        if (language == ScriptType.PowerShell)
        {
            var (desktop, core) = PowerShellScriptRequirements.Read(content ?? string.Empty);
            if ((desktop && core) || (desktop && executor == ShellExecutor.Pwsh) || (core && executor == ShellExecutor.WindowsPowerShell) ||
                executor is ShellExecutor.Bash or ShellExecutor.Cmd)
                throw new JobTargetValidationException("shell_executor_conflict", $"Step {ordinal} requires a different PowerShell engine. Review the script's #requires directives.");
            if (executor == ShellExecutor.Auto)
                required = desktop ? ShellExecutor.WindowsPowerShell : core ? ShellExecutor.Pwsh : ShellExecutor.Auto;
        }
        if (language == ScriptType.Bash && executor is not (ShellExecutor.Auto or ShellExecutor.Bash))
            throw new JobTargetValidationException("shell_executor_conflict", $"Step {ordinal} is a Bash script and requires a Bash executor.");

        return required switch
        {
            ShellExecutor.Pwsh => new(ordinal, executor, "PowerShell 7 (pwsh)", ["pwsh"], "pwsh"),
            ShellExecutor.WindowsPowerShell => new(ordinal, executor, "Windows PowerShell (5.1)", ["powershell"], "powershell"),
            ShellExecutor.Bash => new(ordinal, executor, "Bash", ["bash"], "bash"),
            ShellExecutor.Cmd => new(ordinal, executor, "Command Prompt", ["cmd"], "cmd"),
            ShellExecutor.Auto when language == ScriptType.PowerShell => new(ordinal, executor, "PowerShell", ["pwsh", "powershell"], "powershell-compatible"),
            ShellExecutor.Auto when language == ScriptType.Bash => new(ordinal, executor, "Bash or sh", ["bash", "sh"], "bash/sh"),
            _ => null
        };
    }

    private static JobTargetValidationException Unavailable(Requirement requirement, Guid agentId, string node) =>
        new("required_shell_unavailable", $"Step {requirement.Ordinal} requires {requirement.Label}, which {node} does not advertise for jobs. Install the engine or select a compatible one.", 409)
        {
            AgentId = agentId, StepOrdinal = requirement.Ordinal,
            RequestedExecutor = JobStepExecutor.WireName(requirement.Executor), RequiredShell = requirement.RequiredShell
        };

    private sealed record Requirement(int Ordinal, ShellExecutor Executor, string Label, string[] Keywords, string RequiredShell);
}
