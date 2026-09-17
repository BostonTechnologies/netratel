using NetRatel.Client.Service.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetRatel.Client.Service.RemoteSupport;

[SupportedOSPlatform("windows")]
internal sealed class RemoteSupportConsoleActiveSessionLauncher
{
    private const int TaskRunUseSessionId = 0x4;
    private readonly string _exePath;

    public RemoteSupportConsoleActiveSessionLauncher(string exePath)
    {
        _exePath = exePath;
    }

    public RemoteSupportConsoleLaunchSummary Launch(string arguments)
    {
        var activeSessionId = WTSGetActiveConsoleSessionId();
        var commandLine = $"{Quote(_exePath)} {arguments}";
        var summary = new RemoteSupportConsoleLaunchSummary(activeSessionId, commandLine);

        foreach (var desktop in new[] { @"winsta0\winlogon", @"winsta0\default" })
        {
            var result = TryLaunchWithTokenSession(commandLine, desktop, activeSessionId);
            summary.Results.Add(result);
            if (result.CreateProcessAsUserSucceeded && result.LaunchedPid.HasValue)
            {
                LogLaunchSummary(summary);
                return summary;
            }
        }

        summary.Results.Add(TryLaunchWithTaskScheduler(commandLine, activeSessionId));
        LogLaunchSummary(summary);
        return summary;
    }

    private RemoteSupportConsoleLaunchBackendResult TryLaunchWithTokenSession(
        string commandLine,
        string desktop,
        uint activeSessionId)
    {
        var result = new RemoteSupportConsoleLaunchBackendResult
        {
            LauncherBackend = "local_system_token_create_process_as_user",
            LaunchedDesktop = desktop
        };

        if (activeSessionId == uint.MaxValue)
        {
            result.LaunchException = "No active Windows console session was detected.";
            return result;
        }

        IntPtr currentToken = IntPtr.Zero;
        IntPtr primaryToken = IntPtr.Zero;
        IntPtr environment = IntPtr.Zero;
        try
        {
            var currentProcess = Process.GetCurrentProcess();
            if (!OpenProcessToken(currentProcess.Handle, TokenAccess, out currentToken))
            {
                return result.WithLastWin32("OpenProcessToken failed.");
            }

            if (!DuplicateTokenEx(
                    currentToken,
                    TokenAccess,
                    IntPtr.Zero,
                    SecurityImpersonationLevel.SecurityIdentification,
                    TokenType.TokenPrimary,
                    out primaryToken))
            {
                return result.WithLastWin32("DuplicateTokenEx failed.");
            }

            result.DuplicatedTokenSucceeded = true;
            var sessionId = unchecked((int)activeSessionId);
            if (!SetTokenInformation(primaryToken, TokenInformationClass.TokenSessionId, ref sessionId, sizeof(int)))
            {
                return result.WithLastWin32("SetTokenInformation(TokenSessionId) failed.");
            }

            result.SetTokenSessionIdSucceeded = true;
            result.TokenSessionId = sessionId;
            if (!CreateEnvironmentBlock(out environment, primaryToken, false))
            {
                return result.WithLastWin32("CreateEnvironmentBlock failed.");
            }

            result.CreateEnvironmentBlockSucceeded = true;
            var startupInfo = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                lpDesktop = desktop
            };
            var processInfo = new PROCESS_INFORMATION();
            var workingDirectory = AppContext.BaseDirectory;
            if (!CreateProcessAsUser(
                    primaryToken,
                    null,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    CreationFlags.CreateUnicodeEnvironment | CreationFlags.CreateNoWindow,
                    environment,
                    workingDirectory,
                    ref startupInfo,
                    out processInfo))
            {
                return result.WithLastWin32("CreateProcessAsUser failed.");
            }

            result.CreateProcessAsUserSucceeded = true;
            result.LaunchedPid = processInfo.dwProcessId;
            result.LaunchedProcessSessionId = TryGetProcessSessionId(processInfo.dwProcessId);
            CloseHandle(processInfo.hThread);
            CloseHandle(processInfo.hProcess);
            return result;
        }
        catch (Exception ex)
        {
            result.LaunchException = ex.Message;
            result.LaunchHresult = ex.HResult;
            return result;
        }
        finally
        {
            if (environment != IntPtr.Zero)
            {
                DestroyEnvironmentBlock(environment);
            }

            if (primaryToken != IntPtr.Zero)
            {
                CloseHandle(primaryToken);
            }

            if (currentToken != IntPtr.Zero)
            {
                CloseHandle(currentToken);
            }
        }
    }

    private RemoteSupportConsoleLaunchBackendResult TryLaunchWithTaskScheduler(string commandLine, uint activeSessionId)
    {
        var result = new RemoteSupportConsoleLaunchBackendResult
        {
            LauncherBackend = "task_scheduler_runex_session",
            LaunchedDesktop = "task_scheduler_default"
        };

        try
        {
            if (activeSessionId == uint.MaxValue)
            {
                result.LaunchException = "No active Windows console session was detected.";
                return result;
            }

            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
            dynamic service = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("Unable to create Task Scheduler COM service.");
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");
            dynamic taskDefinition = service.NewTask(0);
            taskDefinition.RegistrationInfo.Description = "Starts the NetRatel remote support console helper in the active console session.";
            taskDefinition.Settings.Enabled = true;
            taskDefinition.Settings.Hidden = true;
            taskDefinition.Settings.AllowDemandStart = true;
            taskDefinition.Settings.ExecutionTimeLimit = "PT5M";
            dynamic principal = taskDefinition.Principal;
            principal.UserId = "SYSTEM";
            principal.LogonType = 5; // TASK_LOGON_SERVICE_ACCOUNT
            principal.RunLevel = 1; // TASK_RUNLEVEL_HIGHEST
            dynamic action = taskDefinition.Actions.Create(0); // TASK_ACTION_EXEC
            action.Path = _exePath;
            action.Arguments = commandLine.StartsWith(Quote(_exePath), StringComparison.Ordinal)
                ? commandLine[(Quote(_exePath).Length + 1)..]
                : "--remote-support-console-helper";
            const string taskName = "NetRatel.RemoteSupport.ConsoleHelper";
            dynamic task = rootFolder.RegisterTaskDefinition(taskName, taskDefinition, 6, null, null, 5);
            _ = task.RunEx(null, TaskRunUseSessionId, unchecked((int)activeSessionId), null);
            result.CreateProcessAsUserSucceeded = true;
            result.LaunchException = "Task Scheduler RunEx requested; pid/session will be confirmed by helper hello.";
            return result;
        }
        catch (COMException ex)
        {
            result.LaunchException = ex.Message;
            result.LaunchHresult = ex.HResult;
            result.LaunchWin32Error = ex.ErrorCode;
            return result;
        }
        catch (Exception ex)
        {
            result.LaunchException = ex.Message;
            result.LaunchHresult = ex.HResult;
            return result;
        }
    }

    private static int? TryGetProcessSessionId(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.SessionId;
        }
        catch
        {
            return null;
        }
    }

    private static void LogLaunchSummary(RemoteSupportConsoleLaunchSummary summary)
    {
        foreach (var result in summary.Results)
        {
            LogManager.WriteLog(
                $"[RemoteSupportConsoleProvider] launch backend={result.LauncherBackend} desktop={result.LaunchedDesktop} duplicated={result.DuplicatedTokenSucceeded} setSession={result.SetTokenSessionIdSucceeded} env={result.CreateEnvironmentBlockSucceeded} create={result.CreateProcessAsUserSucceeded} pid={result.LaunchedPid?.ToString() ?? "<none>"} launchedSession={result.LaunchedProcessSessionId?.ToString() ?? "<unknown>"} win32={result.LaunchWin32Error?.ToString() ?? "<none>"} hresult={result.LaunchHresult?.ToString("X8") ?? "<none>"} error={result.LaunchException ?? "<none>"}");
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private const uint TokenAccess =
        0x000F0000 |
        0x0001 |
        0x0002 |
        0x0004 |
        0x0008 |
        0x0010 |
        0x0020 |
        0x0040 |
        0x0080 |
        0x0100;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr newToken);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool SetTokenInformation(
        IntPtr tokenHandle,
        TokenInformationClass tokenInformationClass,
        ref int tokenInformation,
        int tokenInformationLength);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr environment, IntPtr token, bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr environment);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr token,
        string? applicationName,
        string commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        bool inheritHandles,
        CreationFlags creationFlags,
        IntPtr environment,
        string? currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    private enum SecurityImpersonationLevel
    {
        SecurityAnonymous,
        SecurityIdentification,
        SecurityImpersonation,
        SecurityDelegation
    }

    private enum TokenType
    {
        TokenPrimary = 1,
        TokenImpersonation
    }

    private enum TokenInformationClass
    {
        TokenUser = 1,
        TokenGroups,
        TokenPrivileges,
        TokenOwner,
        TokenPrimaryGroup,
        TokenDefaultDacl,
        TokenSource,
        TokenType,
        TokenImpersonationLevel,
        TokenStatistics,
        TokenRestrictedSids,
        TokenSessionId
    }

    [Flags]
    private enum CreationFlags : uint
    {
        CreateNoWindow = 0x08000000,
        CreateUnicodeEnvironment = 0x00000400
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }
}

internal sealed class RemoteSupportConsoleLaunchSummary
{
    public RemoteSupportConsoleLaunchSummary(uint activeConsoleSessionId, string commandLine)
    {
        ActiveConsoleSessionId = activeConsoleSessionId == uint.MaxValue ? null : activeConsoleSessionId;
        CommandLine = commandLine;
    }

    public uint? ActiveConsoleSessionId { get; }
    public string CommandLine { get; }
    public System.Collections.Generic.List<RemoteSupportConsoleLaunchBackendResult> Results { get; } = new();
    public RemoteSupportConsoleLaunchBackendResult? BestResult => Results.Find(x => x.LaunchedPid.HasValue) ?? Results.Find(x => x.CreateProcessAsUserSucceeded);
}

internal sealed class RemoteSupportConsoleLaunchBackendResult
{
    public string LauncherBackend { get; init; } = string.Empty;
    public bool DuplicatedTokenSucceeded { get; set; }
    public bool SetTokenSessionIdSucceeded { get; set; }
    public int? TokenSessionId { get; set; }
    public bool CreateEnvironmentBlockSucceeded { get; set; }
    public bool CreateProcessAsUserSucceeded { get; set; }
    public int? LaunchedPid { get; set; }
    public int? LaunchedProcessSessionId { get; set; }
    public string? LaunchedDesktop { get; set; }
    public int? LaunchWin32Error { get; set; }
    public int? LaunchHresult { get; set; }
    public string? LaunchException { get; set; }

    public RemoteSupportConsoleLaunchBackendResult WithLastWin32(string message)
    {
        LaunchWin32Error = Marshal.GetLastWin32Error();
        LaunchHresult = Marshal.GetHRForLastWin32Error();
        LaunchException = message;
        return this;
    }
}
