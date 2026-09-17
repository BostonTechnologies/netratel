using System.Globalization;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Diagnostics;
using MudBlazor;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Scripts;
using NetRatel.Shared.Contracts.Tasks;

namespace NetRatel.Web.Services.Search;

public sealed class GlobalSearchService : IGlobalSearchService
{
    private const int PerGroupLimit = 5;
    private const int FetchLimit = PerGroupLimit + 1;
    private static readonly TimeSpan SourceTimeout = TimeSpan.FromMilliseconds(1200);

    private static readonly SectionDefinition[] Sections =
    [
        new("tenants", "Tenants", Icons.Material.Filled.Business, "/tenants"),
        new("clients", "Clients", Icons.Material.Filled.Devices, "/clients"),
        new("scripts", "Scripts", Icons.Material.Filled.Terminal, "/scriptlibrary"),
        new("jobs", "Jobs", Icons.Material.Filled.Work, "/jobs"),
        new("requests", "Requests", Icons.Material.Filled.Hub, "/requests"),
        new("tasks", "Tasks", Icons.Material.Filled.TaskAlt, "/tasks/history")
    ];

    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<GlobalSearchService> _logger;

    public GlobalSearchService(IHttpClientFactory clientFactory, ILogger<GlobalSearchService> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<GlobalSearchGroup>> SearchAsync(string? query, CancellationToken cancellationToken = default)
    {
        var groups = Sections
            .Select(s => new GlobalSearchGroup(s.Key, s.Title, s.Icon, s.Href, []))
            .ToDictionary(x => x.Key, StringComparer.Ordinal);

        await foreach (var group in SearchIncrementalAsync(query, cancellationToken))
        {
            groups[group.Key] = group;
        }

        return Sections.Select(s => groups[s.Key]).ToList();
    }

    public async IAsyncEnumerable<GlobalSearchGroup> SearchIncrementalAsync(
        string? query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var term = query?.Trim();
        if (string.IsNullOrWhiteSpace(term))
        {
            foreach (var section in Sections)
            {
                yield return EmptyGroup(section);
            }

            yield break;
        }

        var overall = Stopwatch.StartNew();
        var queryHash = QueryHash(term);
        _logger.LogInformation(
            "Global search started. QueryLength={QueryLength} QueryHash={QueryHash}",
            term.Length,
            queryHash);

        var http = _clientFactory.CreateClient("OrchestratorApi");
        using var searchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        foreach (var section in Sections)
        {
            yield return LoadingGroup(section, term);
        }

        var tasks = Sections
            .Select(section => SearchSectionWithTimeoutAsync(http, section, term, queryHash, searchCts.Token))
            .ToList();

        try
        {
            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks).WaitAsync(cancellationToken);
                tasks.Remove(completed);
                yield return await completed;
            }
        }
        finally
        {
            overall.Stop();
            await searchCts.CancelAsync();
            _logger.LogInformation(
                "Global search completed. QueryLength={QueryLength} QueryHash={QueryHash} ElapsedMs={ElapsedMs}",
                term.Length,
                queryHash,
                overall.ElapsedMilliseconds);
        }
    }

    private async Task<GlobalSearchGroup> SearchSectionWithTimeoutAsync(
        HttpClient http,
        SectionDefinition section,
        string term,
        string queryHash,
        CancellationToken cancellationToken)
    {
        var source = Stopwatch.StartNew();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SourceTimeout);

        try
        {
            var group = await SearchSectionAsync(http, section.Key, term, timeoutCts.Token);
            source.Stop();
            _logger.LogInformation(
                "Global search source completed. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} ResultCount={ResultCount} Continue={Continue} TimedOut={TimedOut} ElapsedMs={ElapsedMs}",
                section.Key,
                term.Length,
                queryHash,
                group.Results.Count,
                group.ContinueHref is not null,
                false,
                source.ElapsedMilliseconds);
            return group;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutCts.IsCancellationRequested)
        {
            source.Stop();
            _logger.LogWarning(
                "Global search source timed out. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} ResultCount={ResultCount} Continue={Continue} TimedOut={TimedOut} ElapsedMs={ElapsedMs}",
                section.Key,
                term.Length,
                queryHash,
                0,
                true,
                true,
                source.ElapsedMilliseconds);
            return TimeoutGroup(section, term);
        }
        catch (Exception ex)
        {
            source.Stop();
            _logger.LogWarning(
                ex,
                "Global search source completed. Section={Section} QueryLength={QueryLength} QueryHash={QueryHash} ResultCount={ResultCount} Continue={Continue} TimedOut={TimedOut} ElapsedMs={ElapsedMs}",
                section.Key,
                term.Length,
                queryHash,
                0,
                false,
                false,
                source.ElapsedMilliseconds);
            return BuildGroup(section.Key, term, [], totalCount: 0);
        }
    }

    private async Task<GlobalSearchGroup> SearchSectionAsync(HttpClient http, string key, string term, CancellationToken cancellationToken)
    {
        return key switch
        {
            "tenants" => BuildGroup(key, term, await ReadPageAsync<TenantDto>(http, BuildUrl(key, term), cancellationToken), MatchTenants),
            "clients" => BuildGroup(key, term, await ReadPageAsync<GlobalSearchAgentDto>(http, BuildUrl(key, term), cancellationToken), MatchClients),
            "scripts" => BuildGroup(key, term, await ReadPageAsync<ScriptDto>(http, BuildUrl(key, term), cancellationToken), MatchScripts),
            "jobs" => BuildGroup(key, term, await ReadPageAsync<JobDto>(http, BuildUrl(key, term), cancellationToken), MatchJobs),
            "requests" => BuildGroup(key, term, await ReadPageAsync<GlobalSearchRequestDto>(http, BuildUrl(key, term), cancellationToken), MatchRequests),
            "tasks" => BuildGroup(key, term, await ReadPageAsync<TaskDto>(http, BuildUrl(key, term), cancellationToken), MatchTasks),
            _ => BuildGroup(key, term, [], totalCount: 0)
        };
    }

    private async Task<PagedResult<T>> ReadPageAsync<T>(HttpClient http, string url, CancellationToken cancellationToken)
    {
        try
        {
            return await http.GetFromJsonAsync<PagedResult<T>>(url, cancellationToken)
                ?? new PagedResult<T>([], 1, FetchLimit, 0);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Global search source {Url} failed.", url);
            return new PagedResult<T>([], 1, FetchLimit, 0);
        }
    }

    private static GlobalSearchGroup BuildGroup<T>(string key, string term, PagedResult<T> page, Func<IReadOnlyList<T>, string, IEnumerable<ScoredResult>> matcher) =>
        BuildGroup(key, term, matcher(page.Items, term), page.TotalCount);

    private static GlobalSearchGroup BuildGroup(string key, string term, IEnumerable<ScoredResult> results, int totalCount)
    {
        var section = Sections.First(s => s.Key == key);
        var ordered = results
            .OrderByDescending(r => r.Score)
            .ThenBy(r => r.Result.Title, StringComparer.OrdinalIgnoreCase)
            .Take(PerGroupLimit + 1)
            .Select(r => r.Result)
            .ToList();
        var hasMore = totalCount > PerGroupLimit || ordered.Count > PerGroupLimit;

        return new GlobalSearchGroup(
            section.Key,
            section.Title,
            section.Icon,
            section.Href,
            ordered.Take(PerGroupLimit).ToList(),
            hasMore ? ContinueHref(section, term) : null,
            totalCount);
    }

    private static GlobalSearchGroup EmptyGroup(SectionDefinition section) =>
        new(section.Key, section.Title, section.Icon, section.Href, []);

    private static GlobalSearchGroup LoadingGroup(SectionDefinition section, string term) =>
        new(section.Key, section.Title, section.Icon, section.Href, [], ContinueHref(section, term), null, IsLoading: true);

    private static GlobalSearchGroup TimeoutGroup(SectionDefinition section, string term) =>
        new(section.Key, section.Title, section.Icon, section.Href, [], ContinueHref(section, term), null, IsLoading: false, TimedOut: true);

    private static string BuildUrl(string key, string term) =>
        $"/api/v1/global-search/{key}?q={Escape(term)}&pageSize={FetchLimit}";

    private static string ContinueHref(SectionDefinition section, string term)
    {
        var separator = section.Href.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return $"{section.Href}{separator}search={Escape(term)}";
    }

    private static IEnumerable<ScoredResult> MatchTenants(IEnumerable<TenantDto> tenants, string term)
    {
        foreach (var tenant in tenants)
        {
            var fields = new[]
            {
                tenant.Name,
                tenant.Description,
                tenant.Location,
                tenant.ContactPerson,
                tenant.ContactEmail,
                tenant.TenantId.ToString(CultureInfo.InvariantCulture)
            }.Concat(tenant.Domains ?? []);

            var score = Score(term, tenant.Name, fields);
            if (score <= 0)
            {
                continue;
            }

            yield return new ScoredResult(score, new GlobalSearchResult(
                "Tenants",
                tenant.Name,
                tenant.Location,
                tenant.ContactPerson ?? tenant.ContactEmail,
                $"/tenants?search={Escape(tenant.Name)}",
                Icons.Material.Filled.Business));
        }
    }

    private static IEnumerable<ScoredResult> MatchClients(IEnumerable<GlobalSearchAgentDto> clients, string term)
    {
        foreach (var client in clients)
        {
            var title = FirstNonEmpty(client.DisplayName, client.HostName) ?? "Unnamed Agent";
            var fields = new[]
            {
                client.DisplayName,
                client.HostName,
                client.TenantName,
                client.OperatingSystem,
                client.AgentVersion,
                client.AgentId.ToString()
            };

            var score = Score(term, title, fields);
            if (score <= 0)
            {
                continue;
            }

            yield return new ScoredResult(score, new GlobalSearchResult(
                "Clients",
                title,
                client.TenantName,
                FirstNonEmpty(client.HostName, client.OperatingSystem),
                $"/clients?search={Escape(FirstNonEmpty(client.DisplayName, client.HostName) ?? title)}",
                Icons.Material.Filled.Devices));
        }
    }

    private static IEnumerable<ScoredResult> MatchScripts(IEnumerable<ScriptDto> scripts, string term)
    {
        foreach (var script in scripts)
        {
            var fields = new[] { script.Name, script.FolderPath, script.Description, script.ScriptType };
            var score = Score(term, script.Name, fields);
            if (score <= 0)
            {
                continue;
            }

            yield return new ScoredResult(score, new GlobalSearchResult(
                "Scripts",
                script.Name,
                NormalizeFolder(script.FolderPath),
                script.ScriptType,
                $"/scriptlibrary?search={Escape(script.Name)}",
                Icons.Material.Filled.Terminal));
        }
    }

    private static IEnumerable<ScoredResult> MatchJobs(IEnumerable<JobDto> jobs, string term)
    {
        foreach (var job in jobs)
        {
            var fields = new[]
            {
                job.Name,
                job.FolderPath,
                job.Description,
                job.TenantDisplayName,
                job.ClientDisplayName,
                job.AgentId?.ToString(),
                job.ClientIdentity
            };
            var score = Score(term, job.Name, fields);
            if (score <= 0)
            {
                continue;
            }

            yield return new ScoredResult(score, new GlobalSearchResult(
                "Jobs",
                job.Name,
                NormalizeFolder(job.FolderPath),
                FirstNonEmpty(job.TenantDisplayName, job.ClientDisplayName),
                $"/jobs?search={Escape(job.Name)}",
                Icons.Material.Filled.Work));
        }
    }

    private static IEnumerable<ScoredResult> MatchRequests(IEnumerable<GlobalSearchRequestDto> requests, string term)
    {
        foreach (var request in requests)
        {
            var title = $"Request #{request.Id}";
            var summary = FirstNonEmpty(request.JobDefinitionId, request.ResultMessage, request.AgentDisplayName);
            var fields = new[]
            {
                request.Id.ToString(CultureInfo.InvariantCulture),
                request.SourceSystem,
                request.JobDefinitionId,
                request.ExecutionId,
                request.Status,
                request.ResultMessage,
                request.TenantName,
                request.AgentDisplayName,
                request.AgentHostName,
                request.AgentId?.ToString(),
                request.LegacyTargetIdentity
            };

            var score = Score(term, title, fields);
            if (score <= 0)
            {
                continue;
            }

            var searchValue = request.Id.ToString(CultureInfo.InvariantCulture);
            yield return new ScoredResult(score, new GlobalSearchResult(
                "Requests",
                title,
                summary,
                request.Status,
                $"/requests?search={Escape(searchValue)}",
                Icons.Material.Filled.Hub));
        }
    }

    private static IEnumerable<ScoredResult> MatchTasks(IEnumerable<TaskDto> tasks, string term)
    {
        foreach (var task in tasks)
        {
            var fields = new[]
            {
                task.RequestId,
                task.TaskType,
                task.Status,
                task.ClientIdentity,
                task.ClientDisplayName,
                task.ClientHostName,
                task.ClientName,
                task.StatusMessage,
                task.AgentId?.ToString()
            };
            var score = Score(term, task.TaskType, fields);
            if (score <= 0)
            {
                continue;
            }

            yield return new ScoredResult(score, new GlobalSearchResult(
                "Tasks",
                task.TaskType,
                task.RequestId,
                FirstNonEmpty(task.Status, task.ClientDisplayName, task.ClientName),
                $"/tasks/request/{Escape(task.RequestId)}",
                Icons.Material.Filled.TaskAlt));
        }
    }

    private static int Score(string term, string? title, IEnumerable<string?> fields)
    {
        var score = 0;
        if (StartsWith(title, term))
        {
            score += 100;
        }
        else if (Contains(title, term))
        {
            score += 70;
        }

        foreach (var field in fields)
        {
            if (StartsWith(field, term))
            {
                score += 30;
            }
            else if (Contains(field, term))
            {
                score += 15;
            }
        }

        return score;
    }

    private static bool Contains(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static bool StartsWith(string? value, string term) =>
        !string.IsNullOrWhiteSpace(value) && value.StartsWith(term, StringComparison.OrdinalIgnoreCase);

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

    private static string NormalizeFolder(string? folder) =>
        string.IsNullOrWhiteSpace(folder) ? "Root" : folder.Trim('/');

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private static string QueryHash(string term)
    {
        var bytes = SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(term.ToUpperInvariant()));
        return Convert.ToHexString(bytes.AsSpan(0, 6));
    }

    private sealed record SectionDefinition(string Key, string Title, string Icon, string Href);

    private sealed record ScoredResult(int Score, GlobalSearchResult Result);
}
