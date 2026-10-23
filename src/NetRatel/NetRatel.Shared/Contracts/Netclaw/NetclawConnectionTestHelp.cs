namespace NetRatel.Shared.Contracts.Netclaw;

internal static class NetclawConnectionTestHelp
{
    internal static string ForCode(string? code) => code switch
    {
        "device-token-unavailable" => "The NetRatel API cannot read the saved device credential. Restore its original Data Protection keyring and application identity, then test again. If the original keys are unavailable, explicitly re-pair this tenant with a new one-time code.",
        "daemon-authentication-rejected" => "The daemon rejected the saved device credential. Check that the saved daemon address is correct and the device is still authorized; re-pair this tenant only if that credential was revoked or replaced.",
        "daemon-session-endpoint-unavailable" => "The configured address does not serve the Netclaw session endpoint. Check the daemon address and reverse-proxy routing for /hub/session, then test the saved connection again.",
        "daemon-name-resolution-failed" => "The NetRatel API could not resolve the daemon hostname. Check DNS from the API host or container, then test the saved connection again.",
        "daemon-tls-verification-failed" => "The NetRatel API could not verify the daemon's TLS connection. Correct the certificate hostname, validity or trusted chain on the API host, then test again.",
        "daemon-connection-refused" => "The daemon refused the connection from the NetRatel API. Start its listener and check the configured port and firewall from the API host or container, then test again.",
        "daemon-network-unreachable" => "The NetRatel API could not reach the daemon network. Check routing, firewall and the configured daemon address from the API host or container, then test again.",
        "daemon-endpoint-rejected" => "The daemon endpoint was rejected by outbound endpoint validation. Use the supported daemon address and private-HTTP configuration; check its resolved addresses and reverse-proxy routing.",
        "daemon-session-protocol-rejected" or "session-not-confirmed" => "The daemon did not confirm an authenticated Netclaw session. Check that the endpoint serves the supported SignalR session protocol and inspect the daemon logs, then test the saved connection again.",
        "session-test-timeout" => "The authenticated session test timed out. Check daemon health and connectivity from the NetRatel API host or container, then test the saved connection again.",
        "daemon-http-request-failed" => "The daemon or its reverse proxy rejected the session HTTP request. Check their health and session routing from the NetRatel API host or container, then test again.",
        _ => "The authenticated session could not be verified. Check the daemon and its connectivity from the NetRatel API host or container, then test the saved connection again."
    };
}
