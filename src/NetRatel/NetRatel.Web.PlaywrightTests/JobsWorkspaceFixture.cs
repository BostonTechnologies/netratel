using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Application.Jobs;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Jobs;
using NetRatel.Shared.Contracts.Requests;
using NetRatel.Web.Services.Jobs;
using NetRatel.Web.Services.Tenants;

namespace NetRatel.Web.PlaywrightTests;

/// <summary>Disposable in-memory Jobs API for the optional operator workspace browser review.</summary>
internal static class JobsWorkspaceFixture
{
    public static void Configure(IServiceCollection services, int parameterCount = 1, bool mixed = false, JobsRunFixtureStream? stream = null,
        IReadOnlyList<JobParamDto>? parameters = null, Action<RunJobRequest>? runAccepted = null, bool multiNode = false,
        Action<ExecutionGroupWriteDto>? groupSaved = null)
    {
        services.AddSingleton<IHttpClientFactory>(provider => new JobsTransport(parameterCount, mixed, parameters, runAccepted,
            multiNode, provider.GetRequiredService<ClientDirectoryFixtureData>(), groupSaved));
        services.AddSingleton<IJobApiClient>(provider => new JobApiClient(provider.GetRequiredService<IHttpClientFactory>()));
        services.AddSingleton<ITenantApiService, JobsTenants>();
        services.AddSingleton<IJobRunStreamService>(stream ?? new JobsRunFixtureStream());
    }

    private sealed class JobsTenants : ITenantApiService
    {
        public Task<IReadOnlyList<TenantDto>> GetTenantsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TenantDto>>([
                new(3, "Visual fixture", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow),
                new(7, "Release fixture", null, null, [], null, null, true, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)]);
        public Task CreateTenantAsync(CreateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task UpdateTenantAsync(int tenantId, UpdateTenantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteTenantAsync(int tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class JobsTransport(int parameterCount, bool mixed, IReadOnlyList<JobParamDto>? parameters, Action<RunJobRequest>? runAccepted,
        bool multiNode, ClientDirectoryFixtureData directory, Action<ExecutionGroupWriteDto>? groupSaved) : HttpMessageHandler, IHttpClientFactory
    {
        private JobDto _job = new(42, "Nightly backup", "/Visual fixture/Backups/", "Back up the development database", 3, "gateway-agent-01", 0, 0,
            "Visual fixture", "gateway-agent-01", ClientEnvironment.Dev,
            Schedule: new(true, "Africa/Johannesburg", "08:00", ["Monday", "Wednesday", "Friday"]),
            AgentId: Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46"));
        private List<JobStepDto> _steps = [new(1, 42, 1, JobStepTypeDto.RunCommand, "bash", "echo fixture backup", null, null, true)];
        private List<JobParamDto> _parameters = Enumerable.Range(1, parameterCount).Select(index =>
            new JobParamDto((ulong)index, 42, $"Input {index}", "string", true, $"value {index}", $"Short help for input {index}", null)).ToList();
        private JobRunDto _run = JobsRunFixtureStream.Run;
        private IReadOnlyList<JobStepRunDto> _runSteps = JobsRunFixtureStream.Steps(1);
        private static readonly Guid GroupId = Guid.Parse("c186e933-9b40-4d99-9d12-dbb944b59001");
        private readonly IReadOnlyList<JobNodeInventoryDto> _nodes = Enumerable.Range(1, 30).Select(index =>
            new JobNodeInventoryDto(Guid.Parse($"4886c6c0-e486-4f59-a006-{index:000000000000}"), index <= 5 ? $"lab-node-{index:00}" : $"spare-node-{index:00}", $"Synthetic node {index}", null,
                index <= 5 ? new[] { "TestSystem", "Linux" } : new[] { "Auxiliary" }, "Linux", "x64", ClientEnvironment.Dev, index != 5, true, null)).ToArray();
        private IReadOnlyList<Guid> _members = Enumerable.Range(1, 5).Select(index => Guid.Parse($"4886c6c0-e486-4f59-a006-{index:000000000000}")).ToArray();
        private long _groupRevision = 1;
        private string _groupName = "Five-node group";
        private bool _initialized;
        private void Initialize()
        {
            if (_initialized) return;
            _initialized = true;
            if (mixed) _parameters = [
                new(1, 42, "Title with a longer descriptive field label", "string", true, "Fixture", "A longer description that wraps naturally without pushing the label or inputs outside the dialog.", null),
                new(2, 42, "Count", "int", true, "0", "Whole number", null),
                new(3, 42, "Enabled", "bool", true, "false", "False remains a supplied value", null),
                new(4, 42, "Settings", "json", true, "{\"enabled\":false}", "JSON is sent using the existing text representation", null),
                new(5, 42, "Mode", "select", true, "safe", "Choose a value", "[\"safe\",\"preview\"]"),
                new(6, 42, "Token", "secret", false, null, "Masked secret entry", null)];
            if (parameters is not null) _parameters = parameters.ToList();
            if (multiNode)
            {
                _job = _job with { AgentId = null, ClientIdentity = "", ClientDisplayName = null,
                    Target = new() { Source = JobTargetSource.ExecutionGroup, ExecutionGroupId = GroupId }, ExecutionPolicy = new(2) };
                _run = _run with { AgentId = null, ClientIdentity = "", ClientDisplayName = null, Status = JobRunStatusDto.Succeeded,
                    CompletedAtMicros = 9000000, IsAggregate = true, TargetSource = JobTargetSource.ExecutionGroup, TargetCount = 5,
                    EffectiveConcurrency = 2, ExecutionPolicy = new(2), NodesTotalCount = 5,
                    Nodes = _nodes.Take(5).Select((node, index) => new JobRunNodeDto(9, node.AgentId, index + 1, (ulong)(101 + index),
                        JobRunNodeState.Succeeded, node.Hostname, node.DisplayName, null, 1000000, 9000000)).ToArray(), NodeCounts = new(0, 0, 5, 0, 0, 0, 0, 0) };
            }
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://jobs-fixture.invalid") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Initialize();
            var path = request.RequestUri!.AbsolutePath;
            if (path.Contains("/job-targets/", StringComparison.Ordinal) || path.Contains("/execution-groups", StringComparison.Ordinal))
                return await TargetsAsync(request, cancellationToken);
            if (path == "/api/v1/jobruns/preview/42")
            {
                var input = (await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken))!;
                var plan = Plan(_job.Target!, input.RuntimeTarget);
                var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
                var page = query.TryGetValue("page", out var pageText) && int.TryParse(pageText, out var parsedPage) ? parsedPage : 1;
                if (input.ExpectedTargetPlanDigest is { } expected && expected != plan.ComputeDigest()) return new(HttpStatusCode.Conflict);
                return Ok(new JobLaunchPreviewDto(plan.ComputeDigest(), plan.DefinitionDigest, plan.Nodes.Count, plan.Target, plan.ExecutionPolicy,
                    Math.Min(plan.Nodes.Count, plan.ExecutionPolicy.ThreadCount), plan.GroupRevision, plan.Bindings, plan.Nodes.Skip((page - 1) * 25).Take(25).ToArray(), page, 25, page * 25 < plan.Nodes.Count ? page + 1 : null));
            }
            if (path.Contains("/nodes/", StringComparison.Ordinal) && path.StartsWith("/api/v1/jobruns/9/", StringComparison.Ordinal))
            {
                var segments = path.Split('/'); var agent = Guid.Parse(segments[6]); var index = _nodes.ToList().FindIndex(node => node.AgentId == agent);
                var native = (ulong)(101 + index);
                if (path.EndsWith("/steps", StringComparison.Ordinal)) return Ok(Enumerable.Range(1, 2).Select(ordinal =>
                    new JobStepRunDto(native * 10 + (ulong)ordinal, native, (ulong)ordinal, JobStepRunStatusDto.Succeeded, ordinal,
                        $"fixture-node-{index}-step-{ordinal}", 1000000, 9000000, null, ScriptName: $"Ordered step {ordinal}", ScriptType: "Bash", ExitCode: 0)).ToArray());
                if (path.EndsWith("/logs", StringComparison.Ordinal)) return Ok(new ExecutionLogDto("fixture-node-output", $"{_nodes[index].Hostname}: isolated output for step run {segments[8]}", null, 0, []));
            }
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/jobruns/9") return Ok(_run);
                if (path == "/api/v1/jobruns/9/steps") return Ok(_runSteps);
                if (path.StartsWith("/api/v1/jobruns/9/steps/") && path.EndsWith("/logs"))
                    return Ok(new ExecutionLogDto("fixture-task", null, null, null, []));
                if (path == "/api/v1/jobs") return Ok(new[] { _job });
                if (path == "/api/v1/jobruns/query") return Ok(new PagedJobRunsDto([], 0));
                if (path.EndsWith("/params")) return Ok(_parameters);
                if (path.EndsWith("/steps")) return Ok(_steps);
                if (path.EndsWith("/details")) return Ok(new JobWithDetailsDto(_job, _steps, _parameters));
                if (path == "/api/v1/jobs/42") return Ok(_job);
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/jobruns/start/42")
            {
                var body = (await request.Content!.ReadFromJsonAsync<RunJobRequest>(cancellationToken))!;
                runAccepted?.Invoke(body);
                _run = _run with { StartedBy = body.StartedBy, InputsJson = body.InputsJson, Environment = body.Environment };
                return Ok(_run);
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/jobs/42/params")
            {
                var body = (await request.Content!.ReadFromJsonAsync<AddJobParamRequest>(cancellationToken))!;
                var saved = new JobParamDto((ulong)(_parameters.Count + 1), 42, body.Name, body.Type, body.Required, body.Default, body.Description, body.OptionsJson);
                _parameters.Add(saved); return Ok(saved);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/jobs/params/"))
            {
                var body = (await request.Content!.ReadFromJsonAsync<UpdateJobParamRequest>(cancellationToken))!;
                var id = ulong.Parse(path.Split('/')[5]);
                var index = _parameters.FindIndex(parameter => parameter.Id == id);
                var saved = _parameters[index] with { Name = body.Name ?? _parameters[index].Name, Type = body.Type ?? _parameters[index].Type,
                    Default = body.Default ?? _parameters[index].Default, Description = body.Description ?? _parameters[index].Description,
                    Required = body.Required ?? _parameters[index].Required, OptionsJson = body.OptionsJson ?? _parameters[index].OptionsJson };
                _parameters[index] = saved; return Ok(saved);
            }
            if (request.Method == HttpMethod.Post && path == "/api/v1/jobs")
            {
                var body = (await request.Content!.ReadFromJsonAsync<CreateJobRequest>(cancellationToken))!;
                _job = new(43, body.Name, body.FolderPath, body.Description, body.TenantId, "gateway-agent-01", 0, 0,
                    "Visual fixture", "gateway-agent-01", ClientEnvironment.Dev,
                    body.ExpectedRuntimeSeconds ?? 1800, body.GraceSeconds ?? 0, body.HardTimeoutSeconds ?? 1800, body.Schedule, body.AgentId)
                    { Target = body.Target, ExecutionPolicy = body.ExecutionPolicy };
                _steps = []; _parameters = [];
                return Ok(_job);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/jobs/"))
            {
                var body = (await request.Content!.ReadFromJsonAsync<UpdateJobRequest>(cancellationToken))!;
                _job = _job with { Name = body.Name ?? _job.Name, FolderPath = body.FolderPath ?? _job.FolderPath, Schedule = body.Schedule, AgentId = body.AgentId, Target = body.Target ?? _job.Target, ExecutionPolicy = body.ExecutionPolicy ?? _job.ExecutionPolicy };
                return Ok(_job);
            }
            return directory.Respond(request);
        }
        private ExecutionGroupDetailsDto Group() => new(new(GroupId, 3, _groupName, "Synthetic five-node group", _groupRevision, true, false, _members.Count,
            _nodes.Count(node => _members.Contains(node.AgentId) && node.IsOnline), _nodes.Count(node => _members.Contains(node.AgentId) && !node.IsOnline), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch),
            _nodes.Where(node => _members.Contains(node.AgentId)).ToArray());
        private JobResolvedTargetPlan Plan(JobTargetDefinition target, JobRuntimeTargetInput? runtime)
        {
            var nodes = target.Source switch
            {
                JobTargetSource.ExecutionGroup => _nodes.Where(node => _members.Contains(node.AgentId)).ToArray(),
                JobTargetSource.ExplicitNodes => _nodes.Where(node => target.AgentIds!.Contains(node.AgentId)).ToArray(),
                JobTargetSource.NodeFilter => JobNodeFilterEvaluator.Resolve(JobNodeFilterParser.Bind(JobNodeFilterParser.Parse(target.Filter), null), _nodes),
                JobTargetSource.RuntimeInput => RuntimeNodes(target.RuntimeInput!, runtime),
                _ => throw new InvalidOperationException("Unsupported fixture target")
            };
            return new(1, 3, 42, new string('A', 64), target, target.Source == JobTargetSource.ExecutionGroup ? _groupRevision : null, [],
                nodes.Select(node => new JobTargetNode(node.AgentId, node.Hostname, node.DisplayName, node.Tags) { OperatingSystem = node.OperatingSystem, Environment = node.Environment, IsOnline = node.IsOnline }).ToArray(), _job.ExecutionPolicy ?? new());
        }
        private IReadOnlyList<JobNodeInventoryDto> RuntimeNodes(JobRuntimeTargetDefinition definition, JobRuntimeTargetInput? input)
        {
            var values = input?.Values ?? definition.DefaultValues ?? [];
            return _nodes.Where(node => definition.Kind switch
            {
                JobRuntimeTargetKind.ClientId or JobRuntimeTargetKind.ClientIds => values.Contains(node.AgentId.ToString()),
                JobRuntimeTargetKind.Hostname or JobRuntimeTargetKind.Hostnames => values.Contains(node.Hostname),
                JobRuntimeTargetKind.Tag or JobRuntimeTargetKind.Tags => definition.AllOf ? values.All(value => node.Tags.Contains(value)) : values.Any(value => node.Tags.Contains(value)),
                _ => false
            }).ToArray();
        }
        private async Task<HttpResponseMessage> TargetsAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(request.RequestUri.Query);
            var page = query.TryGetValue("page", out var pageValue) && int.TryParse(pageValue, out var parsed) ? parsed : 1;
            var search = query.TryGetValue("search", out var searchValue) ? searchValue.ToString() : "";
            if (path.EndsWith("/properties")) return Ok(JobNodeFilterParser.Properties);
            if (path.EndsWith("/nodes/identities"))
            { var ids = (await request.Content!.ReadFromJsonAsync<Guid[]>(ct))!; return Ok(_nodes.Where(node => ids.Contains(node.AgentId)).ToArray()); }
            if (path.EndsWith("/nodes"))
            {
                var nodes = _nodes.Where(node => JobNodeFilterEvaluator.MatchesSearch(node, search)).ToArray();
                return Ok(new JobNodePageDto(nodes.Skip((page - 1) * 25).Take(25).ToArray(), page, 25, nodes.Length, page * 25 < nodes.Length ? page + 1 : null));
            }
            if (path.EndsWith("/resolve-preview"))
            {
                var input = (await request.Content!.ReadFromJsonAsync<JobTargetPreviewRequest>(ct))!;
                var plan = Plan(input.Target, input.RuntimeTarget);
                var selected = plan.Nodes.Select(node => _nodes.Single(item => item.AgentId == node.AgentId)).ToArray();
                return Ok(new JobTargetPreviewDto(selected.Skip((input.Page - 1) * input.PageSize).Take(input.PageSize).ToArray(), input.Page, input.PageSize, selected.Length,
                    input.Page * input.PageSize < selected.Length ? input.Page + 1 : null, input.Target, plan.GroupRevision, input.Target.Filter, plan.ComputeDigest()));
            }
            if (request.Method == HttpMethod.Post || request.Method == HttpMethod.Put)
            {
                var input = (await request.Content!.ReadFromJsonAsync<ExecutionGroupWriteDto>(ct))!;
                _groupName = input.Name; _members = input.AgentIds; _groupRevision++; groupSaved?.Invoke(input);
                return Ok(Group());
            }
            if (path.EndsWith("/execution-groups")) return Ok(new ExecutionGroupPageDto([Group().Group], page, 25, 1, null));
            return Ok(Group());
        }
        private static HttpResponseMessage Ok<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    }
}


/// <summary>Controlled delayed snapshots exercise the real run page without an execution engine.</summary>
internal sealed class JobsRunFixtureStream : IJobRunStreamService
{
    private readonly Channel<JobRunLiveUpdate> _updates = Channel.CreateUnbounded<JobRunLiveUpdate>();
    internal static JobRunDto Run => new(9, 42, "Nightly backup", 3, "gateway-agent-01", "fixture", JobRunStatusDto.Running, 1,
        100, 100, null, null, "{}", TenantDisplayName: "Visual fixture", ClientDisplayName: "gateway-agent-01", Environment: ClientEnvironment.Dev);
    internal static IReadOnlyList<JobStepRunDto> Steps(int active, bool terminal = false) => Enumerable.Range(1, 3).Select(index =>
        new JobStepRunDto((ulong)(10 + index), 9, (ulong)index,
            index < active || terminal ? JobStepRunStatusDto.Succeeded : index == active ? JobStepRunStatusDto.Running : JobStepRunStatusDto.Pending,
            index, index <= active ? $"fixture-task-{index}" : null, index <= active ? index * 100 : null,
            index < active || terminal ? index * 100 + 90 : null, null, ScriptName: $"Synthetic step {index}", ScriptType: "Bash")).ToList();
    public async IAsyncEnumerable<JobRunLiveUpdate> SubscribeAsync(ulong runId,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in _updates.Reader.ReadAllAsync(cancellationToken)) yield return update;
    }
    public async Task PublishAsync(int active, bool terminal = false)
    {
        await Task.Delay(150);
        var log = new TaskLogDto(active, $"fixture-task-{active}", "fixture-agent", DateTimeOffset.UnixEpoch, "stdout", $"Delayed output for step {active}", active);
        await _updates.Writer.WriteAsync(new("Live", new(Run with { Status = terminal ? JobRunStatusDto.Succeeded : JobRunStatusDto.Running, CurrentStepOrdinal = active },
            Steps(active, terminal), [new(active, [log])])));
    }
}
