using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetRatel.API.Endpoints;
using NetRatel.API.Endpoints.Client;
using NetRatel.API.Gateway;
using NetRatel.API.Services.RemoteSupport;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Observability;
using NetRatel.Application.Presence;
using NetRatel.Application.RemoteSupport;
using NetRatel.Shared.Contracts.FileSystem;
using NetRatel.Shared.Contracts.RemoteSupport;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class GatewayCapabilityEndpointTests
{
    [Fact]
    public async Task FileList_UsesTheAgentIdGatewayRegistry_WhenAuthorityIsActive()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: true, remoteEnabled: false);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync($"/api/v2/agents/3/{agentId:D}/filesystem?path=%2Ftmp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<GatewayFileSystemListResponse>();
        payload!.TenantId.Should().Be(3);
        payload.AgentId.Should().Be(agentId);
        payload.Authority.Should().Be("akka");
        payload.Entries.Should().ContainSingle(entry => entry.Name == "gateway.txt");
    }

    [Fact]
    public async Task FileList_EmitsBoundedAkkaAuthorityMetrics()
    {
        var measurements = new ConcurrentQueue<(string Name, Dictionary<string, object?> Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == NetRatelAkkaTelemetry.MeterName &&
                (instrument.Name == "akka_authority_requests_total" ||
                 instrument.Name == "akka_filebrowser_authority_events_total"))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
        {
            var captured = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var tag in tags)
            {
                captured[tag.Key] = tag.Value;
            }

            measurements.Enqueue((instrument.Name, captured));
        });
        listener.Start();

        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: true, remoteEnabled: false);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync($"/api/v2/agents/3/{agentId:D}/filesystem?path=%2Ftmp");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var capturedMeasurements = measurements.ToArray();
        foreach (var measurement in capturedMeasurements.Where(item =>
                     item.Tags.TryGetValue("feature", out var feature) && Equals(feature, "file-browser")))
        {
            measurement.Tags.Should().Contain(new Dictionary<string, object?>
            {
                ["authority"] = "akka",
                ["migration_phase"] = "authority-cutover",
                ["feature"] = "file-browser",
                ["fallback_used"] = "false",
                ["environment"] = "dev"
            });
        }

        capturedMeasurements.Should().Contain(item => item.Name == "akka_authority_requests_total" &&
            Equals(item.Tags["feature"], "file-browser"));
        capturedMeasurements.Should().Contain(item => item.Name == "akka_filebrowser_authority_events_total" &&
            Equals(item.Tags["feature"], "file-browser"));
    }

    [Theory]
    [InlineData("directory_not_found", HttpStatusCode.NotFound)]
    [InlineData("access_denied", HttpStatusCode.Forbidden)]
    [InlineData("io_failure", HttpStatusCode.Conflict)]
    public async Task FileList_MapsExpectedRemoteFailuresToStructuredProblemDetails(string failureCode, HttpStatusCode expectedStatus)
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: true, remoteEnabled: false);
        app.Services.GetRequiredService<FileRegistry>().FailureCode = failureCode;
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.GetAsync($"/api/v2/agents/3/{agentId:D}/filesystem?path=%2Ftmp");

        response.StatusCode.Should().Be(expectedStatus);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain($"\"code\":\"{failureCode}\"");
    }

    [Fact]
    public async Task RemoteSupportOpen_IsNotExposed_WhenAuthorityIsDisabled()
    {
        using var app = await BuildAppAsync(fileEnabled: false, remoteEnabled: false);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.PostAsJsonAsync($"/api/v2/agents/3/{Guid.NewGuid():D}/remote-support/sessions", new OpenRemoteSupportRequest());

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoteSupportOpen_UsesTheAgentIdGatewayRegistry_WhenAuthorityIsActive()
    {
        var agentId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: false, remoteEnabled: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");

        var response = await client.PostAsJsonAsync($"/api/v2/agents/3/{agentId:D}/remote-support/sessions", new OpenRemoteSupportRequest());

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var payload = await response.Content.ReadFromJsonAsync<GatewayRemoteSupportOpenResponse>();
        payload!.TenantId.Should().Be(3);
        payload.AgentId.Should().Be(agentId);
        payload.Authority.Should().Be("akka-dev-canary");
        app.Services.GetRequiredService<RemoteRegistry>().OpenedClient.Should().Be(new ClientKey(3, agentId));
    }

    [Fact]
    public async Task RemoteSupportV2Events_UsesTheGreatestCursorAndEmitsOnlyDurableLifecycleStatus()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: false, remoteEnabled: true, replicaSafe: true);
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        client.DefaultRequestHeaders.Add("Last-Event-ID", "2");

        var response = await client.GetAsync($"/api/v2/agents/3/{agentId:D}/remote-support/v2/lifecycle/sessions/{sessionId:D}/events?after=1");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/event-stream");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("id: 3\n");
        body.Should().Contain("id: 4\n");
        body.Should().NotContain("id: 2\n");
        body.Should().Contain("event: lifecycle_changed\n");
        body.Should().NotContain("sdp", because: "the SSE adapter only serializes lifecycle/audit contracts");
        app.Services.GetRequiredService<V2LifecycleRouter>().LastSubscribeCursor.Should().Be(2);
        app.Services.GetRequiredService<V2LifecycleRouter>().LastResumeCursor.Should().Be(2);
    }

    [Fact]
    public async Task RemoteSupportV2IceConfiguration_IsIssuedOnlyForTheCurrentReadySession()
    {
        var agentId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();
        using var app = await BuildAppAsync(fileEnabled: false, remoteEnabled: true, replicaSafe: true);
        var client = app.GetTestClient();
        var router = app.Services.GetRequiredService<V2LifecycleRouter>();
        var requestUri = $"/api/v2/agents/3/{agentId:D}/remote-support/v2/lifecycle/sessions/{sessionId:D}/ice-configuration?generation=1";

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Test");
        router.SnapshotState = RemoteSupportV2SessionStates.PreparingTarget;
        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        router.SnapshotState = RemoteSupportV2SessionStates.ReadyForOffer;
        router.SnapshotExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        (await client.GetAsync(requestUri)).StatusCode.Should().Be(HttpStatusCode.NotFound);

        router.SnapshotExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(30);
        var response = await client.GetAsync(requestUri);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("turn:turn.example.invalid:3478?transport=udp");
        body.Should().NotContain("test-shared-secret");
    }

    private static async Task<IHost> BuildAppAsync(bool fileEnabled, bool remoteEnabled, bool replicaSafe = false)
    {
        var options = new NetRatelAkkaMigrationOptions
        {
            Enabled = true,
            PresenceEnabled = true,
            GatewayEnabled = true,
            PresenceAuthorityEnabled = true,
            FileGatewayEnabled = fileEnabled,
            FileBrowseAuthorityEnabled = fileEnabled,
            RemoteSupportGatewayEnabled = remoteEnabled,
            RemoteSupportAuthorityEnabled = remoteEnabled,
            RemoteSupportV2LifecycleAuthorityEnabled = replicaSafe,
            RemoteSupportV2ReplicaSafeEdgeEnabled = replicaSafe,
            RemoteSupportV2InventoryEnabled = replicaSafe,
            RemoteSupportV2MediaEnabled = replicaSafe
        };
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseEnvironment(Environments.Development);
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test").AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(policyOptions =>
                {
                    policyOptions.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser());
                    policyOptions.AddPolicy("FileReader", policy => policy.RequireAuthenticatedUser());
                    policyOptions.AddPolicy("FileWriter", policy => policy.RequireAuthenticatedUser());
                    policyOptions.AddPolicy("RemoteSupportOperator", policy => policy.RequireAuthenticatedUser());
                });
                services.AddSingleton(options);
                services.AddSingleton<FileRegistry>();
                services.AddSingleton<IAgentFileGatewaySessionRegistry>(provider => provider.GetRequiredService<FileRegistry>());
                services.AddSingleton<RemoteRegistry>();
                services.AddSingleton<IGatewayRemoteSupportSessionRegistry>(provider => provider.GetRequiredService<RemoteRegistry>());
                services.AddSingleton<IRemoteSupportIceConfigurationProvider>(new RemoteSupportIceConfigurationProvider(
                    Options.Create(new RemoteSupportIceOptions
                    {
                        Turn = new RemoteSupportTurnOptions
                        {
                            Enabled = true,
                            Urls = ["turn:turn.example.invalid:3478?transport=udp"],
                            Realm = "example.invalid",
                            SharedSecret = "test-shared-secret"
                        }
                    }), TimeProvider.System));
                services.AddSingleton(TimeProvider.System);
                if (replicaSafe)
                {
                    services.AddSingleton<V2LifecycleRouter>();
                    services.AddSingleton<IRemoteSupportLifecycleRouter>(provider => provider.GetRequiredService<V2LifecycleRouter>());
                }
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapAgentFileGatewayEndpoints();
                    endpoints.MapAgentRemoteSupportGatewayEndpoints();
                });
            });
        });
        return await builder.StartAsync();
    }

    private sealed class FileRegistry : IAgentFileGatewaySessionRegistry
    {
        public string? FailureCode { get; set; }

        public AgentFileGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => throw new NotSupportedException();
        public Task<IReadOnlyList<GatewayFileEntry>> ListAsync(ClientKey client, string path, int pageSize, CancellationToken cancellationToken) =>
            FailureCode is { } failureCode
                ? Task.FromException<IReadOnlyList<GatewayFileEntry>>(new AgentFileGatewayOperationException(failureCode))
                : Task.FromResult<IReadOnlyList<GatewayFileEntry>>([new("gateway.txt", "/tmp/gateway.txt", false, 7)]);
        public Task<GatewayFileReadOperation> ReadAsync(ClientKey client, string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task WriteAsync(ClientKey client, string path, Stream source, CancellationToken cancellationToken) => Task.CompletedTask;
        public bool TryAccept(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestAccepted accepted) => false;
        public bool TryAddPage(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileListPage page) => false;
        public Task<bool> TryAddReadChunkAsync(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileTransferChunk chunk, CancellationToken cancellationToken) => Task.FromResult(false);
        public bool TryComplete(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestCompleted completed) => false;
        public bool TryFail(ClientKey client, NetRatel.AgentGateway.Contracts.V1.FileRequestFailed failed) => false;
    }

    private sealed class RemoteRegistry : IGatewayRemoteSupportSessionRegistry
    {
        public ClientKey? OpenedClient { get; private set; }
        public AgentRemoteSupportGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch) => throw new NotSupportedException();
        public Task<GatewayRemoteSupportSession> OpenAsync(ClientKey client, OpenRemoteSupportRequest request, CancellationToken cancellationToken)
        {
            OpenedClient = client;
            return Task.FromResult(new GatewayRemoteSupportSession("session-test", client.TenantId, client.AgentId,
                "open", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "akka-dev-canary", null));
        }
        public Task SendBrowserSignalAsync(string sessionId, RemoteSupportSignalRequest request, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task CloseAsync(string sessionId, string? reason, CancellationToken cancellationToken) => Task.CompletedTask;
        public GatewayRemoteSupportSession? Get(string sessionId) => null;
        public GatewayRemoteSupportSignalSubscription Subscribe(string sessionId) => throw new NotSupportedException();
        public bool TryReceiveAgentSignal(ClientKey client, NetRatel.AgentGateway.Contracts.V1.RemoteSupportSignal signal) => false;
        public bool TryReceiveAgentClose(ClientKey client, NetRatel.AgentGateway.Contracts.V1.RemoteSupportSessionClosed closed) => false;
    }

    private sealed class V2LifecycleRouter : IRemoteSupportLifecycleRouter
    {
        public long? LastSubscribeCursor { get; private set; }
        public long? LastResumeCursor { get; private set; }
        public string SnapshotState { get; set; } = RemoteSupportV2SessionStates.ReadyForOffer;
        public DateTimeOffset? SnapshotExpiresAtUtc { get; set; } = DateTimeOffset.UtcNow.AddMinutes(30);

        public Task<RemoteSupportSessionSnapshot> OpenAsync(RemoteSupportOpenSessionCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteSupportSessionSnapshot?> GetAsync(RemoteSupportSessionKey session, RemoteSupportOperatorBinding operatorBinding, CancellationToken cancellationToken) =>
            Task.FromResult<RemoteSupportSessionSnapshot?>(Snapshot(session));
        public Task<RemoteSupportLifecycleTransitionResult> ControlAsync(RemoteSupportControlCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<RemoteSupportLifecycleTransitionResult> AdvanceAsync(AdvanceRemoteSupportSessionLifecycle command, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<RemoteSupportSessionResume?> ResumeAsync(RemoteSupportResumeRequest request, CancellationToken cancellationToken)
        {
            LastResumeCursor = request.AfterAuditSequence;
            var snapshot = Snapshot(request.Session);
            return Task.FromResult<RemoteSupportSessionResume?>(new(snapshot,
            [
                new(RemoteSupportV2ContractVersions.Current, request.Session, 3, Guid.NewGuid(),
                    RemoteSupportV2AuditEventTypes.LifecycleChanged, "agent", request.Session.AgentId.ToString("D"), Guid.NewGuid(), "accepted", null, DateTimeOffset.UtcNow)
            ]));
        }

        public Task<RemoteSupportBrowserEdgeSubscription?> SubscribeAsync(RemoteSupportSessionKey session, RemoteSupportOperatorBinding operatorBinding, long afterAuditSequence, CancellationToken cancellationToken)
        {
            LastSubscribeCursor = afterAuditSequence;
            var events = Channel.CreateBounded<RemoteSupportBrowserLifecycleEvent>(1);
            events.Writer.TryWrite(new RemoteSupportBrowserLifecycleEvent(4, Snapshot(session), RemoteSupportV2AuditEventTypes.LifecycleChanged));
            events.Writer.TryComplete();
            return Task.FromResult<RemoteSupportBrowserEdgeSubscription?>(new(events.Reader, () => ValueTask.CompletedTask));
        }

        private RemoteSupportSessionSnapshot Snapshot(RemoteSupportSessionKey session) => new(
            RemoteSupportV2ContractVersions.Current, session, Guid.NewGuid(), new RemoteSupportOperatorBinding("test-admin"),
            new(RemoteSupportV2TargetKinds.Console), ["view"], SnapshotState, 3,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, SnapshotExpiresAtUtc);
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "test-admin")], Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}
