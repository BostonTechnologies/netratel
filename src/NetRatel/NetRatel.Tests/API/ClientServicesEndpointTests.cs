using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AwesomeAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Realtime;
using NetRatel.API.Security.Authorization;
using NetRatel.API.Services;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Contracts.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class ClientServicesEndpointTests
{
    private static readonly ClientKey Key = new(7, Guid.Parse("177b268f-b684-4813-92b1-4a182406b8b2"));
    private static string Path => $"/api/v2/agents/{Key.TenantId}/{Key.AgentId:D}/services";

    [Fact]
    public async Task Inventory_read_permission_cannot_admit_service_control_or_read_operation_results()
    {
        var http = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], "Test"));
        var directory = new Directory();
        var request = new ClientServiceControlRequest(Guid.NewGuid(), "Spooler", ClientServicePlatform.Windows, ClientServiceControlAction.Stop);
        var denied = await ClientServicesEndpoints.ControlAsync(Key.TenantId, Key.AgentId, request, http, new Access(), directory, null!, default);
        denied.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        var readDenied = await ClientServicesEndpoints.OperationAsync(Key.TenantId, Key.AgentId, request.OperationId, http, new Access(), directory, null!, default);
        readDenied.Should().BeOfType<Microsoft.AspNetCore.Http.HttpResults.ForbidHttpResult>();
        directory.Reads.Should().Be(0, "target and mutation services are reached only after the manage permission is checked");
    }

    [Fact]
    public async Task Control_http_routes_require_authentication_and_manage_permission_before_resolving_mutation_service()
    {
        using var host = await HostAsync();
        using var http = host.GetTestClient();
        var request = new ClientServiceControlRequest(Guid.NewGuid(), "Spooler", ClientServicePlatform.Windows, ClientServiceControlAction.Stop);
        (await http.PostAsJsonAsync(Path + "/control", request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await http.GetAsync(Path + $"/operations/{request.OperationId:D}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        (await http.PostAsJsonAsync(Path + "/control", request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await http.GetAsync(Path + $"/operations/{request.OperationId:D}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        host.Services.GetRequiredService<Directory>().Reads.Should().Be(0);
        host.Services.GetRequiredService<RecordingRouter>().Reads.Should().Be(0);
    }

    [Fact]
    public async Task CachedOfflineInventoryRequiresTenantAndAgentAuthorizationBeforeDisclosure()
    {
        using var host = await HostAsync();
        var http = host.GetTestClient();
        var router = host.Services.GetRequiredService<RecordingRouter>();
        var directory = host.Services.GetRequiredService<Directory>();
        (await http.GetAsync(Path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        (await http.GetAsync(Path.Replace("/agents/7/", "/agents/8/", StringComparison.Ordinal))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        directory.Reads.Should().Be(0);
        router.Reads.Should().Be(0);
        (await http.GetAsync(Path.Replace(Key.AgentId.ToString("D"), Guid.NewGuid().ToString("D"), StringComparison.Ordinal))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        router.Reads.Should().Be(0);
        var model = await http.GetFromJsonAsync<ClientServicesReadModelDto>(Path);
        model!.LastCompleteInventory!.Services.Should().ContainSingle();
        model.Connected.Should().BeFalse();
        model.SupportsServices.Should().BeFalse();
        model.Revision.Should().Be(7);
        router.PolicyWrites.Should().Be(0, "viewing cached inventory does not collect or start watches");
    }

    [Fact]
    public async Task RefreshIsHonestForOfflineOldClientsAndCoalescesCapableClientRequests()
    {
        using var host = await HostAsync();
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var registry = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>();
        var clock = host.Services.GetRequiredService<Clock>();
        var router = host.Services.GetRequiredService<RecordingRouter>();
        var presence = host.Services.GetRequiredService<Presence>();
        (await Refresh(http)).Status.Should().Be(ClientServicesRefreshStatus.Offline);
        var connection = Guid.NewGuid();
        await using var old = registry.Register(Key, connection, 1, false, "old");
        presence.Set(connection, 1);
        (await Refresh(http)).Status.Should().Be(ClientServicesRefreshStatus.Unsupported);
        await using var current = registry.Register(Key, connection, 2, false, "capable", false, true);
        presence.Set(connection, 2);
        var first = await Refresh(http);
        first.Status.Should().Be(ClientServicesRefreshStatus.Requested);
        var second = await Refresh(http);
        second.Status.Should().Be(ClientServicesRefreshStatus.Throttled);
        second.RequestId.Should().Be(first.RequestId);
        second.RetryAfterUtc.Should().Be(clock.GetUtcNow().Add(ClientServicesLimits.RefreshTimeout));
        router.PolicyWrites.Should().Be(2);
        await using var reader = current.ReadOutboundAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2))).Should().BeTrue();
        reader.Current.ServiceWatchPolicy.RefreshRequestId.Should().Be(first.RequestId!.Value.ToString("D"));
        reader.Current.ServiceWatchPolicy.ServiceNames.Should().BeEmpty();
        var issued = reader.Current.ServiceWatchPolicy;
        ClientServiceContractValidator.IsValidPolicy(new(issued.Revision, issued.ServiceNames.ToArray(),
            issued.WatchIntervalSeconds, issued.InventoryIntervalSeconds, issued.ExpiresAtUtc.ToDateTimeOffset(), first.RequestId),
            clock.GetUtcNow().AddSeconds(-30)).Should().BeTrue("a server-issued policy must survive bounded client clock lag");
        clock.Now = clock.Now.Add(ClientServicesLimits.MinimumRefreshInterval);
        var pending = await Refresh(http);
        pending.Status.Should().Be(ClientServicesRefreshStatus.Throttled);
        pending.RequestId.Should().Be(first.RequestId);
        clock.Now = clock.Now.Add(ClientServicesLimits.RefreshTimeout);
        var next = await Refresh(http);
        next.Status.Should().Be(ClientServicesRefreshStatus.Requested);
        next.RequestId!.Value.Should().NotBe(first.RequestId!.Value);
    }

    [Fact]
    public async Task RefreshOutcomeReadIsAuthorized_AndReconnectRetainsAnOfflineTerminalRecord()
    {
        using var host = await HostAsync();
        var http = host.GetTestClient();
        var registry = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>();
        var presence = host.Services.GetRequiredService<Presence>();
        var connection = Guid.NewGuid();
        await using var session = registry.Register(Key, connection, 4, false, "correlated", false, true, true);
        presence.Set(connection, 4);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var refresh = await Refresh(http);
        refresh.CorrelationSupported.Should().BeTrue();
        var path = Path + $"/refresh/{refresh.RequestId:D}";
        var pending = await http.GetFromJsonAsync<ClientServicesRefreshRequestDto>(path);
        pending!.RequestId.Should().Be(refresh.RequestId!.Value);
        pending.Outcome.Should().Be(ClientServicesRefreshOutcome.Pending);
        (await http.GetAsync(path.Replace("/agents/7/", "/agents/8/", StringComparison.Ordinal))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        http.DefaultRequestHeaders.Authorization = null;
        (await http.GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        await session.DisposeAsync();
        (await http.GetFromJsonAsync<ClientServicesRefreshRequestDto>(path))!.Outcome.Should().Be(ClientServicesRefreshOutcome.Pending,
            "a replica without the telemetry child cannot declare a still-authoritative presence offline");
        var replacementConnection = Guid.NewGuid();
        await using var replacement = registry.Register(Key, replacementConnection, 5, false, "correlated", false, true, true);
        presence.Set(replacementConnection, 5);
        var terminal = await http.GetFromJsonAsync<ClientServicesRefreshRequestDto>(path);
        terminal!.Outcome.Should().Be(ClientServicesRefreshOutcome.Offline);
        terminal.CollectionId.Should().BeNull();
        terminal.RegistrationId.Should().Be(session.RegistrationId);
    }

    [Theory]
    [InlineData("offline")]
    [InlineData("epoch")]
    [InlineData("connection")]
    public async Task RegisteredTelemetryChildCannotClaimOnlineOrRefreshWhenPresenceFenceDisagrees(string defect)
    {
        using var host = await HostAsync();
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var registry = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>();
        var presence = host.Services.GetRequiredService<Presence>();
        var connection = Guid.NewGuid();
        await using var session = registry.Register(Key, connection, 5, false, "capable", false, true);
        presence.Set(connection, 5);
        if (defect == "offline") presence.Snapshot = presence.Snapshot with { Status = ClientPresenceStatus.Offline };
        if (defect == "epoch") presence.Snapshot = presence.Snapshot with { ConnectionEpoch = 6 };
        if (defect == "connection") presence.Snapshot = presence.Snapshot with { ConnectionId = Guid.NewGuid() };
        var model = await http.GetFromJsonAsync<ClientServicesReadModelDto>(Path);
        model!.Connected.Should().BeFalse();
        model.LastCompleteInventory.Should().NotBeNull();
        (await Refresh(http)).Status.Should().Be(ClientServicesRefreshStatus.Offline);
        host.Services.GetRequiredService<RecordingRouter>().PolicyWrites.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshCapturedBeforeReconnectNeverQueuesToReplacementTelemetryRegistration(bool advancePresence)
    {
        using var host = await HostAsync();
        var registry = host.Services.GetRequiredService<IAgentTelemetryGatewaySessionRegistry>();
        var presence = host.Services.GetRequiredService<Presence>();
        var router = host.Services.GetRequiredService<RecordingRouter>();
        var coordinator = host.Services.GetRequiredService<ClientServicesCoordinator>();
        var connection = Guid.NewGuid();
        presence.Set(connection, 5);
        await using var old = registry.Register(Key, connection, 5, false, "old", false, true);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        router.BeforePolicyUpdate = async () => { entered.TrySetResult(); await release.Task; };
        var pending = coordinator.RefreshAsync(Key, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var replacementConnection = advancePresence ? Guid.NewGuid() : connection;
        var replacementEpoch = advancePresence ? 6UL : 5UL;
        await using var replacement = registry.Register(Key, replacementConnection, replacementEpoch, false, "new", false, true);
        if (advancePresence) presence.Set(replacementConnection, (long)replacementEpoch);
        release.TrySetResult();
        (await pending.WaitAsync(TimeSpan.FromSeconds(3))).Status.Should().Be(ClientServicesRefreshStatus.Offline);
        old.CompletionToken.IsCancellationRequested.Should().BeTrue();
        registry.TryPublishServicesPolicy(Key, new(1, [], 30, 900, DateTimeOffset.UtcNow.AddMinutes(1))).Should().BeTrue();
        await using var reader = replacement.ReadOutboundAsync(CancellationToken.None).GetAsyncEnumerator();
        (await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))).Should().BeTrue();
        reader.Current.ServiceWatchPolicy.RefreshRequestId.Should().BeEmpty("the pre-reconnect refresh cannot target a replacement child");
    }

    [Fact]
    public async Task ViewerSseEmitsSameSequenceAssemblyFailureAndDisposesBoundedLease()
    {
        using var host = await HostAsync();
        var http = host.GetTestClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        var router = host.Services.GetRequiredService<RecordingRouter>();
        router.State = router.State with
        {
            LatestAttempt = new(Guid.NewGuid(), ServiceSnapshotKind.Inventory, ServiceCollectionStatus.Partial,
                1, 2, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 1)
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var response = await http.GetAsync(Path + "/events", HttpCompletionOption.ResponseHeadersRead, cancel.Token);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var stream = await response.Content.ReadAsStreamAsync(cancel.Token);
        using var reader = new StreamReader(stream);
        (await reader.ReadLineAsync(cancel.Token)).Should().Be("event: services");
        var first = await reader.ReadLineAsync(cancel.Token);
        first.Should().Contain("\"revision\":7");
        router.State = router.State with
        {
            Revision = 8,
            LatestAttempt = router.State.LatestAttempt! with { Status = ServiceCollectionStatus.Error, ErrorCode = "assembly_timeout" }
        };
        string? line;
        do { line = await reader.ReadLineAsync(cancel.Token); } while (line is not null && !line.Contains("assembly_timeout", StringComparison.Ordinal));
        line.Should().Contain("\"revision\":8");
        router.PolicyWrites.Should().Be(0);
        cancel.Cancel();

        var coordinator = host.Services.GetRequiredService<ClientServicesCoordinator>();
        var leases = Enumerable.Range(0, 4).Select(_ => coordinator.TryAcquireViewer(new ClientKey(7, Guid.NewGuid()))).ToArray();
        leases.Should().OnlyContain(lease => lease != null);
        foreach (var lease in leases) lease!.Dispose();
    }

    [Fact]
    public async Task ViewerAdmissionIsBoundedPerClientAndLeaseDisposalRestoresCapacity()
    {
        using var host = await HostAsync();
        var coordinator = host.Services.GetRequiredService<ClientServicesCoordinator>();
        var leases = Enumerable.Range(0, 4).Select(_ => coordinator.TryAcquireViewer(Key)).ToArray();
        leases.Should().OnlyContain(lease => lease != null);
        coordinator.TryAcquireViewer(Key).Should().BeNull();
        leases[0]!.Dispose();
        leases[0]!.Dispose();
        using var replacement = coordinator.TryAcquireViewer(Key);
        replacement.Should().NotBeNull();
        foreach (var lease in leases) lease!.Dispose();
    }

    private static async Task<ClientServicesRefreshResponse> Refresh(HttpClient client)
    {
        using var result = await client.PostAsync(Path + "/refresh", null);
        result.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await result.Content.ReadFromJsonAsync<ClientServicesRefreshResponse>())!;
    }

    private static async Task<IHost> HostAsync()
    {
        var builder = Host.CreateDefaultBuilder().ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, Authentication>("Test", _ => { });
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("TelemetryReader", policy => policy.RequireAuthenticatedUser()
                        .AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.TelemetryRead)));
                    options.AddPolicy("ClientManager", policy => policy.RequireAuthenticatedUser()
                        .AddRequirements(new EffectiveAccessRequirement(NetRatelPermissions.ClientManagement)));
                });
                services.AddSingleton<IAuthorizationHandler, EffectiveAccessHandler>();
                services.AddSingleton<Clock>();
                services.AddSingleton<TimeProvider>(provider => provider.GetRequiredService<Clock>());
                services.AddSingleton<RecordingRouter>();
                services.AddSingleton<IClientServicesRouter>(provider => provider.GetRequiredService<RecordingRouter>());
                services.AddSingleton<Presence>();
                services.AddSingleton<IClientPresenceRouter>(provider => provider.GetRequiredService<Presence>());
                services.AddSingleton<Directory>();
                services.AddSingleton<IAgentManagementService>(provider => provider.GetRequiredService<Directory>());
                services.AddSingleton<IEffectiveAccessService, Access>();
                services.AddSingleton<ITelemetryInteractiveDemandRegistry>(provider => new TelemetryInteractiveDemandRegistry(
                    provider.GetRequiredService<TimeProvider>(), new ConfigurationBuilder().Build()));
                services.AddSingleton<IGatewayTelemetryLiveRegistry, GatewayTelemetryLiveRegistry>();
                services.AddSingleton<IAgentTelemetryGatewaySessionRegistry, AgentTelemetryGatewaySessionRegistry>();
                services.AddSingleton<IClientServiceWatchPolicySource, EmptyClientServiceWatchPolicySource>();
                services.AddSingleton<ClientServicesCoordinator>();
            });
            web.Configure(app =>
            {
                app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapClientServicesEndpoints());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Presence : IClientPresenceRouter
    {
        public ClientPresenceSnapshot Snapshot { get; set; } = new(Key, ClientPresenceStatus.Offline, null, null, 0, null, "test", [], null, "akka", true);
        public void Set(Guid connection, long epoch) => Snapshot = Snapshot with { Status = ClientPresenceStatus.Online, ConnectionId = connection, ConnectionEpoch = epoch };
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken) => Task.FromResult(Snapshot);
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Authentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(
            Request.Headers.Authorization.ToString() == Scheme.Name
                ? AuthenticateResult.Success(new(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "reader")], Scheme.Name)), Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class Access : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(tenantId == Key.TenantId && permission == NetRatelPermissions.TelemetryRead);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EffectiveAccessSnapshot("reader", false, false, tenantId == Key.TenantId
                ? new HashSet<string>(StringComparer.Ordinal) { NetRatelPermissions.TelemetryRead }
                : new HashSet<string>(StringComparer.Ordinal)));
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class Directory : IAgentManagementService
    {
        public int Reads { get; private set; }
        public Task<AgentDetailDto?> GetAsync(int tenantId, Guid agentId, CancellationToken ct)
        {
            Reads++;
            return Task.FromResult<AgentDetailDto?>(tenantId == Key.TenantId && agentId == Key.AgentId
                ? new(tenantId, agentId, "Synthetic", true, null, DateTimeOffset.UtcNow, "test", null, null, null) : null);
        }
        public Task<AgentListResponse> ListAsync(int tenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAsync(int tenantId, Guid agentId, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(int tenantId, Guid agentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class RecordingRouter(Clock clock) : IClientServicesRouter
    {
        public int Reads { get; private set; }
        public int PolicyWrites { get; private set; }
        public Func<Task>? BeforePolicyUpdate { get; set; }
        public ClientServicesState State { get; set; } = new(Key, 1, 2, 7,
            new(Guid.NewGuid(), 1, 2, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(-1),
                [new("synthetic-service", "Synthetic", ClientServicePlatform.Windows, ClientServiceState.Stopped, "Stopped", "Automatic", null, null, null, null, DateTimeOffset.UtcNow.AddHours(-1))]),
            null, [], [], 0);
        public Task<ClientServicesState> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            Reads++;
            State = ClientServicesRefreshLifecycle.Expire(State, clock.GetUtcNow());
            return Task.FromResult(State);
        }
        public Task<ClientServicesState> UpdateRefreshAsync(UpdateClientServicesRefresh message, CancellationToken cancellationToken)
        {
            State = ClientServicesRefreshLifecycle.Update(State, message.Request, clock.GetUtcNow());
            return Task.FromResult(State);
        }
        public async Task<ClientServicesState> UpdateWatchPolicyAsync(ClientServiceWatchPolicy policy, CancellationToken cancellationToken)
        {
            PolicyWrites++;
            if (BeforePolicyUpdate is not null) await BeforePolicyUpdate();
            return State;
        }
        public Task<ClientServicesMessageResult> RecordAsync(RecordClientServicesChunk message, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
