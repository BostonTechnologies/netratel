using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Powershell;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;

namespace NetRatel.Client.Service.Shells
{
    public enum ShellKind { Pwsh, WindowsPowerShell, Bash, Cmd }

    public sealed class ExternalShellRunner
    {
        // Live view over the singleton inventory
        private ShellInventory Inv => ClientRuntime.Shells;

        public sealed class RunResult
        {
            public int ExitCode { get; set; }
            // Stderr also carries warnings and progress; the process exit code owns success.
            public bool Success => ExitCode == 0;
            public List<string> Output { get; } = new();
            public List<string> Error { get; } = new();
            public string? ShellPath { get; set; }
            public string? Arguments { get; set; }
            public string? WorkingDirectory { get; set; }
            public double DurationMs { get; set; }
        }

        private readonly TimeSpan _defaultTimeout;

        public ExternalShellRunner(TimeSpan? defaultTimeout = null)
        {
            _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
        }

        public Task<RunResult> RunShellCommandAsync(ExecShellCommandPayload payload, CancellationToken ct)
            => RunByPreferenceAsync(payload.Preferred, payload.Command, payload.WorkingDirectory, payload.TimeoutSeconds, ct);

        public Task<RunResult> RunLibraryScriptAsync(
            ExecLibraryScriptPayload payload,
            string scriptContent,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters)
            => RunScriptByTypeAsync(payload.ScriptType, payload.Preferred, scriptContent, payload.WorkingDirectory, payload.TimeoutSeconds, ct, parameters);

        private Task<RunResult> RunByPreferenceAsync(ShellExecutor pref, string command, string? cwd, int? timeoutSec, CancellationToken ct)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSec ?? (int)_defaultTimeout.TotalSeconds);

            switch (pref)
            {
                case ShellExecutor.Pwsh: return RunPwshCommandAsync(command, cwd, timeout, ct);
                case ShellExecutor.WindowsPowerShell: return RunWinPSCommandAsync(command, cwd, timeout, ct);
                case ShellExecutor.Bash: return RunBashCommandAsync(command, cwd, timeout, ct);
                case ShellExecutor.Cmd: return RunCmdCommandAsync(command, cwd, timeout, ct);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var bestPs = Inv.Find("pwsh") ?? Inv.Find("powershell");
                if (bestPs?.Keyword.Equals("pwsh", StringComparison.OrdinalIgnoreCase) == true)
                    return RunPwshCommandAsync(command, cwd, timeout, ct);
                if (bestPs != null)
                    return RunWinPSCommandAsync(command, cwd, timeout, ct);
                return RunCmdCommandAsync(command, cwd, timeout, ct);
            }

            return RunBashCommandAsync(command, cwd, timeout, ct);
        }

        private Task<RunResult> RunScriptByTypeAsync(
            ScriptType type,
            ShellExecutor pref,
            string content,
            string? cwd,
            int? timeoutSec,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters)
        {
            switch (type)
            {
                case ScriptType.PowerShell:
                    return RunPowerShellScriptFileAsync(pref, content, cwd, timeoutSec, ct, parameters);
                case ScriptType.Bash:
                    return RunByPreferenceAsync(pref == ShellExecutor.Auto ? ShellExecutor.Bash : pref, content, cwd, timeoutSec, ct);
                case ScriptType.Python:
                case ScriptType.JavaScript:
                case ScriptType.TypeScript:
                case ScriptType.Sql:
                case ScriptType.Json:
                    return RunByPreferenceAsync(
                        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? ShellExecutor.Cmd : ShellExecutor.Bash,
                        content, cwd, timeoutSec, ct);
                default:
                    throw new NotSupportedException($"Unsupported ScriptType: {type}");
            }
        }

        private async Task<RunResult> RunPowerShellScriptFileAsync(
            ShellExecutor pref,
            string content,
            string? cwd,
            int? timeoutSec,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSec ?? (int)_defaultTimeout.TotalSeconds);
            var scriptPath = WriteTemp(".ps1", content);
            string? wrapperPath = null;
            var (shellPath, _) = ResolvePowerShellHost(pref);

            try
            {
                if (parameters is { Count: > 0 })
                {
                    static string Esc(string? value) => (value ?? string.Empty).Replace("'", "''");

                    var wrapper = new StringBuilder();
                    wrapper.AppendLine("$ErrorActionPreference = 'Stop'");
                    wrapper.AppendLine("$ProgressPreference = 'SilentlyContinue'");
                    wrapper.AppendLine("$__netratelParams = @{}");

                    foreach (var kv in parameters)
                    {
                        if (string.IsNullOrWhiteSpace(kv.Key)) continue;
                        var name = kv.Key.Trim().TrimStart('-');
                        if (name.Length == 0) continue;
                        wrapper.Append("$__netratelParams['")
                               .Append(Esc(name))
                               .Append("'] = '")
                               .Append(Esc(kv.Value))
                               .AppendLine("'");
                    }

                    wrapper.Append("& '")
                           .Append(Esc(scriptPath))
                           .AppendLine("' @__netratelParams");
                    wrapper.AppendLine("exit $LASTEXITCODE");

                    wrapperPath = WriteTemp(".ps1", wrapper.ToString());
                }

                var fileToRun = wrapperPath ?? scriptPath;
                var argsText = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {QuoteArgument(fileToRun)}";
                LogManager.WriteLog($"[Shell] {shellPath} {MaskArgumentsForLog(argsText)}");
                return await StartAsync(shellPath, argsText, cwd, timeout, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(scriptPath);
                if (wrapperPath is not null)
                {
                    TryDelete(wrapperPath);
                }
            }
        }

        private Task<RunResult> RunPwshCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunViaTempPs1Async(Inv.Find("pwsh")?.Path ?? Throw("pwsh"), command, timeout, cwd, ct, isPwsh: true);

        private Task<RunResult> RunWinPSCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunViaTempPs1Async(Inv.Find("powershell")?.Path ?? Throw("powershell"), command, timeout, cwd, ct, isPwsh: false);

        private Task<RunResult> RunBashCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunDirectAsync((Inv.Find("bash")?.Path ?? Inv.Find("sh")?.Path) ?? Throw("bash/sh"), "-lc", command, timeout, cwd, ct);

        private Task<RunResult> RunCmdCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
        {
            var cap = Inv.Find("cmd");
            var path = cap?.Path ?? Environment.GetEnvironmentVariable("ComSpec"); // e.g., C:\Windows\System32\cmd.exe
            if (string.IsNullOrWhiteSpace(path) || (!path.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(path)))
            {
                // last resort: let PATH resolve
                path = "cmd.exe";
            }
            return RunDirectAsync(path, "/d /s /c", command, timeout, cwd, ct);
        }

        private static string Throw(string what) => throw new InvalidOperationException($"Required shell not found: {what}");

        private static string WrapPSBlock(string userCode)
        {
            var sb = new StringBuilder();
            sb.AppendLine("$ErrorActionPreference = 'Stop'");
            sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
            sb.AppendLine("try {");
            sb.AppendLine(userCode);
            sb.AppendLine("  exit 0");
            sb.AppendLine("} catch {");
            sb.AppendLine("  Write-Error $_");
            sb.AppendLine("  if ($LASTEXITCODE -eq $null) { exit 1 } else { exit $LASTEXITCODE }");
            sb.AppendLine("}");
            return sb.ToString();
        }

        private static string WriteTemp(string ext, string contents)
        {
            var dir = EnsureTempDirectory();
            var path = Path.Combine(dir, $"netratel_{Guid.NewGuid():N}{ext}");
            File.WriteAllText(path, contents, new UTF8Encoding(false));
            return path;
        }

        private static string EnsureTempDirectory()
        {
            string? root = null;
            try { root = Path.GetTempPath(); } catch { }

            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    root = string.IsNullOrWhiteSpace(windowsDir)
                        ? Path.Combine("C:", "Windows", "Temp")
                        : Path.Combine(windowsDir, "Temp");
                }
                else
                {
                    root = "/tmp";
                }
            }

            var target = Path.Combine(root!, "netratel");
            Directory.CreateDirectory(target);
            return target;
        }

        private async Task<RunResult> RunViaTempPs1Async(string shellPath, string command, TimeSpan timeout, string? cwd, CancellationToken ct, bool isPwsh)
        {
            var ps1 = WriteTemp(".ps1", WrapPSBlock(command));
            try
            {
                var args = new StringBuilder("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ");

                args.Append("-File ").Append(QuoteArgument(ps1));
                var argsText = args.ToString();
                LogManager.WriteLog($"[Shell] {shellPath} {MaskArgumentsForLog(argsText)}");
                return await StartAsync(shellPath, argsText, cwd, timeout, ct).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(ps1);
            }
        }

        private Task<RunResult> RunDirectAsync(string shellPath, string argPrefix, string command, TimeSpan timeout, string? cwd, CancellationToken ct)
        {
            string args = $"{argPrefix} {QuoteArgument(command)}";
            return StartAsync(shellPath, args, cwd, timeout, ct);
        }

        private (string Path, bool IsPwsh) ResolvePowerShellHost(ShellExecutor preference)
        {
            if (preference == ShellExecutor.Pwsh)
            {
                var explicitPwsh = Inv.Find("pwsh")?.Path;
                if (!string.IsNullOrWhiteSpace(explicitPwsh))
                {
                    return (explicitPwsh!, true);
                }
            }
            else if (preference == ShellExecutor.WindowsPowerShell)
            {
                var explicitWinPs = Inv.Find("powershell")?.Path;
                if (!string.IsNullOrWhiteSpace(explicitWinPs))
                {
                    return (explicitWinPs!, false);
                }

                var resolvedWin = TryResolveExecutable(OperatingSystem.IsWindows() ? "powershell.exe" : "powershell");
                if (!string.IsNullOrWhiteSpace(resolvedWin))
                {
                    return (resolvedWin!, false);
                }
            }

            var pwsh = Inv.Find("pwsh")?.Path;
            if (!string.IsNullOrWhiteSpace(pwsh))
            {
                return (pwsh!, true);
            }

            var winPs = Inv.Find("powershell")?.Path;
            if (!string.IsNullOrWhiteSpace(winPs))
            {
                return (winPs!, false);
            }

            return FindPwshOrWindowsPowerShell();
        }

        private static (string Path, bool IsPwsh) FindPwshOrWindowsPowerShell()
        {
            var pwshNames = OperatingSystem.IsWindows()
                ? new[] { "pwsh.exe", "pwsh" }
                : new[] { "pwsh", "pwsh.exe" };
            foreach (var name in pwshNames)
            {
                var resolved = TryResolveExecutable(name);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return (resolved!, true);
                }
            }

            var winPsNames = OperatingSystem.IsWindows()
                ? new[] { "powershell.exe", "powershell" }
                : new[] { "powershell" };
            foreach (var name in winPsNames)
            {
                var resolved = TryResolveExecutable(name);
                if (!string.IsNullOrWhiteSpace(resolved))
                {
                    return (resolved!, false);
                }
            }

            var fallback = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh";
            var isPwsh = !fallback.Contains("power", StringComparison.OrdinalIgnoreCase);
            return (fallback, isPwsh);
        }

        private static string? TryResolveExecutable(string candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            if (Path.IsPathRooted(candidate) && File.Exists(candidate))
            {
                return candidate;
            }

            var pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(pathEnv))
            {
                return null;
            }

            foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var trimmed = segment.Trim();
                    if (trimmed.Length == 0) continue;
                    var probe = Path.Combine(trimmed, candidate);
                    if (File.Exists(probe))
                    {
                        return probe;
                    }
                }
                catch
                {
                    // ignore path resolution errors
                }
            }

            return null;
        }

        private static string QuoteArgument(string? value)
        {
            var safe = (value ?? string.Empty).Replace("\"", "\\\"");
            return $"\"{safe}\"";
        }

        private static readonly Regex SensitiveArgPattern = new(@"-(?<name>[A-Za-z0-9_]+)\s+""[^""]*""", RegexOptions.Compiled);

        private static string MaskArgumentsForLog(string args)
        {
            if (string.IsNullOrWhiteSpace(args))
            {
                return string.Empty;
            }

            return SensitiveArgPattern.Replace(args, match =>
            {
                var name = match.Groups["name"].Value;
                if (string.Equals(name, "File", StringComparison.OrdinalIgnoreCase))
                {
                    return match.Value;
                }

                return $"-{name} \"***\"";
            });
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore cleanup failures
            }
        }

        private static string ResolveWorkingDirectory(string? cwd)
        {
            if (!string.IsNullOrWhiteSpace(cwd) && Directory.Exists(cwd))
            {
                return cwd;
            }

            try
            {
                var current = Directory.GetCurrentDirectory();
                if (Directory.Exists(current))
                {
                    return current;
                }
            }
            catch
            {
                // ignore
            }

            var systemDir = Environment.SystemDirectory;
            if (!string.IsNullOrWhiteSpace(systemDir) && Directory.Exists(systemDir))
            {
                return systemDir;
            }

            try
            {
                var temp = Path.GetTempPath();
                if (!string.IsNullOrWhiteSpace(temp) && Directory.Exists(temp))
                {
                    return temp;
                }
            }
            catch
            {
                // ignore
            }

            return AppContext.BaseDirectory ?? ".";
        }

        private async Task<RunResult> StartAsync(string fileName, string arguments, string? cwd, TimeSpan timeout, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var res = new RunResult();
            var resolvedCwd = ResolveWorkingDirectory(cwd);
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = resolvedCwd,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            res.ShellPath = fileName;
            res.Arguments = arguments;
            res.WorkingDirectory = resolvedCwd;

            using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            var sw = Stopwatch.StartNew();

            var tcsOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsErr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            proc.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    tcsOut.TrySetResult();
                    return;
                }
                lock (res.Output) res.Output.Add(e.Data);
            };

            proc.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null)
                {
                    tcsErr.TrySetResult();
                    return;
                }
                lock (res.Error) res.Error.Add(e.Data);
            };

            if (!proc.Start()) throw new InvalidOperationException($"Failed to start: {fileName}");
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            try
            {
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) when (proc.HasExited)
                {
                    LogManager.WriteLog("[Shell] Process exited before cancellation termination; awaiting redirected-stream cleanup.");
                }
                // Do not reuse the cancelled execution token for cleanup. Reap the
                // process and drain both redirected streams, with an independent bound.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await proc.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    await Task.WhenAll(tcsOut.Task, tcsErr.Task).WaitAsync(cleanup.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    throw new IOException("Shell process termination or redirected-stream cleanup exceeded its five-second bound.");
                }
                res.ExitCode = -1;
                lock (res.Error) res.Error.Add(ct.IsCancellationRequested ? "Cancelled" : $"Timed out after {timeout.TotalSeconds:n0}s");
                sw.Stop();
                res.DurationMs = sw.Elapsed.TotalMilliseconds;
                return res;
            }

            await Task.WhenAll(tcsOut.Task, tcsErr.Task);
            sw.Stop();
            res.ExitCode = proc.ExitCode;
            res.DurationMs = sw.Elapsed.TotalMilliseconds;
            if (res.ExitCode != 0 && res.Output.Count == 0 && res.Error.Count == 0)
            {
                res.Error.Add($"Process exited with code {res.ExitCode} without emitting stdout/stderr.");
            }
            return res;
        }
    }
}
