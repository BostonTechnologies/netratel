using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using System.Threading;

namespace NetRatel.Client.Service.Terminal;

internal static class Terminal
{
    internal sealed class TerminalOpenPayload
    {
        [JsonPropertyName("shellType")]
        public string? ShellType { get; init; }

        [JsonPropertyName("cols")]
        public int? Cols { get; init; }

        [JsonPropertyName("rows")]
        public int? Rows { get; init; }

        [JsonPropertyName("workingDirectory")]
        public string? WorkingDirectory { get; init; }
    }

    internal sealed class PendingInput
    {
        public required ulong Sequence { get; init; }
        public required string Data { get; init; }
    }

    internal sealed class PendingResize
    {
        public required ulong Sequence { get; init; }
        public required int Cols { get; init; }
        public required int Rows { get; init; }
    }

    internal sealed class PendingOutput
    {
        public required ulong Sequence { get; init; }
        public required string Direction { get; init; }
        public required string Data { get; init; }
    }

    internal sealed class ActiveSessionData : IDisposable
    {
        public string StreamId { get; }
        public string ShellType { get; }
        public ITerminalHostSession Host { get; }
        public CancellationTokenSource Cts { get; } = new();
        public ConcurrentDictionary<ulong, PendingInput> BufferedInputs { get; } = new();
        public ConcurrentDictionary<ulong, byte> SeenInputSequences { get; } = new();
        public SemaphoreSlim InputSemaphore { get; } = new(1, 1);
        public ConcurrentDictionary<ulong, PendingResize> BufferedResizes { get; } = new();
        public ConcurrentDictionary<ulong, byte> SeenResizeSequences { get; } = new();
        public SemaphoreSlim ResizeSemaphore { get; } = new(1, 1);
        public ConcurrentQueue<PendingOutput> PendingOutputs { get; } = new();
        public SemaphoreSlim FlushSemaphore { get; } = new(1, 1);
        public PeriodicTimer FlushTimer { get; }
        public object OutputSequenceLock { get; } = new();
        public object LifecycleLock { get; } = new();
        public ulong NextExpectedInputSequence { get; set; } = 1;
        public ulong NextExpectedResizeSequence { get; set; } = 1;
        public ulong NextOutputSequence { get; set; }
        public int FlushScheduled;
        public bool CloseRequested { get; set; }
        public bool CloseHandled { get; set; }
        public bool ExitHandled { get; set; }

        public ActiveSessionData(string streamId, string shellType, ITerminalHostSession host, TimeSpan flushInterval)
        {
            StreamId = streamId;
            ShellType = shellType;
            Host = host;
            FlushTimer = new PeriodicTimer(flushInterval);
        }

        public void Dispose()
        {
            try { Cts.Cancel(); } catch { }

            Host.Dispose();
            FlushTimer.Dispose();
            InputSemaphore.Dispose();
            ResizeSemaphore.Dispose();
            FlushSemaphore.Dispose();
            Cts.Dispose();
        }
    }
}
