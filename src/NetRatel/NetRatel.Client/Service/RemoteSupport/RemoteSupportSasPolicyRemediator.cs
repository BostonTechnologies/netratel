using Microsoft.Win32;
using System;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace NetRatel.Client.Service.RemoteSupport;

internal sealed record RemoteSupportSasPolicyRemediationResult(
    string Status,
    bool Success,
    bool Confirmed,
    bool Elevated,
    int? BeforeValue,
    int? AfterValue,
    bool Changed,
    string PolicySource,
    string Message,
    string? Error = null);

[SupportedOSPlatform("windows")]
internal static class RemoteSupportSasPolicyRemediator
{
    internal const string PolicyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";
    internal const string PolicyValueName = "SoftwareSASGeneration";

    public static RemoteSupportSasPolicyRemediationResult EnableForServices(bool confirmed)
    {
        var source = $@"HKLM\{PolicyPath}\{PolicyValueName}";
        var before = ReadValue();
        var elevated = IsElevated();
        if (!confirmed)
        {
            return new(
                "confirmation_required",
                false,
                false,
                elevated,
                before,
                before,
                false,
                source,
                "No policy change was made. Re-run with --confirm from an elevated console to allow services to generate SAS.");
        }

        if (!elevated)
        {
            return new(
                "elevation_required",
                false,
                true,
                false,
                before,
                before,
                false,
                source,
                "No policy change was made. Start an elevated console and retry.");
        }

        try
        {
            var target = EnabledValue(before);
            using var key = Registry.LocalMachine.CreateSubKey(PolicyPath, writable: true)
                ?? throw new InvalidOperationException($"Unable to open {source} for writing.");
            key.SetValue(PolicyValueName, target, RegistryValueKind.DWord);
            var after = ReadValue();
            var success = after is 1 or 3;
            return new(
                success ? (before == after ? "already_enabled" : "enabled") : "verification_failed",
                success,
                true,
                true,
                before,
                after,
                before != after,
                source,
                success
                    ? "Windows software SAS policy now permits services to generate Ctrl+Alt+Del. Reconnect Remote Support and retry."
                    : "The policy write completed but verification did not report a service-enabled value.");
        }
        catch (Exception ex)
        {
            return new(
                "failed",
                false,
                true,
                true,
                before,
                ReadValue(),
                false,
                source,
                "Windows software SAS policy could not be updated.",
                ex.Message);
        }
    }

    internal static int EnabledValue(int? currentValue) => currentValue switch
    {
        1 or 3 => currentValue.Value,
        2 => 3,
        _ => 1
    };

    private static int? ReadValue()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(PolicyPath, writable: false);
            return key?.GetValue(PolicyValueName) is int value ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
