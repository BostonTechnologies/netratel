using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NetRatel.Client.Service.Logging;
using NetRatel.Shared.Client;

namespace NetRatel.Client.Service;

internal enum ServicePolicyStatus { NotApplicable, AlreadyManaged, Applied, AdministratorOverride, PermissionOrVerificationFailure }
internal sealed record NativeRecoverySnapshot(bool Owned, bool Recognized, bool Desired);
internal interface INativeRecoveryPolicyAdapter
{
    Task<NativeRecoverySnapshot> ReadAsync(CancellationToken stopping);
    Task ApplyAsync(CancellationToken stopping);
}

/// <summary>Runs from the new candidate itself, including when the installed updater is older.</summary>
public static class NativeServiceRecoveryPolicy
{
    public static string Status { get; private set; } = nameof(ServicePolicyStatus.NotApplicable);
    public static bool IsOwnedService { get; private set; }

    public static async Task TryApplyAsync(string[] args, CancellationToken stopping)
    {
        if (args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && a != "--service") ||
            string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase) ||
            File.Exists("/.dockerenv")) return;
        INativeRecoveryPolicyAdapter? adapter = OperatingSystem.IsWindows() &&
            NetRatelWindowsServiceHost.ShouldRunAsService(args) ? new WindowsRecoveryPolicyAdapter() :
            OperatingSystem.IsLinux() && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("INVOCATION_ID"))
                ? new LinuxRecoveryPolicyAdapter() : null;
        if (adapter is null) return;
        var status = await ApplyOwnedAsync(adapter, stopping, () => IsOwnedService = true).ConfigureAwait(false);
        Status = status.ToString();
        LogManager.WriteLog($"[ServicePolicy] recoveryPolicy={status}; use the existing service repair installer for overrides or permission failures.");
    }

    internal static async Task<ServicePolicyStatus> ApplyOwnedAsync(INativeRecoveryPolicyAdapter adapter, CancellationToken stopping, Action? onOwned = null)
    {
        try
        {
            var before = await adapter.ReadAsync(stopping).ConfigureAwait(false);
            if (!before.Owned) return ServicePolicyStatus.NotApplicable;
            onOwned?.Invoke();
            if (before.Desired) return ServicePolicyStatus.AlreadyManaged;
            if (!before.Recognized) return ServicePolicyStatus.AdministratorOverride;
            await adapter.ApplyAsync(stopping).ConfigureAwait(false);
            var after = await adapter.ReadAsync(stopping).ConfigureAwait(false);
            return after.Owned && after.Desired ? ServicePolicyStatus.Applied : ServicePolicyStatus.PermissionOrVerificationFailure;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or InvalidOperationException)
        {
            LogManager.WriteLog($"[ServicePolicy] Policy verification failed ({e.GetType().Name}); in-process recovery remains active.");
            return ServicePolicyStatus.PermissionOrVerificationFailure;
        }
    }
}

[SupportedOSPlatform("linux")]
internal sealed class LinuxRecoveryPolicyAdapter : INativeRecoveryPolicyAdapter
{
    private const string Unit = "netratel-client.service";
    private readonly Func<string, string[], CancellationToken, Task<string>> _run;
    private string? _dropIn;

    internal LinuxRecoveryPolicyAdapter(Func<string, string[], CancellationToken, Task<string>>? run = null) => _run = run ?? RunAsync;

    public async Task<NativeRecoverySnapshot> ReadAsync(CancellationToken stopping)
    {
        var output = await _run("systemctl", ["show", Unit, "--property=MainPID,FragmentPath,DropInPaths,ExecStart,Restart,RestartUSec,StartLimitIntervalUSec,RestartPreventExitStatus"], stopping).ConfigureAwait(false);
        var properties = output.Split('\n').Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
            .ToDictionary(x => x[0], x => x[1], StringComparer.Ordinal);
        string Get(string name) => properties.GetValueOrDefault(name, "").Trim();
        if (Get("MainPID") != Environment.ProcessId.ToString() || !Get("FragmentPath").EndsWith('/' + Unit, StringComparison.Ordinal))
            return new(false, false, false);
        var command = Regex.Match(Get("ExecStart"), @"(?:^|\{\s*)path=([^;]+)\s*;").Groups[1].Value.Trim();
        if (!await IsOwnedCommandAsync(command, stopping).ConfigureAwait(false)) return new(false, false, false);
        _dropIn = Path.Combine(Path.GetDirectoryName(Get("FragmentPath"))!, Unit + ".d", "50-netratel-recovery.conf");
        var fragment = ReadUnitFile(Get("FragmentPath"));
        var dropIns = Regex.Matches(Get("DropInPaths"), @"(?:""([^""]+)""|(\S+))").Select(x => x.Groups[1].Success ? x.Groups[1].Value : x.Groups[2].Value);
        var overrideFound = false;
        foreach (var path in dropIns)
        {
            var content = ReadUnitFile(path);
            if (path == _dropIn)
            {
                if (content == ManagedServiceRecovery.LinuxDropIn) continue;
                overrideFound = true;
            }
            if (HasRecoveryDirective(content)) overrideFound = true;
        }
        var recognized = !overrideFound && RecognizesGeneratedRecovery(fragment);
        var desired = Get("Restart") == "always" && Get("RestartUSec") == "30s" && Get("StartLimitIntervalUSec") == "0" && Get("RestartPreventExitStatus") == "78";
        return new(true, recognized, desired);
    }

    private async Task<bool> IsOwnedCommandAsync(string command, CancellationToken stopping)
    {
        if (string.IsNullOrWhiteSpace(command) || string.IsNullOrEmpty(Environment.ProcessPath)) return false;
        if (Path.GetFileName(command) == "NetRatel.Client")
            return (await _run("readlink", ["-f", command], stopping).ConfigureAwait(false)).Trim() == Environment.ProcessPath;
        if (Path.GetFileName(command) != "netratel-client-start.sh" || !File.Exists(command)) return false;
        if (new FileInfo(command).Length > 4096) return false;
        var lines = File.ReadAllLines(command).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (lines.Length != 3 || lines[0] != "#!/usr/bin/env bash" || lines[1] != "set -euo pipefail" ||
            !lines[2].StartsWith("exec ", StringComparison.Ordinal) || !lines[2].EndsWith(" --service", StringComparison.Ordinal)) return false;
        var executable = lines[2][5..^10];
        if (executable.StartsWith('\'') && executable.EndsWith('\'')) executable = executable[1..^1].Replace("'\"'\"'", "'", StringComparison.Ordinal);
        return (await _run("readlink", ["-f", executable], stopping).ConfigureAwait(false)).Trim() == Environment.ProcessPath;
    }

    internal static bool HasRecoveryDirective(string content) =>
        Regex.IsMatch(content, @"(?m)^\s*(?:Restart\w*|StartLimit\w*|FailureAction|SuccessAction)\s*=");

    internal static bool RecognizesGeneratedRecovery(string content)
    {
        if (!Regex.IsMatch(content, @"(?m)^\s*Restart\s*=\s*always\s*$") ||
            Regex.IsMatch(content, @"(?m)^\s*(?:RestartMode|RestartForceExitStatus|StartLimitAction|FailureAction|SuccessAction)\s*=")) return false;
        foreach (Match match in Regex.Matches(content, @"(?m)^\s*(Restart|RestartSec|RestartPreventExitStatus|StartLimitInterval(?:Sec)?|StartLimitBurst)\s*=\s*([^\r\n]*)"))
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value.Trim();
            if (key == "Restart" && value != "always" || key == "RestartSec" && value != "30s" ||
                key == "RestartPreventExitStatus" && value != "78" || key.StartsWith("StartLimitInterval", StringComparison.Ordinal) && value != "0" ||
                key == "StartLimitBurst") return false;
        }
        return true;
    }

    private static string ReadUnitFile(string path)
    {
        if (new FileInfo(path).Length > 128 * 1024) throw new IOException("The service policy file is too large for safe retrofit.");
        return File.ReadAllText(path);
    }

    public async Task ApplyAsync(CancellationToken stopping)
    {
        if (_dropIn is null) throw new InvalidOperationException("Service ownership has not been verified.");
        for (FileSystemInfo? entry = new FileInfo(_dropIn); entry is not null; entry = entry is FileInfo file ? file.Directory : ((DirectoryInfo)entry).Parent)
        {
            if (!entry.Exists) continue;
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0 ||
                (File.GetUnixFileMode(entry.FullName) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0 &&
                !(entry is DirectoryInfo && (File.GetUnixFileMode(entry.FullName) & UnixFileMode.StickyBit) != 0))
                throw new UnauthorizedAccessException("The service policy path is not protected.");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_dropIn)!);
        if (File.Exists(_dropIn) && ReadUnitFile(_dropIn) != ManagedServiceRecovery.LinuxDropIn)
            throw new InvalidOperationException("The managed policy path contains an administrator override.");
        var temporary = _dropIn + $".{Environment.ProcessId}.tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, ManagedServiceRecovery.LinuxDropIn, stopping).ConfigureAwait(false);
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            File.Move(temporary, _dropIn, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        await _run("systemctl", ["daemon-reload"], stopping).ConfigureAwait(false);
        // reset-failed belongs only to an explicit repair/start transaction.
    }

    internal static async Task<string> RunAsync(string file, string[] args, CancellationToken stopping)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        bounded.CancelAfter(TimeSpan.FromSeconds(5));
        var start = new ProcessStartInfo(file) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Native policy command did not start.");
        var output = process.StandardOutput.ReadToEndAsync(bounded.Token);
        var error = process.StandardError.ReadToEndAsync(bounded.Token);
        try
        {
            await process.WaitForExitAsync(bounded.Token).ConfigureAwait(false);
            var result = await output.ConfigureAwait(false);
            await error.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Native policy command failed.");
            return result;
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try { await Task.WhenAll(output, error).ConfigureAwait(false); } catch (OperationCanceledException) { }
            if (stopping.IsCancellationRequested) throw;
            throw new IOException("Native policy command timed out.");
        }
    }
}

internal sealed class WindowsRecoveryPolicyAdapter : INativeRecoveryPolicyAdapter
{
    private const uint QueryConfig = 1, ChangeConfig = 2, QueryStatus = 4;
    private const uint FailureActions = 2, FailureFlag = 4;
    private static readonly ActionEntry[] DesiredActions = [new(1, 30000), new(1, 60000), new(1, 300000)];

    public Task<NativeRecoverySnapshot> ReadAsync(CancellationToken stopping)
    {
        stopping.ThrowIfCancellationRequested();
        using var service = Open(QueryConfig | QueryStatus);
        var status = QueryStatusValue(service.Handle);
        using var config = Query(service.Handle, 0);
        var registered = Marshal.PtrToStructure<ServiceConfig>(config.Pointer);
        var image = Marshal.PtrToStringUni(registered.BinaryPath) ?? "";
        var owned = status.ProcessId == Environment.ProcessId && IsOwnedImage(image, Environment.ProcessPath);
        if (!owned) return Task.FromResult(new NativeRecoverySnapshot(false, false, false));
        using var buffer = Query(service.Handle, FailureActions);
        var policy = Marshal.PtrToStructure<FailurePolicy>(buffer.Pointer);
        if (policy.Count > 100) throw new InvalidOperationException("Unsupported recovery action count.");
        var actions = Enumerable.Range(0, checked((int)policy.Count)).Select(i => Marshal.PtrToStructure<ActionEntry>(policy.Actions + i * Marshal.SizeOf<ActionEntry>())).ToArray();
        return Task.FromResult(EvaluatePolicy(policy.ResetSeconds, actions, Marshal.PtrToStringUni(policy.Command), Marshal.PtrToStringUni(policy.RebootMessage)));
    }

    internal static bool IsOwnedImage(string image, string? currentExecutable)
    {
        if (string.IsNullOrEmpty(currentExecutable)) return false;
        var match = Regex.Match(image, "^\\s*(?:\"(?<exe>[^\"]+)\"|(?<exe>\\S+))(?:\\s+--service)?\\s*$", RegexOptions.IgnoreCase);
        return match.Success && match.Groups["exe"].Value.Equals(currentExecutable, StringComparison.OrdinalIgnoreCase);
    }

    internal static NativeRecoverySnapshot EvaluatePolicy(uint resetSeconds, IReadOnlyList<ActionEntry> actions, string? command, string? rebootMessage)
    {
        var customCommand = !string.IsNullOrEmpty(command) || !string.IsNullOrEmpty(rebootMessage);
        var desired = !customCommand && resetSeconds == ManagedServiceRecovery.WindowsResetSeconds && actions.SequenceEqual(DesiredActions);
        var recognized = !customCommand && (actions.Count == 0 || desired);
        return new(true, recognized, desired);
    }

    public Task ApplyAsync(CancellationToken stopping)
    {
        stopping.ThrowIfCancellationRequested();
        using var service = Open(QueryConfig | QueryStatus | ChangeConfig);
        using var actions = new NativeBuffer(Marshal.SizeOf<ActionEntry>() * DesiredActions.Length);
        for (var i = 0; i < DesiredActions.Length; i++) Marshal.StructureToPtr(DesiredActions[i], actions.Pointer + i * Marshal.SizeOf<ActionEntry>(), false);
        var policy = new FailurePolicy { ResetSeconds = ManagedServiceRecovery.WindowsResetSeconds, Count = 3, Actions = actions.Pointer };
        using var nativePolicy = new NativeBuffer(Marshal.SizeOf<FailurePolicy>());
        Marshal.StructureToPtr(policy, nativePolicy.Pointer, false);
        if (!ChangeServiceConfig2(service.Handle, FailureActions, nativePolicy.Pointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
        // Real worker faults terminate the process. This flag additionally handles reported failures.
        using var flag = new NativeBuffer(4);
        Marshal.WriteInt32(flag.Pointer, 1);
        if (!ChangeServiceConfig2(service.Handle, FailureFlag, flag.Pointer)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return Task.CompletedTask;
    }

    private static NativeService Open(uint access)
    {
        var manager = OpenSCManager(null, null, 1);
        if (manager == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service = OpenService(manager, "NetRatel.Client", access);
            if (service == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new NativeService(service);
        }
        finally { CloseServiceHandle(manager); }
    }
    private static NativeBuffer Query(IntPtr service, uint kind)
    {
        uint needed;
        if (kind == 0) QueryServiceConfig(service, IntPtr.Zero, 0, out needed);
        else QueryServiceConfig2(service, kind, IntPtr.Zero, 0, out needed);
        if (needed == 0 || needed > 65536) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new NativeBuffer((int)needed);
        var success = kind == 0 ? QueryServiceConfig(service, buffer.Pointer, needed, out needed) : QueryServiceConfig2(service, kind, buffer.Pointer, needed, out needed);
        if (!success) { buffer.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return buffer;
    }
    private static ServiceStatus QueryStatusValue(IntPtr handle)
    {
        if (!QueryServiceStatusEx(handle, 0, out var status, (uint)Marshal.SizeOf<ServiceStatus>(), out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return status;
    }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceConfig { public uint Type, StartType, ErrorControl; public IntPtr BinaryPath, Group; public uint Tag; public IntPtr Dependencies, Account, DisplayName; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public uint Type, State, Accepted, Win32Exit, ServiceExit, Checkpoint, WaitHint, ProcessId, Flags; }
    [StructLayout(LayoutKind.Sequential)] private struct FailurePolicy { public uint ResetSeconds; public IntPtr RebootMessage, Command; public uint Count; public IntPtr Actions; }
    [StructLayout(LayoutKind.Sequential)] internal readonly record struct ActionEntry(uint Type, uint DelayMs);
    private sealed class NativeBuffer(int length) : IDisposable { public IntPtr Pointer { get; } = Marshal.AllocHGlobal(length); public void Dispose() => Marshal.FreeHGlobal(Pointer); }
    private sealed class NativeService(IntPtr handle) : IDisposable { public IntPtr Handle { get; } = handle; public void Dispose() => CloseServiceHandle(Handle); }
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machine, string? database, uint access);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string name, uint access);
    [DllImport("advapi32", SetLastError = true)] private static extern bool CloseServiceHandle(IntPtr handle);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig(IntPtr handle, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryServiceConfig2(IntPtr handle, uint kind, IntPtr buffer, uint size, out uint needed);
    [DllImport("advapi32", SetLastError = true)] private static extern bool QueryServiceStatusEx(IntPtr handle, uint kind, out ServiceStatus status, uint size, out uint needed);
    [DllImport("advapi32", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ChangeServiceConfig2(IntPtr handle, uint kind, IntPtr buffer);
}
