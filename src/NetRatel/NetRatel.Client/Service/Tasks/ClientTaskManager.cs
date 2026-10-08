using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Shells;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;
using NetRatel.Shared.Security;
using NetRatel.Shared.Data.Task;

namespace NetRatel.Client.Service.Tasks;

public sealed class ClientTaskManager : IDisposable
{
    private const int MaximumGatewayResultBytes = 48 * 1024;
    private static readonly JsonSerializerOptions GatewayPayloadJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ExternalShellRunner _shellRunner;
    private readonly Action<string> _logError;
    private readonly Func<string, string, string?, int?, Task> _gatewayStatusPublisher;

    private readonly object _lifecycleLock = new();
    // One cancellation owner covers admission, queued work, and execution.
    // There is no handoff gap between a queued-intent map and a running map.
    private readonly ConcurrentDictionary<string, CommandWorkItem> _enqueued = new(StringComparer.Ordinal);

    private BlockingCollection<CommandWorkItem>? _queue;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private bool _started;
    private int? _tenantId;

    private sealed record CommandExecutionContext(
        int? TenantId,
        int Environment,
        string TaskType,
        string? Payload,
        string RequestId);

    private sealed record CommandWorkItem(
        string CommandId,
        string CommandType,
        string? Payload,
        int? TenantId,
        int Environment,
        CancellationTokenSource Cancellation) : IDisposable
    {
        private int _disposed;
        public CancellationToken Token { get; } = Cancellation.Token;
        public bool Cancel()
        {
            try { Cancellation.Cancel(); return true; }
            catch (ObjectDisposedException) { return false; }
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Cancellation.Dispose();
        }
    }

    /// <summary>
    /// Executes command-gateway dispatches. The gateway is its only transport.
    /// </summary>
    public ClientTaskManager(
        Func<string, string, string?, int?, Task> gatewayStatusPublisher,
        Action<string> errorLogger)
        : this(new ExternalShellRunner(), gatewayStatusPublisher, errorLogger)
    {
    }

    internal ClientTaskManager(
        ExternalShellRunner shellRunner,
        Func<string, string, string?, int?, Task> gatewayStatusPublisher,
        Action<string> errorLogger)
    {
        ArgumentNullException.ThrowIfNull(gatewayStatusPublisher);
        _gatewayStatusPublisher = gatewayStatusPublisher;
        _logError = errorLogger ?? Console.Error.WriteLine;
        _shellRunner = shellRunner;
    }

    public void Start(int? tenantId, int environment)
    {
        lock (_lifecycleLock)
        {
            if (_started)
            {
                LogManager.WriteLog("[ClientTaskManager] Start ignored (already running).");
                return;
            }

            _tenantId = tenantId;
            _cts = new CancellationTokenSource();
            _queue = new BlockingCollection<CommandWorkItem>(new ConcurrentQueue<CommandWorkItem>());
            var executionToken = _cts.Token;
            var executionQueue = _queue;
            _runner = Task.Run(() => RunLoopAsync(executionQueue, executionToken), CancellationToken.None);
            _started = true;
        }

        LogManager.WriteLog($"[ClientTaskManager] Gateway execution started. Tenant={_tenantId?.ToString() ?? "null"} Env={environment}.");
    }

    public void Stop()
    {
        Task? runnerToWait = null;

        lock (_lifecycleLock)
        {
            if (!_started)
            {
                return;
            }

            _started = false;
            runnerToWait = _runner;
            _runner = null;
            try { _cts?.Cancel(); } catch { }
            try { _queue?.CompleteAdding(); } catch { }
        }

        try
        {
            runnerToWait?.Wait(2000);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException))
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            _logError($"[ClientTaskManager] Runner wait failed: {ex}");
        }

        lock (_lifecycleLock)
        {
            _cts?.Dispose();
            _cts = null;

            _queue?.Dispose();
            _queue = null;

            foreach (var work in _enqueued.Values) work.Dispose();
            _enqueued.Clear();
        }

        LogManager.WriteLog("[ClientTaskManager] Stopped.");
    }

    public void Dispose()
    {
        Stop();
    }

    public bool EnqueueGatewayCommand(string commandId, string taskType, string? payload, int tenantId, int environment)
    {
        if (string.IsNullOrWhiteSpace(commandId) || string.IsNullOrWhiteSpace(taskType)) return false;
        lock (_lifecycleLock)
        {
            if (!_started || _cts is null || _queue is not { IsAddingCompleted: false } queue) return false;
            var work = new CommandWorkItem(commandId, taskType, payload, tenantId, environment,
                CancellationTokenSource.CreateLinkedTokenSource(_cts.Token));
            if (!_enqueued.TryAdd(commandId, work))
            {
                work.Dispose();
                return false;
            }
            try
            {
                // Register cancellation before making work visible to the consumer.
                queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                RemoveWork(work);
                return false;
            }
        }
        LogManager.WriteLog($"[Command Gateway] Accepted command {commandId} type={taskType} authority=akka-dev-canary");
        return true;
    }

    public bool CancelGatewayCommand(string commandId) =>
        _enqueued.TryGetValue(commandId, out var work) && work.Cancel();

    private void RemoveWork(CommandWorkItem work)
    {
        // A late completion from a stopped runner must not remove a newer
        // command that happens to reuse the same identifier after restart.
        ((ICollection<KeyValuePair<string, CommandWorkItem>>)_enqueued).Remove(new(work.CommandId, work));
        work.Dispose();
    }

    private async Task RunLoopAsync(BlockingCollection<CommandWorkItem> queue, CancellationToken token)
    {
        try
        {
            foreach (var workItem in queue.GetConsumingEnumerable(token))
            {
                token.ThrowIfCancellationRequested();

                try
                {
                    await RunOneAsync(workItem, workItem.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    var context = new CommandExecutionContext(
                        workItem.TenantId,
                        workItem.Environment,
                        workItem.CommandType,
                        workItem.Payload,
                        workItem.CommandId);
                    await PublishCancelledAsync(context, null, "Cancelled").ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logError($"RunOne failed for command {workItem.CommandId}: {ex}");
                    await SafeStatusAsync(
                        new CommandExecutionContext(
                            workItem.TenantId,
                            workItem.Environment,
                            workItem.CommandType,
                            workItem.Payload,
                            workItem.CommandId),
                        TaskStatuses.Failed,
                        "Run failed.",
                        ex.Message).ConfigureAwait(false);
                }
                finally
                {
                    RemoveWork(workItem);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // expected during shutdown
        }
        catch (Exception ex)
        {
            _logError($"[ClientTaskManager] Runner loop faulted: {ex}");
        }
    }

    private async Task RunOneAsync(CommandWorkItem workItem, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var task = new CommandExecutionContext(
            workItem.TenantId,
            workItem.Environment,
            workItem.CommandType,
            workItem.Payload,
            workItem.CommandId);

        await SafeStatusAsync(task, TaskStatuses.Processing, "Starting…").ConfigureAwait(false);

        ITaskLogSink sink = NullTaskLogSink.Instance;

        var rawType = task.TaskType;
        var kind = NormalizeTaskType(rawType);

        switch (kind)
        {
            case TaskKinds.ExecShellCommand:
                if (string.Equals(rawType, TaskKinds.Legacy_RunPowerShell, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleLegacyPowerShellAsync(task, sink, task.Payload, ct).ConfigureAwait(false);
                }
                else if (string.Equals(rawType, TaskKinds.Legacy_ExecPs, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleExecPsAsync(task, sink, ct).ConfigureAwait(false);
                }
                else if (string.Equals(rawType, TaskKinds.Legacy_ExecSh, StringComparison.OrdinalIgnoreCase))
                {
                    await HandleExecShAsync(task, sink, ct).ConfigureAwait(false);
                }
                else
                {
                    await HandleExecShellCmdAsync(task, sink, ct).ConfigureAwait(false);
                }
                break;

            case TaskKinds.ExecLibraryScript:
                await HandleExecLibraryScriptAsync(task, sink, ct).ConfigureAwait(false);
                break;

            case TaskKinds.OsInfo:
                await HandleOsInfoAsync(task, sink, ct).ConfigureAwait(false);
                break;

            case TaskKinds.ProcessesList:
                await HandleProcessesListAsync(task, sink, ct).ConfigureAwait(false);
                break;

            case TaskKinds.ProcessesTopCpu:
                await HandleTopCpuAsync(task, sink, ct).ConfigureAwait(false);
                break;

            case TaskKinds.DiskFree:
                await HandleDiskFreeAsync(task, sink, ct).ConfigureAwait(false);
                break;

            default:
                await sink.AppendAsync("stderr", $"Unknown TaskType '{task.TaskType}'", 0).ConfigureAwait(false);
                await SafeStatusAsync(task, TaskStatuses.Failed, $"Unknown TaskType '{task.TaskType}'").ConfigureAwait(false);
                break;
        }
    }

    private static string NormalizeTaskType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        if (string.Equals(value, TaskKinds.ExecShellCommand, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, TaskKinds.Legacy_RunPowerShell, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, TaskKinds.Legacy_ExecPs, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, TaskKinds.Legacy_ExecSh, StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.ExecShellCommand;
        }

        if (string.Equals(value, TaskKinds.ExecLibraryScript, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, TaskKinds.Legacy_RunLibraryScript, StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.ExecLibraryScript;
        }

        if (string.Equals(value, TaskKinds.OsInfo, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "OsInfo", StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.OsInfo;
        }

        if (string.Equals(value, TaskKinds.ProcessesList, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "ProcessesList", StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.ProcessesList;
        }

        if (string.Equals(value, TaskKinds.ProcessesTopCpu, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "ProcessesTopCpu", StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.ProcessesTopCpu;
        }

        if (string.Equals(value, TaskKinds.DiskFree, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(value, "DiskFree", StringComparison.OrdinalIgnoreCase))
        {
            return TaskKinds.DiskFree;
        }

        return value;
    }

    private async Task HandleExecShellCmdAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(task.Payload))
        {
            await PublishFailureAsync(task, sink, "Missing payload", "Task payload was empty before shell execution.").ConfigureAwait(false);
            return;
        }

        ExecShellCommandPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<ExecShellCommandPayload>(task.Payload, GatewayPayloadJsonOptions);
        }
        catch (JsonException)
        {
            // handled below
        }

        if (payload is null || string.IsNullOrWhiteSpace(payload.Command))
        {
            await PublishFailureAsync(task, sink, "Invalid ExecShellCommandPayload", "Shell payload could not be deserialized or did not contain a command.", task.Payload).ConfigureAwait(false);
            return;
        }

        if (!EnvironmentReferencesAreAvailable(payload.EnvironmentReferences))
        {
            await PublishFailureAsync(task, sink, "Environment reference unavailable", "A required client-local environment reference is unavailable.").ConfigureAwait(false);
            return;
        }

        try
        {
            LogManager.WriteLog($"[Command] Executing shell command requestId={task.RequestId} preferred={payload.Preferred} timeout={payload.TimeoutSeconds?.ToString() ?? "default"}");
            var result = await _shellRunner.RunShellCommandAsync(payload, ct).ConfigureAwait(false);
            LogManager.WriteLog($"[Command] Shell command finished requestId={task.RequestId} exitCode={result.ExitCode} stdout={result.Output.Count} stderr={result.Error.Count} shell={result.ShellPath} cwd={result.WorkingDirectory}");
            if (ct.IsCancellationRequested)
            {
                await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
                return;
            }

            await EmitResultAsync(task, sink, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PublishFailureAsync(task, sink, "Shell command execution failed.", ex.ToString(), task.Payload).ConfigureAwait(false);
        }
    }

    private async Task HandleExecLibraryScriptAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(task.Payload))
        {
            await PublishFailureAsync(task, sink, "Missing payload", "Library script payload was empty before execution.").ConfigureAwait(false);
            return;
        }

        ExecLibraryScriptPayload? payload = null;
        try
        {
            payload = JsonSerializer.Deserialize<ExecLibraryScriptPayload>(task.Payload, GatewayPayloadJsonOptions);
        }
        catch (JsonException)
        {
            // fallback handled below
        }

        if (payload is null)
        {
            await PublishFailureAsync(task, sink, "Invalid ExecLibraryScriptPayload", "Library script payload could not be deserialized.", task.Payload).ConfigureAwait(false);
            return;
        }

        var hasSnapshotContent = !string.IsNullOrEmpty(payload.ScriptContent);
        if (payload.ScriptId <= 0 && !hasSnapshotContent)
        {
            await PublishFailureAsync(task, sink, "Invalid script identifier", $"Resolved script id was '{payload.ScriptId}'.", task.Payload).ConfigureAwait(false);
            return;
        }

        var content = ResolveLibraryScriptContent(payload);

        if (content is null)
        {
            await PublishFailureAsync(
                task,
                sink,
                $"Script {payload.ScriptId} not found",
                $"Library script with id {payload.ScriptId} was not found on the agent and the dispatch payload did not include a script content snapshot.",
                task.Payload).ConfigureAwait(false);
            return;
        }

        IReadOnlyDictionary<string, string>? mergedParams = payload.Parameters;
        if (mergedParams is null || mergedParams.Count == 0)
        {
            // No additional command parameter bag is carried beyond the dispatch payload today.
        }

        try
        {
            LogManager.WriteLog($"[Command] Executing library script requestId={task.RequestId} scriptId={payload.ScriptId} scriptType={payload.ScriptType} preferred={payload.Preferred} contentHash={ComputeContentHash(content)}");
            var result = await _shellRunner.RunLibraryScriptAsync(payload, content, ct, mergedParams).ConfigureAwait(false);
            LogManager.WriteLog($"[Command] Library script finished requestId={task.RequestId} exitCode={result.ExitCode} stdout={result.Output.Count} stderr={result.Error.Count} shell={result.ShellPath} cwd={result.WorkingDirectory}");
            if (ct.IsCancellationRequested)
            {
                await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
                return;
            }

            await EmitResultAsync(task, sink, result).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await PublishFailureAsync(task, sink, "Library script execution failed.", ex.ToString(), task.Payload).ConfigureAwait(false);
        }
    }

    public static string? ResolveLibraryScriptContent(
        ExecLibraryScriptPayload payload)
    {
        return payload.ScriptContent;
    }

    private static bool EnvironmentReferencesAreAvailable(IReadOnlyList<string>? references)
    {
        if (references is null || references.Count == 0)
        {
            return true;
        }

        return references.Count <= 32 && references.All(reference =>
            reference is { Length: > 0 and <= 128 } &&
            (char.IsLetter(reference[0]) || reference[0] == '_') &&
            reference.All(character => char.IsLetterOrDigit(character) || character == '_') &&
            Environment.GetEnvironmentVariable(reference) is not null);
    }

    private async Task EmitResultAsync(CommandExecutionContext task, ITaskLogSink sink, ExternalShellRunner.RunResult res)
    {
        var remainingBytes = MaximumGatewayResultBytes;
        var stdout = BoundOutput(res.Output, ref remainingBytes, out var stdoutTruncated);
        var stderr = BoundOutput(res.Error, ref remainingBytes, out var stderrTruncated);
        await AppendOutputLogsAsync(sink, stdout, stderr).ConfigureAwait(false);

        var payload = JsonSerializer.Serialize(new
        {
            exitCode = res.ExitCode,
            stdout,
            stderr,
            outputTruncated = stdoutTruncated || stderrTruncated,
            diagnostics = new
            {
                shellPath = res.ShellPath,
                arguments = res.Arguments,
                workingDirectory = res.WorkingDirectory,
                durationMs = res.DurationMs
            }
        });
        await SafeStatusAsync(task, res.Success ? TaskStatuses.Completed : TaskStatuses.Failed, res.Success ? "OK" : "Failed", payload, res.ExitCode).ConfigureAwait(false);
    }

    private static async Task AppendOutputLogsAsync(ITaskLogSink sink, IReadOnlyList<string> stdout, IReadOnlyList<string> stderr)
    {
        if (stdout.Count > 0)
        {
            var outputEntries = stdout
                .Select((line, index) => ("stdout", line, (ulong)index + 1))
                .ToList();
            await sink.AppendBatchAsync(outputEntries).ConfigureAwait(false);
        }

        if (stderr.Count > 0)
        {
            var errorEntries = stderr
                .Select((line, index) => ("stderr", line, (ulong)index + 1))
                .ToList();
            await sink.AppendBatchAsync(errorEntries).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<string> BoundOutput(IReadOnlyList<string> lines, ref int remainingBytes, out bool truncated)
    {
        truncated = false;
        var bounded = new List<string>(lines.Count);
        foreach (var line in lines)
        {
            var redacted = OperatorOutputRedactor.Redact(line);
            var bytes = Encoding.UTF8.GetByteCount(redacted);
            if (bytes <= remainingBytes)
            {
                bounded.Add(redacted);
                remainingBytes -= bytes;
                continue;
            }

            truncated = true;
            if (remainingBytes > 0)
            {
                var length = Math.Min(redacted.Length, remainingBytes);
                while (length > 0 && Encoding.UTF8.GetByteCount(redacted.AsSpan(0, length)) > remainingBytes)
                    length--;
                if (length > 0)
                    bounded.Add(redacted[..length]);
            }
            remainingBytes = 0;
            break;
        }
        return bounded;
    }

    private async Task PublishFailureAsync(CommandExecutionContext task, ITaskLogSink sink, string message, string? detail, string? payload = null, int? exitCode = 1)
    {
        var errorText = OperatorOutputRedactor.Redact(string.IsNullOrWhiteSpace(detail) ? message : detail!);
        try
        {
            await sink.AppendAsync("stderr", errorText, 1).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logError($"Failed to append failure log for command {task.RequestId}: {ex}");
        }

        var returnData = JsonSerializer.Serialize(new
        {
            exitCode = exitCode,
            stdout = Array.Empty<string>(),
            stderr = new[] { errorText },
            diagnostics = new
            {
                taskType = task.TaskType,
                payload = ExecLibraryScriptPayloadDiagnostics.Sanitize(task.TaskType, payload),
                failureMessage = message
            }
        });

        await SafeStatusAsync(task, TaskStatuses.Failed, message, returnData, exitCode).ConfigureAwait(false);
    }

    private async Task PublishCancelledAsync(CommandExecutionContext task, ITaskLogSink? sink, string reason)
    {
        try
        {
            if (sink is not null)
            {
                await sink.AppendAsync("stderr", reason, 1).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logError($"Failed to append cancellation log for command {task.RequestId}: {ex}");
        }

        var returnData = JsonSerializer.Serialize(new
        {
            exitCode = -1,
            stdout = Array.Empty<string>(),
            stderr = new[] { reason },
            diagnostics = new
            {
                taskType = task.TaskType,
                cancelled = true
            }
        });

        await SafeStatusAsync(task, TaskStatuses.Cancelled, reason, returnData, -1).ConfigureAwait(false);
    }

    private async Task SafeStatusAsync(CommandExecutionContext task, string status, string? message, string? returnData = null, int? exitCode = null)
    {
        try
        {
            await UpdateTaskStatusAsync(task, status, message, returnData, exitCode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logError($"Status update failed for command {task.RequestId} -> {status}: {ex}");
        }
    }

    private Task UpdateTaskStatusAsync(CommandExecutionContext task, string newStatus, string? message, string? returnData, int? exitCode = null)
    {
        var gatewayStatus = string.Equals(newStatus, TaskStatuses.Running, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(newStatus, TaskStatuses.Processing, StringComparison.OrdinalIgnoreCase)
                ? "started"
                : string.Equals(newStatus, TaskStatuses.Completed, StringComparison.OrdinalIgnoreCase)
                    ? "completed"
                    : string.Equals(newStatus, TaskStatuses.Cancelled, StringComparison.OrdinalIgnoreCase)
                        ? "cancelled"
                        : "failed";
        return _gatewayStatusPublisher(task.RequestId, gatewayStatus, returnData ?? message, exitCode);
    }

    private static string ComputeContentHash(string content)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(content));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private async Task HandleLegacyPowerShellAsync(
        CommandExecutionContext task,
        ITaskLogSink sink,
        string? script,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(script))
        {
            await SafeStatusAsync(task, TaskStatuses.Failed, "Missing PowerShell script", null, 1).ConfigureAwait(false);
            return;
        }

        try
        {
            var (execution, returnData) = await LegacyPowerShellAdapter.ExecuteAsync(
                _shellRunner, script, MaximumGatewayResultBytes, ct).ConfigureAwait(false);
            var remainingBytes = MaximumGatewayResultBytes;
            var stdout = BoundOutput(execution.Output, ref remainingBytes, out _);
            var stderr = BoundOutput(execution.Error, ref remainingBytes, out _);
            await AppendOutputLogsAsync(sink, stdout, stderr).ConfigureAwait(false);
            if (ct.IsCancellationRequested)
            {
                await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
                return;
            }

            if (execution.Success)
            {
                // Persisted legacy dispatches expose an array of object properties or
                // strings through TaskDto.ReturnData. Keep that shape at this boundary.
                await SafeStatusAsync(task, TaskStatuses.Completed, "Execution successful", returnData, 0).ConfigureAwait(false);
            }
            else
            {
                var error = stderr.Count > 0 ? string.Join(Environment.NewLine, stderr) : "PowerShell execution failed.";
                if (error.Length > 1000) error = error[..1000] + "... (truncated)";
                await SafeStatusAsync(task, TaskStatuses.Failed, "Task Failed.", error, execution.ExitCode).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await PublishCancelledAsync(task, sink, "Cancelled").ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await SafeStatusAsync(task, TaskStatuses.Failed, "PowerShell execution failed.",
                OperatorOutputRedactor.Redact(exception.Message), 1).ConfigureAwait(false);
        }
    }

    private Task HandleOsInfoAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();

        var os = System.Runtime.InteropServices.RuntimeInformation.OSDescription;
        var arch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString();
        var host = Environment.MachineName;
        var terms = new List<string>();
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            terms.AddRange(new[] { "powershell", "cmd" });
        }
        else
        {
            terms.AddRange(new[] { "bash", "sh" });
        }

        var payload = new { os, arch, hostname = host, terminals = terms.ToArray() };
        var json = JsonSerializer.Serialize(payload);
        return SafeStatusAsync(task, TaskStatuses.Completed, "OK", json);
    }

    private async Task HandleProcessesListAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();

        int? top = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(task.Payload))
            {
                using var doc = JsonDocument.Parse(task.Payload);
                if (doc.RootElement.TryGetProperty("top", out var t) && t.TryGetInt32(out var ti)) top = ti;
            }
        }
        catch { }

        var procs = Process.GetProcesses();
        var items = new List<object>();
        foreach (var p in procs)
        {
            try
            {
                var name = p.ProcessName;
                var pid = p.Id;
                double cpu = 0;
                double mem = 0;
                try { mem = (double)p.WorkingSet64 / (Environment.WorkingSet + 1) * 100.0; } catch { }
                items.Add(new { pid, name, cpuPercent = cpu, memPercent = mem, cmdline = string.Empty });
            }
            catch { }
        }
        if (top.HasValue)
        {
            items = items.Take(top.Value).ToList();
        }
        var payload = new { items };
        var json = JsonSerializer.Serialize(payload);
        await SafeStatusAsync(task, TaskStatuses.Completed, "OK", json).ConfigureAwait(false);
    }

    private async Task HandleTopCpuAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();

        var procs = Process.GetProcesses();
        var list = new List<object>();
        foreach (var p in procs)
        {
            try
            {
                double cpu = 0;
                double mem = 0;
                try { mem = (double)p.WorkingSet64 / (Environment.WorkingSet + 1) * 100.0; } catch { }
                list.Add(new { pid = p.Id, name = p.ProcessName, cpuPercent = cpu, memPercent = mem });
            }
            catch { }
        }
        var top = list.Take(5).ToList();
        var payload = new { items = top };
        var json = JsonSerializer.Serialize(payload);
        await SafeStatusAsync(task, TaskStatuses.Completed, "OK", json).ConfigureAwait(false);
    }

    private async Task HandleDiskFreeAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();

        string? path = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(task.Payload))
            {
                using var doc = JsonDocument.Parse(task.Payload);
                if (doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                {
                    path = p.GetString();
                }
            }
        }
        catch { }

        var list = new List<object>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                if (!string.IsNullOrWhiteSpace(path) && !drive.Name.Equals(path, StringComparison.OrdinalIgnoreCase)) { }
                long total = drive.TotalSize;
                long free = drive.AvailableFreeSpace;
                double pct = total > 0 ? (double)free / total * 100.0 : 0;
                list.Add(new { mount = drive.Name, total, free, percentFree = pct });
            }
            catch { }
        }
        var json = JsonSerializer.Serialize(new { items = list });
        await SafeStatusAsync(task, TaskStatuses.Completed, "OK", json).ConfigureAwait(false);
    }

    private async Task HandleExecPsAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        string? script = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(task.Payload))
            {
                using var doc = JsonDocument.Parse(task.Payload);
                if (doc.RootElement.TryGetProperty("script", out var s) && s.ValueKind == JsonValueKind.String)
                    script = s.GetString();
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(script))
        {
            await SafeStatusAsync(task, TaskStatuses.Failed, "Missing script", null).ConfigureAwait(false);
            return;
        }
        await HandleLegacyPowerShellAsync(task, sink, script, ct).ConfigureAwait(false);
    }

    private async Task HandleExecShAsync(CommandExecutionContext task, ITaskLogSink sink, CancellationToken ct)
    {
        _ = sink;
        ct.ThrowIfCancellationRequested();
        string? script = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(task.Payload))
            {
                using var doc = JsonDocument.Parse(task.Payload);
                if (doc.RootElement.TryGetProperty("script", out var s) && s.ValueKind == JsonValueKind.String)
                    script = s.GetString();
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(script))
        {
            await SafeStatusAsync(task, TaskStatuses.Failed, "Missing script", null).ConfigureAwait(false);
            return;
        }
        try
        {
            var isWindows = System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows);
            if (isWindows)
            {
                await SafeStatusAsync(task, TaskStatuses.Failed, "sh not available on Windows", null).ConfigureAwait(false);
                return;
            }
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/bin/bash",
                    Arguments = "-lc \"" + script.Replace("\"", "\\\"") + "\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();
            string stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            string stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            process.WaitForExit();
            var payload = new { exitCode = process.ExitCode, stdout, stderr };
            await SafeStatusAsync(
                task,
                process.ExitCode == 0 ? TaskStatuses.Completed : TaskStatuses.Failed,
                process.ExitCode == 0 ? "OK" : "Failed",
                JsonSerializer.Serialize(payload),
                process.ExitCode).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SafeStatusAsync(task, TaskStatuses.Failed, ex.Message, null).ConfigureAwait(false);
        }
    }
}
