using System;
using System.Collections.Generic;
using System.Linq;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Utils;
using NetRatel.Application.Jobs;

namespace NetRatel.API.Services;

internal static class ExecutionLogBuilder
{
    public static ExecutionLogDto BuildExecutionLog(JobTaskActivityInfo? activity, IReadOnlyList<JobTaskLogInfo> logs)
    {
        var plain = logs.Count == 0
            ? (activity is null
                ? string.Empty
                : string.IsNullOrWhiteSpace(activity.Error) ? activity.Status : AnsiConsole.StripAnsi(activity.Error))
            : string.Join(Environment.NewLine, logs.Select(l => AnsiConsole.StripAnsi(l.Message)));

        return new ExecutionLogDto(
            activity?.RequestId,
            plain,
            Html: null,
            0,
            logs.Select(log => MapLog(activity?.RequestId, log)).ToList());
    }

    private static TaskLogDto MapLog(string? requestId, JobTaskLogInfo log)
        => new(
            log.Id > int.MaxValue ? int.MaxValue : (int)log.Id,
            requestId ?? string.Empty,
            log.ClientIdentity,
            log.TimestampUtc,
            log.Stream,
            log.Message,
            log.Sequence);
}
