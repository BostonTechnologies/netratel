using System;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Offboarding;

/// <summary>Small native wrappers provide an independent lifetime and the existing maintenance lock.</summary>
internal static class OffboardingPlatformScripts
{
    internal static void Write(OffboardingRequest request)
    {
        var directory = OffboardingState.DirectoryFor(request.OperationId);
        WriteScript(Path.Combine(directory, OperatingSystem.IsWindows() ? "run.ps1" : "run.sh"),
            OperatingSystem.IsWindows() ? WindowsWrapper(request) : LinuxWrapper(request));
        WriteScript(Path.Combine(directory, OperatingSystem.IsWindows() ? "remove.ps1" : "remove.sh"),
            OperatingSystem.IsWindows() ? WindowsRemoval(request) : LinuxRemoval(request));
        if (OperatingSystem.IsWindows()) WriteScript(Path.Combine(directory, "task.xml"), WindowsTaskXml(request));
    }

    private static void WriteScript(string path, string content)
    {
        if (File.Exists(path))
        {
            OffboardingState.AssertProtectedPath(path);
            if (File.ReadAllText(path) == content) return;
            throw new IOException("offboarding_helper_script_changed");
        }
        File.WriteAllText(path, content);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    internal static async Task RegisterAsync(OffboardingRequest request, CancellationToken cancellationToken)
    {
        var directory = OffboardingState.DirectoryFor(request.OperationId);
        if (OperatingSystem.IsWindows())
        {
            await OffboardingProcess.RunAsync(TaskScheduler, ["/Create", "/TN", OffboardingState.TaskName(request.OperationId),
                "/XML", Path.Combine(directory, "task.xml"), "/F"], cancellationToken).ConfigureAwait(false);
            return;
        }
        var unit = Path.Combine("/etc/systemd/system", OffboardingState.UnitName(request.OperationId));
        var content = LinuxUnit(request);
        if (File.Exists(unit))
        {
            OffboardingState.AssertProtectedPath(unit);
            if (File.ReadAllText(unit) != content) throw new IOException("offboarding_helper_unit_changed");
        }
        else
        {
            OffboardingState.AssertProtectedPath("/etc/systemd/system");
            File.WriteAllText(unit, content);
            File.SetUnixFileMode(unit, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }
        await OffboardingProcess.RunAsync("/usr/bin/systemctl", ["daemon-reload"], cancellationToken).ConfigureAwait(false);
        await OffboardingProcess.RunAsync("/usr/bin/systemctl", ["enable", OffboardingState.UnitName(request.OperationId)], cancellationToken).ConfigureAwait(false);
    }

    internal static Task<string> StartAsync(OffboardingRequest request, CancellationToken cancellationToken) => OperatingSystem.IsWindows()
        ? OffboardingProcess.RunAsync(TaskScheduler, ["/Run", "/TN", OffboardingState.TaskName(request.OperationId)], cancellationToken)
        : OffboardingProcess.RunAsync("/usr/bin/systemctl", ["start", "--no-block", OffboardingState.UnitName(request.OperationId)], cancellationToken);

    internal static Task<string> RemoveServiceAsync(OffboardingRequest request, CancellationToken cancellationToken) => OperatingSystem.IsWindows()
        ? OffboardingProcess.RunAsync(OffboardingProcess.PowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
            Path.Combine(OffboardingState.DirectoryFor(request.OperationId), "remove.ps1")], cancellationToken, TimeSpan.FromMinutes(2))
        : OffboardingProcess.RunAsync("/bin/bash", [Path.Combine(OffboardingState.DirectoryFor(request.OperationId), "remove.sh")],
            cancellationToken, TimeSpan.FromMinutes(2));

    private static string TaskScheduler => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "schtasks.exe");
    private static string Ps(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";
    private static string Sh(string text) => "'" + text.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
    private static string Xml(string text) => SecurityElement.Escape(text) ?? string.Empty;

    internal static string WindowsTaskXml(OffboardingRequest request) => $$"""
        <?xml version="1.0" encoding="utf-8"?>
        <Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
          <Triggers><BootTrigger><Enabled>true</Enabled></BootTrigger></Triggers>
          <Principals><Principal id="System"><UserId>S-1-5-18</UserId><LogonType>ServiceAccount</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
          <Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowStartOnDemand>true</AllowStartOnDemand><Enabled>true</Enabled><ExecutionTimeLimit>PT25H</ExecutionTimeLimit><RestartOnFailure><Interval>PT1M</Interval><Count>10</Count></RestartOnFailure></Settings>
          <Actions Context="System"><Exec><Command>{{Xml(OffboardingProcess.PowerShell)}}</Command><Arguments>{{Xml("-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + Path.Combine(OffboardingState.DirectoryFor(request.OperationId), "run.ps1") + "\"")}}</Arguments></Exec></Actions>
        </Task>
        """;

    private static string LinuxUnit(OffboardingRequest request) => $$"""
        [Unit]
        Description=NetRatel client offboarding {{request.OperationId:D}}
        After=network-online.target
        Wants=network-online.target
        StartLimitIntervalSec=0
        [Service]
        Type=simple
        ExecStart=/bin/bash {{OffboardingState.DirectoryFor(request.OperationId)}}/run.sh
        Restart=on-failure
        RestartSec=30s
        [Install]
        WantedBy=multi-user.target
        """;

    private static string WindowsWrapper(OffboardingRequest request) => $$"""
        $ErrorActionPreference = 'Stop'
        $operation = '{{request.OperationId:D}}'
        $directory = {{Ps(OffboardingState.DirectoryFor(request.OperationId))}}
        $lock = $null
        try {
            $lock = [IO.File]::Open({{Ps(Path.Combine(request.UpdateDirectory, "update.lock"))}}, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
            & (Join-Path $directory 'NetRatel.Client.exe') --offboarding-helper $operation
            $result = $LASTEXITCODE
        } finally { if ($null -ne $lock) { $lock.Dispose() } }
        if ($result -eq 0 -or $result -eq 2) {
            Unregister-ScheduledTask -TaskName '{{OffboardingState.TaskName(request.OperationId)}}' -Confirm:$false -ErrorAction Stop
            $active = {{Ps(Path.Combine(OffboardingState.Root, "active.json"))}}
            if ((Test-Path -LiteralPath $active) -and ((Get-Content -LiteralPath $active -Raw | ConvertFrom-Json) -eq $operation)) { Remove-Item -LiteralPath $active -Force }
            if ($result -eq 0) { Remove-Item -LiteralPath $directory -Recurse -Force }
            exit 0
        }
        exit $result
        """;

    private static string LinuxWrapper(OffboardingRequest request) => $$"""
        #!/usr/bin/env bash
        set -euo pipefail
        exec 9>{{Sh(Path.Combine(request.UpdateDirectory, "update.lock"))}}
        /usr/bin/flock --nonblock 9 || exit 75
        result=0
        {{Sh(Path.Combine(OffboardingState.DirectoryFor(request.OperationId), "NetRatel.Client"))}} --offboarding-helper '{{request.OperationId:D}}' || result=$?
        if [ "$result" -eq 0 ] || [ "$result" -eq 2 ]; then
          /usr/bin/systemctl disable '{{OffboardingState.UnitName(request.OperationId)}}'
          rm -f -- '/etc/systemd/system/{{OffboardingState.UnitName(request.OperationId)}}'
          /usr/bin/systemctl daemon-reload
          active={{Sh(Path.Combine(OffboardingState.Root, "active.json"))}}
          if [ -f "$active" ] && [ "$(cat "$active")" = '"{{request.OperationId:D}}"' ]; then rm -- "$active"; fi
          if [ "$result" -eq 0 ]; then rm -rf -- {{Sh(OffboardingState.DirectoryFor(request.OperationId))}}; fi
          exit 0
        fi
        exit "$result"
        """;

    private static string WindowsRemoval(OffboardingRequest request) => $$"""
        $ErrorActionPreference = 'Stop'
        $serviceName = 'NetRatel.Client'
        $sc = Join-Path $env:SystemRoot 'System32\sc.exe'
        $helperName = 'NetRatel.RemoteDesktop.UserHelper'
        $launcher = {{Ps(Path.Combine(Path.GetDirectoryName(request.CredentialPath)!, "remote-desktop", "netratel-remote-desktop-user-helper.vbs"))}}
        $helper = @(Get-ScheduledTask -TaskPath '\' -ErrorAction Stop | Where-Object { $_.TaskName -eq $helperName })
        if ($helper.Count -gt 0) {
            $actions = @($helper[0].Actions)
            if ($actions.Count -ne 1 -or $actions[0].Execute -ne 'wscript.exe' -or $actions[0].Arguments -cne ('//B "' + $launcher + '"')) { throw 'offboarding_user_helper_task_changed' }
        }
        $runKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', $true)
        try {
            $runValue = if ($null -ne $runKey) { $runKey.GetValue($helperName) } else { $null }
            if ($null -ne $runValue -and $runValue -cne ('wscript.exe //B "' + $launcher + '"')) { throw 'offboarding_user_helper_run_changed' }
        } finally { if ($null -ne $runKey) { $runKey.Dispose() } }
        $service = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" -OperationTimeoutSec 10
        if ($null -ne $service) {
            if ($service.PathName -cne {{Ps(request.ServiceImage)}} -or $service.StartName -notin @('LocalSystem', 'NT AUTHORITY\SYSTEM')) { throw 'offboarding_service_changed' }
            & $sc config $serviceName start= disabled | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'offboarding_disable_failed' }
            Stop-Service -Name $serviceName -ErrorAction Stop
            $stopped = Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" -OperationTimeoutSec 10
            if ($stopped.State -ne 'Stopped' -or $stopped.ProcessId -ne 0) { throw 'offboarding_stop_failed' }
            & $sc delete $serviceName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'offboarding_delete_failed' }
        }
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(45)
        while ($null -ne (Get-CimInstance Win32_Service -Filter "Name='NetRatel.Client'" -OperationTimeoutSec 10)) {
            if ([DateTimeOffset]::UtcNow -ge $deadline) { throw 'offboarding_service_still_registered' }
            Start-Sleep -Milliseconds 250
        }
        if ($helper.Count -gt 0) { Unregister-ScheduledTask -TaskName $helperName -TaskPath '\' -Confirm:$false -ErrorAction Stop }
        $runKey = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SOFTWARE\Microsoft\Windows\CurrentVersion\Run', $true)
        try { if ($null -ne $runKey) { $runKey.DeleteValue($helperName, $false) } }
        finally { if ($null -ne $runKey) { $runKey.Dispose() } }
        """;

    private static string LinuxRemoval(OffboardingRequest request) => $$"""
        #!/usr/bin/env bash
        set -euo pipefail
        unit='/etc/systemd/system/netratel-client.service'
        if [ -f "$unit" ]; then
          [ "$(/usr/bin/systemctl show netratel-client.service --property=FragmentPath --value)" = "$unit" ]
          [ "$(sha256sum "$unit" | cut -d ' ' -f 1)" = '{{request.ServiceUnitSha256?.ToLowerInvariant()}}' ]
          /usr/bin/systemctl disable --now netratel-client.service
          if /usr/bin/systemctl is-active --quiet netratel-client.service; then exit 1; fi
          rm -- "$unit"
          dropin='/etc/systemd/system/netratel-client.service.d/50-netratel-recovery.conf'
          if [ -f "$dropin" ]; then rm -- "$dropin"; rmdir '/etc/systemd/system/netratel-client.service.d'; fi
        fi
        updater='/etc/systemd/system/netratel-update.service'
        if [ -f "$updater" ]; then
          [ "$(sha256sum "$updater" | cut -d ' ' -f 1)" = '{{request.UpdaterUnitSha256?.ToLowerInvariant()}}' ]
          /usr/bin/systemctl disable --now netratel-update.service
          rm -- "$updater"
        fi
        /usr/bin/systemctl daemon-reload
        [ "$(/usr/bin/systemctl show netratel-client.service --property=LoadState --value)" = 'not-found' ]
        """;
}
