using System.Collections.Concurrent;
using System.Threading.Channels;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed class TerminalStreamRouter
{
    private readonly ConcurrentDictionary<string, TerminalSession> _sessions = new(StringComparer.Ordinal);

    public TerminalSession Register(string sessionId, CancellationToken cancellationToken)
    {
        var session = _sessions.GetOrAdd(sessionId, static id => new TerminalSession(id));
        session.Attach(cancellationToken);
        return session;
    }

    public bool Write(string sessionId, string chunkData, string? direction, ulong? sequence)
    {
        if (string.IsNullOrEmpty(chunkData))
        {
            return false;
        }

        var session = _sessions.GetOrAdd(sessionId, static id => new TerminalSession(id));
        return session.TryWrite(
            new TerminalStreamMessage(
                Kind: "data",
                Data: chunkData,
                Direction: direction,
                Sequence: sequence),
            CreateDataKey(direction, sequence, chunkData));
    }

    public void Close(string sessionId, string? reason)
    {
        var session = _sessions.GetOrAdd(sessionId, static id => new TerminalSession(id));
        session.TryWrite(
            new TerminalStreamMessage(
                Kind: "close",
                Reason: reason),
            CreateCloseKey(reason));
        session.OutputChannel.Writer.TryComplete();
    }

    public void Unregister(string sessionId, TerminalSession session)
    {
        if (_sessions.TryGetValue(sessionId, out var current) && ReferenceEquals(current, session))
        {
            _sessions.TryRemove(sessionId, out _);
        }
    }

    public sealed class TerminalSession
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _seen = new(StringComparer.Ordinal);
        private CancellationTokenRegistration _registration;

        public TerminalSession(string sessionId)
        {
            SessionId = sessionId;
            OutputChannel = Channel.CreateUnbounded<TerminalStreamMessage>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = false
            });
            CancellationToken = CancellationToken.None;
        }

        public string SessionId { get; }
        public Channel<TerminalStreamMessage> OutputChannel { get; }
        public CancellationToken CancellationToken { get; private set; }

        public void Attach(CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _registration.Dispose();
                CancellationToken = cancellationToken;
                if (cancellationToken.CanBeCanceled)
                {
                    _registration = cancellationToken.Register(() => OutputChannel.Writer.TryComplete());
                }
            }
        }

        public bool TryWrite(TerminalStreamMessage message, string dedupeKey)
        {
            lock (_sync)
            {
                if (!_seen.Add(dedupeKey))
                {
                    return false;
                }
            }

            return OutputChannel.Writer.TryWrite(message);
        }
    }

    private static string CreateDataKey(string? direction, ulong? sequence, string chunkData) =>
        $"data:{direction ?? "stdout"}:{sequence?.ToString() ?? string.Empty}:{chunkData}";

    private static string CreateCloseKey(string? reason) =>
        $"close:{reason ?? string.Empty}";
}
