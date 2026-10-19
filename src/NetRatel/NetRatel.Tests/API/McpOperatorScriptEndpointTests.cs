using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Middleware;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Events;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Operations;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class McpOperatorScriptEndpointTests
{
    private const string Resource = "https://mcp.example.test/mcp";
    private const string Subject = "script-operator";
    private const string ClientId = "script-client";
    private const string ScriptContent = "printf 'reviewed-marker'";
    private const long ScriptId = 801;
    private static readonly Guid AgentId = Guid.Parse("865d8a10-b2aa-4f11-93c0-a4d2f3a400e0");
    private static string ScriptRoot => $"/api/v2/mcp/operator/agents/42/{AgentId:D}/scripts";

    [Fact]
    public async Task Deleted_script_replays_its_original_version_without_another_write_or_audit()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewDeleteAsync(client);
        var confirmed = await ConfirmDeleteAsync(client, request);
        confirmed.Should().Be(new McpOperatorScriptMutationResult(ScriptId, 2, true, false, "script-correlation"));
        var writes = app.Services.GetRequiredService<MutationWrites>();
        var afterConfirmation = writes.Count;

        var replayed = await ConfirmDeleteAsync(client, request);

        replayed.Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterConfirmation);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
            (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
            (await db.McpOperatorScriptVersions.SingleAsync()).Action.Should().Be("deleted");
            var scripts = scope.ServiceProvider.GetRequiredService<IMcpOperatorScriptStore>();
            (await scripts.GetOwnedAsync(ScriptId, 42, Principal(), Resource, "prod", default)).Should().BeNull();
            (await scripts.ListOwnedAsync(42, Principal(), Resource, "prod", default)).Should().BeEmpty();
            // A receipt retains the deletion's result even if retained metadata
            // later advances; it must not substitute that subsequent version.
            (await db.McpOperatorScripts.SingleAsync()).Version++;
            await db.SaveChangesAsync();
        }
        var afterRetainedChange = writes.Count;

        (await ConfirmDeleteAsync(client, request)).Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(afterRetainedChange);
    }

    [Fact]
    public async Task Changed_payload_and_fresh_competing_delete_cannot_reuse_a_successful_delete()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var stale = await PreviewDeleteAsync(client);
        var winning = await PreviewDeleteAsync(client);
        var confirmed = await ConfirmDeleteAsync(client, winning);
        var writes = app.Services.GetRequiredService<MutationWrites>();
        var beforeAttempts = writes.Count;

        using var changed = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", winning with { ExpectedVersion = 2 });
        changed.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await changed.Content.ReadAsStringAsync()).Should().Contain("confirmation_plan_stale");
        using var staleResponse = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", stale);
        staleResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await staleResponse.Content.ReadAsStringAsync()).Should().Contain("script_not_found");

        writes.Count.Should().Be(beforeAttempts);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorConfirmationPlans.CountAsync(plan => plan.ConsumedAtUtc == null)).Should().Be(1);
        (await db.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(1);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorScriptVersions.CountAsync()).Should().Be(1);
        (await ConfirmDeleteAsync(client, winning)).Should().Be(confirmed with { Replayed = true });
    }

    [Fact]
    public async Task Fresh_delete_with_a_stale_live_revision_does_not_consume_its_plan()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewDeleteAsync(client);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorScripts.SingleAsync()).Version++;
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWrites>();
        var beforeAttempt = writes.Count;

        using var rejected = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", request);

        rejected.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await rejected.Content.ReadAsStringAsync()).Should().Contain("script_etag_mismatch");
        writes.Count.Should().Be(beforeAttempt);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorConfirmationPlans.SingleAsync()).ConsumedAtUtc.Should().BeNull();
        (await verificationDb.McpOperatorIdempotencyRecords.CountAsync()).Should().Be(0);
        (await verificationDb.McpOperatorAcceptedAudits.CountAsync()).Should().Be(0);
        (await verificationDb.McpOperatorScripts.SingleAsync()).DeletedAtUtc.Should().BeNull();
    }

    [Theory]
    [InlineData("policy-version")]
    [InlineData("policy-disabled")]
    [InlineData("expired")]
    [InlineData("scope")]
    [InlineData("subject")]
    [InlineData("client")]
    [InlineData("resource")]
    [InlineData("target")]
    [InlineData("target-version")]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("owner-tenant")]
    [InlineData("content")]
    public async Task Deleted_script_replay_rechecks_current_authority_and_retained_ownership(string change)
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewDeleteAsync(client);
        await ConfirmDeleteAsync(client, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            var caller = app.Services.GetRequiredService<MutationCaller>();
            switch (change)
            {
                case "policy-version": (await db.McpOperatorPolicies.SingleAsync()).Version++; break;
                case "policy-disabled": (await db.McpOperatorPolicies.SingleAsync()).DisabledAtUtc = DateTimeOffset.UtcNow; break;
                case "expired": (await db.McpOperatorConfirmationPlans.SingleAsync()).ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1); break;
                case "scope": caller.Scopes = []; break;
                case "subject": caller.Subject = "other-operator"; break;
                case "client": caller.ClientId = "other-client"; break;
                case "resource": caller.Resource = "https://other.example.test/mcp"; break;
                case "target": caller.AgentId = Guid.NewGuid(); break;
                case "target-version": (await db.McpOperatorTargetProfiles.SingleAsync()).Version++; break;
                case "tenant": caller.TenantId = 43; break;
                case "owner": (await db.McpOperatorScripts.SingleAsync()).Subject = "other-owner"; break;
                case "owner-tenant": (await db.McpOperatorScripts.SingleAsync()).TenantId = 43; break;
                case "content": (await db.Scripts.SingleAsync()).Content = "changed outside the retained revision"; break;
            }
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWrites>();
        var beforeReplay = writes.Count;

        using var replayed = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", request);

        replayed.IsSuccessStatusCode.Should().BeFalse();
        writes.Count.Should().Be(beforeReplay);
        await using var verification = app.Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await verificationDb.McpOperatorScriptVersions.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await verificationDb.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
    }

    [Fact]
    public async Task Legacy_script_id_receipt_replays_the_authorized_tombstone_version()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewDeleteAsync(client);
        var confirmed = await ConfirmDeleteAsync(client, request);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
            (await db.McpOperatorIdempotencyRecords.SingleAsync()).ResultReference = ScriptId.ToString();
            await db.SaveChangesAsync();
        }
        var writes = app.Services.GetRequiredService<MutationWrites>();
        var beforeReplay = writes.Count;

        (await ConfirmDeleteAsync(client, request)).Should().Be(confirmed with { Replayed = true });
        writes.Count.Should().Be(beforeReplay);
    }

    [Fact]
    public async Task Concurrent_retry_and_lost_response_preserve_one_successful_deletion_receipt()
    {
        await using var app = await BuildMutationAppAsync();
        using var client = app.GetTestClient();
        var request = await PreviewDeleteAsync(client);
        var writes = app.Services.GetRequiredService<MutationWrites>();
        writes.PauseReceipt = true;
        using var disconnected = new CancellationTokenSource();
        var confirmation = client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", request, disconnected.Token);
        try
        {
            await writes.ReceiptStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            using var pending = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", request);
            pending.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await pending.Content.ReadAsStringAsync()).Should().Contain("idempotency_pending");
            disconnected.Cancel();
        }
        finally
        {
            writes.AllowReceipt.TrySetResult();
        }
        var lostResponse = async () => await confirmation;
        await lostResponse.Should().ThrowAsync<OperationCanceledException>();
        await writes.ReceiptSaved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var afterConfirmation = writes.Count;

        (await ConfirmDeleteAsync(client, request)).Should().Be(new McpOperatorScriptMutationResult(ScriptId, 2, true, true, "script-correlation"));
        writes.Count.Should().Be(afterConfirmation);
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        (await db.McpOperatorIdempotencyRecords.SingleAsync()).Outcome.Should().Be(McpOperatorIdempotencyOutcome.Succeeded);
        (await db.McpOperatorAcceptedAudits.CountAsync()).Should().Be(1);
        (await db.McpOperatorScriptVersions.CountAsync()).Should().Be(1);
        (await db.McpOperatorScripts.SingleAsync()).Version.Should().Be(2);
    }

    private static async Task<McpOperatorScriptMutationRequest> PreviewDeleteAsync(HttpClient client)
    {
        var request = new McpOperatorScriptMutationRequest(ScriptId: ScriptId, ExpectedVersion: 1);
        using var response = await client.PostAsJsonAsync(ScriptRoot + "/preview/delete", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var preview = (await response.Content.ReadFromJsonAsync<McpOperatorScriptPreview>())!;
        return request with { PlanToken = preview.PlanToken, IdempotencyKey = preview.IdempotencyKey };
    }

    private static async Task<McpOperatorScriptMutationResult> ConfirmDeleteAsync(HttpClient client, McpOperatorScriptMutationRequest request)
    {
        using var response = await client.PostAsJsonAsync(ScriptRoot + "/confirm/delete", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var result = (await response.Content.ReadFromJsonAsync<McpOperatorScriptMutationResult>())!;
        response.Headers.ETag!.Tag.Should().Be($"\"{result.Version}\"");
        return result;
    }

    private static async Task<WebApplication> BuildMutationAppAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, MutationAuthentication>("Test", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy("M2MOnly", policy => policy.RequireAuthenticatedUser()));
        var databaseName = "script-mutation-" + Guid.NewGuid();
        builder.Services.AddSingleton<MutationWrites>();
        builder.Services.AddSingleton<MutationCaller>();
        builder.Services.AddDbContext<OrchestratorDbContext>((provider, options) => options.UseInMemoryDatabase(databaseName)
            .AddInterceptors(provider.GetRequiredService<MutationWrites>()));
        builder.Services.AddScoped<IMcpOperatorScriptStore, McpOperatorScriptStore>();
        builder.Services.AddScoped<IMcpOperatorAuthorization, McpOperatorAuthorization>();
        builder.Services.AddScoped<IMcpOperatorRouteAdmission, McpOperatorRouteAdmissionService>();
        builder.Services.AddScoped<IMcpOperatorConfirmationService, McpOperatorConfirmationService>();
        builder.Services.AddScoped<IMcpOperatorTaskStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IClientPresenceRouter, MutationPresence>();
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher, MutationDispatcher>();
        builder.Services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<McpOperatorLocalAgentOptions>();
        var app = builder.Build();
        app.UseAuthentication();
        // Reuse the endpoint regression layer with the real policy, ownership
        // and confirmation services after trusted delegation ingress.
        app.Use(async (context, next) =>
        {
            var caller = context.RequestServices.GetRequiredService<MutationCaller>();
            context.Items[McpOperatorDelegationMiddleware.HttpContextItemKey] = new McpOperatorDelegation(
                new McpOperatorDelegationIdentity(caller.Subject, caller.ClientId, null, [], ["ScriptOperator"], caller.Scopes),
                "mcp-service", "netratel_scripts", "delete", "script-request", DateTimeOffset.UtcNow.AddMinutes(1),
                caller.Resource, "prod", caller.TenantId, caller.AgentId, "script-correlation");
            await next(context);
        });
        app.UseAuthorization();
        app.MapMcpOperatorScriptEndpoints();
        await app.StartAsync();
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = DateTimeOffset.UtcNow;
        var policyId = Guid.NewGuid();
        db.Agents.Add(new Agent { Id = AgentId, TenantId = 42, IsEnabled = true, Status = AgentStatus.Active, CreatedAtUtc = now });
        db.McpOperatorTargetProfiles.Add(new McpOperatorTargetProfileRecord
        {
            AgentId = AgentId, TenantId = 42, Classification = McpOperatorTargetClassification.ManagedStandard,
            TagsJson = "[]", Version = 1, UpdatedAtUtc = now, UpdatedBy = "independent-admin"
        });
        db.McpOperatorPolicies.Add(new McpOperatorPolicyRecord
        {
            Id = policyId, Name = "owned script mutations", Environment = McpOperatorEnvironment.Production,
            Effect = McpOperatorPolicyEffect.Allow, Priority = 10,
            PrincipalSelectorKind = McpOperatorPrincipalSelectorKind.OAuthSubject, PrincipalSelectorValue = Subject,
            TargetSelectorKind = McpOperatorTargetSelectorKind.ExactAgent, TenantId = 42, AgentId = AgentId,
            TargetClassification = McpOperatorTargetClassification.ManagedStandard, OperationFamily = McpOperatorOperationFamily.ScriptsWrite,
            ConstraintsJson = JsonSerializer.Serialize(new McpOperatorConstraints(AllowedShells: ["sh"], WorkingDirectories: ["/tmp"],
                MaxCommandDurationSeconds: 60, MaxScriptBytes: 4096, MaxTaskTargetCount: 1, MaxFanOut: 1), McpOperatorJsonContext.Default.McpOperatorConstraints),
            CreatedAtUtc = now, CreatedBy = "independent-admin", Version = 1
        });
        db.Scripts.Add(new ScriptDefinition { Id = ScriptId, Name = "owned-script", Description = "marker", ScriptType = "bash", Content = ScriptContent });
        db.McpOperatorScripts.Add(new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(), ScriptId = ScriptId, TenantId = 42, Subject = Subject, ClientId = ClientId,
            McpResource = Resource, McpInstance = "prod", PolicyId = policyId, PolicyVersion = 1,
            Name = "owned-script", Description = "marker", ShellType = "sh", ContentHash = Hash(ScriptContent), ManifestHash = Hash(string.Empty),
            TimeoutSeconds = 15, WorkingDirectory = "/tmp", DeclaredSideEffectsJson = "[1]", Version = 1
        });
        await db.SaveChangesAsync();
        return app;
    }

    private static McpOperatorPrincipal Principal() => new(Subject, ClientId, null, new HashSet<string>(),
        new HashSet<string>(["ScriptOperator"], StringComparer.Ordinal), new HashSet<string>(["netratel.mcp.write"], StringComparer.Ordinal));

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private sealed class MutationCaller
    {
        public string Subject { get; set; } = McpOperatorScriptEndpointTests.Subject;
        public string ClientId { get; set; } = McpOperatorScriptEndpointTests.ClientId;
        public string Resource { get; set; } = McpOperatorScriptEndpointTests.Resource;
        public string[] Scopes { get; set; } = ["netratel.mcp.write"];
        public int TenantId { get; set; } = 42;
        public Guid AgentId { get; set; } = McpOperatorScriptEndpointTests.AgentId;
    }

    private sealed class MutationWrites : SaveChangesInterceptor
    {
        private int _count;
        public int Count => _count;
        public bool PauseReceipt { get; set; }
        public TaskCompletionSource ReceiptStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowReceipt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReceiptSaved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _count);
            if (PauseReceipt && eventData.Context!.ChangeTracker.Entries<McpOperatorIdempotencyRecord>()
                .Any(entry => entry.State == EntityState.Modified && entry.Entity.Outcome == McpOperatorIdempotencyOutcome.Succeeded))
            {
                ReceiptStarted.TrySetResult();
                await AllowReceipt.Task.WaitAsync(cancellationToken);
            }
            return result;
        }

        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            if (PauseReceipt && eventData.Context!.ChangeTracker.Entries<McpOperatorIdempotencyRecord>()
                .Any(entry => entry.Entity.Outcome == McpOperatorIdempotencyOutcome.Succeeded))
                ReceiptSaved.TrySetResult();
            return ValueTask.FromResult(result);
        }
    }

    private sealed class MutationAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "mcp-service")], Scheme.Name)), Scheme.Name)));
    }

    private sealed class MutationPresence : IClientPresenceRouter
    {
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class MutationDispatcher : IAgentCommandAuthorityDispatcher
    {
        public bool IsAvailable(ClientKey client) => throw new NotSupportedException();
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    [Fact]
    public void Production_script_routes_are_explicitly_versioned_and_require_the_mcp_m2m_identity()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<IClientPresenceRouter>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorRouteAdmission>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorConfirmationService>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorScriptStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IMcpOperatorTaskStore>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<IEventRecorder>(_ => throw new NotSupportedException());
        builder.Services.AddSingleton<NetRatelAkkaOptions>();
        builder.Services.AddSingleton(new McpOperatorLocalAgentOptions());
        builder.Services.AddSingleton<IAgentCommandAuthorityDispatcher>(_ => throw new NotSupportedException());
        var app = builder.Build();

        app.MapMcpOperatorScriptEndpoints();

        var routes = ((IEndpointRouteBuilder)app).DataSources.SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.RoutePattern.RawText?.StartsWith("/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts", StringComparison.Ordinal) is true)
            .ToArray();

        routes.Select(route => route.RoutePattern.RawText).Should().BeEquivalentTo(
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/params",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/validate",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/preview/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/confirm/{action}",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/runs/preview",
            "/api/v2/mcp/operator/agents/{tenantId:int}/{agentId:guid}/scripts/{scriptId:long}/runs/confirm");
        routes.Should().OnlyContain(route => route.Metadata.GetOrderedMetadata<IAuthorizeData>()
            .Any(metadata => metadata.Policy == "M2MOnly"));
    }
}
