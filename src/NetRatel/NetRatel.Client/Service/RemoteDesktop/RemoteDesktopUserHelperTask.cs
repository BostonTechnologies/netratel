using NetRatel.Client.Service.Logging;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace NetRatel.Client.Service.RemoteDesktop;

internal static class RemoteDesktopUserHelperConstants
{
    public const string PipeName = "netratel-remote-desktop-user-helper";
    public const string FullPipePath = @"\\.\pipe\netratel-remote-desktop-user-helper";
    public const string TaskName = "NetRatel.RemoteDesktop.UserHelper";
    public const string RunValueName = "NetRatel.RemoteDesktop.UserHelper";
}

[SupportedOSPlatform("windows")]
internal sealed class RemoteDesktopUserHelperTask
{
    private const int TaskRunUseSessionId = 0x4;
    private const uint ErrorFileNotFound = 0x80070002;
    private readonly string _launcherDirectory;
    private readonly string _commandLauncherPath;
    private readonly string _hiddenLauncherPath;

    public RemoteDesktopUserHelperTask()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NetRatel");
        }

        _launcherDirectory = Path.Combine(programData, "NetRatel", "remote-desktop");
        _commandLauncherPath = Path.Combine(_launcherDirectory, "netratel-remote-desktop-user-helper.cmd");
        _hiddenLauncherPath = Path.Combine(_launcherDirectory, "netratel-remote-desktop-user-helper.vbs");
    }

    public void EnsureRegistered()
    {
        try
        {
            EnsureLauncherAndRunKey();
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper launcher/HKLM bootstrap warning during EnsureRegistered: {ex}");
        }

        TryRegisterScheduledTask();
    }

    public void EnsureLauncherAndRunKey()
    {
        Directory.CreateDirectory(_launcherDirectory);
        EnsureHelperLogDirectory();
        WriteLauncher();
        RegisterRunKey();
    }

    public void TryRegisterScheduledTask()
    {
        try
        {
            RegisterTaskFromXml();
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper task XML registration warning; trying CLI fallback: {ex.Message}");
            try
            {
                RegisterTaskFromCli();
            }
            catch (Exception cliEx)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper task CLI registration warning; continuing without scheduled task: {cliEx.Message}");
            }
        }
    }

    private static void EnsureHelperLogDirectory()
    {
        try
        {
            var helperLogDirectory = GetHelperLogDirectory();
            Directory.CreateDirectory(helperLogDirectory);
            var directoryInfo = new DirectoryInfo(helperLogDirectory);
            var security = directoryInfo.GetAccessControl();
            security.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            directoryInfo.SetAccessControl(security);
            LogManager.WriteLog($"[RemoteDesktop] Helper log directory ready path={helperLogDirectory} acl=BUILTIN\\Users Modify");
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper log directory ACL setup failed: {ex}");
        }
    }

    public static string GetHelperLogDirectory() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "NetRatel",
            "Client",
            "logs",
            "remote-desktop-helper");

    public static string GetLauncherPath()
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        if (string.IsNullOrWhiteSpace(programData))
        {
            programData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NetRatel");
        }

        return Path.Combine(programData, "NetRatel", "remote-desktop", "netratel-remote-desktop-user-helper.vbs");
    }

    private void RegisterRunKey()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key is null)
            {
                LogManager.WriteLog("[RemoteDesktop] HKLM Run bootstrap registration skipped because Run key could not be opened.");
                return;
            }

            var value = $"wscript.exe //B {Quote(_hiddenLauncherPath)}";
            key.SetValue(RemoteDesktopUserHelperConstants.RunValueName, value, RegistryValueKind.String);
            LogManager.WriteLog($"[RemoteDesktop] HKLM Run bootstrap registered name={RemoteDesktopUserHelperConstants.RunValueName} value={value}");
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] HKLM Run bootstrap registration failed: {ex}");
        }
    }

    public uint GetActiveConsoleSessionId() => WTSGetActiveConsoleSessionId();

    public void StartForActiveConsoleSession()
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        LogManager.WriteLog($"[RemoteDesktop] Active console session id={sessionId}");
        if (sessionId == uint.MaxValue || sessionId == 0)
        {
            LogManager.WriteLog("[RemoteDesktop] Skipping helper task launch because no interactive console session is active.");
            return;
        }

        StartForSession(sessionId);
    }

    public void StartForSession(uint sessionId)
    {
        try
        {
            if (sessionId == uint.MaxValue || sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId), sessionId, "A concrete interactive Windows session is required.");
            }

            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
            dynamic service = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("Unable to create Task Scheduler COM service.");
            service.Connect();
            dynamic rootFolder = service.GetFolder("\\");
            dynamic task;
            try
            {
                task = rootFolder.GetTask(RemoteDesktopUserHelperConstants.TaskName);
            }
            catch (COMException ex) when (unchecked((uint)ex.HResult) == ErrorFileNotFound)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper task missing; re-registering task={RemoteDesktopUserHelperConstants.TaskName}");
                TryRegisterScheduledTask();
                task = rootFolder.GetTask(RemoteDesktopUserHelperConstants.TaskName);
            }

            try
            {
                _ = task.RunEx(null, TaskRunUseSessionId, unchecked((int)sessionId), null);
                LogManager.WriteLog($"[RemoteDesktop] Requested targeted user helper task RunEx task={RemoteDesktopUserHelperConstants.TaskName} session={sessionId}");
                return;
            }
            catch (COMException ex)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper task RunEx COM failure session={sessionId} hresult=0x{ex.HResult:X8}: {ex.Message}");
            }

            TryLaunchHelperDirectly(sessionId);
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper task RunEx warning; continuing without scheduled-task launch: {ex.Message}");
        }
    }

    private void TryLaunchHelperDirectly(uint targetSessionId)
    {
        try
        {
            var currentSessionId = Process.GetCurrentProcess().SessionId;
            if (currentSessionId != unchecked((int)targetSessionId))
            {
                LogManager.WriteLog($"[RemoteDesktop] Direct helper launch skipped currentSession={currentSessionId} targetSession={targetSessionId}");
                return;
            }

            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName
                ?? throw new InvalidOperationException("Unable to resolve NetRatel.Client executable path.");
            var startInfo = new ProcessStartInfo
            {
                FileName = exePath,
                Arguments = "--remote-desktop-user-helper",
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var process = Process.Start(startInfo);
            LogManager.WriteLog($"[RemoteDesktop] Directly launched user helper pid={process?.Id} session={currentSessionId}");
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Direct helper launch failed: {ex.Message}");
        }
    }

    private void WriteLauncher()
    {
        var exePath = Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? throw new InvalidOperationException("Unable to resolve NetRatel.Client executable path.");
        var commandContent = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("setlocal")
            .Append(Quote(exePath))
            .AppendLine(" --remote-desktop-user-helper")
            .ToString();
        File.WriteAllText(_commandLauncherPath, commandContent, Encoding.ASCII);

        var escapedExePath = exePath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var hiddenContent = new StringBuilder()
            .AppendLine("Set shell = CreateObject(\"WScript.Shell\")")
            .Append("shell.Run \"\"\"")
            .Append(escapedExePath)
            .AppendLine("\"\" --remote-desktop-user-helper\", 0, False")
            .ToString();
        File.WriteAllText(_hiddenLauncherPath, hiddenContent, Encoding.ASCII);
        LogManager.WriteLog($"[RemoteDesktop] User helper launchers updated hiddenPath={_hiddenLauncherPath} commandPath={_commandLauncherPath} exe={exePath}");
    }

    private void RegisterTaskFromXml()
    {
        var xmlPath = Path.Combine(_launcherDirectory, "netratel-remote-desktop-user-helper.xml");
        var xml = BuildTaskXml("wscript.exe", $"//B {Quote(_hiddenLauncherPath)}");
        File.WriteAllText(xmlPath, xml, Encoding.UTF8);

        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Create /TN {Quote(RemoteDesktopUserHelperConstants.TaskName)} /XML {Quote(xmlPath)} /F",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var result = RunProcess(startInfo, "register remote desktop helper task from XML");
        LogManager.WriteLog($"[RemoteDesktop] User helper scheduled task registered from XML task={RemoteDesktopUserHelperConstants.TaskName} output={TrimForLog(result.Output)}");
    }

    private void RegisterTaskFromCli()
    {
        var action = $"wscript.exe //B {Quote(_hiddenLauncherPath)}";
        var startInfo = new ProcessStartInfo
        {
            FileName = "schtasks.exe",
            Arguments = $"/Create /TN {Quote(RemoteDesktopUserHelperConstants.TaskName)} /SC ONLOGON /TR {Quote(action)} /F",
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        var result = RunProcess(startInfo, "register remote desktop helper task from CLI");
        LogManager.WriteLog($"[RemoteDesktop] User helper scheduled task registered from CLI task={RemoteDesktopUserHelperConstants.TaskName} output={TrimForLog(result.Output)}");
    }

    private static ProcessResult RunProcess(ProcessStartInfo startInfo, string operation)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {startInfo.FileName} to {operation}.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(15000))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            throw new TimeoutException($"Timed out while trying to {operation}.");
        }

        if (process.ExitCode != 0)
        {
            throw new Win32Exception(process.ExitCode, $"{operation} failed: {TrimForLog(error)} {TrimForLog(output)}");
        }

        return new ProcessResult(output, error);
    }

    private static string BuildTaskXml(string command, string arguments)
    {
        var escapedCommand = SecurityElement.Escape(command) ?? command;
        var escapedArguments = SecurityElement.Escape(arguments) ?? arguments;
        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo>
    <Description>Starts the NetRatel remote desktop user-session helper for interactive capture and input.</Description>
    <Author>SpaceTime Orchestrator</Author>
  </RegistrationInfo>
  <Triggers>
    <LogonTrigger>
      <Enabled>true</Enabled>
    </LogonTrigger>
  </Triggers>
  <Principals>
    <Principal id="Users">
      <GroupId>S-1-5-32-545</GroupId>
      <LogonType>Group</LogonType>
      <RunLevel>LeastPrivilege</RunLevel>
    </Principal>
  </Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings>
      <StopOnIdleEnd>false</StopOnIdleEnd>
      <RestartOnIdle>false</RestartOnIdle>
    </IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>true</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context="Users">
    <Exec>
      <Command>{{escapedCommand}}</Command>
      <Arguments>{{escapedArguments}}</Arguments>
    </Exec>
  </Actions>
</Task>
""";
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string TrimForLog(string value)
    {
        value = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal).Trim();
        return value.Length <= 300 ? value : value[..300];
    }

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    private sealed record ProcessResult(string Output, string Error);
}
