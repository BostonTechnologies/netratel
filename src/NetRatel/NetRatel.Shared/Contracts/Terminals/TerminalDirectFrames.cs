namespace NetRatel.Shared.Contracts.Terminals;

public sealed record TerminalDirectFrame(
    string Type,
    string? SessionId = null,
    string? ClientIdentityHex = null,
    string? ShellType = null,
    string? PayloadJson = null,
    string? Data = null,
    string? Direction = null,
    ulong? Sequence = null,
    string? Reason = null,
    int? Cols = null,
    int? Rows = null,
    string? Backend = null,
    string? TimingId = null,
    long? SentUnixMs = null);

public static class TerminalDirectFrameTypes
{
    public const string Hello = "hello";
    public const string Ack = "ack";
    public const string Open = "open";
    public const string Opened = "opened";
    public const string Stdin = "stdin";
    public const string Resize = "resize";
    public const string Output = "output";
    public const string Close = "close";
    public const string Closed = "closed";
    public const string Error = "error";
    public const string Ping = "ping";
    public const string Pong = "pong";
}

public static class TerminalDirectFrameDirections
{
    public const string AgentInputPty = "agent.input.pty";
    public const string AgentResizeApplied = "agent.resize.applied";
    public const string AgentResizeFailed = "agent.resize.failed";
    public const string AgentResizeUnsupported = "agent.resize.unsupported";
}
