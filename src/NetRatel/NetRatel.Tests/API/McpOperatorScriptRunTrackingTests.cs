using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorScriptRunTrackingTests
{
    private static readonly Guid AgentId = Guid.Parse("12075997-8ed4-4f23-bec9-31b69fb53fe9");
    private static readonly Guid PolicyId = Guid.Parse("72075997-8ed4-4f23-bec9-31b69fb53fe9");
    private const string Resource = "https://mcp.prod.example/mcp";
    private const string Content = "printf 'tracked-script'";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Fact]
    public async Task Confirmed_script_run_returns_an_owned_task_that_exposes_results_and_replays_without_redispatch()
    {
        await using var app = await BuildAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Tool", "netratel_scripts");
        client.DefaultRequestHeaders.Add("X-Test-Operation", "run");
        var root = $"/api/v2/mcp/operator/agents/42/{AgentId:D}";
        var previewResponse = await client.PostAsJsonAsync(root + "/scripts/901/runs/preview", new { version = 1, contentHash = Hash(Content) });
        previewResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var preview = await previewResponse.Content.ReadFromJsonAsync<McpOperatorScriptRunPreview>();
        var request = new { version = 1, contentHash = Hash(Content), planToken = preview!.PlanToken, idempotencyKey = preview.IdempotencyKey };
        var response = await client.PostAsJsonAsync(root + "/scripts/901/runs/confirm", request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var run = await response.Content.ReadFromJsonAsync<McpOperatorScriptRunResult>();
        run!.TaskId.Should().NotBeNullOrWhiteSpace();
        response.Headers.Location!.ToString().Should().EndWith("/tasks/" + run.TaskId);
        var replay = await (await client.PostAsJsonAsync(root + "/scripts/901/runs/confirm", request)).Content.ReadFromJsonAsync<McpOperatorScriptRunResult>();
        replay!.TaskId.Should().Be(run.TaskId);
        replay.RequestId.Should().Be(run.RequestId);
        replay.Replayed.Should().BeTrue();
        app.Services.GetRequiredService<Dispatcher>().Dispatches.Should().Be(1);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var tasks = scope.ServiceProvider.GetRequiredService<IMcpOperatorTaskStore>();
            await tasks.RecordLifecycleAsync(run.RequestId, 42, AgentId, "Completed", "tracked-script-result", DateTimeOffset.UtcNow, CancellationToken.None);
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorScripts.SingleAsync()).DeletedAtUtc = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }
        client.DefaultRequestHeaders.Remove("X-Test-Tool");
        client.DefaultRequestHeaders.Add("X-Test-Tool", "netratel_tasks");
        client.DefaultRequestHeaders.Remove("X-Test-Operation");
        client.DefaultRequestHeaders.Add("X-Test-Operation", "get");
        var result = await client.GetAsync(root + "/tasks/" + run.TaskId);
        result.StatusCode.Should().Be(HttpStatusCode.OK);
        var resultJson = await result.Content.ReadAsStringAsync();
        resultJson.Should().Contain("tracked-script-result").And.Contain("Completed");
        client.DefaultRequestHeaders.Add("X-Test-Subject", "another-operator");
        (await client.GetAsync(root + "/tasks/" + run.TaskId)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dispatch_failure_closes_the_created_task_instead_of_leaking_execution_capacity()
    {
        await using var app = await BuildAsync();
        app.Services.GetRequiredService<Dispatcher>().Fail = true;
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Tool", "netratel_scripts");
        client.DefaultRequestHeaders.Add("X-Test-Operation", "run");
        var path = $"/api/v2/mcp/operator/agents/42/{AgentId:D}/scripts/901/runs/confirm";
        var response = await client.PostAsJsonAsync(path, new
        {
            version = 1, contentHash = Hash(Content),
            planToken = "0123456789abcdef0123456789abcdef", idempotencyKey = "0123456789abcdef0123456789abcdef"
        });
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        await using var scope = app.Services.CreateAsyncScope();
        var task = await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().McpOperatorTasks.SingleAsync();
        task.State.Should().Be("Failed");
        task.CompletedAtUtc.Should().NotBeNull();
    }

    private static async Task<WebApplication> BuildAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "script-run-" + Guid.NewGuid();
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(databaseName));
        builder.Services.AddScoped<IMcpOperatorScriptStore, McpOperatorScriptStore>();
        builder.Services.AddScoped<IMcpOperatorTaskStore, McpOperatorTaskStore>();
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission, Admission>();
        builder.Services.AddSingleton<IMcpOperatorConfirmationService, Confirmations>();
        builder.Services.AddSingleton<IClientPresenceRouter, Presence>();
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(services => services.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<IAgentCommandGatewaySessionRegistry>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEventRecorder, Events>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        builder.Services.AddSingleton(new NetRatelAkkaMigrationOptions
        {
            Enabled = true, PresenceEnabled = true, GatewayEnabled = true, PresenceAuthorityEnabled = true,
            CommandShadowEnabled = true, CommandAuthorityEnabled = true
        });
        var app = builder.Build();
        app.UseAuthentication();
        // Authentication/delegation cryptography has separate integration coverage; this fixture
        // supplies the trusted middleware output to exercise the real route/store/result boundary.
        app.Use(async (context, next) =>
        {
            var subject = context.Request.Headers["X-Test-Subject"].FirstOrDefault() ?? "script-operator";
            context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity(subject, null, Resource, [], ["Operator"], ["netratel.mcp.execute", "netratel.mcp.read"]),
                "mcp-service", context.Request.Headers["X-Test-Tool"].ToString(), context.Request.Headers["X-Test-Operation"].ToString(),
                "request-test", DateTimeOffset.UtcNow.AddMinutes(1), Resource, "prod", 42, AgentId, "script-test");
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcpOperatorScriptEndpoints();
        app.MapMcpOperatorTaskEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Scripts.Add(new ScriptDefinition { Id = 901, Name = "tracked-script", Description = "test", ScriptType = "bash", Content = Content });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = 901, TenantId = 42, Subject = "script-operator", ClientId = string.Empty,
            McpResource = Resource, McpInstance = "prod", PolicyId = PolicyId, PolicyVersion = 1,
            Name = "tracked-script", Description = "test", ShellType = "sh", ContentHash = Hash(Content), ManifestHash = Hash(string.Empty),
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", DeclaredSideEffectsJson = "[1]", Version = 1
        });
        await db.SaveChangesAsync();
        return app;
    }

    private sealed class Admission : IMcpOperatorRouteAdmission
    {
        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            var access = new McpOperatorAccessRequest(request.Environment, request.Principal, request.TenantId, request.AgentId, null,
                McpOperatorOperationFamily.TaskExecution, $"{request.Tool}/{request.Operation}", request.RequiredScopes,
                McpOperatorConfirmationClass.RemoteExecution, request.CorrelationId, request.RequestId, Hash("target"),
                McpResource: request.McpResource, McpInstance: request.McpInstance, Tool: request.Tool);
            return Task.FromResult(new McpOperatorRouteAdmission(new McpOperatorDecision(true, null, null, [PolicyId],
                new McpOperatorConstraints(MaxConcurrentCommands: 2, MaxTaskTargetCount: 1, MaxFanOut: 1, MaxOutputBytes: 4096, MaxCommandDurationSeconds: 60),
                Hash("target"), access, 1), McpOperatorOperationCatalog.Find(request.Tool, request.Operation)));
        }
        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new McpOperatorAcceptedAudit(Guid.NewGuid(), PolicyId, request.Environment, request.ServicePrincipal, request.Principal.Subject,
                request.Principal.ClientId, request.Principal.AuthorizedParty, [], [], [], request.McpResource, request.McpInstance,
                request.Tool, request.TenantId, request.AgentId, McpOperatorOperationFamily.TaskExecution, $"{request.Tool}/{request.Operation}",
                request.CorrelationId, request.RequestId, DateTimeOffset.UtcNow));
    }
    private sealed class Confirmations : IMcpOperatorConfirmationService
    {
        private const string Credential = "0123456789abcdef0123456789abcdef";
        private readonly Guid id = Guid.NewGuid();
        private string? result;
        public Task<McpOperatorConfirmationPlan> CreatePlanAsync(McpOperatorConfirmationPlanRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new McpOperatorConfirmationPlan(Credential, Credential, DateTimeOffset.UtcNow.AddMinutes(1), McpOperatorConfirmationClass.RemoteExecution, request.PayloadHash, request.Decision.TargetSetDigest));
        public Task<McpOperatorConfirmationAdmission> ConfirmAsync(McpOperatorConfirmationRequest request, CancellationToken cancellationToken)
        {
            request.PlanToken.Should().Be(Credential);
            request.IdempotencyKey.Should().Be(Credential);
            return Task.FromResult(new McpOperatorConfirmationAdmission(result is null, result is not null, null, id,
                result is null ? McpOperatorIdempotencyOutcome.Pending : McpOperatorIdempotencyOutcome.Succeeded, result));
        }
        public Task CompleteAsync(Guid idempotencyId, McpOperatorIdempotencyOutcome outcome, string? resultReference, CancellationToken cancellationToken)
        { result = resultReference; return Task.CompletedTask; }
    }
    private sealed class Dispatcher : IAgentCommandAuthorityDispatcher
    {
        public int Dispatches { get; private set; }
        public bool Fail { get; set; }
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken)
        { if (Fail) throw new InvalidOperationException("test-dispatch-failure"); Dispatches++; return Task.CompletedTask; }
    }
    private sealed class Presence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(
            new ClientPresenceSnapshot(client, ShadowPresenceStatus.Online, 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "test", [], null, "test", true));
    }
    private sealed class Events : IEventRecorder
    {
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }
}
