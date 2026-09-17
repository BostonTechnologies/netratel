using System.Text.RegularExpressions;
using NetRatel.Application.Jobs;
using NetRatel.Application.Requests;

namespace NetRatel.API.Services.Requests;

public static partial class RequestJobRunLinkResolver
{
    public static JobRunInfo? Resolve(RequestInfo request, IReadOnlyDictionary<ulong, JobRunInfo> runsById)
    {
        foreach (var runId in EnumerateCandidateRunIds(request))
        {
            if (runsById.TryGetValue(runId, out var run))
            {
                return run;
            }
        }

        return null;
    }

    public static IReadOnlyList<ulong> EnumerateCandidateRunIds(RequestInfo request)
    {
        var candidates = new List<ulong>();

        AddIfValid(request.ExecutionId, candidates);
        AddLogMatches(request.Logs, candidates);
        AddTextMatches(request.ResultMessage, candidates);
        AddTextMatches(request.ResultData, candidates);

        return candidates;
    }

    private static void AddIfValid(string? value, List<ulong> candidates)
    {
        if (ulong.TryParse(value, out var runId))
        {
            AddDistinct(runId, candidates);
        }
    }

    private static void AddLogMatches(IReadOnlyList<string> logs, List<ulong> candidates)
    {
        for (var i = logs.Count - 1; i >= 0; i--)
        {
            AddTextMatches(logs[i], candidates);
        }
    }

    private static void AddTextMatches(string? value, List<ulong> candidates)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        foreach (Match match in JobRunRegex().Matches(value))
        {
            if (ulong.TryParse(match.Groups["id"].Value, out var runId))
            {
                AddDistinct(runId, candidates);
            }
        }
    }

    private static void AddDistinct(ulong runId, List<ulong> candidates)
    {
        if (runId > 0 && !candidates.Contains(runId))
        {
            candidates.Add(runId);
        }
    }

    [GeneratedRegex(@"\bJobRun\s+(?<id>\d+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JobRunRegex();
}
