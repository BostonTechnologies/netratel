using System;

namespace NetRatel.Client.Service.RemoteDesktop;

/// <summary>Process/session lookup boundary for helper hello validation.</summary>
internal interface IRemoteSupportHelperProcessInspector
{
    bool TryGetSessionId(int processId, out int sessionId, out string? error);
}

internal sealed class CurrentProcessRemoteSupportHelperProcessInspector : IRemoteSupportHelperProcessInspector
{
    public bool TryGetSessionId(int processId, out int sessionId, out string? error)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            sessionId = process.SessionId;
            error = null;
            return true;
        }
        catch (Exception exception)
        {
            sessionId = default;
            error = $"process_validation_failed:{exception.GetType().Name}";
            return false;
        }
    }
}

internal static class RemoteSupportHelperProcessValidation
{
    public static bool TryValidate(
        RemoteDesktopHelperHello hello,
        IRemoteSupportHelperProcessInspector inspector,
        out string? error)
    {
        if (!inspector.TryGetSessionId(hello.ProcessId, out var actualSessionId, out error))
        {
            return false;
        }

        if (actualSessionId != hello.SessionId)
        {
            error = $"process_session_mismatch:{actualSessionId}";
            return false;
        }

        error = null;
        return true;
    }
}
