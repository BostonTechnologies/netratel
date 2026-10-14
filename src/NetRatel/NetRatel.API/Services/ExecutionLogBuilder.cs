using System;
using System.Collections.Generic;
using System.Linq;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Utils;
using NetRatel.Application.Jobs;
using System.Text;
using NetRatel.Shared.Security;

namespace NetRatel.API.Services;

internal static class ExecutionLogBuilder
{
    public static ExecutionLogDto BuildExecutionLog(JobTaskActivityInfo? activity, IReadOnlyList<JobTaskLogInfo> logs)
    {
        var bounded = new List<TaskLogDto>();
        var remaining = 64 * 1024;
        var truncated = false;
        foreach (var row in logs.OrderByDescending(x => x.Sequence).ThenByDescending(x => x.Id))
        {
            if (bounded.Count >= 500 || remaining == 0) { truncated = true; break; }
            var message = OperatorOutputRedactor.Redact(row.Message);
            var length = Math.Min(message.Length, remaining);
            while (length > 0 && Encoding.UTF8.GetByteCount(message.AsSpan(0, length)) > remaining) length--;
            truncated |= length != message.Length;
            message = message[..length];
            remaining -= Encoding.UTF8.GetByteCount(message);
            bounded.Add(MapLog(activity?.RequestId, row) with { Message = message });
        }
        return new ExecutionLogDto(
            activity?.RequestId,
            Plain: null,
            Html: null,
            activity?.ExitCode,
            bounded.OrderBy(x => x.Seq).ThenBy(x => x.Id).ToList(), truncated);
    }

    private static TaskLogDto MapLog(string? requestId, JobTaskLogInfo log)
        => new(
            log.Id,
            requestId ?? log.RequestId,
            log.ClientIdentity,
            log.TimestampUtc,
            log.Stream,
            log.Message,
            log.Sequence);
}
