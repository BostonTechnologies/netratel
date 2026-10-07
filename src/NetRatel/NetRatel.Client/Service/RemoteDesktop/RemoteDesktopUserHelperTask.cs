using NetRatel.Client.Service.Logging;
using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32;

namespace NetRatel.Client.Service.RemoteDesktop;

internal static class RemoteDesktopUserHelperConstants
{
    public const string PipeName = "netratel-remote-desktop-user-helper";
    public const string FullPipePath = @"\\.\pipe\netratel-remote-desktop-user-helper";
    public const string TaskName = "NetRatel.RemoteDesktop.UserHelper";
    public const string RunValueName = "NetRatel.RemoteDesktop.UserHelper";
    public const string RegistrationCommand = "--register-remote-desktop-helper-task";
}

[SupportedOSPlatform("windows")]
internal sealed class RemoteDesktopUserHelperTask
{
    private const int TaskRunUseSessionId = 0x4;
    private const uint ErrorFileNotFound = 0x80070002;
    private static readonly ConcurrentDictionary<string, HelperTaskRegistrationState> Registrations = new(StringComparer.OrdinalIgnoreCase);
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

    public async Task<bool> EnsureRegisteredAsync(CancellationToken ct, bool force = false)
    {
        var exePath = ResolveExecutablePath();
        var xml = BuildCurrentTaskXml();
        var registration = Registrations.GetOrAdd(_launcherDirectory, _ => new HelperTaskRegistrationState());
        try
        {
            await registration.EnsureAsync(exePath + "\n" + xml, async () =>
            {
                EnsureLauncherAndRunKey(exePath);
                var result = await BoundedProcessRunner.RunAsync(
                    BuildRegistrationStartInfo(exePath),
                    TimeSpan.FromSeconds(15),
                    "register remote desktop helper task",
                    ct).ConfigureAwait(false);
                if (result.ExitCode != 0)
                {
                    throw new Win32Exception(result.ExitCode, $"Helper task registration failed: {TrimForLog(result.Error)} {TrimForLog(result.Output)}");
                }

                LogManager.WriteLog($"[RemoteDesktop] User helper scheduled task registered through Task Scheduler Unicode API task={RemoteDesktopUserHelperConstants.TaskName}");
            }, ct, force).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper feature unavailable: task/launcher registration is not confirmed: {ex.Message}");
            RemoteDesktopDiagnosticState.Update(state =>
            {
                state.LatestStage = "helper_task_registration_failed";
                state.LatestError = ex.Message;
            });
            return false;
        }
    }

    private void EnsureLauncherAndRunKey(string exePath)
    {
        Directory.CreateDirectory(_launcherDirectory);
        // Interactive users need read/execute access to launchers, never write.
        var launcherDirectory = new DirectoryInfo(_launcherDirectory);
        var launcherSecurity = launcherDirectory.GetAccessControl();
        launcherSecurity.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        launcherDirectory.SetAccessControl(launcherSecurity);
        EnsureHelperLogDirectory();
        WriteLauncher(exePath);
        RegisterRunKey();
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

    public async Task<bool> StartForActiveConsoleSessionAsync(CancellationToken ct)
    {
        var sessionId = WTSGetActiveConsoleSessionId();
        LogManager.WriteLog($"[RemoteDesktop] Active console session id={sessionId}");
        if (sessionId == uint.MaxValue || sessionId == 0)
        {
            LogManager.WriteLog("[RemoteDesktop] Skipping helper task launch because no interactive console session is active.");
            return false;
        }

        return await StartForSessionAsync(sessionId, ct).ConfigureAwait(false);
    }

    public async Task<bool> StartForSessionAsync(uint sessionId, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        object? runningObject = null;
        var taskStatus = "unavailable";
        try
        {
            if (sessionId == uint.MaxValue || sessionId == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sessionId), sessionId, "A concrete interactive Windows session is required.");
            }

            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
            serviceObject = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("Unable to create Task Scheduler COM service.");
            dynamic service = serviceObject;
            service.Connect();
            folderObject = service.GetFolder("\\");
            dynamic rootFolder = folderObject;
            try
            {
                taskObject = rootFolder.GetTask(RemoteDesktopUserHelperConstants.TaskName);
            }
            catch (COMException ex) when (unchecked((uint)ex.HResult) == ErrorFileNotFound)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper task missing; re-registering task={RemoteDesktopUserHelperConstants.TaskName}");
                if (!await EnsureRegisteredAsync(ct, force: true).ConfigureAwait(false)) return false;
                taskObject = rootFolder.GetTask(RemoteDesktopUserHelperConstants.TaskName);
            }
            dynamic task = taskObject;
            taskStatus = $"enabled={task.Enabled}, taskState={task.State}, lastTaskResult=0x{unchecked((uint)(int)task.LastTaskResult):X8}";

            try
            {
                ct.ThrowIfCancellationRequested();
                runningObject = task.RunEx(null, TaskRunUseSessionId, unchecked((int)sessionId), null);
                LogManager.WriteLog($"[RemoteDesktop] Requested targeted user helper task RunEx task={RemoteDesktopUserHelperConstants.TaskName} session={sessionId} flags=0x{TaskRunUseSessionId:X} {taskStatus} requestAccepted={runningObject is not null} helperReadiness=unconfirmed");
                return runningObject is not null;
            }
            catch (COMException ex)
            {
                LogManager.WriteLog($"[RemoteDesktop] Helper task RunEx COM failure session={sessionId} hresult=0x{ex.HResult:X8} {taskStatus}: {TrimForLog(ex.Message)}");
            }

            return TryLaunchHelperDirectly(sessionId);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Helper task RunEx warning; continuing without scheduled-task launch: session={sessionId} exception={ex.GetType().Name} hresult=0x{ex.HResult:X8} {taskStatus}: {TrimForLog(ex.Message)}");
            return false;
        }
        finally
        {
            ReleaseComObject(runningObject);
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    private bool TryLaunchHelperDirectly(uint targetSessionId)
    {
        try
        {
            var currentSessionId = Process.GetCurrentProcess().SessionId;
            if (currentSessionId != unchecked((int)targetSessionId))
            {
                LogManager.WriteLog($"[RemoteDesktop] Direct helper launch skipped currentSession={currentSessionId} targetSession={targetSessionId}");
                return false;
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
            return process is not null;
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteDesktop] Direct helper launch failed: {ex.Message}");
            return false;
        }
    }

    private void WriteLauncher(string exePath)
    {
        var commandContent = new StringBuilder()
            .AppendLine("@echo off")
            .AppendLine("setlocal")
            .Append(Quote(exePath))
            .AppendLine(" --remote-desktop-user-helper")
            .ToString();
        WriteLauncherIfChanged(_commandLauncherPath, commandContent, Encoding.ASCII);

        var escapedExePath = exePath.Replace("\"", "\"\"", StringComparison.Ordinal);
        var hiddenContent = new StringBuilder()
            .AppendLine("Set shell = CreateObject(\"WScript.Shell\")")
            .Append("shell.Run \"\"\"")
            .Append(escapedExePath)
            .AppendLine("\"\" --remote-desktop-user-helper\", 0, False")
            .ToString();
        WriteLauncherIfChanged(_hiddenLauncherPath, hiddenContent, Encoding.Unicode);
        LogManager.WriteLog($"[RemoteDesktop] User helper launchers updated hiddenPath={_hiddenLauncherPath} commandPath={_commandLauncherPath} exe={exePath}");
    }

    private static string ResolveExecutablePath() => Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? throw new InvalidOperationException("Unable to resolve NetRatel.Client executable path.");

    private static void WriteLauncherIfChanged(string path, string content, Encoding encoding)
    {
        if (File.Exists(path) && string.Equals(File.ReadAllText(path, encoding), content, StringComparison.Ordinal))
        {
            return;
        }

        // Readers see a complete old or new launcher even across process instances.
        var staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(staged, content, encoding);
            File.Move(staged, path, overwrite: true);
        }
        finally
        {
            File.Delete(staged);
        }
    }

    internal static ProcessStartInfo BuildRegistrationStartInfo(string executablePath) => new()
    {
        FileName = executablePath,
        ArgumentList = { RemoteDesktopUserHelperConstants.RegistrationCommand },
        UseShellExecute = false,
        RedirectStandardError = true,
        RedirectStandardOutput = true,
        CreateNoWindow = true
    };

    // Only the bounded child invokes this mutating mode. Native tests use the
    // same complete Unicode handoff with TASK_VALIDATE_ONLY, which creates nothing.
    internal static string BuildCurrentTaskXml() => BuildTaskXml("wscript.exe", $"//B {Quote(GetLauncherPath())}");

    internal static void RegisterCurrentTaskInChild() => RegisterTaskXml(BuildCurrentTaskXml(), validateOnly: false);

    internal static void RegisterTaskXml(string xml, bool validateOnly)
    {
        const int TaskValidateOnly = 1;
        const int TaskCreateOrUpdate = 6;
        const int TaskLogonGroup = 4;
        object? serviceObject = null;
        object? folderObject = null;
        object? taskObject = null;
        try
        {
            var serviceType = Type.GetTypeFromProgID("Schedule.Service")
                ?? throw new InvalidOperationException("Task Scheduler COM service is unavailable.");
            serviceObject = Activator.CreateInstance(serviceType)
                ?? throw new InvalidOperationException("Unable to create Task Scheduler COM service.");
            dynamic service = serviceObject;
            service.Connect();
            folderObject = service.GetFolder("\\");
            dynamic folder = folderObject;
            taskObject = folder.RegisterTask(RemoteDesktopUserHelperConstants.TaskName, xml,
                validateOnly ? TaskValidateOnly : TaskCreateOrUpdate,
                "S-1-5-32-545", null, TaskLogonGroup, null);
        }
        finally
        {
            ReleaseComObject(taskObject);
            ReleaseComObject(folderObject);
            ReleaseComObject(serviceObject);
        }
    }

    private static void ReleaseComObject(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            Marshal.ReleaseComObject(value);
        }
    }

    internal static string BuildTaskXml(string command, string arguments)
    {
        var escapedCommand = SecurityElement.Escape(command) ?? command;
        var escapedArguments = SecurityElement.Escape(arguments) ?? arguments;
        return $$"""
<?xml version="1.0" encoding="UTF-16"?>
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

}

// Native task and launcher state is shared by reconnect/repair callers in this
// process. Failed or canceled work remains eligible for the next bounded attempt.
internal sealed class HelperTaskRegistrationState
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _registeredFingerprint;

    internal async Task EnsureAsync(string fingerprint, Func<Task> register, CancellationToken ct, bool force = false)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (!force && string.Equals(_registeredFingerprint, fingerprint, StringComparison.Ordinal))
            {
                return;
            }

            _registeredFingerprint = null;
            await register().ConfigureAwait(false);
            _registeredFingerprint = fingerprint;
        }
        finally
        {
            _gate.Release();
        }
    }
}
