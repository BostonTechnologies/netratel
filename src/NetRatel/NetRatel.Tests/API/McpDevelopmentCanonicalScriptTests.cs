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
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.API.Services.Operations;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpDevelopmentCanonicalScriptTests
{
    private static readonly Guid AgentId = Guid.Parse("12075997-8ed4-4f23-bec9-31b69fb53fe9");
    private static readonly Guid PolicyId = Guid.Parse("72075997-8ed4-4f23-bec9-31b69fb53fe9");
    private static string Root => $"/api/v2/mcp/operator/agents/42/{AgentId:D}/scripts";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    [Theory]
    [InlineData("Development", true, true)]
    [InlineData("Development", false, false)]
    [InlineData("Production", true, false)]
    [InlineData("Production", false, false)]
    public async Task Canonical_visibility_requires_actual_Development_and_admitted_environment_access(string environment, bool full, bool visible)
    {
        await using var app = await BuildAsync(environment, full);
        using var client = Client(app, "list");
        var list = await client.GetAsync(Root);
        list.StatusCode.Should().Be(HttpStatusCode.OK, await list.Content.ReadAsStringAsync());
        var rows = await list.Content.ReadFromJsonAsync<JsonElement>();
        rows.GetArrayLength().Should().Be(visible ? 1 : 0);
        if (visible)
        {
            rows[0].GetProperty("scriptId").GetInt64().Should().Be(901);
            rows[0].GetProperty("definitionScope").GetString().Should().Be("development_environment");
            rows[0].GetProperty("sourceRevision").GetInt64().Should().Be(1);
            rows[0].GetProperty("executionEligible").GetBoolean().Should().BeFalse();
        }
        client.DefaultRequestHeaders.Remove("X-Test-Operation");
        client.DefaultRequestHeaders.Add("X-Test-Operation", "create");
        var response = await client.PostAsJsonAsync(Root + "/preview/create", new { source = Source("created") });
        response.StatusCode.Should().Be(visible ? HttpStatusCode.OK : HttpStatusCode.Forbidden);
        await using var scope = app.Services.CreateAsyncScope();
        (await scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>().McpOperatorScripts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Shared_UI_edit_invalidates_a_previously_confirmed_source_preview()
    {
        await using var app = await BuildAsync();
        using var client = Client(app, "update");
        var preview = await Preview(client, "update", new { scriptId = 901, expectedSourceRevision = 1, expectedContentHash = Hash("original"), source = Source("MCP edit") });
        var ui = await client.PutAsJsonAsync("/api/v1/script-library/901", new { content = "UI winner", expectedSourceRevision = 1 });
        ui.StatusCode.Should().Be(HttpStatusCode.OK, await ui.Content.ReadAsStringAsync());
        (await ui.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("sourceRevision").GetInt64().Should().Be(2);
        var response = await client.PostAsJsonAsync(Root + "/confirm/update", new
        {
            scriptId = 901,
            expectedSourceRevision = 1,
            expectedContentHash = Hash("original"),
            source = Source("MCP edit"),
            planToken = preview.GetProperty("planToken").GetString(),
            idempotencyKey = preview.GetProperty("idempotencyKey").GetString()
        });
        response.StatusCode.Should().Be(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("script_source_revision_conflict");
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Scripts.SingleAsync()).Content.Should().Be("UI winner");
        (await db.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Failed);
        app.Services.GetRequiredService<Events>().Recorded.Should().ContainSingle();
    }

    [Fact]
    public async Task Canonical_create_and_delete_replay_without_adoption_or_duplicate_source_writes()
    {
        await using var app = await BuildAsync();
        using var client = Client(app, "create");
        var preview = await Preview(client, "create", new { source = Source("new source") });
        var request = new { source = Source("new source"), planToken = preview.GetProperty("planToken").GetString(), idempotencyKey = preview.GetProperty("idempotencyKey").GetString() };
        var response = await client.PostAsJsonAsync(Root + "/confirm/create", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var created = await response.Content.ReadFromJsonAsync<McpDevelopmentScriptMutationResult>();
        var replayResponse = await client.PostAsJsonAsync(Root + "/confirm/create", request);
        replayResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await replayResponse.Content.ReadFromJsonAsync<McpDevelopmentScriptMutationResult>();
        replay!.ScriptId.Should().Be(created!.ScriptId);
        replay.Replayed.Should().BeTrue();
        client.DefaultRequestHeaders.Remove("X-Test-Operation");
        client.DefaultRequestHeaders.Add("X-Test-Operation", "delete");
        var deletePreview = await Preview(client, "delete", new { scriptId = created.ScriptId, expectedSourceRevision = created.SourceRevision, expectedContentHash = Hash("new source") });
        var delete = new { scriptId = created.ScriptId, expectedSourceRevision = created.SourceRevision, expectedContentHash = Hash("new source"), planToken = deletePreview.GetProperty("planToken").GetString(), idempotencyKey = deletePreview.GetProperty("idempotencyKey").GetString() };
        (await client.PostAsJsonAsync(Root + "/confirm/delete", delete)).StatusCode.Should().Be(HttpStatusCode.OK);
        var deletedReplay = await (await client.PostAsJsonAsync(Root + "/confirm/delete", delete)).Content.ReadFromJsonAsync<McpDevelopmentScriptMutationResult>();
        deletedReplay!.Deleted.Should().BeTrue();
        deletedReplay.Replayed.Should().BeTrue();
        deletedReplay.SourceRevision.Should().Be(2);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.Scripts.CountAsync()).Should().Be(1);
        (await db.Scripts.IgnoreQueryFilters().CountAsync()).Should().Be(2);
        (await db.McpOperatorScripts.CountAsync()).Should().Be(0);
        app.Services.GetRequiredService<Events>().Recorded.Should().HaveCount(2);
        JsonSerializer.Serialize(app.Services.GetRequiredService<Events>().Recorded).Should().NotContain("new source");
    }

    [Fact]
    public async Task UI_requires_observed_revision_and_preserves_winner_after_stale_edit()
    {
        await using var app = await BuildAsync();
        using var client = Client(app, "get");
        (await client.PutAsJsonAsync("/api/v1/script-library/901", new { content = "unfenced" })).StatusCode.Should().Be((HttpStatusCode)428);
        (await client.DeleteAsync("/api/v1/script-library/901")).StatusCode.Should().Be((HttpStatusCode)428);
        (await client.PutAsJsonAsync("/api/v1/script-library/901", new { content = "winner", expectedSourceRevision = 1 })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PutAsJsonAsync("/api/v1/script-library/901", new { content = "stale", expectedSourceRevision = 1 })).StatusCode.Should().Be(HttpStatusCode.Conflict);
        var source = await client.GetFromJsonAsync<JsonElement>("/api/v1/script-library/901");
        source.GetProperty("content").GetString().Should().Be("winner");
        source.GetProperty("sourceRevision").GetInt64().Should().Be(2);
    }

    private static object Source(string content) => new { name = "canonical", content, scriptType = "bash" };
    private static HttpClient Client(WebApplication app, string operation)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Test-Operation", operation);
        return client;
    }
    private static async Task<JsonElement> Preview(HttpClient client, string action, object request)
    {
        var response = await client.PostAsJsonAsync(Root + "/preview/" + action, request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static async Task<WebApplication> BuildAsync(string environment = "Development", bool full = true)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
        builder.Services.AddAuthorization(options =>
        {
            options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser());
            options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser());
        });
        var name = "canonical-script-" + Guid.NewGuid();
        builder.Services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(name));
        builder.Services.AddScoped<IMcpOperatorScriptStore, McpOperatorScriptStore>();
        builder.Services.AddScoped<IScriptService, ScriptService>();
        builder.Services.AddScoped<McpDevelopmentScriptAdapter>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(new Admission(full));
        builder.Services.AddSingleton<IClientPresenceRouter, Presence>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher, Dispatcher>();
        builder.Services.AddSingleton<IAgentCommandGatewaySessionRegistry>(_ => throw new NotSupportedException());
        builder.Services.AddScoped<IMcpOperatorTaskStore, McpOperatorTaskStore>();
        builder.Services.AddSingleton<Events>();
        builder.Services.AddSingleton<IEventRecorder>(services => services.GetRequiredService<Events>());
        builder.Services.AddSingleton<ICorrelationContext, Correlation>();
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        builder.Services.AddSingleton<NetRatelAkkaMigrationOptions>();
        var app = builder.Build();
        app.UseAuthentication();
        // The trusted delegation output is supplied here; signature verification has separate coverage.
        app.Use(async (context, next) =>
        {
            var instance = environment == "Development" ? "dev" : "prod";
            var resource = $"https://mcp.{instance}.example/mcp";
            context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity("script-operator", null, resource, [], ["Operator"], ["netratel.mcp.write", "netratel.mcp.read"]),
                "mcp-service", "netratel_scripts", context.Request.Headers["X-Test-Operation"].ToString(),
                "request-test", DateTimeOffset.UtcNow.AddMinutes(1), resource, instance, 42, AgentId, "canonical-script-test");
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcpOperatorScriptEndpoints();
        app.MapScriptEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.Scripts.Add(new ScriptDefinition { Id = 901, Name = "UI source", ScriptType = "bash", Content = "original" });
        await db.SaveChangesAsync();
        return app;
    }

    private sealed class Admission(bool full) : IMcpOperatorRouteAdmission
    {
        public Task<McpOperatorRouteAdmission> EvaluateAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken)
        {
            var access = new McpOperatorAccessRequest(request.Environment, request.Principal, request.TenantId, request.AgentId, null,
                McpOperatorOperationFamily.ScriptsWrite, $"{request.Tool}/{request.Operation}", request.RequiredScopes,
                McpOperatorConfirmationClass.StandardMutation, request.CorrelationId, request.RequestId, Hash("target"),
                McpResource: request.McpResource, McpInstance: request.McpInstance, Tool: request.Tool);
            return Task.FromResult(new McpOperatorRouteAdmission(new McpOperatorDecision(true, null, null, [PolicyId],
                new McpOperatorConstraints(), Hash("target"), access, 1, DevelopmentEnvironmentAccess: full),
                McpOperatorOperationCatalog.Find(request.Tool, request.Operation)));
        }
        public Task<McpOperatorAcceptedAudit> RecordAcceptedAsync(McpOperatorRouteAccessRequest request, CancellationToken cancellationToken) => Task.FromResult(
            new McpOperatorAcceptedAudit(Guid.NewGuid(), PolicyId, request.Environment, request.ServicePrincipal, request.Principal.Subject,
                request.Principal.ClientId, request.Principal.AuthorizedParty, [], [], [], request.McpResource, request.McpInstance,
                request.Tool, request.TenantId, request.AgentId, McpOperatorOperationFamily.ScriptsWrite, $"{request.Tool}/{request.Operation}",
                request.CorrelationId, request.RequestId, DateTimeOffset.UtcNow));
    }
    private sealed class Dispatcher : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => false;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Presence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class Events : IEventRecorder
    {
        public List<DomainEvent> Recorded { get; } = [];
        public Task RecordAsync(DomainEvent domainEvent, CancellationToken ct = default) { Recorded.Add(domainEvent); return Task.CompletedTask; }
    }
    private sealed class Correlation : ICorrelationContext
    {
        public string? Current => "UI-test";
        public string GetOrCreate() => "UI-test";
    }
    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }
}
