namespace NetRatel.Shared.Client;

/// <summary>The secondary process-failure policy shared by native installations and repair templates.</summary>
public static class ManagedServiceRecovery
{
    public const string LinuxDropIn = "[Unit]\nStartLimitIntervalSec=0\n[Service]\nRestart=always\nRestartSec=30s\nRestartPreventExitStatus=78\n";
    public const string WindowsActions = "restart/30000/restart/60000/restart/300000";
    public const uint WindowsResetSeconds = 86400;
}
