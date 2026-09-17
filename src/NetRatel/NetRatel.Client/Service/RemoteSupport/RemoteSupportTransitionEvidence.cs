using System;

namespace NetRatel.Client.Service.RemoteSupport;

/// <summary>Endpoint-local fact sink. Windows/native code never depends on gRPC or protobuf contracts.</summary>
internal interface IRemoteSupportTransitionEvidenceSink
{
    void Report(RemoteSupportTransitionEvidenceFact evidence);
}

/// <summary>Redacted factual observation associated with one logical V2 media session.</summary>
internal sealed record RemoteSupportTransitionEvidenceFact(
    string SessionId,
    string Kind,
    int? WindowsSessionId = null,
    string? UserSidHash = null,
    int? ActiveConsoleSessionId = null,
    string? WindowsSessionState = null,
    string? DesktopKind = null,
    string? ProviderKind = null,
    Guid? HelperRouteId = null);
