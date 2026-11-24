using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NetRatel.Shared.Contracts.Secrets;

namespace NetRatel.Client.Service.Shells;

internal static class NativeProcessIdentity
{
    // The default Unix process backends spawn with the service's effective UID.
    // Observe it before fork/exec so account lookup stays out of the forked child.
    public static ObservedExecutionIdentity? ObserveInheritedUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return null;
        var uid = GetEffectiveUserId().ToString(CultureInfo.InvariantCulture);
        var account = Environment.UserName;
        return ObservedExecutionIdentity.FromObservation(string.IsNullOrWhiteSpace(account) ? $"UID {uid}" : account, uid);
    }

    public static ObservedExecutionIdentity? ObserveStartedProcess(Process process)
    {
        if (!OperatingSystem.IsWindows()) return ObserveInheritedUnix();
        try { return ObserveWindowsProcess(process.SafeHandle); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { return null; }
    }

    [SupportedOSPlatform("windows")]
    public static ObservedExecutionIdentity? ObserveWindowsProcess(SafeProcessHandle process)
    {
        const uint tokenQuery = 0x0008;
        if (!OpenProcessToken(process, tokenQuery, out var token)) return null;
        using (token)
        using (var identity = new WindowsIdentity(token.DangerousGetHandle()))
        {
            if (identity.User?.Value is not { } sid) return null;
            try { return ObservedExecutionIdentity.FromObservation(identity.Name, sid); }
            catch (Exception error) when (error is IdentityNotMappedException or Win32Exception or SecurityException)
            { return ObservedExecutionIdentity.FromObservation(sid, sid); }
        }
    }

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserId();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
}
