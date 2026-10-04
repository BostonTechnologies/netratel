using System.Security.Claims;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography.X509Certificates;
using Akka.Hosting;
using AwesomeAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.EntityFrameworkCore;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.Presence;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayServiceTests(ITestOutputHelper output)
{
    [Fact]
    [Trait("category", "hosted")]
    public async Task Connect_NativeDuplexRemainsAdmittedAcrossProxyReadDeadline()
    {
        const int tenantId = 75;
        var directAgent = Guid.NewGuid();
        var defaultProxyAgent = Guid.NewGuid();
        var proxyAgent = Guid.NewGuid();
        var functionalAgent = Guid.NewGuid();
        var lateTerminalAgent = Guid.NewGuid();
        var interruptedAgent = Guid.NewGuid();
        var presenceAborters = new ConcurrentDictionary<Guid, Action>();
        using var credentials = new AgentGatewayRenewalTestCredentials();
        var expiry = DateTimeOffset.UtcNow.AddHours(2);
        var directToken = credentials.CreateToken(new(tenantId, directAgent), expiry, DateTimeOffset.UtcNow.AddMinutes(-1));
        var defaultToken = credentials.CreateToken(new(tenantId, defaultProxyAgent), expiry, DateTimeOffset.UtcNow.AddMinutes(-1));
        var proxyToken = credentials.CreateToken(new(tenantId, proxyAgent), expiry, DateTimeOffset.UtcNow.AddMinutes(-1));
        var identities = new Dictionary<string, Guid>
        {
            [directToken] = directAgent,
            [defaultToken] = defaultProxyAgent,
            [proxyToken] = proxyAgent,
            ["functional-agent-catalog-entry"] = functionalAgent,
            ["late-terminal-catalog-entry"] = lateTerminalAgent,
            ["interrupted-agent-catalog-entry"] = interruptedAgent
        };
        var proxyEndpoint = RequiredFixtureSetting("NETRATEL_GATEWAY_PROXY_ENDPOINT");
        var defaultProxyEndpoint = RequiredFixtureSetting("NETRATEL_GATEWAY_PROXY_DEFAULT_ENDPOINT");
        var port = int.Parse(RequiredFixtureSetting("NETRATEL_GATEWAY_TEST_BACKEND_PORT"));
        using var certificate = X509Certificate2.CreateFromPem(File.ReadAllText(
            RequiredFixtureSetting("NETRATEL_GATEWAY_PROXY_CA_PATH")));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(230));
        using var host = await BuildHostAsync(tenantId, directAgent, null, port, identities, credentials, presenceAborters);
        var router = host.Services.GetRequiredService<IClientPresenceRouter>();
        try
        {
            // Existing A/B profiles retain one stream across thirteen heartbeat
            // intervals. The additional probe uses the real terminal/file clients,
            // signed Agent JWT renewal and the same disposable HTTPS route.
            await Task.WhenAll(
                SustainNativeDuplexAsync("direct-h2c", $"http://127.0.0.1:{port}", tenantId,
                    directAgent, directToken, new FixtureRouteHandler(new SocketsHttpHandler { UseProxy = false }), router, deadline.Token),
                SustainNativeDuplexAsync("default-proxy-https", defaultProxyEndpoint, tenantId,
                    defaultProxyAgent, defaultToken, CreateTrustedProxyHandler(certificate), router,
                    deadline.Token, expectReadDeadline: true),
                SustainNativeDuplexAsync("proxy-https", proxyEndpoint, tenantId,
                    proxyAgent, proxyToken, CreateTrustedProxyHandler(certificate), router, deadline.Token),
                AgentGatewayRenewalTerminalTests.RunContinuityAsync(host, proxyEndpoint,
                    () => CreateTrustedProxyHandler(certificate), credentials, tenantId, functionalAgent,
                    TimeSpan.FromSeconds(195), deadline.Token, output.WriteLine),
                GatewayFunctionalInterruptionProbe.RunLateTerminalDefaultDeadlineAsync(host, defaultProxyEndpoint,
                    () => CreateTrustedProxyHandler(certificate), credentials, tenantId, lateTerminalAgent, deadline.Token, output.WriteLine),
                GatewayFunctionalInterruptionProbe.RunControlledInterruptionAsync(host, proxyEndpoint,
                    () => CreateTrustedProxyHandler(certificate), credentials, tenantId, interruptedAgent,
                    () => presenceAborters[interruptedAgent](), deadline.Token, output.WriteLine));
            output.WriteLine("Real terminal retained process, state, registration and generation through two signed JWT renewals; terminal I/O/resize and integrity-checked file probes remained usable across 195 seconds of corrected HTTPS ingress.");
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static string RequiredFixtureSetting(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"{name} is required; run through tools/ci/tests/traefik-gateway-routing.sh.");

    private async Task SustainNativeDuplexAsync(
        string route, string endpoint, int tenantId, Guid agentId, string bearerToken,
        HttpMessageHandler handler, IClientPresenceRouter router, CancellationToken cancellationToken,
        bool expectReadDeadline = false)
    {
        using var channel = GrpcChannel.ForAddress(endpoint,
            new GrpcChannelOptions { HttpHandler = handler, DisposeHttpClient = true });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        using var call = client.Connect(new Metadata
        {
            { "authorization", $"Bearer {bearerToken}" },
            { "x-netratel-real-gateway", "1" }
        }, cancellationToken: cancellationToken);
        var helloOperation = Guid.NewGuid().ToString("D");
        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0", TenantId = tenantId, ClientId = agentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"), OperationId = helloOperation,
            Hello = new ConnectHello { AgentVersion = "transport-test" }
        });
        (await call.ResponseStream.MoveNext(cancellationToken)).Should().BeTrue();
        var connected = call.ResponseStream.Current;
        connected.PayloadCase.Should().Be(GatewayFrame.PayloadOneofCase.Connected);
        connected.OperationId.Should().Be(helloOperation);
        connected.Connected.PresenceAuthority.Should().Be("akka");
        connected.Connected.HeartbeatIntervalSeconds.Should().Be(15);
        connected.ConnectionEpoch.Should().Be(1);
        var started = Stopwatch.GetTimestamp();
        double? previousRoundTrip = null;
        ulong acknowledged = 0;
        using var heartbeats = new PeriodicTimer(TimeSpan.FromSeconds(15));
        try
        {
            for (ulong sequence = 1; sequence <= (expectReadDeadline ? 7UL : 14UL); sequence++)
            {
                if (sequence > 1)
                    (await heartbeats.WaitForNextTickAsync(cancellationToken)).Should().BeTrue();
                var operation = Guid.NewGuid().ToString("D");
                var payload = new PresenceHeartbeat();
                if (previousRoundTrip is { } milliseconds)
                {
                    payload.AcknowledgedHeartbeatSequence = sequence - 1;
                    payload.AcknowledgedHeartbeatRoundTripMs = milliseconds;
                }
                var sent = Stopwatch.GetTimestamp();
                await call.RequestStream.WriteAsync(new AgentFrame
                {
                    ProtocolVersion = "1.0", TenantId = tenantId, ClientId = agentId.ToString("D"),
                    ConnectionId = connected.ConnectionId, ConnectionEpoch = connected.ConnectionEpoch,
                    OperationId = operation, Sequence = sequence, Heartbeat = payload
                });
                (await call.ResponseStream.MoveNext(cancellationToken)).Should().BeTrue();
                previousRoundTrip = Stopwatch.GetElapsedTime(sent).TotalMilliseconds;
                var acknowledgement = call.ResponseStream.Current;
                acknowledgement.PayloadCase.Should().Be(GatewayFrame.PayloadOneofCase.HeartbeatAccepted);
                acknowledgement.ConnectionId.Should().Be(connected.ConnectionId);
                acknowledgement.ConnectionEpoch.Should().Be(connected.ConnectionEpoch);
                acknowledgement.ClientId.Should().Be(agentId.ToString("D"));
                acknowledgement.TenantId.Should().Be(tenantId);
                acknowledgement.OperationId.Should().Be(operation);
                acknowledgement.Sequence.Should().Be(sequence);
                acknowledgement.HeartbeatAccepted.Duplicate.Should().BeFalse();
                acknowledgement.HeartbeatAccepted.PresenceAuthority.Should().Be("akka");
                acknowledged = sequence;
            }
        }
        catch (Exception exception) when (expectReadDeadline && exception is (RpcException or IOException or HttpRequestException))
        {
            var age = Stopwatch.GetElapsedTime(started).TotalSeconds;
            age.Should().BeInRange(55, 85);
            acknowledged.Should().BeGreaterThanOrEqualTo(3);
            if (exception is RpcException rpc)
                rpc.StatusCode.Should().BeOneOf(StatusCode.Internal, StatusCode.Unavailable, StatusCode.Cancelled, StatusCode.Unknown);
            var status = exception is RpcException failure ? failure.StatusCode.ToString() : "transport";
            output.WriteLine($"route={route} connectionId={connected.ConnectionId} epoch={connected.ConnectionEpoch} ageSeconds={age:F1} heartbeatAcks={acknowledged} httpVersion=2 failureType={exception.GetType().Name} grpcStatus={status}");
            return;
        }
        expectReadDeadline.Should().BeFalse("the default proxy must expire the unfinished native duplex request body near 60 seconds");
        var elapsed = Stopwatch.GetElapsedTime(started);
        elapsed.Should().BeGreaterThan(TimeSpan.FromSeconds(185));
        var snapshot = await router.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken);
        snapshot.Status.Should().Be(ClientPresenceStatus.Online);
        snapshot.LastAcceptedSequence.Should().Be(14);
        snapshot.ConnectionId.Should().Be(Guid.Parse(connected.ConnectionId));
        snapshot.LatencyMilliseconds.Should().BeGreaterThanOrEqualTo(0);
        snapshot.Source.Should().Be("akka");
        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(cancellationToken)).Should().BeFalse();
        (await router.GetSnapshotAsync(new ClientKey(tenantId, agentId), cancellationToken))
            .Status.Should().Be(ClientPresenceStatus.Offline);
        output.WriteLine($"route={route} connectionId={connected.ConnectionId} epoch={connected.ConnectionEpoch} ageSeconds={elapsed.TotalSeconds:F1} heartbeatAcks=14 streamEnded=graceful");
    }

    private static HttpMessageHandler CreateTrustedProxyHandler(X509Certificate2 root)
    {
        var policy = new X509ChainPolicy
        {
            TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck,
            VerificationFlags = X509VerificationFlags.NoFlag
        };
        policy.CustomTrustStore.Add(root);
        return new FixtureRouteHandler(new SocketsHttpHandler
        {
            UseProxy = false,
            EnableMultipleHttp2Connections = true,
            SslOptions = { CertificateChainPolicy = policy }
        });
    }

    private sealed class FixtureRouteHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!request.Headers.Contains("x-netratel-real-gateway"))
                request.Headers.TryAddWithoutValidation("x-netratel-real-gateway", "1");
            var response = await base.SendAsync(request, cancellationToken);
            response.Version.Should().Be(HttpVersion.Version20);
            return response;
        }
    }

    [Fact]
    public async Task Connect_AdmitsAuthenticatedAgentAndReportsNormalPresenceAuthority()
    {
        const int tenantId = 73;
        var agentId = Guid.NewGuid();
        var helloOperationId = Guid.NewGuid();
        var heartbeatOperationId = Guid.NewGuid();
        var router = new RecordingPresenceRouter();
        var legacySpacetimeIdentity = new string('a', 64);

        using var host = await BuildHostAsync(
            tenantId,
            agentId,
            router);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        using var call = client.Connect();

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"),
            OperationId = helloOperationId.ToString("D"),
            Hello = new ConnectHello
            {
                AgentVersion = "phase1-test",
                LegacySpacetimeIdentity = legacySpacetimeIdentity
            }
        });

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var connected = call.ResponseStream.Current;
        connected.Connected.PresenceAuthority.Should().Be("akka");
        connected.ConnectionEpoch.Should().Be(1);
        Guid.Parse(connected.ConnectionId).Should().NotBeEmpty();

        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionEpoch = connected.ConnectionEpoch,
            ConnectionId = connected.ConnectionId,
            OperationId = heartbeatOperationId.ToString("D"),
            Sequence = 1,
            Heartbeat = new PresenceHeartbeat()
        });

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var heartbeat = call.ResponseStream.Current;
        heartbeat.Sequence.Should().Be(1);
        heartbeat.HeartbeatAccepted.Duplicate.Should().BeFalse();
        heartbeat.HeartbeatAccepted.PresenceAuthority.Should().Be("akka");

        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();

        router.Started.Should().NotBeNull();
        router.Started!.Client.Should().Be(new ClientKey(tenantId, agentId));
        router.Started.OperationId.Should().Be(helloOperationId);
        router.Started.LegacySpacetimeIdentity.Should().Be(legacySpacetimeIdentity);
        router.Heartbeat.Should().NotBeNull();
        router.Heartbeat!.OperationId.Should().Be(heartbeatOperationId);
        router.Ended.Should().NotBeNull();
        router.Ended!.Reason.Should().Be("stream_closed");
    }

    [Theory]
    [InlineData(18.75, 1UL, true)]
    [InlineData(0, 1UL, true)]
    [InlineData(18.75, 0UL, false)]
    [InlineData(18.75, 2UL, false)]
    [InlineData(-1, 1UL, false)]
    [InlineData(double.NaN, 1UL, false)]
    [InlineData(double.PositiveInfinity, 1UL, false)]
    [InlineData(double.MaxValue, 1UL, false)]
    public async Task Connect_ProjectsOnlyBoundedLatencyOfAnAcknowledgedHeartbeat(
        double milliseconds, ulong acknowledgedSequence, bool validSample)
    {
        const int tenantId = 74;
        var agentId = Guid.NewGuid();
        var router = new RecordingPresenceRouter();
        using var host = await BuildHostAsync(tenantId, agentId, router);
        using var channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var gateway = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        using var call = gateway.Connect();
        await call.RequestStream.WriteAsync(new AgentFrame
        {
            ProtocolVersion = "1.0", TenantId = tenantId, ClientId = agentId.ToString("D"),
            ConnectionId = Guid.NewGuid().ToString("D"), OperationId = Guid.NewGuid().ToString("D"),
            Hello = new ConnectHello { AgentVersion = "latency-test" }
        });
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        var connected = call.ResponseStream.Current;

        AgentFrame Heartbeat(ulong sequence, PresenceHeartbeat payload) => new()
        {
            ProtocolVersion = "1.0", TenantId = tenantId, ClientId = agentId.ToString("D"),
            ConnectionId = connected.ConnectionId, ConnectionEpoch = connected.ConnectionEpoch,
            OperationId = Guid.NewGuid().ToString("D"), Sequence = sequence, Heartbeat = payload
        };

        // There is no previous heartbeat ACK on this stream yet.
        await call.RequestStream.WriteAsync(Heartbeat(1, new PresenceHeartbeat
        {
            AcknowledgedHeartbeatSequence = 1, AcknowledgedHeartbeatRoundTripMs = 5
        }));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        router.Heartbeat!.HeartbeatRoundTripMilliseconds.Should().BeNull();
        router.Heartbeat.LatencyMeasuredAtUtc.Should().BeNull();

        await call.RequestStream.WriteAsync(Heartbeat(2, new PresenceHeartbeat
        {
            AcknowledgedHeartbeatSequence = acknowledgedSequence,
            AcknowledgedHeartbeatRoundTripMs = milliseconds
        }));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.HeartbeatAccepted.PresenceAuthority.Should().Be("akka");
        router.Heartbeat!.HeartbeatRoundTripMilliseconds.Should().Be(validSample ? milliseconds : null);
        if (validSample)
        {
            router.Heartbeat.LatencyMeasuredAtUtc.Should().NotBeNull();
            router.Heartbeat.LatencyMeasuredAtUtc!.Value.Should().BeOnOrBefore(router.Heartbeat.ReceivedAtUtc);
        }
        else
        {
            router.Heartbeat.LatencyMeasuredAtUtc.Should().BeNull();
        }

        await call.RequestStream.CompleteAsync();
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeFalse();
    }

    private static async Task<IHost> BuildHostAsync(
        int tenantId,
        Guid agentId,
        IClientPresenceRouter? router,
        int? networkPort = null,
        IReadOnlyDictionary<string, Guid>? bearerAgents = null,
        AgentGatewayRenewalTestCredentials? renewalCredentials = null,
        ConcurrentDictionary<Guid, Action>? presenceAborters = null)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            if (networkPort is { } port)
                web.UseKestrel(options => options.Listen(IPAddress.Any, port,
                    listen => listen.Protocols = HttpProtocols.Http2));
            else
                web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options =>
                {
                    options.AddPolicy("AgentGatewayAccess", policy =>
                        policy.RequireAssertion(context =>
                            AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _)));
                });
                services.AddGrpc();
                var akkaOptions = new NetRatelAkkaOptions();
                if (router is not null)
                    services.AddSingleton(router);
                else
                {
                    services.AddAkka($"gateway-transport-{Guid.NewGuid():N}", (akka, serviceProvider) =>
                        akka.WithActors((system, registry, _) =>
                        {
                            var readModel = system.ActorOf(PresenceReadModelActor.Props(), "presence-read-model");
                            registry.Register<ClientPresenceRegion>(system.ActorOf(
                                ClientPresenceRouterActor.Props(akkaOptions, readModel), "presence"));
                        }));
                    services.AddSingleton<IClientPresenceRouter>(provider => new AkkaClientPresenceRouter(
                        provider.GetRequiredService<IRequiredActor<ClientPresenceRegion>>(), akkaOptions.AskTimeout));
                }
                services.AddSingleton<IAgentManagementService>(
                    new ActiveAgentManagementService(tenantId, agentId, bearerAgents?.Values.ToHashSet()));
                services.AddSingleton(akkaOptions);
                services.AddSingleton(TimeProvider.System);
                services.AddDbContext<OrchestratorDbContext>(options => options.UseInMemoryDatabase($"gateway-{Guid.NewGuid():N}"));
                services.AddSingleton<IClientUpdateCatalog, EmptyClientUpdateCatalog>();
                services.AddScoped<ClientUpdateAuthorityService>();
                services.AddScoped<IClientUpdateActivationAuthority>(serviceProvider =>
                    serviceProvider.GetRequiredService<ClientUpdateAuthorityService>());
                services.AddSingleton(NullLogger<AgentGatewayService>.Instance);
                if (renewalCredentials is not null)
                    AgentGatewayRenewalTerminalTests.AddTerminalAndRenewalServices(services, renewalCredentials);
            });

            web.Configure(app =>
            {
                if (renewalCredentials is not null) app.UseAuthentication();
                app.Use(async (context, next) =>
                {
                    var authenticatedAgent = agentId;
                    if (bearerAgents is not null && !bearerAgents.TryGetValue(
                            context.Request.Headers.Authorization.ToString().Replace("Bearer ", "", StringComparison.Ordinal),
                            out authenticatedAgent))
                    {
                        if (renewalCredentials is not null &&
                            AgentGatewayIdentityResolver.TryResolve(context.User, out var signedIdentity, out _) &&
                            signedIdentity is not null && signedIdentity.TenantId == tenantId &&
                            bearerAgents.Values.Contains(signedIdentity.AgentId))
                        {
                            if (context.Request.Path == "/netratel.gateway.v1.AgentGateway/Connect")
                                presenceAborters?.AddOrUpdate(signedIdentity.AgentId,
                                    _ => context.Abort, (_, _) => context.Abort);
                            await next(context);
                            return;
                        }
                        context.Response.StatusCode = 401;
                        return;
                    }
                    // Network coverage uses fixed fixture bearer identities; production
                    // JWT issuance, enrollment and operating-system installation are separate gates.
                    var id = authenticatedAgent.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [
                        new Claim("role", "agent"),
                        new Claim("sub", id),
                        new Claim("agent_id", id),
                        new Claim("tenant_id", tenantId.ToString()),
                        new Claim("scope", "netratel:connect")
                    ], "Phase1Test"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<AgentGatewayService>();
                    if (renewalCredentials is not null)
                    {
                        endpoints.MapGrpcService<AgentTerminalGatewayService>();
                        endpoints.MapGrpcService<AgentFileGatewayService>();
                    }
                });
            });
        });

        return await builder.StartAsync();
    }

    private sealed class EmptyClientUpdateCatalog : IClientUpdateCatalog
    {
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPresenceRouter : IClientPresenceRouter
    {
        public StartGatewayPresenceSession? Started { get; private set; }

        public RecordGatewayHeartbeat? Heartbeat { get; private set; }

        public EndGatewayPresenceSession? Ended { get; private set; }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(
            StartGatewayPresenceSession message,
            CancellationToken cancellationToken)
        {
            Started = message;
            return Task.FromResult(new GatewayPresenceSessionStarted(
                message.Client,
                message.ConnectionId,
                ConnectionEpoch: 1,
                PresenceMessageDisposition.Accepted,
                message.ReceivedAtUtc));
        }

        public Task<PresenceMessageResult> RecordHeartbeatAsync(
            RecordGatewayHeartbeat message,
            CancellationToken cancellationToken)
        {
            Heartbeat = message;
            return Task.FromResult(new PresenceMessageResult(
                message.Client,
                message.ConnectionEpoch,
                PresenceMessageDisposition.Accepted,
                message.Sequence));
        }

        public Task<PresenceMessageResult> EndSessionAsync(
            EndGatewayPresenceSession message,
            CancellationToken cancellationToken)
        {
            Ended = message;
            return Task.FromResult(new PresenceMessageResult(
                message.Client,
                message.ConnectionEpoch,
                PresenceMessageDisposition.Accepted,
                Heartbeat?.Sequence ?? 0));
        }

        public Task<ClientPresenceSnapshot> GetSnapshotAsync(
            ClientKey client,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class ActiveAgentManagementService(int tenantId, Guid agentId, IReadOnlySet<Guid>? agents = null) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken ct)
        {
            if (requestedTenantId != tenantId || !(agents?.Contains(requestedAgentId) ?? requestedAgentId == agentId))
            {
                return Task.FromResult<AgentDetailDto?>(null);
            }

            return Task.FromResult<AgentDetailDto?>(new AgentDetailDto(
                tenantId,
                requestedAgentId,
                "Phase 1 test agent",
                IsEnabled: true,
                DisabledReason: null,
                DateTimeOffset.UtcNow,
                CreatedBy: "test",
                LastSeenAtUtc: null,
                LastTokenIssuedAtUtc: null,
                RevokedAtUtc: null));
        }

        public Task<AgentListResponse> ListAsync(int requestedTenantId, AgentListQuery query, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DisableAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task EnableAsync(int requestedTenantId, Guid requestedAgentId, string actor, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task DeleteAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
