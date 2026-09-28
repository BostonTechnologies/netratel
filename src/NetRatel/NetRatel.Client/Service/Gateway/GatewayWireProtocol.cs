using System;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Immutable tokens carried by the supported gateway protocol. This validates
/// the peer's negotiated frame; it does not choose or configure a transport.
/// </summary>
public static class GatewayWireProtocol
{
    public const string AkkaAuthorityToken = "akka";

    public static bool HasAkkaAuthority(string? value) =>
        string.Equals(value, AkkaAuthorityToken, StringComparison.Ordinal);
}
