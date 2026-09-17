using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using NetRatel.Shared.Contracts.Terminals;

namespace NetRatel.API.Services.Terminal;

public sealed class TerminalTimingRecorder
{
    private static readonly Meter Meter = new("NetRatel.Terminal", "1.0.0");
    private static readonly Histogram<double> StageLatencyMs = Meter.CreateHistogram<double>(
        "netratel_terminal_stage_latency_ms",
        "ms",
        "Terminal stage latency in milliseconds.");
    private static readonly Counter<long> FrameCounter = Meter.CreateCounter<long>(
        "netratel_terminal_frames_total",
        "frames",
        "Terminal frames observed.");
    private static readonly Counter<long> ByteCounter = Meter.CreateCounter<long>(
        "netratel_terminal_bytes_total",
        "bytes",
        "Terminal bytes observed.");

    private readonly ConcurrentDictionary<string, TerminalTimingWindow> _windows = new(StringComparer.Ordinal);

    public TerminalTimingWindow GetOrCreate(string sessionId, TerminalTransportKind transport) =>
        _windows.GetOrAdd(sessionId, id => new TerminalTimingWindow(id, transport));

    public void Remove(string sessionId) => _windows.TryRemove(sessionId, out _);

    public TerminalDiagnosticsDto? GetDiagnostics(string sessionId)
        => _windows.TryGetValue(sessionId, out var window) ? window.ToDto() : null;

    public void RecordStage(string sessionId, TerminalTransportKind transport, string stage, double elapsedMs)
    {
        GetOrCreate(sessionId, transport).RecordStage(stage, elapsedMs);
        StageLatencyMs.Record(elapsedMs, new KeyValuePair<string, object?>("transport", transport.ToString()), new KeyValuePair<string, object?>("stage", stage));
    }

    public void RecordFrame(string sessionId, TerminalTransportKind transport, string direction, int bytes)
    {
        GetOrCreate(sessionId, transport).RecordFrame(direction, bytes);
        FrameCounter.Add(1, new KeyValuePair<string, object?>("transport", transport.ToString()), new KeyValuePair<string, object?>("direction", direction));
        if (bytes > 0)
        {
            ByteCounter.Add(bytes, new KeyValuePair<string, object?>("transport", transport.ToString()), new KeyValuePair<string, object?>("direction", direction));
        }
    }

    public sealed class TerminalTimingWindow
    {
        private const int MaxSamples = 256;
        private readonly object _sync = new();
        private readonly Dictionary<string, Queue<double>> _samples = new(StringComparer.OrdinalIgnoreCase);
        private long _inputFrames;
        private long _outputFrames;
        private long _inputBytes;
        private long _outputBytes;
        private string? _lastStage;
        private long? _lastStageUnixMs;

        public TerminalTimingWindow(string sessionId, TerminalTransportKind transport)
        {
            SessionId = sessionId;
            Transport = transport;
        }

        public string SessionId { get; }
        public TerminalTransportKind Transport { get; }
        public string State { get; set; } = "opening";

        public void RecordStage(string stage, double elapsedMs)
        {
            if (elapsedMs < 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs))
            {
                return;
            }

            lock (_sync)
            {
                _lastStage = stage;
                _lastStageUnixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

                if (!_samples.TryGetValue(stage, out var queue))
                {
                    queue = new Queue<double>(MaxSamples);
                    _samples[stage] = queue;
                }

                queue.Enqueue(elapsedMs);
                while (queue.Count > MaxSamples)
                {
                    queue.Dequeue();
                }
            }
        }

        public void RecordFrame(string direction, int bytes)
        {
            if (string.Equals(direction, "input", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _inputFrames);
                Interlocked.Add(ref _inputBytes, Math.Max(bytes, 0));
            }
            else
            {
                Interlocked.Increment(ref _outputFrames);
                Interlocked.Add(ref _outputBytes, Math.Max(bytes, 0));
            }
        }

        public TerminalDiagnosticsDto ToDto()
        {
            lock (_sync)
            {
                return new TerminalDiagnosticsDto(
                    SessionId,
                    Transport,
                    State,
                    Interlocked.Read(ref _inputFrames),
                    Interlocked.Read(ref _outputFrames),
                    Interlocked.Read(ref _inputBytes),
                    Interlocked.Read(ref _outputBytes),
                    Percentile("api.input.dispatch", 50),
                    Percentile("api.input.dispatch", 95),
                    Percentile("agent.input.pty", 50),
                    Percentile("agent.input.pty", 95),
                    Percentile("agent.output.api", 50),
                    Percentile("agent.output.api", 95),
                    Percentile("api.output.sse", 50),
                    Percentile("api.output.sse", 95),
                    _lastStage,
                    _lastStageUnixMs);
            }
        }

        private double? Percentile(string stage, int percentile)
        {
            if (!_samples.TryGetValue(stage, out var queue) || queue.Count == 0)
            {
                return null;
            }

            var ordered = queue.OrderBy(x => x).ToArray();
            var index = Math.Clamp((int)Math.Ceiling(percentile / 100d * ordered.Length) - 1, 0, ordered.Length - 1);
            return Math.Round(ordered[index], 2);
        }
    }
}
