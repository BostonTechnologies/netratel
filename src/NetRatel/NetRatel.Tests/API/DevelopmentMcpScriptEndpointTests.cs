using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.API.Services.Events;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class DevelopmentMcpScriptEndpointTests
{
    [Fact]
    public async Task MarkerScriptLifecycle_IsTargetOwned_ServerGenerated_AndAudited()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";

        var create = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-001", "Initial marker", "safe", "sh"));
        create.StatusCode.Should().Be(HttpStatusCode.Created);
        var script = await create.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();
        script.Should().NotBeNull();
        script!.FolderPath.Should().Be($"/mcp-dev/3/{agentId:N}/");
        script.Content.Should().Contain("NETRATEL_MCP_QA_MARKER=mcp-qa-script-001").And.Contain("id -un").And.NotContain("curl");
        create.Headers.Should().Contain(header => header.Key == "X-Development-Operation-Audit-Id");

        var list = await client.GetFromJsonAsync<List<DevelopmentMcpScriptDto>>(root);
        var get = await client.GetFromJsonAsync<DevelopmentMcpScriptDto>($"{root}/{script.Id}");
        var parameters = await client.GetFromJsonAsync<List<DevelopmentMcpScriptParameterDto>>($"{root}/{script.Id}/params");
        var parse = await client.PostAsync($"{root}/{script.Id}/parse-manifest", null);
        var update = await client.PutAsJsonAsync($"{root}/{script.Id}", new UpdateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-002", "Updated marker", "still safe"));
        var updated = await update.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();
        var delete = await client.DeleteAsync($"{root}/{script.Id}");
        var reread = await client.GetAsync($"{root}/{script.Id}");

        list.Should().ContainSingle(row => row.Id == script.Id);
        get!.Marker.Should().Be("mcp-qa-script-001");
        parameters.Should().BeEmpty();
        parse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        update.StatusCode.Should().Be(HttpStatusCode.OK);
        updated!.Marker.Should().Be("mcp-qa-script-002");
        updated.Content.Should().Contain("NETRATEL_MCP_QA_MARKER=mcp-qa-script-002");
        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        reread.StatusCode.Should().Be(HttpStatusCode.NotFound);
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().OnlyContain(operation => operation == DevelopmentOperatorOperation.ScriptMutation);
    }

    [Fact]
    public async Task MarkerScriptRoutes_RejectInvalidBodyAndCrossTargetWithoutExposingGlobalScriptLibrary()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";

        var invalid = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("contains space", "x", "x", "sh"));
        var created = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-003", "x", "x", "powershell"));
        var script = await created.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();
        var crossTarget = await client.GetAsync($"/api/v2/development/mcp/agents/3/{Guid.NewGuid():D}/scripts/{script!.Id}");

        invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        crossTarget.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await crossTarget.Content.ReadAsStringAsync()).Should().Contain("target_not_authorized");
        using var scope = app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Scripts.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task CancellationProbeMarkerScript_IsFixedDelayServerGeneratedAndPersistsItsMode()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";

        var create = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-cancel-probe", "Cancellation probe", "safe", "sh", "cancellation_probe"));
        var script = await create.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        script.Should().NotBeNull();
        script!.ExecutionMode.Should().Be("cancellation_probe");
        script.Content.Should().Contain("sleep 15").And.NotContain("curl");
        using var scope = app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().DevelopmentMcpScripts.SingleAsync()).ExecutionMode.Should().Be("cancellation_probe");
    }

    [Fact]
    public async Task MarkerScriptRoutes_RejectRevokedTargetBeforeAnyScriptWrite()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        app.Services.GetRequiredService<TestTargetAuthority>().Enabled = false;

        var response = await AuthorizedClient(app).PostAsJsonAsync(
            $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts",
            new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-004", "x", "x", "sh"));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Scripts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MarkerScriptRun_IsTargetOwned_ServerGenerated_AndCreatesNoCallerControlledCommand()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";
        var created = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-run", "Run marker", "safe", "sh"));
        var script = await created.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();

        var run = await client.PostAsync($"{root}/{script!.Id}/runs", null);
        var result = await run.Content.ReadFromJsonAsync<DevelopmentMcpMarkerScriptRunDto>();

        run.StatusCode.Should().Be(HttpStatusCode.Accepted);
        result.Should().NotBeNull();
        result!.ScriptId.Should().Be(script.Id);
        result.RequestId.Should().HaveLength(32).And.MatchRegex("^[0-9a-f]{32}$");
        app.Services.GetRequiredService<RecordingCommandDispatcher>().Dispatches.Should().ContainSingle();
        var dispatch = app.Services.GetRequiredService<RecordingCommandDispatcher>().Dispatches.Single();
        dispatch.CommandId.Should().Be(result.RequestId);
        dispatch.TaskType.Should().Be(TaskKinds.ExecLibraryScript);
        dispatch.Payload.Should().Contain("NETRATEL_MCP_QA_MARKER=mcp-qa-script-run").And.NotContain("curl");
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Contain(DevelopmentOperatorOperation.ScriptMutation).And.Contain(DevelopmentOperatorOperation.TaskCreate);
    }

    [Fact]
    public async Task MarkerScriptRun_RejectsAChangedLibraryEntryBeforeDispatch()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var root = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";
        var created = await client.PostAsJsonAsync(root, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-script-tamper", "Tamper marker", "safe", "sh"));
        var script = await created.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.Scripts.SingleAsync(candidate => candidate.Id == (long)script!.Id)).Content = "echo changed";
            await db.SaveChangesAsync();
        }

        var run = await client.PostAsync($"{root}/{script!.Id}/runs", null);

        run.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await run.Content.ReadAsStringAsync()).Should().Contain("marker_script_integrity_invalid");
        app.Services.GetRequiredService<RecordingCommandDispatcher>().Dispatches.Should().BeEmpty();
    }

    [Fact]
    public async Task MarkerJobLifecycle_IsTargetOwned_ServerGenerated_AndAudited()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var scriptsRoot = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";
        var markerJobRoot = $"/api/v2/development/mcp/agents/3/{agentId:D}/marker-jobs";
        var scriptResponse = await client.PostAsJsonAsync(scriptsRoot, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-marker-job", "Marker job", "safe", "sh"));
        var script = await scriptResponse.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();

        var create = await client.PostAsync($"{markerJobRoot}/{script!.Id}", null);
        var markerJob = await create.Content.ReadFromJsonAsync<DevelopmentMcpMarkerJobDto>();

        create.StatusCode.Should().Be(HttpStatusCode.Created);
        create.Headers.Should().Contain(header => header.Key == "X-Development-Operation-Audit-Id");
        markerJob.Should().NotBeNull();
        markerJob!.ScriptId.Should().Be(script.Id);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var definition = await db.Jobs.SingleAsync(job => job.Id == (long)markerJob.JobId);
            definition.FolderPath.Should().Be($"/mcp-dev/3/{agentId:N}/jobs/");
            definition.ClientIdentity.Should().Be(agentId.ToString("D"));
            var step = await db.JobSteps.SingleAsync(candidate => candidate.JobId == (long)markerJob.JobId);
            step.Type.Should().Be((int)JobStepKind.LibraryScript);
            step.ScriptId.Should().Be((long)script.Id);
            step.Command.Should().BeNull();
            step.PayloadJson.Should().BeNull();
        }

        var run = await client.PostAsync($"{markerJobRoot}/{markerJob.JobId}/runs", null);
        var started = await run.Content.ReadFromJsonAsync<DevelopmentMcpMarkerJobRunDto>();

        run.StatusCode.Should().Be(HttpStatusCode.Accepted);
        started.Should().NotBeNull();
        started!.JobId.Should().Be(markerJob.JobId);
        var startedCall = app.Services.GetRequiredService<RecordingJobAuthority>().Starts.Should().ContainSingle().Which;
        startedCall.JobId.Should().Be(markerJob.JobId);
        startedCall.Request.StartedBy.Should().Be("mcp:marker-job");
        startedCall.Request.InputsJson.Should().BeNull();
        startedCall.Request.ClientIdentityOverride.Should().BeNull();
        using (var scope = app.Services.CreateScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            await runs.UpsertRunAsync(new UpsertJobRunCommand(started.RunId, markerJob.JobId, 3, string.Empty, "mcp:marker-job", JobRunState.Running, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, agentId));
        }

        var cancel = await client.PostAsync($"{markerJobRoot}/{markerJob.JobId}/runs/{started.RunId}/cancel", null);

        cancel.StatusCode.Should().Be(HttpStatusCode.Accepted);
        app.Services.GetRequiredService<RecordingJobAuthority>().CancelledRunIds.Should().ContainSingle().Which.Should().Be(started.RunId);
        using (var scope = app.Services.CreateScope())
        {
            var runs = scope.ServiceProvider.GetRequiredService<IJobRunService>();
            (await runs.GetAsync(started.RunId))!.Status.Should().Be(JobRunState.Cancelled);
        }

        var delete = await client.DeleteAsync($"{markerJobRoot}/{markerJob.JobId}");

        delete.StatusCode.Should().Be(HttpStatusCode.NoContent);
        app.Services.GetRequiredService<TestTargetAuthority>().AcceptedOperations.Should().Contain([DevelopmentOperatorOperation.ScriptMutation, DevelopmentOperatorOperation.JobDefinitionMutation, DevelopmentOperatorOperation.JobRunStart, DevelopmentOperatorOperation.JobRunCancel, DevelopmentOperatorOperation.JobRunDelete]);
    }

    [Fact]
    public async Task MarkerJobRoutes_RejectTargetBeforeCreatingGlobalJob()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(agentId);
        var client = AuthorizedClient(app);
        var scriptsRoot = $"/api/v2/development/mcp/agents/3/{agentId:D}/scripts";
        var scriptResponse = await client.PostAsJsonAsync(scriptsRoot, new CreateDevelopmentMcpMarkerScriptRequest("mcp-qa-marker-job-reject", "Marker job", "safe", "sh"));
        var script = await scriptResponse.Content.ReadFromJsonAsync<DevelopmentMcpScriptDto>();
        app.Services.GetRequiredService<TestTargetAuthority>().Enabled = false;

        var response = await client.PostAsync($"/api/v2/development/mcp/agents/3/{agentId:D}/marker-jobs/{script!.Id}", null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var scope = app.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().Jobs.CountAsync()).Should().Be(0);
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().DevelopmentMcpMarkerJobs.CountAsync()).Should().Be(0);
    }

    private static HttpClient AuthorizedClient(IHost app)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        return client;
    }

    private static async Task<IHost> BuildAppAsync(Guid agentId)
    {
        var databaseName = $"development-mcp-scripts-{Guid.NewGuid():N}";
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddHttpContextAccessor();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
                services.AddScoped<IScriptService, ScriptService>();
                services.AddScoped<IJobDefinitionService, JobDefinitionService>();
                services.AddScoped<IJobRunService, JobRunService>();
                services.AddScoped<IEventRecorder, NoopEventRecorder>();
                services.AddScoped<ICorrelationContext, HttpCorrelationContext>();
                services.AddSingleton<RecordingCommandDispatcher>();
                services.AddSingleton<IAgentCommandAuthorityDispatcher>(provider => provider.GetRequiredService<RecordingCommandDispatcher>());
                services.AddSingleton<RecordingJobAuthority>();
                services.AddSingleton<IAkkaJobAuthorityService>(provider => provider.GetRequiredService<RecordingJobAuthority>());
                services.AddSingleton(new TestTargetAuthority(agentId));
                services.AddSingleton<IDevelopmentOperatorTargetAuthority>(provider => provider.GetRequiredService<TestTargetAuthority>());
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapDevelopmentMcpScriptEndpoints();
                    endpoints.MapDevelopmentMcpMarkerJobEndpoints();
                });
            });
        });
        var app = await builder.StartAsync();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Agents.Add(new Agent { Id = agentId, TenantId = 3, Name = "mcp-qa-agent", CreatedAtUtc = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        return app;
    }

    private sealed class NoopEventRecorder : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestTargetAuthority(Guid agentId) : IDevelopmentOperatorTargetAuthority
    {
        private readonly DevelopmentOperatorTargetGrantView _grant = new(Guid.NewGuid(), 3, agentId, DevelopmentOperatorTargetClassification.DedicatedQa, DevelopmentOperatorOperationScope.Scripts, DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1), true, "test-evidence");
        public List<DevelopmentOperatorOperation> AcceptedOperations { get; } = [];
        public bool Enabled { get; set; } = true;
        public Task<DevelopmentOperatorTargetGrantView> GrantAsync(DevelopmentOperatorTargetGrantRequest request, CancellationToken cancellationToken) => Task.FromResult(_grant);
        public Task<DevelopmentOperatorTargetGrantView?> RevokeAsync(int tenantId, Guid requestedAgentId, string reason, string actorId, string correlationId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(null);
        public Task<DevelopmentOperatorTargetGrantView?> GetActiveGrantAsync(int tenantId, Guid requestedAgentId, CancellationToken cancellationToken) => Task.FromResult<DevelopmentOperatorTargetGrantView?>(Enabled && tenantId == 3 && requestedAgentId == agentId ? _grant : null);
        public Task<DevelopmentOperatorTargetDecision> EvaluateAsync(DevelopmentOperatorTargetRequest request, CancellationToken cancellationToken)
        {
            var allowed = Enabled && request.TenantId == 3 && request.AgentId == agentId;
            return Task.FromResult(new DevelopmentOperatorTargetDecision(allowed, allowed ? null : "target_not_authorized", allowed ? _grant.GrantId : null, request));
        }
        public Task<DevelopmentOperatorAcceptedAudit> RecordAcceptedAsync(DevelopmentOperatorTargetDecision decision, CancellationToken cancellationToken)
        {
            AcceptedOperations.Add(decision.Request.Operation);
            return Task.FromResult(new DevelopmentOperatorAcceptedAudit(Guid.NewGuid(), decision.Request.TenantId, decision.Request.AgentId, decision.Request.Operation, decision.Request.ActorId, decision.Request.CorrelationId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class RecordingCommandDispatcher : IAgentCommandAuthorityDispatcher
    {
        public List<Dispatch> Dispatches { get; } = [];
        public bool IsAvailable(ClientKey client) => true;

        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken)
        {
            Dispatches.Add(new Dispatch(client, commandId, correlationId, taskType, payloadJson, environment));
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingJobAuthority : IAkkaJobAuthorityService
    {
        public List<(ulong JobId, RunJobRequest Request)> Starts { get; } = [];
        public List<ulong> CancelledRunIds { get; } = [];

        public Task<JobRunInfo> StartAsync(ulong jobId, RunJobRequest request, CancellationToken cancellationToken)
        {
            Starts.Add((jobId, request));
            return Task.FromResult(new JobRunInfo(700, jobId, 3, string.Empty, request.StartedBy, JobRunState.Running, 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, null, Starts.Count == 0 ? null : Guid.Empty));
        }

        public Task RecordLifecycleAsync(ClientKey client, JobLifecycleUpdateEnvelope lifecycle, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> CancelAsync(ulong jobRunId, string reason, CancellationToken cancellationToken)
        {
            CancelledRunIds.Add(jobRunId);
            return Task.FromResult(true);
        }
    }

    private sealed record Dispatch(ClientKey Client, string CommandId, string CorrelationId, string TaskType, string Payload, int Environment);

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "test-admin")], Scheme.Name)), Scheme.Name)));
    }
}
