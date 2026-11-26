using System.Globalization;
using System.Text.Json;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.API.Services.Orchestration;

public sealed record NetRatelProviderDiagnostic(string Provider, string Stage, string Code, string Message,
    string RetryDisposition, string CorrelationId, bool? ExecutionCreated, int? HttpStatus = null,
    string? ConnectionId = null, string? JobDefinitionId = null, string? ProviderRequestId = null,
    string? ProviderRunId = null, Guid? NodeId = null, int? StepOrdinal = null,
    string? RequestedExecutor = null, string? ResolvedEngine = null, string? RequiredShell = null,
    string? ParentResult = null);

internal static class NetRatelProviderDiagnostics
{
    internal static NetRatelProviderDiagnostic Admission(ManagedOrchestrationRequestBinding binding, JobTargetValidationException? error)
    {
        var (code, message) = error?.Code switch
        {
            "run_as_denied" => ("run_as_denied", ManagedJobRunAsGrants.ApprovalRequired),
            "run_as_profile_unavailable" or "run_as_profile_ineligible" or "run_as_plan_changed" or "run_as_secret_unavailable" =>
                (error.Code, "Review the current account, credential profile and secret policy in NetRatel."),
            "required_shell_unavailable" => ("required_shell_unavailable", "Install the required shell or select a compatible engine."),
            "shell_executor_conflict" => ("shell_executor_conflict", "Choose matching step and library payload execution engines."),
            _ => ("managed_admission_rejected", "Review the current target, job inputs and retained connection authority in NetRatel.")
        };
        return new("NetRatel", "admission", code, message, "configuration_required", binding.CorrelationId, false, 422,
            binding.LinkId, binding.JobDefinitionId, NodeId: error?.AgentId, StepOrdinal: error?.StepOrdinal,
            RequestedExecutor: Engine(error?.RequestedExecutor), RequiredShell: Engine(error?.RequiredShell));
    }

    internal static NetRatelProviderDiagnostic? ForRun(ManagedOrchestrationRequestBinding binding, JobRunDetails details,
        JobRunDetails? native = null)
    {
        var run = details.Run;
        if (run.Status is JobRunState.Succeeded or JobRunState.Pending or JobRunState.Running) return null;
        var source = native ?? details;
        var failure = source.Activities.OrderBy(activity => activity.Id).Select(activity => ReadFailure(activity, source))
            .FirstOrDefault(value => value is not null);
        var code = (run.Status == JobRunState.Failed ? source.Run.Status : run.Status) switch
        {
            JobRunState.Cancelled => "cancelled",
            JobRunState.TimedOut => "timed_out",
            JobRunState.Unknown => "delivery_uncertain",
            _ => failure?.Code ?? DispatchCode(source.Run.Error) ?? "execution_failed"
        };
        var (stage, message, retry) = code switch
        {
            "required_shell_unavailable" => ("native-shell-resolution", "Install the required shell or select a compatible execution engine.", "configuration_required"),
            "run_as_logon_failed" => ("native-account-logon", "Check the saved account credentials and native logon rights on the execution node.", "configuration_required"),
            "run_as_unavailable" => ("dispatch", "Review the approved account and connection authority in NetRatel.", "configuration_required"),
            "run_as_unsupported" => ("dispatch", "Install an agent that supports the selected execution account.", "configuration_required"),
            "cancelled" => ("execution", "The execution was cancelled.", "operator_review"),
            "timed_out" => ("execution", "The execution timed out.", "operator_review"),
            "delivery_uncertain" => ("delivery", "The provider outcome is uncertain. Reconcile the existing attempt.", "reconciliation_required"),
            _ => ("execution", "The admitted execution failed. Review the linked job run.", "operator_review")
        };
        return new("NetRatel", stage, code, message, retry, binding.CorrelationId, true,
            ConnectionId: binding.LinkId, JobDefinitionId: binding.JobDefinitionId,
            ProviderRequestId: binding.RequestId.ToString(CultureInfo.InvariantCulture), ProviderRunId: binding.ExecutionId,
            NodeId: source.Run.AgentId, StepOrdinal: failure?.Ordinal, RequestedExecutor: failure?.Requested,
            ResolvedEngine: failure?.Resolved, RequiredShell: failure?.RequiredShell,
            ParentResult: run.Error is "StoppedAfterNodeFailure" or "StoppedAfterUnknownExecution" ? run.Error : null);
    }

    private static NativeFailure? ReadFailure(JobTaskActivityInfo activity, JobRunDetails details)
    {
        if (activity.ResultJson is null) return null;
        using var document = TryParse(activity.ResultJson);
        if (document is null || document.RootElement.ValueKind != JsonValueKind.Object) return null;
        var root = document.RootElement;
        if (!root.TryGetProperty("diagnostics", out var diagnostic) || diagnostic.ValueKind != JsonValueKind.Object) return null;
        var code = String(diagnostic, "failureCode");
        var required = Engine(String(diagnostic, "requiredShell"));
        // Legacy agents used a failure envelope with a generic label and a trusted exception prefix.
        if (code is null && String(diagnostic, "failureMessage") is "Shell command execution failed." or "Library script execution failed." &&
            root.TryGetProperty("stderr", out var stderr) && stderr.ValueKind == JsonValueKind.Array && stderr.GetArrayLength() > 0 &&
            stderr[0].ValueKind == JsonValueKind.String && stderr[0].GetString() is { } legacy &&
            legacy.StartsWith("System.InvalidOperationException: Required shell not found: pwsh", StringComparison.Ordinal))
        {
            code = "required_shell_unavailable";
            required = "pwsh";
        }
        if (code is not ("required_shell_unavailable" or "run_as_logon_failed")) return null;
        var ordinal = details.Steps.FirstOrDefault(step => step.TaskRequestId == activity.RequestId)?.Ordinal;
        return new(code, ordinal, Engine(String(diagnostic, "requestedExecutor")), Engine(String(diagnostic, "resolvedEngine")), required);
    }

    private static string? DispatchCode(string? error)
    {
        if (error is null) return null;
        var separator = error.IndexOf(':');
        return separator > 0 ? error[..separator] switch
        {
            "required_shell_unavailable" => "required_shell_unavailable",
            "run_as_unsupported" => "run_as_unsupported",
            "run_as_denied" or "run_as_unavailable" or "run_as_secret_changed" or "run_as_admission_unavailable" => "run_as_unavailable",
            _ => null
        } : null;
    }

    private static string? Engine(string? value) => value is "auto" or "pwsh" or "powershell" or "windows-powershell" or
        "bash" or "cmd" or "sh" or "bash/sh" or "powershell-compatible" ? value : null;

    private static string? String(JsonElement value, string key) => value.TryGetProperty(key, out var property) &&
        property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static JsonDocument? TryParse(string value)
    {
        try { return JsonDocument.Parse(value); }
        catch (JsonException) { return null; }
    }

    private sealed record NativeFailure(string Code, int? Ordinal, string? Requested, string? Resolved, string? RequiredShell);
}
