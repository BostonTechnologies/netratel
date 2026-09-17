using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Core.Interceptors;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.Application.Agents;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentLogGatewayServiceTests
{
    private const string TraceParent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
    private const string TraceState = "netratel=phase1";

    [Fact]
    public async Task Connect_AcceptsCurrentPresenceAndMakesStructuredRecordsAvailable()
    {
        const int tenantId = 91;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        const long connectionEpoch = 6;
        using var host = await BuildHostAsync(tenantId, agentId, connectionId, connectionEpoch, enabled: true);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentLogGateway.AgentLogGatewayClient(channel);
        using var call = client.Connect();
        var operationId = Guid.NewGuid();

        await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connectionId, connectionEpoch, operationId));
        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.Accepted.LogAuthority.Should().Be("akka");
        call.ResponseStream.Current.Traceparent.Should().Be(TraceParent);
        call.ResponseStream.Current.Tracestate.Should().Be(TraceState);

        var batchOperationId = Guid.NewGuid();
        await call.RequestStream.WriteAsync(new AgentLogFrame
        {
            ProtocolVersion = "1.0",
            TenantId = tenantId,
            ClientId = agentId.ToString("D"),
            ConnectionId = connectionId.ToString("D"),
            ConnectionEpoch = (ulong)connectionEpoch,
            OperationId = batchOperationId.ToString("D"),
            Sequence = 1,
            Traceparent = TraceParent,
            Tracestate = TraceState,
            Batch = new AgentLogBatch
            {
                SessionId = connectionId.ToString("D"),
                Records =
                {
                    new AgentLogRecord
                    {
                        Cursor = "record-1",
                        RecordSequence = 1,
                        TimestampUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                        Severity = "Information",
                        SourceId = "netratel-runtime",
                        Category = "Gateway",
                        Message = "same text is an independent event"
                    }
                }
            }
        });

        (await call.ResponseStream.MoveNext(CancellationToken.None)).Should().BeTrue();
        call.ResponseStream.Current.FlowControl.AvailableCredits.Should().BeGreaterThan(0);
        call.ResponseStream.Current.Traceparent.Should().Be(TraceParent);
        call.ResponseStream.Current.Tracestate.Should().Be(TraceState);
        var registry = host.Services.GetRequiredService<IAgentLogGatewaySessionRegistry>();
        var page = registry.Query(new ClientKey(tenantId, agentId), new GatewayLogPageRequest("netratel-runtime", null, 100, null, null, null, null, null));
        page.Records.Should().ContainSingle(record => record.Message == "same text is an independent event" && record.Category == "Gateway");

        await call.RequestStream.CompleteAsync();
    }

    [Fact]
    public async Task Connect_IsRejectedWhenLogAuthorityIsDisabled()
    {
        const int tenantId = 92;
        var agentId = Guid.NewGuid();
        var connectionId = Guid.NewGuid();
        using var host = await BuildHostAsync(tenantId, agentId, connectionId, 1, enabled: false);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentLogGateway.AgentLogGatewayClient(channel);
        using var call = client.Connect();

        var action = async () =>
        {
            await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connectionId, 1, Guid.NewGuid()));
            await call.ResponseStream.MoveNext(CancellationToken.None);
        };
        var exception = await action.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.FailedPrecondition);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_ExactReconnectTerminatesOldRpc(bool queriesEnabled)
    {
        const int tenantId = 93;
        var agentId = Guid.NewGuid();
        var connection = Guid.NewGuid();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = await BuildHostAsync(tenantId, agentId, connection, 5, true, services =>
        {
            if (queriesEnabled) services.AddSingleton<IAgentLogGatewayQueryDispatcher, AgentLogGatewayQueryDispatcher>();
        });
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        var client = new AgentLogGateway.AgentLogGatewayClient(channel);
        using var old = client.Connect(cancellationToken: timeout.Token);
        await old.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        (await old.ResponseStream.MoveNext(timeout.Token)).Should().BeTrue();
        using var current = client.Connect(cancellationToken: timeout.Token);
        await current.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        (await current.ResponseStream.MoveNext(timeout.Token)).Should().BeTrue();
        (await old.ResponseStream.MoveNext(timeout.Token)).Should().BeFalse();
        host.Services.GetRequiredService<IAgentLogGatewaySessionRegistry>().GetSources(new ClientKey(tenantId, agentId)).Should().NotBeEmpty();
        await current.RequestStream.CompleteAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Connect_StaleHelloThatPassedPresenceReadIsAborted(bool ambiguous)
    {
        const int tenantId = 94;
        var agentId = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var firstRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = new CurrentPresenceRouter(tenantId, agentId, connection, 5, async (read, snapshot) =>
        {
            if (read == 1)
            {
                firstRead.SetResult();
                await resume.Task.WaitAsync(timeout.Token);
            }
            return snapshot;
        });
        using var host = await BuildHostAsync(tenantId, agentId, connection, 5, true, services => services.AddSingleton<IClientPresenceRouter>(router));
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentLogGateway.AgentLogGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        var response = call.ResponseStream.MoveNext(timeout.Token);
        await firstRead.Task.WaitAsync(timeout.Token);
        var registry = host.Services.GetRequiredService<IAgentLogGatewaySessionRegistry>();
        using var current = registry.Register(new ClientKey(tenantId, agentId), Guid.NewGuid(), ambiguous ? 5UL : 6UL,
            CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()).Hello);
        var registrationId = current.RegistrationId;
        resume.SetResult();
        var error = await Assert.ThrowsAsync<RpcException>(() => response);
        error.StatusCode.Should().Be(StatusCode.Aborted);
        error.Status.Detail.Should().NotContain(agentId.ToString()).And.NotContain(connection.ToString());
        current.IsCurrent.Should().BeTrue();
        current.RegistrationId.Should().Be(registrationId);
        current.CompletionToken.IsCancellationRequested.Should().BeFalse();
        registry.GetSources(new ClientKey(tenantId, agentId)).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Connect_PostRegistrationPresenceAdvanceNeverPublishesCandidateOrAccepted()
    {
        const int tenantId = 95;
        var agentId = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var revalidation = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var router = new CurrentPresenceRouter(tenantId, agentId, connection, 5, async (read, snapshot) =>
        {
            if (read == 2)
            {
                revalidation.SetResult();
                await resume.Task.WaitAsync(timeout.Token);
                return snapshot with { ConnectionId = Guid.NewGuid(), ConnectionEpoch = 6 };
            }
            return snapshot;
        });
        using var host = await BuildHostAsync(tenantId, agentId, connection, 5, true, services =>
        {
            services.AddSingleton<IClientPresenceRouter>(router);
            services.AddSingleton<IAgentLogGatewayQueryDispatcher, AgentLogGatewayQueryDispatcher>();
        });
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentLogGateway.AgentLogGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        var response = call.ResponseStream.MoveNext(timeout.Token);
        await revalidation.Task.WaitAsync(timeout.Token);
        var registry = host.Services.GetRequiredService<IAgentLogGatewaySessionRegistry>();
        registry.GetSources(new ClientKey(tenantId, agentId)).Should().BeEmpty();
        resume.SetResult();
        (await Assert.ThrowsAsync<RpcException>(() => response)).StatusCode.Should().Be(StatusCode.Aborted);
        registry.GetSources(new ClientKey(tenantId, agentId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Connect_QueryAdmissionFailureDisposesOnlyCandidateState()
    {
        const int tenantId = 96;
        var agentId = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        AgentLogRegistration? replacement = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = await BuildHostAsync(tenantId, agentId, connection, 5, true, services =>
        {
            services.AddSingleton<IAgentLogGatewaySessionRegistry>(registry);
            services.AddSingleton<IAgentLogGatewayQueryDispatcher>(new RejectingQueryDispatcher(candidate =>
            {
                replacement = registry.Register(candidate.Client, connection, 5, CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()).Hello);
            }));
        });
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentLogGateway.AgentLogGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        (await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(timeout.Token))).StatusCode.Should().Be(StatusCode.Aborted);
        replacement.Should().NotBeNull();
        replacement!.IsCurrent.Should().BeTrue();
        registry.GetSources(new ClientKey(tenantId, agentId)).Should().NotBeEmpty();
        replacement.Dispose();
    }

    [Fact]
    public async Task Connect_ReplacementDuringAcceptedWriteReturnsAborted()
    {
        const int tenantId = 97;
        var agentId = Guid.NewGuid();
        var connection = Guid.NewGuid();
        var registry = new AgentLogGatewaySessionRegistry();
        AgentLogRegistration? replacement = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var host = await BuildHostAsync(tenantId, agentId, connection, 5, true, services =>
        {
            services.AddSingleton<IAgentLogGatewaySessionRegistry>(registry);
            services.AddSingleton(new ReplaceDuringWriteInterceptor(() =>
            {
                replacement = registry.Register(new ClientKey(tenantId, agentId), connection, 5,
                    CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()).Hello);
            }));
            services.AddGrpc(options => options.Interceptors.Add<ReplaceDuringWriteInterceptor>());
        });
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = host.GetTestServer().CreateHandler() });
        using var call = new AgentLogGateway.AgentLogGatewayClient(channel).Connect(cancellationToken: timeout.Token);
        await call.RequestStream.WriteAsync(CreateHello(tenantId, agentId, connection, 5, Guid.NewGuid()));
        (await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(timeout.Token))).StatusCode.Should().Be(StatusCode.Aborted);
        replacement.Should().NotBeNull();
        replacement!.IsCurrent.Should().BeTrue();
        replacement.Dispose();
    }

    private sealed class ReplaceDuringWriteInterceptor(Action replace) : Interceptor
    {
        public override Task DuplexStreamingServerHandler<TRequest, TResponse>(IAsyncStreamReader<TRequest> requestStream,
            IServerStreamWriter<TResponse> responseStream, ServerCallContext context,
            DuplexStreamingServerMethod<TRequest, TResponse> continuation) =>
            continuation(requestStream, new ReplacingWriter<TResponse>(responseStream, replace), context);
    }

    private sealed class ReplacingWriter<T>(IServerStreamWriter<T> inner, Action replace) : IServerStreamWriter<T>
    {
        public WriteOptions? WriteOptions { get => inner.WriteOptions; set => inner.WriteOptions = value; }
        public Task WriteAsync(T message)
        {
            replace();
            return Task.FromException(new OperationCanceledException());
        }
        public Task WriteAsync(T message, CancellationToken cancellationToken) => WriteAsync(message);
    }

    private sealed class RejectingQueryDispatcher(Action<AgentLogRegistration> beforeReject) : IAgentLogGatewayQueryDispatcher
    {
        public AgentLogQueryRegistration Register(AgentLogRegistration registration, bool provisional = false)
        {
            beforeReject(registration);
            throw new AgentGatewayRegistrationFencedException();
        }

        public Task<GatewayLogPageDto> QueryAsync(ClientKey client, GatewayLogPageRequest request, LogQueryOperation operation, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static AgentLogFrame CreateHello(int tenantId, Guid agentId, Guid connectionId, long epoch, Guid operationId) => new()
    {
        ProtocolVersion = "1.0",
        TenantId = tenantId,
        ClientId = agentId.ToString("D"),
        ConnectionId = connectionId.ToString("D"),
        ConnectionEpoch = (ulong)epoch,
        OperationId = operationId.ToString("D"),
        Sequence = 0,
        Traceparent = TraceParent,
        Tracestate = TraceState,
        Hello = new AgentLogHello
        {
            Capabilities = { "log-gateway" },
            Sources =
            {
                new LogSourceDescriptor
                {
                    SourceId = "netratel-runtime",
                    Kind = "runtime",
                    DisplayName = "NetRatel Client Logs",
                    Platform = "linux",
                    Available = true,
                    SupportsLive = true,
                    SupportsHistory = true,
                    SupportsPaging = true
                }
            }
        }
    };

    private static async Task<IHost> BuildHostAsync(int tenantId, Guid agentId, Guid connectionId, long epoch, bool enabled, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options => options.AddPolicy("AgentGatewayAccess", policy =>
                    policy.RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _))));
                services.AddGrpc();
                services.AddSingleton<IClientPresenceRouter>(new CurrentPresenceRouter(tenantId, agentId, connectionId, epoch));
                services.AddSingleton<IAgentManagementService>(new ActiveAgentManagementService(tenantId, agentId));
                services.AddSingleton<IAgentLogGatewaySessionRegistry, AgentLogGatewaySessionRegistry>();
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    LogGatewayEnabled = enabled,
                    LogAuthorityEnabled = enabled
                });
                configure?.Invoke(services);
            });
            web.Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    var id = agentId.ToString("D");
                    context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim("role", "agent"), new Claim("sub", id), new Claim("agent_id", id), new Claim("tenant_id", tenantId.ToString()), new Claim("scope", "netratel:connect")],
                    "LogGatewayTest"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<AgentLogGatewayService>());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class CurrentPresenceRouter(int tenantId, Guid agentId, Guid connectionId, long epoch, Func<int, ClientPresenceSnapshot, Task<ClientPresenceSnapshot>>? onRead = null) : IClientPresenceRouter
    {
        private int _reads;
        public Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey client, CancellationToken cancellationToken)
        {
            var snapshot = new ClientPresenceSnapshot(client,
                client.TenantId == tenantId && client.AgentId == agentId ? ShadowPresenceStatus.Online : ShadowPresenceStatus.Offline,
                epoch, connectionId, 0, DateTimeOffset.UtcNow, "test", [], null, "akka", true);
            return onRead is null ? Task.FromResult(snapshot) : onRead(Interlocked.Increment(ref _reads), snapshot);
        }
        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ActiveAgentManagementService(int tenantId, Guid agentId) : IAgentManagementService
    {
        public Task<AgentDetailDto?> GetAsync(int requestedTenantId, Guid requestedAgentId, CancellationToken ct) =>
            Task.FromResult<AgentDetailDto?>(requestedTenantId == tenantId && requestedAgentId == agentId
                ? new AgentDetailDto(tenantId, agentId, "Log gateway test agent", true, null, DateTimeOffset.UtcNow, "test", null, null, null)
                : null);
        public Task<AgentListResponse> ListAsync(int requestedTenantId, AgentListQuery query, CancellationToken ct) => throw new NotSupportedException();
        public Task DisableAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
        public Task EnableAsync(int requestedTenantId, Guid requestedAgentId, string actor, CancellationToken ct) => throw new NotSupportedException();

        public Task DeleteAsync(int requestedTenantId, Guid requestedAgentId, string reason, string actor, CancellationToken ct) => throw new NotSupportedException();
    }
}
