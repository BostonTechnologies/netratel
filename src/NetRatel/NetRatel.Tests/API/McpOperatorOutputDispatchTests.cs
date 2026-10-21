using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
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
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Contracts.Tasks;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorOutputDispatchTests
{
    private const string Resource = "https://mcp.example.test/mcp";
    private const string Content = "printf 'fixture script'";
    private static readonly Guid AgentId = Guid.Parse("965d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static readonly Guid PolicyId = Guid.Parse("7252da81-08cf-4f62-a546-241428329d2b");
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true };

    [Theory]
    [InlineData("command")]
    [InlineData("task_command")]
    [InlineData("task_script")]
    public async Task Confirmed_dispatch_captures_reference_classification_and_never_accepts_caller_output_policy(string route)
    {
        await using var app = await BuildAsync(route);
        using var client = app.GetTestClient();
        var command = JsonSerializer.SerializeToNode(new
        {
            shell = "bash", command = "printf '%s' \"$FIXTURE_REFERENCE\"", workingDirectory = "/tmp",
            timeoutSeconds = 15, maximumOutputBytes = 4096, environmentReferences = new[] { "FIXTURE_REFERENCE" }
        })!.AsObject();
        var script = JsonSerializer.SerializeToNode(new
        {
            scriptId = 81, version = 1, contentHash = Hash(Content), parameters = new { vault = "FIXTURE_REFERENCE" }
        })!.AsObject();
        var target = route == "task_script" ? script : command;
        target["outputPolicy"] = JsonSerializer.SerializeToNode(TaskOutputPolicy.Create([]) with { Binding = new string('0', 32) });
        target["outputSecretValues"] = new JsonObject { ["injected"] = "caller cannot classify output" };
        var body = route == "command" ? command : new JsonObject { [route == "task_script" ? "script" : "command"] = target };
        var root = $"/api/v2/mcp/operator/agents/42/{AgentId:D}/";
        var previewPath = root + (route == "command" ? "commands/preview" : $"tasks/preview/{(route == "task_script" ? "run_library_script" : "create_command")}");
        var confirmPath = root + (route == "command" ? "commands/confirm" : $"tasks/confirm/{(route == "task_script" ? "run_library_script" : "create_command")}");
        var preview = await client.PostAsJsonAsync(previewPath, body);
        if (route != "command")
        {
            preview.StatusCode.Should().Be(HttpStatusCode.BadRequest, "typed task input rejects output-policy injection");
            target.Remove("outputPolicy");
            target.Remove("outputSecretValues");
            preview = await client.PostAsJsonAsync(previewPath, body);
        }
        preview.StatusCode.Should().Be(HttpStatusCode.OK, await preview.Content.ReadAsStringAsync());
        using var plan = JsonDocument.Parse(await preview.Content.ReadAsStringAsync());
        body["planToken"] = plan.RootElement.GetProperty("planToken").GetString();
        body["idempotencyKey"] = plan.RootElement.GetProperty("idempotencyKey").GetString();

        var confirmed = await client.PostAsJsonAsync(confirmPath, body);

        confirmed.StatusCode.Should().Be(HttpStatusCode.Accepted, await confirmed.Content.ReadAsStringAsync());
        var dispatch = app.Services.GetRequiredService<Dispatcher>();
        dispatch.Count.Should().Be(1);
        var shellPayload = route != "task_script" ? JsonSerializer.Deserialize<ExecShellCommandPayload>(dispatch.Payload!, PayloadJson) : null;
        var libraryPayload = route == "task_script" ? JsonSerializer.Deserialize<ExecLibraryScriptPayload>(dispatch.Payload!, PayloadJson) : null;
        var policy = shellPayload?.OutputPolicy ?? libraryPayload!.OutputPolicy!;
        policy.Binding.Should().HaveLength(32).And.NotBe(new string('0', 32));
        policy.HasSecrets.Should().BeTrue();
        policy.SecretParameters.Should().BeEmpty();
        if (route == "task_script")
        {
            policy.SecretReferenceParameters.Should().Equal("vault");
            policy.ScriptContentHash.Should().Be(Hash(Content));
            libraryPayload!.Parameters!["vault"].Should().Be("FIXTURE_REFERENCE");
            libraryPayload.OutputSecretValues.Should().BeNull();
        }
        else
        {
            policy.EnvironmentReferences.Should().Equal("FIXTURE_REFERENCE");
            shellPayload!.OutputSecretValues.Should().BeNull();
        }
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var storedPolicy = route == "command" ? (await db.McpOperatorCommands.SingleAsync()).OutputProtectionJson
            : (await db.JobTaskActivities.SingleAsync()).OutputProtectionJson;
        JsonSerializer.Deserialize<TaskOutputPolicy>(storedPolicy!)!.Binding.Should().Be(policy.Binding);
        storedPolicy.Should().NotContain("caller cannot classify output");
    }

    [Fact]
    public async Task Library_script_task_rejects_a_script_owned_by_another_caller()
    {
        await using var app = await BuildAsync("task_script", scriptOwner: "another-operator");
        using var client = app.GetTestClient();
        var root = $"/api/v2/mcp/operator/agents/42/{AgentId:D}/tasks/preview/run_library_script";
        var response = await client.PostAsJsonAsync(root, new
        {
            script = new { scriptId = 81, version = 1, contentHash = Hash(Content), parameters = new { vault = "FIXTURE_REFERENCE" } }
        });

        response.IsSuccessStatusCode.Should().BeFalse();
        app.Services.GetRequiredService<Dispatcher>().Count.Should().Be(0);
    }

    private static async Task<WebApplication> BuildAsync(string route, string scriptOwner = "output-operator")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var database = "output-dispatch-" + Guid.NewGuid();
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
        builder.Services.AddScoped<IMcpOperatorCommandStore, McpOperatorCommandStore>();
        builder.Services.AddScoped<IMcpOperatorTaskStore, McpOperatorTaskStore>();
        builder.Services.AddScoped<IMcpOperatorScriptStore, McpOperatorScriptStore>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission, Admission>();
        builder.Services.AddSingleton<Dispatcher>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(provider => provider.GetRequiredService<Dispatcher>());
        builder.Services.AddSingleton<IAgentCommandGatewaySessionRegistry, AgentCommandGatewaySessionRegistry>();
        builder.Services.AddSingleton<IClientPresenceRouter, Presence>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        var app = builder.Build();
        app.UseAuthentication();
        app.Use(async (http, next) =>
        {
            var tool = route == "command" ? "netratel_commands" : "netratel_tasks";
            var operation = route == "command" ? http.Request.Path.Value!.EndsWith("/preview", StringComparison.Ordinal) ? "preview_execute" : "execute"
                : route == "task_script" ? "run_library_script" : "create_command";
            http.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity("output-operator", "output-client", null, [], [], ["netratel.mcp.execute", "netratel.mcp.read"]),
                "mcp-service", tool, operation, "output-request", DateTimeOffset.UtcNow.AddMinutes(1),
                Resource, "prod", 42, AgentId, "output-correlation");
            await next(http);
        });
        app.UseAuthorization();
        app.MapMcpOperatorCommandEndpoints();
        app.MapMcpOperatorTaskEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Scripts.Add(new ScriptDefinition { Id = 81, Name = "reference fixture", ScriptType = "bash", Content = Content });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = 81, TenantId = 42, Subject = scriptOwner, ClientId = "output-client",
            McpResource = Resource, McpInstance = "prod", PolicyId = PolicyId, PolicyVersion = 1, Name = "reference fixture",
            ShellType = "sh", ContentHash = Hash(Content), ManifestHash = Hash(string.Empty), TimeoutSeconds = 15,
            WorkingDirectory = "/tmp", Version = 1,
            ParametersJson = JsonSerializer.Serialize(new[] { new McpOperatorScriptParameter("vault", "secret_reference", true) })
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class Admission : IMcpOperatorRouteAdmission
    {
        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            if (request.Tool == "netratel_scripts" && request.Operation == "get")
                throw new InvalidOperationException("Library-script execution must not require a separate script-read grant.");
            var operation = McpOperatorOperationCatalog.Find(request.Tool, request.Operation)!;
            var access = new McpOperatorAccessRequest(request.Environment, request.Principal, request.TenantId, request.AgentId,
                McpOperatorTargetClassification.ManagedStandard, operation.OperationFamily, $"{request.Tool}/{request.Operation}",
                request.RequiredScopes, operation.ConfirmationClass, request.CorrelationId, request.RequestId, new string('A', 64),
                McpResource: request.McpResource, McpInstance: request.McpInstance, Tool: request.Tool,
                TargetOnline: true, CapabilityAvailable: true);
            var constraints = new McpOperatorConstraints(AllowedShells: ["bash"], WorkingDirectories: ["/tmp"],
                MaxCommandDurationSeconds: 60, MaxConcurrentCommands: 2, MaxOutputBytes: 4096, MaxTaskTargetCount: 1,
                MaxFanOut: 1, DestructiveOperationsAllowed: true);
            return Task.FromResult(new McpOperatorRouteAdmission(new McpOperatorDecision(true, null, null,
                [PolicyId], constraints, access.TargetSetDigest, access, 1), operation));
        }

        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            var operation = McpOperatorOperationCatalog.Find(request.Tool, request.Operation)!;
            return Task.FromResult(new McpOperatorAcceptedAudit(Guid.NewGuid(), PolicyId, request.Environment,
                request.ServicePrincipal, request.Principal.Subject, request.Principal.ClientId, null, [], [],
                request.Principal.Scopes.ToArray(), request.McpResource, request.McpInstance, request.Tool,
                request.TenantId, request.AgentId, operation.OperationFamily, $"{request.Tool}/{request.Operation}",
                request.CorrelationId, request.RequestId, DateTimeOffset.UtcNow));
        }
    }

    private sealed class Dispatcher : IAgentCommandAuthorityDispatcher
    {
        public string? Payload { get; private set; }
        public int Count { get; private set; }
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken)
        { Count++; Payload = payloadJson; return Task.CompletedTask; }
    }

    private sealed class Presence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(
            new ClientPresenceSnapshot(client, ClientPresenceStatus.Online, 1, Guid.NewGuid(), 1, DateTimeOffset.UtcNow, "test", [], null, "test", true));
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }
}
