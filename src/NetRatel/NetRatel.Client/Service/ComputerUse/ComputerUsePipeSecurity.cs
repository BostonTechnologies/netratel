using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetRatel.Client.Service.ComputerUse;

/// <summary>Bind CUA IPC to the actual OS pipe peer, never a PID supplied in a hello message.</summary>
[SupportedOSPlatform("windows")]
internal static class ComputerUsePipeSecurity
{
    internal static bool IsVerifiedHelper(NamedPipeServerStream pipe, int claimedPid, int claimedSessionId)
    {
        if (!GetNamedPipeClientProcessId(pipe.SafePipeHandle, out var peerPid) || peerPid != claimedPid)
            return false;
        return IsOurExecutable(checked((int)peerPid), claimedSessionId);
    }

    internal static bool IsVerifiedService(NamedPipeClientStream pipe)
    {
        if (!GetNamedPipeServerProcessId(pipe.SafePipeHandle, out var peerPid)) return false;
        return IsOurExecutable(checked((int)peerPid), 0) && IsInstalledSystemService(peerPid);
    }

    private static bool IsOurExecutable(int pid, int expectedSession)
    {
        try
        {
            using var peer = Process.GetProcessById(pid);
            using var handle = OpenProcess(0x1000, false, checked((uint)pid)); // PROCESS_QUERY_LIMITED_INFORMATION
            var image = new StringBuilder(32768);
            var characters = image.Capacity;
            if (peer.SessionId != expectedSession || string.IsNullOrWhiteSpace(Environment.ProcessPath) ||
                !string.Equals(Path.GetFileName(Environment.ProcessPath), "NetRatel.Client.exe", StringComparison.OrdinalIgnoreCase) || handle.IsInvalid ||
                !QueryFullProcessImageName(handle, 0, image, ref characters) ||
                !string.Equals(Path.GetFullPath(image.ToString()), Path.GetFullPath(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    private static bool IsInstalledSystemService(uint peerPid)
    {
        // SCM read access works for ordinary logged-in users and avoids opening the LocalSystem token.
        var manager = OpenSCManager(null, null, 0x0001);
        if (manager == IntPtr.Zero) return false;
        try
        {
            var service = OpenService(manager, "NetRatel.Client", 0x0001 | 0x0004);
            if (service == IntPtr.Zero) return false;
            try
            {
                if (!QueryServiceStatusEx(service, 0, out var status, Marshal.SizeOf<ServiceStatusProcess>(), out _) ||
                    status.ProcessId != peerPid || status.CurrentState != 4) return false;
                QueryServiceConfig(service, IntPtr.Zero, 0, out var needed);
                if (needed is <= 0 or > 65536) return false;
                var buffer = Marshal.AllocHGlobal(needed);
                try
                {
                    if (!QueryServiceConfig(service, buffer, needed, out _)) return false;
                    var config = Marshal.PtrToStructure<ServiceConfiguration>(buffer);
                    return string.Equals(Marshal.PtrToStringUni(config.StartName), "LocalSystem", StringComparison.OrdinalIgnoreCase);
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(manager); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusProcess
    {
        internal uint ServiceType, CurrentState, ControlsAccepted, Win32ExitCode, ServiceSpecificExitCode, CheckPoint, WaitHint, ProcessId, ServiceFlags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceConfiguration
    {
        internal uint ServiceType, StartType, ErrorControl;
        internal IntPtr BinaryPathName, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, StartName, DisplayName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, int flags, StringBuilder image, ref int size);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenSCManager(string? machineName, string? databaseName, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenService(IntPtr manager, string serviceName, uint access);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatusEx(IntPtr service, int level, out ServiceStatusProcess status, int size, out int needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceConfig(IntPtr service, IntPtr config, int size, out int needed);
    [DllImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);
}
