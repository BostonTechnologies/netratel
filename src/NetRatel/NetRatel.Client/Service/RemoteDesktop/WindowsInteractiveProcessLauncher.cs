using NetRatel.Client.Service.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NetRatel.Client.Service.RemoteDesktop;

internal static class WindowsInteractiveProcessLauncher
{
    [SupportedOSPlatform("windows")]
    public static Process LaunchCurrentExecutable(string arguments)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        if (sessionId == uint.MaxValue)
        {
            throw new InvalidOperationException("No active Windows console session was detected.");
        }

        var primaryToken = GetPrimaryUserToken(sessionId);

        try
        {
            var environment = IntPtr.Zero;
            try
            {
                if (!CreateEnvironmentBlock(out environment, primaryToken, false))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEnvironmentBlock failed.");
                }

                var exePath = Environment.ProcessPath
                    ?? Process.GetCurrentProcess().MainModule?.FileName
                    ?? throw new InvalidOperationException("Unable to resolve NetRatel.Client executable path.");
                var workingDirectory = Path.GetDirectoryName(exePath) ?? AppContext.BaseDirectory;
                var commandLine = $"{Quote(exePath)} {arguments}";
                var startupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFO>(),
                    lpDesktop = "winsta0\\default"
                };
                var processInfo = new PROCESS_INFORMATION();

                if (!CreateProcessAsUser(
                        primaryToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CreationFlags.CreateUnicodeEnvironment,
                        environment,
                        workingDirectory,
                        ref startupInfo,
                        out processInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcessAsUser failed.");
                }

                CloseHandle(processInfo.hThread);
                var process = Process.GetProcessById(processInfo.dwProcessId);
                CloseHandle(processInfo.hProcess);
                LogManager.WriteLog($"[RemoteDesktop] Launched interactive helper pid={process.Id} session={sessionId}");
                return process;
            }
            finally
            {
                if (environment != IntPtr.Zero)
                {
                    DestroyEnvironmentBlock(environment);
                }
            }
        }
        finally
        {
            CloseHandle(primaryToken);
        }
    }

    private static IntPtr GetPrimaryUserToken(uint sessionId)
    {
        if (WTSQueryUserToken(sessionId, out var wtsToken))
        {
            try
            {
                LogManager.WriteLog($"[RemoteDesktop] Acquired active session token via WTSQueryUserToken session={sessionId}");
                return DuplicatePrimaryToken(wtsToken, "WTSQueryUserToken");
            }
            finally
            {
                CloseHandle(wtsToken);
            }
        }

        var wtsError = Marshal.GetLastWin32Error();
        LogManager.WriteLog($"[RemoteDesktop] WTSQueryUserToken failed session={sessionId} error={wtsError}; falling back to explorer.exe token.");
        return GetExplorerPrimaryToken(sessionId, wtsError);
    }

    private static IntPtr GetExplorerPrimaryToken(uint sessionId, int wtsError)
    {
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process)
            {
                try
                {
                    if ((uint)process.SessionId != sessionId)
                    {
                        continue;
                    }

                    var processHandle = OpenProcess(ProcessQueryLimitedInformation, false, process.Id);
                    if (processHandle == IntPtr.Zero)
                    {
                        LogManager.WriteLog($"[RemoteDesktop] OpenProcess failed for explorer pid={process.Id} error={Marshal.GetLastWin32Error()}");
                        continue;
                    }

                    try
                    {
                        if (!OpenProcessToken(processHandle, TokenAccess, out var processToken))
                        {
                            LogManager.WriteLog($"[RemoteDesktop] OpenProcessToken failed for explorer pid={process.Id} error={Marshal.GetLastWin32Error()}");
                            continue;
                        }

                        try
                        {
                            LogManager.WriteLog($"[RemoteDesktop] Acquired active session token from explorer.exe pid={process.Id} session={sessionId}");
                            return DuplicatePrimaryToken(processToken, $"explorer.exe pid={process.Id}");
                        }
                        finally
                        {
                            CloseHandle(processToken);
                        }
                    }
                    finally
                    {
                        CloseHandle(processHandle);
                    }
                }
                catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
                {
                    LogManager.WriteLog($"[RemoteDesktop] Explorer token fallback skipped process error: {ex.Message}");
                }
            }
        }

        throw new Win32Exception(wtsError, $"WTSQueryUserToken failed for session {sessionId}, and no usable explorer.exe token was found.");
    }

    private static IntPtr DuplicatePrimaryToken(IntPtr token, string source)
    {
        if (!DuplicateTokenEx(
                token,
                TokenAccess,
                IntPtr.Zero,
                SecurityImpersonationLevel.SecurityIdentification,
                TokenType.TokenPrimary,
                out var primaryToken))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"DuplicateTokenEx failed for {source}.");
        }

        return primaryToken;
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private const uint TokenAccess =
        0x000F0000 | // STANDARD_RIGHTS_REQUIRED
        0x0001 |     // TOKEN_ASSIGN_PRIMARY
        0x0002 |     // TOKEN_DUPLICATE
        0x0004 |     // TOKEN_IMPERSONATE
        0x0008 |     // TOKEN_QUERY
        0x0010 |     // TOKEN_QUERY_SOURCE
        0x0020 |     // TOKEN_ADJUST_PRIVILEGES
        0x0040 |     // TOKEN_ADJUST_GROUPS
        0x0080 |     // TOKEN_ADJUST_DEFAULT
        0x0100;      // TOKEN_ADJUST_SESSIONID

    private const uint ProcessQueryLimitedInformation = 0x1000;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(
        IntPtr existingToken,
        uint desiredAccess,
        IntPtr tokenAttributes,
        SecurityImpersonationLevel impersonationLevel,
        TokenType tokenType,
        out IntPtr newToken);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, int processId);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

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

    [Flags]
    private enum CreationFlags : uint
    {
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
