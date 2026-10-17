using System.Net;
using System.Net.Http.Json;
using System.Threading.Channels;
using NetRatel.Shared.Contracts.Tasks;
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
        IReadOnlyList<JobParamDto>? parameters = null, Action<RunJobRequest>? runAccepted = null)
    {
        services.AddSingleton<IJobApiClient>(new JobApiClient(new JobsTransport(parameterCount, mixed, parameters, runAccepted)));
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

    private sealed class JobsTransport(int parameterCount, bool mixed, IReadOnlyList<JobParamDto>? parameters, Action<RunJobRequest>? runAccepted) : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Guid _agentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
        private JobDto _job = new(42, "Nightly backup", "/Visual fixture/Backups/", "Back up the development database", 3, "gateway-agent-01", 0, 0,
            "Visual fixture", "gateway-agent-01", ClientEnvironment.Dev,
            Schedule: new(true, "Africa/Johannesburg", "08:00", ["Monday", "Wednesday", "Friday"]),
            AgentId: Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46"));
        private List<JobStepDto> _steps = [new(1, 42, 1, JobStepTypeDto.RunCommand, "bash", "echo fixture backup", null, null, true)];
        private List<JobParamDto> _parameters = Enumerable.Range(1, parameterCount).Select(index =>
            new JobParamDto((ulong)index, 42, $"Input {index}", "string", true, $"value {index}", $"Short help for input {index}", null)).ToList();
        private JobRunDto _run = JobsRunFixtureStream.Run;
        private IReadOnlyList<JobStepRunDto> _runSteps = JobsRunFixtureStream.Steps(1);
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
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://jobs-fixture.invalid") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Initialize();
            var path = request.RequestUri!.AbsolutePath;
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
                    body.ExpectedRuntimeSeconds ?? 1800, body.GraceSeconds ?? 0, body.HardTimeoutSeconds ?? 1800, body.Schedule, body.AgentId ?? _agentId);
                _steps = []; _parameters = [];
                return Ok(_job);
            }
            if (request.Method == HttpMethod.Put && path.StartsWith("/api/v1/jobs/"))
            {
                var body = (await request.Content!.ReadFromJsonAsync<UpdateJobRequest>(cancellationToken))!;
                _job = _job with { Name = body.Name ?? _job.Name, FolderPath = body.FolderPath ?? _job.FolderPath, Schedule = body.Schedule, AgentId = body.AgentId };
                return Ok(_job);
            }
            throw new InvalidOperationException($"Unexpected Jobs fixture request: {request.Method} {path}");
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
