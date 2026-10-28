using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Client.Service.ComputerUse;

internal interface IComputerUseDesktop
{
    void Validate(ComputerUseTarget target, bool requireWindow);
    string Fingerprint(ComputerUseTarget target);
    uint LastInputTick { get; }
}

[SupportedOSPlatform("windows")]
internal sealed class WindowsComputerUseDesktop : IComputerUseDesktop
{
    public uint LastInputTick
    {
        get
        {
            var info = new LastInputInfo { Size = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) throw new ComputerUseRefusalException("input_state_unavailable", "Cannot establish the user's current input state.");
            return info.Tick;
        }
    }

    public void Validate(ComputerUseTarget target, bool requireWindow)
    {
        using var process = Process.GetCurrentProcess();
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value;
        var hash = sid is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sid.ToLowerInvariant()))).ToLowerInvariant();
        if (process.SessionId <= 0 || process.SessionId != target.WindowsSessionId ||
            !string.Equals(hash, target.UserSidHash, StringComparison.Ordinal))
            throw new ComputerUseRefusalException("wrong_windows_session", "The requested Windows session and user do not match this helper.");

        if (!WTSQuerySessionInformation(IntPtr.Zero, process.SessionId, 8, out var sessionBuffer, out var sessionBytes))
            throw new ComputerUseRefusalException("session_state_unavailable", "Cannot determine whether the selected session is connected.");
        try
        {
            if (sessionBytes < sizeof(int) || Marshal.ReadInt32(sessionBuffer) != 0)
                throw new ComputerUseRefusalException("session_disconnected", "The selected session is not active.");
        }
        finally { WTSFreeMemory(sessionBuffer); }

        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) throw new ComputerUseRefusalException("desktop_unavailable", "The input desktop is locked, protected, or unavailable.");
        try
        {
            var name = new StringBuilder(128);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * sizeof(char), out _) ||
                !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new ComputerUseRefusalException("protected_desktop", "Computer use is limited to the user's unlocked default desktop.");
        }
        finally { CloseDesktop(desktop); }

        if (!requireWindow) return;
        if (!target.IsWindowTarget) throw new ComputerUseRefusalException("window_required", "An exact process and window are required.");
        var hwnd = WindowHandle(target);
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != hwnd)
            throw new ComputerUseRefusalException("window_unavailable", "The selected top-level window is no longer visible or valid.");
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != target.ProcessId) throw new ComputerUseRefusalException("wrong_window", "The window no longer belongs to the selected process.");
        try
        {
            using var targetProcess = Process.GetProcessById(checked((int)pid));
            if (targetProcess.SessionId != target.WindowsSessionId)
                throw new ComputerUseRefusalException("wrong_windows_session", "The target process belongs to another Windows session.");
            using var handle = OpenProcess(0x1000, false, pid);
            if (handle.IsInvalid || !OpenProcessToken(handle, 0x0008, out var targetToken))
                throw new ComputerUseRefusalException("target_access_denied", "Cannot verify the target process integrity level.");
            using (targetToken)
            {
                using var targetIdentity = new WindowsIdentity(targetToken.DangerousGetHandle());
                if (!string.Equals(targetIdentity.User?.Value, sid, StringComparison.OrdinalIgnoreCase))
                    throw new ComputerUseRefusalException("wrong_target_user", "The target process belongs to another Windows identity.");
                if (!GetTokenInformation(targetToken, 20, out var elevated, sizeof(int), out _) || elevated != 0)
                    throw new ComputerUseRefusalException("elevated_target_unsupported", "Elevated applications are outside this provider's authority.");
            }
        }
        catch (ArgumentException)
        {
            throw new ComputerUseRefusalException("target_exited", "The target process has exited.");
        }
    }

    public string Fingerprint(ComputerUseTarget target)
    {
        using var process = Process.GetProcessById(target.ProcessId!.Value);
        return FormattableString.Invariant($"{process.Id}:{process.StartTime.ToUniversalTime().Ticks}:{target.WindowId}");
    }

    internal static IntPtr WindowHandle(ComputerUseTarget target)
    {
        if (!long.TryParse(target.WindowId, NumberStyles.None, CultureInfo.InvariantCulture, out var hwnd) || hwnd <= 0)
            throw new ComputerUseRefusalException("invalid_window", "Window IDs must come from this provider's window inventory.");
        return new IntPtr(hwnd);
    }

    internal static WindowRect Bounds(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect) || rect.Width <= 0 || rect.Height <= 0 || rect.Width > 16384 || rect.Height > 16384)
            throw new ComputerUseRefusalException("window_geometry_unavailable", "Cannot establish bounded window geometry.");
        return rect;
    }

    internal static IReadOnlyList<ComputerUseWindow> ListWindows(ComputerUseTarget target)
    {
        List<ComputerUseWindow> windows = [];
        EnumWindows((hwnd, _) =>
        {
            if (windows.Count >= 128) return false;
            if (!IsWindowVisible(hwnd)) return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == 0 || target.ProcessId is { } requested && requested != pid) return true;
            try
            {
                using var process = Process.GetProcessById(checked((int)pid));
                if (process.SessionId != target.WindowsSessionId) return true;
                var title = new StringBuilder(257);
                GetWindowText(hwnd, title, title.Capacity);
                if (title.Length != 0)
                    windows.Add(new(checked((int)pid), hwnd.ToInt64().ToString(CultureInfo.InvariantCulture), title.ToString(), process.ProcessName));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                ComputerUseDiagnostics.Record("window_inventory_entry_unavailable", exception);
            }
            return true;
        }, IntPtr.Zero);
        return windows;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct WindowRect
    {
        internal readonly int Left, Top, Right, Bottom;
        internal int Width => Right - Left;
        internal int Height => Bottom - Top;
    }
    [StructLayout(LayoutKind.Sequential)] private struct LastInputInfo { internal uint Size; internal uint Tick; }
    internal delegate bool EnumWindowCallback(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumWindows(EnumWindowCallback callback, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool EnumChildWindows(IntPtr parent, EnumWindowCallback callback, IntPtr lParam);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] internal static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] internal static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(IntPtr hwnd, out WindowRect rect);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetUserObjectInformation(IntPtr obj, int index, StringBuilder info, int length, out int needed);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetLastInputInfo(ref LastInputInfo info);
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool WTSQuerySessionInformation(IntPtr server, int session, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int infoClass, out int value, int length, out int needed);
}
