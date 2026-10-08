using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;
using NetRatel.Shared.Contracts.Execution;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Service.Shells;

namespace NetRatel.Client.Service.Shells
{
    public enum ShellKind { Pwsh, WindowsPowerShell, Bash, Cmd }

    public sealed class ExternalShellRunner
    {
        private readonly Func<string, string?> _resolveExecutable;
        private string? FindShell(string keyword) => _resolveExecutable(keyword);

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
            : this(defaultTimeout, ShellExecutableResolver.Resolve)
        {
        }

        internal ExternalShellRunner(TimeSpan? defaultTimeout, Func<string, string?> resolveExecutable)
        {
            _defaultTimeout = defaultTimeout ?? TimeSpan.FromMinutes(5);
            _resolveExecutable = resolveExecutable;
        }

        public Task<RunResult> RunShellCommandAsync(ExecShellCommandPayload payload, CancellationToken ct)
            => RunByPreferenceAsync(payload.Preferred, payload.Command, payload.WorkingDirectory, payload.TimeoutSeconds, ct);

        public Task<RunResult> RunLibraryScriptAsync(
            ExecLibraryScriptPayload payload,
            string scriptContent,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters)
            => RunScriptByTypeAsync(payload.ScriptType, payload.Preferred, scriptContent, payload.WorkingDirectory, payload.TimeoutSeconds, ct, parameters);

        public Task<RunResult> RunPowerShellScriptAsync(
            string content,
            ShellExecutor preferred,
            string? workingDirectory,
            int? timeoutSeconds,
            CancellationToken ct)
            => RunPowerShellScriptFileAsync(preferred, content, workingDirectory, timeoutSeconds, ct, parameters: null);

        internal Task<RunResult> RunPowerShellScriptAsync(
            string content,
            ShellExecutor preferred,
            string? workingDirectory,
            int? timeoutSeconds,
            CancellationToken ct,
            string requirementsContent)
            => RunPowerShellScriptFileAsync(preferred, content, workingDirectory, timeoutSeconds, ct,
                parameters: null, requirementsContent);

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
                if (FindShell("pwsh") is not null || FindShell("powershell") is not null)
                    return RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.Auto, command, FindShell, isWindows: true), command, timeout, cwd, ct);
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
                    var bashPreference = pref == ShellExecutor.Auto ? ShellExecutor.Bash : pref;
                    if (bashPreference == ShellExecutor.Bash)
                    {
                        return RunBashScriptAsync(content, cwd, timeoutSec, ct, parameters);
                    }

                    return RunByPreferenceAsync(bashPreference, content, cwd, timeoutSec, ct);
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
            IReadOnlyDictionary<string, string>? parameters,
            string? requirementsContent = null)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSec ?? (int)_defaultTimeout.TotalSeconds);
            var scriptPath = WritePrivateTemp(".ps1", content);
            string? wrapperPath = null;

            try
            {
                var shellPath = ResolvePowerShellHost(pref, requirementsContent ?? content, FindShell, OperatingSystem.IsWindows());
                var hasParameters = parameters is { Count: > 0 };
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
                        foreach (var kv in parameters!)
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
                return await StartAsync(shellPath, argsText, cwd, timeout, ct, outputEncoding: Encoding.UTF8).ConfigureAwait(false);
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

        private Task<RunResult> RunPwshCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.Pwsh, command, FindShell, OperatingSystem.IsWindows()), command, timeout, cwd, ct);

        private Task<RunResult> RunWinPSCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunViaTempPs1Async(ResolvePowerShellHost(ShellExecutor.WindowsPowerShell, command, FindShell, OperatingSystem.IsWindows()), command, timeout, cwd, ct);

        private Task<RunResult> RunBashCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
            => RunDirectAsync((FindShell("bash") ?? FindShell("sh")) ?? Throw("bash/sh"), "-lc", command, timeout, cwd, ct);

        private Task<RunResult> RunBashScriptAsync(
            string script,
            string? cwd,
            int? timeoutSeconds,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? parameters)
        {
            var timeout = TimeSpan.FromSeconds(timeoutSeconds ?? (int)_defaultTimeout.TotalSeconds);
            var environment = CreateBashParameterEnvironment(parameters);
            var shellPath = (FindShell("bash") ?? FindShell("sh")) ?? Throw("bash/sh");
            return RunDirectAsync(shellPath, "-lc", script, timeout, cwd, ct, environment);
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

        private Task<RunResult> RunCmdCommandAsync(string command, string? cwd, TimeSpan timeout, CancellationToken ct)
        {
            var path = FindShell("cmd") ?? Throw("cmd");
            return RunDirectAsync(path, "/d /s /c", command, timeout, cwd, ct);
        }

        private static string Throw(string what) => throw new InvalidOperationException($"Required shell not found: {what}");

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
                File.WriteAllText(path, scriptContents, new UTF8Encoding(powerShellScript));
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
                Directory.CreateDirectory(windowsExecutionDirectory);
                try
                {
                    ProtectWindowsPrivateDirectory(windowsExecutionDirectory);
                    ValidateWindowsPrivateDirectory(windowsExecutionDirectory, owner, protectContents: true);
                    return windowsExecutionDirectory;
                }
                catch
                {
                    TryDeletePrivateTempDirectory(windowsExecutionDirectory);
                    throw;
                }
            }

            var configuredRoot = tempRootOverride ?? (OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp");
            if (string.IsNullOrWhiteSpace(configuredRoot))
            {
                throw new InvalidOperationException("The operating system temporary directory is unavailable.");
            }

            var root = ResolveTrustedUnixTemporaryDirectory(configuredRoot);
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

        private static string ResolveTrustedUnixTemporaryDirectory(string configuredRoot)
        {
            var candidate = new DirectoryInfo(Path.GetFullPath(configuredRoot));
            var expectedRoot = OperatingSystem.IsMacOS() ? "/private/tmp" : "/tmp";
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

            return candidate.FullName;
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
        private static void EnsureWindowsPrivateDirectory(string path, SecurityIdentifier owner)
        {
            if (Directory.Exists(path))
            {
                ValidateWindowsPrivateDirectory(path, owner, protectContents: true);
                return;
            }

            Directory.CreateDirectory(path);
            ProtectWindowsPrivateDirectory(path);
            ValidateWindowsPrivateDirectory(path, owner, protectContents: true);
        }

        [SupportedOSPlatform("windows")]
        private static void ValidateWindowsPrivateDirectory(string path, SecurityIdentifier owner, bool protectContents)
        {
            var info = new DirectoryInfo(path);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new UnauthorizedAccessException("A private temporary directory is missing or is a reparse point.");
            }

            var security = info.GetAccessControl();
            if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier directoryOwner || !directoryOwner.Equals(owner))
            {
                throw new UnauthorizedAccessException("A private temporary directory is not owned by the current Windows identity.");
            }

            if (protectContents && !security.AreAccessRulesProtected)
            {
                throw new UnauthorizedAccessException("A private temporary directory must not inherit access rules from its parent.");
            }

            var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
            var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
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
        }

        [SupportedOSPlatform("windows")]
        private static void ProtectWindowsPrivateDirectory(string path)
        {
            using var identity = WindowsIdentity.GetCurrent();
            var currentSid = identity.User ?? throw new InvalidOperationException("The current Windows identity has no user SID.");
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
            new DirectoryInfo(path).SetAccessControl(security);
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

        private async Task<RunResult> RunViaTempPs1Async(string shellPath, string command, TimeSpan timeout, string? cwd, CancellationToken ct)
        {
            var ps1 = WritePrivateTemp(".ps1", WrapPSBlock(command));
            try
            {
                var args = new StringBuilder("-NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass ");

                args.Append("-File ").Append(QuoteArgument(ps1));
                var argsText = args.ToString();
                LogManager.WriteLog($"[Shell] {shellPath} {MaskArgumentsForLog(argsText)}");
                return await StartAsync(shellPath, argsText, cwd, timeout, ct, outputEncoding: Encoding.UTF8).ConfigureAwait(false);
            }
            finally
            {
                TryDeletePrivateTemp(ps1);
            }
        }

        private Task<RunResult> RunDirectAsync(
            string shellPath,
            string argPrefix,
            string command,
            TimeSpan timeout,
            string? cwd,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null)
        {
            string args = $"{argPrefix} {QuoteArgument(command)}";
            return StartAsync(shellPath, args, cwd, timeout, ct, environment);
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
                if (!isWindows) throw new InvalidOperationException("Windows PowerShell requires Windows; install and select pwsh on this platform.");
                return resolve("powershell") ?? Throw("Windows PowerShell");
            }
            if (preference != ShellExecutor.Auto)
                throw new InvalidOperationException($"PowerShell scripts require a PowerShell executor, not {preference}.");

            // Only choose an engine for the supported #requires engine/version hints.
            // The selected external engine still parses and validates the original
            // script, including all other #requires options and exact versions.
            var requiresDesktop = false;
            var requiresCore = false;
            foreach (var options in ReadPowerShellRequirements(script))
            {
                var edition = PowerShellEditionPattern.Match(options).Groups["edition"].Value;
                requiresDesktop |= edition.Equals("Desktop", StringComparison.OrdinalIgnoreCase);
                requiresCore |= edition.Equals("Core", StringComparison.OrdinalIgnoreCase);
                var versionText = PowerShellVersionPattern.Match(options).Groups["version"].Value;
                if (Version.TryParse(versionText.Contains('.') ? versionText : versionText + ".0", out var version) &&
                    (version.Major > 5 || version.Major == 5 && version.Minor > 1)) requiresCore = true;
            }

            if (requiresDesktop)
            {
                if (!isWindows) throw new InvalidOperationException("The script requires Windows PowerShell (Desktop edition), which is unavailable on this platform.");
                if (requiresCore) throw new InvalidOperationException("The script requires incompatible PowerShell Desktop and Core versions or editions.");
                return resolve("powershell") ?? Throw("Windows PowerShell required by #requires -PSEdition Desktop");
            }

            var pwsh = resolve("pwsh");
            if (pwsh is not null) return pwsh;
            if (isWindows && !requiresCore)
                return resolve("powershell") ?? Throw("pwsh or Windows PowerShell");
            return Throw(requiresCore ? "pwsh required by the script's #requires engine/version directive" : "pwsh");
        }

        private static IEnumerable<string> ReadPowerShellRequirements(string script)
        {
            using var reader = new StringReader(script);
            var blockCommentDepth = 0;
            char? quote = null;
            string? hereStringEnd = null;
            while (reader.ReadLine() is { } line)
            {
                var offset = 0;
                if (hereStringEnd is not null)
                {
                    if (!line.StartsWith(hereStringEnd, StringComparison.Ordinal)) continue;
                    offset = hereStringEnd.Length;
                    hereStringEnd = null;
                }
                for (var index = offset; index < line.Length; index++)
                {
                    var character = line[index];
                    var next = index + 1 < line.Length ? line[index + 1] : '\0';
                    if (blockCommentDepth > 0)
                    {
                        if (character == '<' && next == '#') { blockCommentDepth++; index++; }
                        else if (character == '#' && next == '>') { blockCommentDepth--; index++; }
                        continue;
                    }
                    if (quote is not null)
                    {
                        if (quote == '"' && character == '`') { index++; continue; }
                        if (character != quote) continue;
                        if (quote == '\'' && next == '\'') { index++; continue; }
                        quote = null;
                        continue;
                    }
                    if (character == '`') { index++; continue; }
                    if (character == '<' && next == '#') { blockCommentDepth++; index++; continue; }
                    if (character == '#')
                    {
                        const string directive = "#requires";
                        var comment = line[index..];
                        if (comment.StartsWith(directive, StringComparison.OrdinalIgnoreCase) &&
                            comment.Length > directive.Length && char.IsWhiteSpace(comment[directive.Length]))
                            yield return comment[directive.Length..].Trim();
                        break;
                    }
                    if (character == '@' && next is '\'' or '"' && string.IsNullOrWhiteSpace(line[(index + 2)..]))
                    {
                        hereStringEnd = next + "@";
                        break;
                    }
                    if (character is '\'' or '"') quote = character;
                }
            }
        }

        private static readonly Regex PowerShellEditionPattern = new(
            "(?:^|\\s)-PSEdition\\s+['\"]?(?<edition>Desktop|Core)['\"]?(?=\\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex PowerShellVersionPattern = new(
            @"(?:^|\s)-Version\s+(?<version>\d+(?:\.\d+){0,3})(?=\s|$)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

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

        private async Task<RunResult> StartAsync(
            string fileName,
            string arguments,
            string? cwd,
            TimeSpan timeout,
            CancellationToken ct,
            IReadOnlyDictionary<string, string>? environment = null,
            Encoding? outputEncoding = null)
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
