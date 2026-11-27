using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Channels;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Shared.Security;
using NetRatel.Client.Service.Logging;
using NetRatel.Client.Service.Tasks;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Secrets;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;

namespace NetRatel.Client.Service.Shells
{
    public enum ShellKind { Pwsh, WindowsPowerShell, Bash, Cmd }

    public sealed class ExternalShellRunner
    {
        private readonly Func<string, string?> _resolveExecutable;
        private readonly Action<Process> _terminateProcess;
        private string? FindShell(string keyword) => _resolveExecutable(keyword);

        public sealed class RunResult
        {
            public int? ExitCode { get; set; }
            public bool ProcessStarted { get; set; }
            public bool Cancelled { get; set; }
            public bool TimedOut { get; set; }
            public bool OutputTruncated { get; set; }
            public bool OutcomeUnknown { get; set; }
            internal bool ProtectedInputDeliveryFailed { get; set; }
            // Stderr also carries warnings and progress; the process exit code owns success.
            public bool Success => ExitCode == 0 && !Cancelled && !TimedOut && !OutcomeUnknown && !ProtectedInputDeliveryFailed;
            public List<string> Output { get; } = new();
            public List<string> Error { get; } = new();
            public string? ShellPath { get; set; }
            public string? Arguments { get; set; }
            public string? WorkingDirectory { get; set; }
            public double DurationMs { get; set; }
            public ObservedExecutionIdentity? ObservedIdentity { get; set; }
        }

        public sealed class ExecutionOutcomeUnknownException(string message, RunResult execution, Exception cause)
            : IOException(message, cause)
        {
            public RunResult Execution { get; } = execution;
        }

        internal sealed class RequiredShellUnavailableException(string shell, string message) : InvalidOperationException(message)
        {
            public string Shell { get; } = shell;
        }

        public sealed record ExecutionObserver(Func<Task> Started, Func<string, string, Task> Output,
            Func<string, string>? RedactOutput = null)
        {
            internal RunAsCredential? Credential { get; init; }
        }

        private readonly TimeSpan _defaultTimeout;

        public ExternalShellRunner(TimeSpan? defaultTimeout = null)
            : this(defaultTimeout, ShellExecutableResolver.Resolve)
        {
        }

        internal ExternalShellRunner(TimeSpan? defaultTimeout, Func<string, string?> resolveExecutable, Action<Process>? terminateProcess = null)
        {
            _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
            _resolveExecutable = resolveExecutable;
            _terminateProcess = terminateProcess ?? (process => process.Kill(entireProcessTree: true));
        }

        public Task<RunResult> RunShellCommandAsync(ExecShellCommandPayload payload, CancellationToken ct, ExecutionObserver? observer = null)
            => RunByPreferenceAsync(payload.Preferred, payload.Command, payload.WorkingDirectory, payload.TimeoutSeconds, ct, observer);

        public Task<RunResult> RunLibraryScriptAsync(
            ExecLibraryScriptPayload payload,
            string scriptContent,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters,
            ExecutionObserver? observer = null)
            => RunScriptByTypeAsync(payload.ScriptType, payload.Preferred, scriptContent, payload.WorkingDirectory, payload.TimeoutSeconds, ct, parameters, observer);

        internal Task<RunResult> RunProtectedLibraryScriptAsync(ExecLibraryScriptPayload payload, string content, CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters, IReadOnlyDictionary<string, string> protectedParameters, ExecutionObserver observer)
        {
            if (!ProtectedJobParameterRules.AreValid(protectedParameters))
                throw new InvalidOperationException("The protected script parameters are invalid.");
            return payload.ScriptType switch
            {
                ScriptType.PowerShell => RunPowerShellScriptFileAsync(payload.Preferred, content, payload.WorkingDirectory, payload.TimeoutSeconds,
                    ct, parameters, observer: observer, protectedParameters: protectedParameters),
                ScriptType.Bash when payload.Preferred is ShellExecutor.Auto or ShellExecutor.Bash =>
                    RunProtectedBashScriptAsync(payload, content, ct, parameters, protectedParameters, observer),
                _ => throw new InvalidOperationException("Protected parameters require a PowerShell or Bash library script with its matching executor.")
            };
        }

        public Task<RunResult> RunPowerShellScriptAsync(
            string content,
            ShellExecutor preferred,
            string? workingDirectory,
            int? timeoutSeconds,
            CancellationToken ct,
            ExecutionObserver? observer = null)
            => RunPowerShellScriptFileAsync(preferred, content, workingDirectory, timeoutSeconds, ct, parameters: null, observer: observer);

        internal Task<RunResult> RunPowerShellScriptAsync(
            string content,
            ShellExecutor preferred,
            string? workingDirectory,
            int? timeoutSeconds,
            CancellationToken ct,
            string requirementsContent,
            ExecutionObserver? observer = null)
            => RunPowerShellScriptFileAsync(preferred, content, workingDirectory, timeoutSeconds, ct,
                parameters: null, requirementsContent, observer);

        private Task<RunResult> RunByPreferenceAsync(ShellExecutor pref, string command, string? cwd, int? timeoutSec, CancellationToken ct, ExecutionObserver? observer)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSec ?? (int)_defaultTimeout.TotalSeconds);

            switch (pref)
            {
                case ShellExecutor.Pwsh: return RunPwshCommandAsync(command, cwd, timeout, ct, observer);
                case ShellExecutor.WindowsPowerShell: return RunWinPSCommandAsync(command, cwd, timeout, ct, observer);
                case ShellExecutor.Bash: return RunBashCommandAsync(command, cwd, timeout, ct, observer);
                case ShellExecutor.Cmd: return RunCmdCommandAsync(command, cwd, timeout, ct, observer);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (FindShell("pwsh") is not null || FindShell("powershell") is not null)
                    return RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.Auto, command, FindShell, isWindows: true), command, timeout, cwd, ct, observer);
                return RunCmdCommandAsync(command, cwd, timeout, ct, observer);
            }

            return RunBashCommandAsync(command, cwd, timeout, ct, observer, allowSh: true);
        }

        private Task<RunResult> RunScriptByTypeAsync(
            ScriptType type,
            ShellExecutor pref,
            string content,
            string? cwd,
            int? timeoutSec,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters,
            ExecutionObserver? observer)
        {
            switch (type)
            {
                case ScriptType.PowerShell:
                    return RunPowerShellScriptFileAsync(pref, content, cwd, timeoutSec, ct, parameters, observer: observer);
                case ScriptType.Bash:
                    if (pref is ShellExecutor.Auto or ShellExecutor.Bash)
                    {
                        return RunBashScriptAsync(content, cwd, timeoutSec, ct, parameters, observer, allowSh: pref == ShellExecutor.Auto);
                    }

                    return RunByPreferenceAsync(pref, content, cwd, timeoutSec, ct, observer);
                case ScriptType.Python:
                case ScriptType.JavaScript:
                case ScriptType.TypeScript:
                case ScriptType.Sql:
                case ScriptType.Json:
                    return RunByPreferenceAsync(
                        pref == ShellExecutor.Auto
                            ? OperatingSystem.IsWindows() ? ShellExecutor.Cmd : ShellExecutor.Bash
                            : pref,
                        content, cwd, timeoutSec, ct, observer);
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
            IReadOnlyDictionary<string, string>? parameters,
            string? requirementsContent = null,
            ExecutionObserver? observer = null,
            IReadOnlyDictionary<string, string>? protectedParameters = null)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSec ?? (int)_defaultTimeout.TotalSeconds);
            if (observer?.Credential is not null)
                return await RunPowerShellFromPipeAsync(
                    ResolvePowerShellHost(pref, requirementsContent ?? content, FindShell, OperatingSystem.IsWindows()),
                    content, cwd, timeout, ct, observer, parameters, protectedParameters).ConfigureAwait(false);
            var scriptPath = WritePrivateTemp(".ps1", content);
            string? wrapperPath = null;

            try
            {
                var shellPath = ResolvePowerShellHost(pref, requirementsContent ?? content, FindShell, OperatingSystem.IsWindows());
                var hasParameters = parameters is { Count: > 0 } || protectedParameters is not null;
                var windowsPowerShell = OperatingSystem.IsWindows() &&
                    Path.GetFileName(shellPath).Equals("powershell.exe", StringComparison.OrdinalIgnoreCase);
                if (hasParameters || windowsPowerShell)
                {
                    static string Esc(string? value) => (value ?? string.Empty).Replace("'", "''");

                    var wrapper = new StringBuilder();
                    wrapper.Append(PowerShellUtf8Output);
                    if (hasParameters)
                    {
                        wrapper.AppendLine("$ErrorActionPreference = 'Stop'");
                        wrapper.AppendLine("$ProgressPreference = 'SilentlyContinue'");
                        wrapper.AppendLine("$__netratelParams = @{}");
                        foreach (var kv in parameters?.AsEnumerable() ?? [])
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
                        if (protectedParameters is not null)
                        {
                            wrapper.AppendLine("try {");
                            wrapper.AppendLine("    [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)");
                            wrapper.AppendLine("    $__netratelSecret = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd()) -ErrorAction Stop");
                            wrapper.AppendLine("} catch { [Console]::Error.WriteLine('Protected parameters could not be read.'); exit 126 }");
                            wrapper.AppendLine("if ($null -eq $__netratelSecret) { exit 126 }");
                            wrapper.AppendLine("foreach ($p in $__netratelSecret.PSObject.Properties) { $__netratelParams[$p.Name] = $p.Value }");
                        }
                        wrapper.Append("& '").Append(Esc(scriptPath)).AppendLine("' @__netratelParams");
                        wrapper.AppendLine("exit $LASTEXITCODE");
                    }
                    else
                    {
                        // Dot-sourcing keeps an explicit script exit in the outer
                        // host, without turning a native command's status into a
                        // script exit code when the original script did not do so.
                        wrapper.Append(". '").Append(Esc(scriptPath)).AppendLine("'");
                    }

                    wrapperPath = WritePrivateTemp(".ps1", wrapper.ToString());
                }

                var fileToRun = wrapperPath ?? scriptPath;
                var argsText = $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File {QuoteArgument(fileToRun)}";
                LogManager.WriteLog($"[Shell] {shellPath} {MaskArgumentsForLog(argsText)}");
                var protectedInput = protectedParameters is null ? null : JsonSerializer.Serialize(protectedParameters, ClientShellJsonContext.Default.ParameterMap);
                return await StartAsync(shellPath, argsText, cwd, timeout, ct, outputEncoding: Encoding.UTF8, observer: observer,
                    protectedInput: protectedInput).ConfigureAwait(false);
            }
            finally
            {
                TryDeletePrivateTemp(scriptPath);
                if (wrapperPath is not null)
                {
                    TryDeletePrivateTemp(wrapperPath);
                }
            }
        }

        private Task<RunResult> RunPwshCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct, ExecutionObserver? observer)
            => RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.Pwsh, command, FindShell, OperatingSystem.IsWindows()), command, timeout, cwd, ct, observer);

        private Task<RunResult> RunWinPSCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct, ExecutionObserver? observer)
            => RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.WindowsPowerShell, command, FindShell, OperatingSystem.IsWindows()), command, timeout, cwd, ct, observer);

        private Task<RunResult> RunBashCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct,
            ExecutionObserver? observer, bool allowSh = false)
            => RunDirectAsync(FindShell("bash") ?? (allowSh ? FindShell("sh") : null) ?? Throw(allowSh ? "bash/sh" : "bash"),
                "-lc", command, timeout, cwd, ct, observer);

        private Task<RunResult> RunBashScriptAsync(
            string script,
            string? cwd,
            int? timeoutSeconds,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters,
            ExecutionObserver? observer,
            bool allowSh)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? (int)_defaultTimeout.TotalSeconds);
            var environment = CreateBashParameterEnvironment(parameters);
            var shellPath = FindShell("bash") ?? (allowSh ? FindShell("sh") : null) ?? Throw(allowSh ? "bash/sh" : "bash");
            return RunDirectAsync(shellPath, "-lc", script, timeout, cwd, ct, observer, environment);
        }

        private async Task<RunResult> RunProtectedBashScriptAsync(ExecLibraryScriptPayload payload, string content, CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters, IReadOnlyDictionary<string, string> protectedParameters, ExecutionObserver observer)
        {
            if (protectedParameters.Values.Any(value => value.Contains('\0')))
                throw new InvalidOperationException("Bash protected parameters cannot contain a NUL character.");
            var shellPath = FindShell("bash") ?? Throw("bash");
            var script = new StringBuilder();
            var input = new StringBuilder();
            foreach (var (name, value) in protectedParameters)
            {
                var variable = $"NETRATEL_SECRET_{name}";
                script.Append("unset -v ").Append(variable).AppendLine(" || exit 126");
                script.Append("IFS= read -r -d '' ").Append(variable).AppendLine(" || exit 126");
                input.Append(value).Append('\0');
            }
            script.Append(content);
            var timeout = TimeSpan.FromSeconds(payload.TimeoutSeconds ?? (int)_defaultTimeout.TotalSeconds);
            if (observer.Credential is not null)
                return await StartAsync(shellPath, $"--noprofile --norc -p -c {QuoteArgument(script.ToString())}", payload.WorkingDirectory,
                    timeout, ct, CreateBashParameterEnvironment(parameters), observer: observer, protectedInput: input.ToString()).ConfigureAwait(false);
            var scriptPath = WritePrivateTemp(".sh", script.ToString());
            try
            {
                return await StartAsync(shellPath, $"--noprofile --norc -p -- {QuoteArgument(scriptPath)}", payload.WorkingDirectory,
                    timeout, ct, CreateBashParameterEnvironment(parameters), observer: observer, protectedInput: input.ToString()).ConfigureAwait(false);
            }
            finally { TryDeletePrivateTemp(scriptPath); }
        }

        private static Dictionary<string, string>? CreateBashParameterEnvironment(
            IReadOnlyDictionary<string, string>? parameters)
        {
            if (parameters is not { Count: > 0 }) return null;

            var environment = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, value) in parameters)
            {
                var name = key.Trim();
                if (name.Length == 0 || !(char.IsAsciiLetter(name[0]) || name[0] == '_'))
                {
                    throw new InvalidOperationException("Bash library script parameters must use valid environment variable names.");
                }

                if (name.Skip(1).Any(character => !(char.IsAsciiLetterOrDigit(character) || character == '_')))
                {
                    throw new InvalidOperationException("Bash library script parameters must use valid environment variable names.");
                }

                environment[name] = value;
            }

            return environment;
        }

        private Task<RunResult> RunCmdCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct, ExecutionObserver? observer)
        {
            var path = FindShell("cmd") ?? Throw("cmd");
            return RunDirectAsync(path, "/d /s /c", command, timeout, cwd, ct, observer);
        }

        private static string Throw(string what, string? keyword = null) =>
            throw new RequiredShellUnavailableException(keyword ?? what, $"Required shell not found: {what}");

        private static string WrapPSBlock(string userCode)
        {
            var sb = new StringBuilder();
            sb.Append(PowerShellUtf8Output);
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

        private const string PowerShellUtf8Output =
            "[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)\n$OutputEncoding = [Console]::OutputEncoding\n";

        internal static string WritePrivateTemp(string ext, string contents)
            => WritePrivateTemp(ext, contents, tempRootOverride: null);

        internal static string WritePrivateTemp(string ext, string contents, string? tempRootOverride)
        {
            var privateDirectory = CreatePrivateTempDirectory(tempRootOverride);
            try
            {
                var path = Path.Combine(privateDirectory, $"netratel_{Guid.NewGuid():N}{ext}");
                // Windows PowerShell 5.1 reads BOM-less scripts as the system ANSI
                // encoding. Both external engines understand a UTF-8 script BOM.
                var powerShellScript = ext.Equals(".ps1", StringComparison.OrdinalIgnoreCase);
                var scriptContents = powerShellScript && contents.StartsWith('\uFEFF') ? contents[1..] : contents;
                var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
                if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                using var writer = new StreamWriter(path, new UTF8Encoding(powerShellScript), options);
                writer.Write(scriptContents);
                return path;
            }
            catch
            {
                TryDeletePrivateTempDirectory(privateDirectory);
                throw;
            }
        }

        private const UnixFileMode OwnerOnlyDirectoryMode =
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        private const UnixFileMode StickyDirectoryMode = (UnixFileMode)0x200;

        private static string CreatePrivateTempDirectory(string? tempRootOverride)
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var localApplicationData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrWhiteSpace(localApplicationData) || !Directory.Exists(localApplicationData))
                {
                    throw new InvalidOperationException("The current Windows user's local application data directory is unavailable.");
                }

                var owner = GetCurrentWindowsSid();
                ValidateWindowsPrivateDirectory(localApplicationData, owner, protectContents: false);
                var applicationRoot = Path.Combine(localApplicationData, "NetRatel");
                EnsureWindowsPrivateDirectory(applicationRoot, owner);
                var privateRoot = Path.Combine(applicationRoot, "PrivateParameters");
                EnsureWindowsPrivateDirectory(privateRoot, owner);
                var windowsExecutionDirectory = Path.Combine(privateRoot, $"netratel_private_{Guid.NewGuid():N}");
                new DirectoryInfo(windowsExecutionDirectory).Create(CreateWindowsPrivateSecurity(owner));
                try
                {
                    ValidateWindowsPrivateDirectory(windowsExecutionDirectory, owner, protectContents: true);
                    return windowsExecutionDirectory;
                }
                catch
                {
                    TryDeletePrivateTempDirectory(windowsExecutionDirectory);
                    throw;
                }
            }

            var root = OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp";
            ValidateTrustedUnixTemporaryDirectory(root, tempRootOverride);
            var executionDirectory = Path.Combine(root, $"netratel_private_{Guid.NewGuid():N}");
            if (MakeUnixDirectory(executionDirectory, (int)OwnerOnlyDirectoryMode) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                throw new IOException($"Could not create a private temporary directory (errno {error}).");
            }

            try
            {
                if (ReadUnixFileStatus(executionDirectory).Uid != GetUnixEffectiveUserId())
                {
                    throw new UnauthorizedAccessException("The private temporary directory is not owned by the current Unix identity.");
                }

                File.SetUnixFileMode(executionDirectory, OwnerOnlyDirectoryMode);
                return executionDirectory;
            }
            catch
            {
                TryDeletePrivateTempDirectory(executionDirectory);
                throw;
            }
        }

        private static void ValidateTrustedUnixTemporaryDirectory(string expectedRoot, string? tempRootOverride)
        {
            var configuredRoot = tempRootOverride ?? expectedRoot;
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                throw new InvalidOperationException("The operating system temporary directory is unavailable.");
            }
            var candidate = new DirectoryInfo(Path.GetFullPath(configuredRoot));
            if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            {
                throw new PlatformNotSupportedException("Private script files require a supported Linux or macOS temporary directory.");
            }

            if (!string.Equals(candidate.FullName, expectedRoot, StringComparison.Ordinal))
            {
                throw new UnauthorizedAccessException("Private script files require the canonical system temporary directory.");
            }

            if (!candidate.Exists)
            {
                throw new InvalidOperationException("The operating system temporary directory does not exist.");
            }

            if ((candidate.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("The canonical system temporary directory must not be a symbolic link.");
            }

            var ancestor = new DirectoryInfo(Path.GetPathRoot(candidate.FullName)!);
            ValidateUnixTemporaryAncestor(ancestor.FullName, allowSharedWrite: false);
            var ancestorPath = ancestor.FullName;
            foreach (var component in Path.GetRelativePath(ancestorPath, candidate.FullName)
                         .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
            {
                ancestorPath = Path.Combine(ancestorPath, component);
                var isTemporaryRoot = string.Equals(ancestorPath, candidate.FullName, StringComparison.Ordinal);
                ValidateUnixTemporaryAncestor(ancestorPath, isTemporaryRoot);
            }
        }

        private static void ValidateUnixTemporaryAncestor(string path, bool allowSharedWrite)
        {
            var status = ReadUnixFileStatus(path);
            if ((status.Mode & UnixFileTypeMask) != UnixDirectoryFileType)
            {
                throw new UnauthorizedAccessException("The system temporary directory path contains a missing directory or symbolic link.");
            }

            if (status.Uid != 0)
            {
                throw new UnauthorizedAccessException("The system temporary directory path must be owned by the operating system administrator.");
            }

            var mode = (UnixFileMode)(status.Mode & UnixPermissionMask);
            var sharedWrite = UnixFileMode.GroupWrite | UnixFileMode.OtherWrite;
            if ((mode & sharedWrite) != 0 &&
                (!allowSharedWrite || (mode & StickyDirectoryMode) == 0))
            {
                throw new UnauthorizedAccessException("A system temporary directory ancestor is writable by other users.");
            }
        }

        private const int UnixFileTypeMask = 0xF000;
        private const int UnixDirectoryFileType = 0x4000;
        private const int UnixPermissionMask = 0xFFF;

        // Keep this layout in sync with .NET 10's normalized Interop.Sys.FileStatus
        // in System.Native. The application targets net10.0; this avoids depending on
        // Linux/Darwin struct stat ABI offsets. Recheck on a target framework upgrade.
        [StructLayout(LayoutKind.Sequential)]
        private struct UnixFileStatus
        {
            public int Flags;
            public int Mode;
            public uint Uid;
            public uint Gid;
            public long Size;
            public long ATime;
            public long ATimeNsec;
            public long MTime;
            public long MTimeNsec;
            public long CTime;
            public long CTimeNsec;
            public long BirthTime;
            public long BirthTimeNsec;
            public long Dev;
            public long RDev;
            public long Ino;
            public uint UserFlags;
        }

        [DllImport("libc", EntryPoint = "geteuid", SetLastError = true)]
        private static extern uint GetUnixEffectiveUserId();

        [DllImport("System.Native", EntryPoint = "SystemNative_LStat", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int LStatUnixPath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out UnixFileStatus status);

        [DllImport("System.Native", EntryPoint = "SystemNative_MkDir", SetLastError = true, CharSet = CharSet.Ansi)]
        private static extern int MakeUnixDirectory([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int mode);

        private static UnixFileStatus ReadUnixFileStatus(string path)
        {
            if (LStatUnixPath(path, out var status) != 0)
            {
                throw new IOException($"Could not verify system temporary directory metadata (errno {Marshal.GetLastPInvokeError()}).");
            }

            return status;
        }

        [SupportedOSPlatform("windows")]
        private static SecurityIdentifier GetCurrentWindowsSid()
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
        }

        [SupportedOSPlatform("windows")]
        internal static void EnsureWindowsPrivateDirectory(string path, SecurityIdentifier owner)
        {
            var directory = new DirectoryInfo(path);
            if (directory.Exists)
            {
                // A service or elevated account may have created the directory with
                // SYSTEM or Administrators as owner. Validate its ACL before repair.
                var security = ValidateWindowsPrivateDirectory(path, owner, protectContents: true, requireProtected: false);
                if (!security.AreAccessRulesProtected || !owner.Equals(security.GetOwner(typeof(SecurityIdentifier))))
                    directory.SetAccessControl(CreateWindowsPrivateSecurity(owner));
            }
            else directory.Create(CreateWindowsPrivateSecurity(owner));

            ValidateWindowsPrivateDirectory(path, owner, protectContents: true);
        }

        [SupportedOSPlatform("windows")]
        private static DirectorySecurity ValidateWindowsPrivateDirectory(string path, SecurityIdentifier owner, bool protectContents, bool requireProtected = true)
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("A private temporary directory is missing or is a reparse point.");
            }

            var security = info.GetAccessControl();
            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var requireExecutionOwner = protectContents && requireProtected;
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier directoryOwner ||
                !directoryOwner.Equals(owner) && (requireExecutionOwner || !directoryOwner.Equals(system) && !directoryOwner.Equals(administrators)))
            {
                throw new UnauthorizedAccessException("The Windows temporary-directory owner is outside the allowed identities.");
            }

            if (protectContents && requireProtected && !security.AreAccessRulesProtected)
            {
                throw new UnauthorizedAccessException("A private temporary directory must not inherit access rules from its parent.");
            }

            var creatorOwner = new SecurityIdentifier(WellKnownSidType.CreatorOwnerSid, null);
            var allowedWriters = new[] { owner, system, administrators, creatorOwner };
            var protectedRights = FileSystemRights.Write | FileSystemRights.Delete |
                                  FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions |
                                  FileSystemRights.TakeOwnership;
            if (protectContents)
            {
                protectedRights |= FileSystemRights.Read | FileSystemRights.ReadAndExecute;
            }
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            foreach (FileSystemAccessRule rule in rules)
            {
                if (rule.AccessControlType != AccessControlType.Allow || (rule.FileSystemRights & protectedRights) == 0)
                {
                    continue;
                }

                if (!allowedWriters.Any(sid => sid.Equals((SecurityIdentifier)rule.IdentityReference)))
                {
                    throw new UnauthorizedAccessException("A private temporary directory grants write access to an untrusted Windows identity.");
                }
            }
            return security;
        }

        [SupportedOSPlatform("windows")]
        private static DirectorySecurity CreateWindowsPrivateSecurity(SecurityIdentifier currentSid)
        {
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var security = new DirectorySecurity();
            security.SetOwner(currentSid);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[] { currentSid, systemSid, administratorsSid })
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    sid,
                    FileSystemRights.FullControl,
                    inheritance,
                    PropagationFlags.None,
                    AccessControlType.Allow));
            }
            return security;
        }

        internal static void TryDeletePrivateTemp(string path)
        {
            TryDelete(path);
            var directory = Path.GetDirectoryName(path);
            if (directory is not null && Path.GetFileName(directory).StartsWith("netratel_private_", StringComparison.Ordinal))
                TryDeletePrivateTempDirectory(directory);
        }

        private static void TryDeletePrivateTempDirectory(string path)
        {
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                LogManager.WriteLog($"[Shell] Private parameter temp cleanup failed ({exception.GetType().Name}); the protected directory remains in place.");
            }
        }

        private async Task<RunResult> RunViaTempPs1Async(string shellPath, string command, TimeSpan timeout, string? cwd, CancellationToken ct, ExecutionObserver? observer)
        {
            if (observer?.Credential is not null)
                return await RunPowerShellFromPipeAsync(shellPath, WrapPSBlock(command), cwd, timeout, ct, observer).ConfigureAwait(false);
            var ps1 = WritePrivateTemp(".ps1", WrapPSBlock(command));
            try
            {
                var args = new StringBuilder("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ");

                args.Append("-File ").Append(QuoteArgument(ps1));
                var argsText = args.ToString();
                LogManager.WriteLog($"[Shell] {shellPath} {MaskArgumentsForLog(argsText)}");
                return await StartAsync(shellPath, argsText, cwd, timeout, ct, outputEncoding: Encoding.UTF8, observer: observer).ConfigureAwait(false);
            }
            finally
            {
                TryDeletePrivateTemp(ps1);
            }
        }

        private Task<RunResult> RunPowerShellFromPipeAsync(string executable, string script, string? cwd, TimeSpan timeout,
            CancellationToken ct, ExecutionObserver observer, IReadOnlyDictionary<string, string>? parameters = null,
            IReadOnlyDictionary<string, string>? protectedParameters = null)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in parameters ?? new Dictionary<string, string>())
            {
                var name = key.Trim().TrimStart('-');
                if (name.Length > 0) values[name] = value;
            }
            if (protectedParameters is not null)
                foreach (var (name, value) in protectedParameters) values[name] = value;
            var input = JsonSerializer.Serialize(new RunAsPowerShellInput(script, values), ClientShellJsonContext.Default.RunAsPowerShellInput);
            const string bootstrap = """
                [Console]::InputEncoding = [Text.UTF8Encoding]::new($false)
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                $ErrorActionPreference = 'Stop'
                $ProgressPreference = 'SilentlyContinue'
                try {
                    $request = ConvertFrom-Json -InputObject ([Console]::In.ReadToEnd())
                    $arguments = @{}
                    foreach ($property in $request.Parameters.PSObject.Properties) { $arguments[$property.Name] = $property.Value }
                    $script = [scriptblock]::Create($request.Script)
                } catch { [Console]::Error.WriteLine('The private script input could not be read.'); exit 126 }
                # An in-memory script block does not get the script file engine checks.
                $requirements = $script.Ast.ScriptRequirements
                if ($null -ne $requirements -and (
                    ($requirements.RequiredPSVersion -and $PSVersionTable.PSVersion -lt $requirements.RequiredPSVersion) -or
                    ($requirements.RequiredPSEditions -and $PSVersionTable.PSEdition -notin $requirements.RequiredPSEditions))) {
                    [Console]::Error.WriteLine('The selected PowerShell engine does not satisfy the script edition or version requirements.')
                    exit 125
                }
                & $script @arguments
                if ($null -ne $LASTEXITCODE) { exit $LASTEXITCODE }
                """;
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
            return StartAsync(executable, $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                cwd, timeout, ct, outputEncoding: Encoding.UTF8, observer: observer, protectedInput: input);
        }

        private Task<RunResult> RunDirectAsync(
            string shellPath,
            string argPrefix,
            string command,
            TimeSpan timeout,
            string? cwd,
            CancellationToken ct,
            ExecutionObserver? observer,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            string args = $"{argPrefix} {QuoteArgument(command)}";
            return StartAsync(shellPath, args, cwd, timeout, ct, environment, observer: observer);
        }

        internal static string ResolvePowerShellHost(
            ShellExecutor preference,
            string script,
            Func<string, string?> resolve,
            bool isWindows)
        {
            if (preference == ShellExecutor.Pwsh)
                return resolve("pwsh") ?? Throw("pwsh");
            if (preference == ShellExecutor.WindowsPowerShell)
            {
                if (!isWindows) throw new RequiredShellUnavailableException("powershell", "Windows PowerShell requires Windows; install and select pwsh on this platform.");
                return resolve("powershell") ?? Throw("Windows PowerShell", "powershell");
            }
            if (preference != ShellExecutor.Auto)
                throw new InvalidOperationException($"PowerShell scripts require a PowerShell executor, not {preference}.");

            var (requiresDesktop, requiresCore) = PowerShellScriptRequirements.Read(script);

            if (requiresDesktop)
            {
                if (!isWindows) throw new RequiredShellUnavailableException("powershell", "The script requires Windows PowerShell (Desktop edition), which is unavailable on this platform.");
                if (requiresCore) throw new InvalidOperationException("The script requires incompatible PowerShell Desktop and Core versions or editions.");
                return resolve("powershell") ?? Throw("Windows PowerShell required by #requires -PSEdition Desktop", "powershell");
            }

            var pwsh = resolve("pwsh");
            if (pwsh is not null) return pwsh;
            if (isWindows && !requiresCore)
                return resolve("powershell") ?? Throw("pwsh or Windows PowerShell", "powershell-compatible");
            return Throw(requiresCore ? "pwsh required by the script's #requires engine/version directive" : "pwsh", "pwsh");
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
            if (!string.IsNullOrWhiteSpace(cwd))
            {
                if (!Directory.Exists(cwd))
                    throw new DirectoryNotFoundException("The selected working directory does not exist.");
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

        private async Task<RunResult> StartAsync(
            string fileName,
            string arguments,
            string? cwd,
            TimeSpan timeout,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null,
            Encoding? outputEncoding = null,
            ExecutionObserver? observer = null,
            string? protectedInput = null)
        {
            ct.ThrowIfCancellationRequested();
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > uint.MaxValue - 1)
                throw new InvalidOperationException("Execution timeout must be positive and supported by the process timer.");
            var credential = observer?.Credential;
            using var windowsAccount = OperatingSystem.IsWindows() && credential?.Mode == RunAsCredential.Types.Mode.WindowsPassword
                ? WindowsExecutionAccount.LogOn(credential, terminal: false) : null;
            var unixAccount = credential?.Mode == RunAsCredential.Types.Mode.UnixPrivilegedLocal
                ? await UnixExecutionAccount.ResolveAsync(credential, ct).ConfigureAwait(false) : null;
            if (credential is not null && windowsAccount is null && unixAccount is null)
                throw new InvalidOperationException("The selected execution account mode is unavailable on this client.");
            var res = new RunResult();
            var resolvedCwd = ResolveWorkingDirectory(cwd);
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = resolvedCwd,
                UseShellExecute = false,
                RedirectStandardInput = protectedInput is not null || credential is not null,
                StandardInputEncoding = protectedInput is null ? null : new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = outputEncoding,
                StandardErrorEncoding = outputEncoding,
                CreateNoWindow = true
            };
            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                {
                    psi.Environment[key] = value;
                }
            }
            res.ShellPath = fileName;
            res.Arguments = arguments;
            res.WorkingDirectory = resolvedCwd;

            unixAccount?.PrepareJob(psi, cwd, environment);
            using var windowsProcess = OperatingSystem.IsWindows() && windowsAccount is not null
                ? WindowsAccountJobProcess.Start(windowsAccount, psi,
                    string.IsNullOrWhiteSpace(cwd) ? null : cwd, environment, outputEncoding ?? Encoding.UTF8) : null;
            using var proc = OperatingSystem.IsWindows() && windowsProcess is not null
                ? windowsProcess.Process : new Process { StartInfo = psi, EnableRaisingEvents = true };
            var sw = Stopwatch.StartNew();

            var tcsOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tcsErr = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            // One serial publisher orders both streams. Capture and the publication
            // queue share a fixed budget so a noisy child cannot grow memory forever.
            var output = Channel.CreateUnbounded<(string Stream, string Text)>();
            var captureLock = new object();
            var remainingBytes = 48 * 1024;
            var remainingLines = 1024;
            var captureStopped = false;
            void Capture(string stream, string? text, TaskCompletionSource end)
            {
                if (text is null) { end.TrySetResult(); return; }
                lock (captureLock)
                {
                    if (captureStopped) return;
                    if (remainingBytes == 0 || remainingLines == 0)
                    {
                        res.OutputTruncated = true;
                        return;
                    }
                    // Redact the complete process line before truncation, bounded
                    // capture, or queueing; truncation must not retain a secret prefix.
                    text = observer?.RedactOutput?.Invoke(text) ?? OperatorOutputRedactor.Redact(text);
                    var length = Math.Min(text.Length, Math.Min(4096, remainingBytes));
                    while (length > 0 && Encoding.UTF8.GetByteCount(text.AsSpan(0, length)) > remainingBytes) length--;
                    if (length < text.Length) res.OutputTruncated = true;
                    // Do not split the UTF-16 pair of a Unicode character.
                    if (length > 0 && length < text.Length && char.IsHighSurrogate(text[length - 1])) length--;
                    text = text[..length];
                    remainingBytes -= Encoding.UTF8.GetByteCount(text);
                    remainingLines--;
                    (stream == "stdout" ? res.Output : res.Error).Add(text);
                    output.Writer.TryWrite((stream, text));
                }
            }
            if (!(OperatingSystem.IsWindows() && windowsProcess is not null) && !proc.Start())
                throw new InvalidOperationException($"Failed to start: {fileName}");
            var stdout = OperatingSystem.IsWindows() && windowsProcess is not null ? windowsProcess.Output : proc.StandardOutput;
            var stderr = OperatingSystem.IsWindows() && windowsProcess is not null ? windowsProcess.Error : proc.StandardError;
            if (unixAccount is not null)
            {
                try { await unixAccount.VerifyJobAsync(stderr, ct).ConfigureAwait(false); }
                catch
                {
                    if (!proc.HasExited) await ShutdownUnverifiedProcessAsync(proc).ConfigureAwait(false);
                    throw;
                }
            }
            res.ProcessStarted = true;
            res.ObservedIdentity = OperatingSystem.IsWindows() && windowsProcess is not null ? windowsProcess.Identity :
                unixAccount?.Identity ?? NativeProcessIdentity.ObserveStartedProcess(proc);
            async Task PublishOutputAsync()
            {
                if (observer is not null) await observer.Started().ConfigureAwait(false);
                await foreach (var line in output.Reader.ReadAllAsync())
                    if (observer is not null) await observer.Output(line.Stream, line.Text).ConfigureAwait(false);
            }
            var publisher = PublishOutputAsync();
            async Task<ExecutionOutcomeUnknownException> UnknownOutcomeAsync(Exception cause)
            {
                lock (captureLock)
                {
                    // Freeze the bounded snapshot before detaching a process whose
                    // physical termination could not be proven.
                    captureStopped = true;
                    res.OutcomeUnknown = true;
                    res.Cancelled = false;
                    res.TimedOut = false;
                    res.ExitCode = null;
                    res.Error.Add("Native process termination could not be confirmed; the physical outcome is unknown.");
                }
                sw.Stop();
                res.DurationMs = sw.Elapsed.TotalMilliseconds;
                output.Writer.TryComplete();
                await publisher.ConfigureAwait(false);
                return new ExecutionOutcomeUnknownException("Native process termination could not be confirmed.", res, cause);
            }
            async Task DrainAsync(StreamReader reader, string stream, TaskCompletionSource done)
            {
                try
                {
                    while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line) Capture(stream, line, done);
                }
                finally { done.TrySetResult(); }
            }
            var outputDrain = DrainAsync(stdout, "stdout", tcsOut);
            var errorDrain = DrainAsync(stderr, "stderr", tcsErr);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            var inputStream = OperatingSystem.IsWindows() && windowsProcess is not null ? windowsProcess.Input :
                psi.RedirectStandardInput ? proc.StandardInput.BaseStream : null;
            if (protectedInput is null) inputStream?.Dispose();
            var inputSent = protectedInput is null ? Task.FromResult(true) : SendProtectedInputAsync(inputStream!, protectedInput, cts.Token);

            try
            {
                res.ProtectedInputDeliveryFailed = !await inputSent.WaitAsync(cts.Token).ConfigureAwait(false);
                await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
                res.Cancelled = ct.IsCancellationRequested;
                res.TimedOut = !res.Cancelled;
                try { if (!proc.HasExited) _terminateProcess(proc); }
                catch (InvalidOperationException) when (proc.HasExited)
                {
                    LogManager.WriteLog("[Shell] Process exited before cancellation termination; awaiting redirected-stream cleanup.");
                }
                catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                    throw await UnknownOutcomeAsync(exception).ConfigureAwait(false);
                }
                // Do not reuse the cancelled execution token for cleanup. Reap the
                // process and drain both redirected streams, with an independent bound.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try
                {
                    await proc.WaitForExitAsync(cleanup.Token).ConfigureAwait(false);
                    await Task.WhenAll(tcsOut.Task, tcsErr.Task).WaitAsync(cleanup.Token).ConfigureAwait(false);
                    try { await inputSent.WaitAsync(cleanup.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!cleanup.IsCancellationRequested)
                    {
                        res.ProtectedInputDeliveryFailed = true;
                    }
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    throw await UnknownOutcomeAsync(new IOException("Shell process termination or redirected-stream cleanup exceeded its five-second bound.")).ConfigureAwait(false);
                }
                // A killed process has no command exit receipt; never fabricate -1.
                res.ExitCode = null;
                sw.Stop();
                res.DurationMs = sw.Elapsed.TotalMilliseconds;
                output.Writer.TryComplete();
                await publisher.ConfigureAwait(false);
                lock (res.Error) res.Error.Add(res.Cancelled ? "Cancelled" : $"Timed out after {timeout.TotalSeconds:n0}s");
                return res;
            }

            await Task.WhenAll(outputDrain, errorDrain).ConfigureAwait(false);
            sw.Stop();
            res.ExitCode = proc.ExitCode;
            if (res.ProtectedInputDeliveryFailed) res.Error.Add("The private input pipe closed before the protected parameters were delivered.");
            res.DurationMs = sw.Elapsed.TotalMilliseconds;
            output.Writer.TryComplete();
            await publisher.ConfigureAwait(false);
            if (res.ExitCode != 0 && res.Output.Count == 0 && res.Error.Count == 0)
            {
                res.Error.Add($"Process exited with code {res.ExitCode} without emitting stdout/stderr.");
            }
            return res;
        }

        private static async Task ShutdownUnverifiedProcessAsync(Process process)
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { return; }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }

        private static async Task<bool> SendProtectedInputAsync(Stream stream, string input, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(input);
            try
            {
                using (stream)
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                return true;
            }
            catch (IOException) { return false; }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
