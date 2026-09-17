using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed class TerminalTransportOptions
{
    public TerminalTransportKind DefaultTransport { get; set; } = TerminalTransportKind.ApiWebSocket;
    public int AgentTunnelStaleSeconds { get; set; } = 30;
    public int AgentTunnelPingSeconds { get; set; } = 15;
}
