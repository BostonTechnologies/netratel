using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Application.Jobs;
using NetRatel.Application.Scripts;

namespace NetRatel.API.Services.Jobs;

/// <summary>
/// Translates PostgreSQL job-definition steps into fenced gateway task payloads.
/// The gateway authority owns dispatch, lifecycle, and PostgreSQL persistence.
/// </summary>
public sealed class JobTaskBridge
{
    private static readonly JsonSerializerOptions DeserializeOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly ILogger<JobTaskBridge> _logger;
    private readonly IScriptService _scripts;

    public JobTaskBridge(
        IScriptService scripts,
        ILogger<JobTaskBridge> logger)
    {
        _scripts = scripts;
        _logger = logger;
    }

    /// <summary>
    /// Builds the exact client execution envelope for the fenced job gateway.
    /// </summary>
    public async Task<JobGatewayTaskInvocation> BuildGatewayInvocationAsync(
        JobRunInfo run,
        JobStepInfo step,
        IReadOnlyDictionary<string, object?> inputs,
        JobExecutionRuntimePolicy runtimePolicy,
        CancellationToken cancellationToken)
    {
        var build = await TryBuildInvocationAsync(
                run,
                step,
                inputs,
                runtimePolicy,
                cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return build.Success
            ? new(true, build.Invocation.TaskType, build.Invocation.Payload, null)
            : new(false, null, null, build.Error);
    }

    public static bool ResultPayloadHasSuccessfulExitCode(string? resultPayloadJson)
    {
        if (string.IsNullOrWhiteSpace(resultPayloadJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(resultPayloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("exitCode", out var exitCode)
                && exitCode.ValueKind == JsonValueKind.Number
                && exitCode.TryGetInt32(out var code)
                && code == 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<(bool Success, TaskInvocation Invocation, string? Error)> TryBuildInvocationAsync(
        JobRunInfo run,
        JobStepInfo step,
        IReadOnlyDictionary<string, object?> inputs,
        JobExecutionRuntimePolicy runtimePolicy,
        CancellationToken cancellationToken)
    {
        switch (step.Type)
        {
            case JobStepKind.RunCommand:
                {
                    var ok = TryBuildShellInvocation(run, step, inputs, runtimePolicy, out var invocation, out var error);
                    return (ok, invocation, error);
                }

            case JobStepKind.LibraryScript:
                return await TryBuildLibraryScriptInvocationAsync(run, step, inputs, runtimePolicy, cancellationToken).ConfigureAwait(false);

            default:
                return (false, default, $"Unsupported step type {step.Type}.");
        }
    }

    private ExecShellCommandPayload? TryDeserializeShellPayload(string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ExecShellCommandPayload>(payloadJson, DeserializeOptions);
            if (payload is not null && !string.IsNullOrWhiteSpace(payload.Command))
            {
                return payload;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize shell payload json.");
        }

        return null;
    }

    private ExecLibraryScriptPayload? TryDeserializeLibraryPayload(string payloadJson, out Dictionary<string, string>? parameters)
    {
        parameters = null;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return null;
        }

        try
        {
            var payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(payloadJson, DeserializeOptions);
            if (payload is not null)
            {
                parameters = null;
                return payload;
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize library payload json into ExecLibraryScriptPayload.");
        }

        try
        {
            var rawMap = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payloadJson, DeserializeOptions);
            if (rawMap is not null)
            {
                parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var kvp in rawMap)
                {
                    parameters[kvp.Key] = JobTemplateHelper.ConvertValueToString(kvp.Value);
                }
            }
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "Failed to deserialize library payload json into parameter map.");
        }

        return null;
    }

    private int? ResolveScriptId(JobStepInfo step, ExecLibraryScriptPayload payload, out string? error)
    {
        error = null;

        var candidate = payload.ScriptId;
        if (candidate <= 0 && step.ScriptId.HasValue)
        {
            if (step.ScriptId.Value > int.MaxValue)
            {
                error = "ScriptId exceeds supported range.";
                return null;
            }

            candidate = Convert.ToInt32(step.ScriptId.Value);
        }

        if (candidate <= 0)
        {
            error = "ScriptId is required for LibraryScript steps.";
            return null;
        }

        return candidate;
    }

    private bool TryBuildShellInvocation(
        JobRunInfo run,
        JobStepInfo step,
        IReadOnlyDictionary<string, object?> inputs,
        JobExecutionRuntimePolicy runtimePolicy,
        out TaskInvocation invocation,
        out string? error)
    {
        invocation = default;
        error = null;

        ExecShellCommandPayload? payload = null;
        if (!string.IsNullOrWhiteSpace(step.PayloadJson))
        {
            var resolvedPayload = JobTemplateHelper.ResolvePlaceholders(step.PayloadJson!, inputs) ?? string.Empty;
            payload = TryDeserializeShellPayload(resolvedPayload);

            if (payload is null && !string.IsNullOrWhiteSpace(resolvedPayload))
            {
                payload = new ExecShellCommandPayload
                {
                    Command = resolvedPayload
                };
            }
        }

        if (payload is null)
        {
            var resolvedCommand = JobTemplateHelper.ResolvePlaceholders(step.Command, inputs)?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(resolvedCommand))
            {
                error = "Command text is required for RunCommand steps.";
                return false;
            }

            payload = new ExecShellCommandPayload
            {
                Command = resolvedCommand
            };
        }

        if (payload.Preferred == ShellExecutor.Auto)
        {
            payload.Preferred = ParseShellExecutor(step.Runner);
        }

        if (string.IsNullOrWhiteSpace(payload.Command))
        {
            error = "Command text is required for RunCommand steps.";
            return false;
        }

        payload.TimeoutSeconds = runtimePolicy.HardTimeoutSeconds;

        invocation = new TaskInvocation(TaskKinds.ExecShellCommand, JsonSerializer.Serialize(payload));

        _logger.LogDebug(
            "JobRun {RunId} step {Ordinal}: ExecShellCommand payload {@Payload}",
            run.Id,
            step.Ordinal,
            payload);

        return true;
    }

    private async Task<(ScriptInfo? Script, ScriptType ScriptType, ShellExecutor Preferred)> ResolveLibraryExecutionAsync(
        JobStepInfo step,
        ExecLibraryScriptPayload payload,
        CancellationToken cancellationToken)
    {
        var scriptType = payload.ScriptType == default ? ParseScriptType(step.Runner) : payload.ScriptType;
        var preferred = payload.Preferred == ShellExecutor.Auto ? ParseShellExecutor(step.Runner) : payload.Preferred;

        if (payload.ScriptId <= 0)
        {
            return (null, scriptType, preferred);
        }

        var script = await _scripts.GetAsync((ulong)payload.ScriptId, cancellationToken).ConfigureAwait(false);
        if (script is null)
        {
            return (null, scriptType, preferred);
        }

        if (payload.ScriptType == default && Enum.TryParse<ScriptType>(script.ScriptType, true, out var resolvedType))
        {
            scriptType = resolvedType;
        }

        if (payload.Preferred == ShellExecutor.Auto)
        {
            preferred = scriptType switch
            {
                ScriptType.Bash => ShellExecutor.Bash,
                ScriptType.PowerShell => ShellExecutor.Pwsh,
                _ => preferred
            };
        }

        return (script, scriptType, preferred);
    }

    private async Task<(bool Success, TaskInvocation Invocation, string? Error)> TryBuildLibraryScriptInvocationAsync(
        JobRunInfo run,
        JobStepInfo step,
        IReadOnlyDictionary<string, object?> inputs,
        JobExecutionRuntimePolicy runtimePolicy,
        CancellationToken cancellationToken)
    {
        ExecLibraryScriptPayload payload;
        Dictionary<string, string>? payloadParameters = null;
        if (!string.IsNullOrWhiteSpace(step.PayloadJson))
        {
            var resolvedPayload = JobTemplateHelper.ResolvePlaceholders(step.PayloadJson!, inputs) ?? string.Empty;
            payload = TryDeserializeLibraryPayload(resolvedPayload, out payloadParameters) ?? new ExecLibraryScriptPayload();
        }
        else
        {
            payload = new ExecLibraryScriptPayload();
        }

        var scriptId = ResolveScriptId(step, payload, out var idError);
        if (!scriptId.HasValue)
        {
            return (false, default, idError);
        }

        payload.ScriptId = scriptId.Value;
        var execution = await ResolveLibraryExecutionAsync(step, payload, cancellationToken).ConfigureAwait(false);
        if (execution.Script is null)
        {
            return (false, default, $"Script {payload.ScriptId} not found.");
        }

        payload.ScriptType = execution.ScriptType;
        payload.ScriptContent = execution.Script.Content;
        payload.Preferred = execution.Preferred;
        _logger.LogInformation(
            "JobRun {RunId} step {Ordinal}: library script snapshot scriptId={ScriptId} scriptType={ScriptType} updated={UpdatedAtUtc} contentHash={ContentHash}",
            run.Id,
            step.Ordinal,
            payload.ScriptId,
            payload.ScriptType,
            execution.Script.UpdatedAtUtc,
            ComputeContentHash(execution.Script.Content));

        var parameters = BuildLibraryScriptParameters(inputs, payloadParameters, payload.Parameters);

        payload.Parameters = parameters.Count > 0 ? parameters : new Dictionary<string, string>();
        payload.TimeoutSeconds = runtimePolicy.HardTimeoutSeconds;

        _logger.LogInformation(
            "Prepared library script dispatch. RunId={RunId} StepOrdinal={StepOrdinal} TaskRequestPending=true InputKeyCount={InputKeyCount} ExecutableParameterKeyCount={ExecutableParameterKeyCount} ExpectedRuntimeSeconds={ExpectedRuntimeSeconds} GraceSeconds={GraceSeconds} HardTimeoutSeconds={HardTimeoutSeconds}",
            run.Id,
            step.Ordinal,
            inputs.Count,
            payload.Parameters.Count,
            runtimePolicy.ExpectedRuntimeSeconds,
            runtimePolicy.GraceSeconds,
            runtimePolicy.HardTimeoutSeconds);

        var invocation = new TaskInvocation(TaskKinds.ExecLibraryScript, JsonSerializer.Serialize(payload));

        _logger.LogDebug(
            "JobRun {RunId} step {Ordinal}: ExecLibraryScript payload {@Payload}",
            run.Id,
            step.Ordinal,
            payload);

        return (true, invocation, null);
    }

    public static Dictionary<string, string> BuildLibraryScriptParameters(
        IReadOnlyDictionary<string, object?> inputs,
        IReadOnlyDictionary<string, string>? payloadParameters,
        IReadOnlyDictionary<string, string>? stepParameters)
    {
        var parameters = ExtractInputs(inputs);
        if (payloadParameters is not null)
        {
            foreach (var kvp in payloadParameters)
            {
                parameters[kvp.Key] = kvp.Value;
            }
        }

        if (stepParameters is not null)
        {
            foreach (var kvp in stepParameters)
            {
                parameters[kvp.Key] = kvp.Value;
            }
        }

        return parameters;
    }

    private static Dictionary<string, string> ExtractInputs(IReadOnlyDictionary<string, object?> inputs)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in inputs)
        {
            if (IsEnvelopeContainerKey(entry.Key) || !IsScalarParameterValue(entry.Value))
            {
                continue;
            }

            result[entry.Key] = JobTemplateHelper.ConvertValueToString(entry.Value);
        }

        return result;
    }

    private static bool IsEnvelopeContainerKey(string key)
        => string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "input", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "expectedRuntimeSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "graceSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "hardTimeoutSeconds", StringComparison.OrdinalIgnoreCase);

    private static bool IsScalarParameterValue(object? value)
    {
        if (value is null)
        {
            return true;
        }

        if (value is JsonElement element)
        {
            return element.ValueKind is JsonValueKind.String
                or JsonValueKind.Number
                or JsonValueKind.True
                or JsonValueKind.False
                or JsonValueKind.Null;
        }

        return value is string
            or bool
            or byte
            or sbyte
            or short
            or ushort
            or int
            or uint
            or long
            or ulong
            or float
            or double
            or decimal
            or DateTime
            or DateTimeOffset
            or Guid;
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static ShellExecutor ParseShellExecutor(string? runner)
    {
        if (string.IsNullOrWhiteSpace(runner))
        {
            return ShellExecutor.Auto;
        }

        var normalized = runner.Trim().ToLowerInvariant();
        return normalized switch
        {
            "pwsh" or "powershell" or "powershellcore" => ShellExecutor.Pwsh,
            "windows-powershell" or "powershell5" => ShellExecutor.WindowsPowerShell,
            "bash" => ShellExecutor.Bash,
            "cmd" or "commandprompt" => ShellExecutor.Cmd,
            _ when Enum.TryParse<ShellExecutor>(runner, true, out var parsed) => parsed,
            _ => ShellExecutor.Auto
        };
    }

    private static ScriptType ParseScriptType(string? runner)
    {
        if (string.IsNullOrWhiteSpace(runner))
        {
            return ScriptType.PowerShell;
        }

        var normalized = runner.Trim().ToLowerInvariant();
        return normalized switch
        {
            "powershell" or "pwsh" => ScriptType.PowerShell,
            "bash" => ScriptType.Bash,
            "python" or "py" => ScriptType.Python,
            "javascript" or "js" => ScriptType.JavaScript,
            "typescript" or "ts" => ScriptType.TypeScript,
            "sql" => ScriptType.Sql,
            "json" => ScriptType.Json,
            _ when Enum.TryParse<ScriptType>(runner, true, out var parsed) => parsed,
            _ => ScriptType.PowerShell
        };
    }

    private readonly record struct TaskInvocation(string TaskType, string Payload);
}

public sealed record JobGatewayTaskInvocation(
    bool Success,
    string? TaskType,
    string? PayloadJson,
    string? Error);
