using NetRatel.Client.Service.Logging;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace NetRatel.Client.Service.RemoteSupport;

[SupportedOSPlatform("windows")]
internal static class WindowsRemoteSupportFirewall
{
    public const string RuleNameUdp = "NetRatel Client Remote Support (WebRTC UDP)";
    public const string RuleNameTcp = "NetRatel Client Remote Support (WebRTC TCP)";
    private const string RuleGroup = "NetRatel Client Remote Support";
    private const string StateFileName = "remote-support-firewall-state.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "NetRatel",
        "Client",
        StateFileName);

    public static void EnsureRulesForCurrentProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var state = new FirewallState
        {
            UpdatedUtc = DateTimeOffset.UtcNow,
            Ran = true,
            ProcessId = Environment.ProcessId,
            ProcessPath = Environment.ProcessPath,
            IsServiceMode = !Environment.UserInteractive
        };

        try
        {
            var exePath = Environment.ProcessPath
                ?? Process.GetCurrentProcess().MainModule?.FileName;
            state.ProcessPath = exePath;
            if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            {
                state.Error = $"Missing executable path: {exePath ?? "<null>"}";
                LogAndWriteState(state, $"[RemoteSupportFirewall] skipped reason=missing_executable path={exePath ?? "<null>"}");
                return;
            }

            LogManager.WriteLog($"[RemoteSupportFirewall] ensuring Windows Firewall rules executable={exePath}");
            state.Udp = EnsureRule(RuleNameUdp, exePath, "UDP");
            state.Tcp = EnsureRule(RuleNameTcp, exePath, "TCP");
            state.UdpRulePresent = RuleExists(RuleNameUdp, out var udpRuleCheckOutput);
            state.UdpRuleCheckOutput = udpRuleCheckOutput;
            state.TcpRulePresent = RuleExists(RuleNameTcp, out var tcpRuleCheckOutput);
            state.TcpRuleCheckOutput = tcpRuleCheckOutput;
            LogAndWriteState(
                state,
                $"[RemoteSupportFirewall] preflight complete udpPresent={state.UdpRulePresent} tcpPresent={state.TcpRulePresent}");
        }
        catch (Exception ex)
        {
            state.Error = ex.ToString();
            LogAndWriteState(state, $"[RemoteSupportFirewall] setup failed; continuing without firewall preflight: {ex}");
        }
    }

    private static RuleAttempt EnsureRule(string ruleName, string exePath, string protocol)
    {
        var attempt = new RuleAttempt
        {
            RuleName = ruleName,
            Protocol = protocol
        };

        var delete = RunNetsh(
            $"advfirewall firewall delete rule name={Quote(ruleName)}",
            TimeSpan.FromSeconds(10));
        attempt.NetshDeleteExitCode = delete.ExitCode;
        attempt.NetshDeleteOutput = TrimForState(delete.Output);
        if (delete.ExitCode != 0)
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] existing rule delete skipped name={ruleName} protocol={protocol} exit={delete.ExitCode} output={TrimForLog(delete.Output)}");
        }

        var addArgs =
            "advfirewall firewall add rule " +
            $"name={Quote(ruleName)} " +
            "dir=in action=allow enable=yes profile=any interfacetype=any " +
            $"program={Quote(exePath)} protocol={protocol} " +
            "description=\"Allows NetRatel.Client WebRTC remote support ICE media sockets.\"";

        var add = RunNetsh(addArgs, TimeSpan.FromSeconds(10));
        attempt.NetshAddExitCode = add.ExitCode;
        attempt.NetshAddOutput = TrimForState(add.Output);
        if (add.ExitCode == 0)
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] rule ready name={ruleName} protocol={protocol} executable={exePath}");
            attempt.RulePresentAfterNetsh = RuleExists(ruleName, out var ruleCheckAfterNetshOutput);
            attempt.RuleCheckAfterNetshOutput = ruleCheckAfterNetshOutput;
            if (attempt.RulePresentAfterNetsh)
            {
                return attempt;
            }

            LogManager.WriteLog($"[RemoteSupportFirewall] netsh reported success but rule was not found name={ruleName} protocol={protocol} output={TrimForLog(attempt.RuleCheckAfterNetshOutput)}");
        }
        else
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] rule add failed name={ruleName} protocol={protocol} exit={add.ExitCode} output={TrimForLog(add.Output)}");
        }

        var ps = RunPowerShell(CreatePowerShellRuleScript(ruleName, exePath, protocol), TimeSpan.FromSeconds(20));
        attempt.PowerShellExitCode = ps.ExitCode;
        attempt.PowerShellOutput = TrimForState(ps.Output);
        attempt.RulePresentAfterPowerShell = RuleExists(ruleName, out var ruleCheckAfterPowerShellOutput);
        attempt.RuleCheckAfterPowerShellOutput = ruleCheckAfterPowerShellOutput;
        if (attempt.PowerShellExitCode == 0 && attempt.RulePresentAfterPowerShell)
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] PowerShell fallback rule ready name={ruleName} protocol={protocol} executable={exePath}");
        }
        else
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] PowerShell fallback failed name={ruleName} protocol={protocol} exit={attempt.PowerShellExitCode} present={attempt.RulePresentAfterPowerShell} output={TrimForLog(attempt.PowerShellOutput)} check={TrimForLog(attempt.RuleCheckAfterPowerShellOutput)}");
        }

        return attempt;
    }

    private static ProcessResult RunNetsh(string arguments, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "netsh.exe",
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start netsh.exe.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort only; firewall setup must never block client startup.
            }

            return new ProcessResult(-1, "netsh.exe timed out.");
        }

        process.WaitForExit();
        var output = (stdout.ToString() + stderr.ToString()).Trim();
        return new ProcessResult(process.ExitCode, output);
    }

    private static ProcessResult RunPowerShell(string command, TimeSpan timeout)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = "-NoProfile -ExecutionPolicy Bypass -Command " + Quote(command),
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        return RunProcess(startInfo, timeout, "powershell.exe");
    }

    private static ProcessResult RunProcess(ProcessStartInfo startInfo, TimeSpan timeout, string processName)
    {
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Unable to start {processName}.");

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stdout.AppendLine(e.Data);
            }
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is not null)
            {
                stderr.AppendLine(e.Data);
            }
        };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
            }

            return new ProcessResult(-1, $"{processName} timed out.");
        }

        process.WaitForExit();
        var output = (stdout.ToString() + stderr.ToString()).Trim();
        return new ProcessResult(process.ExitCode, output);
    }

    private static bool RuleExists(string ruleName, out string output)
    {
        var result = RunNetsh(
            $"advfirewall firewall show rule name={Quote(ruleName)}",
            TimeSpan.FromSeconds(5));
        output = TrimForState(result.Output);
        return result.ExitCode == 0 &&
            !result.Output.Contains("No rules match", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreatePowerShellRuleScript(string ruleName, string exePath, string protocol)
    {
        var escapedRule = EscapePowerShellSingleQuoted(ruleName);
        var escapedGroup = EscapePowerShellSingleQuoted(RuleGroup);
        var escapedPath = EscapePowerShellSingleQuoted(exePath);
        var escapedProtocol = EscapePowerShellSingleQuoted(protocol);

        return
            "$ErrorActionPreference = 'Stop'; " +
            $"$name = '{escapedRule}'; " +
            $"Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue | Remove-NetFirewallRule; " +
            $"New-NetFirewallRule -DisplayName $name -Group '{escapedGroup}' -Direction Inbound -Action Allow -Enabled True -Profile Any -InterfaceType Any -Program '{escapedPath}' -Protocol '{escapedProtocol}' | Out-Null; " +
            "Get-NetFirewallRule -DisplayName $name | Format-List DisplayName,Enabled,Direction,Action,Profile";
    }

    private static string EscapePowerShellSingleQuoted(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);

    private static void LogAndWriteState(FirewallState state, string logLine)
    {
        LogManager.WriteLog(logLine);
        WriteState(state);
    }

    private static void WriteState(FirewallState state)
    {
        try
        {
            state.UpdatedUtc = DateTimeOffset.UtcNow;
            var directory = Path.GetDirectoryName(StatePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            LogManager.WriteLog($"[RemoteSupportFirewall] state write failed path={StatePath} error={ex.Message}");
        }
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private static string TrimForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 500
            ? normalized
            : normalized[..500] + "...";
    }

    private static string TrimForState(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings(" ").Trim();
        return normalized.Length <= 4000
            ? normalized
            : normalized[..4000] + "...";
    }

    private readonly record struct ProcessResult(int ExitCode, string Output);

    private sealed class FirewallState
    {
        public DateTimeOffset UpdatedUtc { get; set; }
        public bool Ran { get; set; }
        public int ProcessId { get; set; }
        public string? ProcessPath { get; set; }
        public bool IsServiceMode { get; set; }
        public bool UdpRulePresent { get; set; }
        public bool TcpRulePresent { get; set; }
        public string? UdpRuleCheckOutput { get; set; }
        public string? TcpRuleCheckOutput { get; set; }
        public RuleAttempt? Udp { get; set; }
        public RuleAttempt? Tcp { get; set; }
        public string? Error { get; set; }
    }

    private sealed class RuleAttempt
    {
        public string RuleName { get; set; } = string.Empty;
        public string Protocol { get; set; } = string.Empty;
        public int NetshDeleteExitCode { get; set; }
        public string? NetshDeleteOutput { get; set; }
        public int NetshAddExitCode { get; set; }
        public string? NetshAddOutput { get; set; }
        public bool RulePresentAfterNetsh { get; set; }
        public string? RuleCheckAfterNetshOutput { get; set; }
        public int? PowerShellExitCode { get; set; }
        public string? PowerShellOutput { get; set; }
        public bool RulePresentAfterPowerShell { get; set; }
        public string? RuleCheckAfterPowerShellOutput { get; set; }
    }
}
