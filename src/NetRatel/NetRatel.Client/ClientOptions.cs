using System;
using NetRatel.Shared;

namespace NetRatel.Client;

public class ClientOptions
{
    public Guid TenantId { get; set; }
    public ClientEnvironment Environment { get; set; } = ClientEnvironment.Dev;
    public string ApiBaseUrl { get; set; } = "https://localhost:5005";
    public bool UseInProcPowerShell { get; set; }
    public string TerminalBackendPreference { get; set; } = "Auto";
    public bool EnableNativeUnixPty { get; set; } = true;
    public int TerminalGracefulExitTimeoutMs { get; set; } = 1500;
    public int TerminalKillTimeoutMs { get; set; } = 2000;
    public string? EnrollmentCode { get; set; }
    public string? AgentId { get; set; }
    public AutoUpdateOptions AutoUpdate { get; set; } = new();
}

public sealed class AutoUpdateOptions
{
    public string Mode { get; set; } = "Service";
    public string? RuntimeId { get; set; }
    public string StateDirectory { get; set; } = "";
    public string RequestPath { get; set; } = "";
    public string ReadyPath { get; set; } = "";
    public string LinuxServiceName { get; set; } = "netratel-update.service";
    public string WindowsServiceName { get; set; } = "NetRatel.Update";
    public string Channel { get; set; } = "Stable";
    public long MaximumArtifactBytes { get; set; } = 1_073_741_824;
    public int ActivationTimeoutSeconds { get; set; } = 180;
    public int RollbackCheckInTimeoutSeconds { get; set; } = 120;
}
