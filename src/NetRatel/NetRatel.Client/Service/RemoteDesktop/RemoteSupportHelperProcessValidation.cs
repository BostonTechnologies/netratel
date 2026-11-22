using System;

namespace NetRatel.Client.Service.RemoteDesktop;

internal sealed record VerifiedRemoteSupportHelper(int ProcessId, int SessionId, string UserName);

internal static class RemoteSupportHelperProcessValidation
{
    public static bool MatchesPeer(RemoteDesktopHelperHello hello, VerifiedRemoteSupportHelper peer, string expectedVersion) =>
        hello.ProcessId == peer.ProcessId && hello.SessionId == peer.SessionId &&
        hello.UserInteractive && string.Equals(hello.Version, expectedVersion, StringComparison.Ordinal);
}
