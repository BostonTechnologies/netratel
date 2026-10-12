using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Security.Authorization;
using NetRatel.Application.Commands;
using NetRatel.Application.Presence;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentCommandGatewayReadEndpointTests
{
    private static readonly Guid AgentId = Guid.Parse("d83a5a63-60e6-4a91-b659-46ae1d523bf7");

    [Fact]
    public async Task Script_execute_alone_cannot_dispatch_or_cancel_a_typed_service_operation()
    {
        using var app = await BuildAppAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var commandPath = $"/api/v2/agents/7/{AgentId:D}/commands";
        foreach (var taskType in new[] { "service-control", " SERVICE-CONTROL " })
        {
            var rejected = await client.PostAsJsonAsync(commandPath, new GatewayCommandDispatchRequest(taskType, "{}", 0, null));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejected.Content.ReadAsStringAsync()).Should().Contain("service_control_requires_dedicated_route");
        }
        app.Services.GetRequiredService<TestDispatcher>().Dispatches.Should().Be(0);

        var operationId = Guid.NewGuid();
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        db.ClientServiceOperations.Add(new ClientServiceOperationRecord
        {
            Id = operationId, TenantId = 7, AgentId = AgentId, ServiceName = "Spooler", RequestedBy = "manager",
            Platform = ClientServicePlatform.Windows, Action = ClientServiceControlAction.Restart
        });
        await db.SaveChangesAsync();
        using var session = app.Services.GetRequiredService<IAgentCommandGatewaySessionRegistry>().Register(new ClientKey(7, AgentId), Guid.NewGuid(), 1);
        var cancel = await client.PostAsJsonAsync($"{commandPath}/{operationId:N}/cancel", new GatewayCommandCancelRequest("script operator"));
        cancel.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await cancel.Content.ReadAsStringAsync()).Should().Contain("service_operation_cannot_be_cancelled");
        session.Reader.TryRead(out _).Should().BeFalse();
    }

    [Fact]
    public async Task Get_preserves_tenant_permission_and_agent_fencing_while_reporting_store_failures()
    {
        using var app = await BuildAppAsync();
        var client = app.GetTestClient();
        var requestUri = $"/api/v2/agents/7/{AgentId:D}/commands/cmd-1";

        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var access = app.Services.GetRequiredService<TestEffectiveAccess>();
        var router = app.Services.GetRequiredService<TestCommandRouter>();

        access.Allowed = false;
        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        access.Allowed = true;
        router.State = State(new ClientKey(8, AgentId), source: "akka", isAuthoritative: false);
        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        router.State = State(new ClientKey(7, Guid.NewGuid()), source: "akka", isAuthoritative: false);
        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        router.State = State(new ClientKey(7, AgentId), source: "akka", isAuthoritative: false);
        var historical = await client.GetAsync(requestUri);
        historical.StatusCode.Should().Be(HttpStatusCode.OK);
        var returnedState = await historical.Content.ReadFromJsonAsync<CommandState>();
        returnedState.Should().NotBeNull();
        returnedState!.IsAuthoritative.Should().BeFalse("the field describes persisted historical provenance, not runtime selection");

        router.State = CommandActorUnavailableState();
        var unavailable = await client.GetAsync(requestUri);
        unavailable.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var problem = await unavailable.Content.ReadAsStringAsync();
        problem.Should().Contain("Command state is temporarily unavailable.");
        problem.Should().NotContain("database");
    }

    private static async Task<IHost> BuildAppAsync()
    {
        var database = Guid.NewGuid().ToString("N");
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Production);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("Test", _ => { });
                services.AddAuthorization(options => options.AddPolicy("CommandOperator", policy =>
                {
                    policy.RequireAuthenticatedUser();
                    policy.AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ScriptExecute));
                }));
                services.AddSingleton<TestEffectiveAccess>();
                services.AddSingleton<IEffectiveAccessService>(provider => provider.GetRequiredService<TestEffectiveAccess>());
                services.AddSingleton<Microsoft.AspNetCore.Authorization.IAuthorizationHandler, EffectiveAccessHandler>();
                services.AddSingleton<TestCommandRouter>();
                services.AddSingleton<IClientCommandRouter>(provider => provider.GetRequiredService<TestCommandRouter>());
                services.AddSingleton<TestDispatcher>();
                services.AddSingleton<IAgentCommandAuthorityDispatcher>(provider => provider.GetRequiredService<TestDispatcher>());
                services.AddSingleton<IAgentCommandGatewaySessionRegistry, AgentCommandGatewaySessionRegistry>();
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase(database));
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAgentCommandGatewayEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class TestDispatcher : IAgentCommandAuthorityDispatcher
    {
        public int Dispatches { get; private set; }
        public bool IsAvailable(ClientKey client) => true;
        public Task DispatchAsync(ClientKey client, string commandId, string correlationId, string taskType, string payloadJson, int environment, CancellationToken cancellationToken)
        {
            Dispatches++;
            return Task.CompletedTask;
        }
    }

    private static CommandState State(ClientKey client, string source, bool isAuthoritative) => new(
        new CommandKey(7, "cmd-1"),
        client,
        "correlation-1",
        DateTimeOffset.UtcNow.AddSeconds(-1),
        CommandLifecycleStatus.Completed,
        2,
        2,
        [new(CommandLifecycleStatus.Completed, DateTimeOffset.UtcNow, 2, 2)],
        source,
        isAuthoritative);

    private static CommandState CommandActorUnavailableState() => new(
        new CommandKey(7, "cmd-1"),
        null,
        null,
        null,
        null,
        0,
        0,
        [],
        "akka-persistence-unavailable",
        false);

    private sealed class TestAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!string.Equals(Request.Headers.Authorization.ToString(), Scheme.Name, StringComparison.Ordinal))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "operator-1")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }

    private sealed class TestEffectiveAccess : IEffectiveAccessService
    {
        public bool Allowed { get; set; } = true;

        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Allowed && tenantId == 7 && permission == NetRatelPermissions.ScriptExecute);

        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot(
                principal.FindFirstValue(ClaimTypes.NameIdentifier),
                IsLegacyOperator: false,
                IsInstanceAdministrator: false,
                Allowed && tenantId == 7
                    ? new HashSet<string>([NetRatelPermissions.ScriptExecute], StringComparer.Ordinal)
                    : new HashSet<string>(StringComparer.Ordinal)));

        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class TestCommandRouter : IClientCommandRouter
    {
        public CommandState State { get; set; } = CommandActorUnavailableState();

        public Task<CommandMessageResult> RecordAsync(RecordCommandLifecycleEvent message, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<CommandState> GetStateAsync(CommandKey command, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task<ClientCommandRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ClientCommandRouteStatus(0, 0, 0, 0, 0, DateTimeOffset.UtcNow, "akka", "akka"));
    }
}
