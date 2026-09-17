using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Text;

namespace NetRatel.Client.Service.Logging;

/// <summary>
/// The non-blocking observation point for the client runtime log stream. It is
/// intentionally independent of network transports: logging must never wait
/// for a gateway or browser consumer.
/// </summary>
public static class ClientRuntimeLogBuffer
{
    private const int MaximumRecords = 2_000;
    private const int MaximumBytes = 4 * 1024 * 1024;
    private const int MaximumRecordBytes = 8 * 1024;
    private static readonly object Gate = new();
    private static readonly Queue<ClientRuntimeLogRecord> Records = new(MaximumRecords);
    private static long _nextSequence;
    private static int _retainedBytes;
    private static ulong _droppedRecords;

    public static event Action<ClientRuntimeLogRecord>? RecordCaptured;

    public static ClientRuntimeLogRecord Capture(string message, DateTimeOffset timestampUtc)
    {
        var (prefix, remainder) = SplitPrefix(message);
        var (normalizedMessage, truncated) = Truncate(remainder);
        var sequence = NextGatewaySequence();
        var record = new ClientRuntimeLogRecord(
            Cursor: sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Sequence: sequence,
            TimestampUtc: timestampUtc,
            Severity: InferSeverity(prefix, normalizedMessage),
            SourceId: "netratel-runtime",
            Category: prefix ?? "Other",
            Prefix: prefix,
            Provider: "NetRatel.Client",
            Message: normalizedMessage,
            Truncated: truncated);
        var bytes = EstimateBytes(record);

        lock (Gate)
        {
            while (Records.Count > 0 && (Records.Count >= MaximumRecords || _retainedBytes + bytes > MaximumBytes))
            {
                _retainedBytes -= EstimateBytes(Records.Dequeue());
                _droppedRecords++;
            }

            // A record is already truncated to a bounded size, so it always
            // fits the byte budget once the oldest entry has been removed.
            Records.Enqueue(record);
            _retainedBytes += bytes;
        }

        Notify(record);
        return record;
    }

    public static ClientRuntimeLogSnapshot Snapshot(ulong afterSequence = 0)
    {
        lock (Gate)
        {
            var records = Records.Where(record => record.Sequence > afterSequence).ToArray();
            return new ClientRuntimeLogSnapshot(records, _droppedRecords, records.LastOrDefault()?.Cursor);
        }
    }

    /// <summary>
    /// Allocates an identity for a non-runtime provider without colliding with
    /// the runtime tap. The per-agent gateway stream uses this common sequence
    /// space while each provider retains its own opaque source cursor.
    /// </summary>
    internal static ulong NextGatewaySequence() => checked((ulong)Interlocked.Increment(ref _nextSequence));

    private static void Notify(ClientRuntimeLogRecord record)
    {
        var subscribers = RecordCaptured;
        if (subscribers is null) return;

        foreach (Action<ClientRuntimeLogRecord> subscriber in subscribers.GetInvocationList())
        {
            try
            {
                subscriber(record);
            }
            catch (Exception exception)
            {
                // A diagnostic listener cannot be allowed to break logging.
                System.Diagnostics.Trace.TraceWarning("A client runtime log observer failed: {0}", exception.GetType().Name);
            }
        }
    }

    private static (string? Prefix, string Message) SplitPrefix(string message)
    {
        var value = message?.Trim() ?? string.Empty;
        if (value.Length > 2 && value[0] == '[')
        {
            var closing = value.IndexOf(']');
            if (closing is > 1 and <= 96)
            {
                var prefix = value[1..closing].Trim();
                if (prefix.All(character => char.IsLetterOrDigit(character) || character is '-' or '_' or '.'))
                {
                    return (prefix, value[(closing + 1)..].TrimStart());
                }
            }
        }

        return (null, value);
    }

    private static (string Message, bool Truncated) Truncate(string message)
    {
        if (Encoding.UTF8.GetByteCount(message) <= MaximumRecordBytes) return (message, false);

        var builder = new StringBuilder(message.Length);
        var bytes = 0;
        foreach (var character in message)
        {
            var characterBytes = Encoding.UTF8.GetByteCount([character]);
            if (bytes + characterBytes > MaximumRecordBytes - 32) break;
            builder.Append(character);
            bytes += characterBytes;
        }

        return ($"{builder} … [truncated]", true);
    }

    private static int EstimateBytes(ClientRuntimeLogRecord record) =>
        Math.Min(MaximumRecordBytes, Encoding.UTF8.GetByteCount(record.Message)) + 192;

    private static string InferSeverity(string? prefix, string message)
    {
        var value = $"{prefix} {message}";
        if (value.Contains("fatal", StringComparison.OrdinalIgnoreCase)) return "Fatal";
        if (value.Contains("error", StringComparison.OrdinalIgnoreCase) || value.Contains("failed", StringComparison.OrdinalIgnoreCase)) return "Error";
        if (value.Contains("warn", StringComparison.OrdinalIgnoreCase)) return "Warning";
        if (value.Contains("debug", StringComparison.OrdinalIgnoreCase)) return "Debug";
        return "Information";
    }
}

public sealed record ClientRuntimeLogRecord(
    string Cursor,
    ulong Sequence,
    DateTimeOffset TimestampUtc,
    string Severity,
    string SourceId,
    string Category,
    string? Prefix,
    string Provider,
    string Message,
    bool Truncated,
    long? EventId = null,
    long? RecordId = null,
    string? Machine = null);

public sealed record ClientRuntimeLogSnapshot(
    IReadOnlyList<ClientRuntimeLogRecord> Records,
    ulong DroppedRecordCount,
    string? NextCursor);
