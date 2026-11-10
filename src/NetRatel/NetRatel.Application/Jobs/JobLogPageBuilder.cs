using System.Globalization;
using System.Text;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Security;

namespace NetRatel.Application.Jobs;

/// <summary>Forward keyset pages, shared by job REST and both tooling transports. Tail previews remain separate.</summary>
public static class JobLogPageBuilder
{
    public static (long Sequence, long Id) ParseCursor(string? cursor)
    {
        if (cursor is null) return (-1, 0);
        var parts = cursor.Split(':');
        if (cursor.Length > 64 || parts.Length != 2 ||
            !long.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence) || sequence < 0 ||
            !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) || id <= 0)
            throw new JobTargetValidationException("node_log_cursor_invalid", "Use the exact forward log cursor returned by the previous page.");
        return (sequence, id);
    }

    public static void ValidateBounds(int limit, int maximumBytes)
    {
        if (limit is < 1 or > 1000 || maximumBytes is < 1 or > 65536)
            throw new JobTargetValidationException("node_log_page_invalid", "Log pages allow 1–1000 rows and 1–65536 UTF-8 text bytes.");
    }

    public static ExecutionLogDto Build(string requestId, IReadOnlyList<JobTaskLogInfo> candidates,
        string? cursor, int limit, int maximumBytes)
    {
        ValidateBounds(limit, maximumBytes);
        var after = ParseCursor(cursor);
        var rows = candidates.Where(row => row.RequestId == requestId &&
                (row.Sequence > after.Sequence || row.Sequence == after.Sequence && row.Id > after.Id))
            .OrderBy(row => row.Sequence).ThenBy(row => row.Id).ToArray();
        var logs = new List<TaskLogDto>();
        var truncated = new List<long>();
        var remaining = maximumBytes;
        foreach (var row in rows)
        {
            if (logs.Count >= limit || remaining == 0) break;
            var message = OperatorOutputRedactor.Redact(row.Message);
            var bytes = Encoding.UTF8.GetByteCount(message);
            // Do not consume a partially fitting later row. The next cursor resumes exactly there.
            if (bytes > remaining && logs.Count > 0) break;
            if (bytes > remaining)
            {
                message = Utf8Prefix(message, remaining);
                truncated.Add(row.Id); // Oversized first row is explicit and retains full task-output navigation.
            }
            remaining -= Encoding.UTF8.GetByteCount(message);
            logs.Add(new(row.Id, requestId, row.ClientIdentity, row.TimestampUtc, row.Stream, message, row.Sequence));
        }
        var hasMore = rows.Length > logs.Count;
        return new(requestId, null, null, null, logs, hasMore || truncated.Count > 0)
        {
            NextCursor = logs.Count == 0 ? cursor : FormattableString.Invariant($"{logs[^1].Seq}:{logs[^1].Id}"),
            HasMore = hasMore,
            TruncatedLogIds = truncated.Count == 0 ? null : truncated,
            FullOutputUrl = truncated.Count == 0 ? null : $"/tasks/request/{Uri.EscapeDataString(requestId)}"
        };
    }

    public static string Utf8Prefix(string value, int maximumBytes)
    {
        var length = Math.Min(value.Length, maximumBytes);
        while (length > 0 && Encoding.UTF8.GetByteCount(value.AsSpan(0, length)) > maximumBytes) length--;
        if (length > 0 && char.IsHighSurrogate(value[length - 1])) length--;
        return value[..length];
    }
}
