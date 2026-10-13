using System.Net;
using System.Net.Http.Json;
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
    public static void Configure(IServiceCollection services)
    {
        services.AddSingleton<IJobApiClient>(new JobApiClient(new JobsTransport()));
        services.AddSingleton<ITenantApiService, JobsTenants>();
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

    private sealed class JobsTransport : HttpMessageHandler, IHttpClientFactory
    {
        private readonly Guid _agentId = Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46");
        private JobDto _job = new(42, "Nightly backup", "/Visual fixture/Backups/", "Back up the development database", 3, "gateway-agent-01", 0, 0,
            "Visual fixture", "gateway-agent-01", ClientEnvironment.Dev,
            Schedule: new(true, "Africa/Johannesburg", "08:00", ["Monday", "Wednesday", "Friday"]),
            AgentId: Guid.Parse("4886c6c0-e486-4f59-a006-40e4043a4a46"));
        private List<JobStepDto> _steps = [new(1, 42, 1, JobStepTypeDto.RunCommand, "bash", "echo fixture backup", null, null, true)];
        private List<JobParamDto> _parameters = [new(1, 42, "Folder", "string", true, "/fixture/backup", "Fixture output path", null)];
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false) { BaseAddress = new("https://jobs-fixture.invalid") };
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get)
            {
                if (path == "/api/v1/jobs") return Ok(new[] { _job });
                if (path == "/api/v1/jobruns/query") return Ok(new PagedJobRunsDto([], 0));
                if (path.EndsWith("/params")) return Ok(_parameters);
                if (path.EndsWith("/steps")) return Ok(_steps);
                if (path.EndsWith("/details")) return Ok(new JobWithDetailsDto(_job, _steps, _parameters));
                if (path == "/api/v1/jobs/42") return Ok(_job);
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
