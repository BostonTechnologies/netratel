using NetRatel.Client.Service.RemoteDesktop;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>
/// Local Windows-session inspection used by the Akka gateway provider. It has no
/// network, reducer, or subscription responsibility.
/// </summary>
internal static class RemoteSupportWindowsSessionInventory
{
    public static IReadOnlyList<WindowsSessionInventoryItem> CaptureLocalForDiagnostics()
    {
        if (!OperatingSystem.IsWindows())
        {
            return Array.Empty<WindowsSessionInventoryItem>();
        }

        return CaptureWindowsCore(
            Array.Empty<ConnectedUserHelper>(),
            "diagnostics",
            new RemoteSupportSessionIdentityCache());
    }

    /// <summary>
    /// Captures the agent-owned inventory used by the V2 preparation stream.
    /// Helper connections are supplied by the same process that owns the pipe
    /// host, so this method cannot silently substitute another WTS session.
    /// </summary>
    internal static IReadOnlyList<WindowsSessionInventoryItem> CaptureForRemoteSupport(
        IReadOnlyCollection<ConnectedUserHelper> helpers,
        string agentVersion,
        RemoteSupportSessionIdentityCache identityCache)
    {
        ArgumentNullException.ThrowIfNull(helpers);
        ArgumentNullException.ThrowIfNull(identityCache);

        return OperatingSystem.IsWindows()
            ? CaptureWindowsCore(helpers, agentVersion, identityCache)
            : Array.Empty<WindowsSessionInventoryItem>();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WindowsSessionInventoryItem> CaptureWindowsCore(
        IReadOnlyCollection<ConnectedUserHelper> helpers,
        string agentVersion,
        RemoteSupportSessionIdentityCache identityCache)
    {
        var activeConsole = WTSGetActiveConsoleSessionId();
        var activeHelper = activeConsole == uint.MaxValue
            ? null
            : helpers.FirstOrDefault(item => item.SessionId == unchecked((int)activeConsole));
        var providerDecision = RemoteSupportProviderDiagnostics.Evaluate(
            activeHelper,
            agentVersion,
            activeHelper is not null && RemoteSupportHelperVersion.IsCompatible(activeHelper.Version, agentVersion));
        var sessions = EnumerateSessions();
        identityCache.Reconcile(sessions.Select(session => session.SessionId));

        return sessions.Select(session =>
        {
            var isActiveConsole = session.SessionId == activeConsole;
            var isConnected = session.State is WtsConnectState.Active or WtsConnectState.Connected;
            var isLocked = isActiveConsole && string.Equals(providerDecision.DesktopState, RemoteSupportDesktopStates.Locked, StringComparison.Ordinal);
            var isWinlogon = isLocked || (isActiveConsole && string.Equals(providerDecision.InputDesktopName, "Winlogon", StringComparison.OrdinalIgnoreCase));
            var username = string.IsNullOrWhiteSpace(session.Username) ? null : session.Username;
            var domain = string.IsNullOrWhiteSpace(session.Domain) ? null : session.Domain;
            var display = DisplayLabel(domain, username, session.SessionId);
            var helper = helpers.FirstOrDefault(item => item.SessionId == checked((int)session.SessionId));
            var helperMatches = helper is not null;
            var versionMatches = helperMatches && RemoteSupportHelperVersion.IsCompatible(helper!.Version, agentVersion);
            var resolvedSid = identityCache.ResolveSid(session.SessionId, domain, username, session.UserSid, isConnected);
            var identityHash = HashUserIdentity(resolvedSid);
            var hasResolvedIdentity = username is not null && identityHash is not null;
            var assistable = hasResolvedIdentity && isConnected && !isLocked && !isWinlogon && helperMatches && versionMatches;

            return new WindowsSessionInventoryItem(
                checked((int)session.SessionId),
                NormalizeState(session.State), username, domain, display, identityHash,
                isActiveConsole, session.State == WtsConnectState.Active, isConnected,
                isLocked, isWinlogon, assistable, ResolveSessionType(session.ProtocolType, isActiveConsole),
                assistable ? "interactive_user_helper" : null, helperMatches, versionMatches,
                hasResolvedIdentity && isConnected && !isLocked && !isWinlogon,
                hasResolvedIdentity && helperMatches && !versionMatches,
                helperMatches ? helper!.ProcessId : null, helperMatches ? helper!.Version : null);
        }).ToArray();
    }

    [SupportedOSPlatform("windows")]
    private static IReadOnlyList<WtsSession> EnumerateSessions()
    {
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var buffer, out var count))
        {
            return Array.Empty<WtsSession>();
        }

        try
        {
            var result = new List<WtsSession>();
            var size = Marshal.SizeOf<WTS_SESSION_INFO>();
            for (var i = 0; i < count; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(IntPtr.Add(buffer, i * size));
                var username = QuerySessionString(info.SessionID, WtsInfoClass.UserName);
                var domain = QuerySessionString(info.SessionID, WtsInfoClass.DomainName);
                var extended = QueryExtendedSessionInfo(info.SessionID);
                username ??= extended?.Username;
                domain ??= extended?.Domain;
                var tokenIdentity = QuerySessionTokenIdentity(info.SessionID);
                username ??= tokenIdentity?.Username;
                domain ??= tokenIdentity?.Domain;
                result.Add(new WtsSession(info.SessionID, info.State, username, domain, tokenIdentity?.Sid, QuerySessionProtocol(info.SessionID)));
            }

            return result;
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static string? QuerySessionString(uint sessionId, WtsInfoClass infoClass)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, infoClass, out var buffer, out _) || buffer == IntPtr.Zero)
        {
            return null;
        }

        try { return Marshal.PtrToStringUni(buffer)?.Trim(); }
        finally { WTSFreeMemory(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static WtsIdentity? QueryExtendedSessionInfo(uint sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.SessionInfoEx, out var buffer, out var bytes) ||
            buffer == IntPtr.Zero || bytes < Marshal.SizeOf<WtsInfoEx>())
        {
            return null;
        }

        try
        {
            var info = Marshal.PtrToStructure<WtsInfoEx>(buffer);
            return info.Level == 1
                ? new WtsIdentity(NullIfEmpty(info.Data.Username), NullIfEmpty(info.Data.DomainName), null)
                : null;
        }
        catch { return null; }
        finally { WTSFreeMemory(buffer); }
    }

    [SupportedOSPlatform("windows")]
    private static WtsIdentity? QuerySessionTokenIdentity(uint sessionId)
    {
        if (!WTSQueryUserToken(sessionId, out var token) || token == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using var identity = new WindowsIdentity(token);
            var accountName = identity.Name;
            var separator = accountName?.IndexOf('\\') ?? -1;
            var domain = separator > 0 ? accountName![..separator] : null;
            var username = separator >= 0 && separator < accountName!.Length - 1 ? accountName[(separator + 1)..] : accountName;
            return new WtsIdentity(NullIfEmpty(username), NullIfEmpty(domain), identity.User?.Value);
        }
        catch { return null; }
        finally { CloseHandle(token); }
    }

    [SupportedOSPlatform("windows")]
    private static ushort? QuerySessionProtocol(uint sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WtsInfoClass.ClientProtocolType, out var buffer, out var bytes) ||
            buffer == IntPtr.Zero || bytes < sizeof(ushort))
        {
            return null;
        }

        try { return unchecked((ushort)Marshal.ReadInt16(buffer)); }
        finally { WTSFreeMemory(buffer); }
    }

    internal static string ResolveSessionType(ushort? protocolType, bool isActiveConsole) => protocolType switch
    {
        0 => "console",
        2 => "rdp",
        _ => isActiveConsole ? "console" : "unknown"
    };

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeState(WtsConnectState state) => state switch
    {
        WtsConnectState.Active => "active",
        WtsConnectState.Connected => "connected",
        WtsConnectState.Disconnected => "disconnected",
        WtsConnectState.Idle => "idle",
        WtsConnectState.Listen => "listen",
        WtsConnectState.Reset => "reset",
        WtsConnectState.Down => "down",
        WtsConnectState.Init => "init",
        _ => "unknown"
    };

    private static string DisplayLabel(string? domain, string? username, uint sessionId) =>
        !string.IsNullOrWhiteSpace(username) ? (string.IsNullOrWhiteSpace(domain) ? username : $"{domain}\\{username}") : $"Session {sessionId}";

    private static string? HashUserIdentity(string? userSid) => string.IsNullOrWhiteSpace(userSid)
        ? null
        : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userSid.Trim().ToLowerInvariant()))).ToLowerInvariant();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(IntPtr hServer, int reserved, int version, out IntPtr ppSessionInfo, out int pCount);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(IntPtr memory);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(IntPtr hServer, uint sessionId, WtsInfoClass infoClass, out IntPtr ppBuffer, out uint pBytesReturned);
    [DllImport("kernel32.dll")] private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)] private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)] private struct WTS_SESSION_INFO { public uint SessionID; public IntPtr pWinStationName; public WtsConnectState State; }
    private enum WtsInfoClass { UserName = 5, DomainName = 7, ClientProtocolType = 16, SessionInfoEx = 25 }
    private enum WtsConnectState { Active, Connected, ConnectQuery, Shadow, Disconnected, Idle, Listen, Reset, Down, Init }
    private sealed record WtsSession(uint SessionId, WtsConnectState State, string? Username, string? Domain, string? UserSid, ushort? ProtocolType);
    private sealed record WtsIdentity(string? Username, string? Domain, string? Sid);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct WtsInfoEx { public int Level; public WtsInfoExLevel1 Data; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WtsInfoExLevel1
    {
        public int SessionId; public WtsConnectState SessionState; public int SessionFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 33)] public string WinStationName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 21)] public string Username;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 18)] public string DomainName;
        public long LogonTime; public long ConnectTime; public long DisconnectTime; public long LastInputTime; public long CurrentTime;
        public uint IncomingBytes; public uint OutgoingBytes; public uint IncomingFrames; public uint OutgoingFrames; public uint IncomingCompressedBytes; public uint OutgoingCompressedBytes;
    }
}

internal sealed record WindowsSessionInventoryItem(
    int WindowsSessionId, string State, string? Username, string? Domain, string DisplayLabel, string? UserSidHash,
    bool IsConsoleSession, bool IsActive, bool IsConnected, bool IsLocked, bool IsWinlogon, bool IsAssistable,
    string SessionType, string? Provider, bool HelperConnected, bool HelperVersionMatches, bool HelperLaunchable,
    bool HelperRepairable, int? HelperPid, string? HelperVersion);
