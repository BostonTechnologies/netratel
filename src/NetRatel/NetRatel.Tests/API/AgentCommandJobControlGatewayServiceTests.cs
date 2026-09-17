using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
using NetRatel.API.Services.Jobs;
using NetRatel.Application.Agents;
using NetRatel.Application.Commands;
using NetRatel.Application.Jobs;
using NetRatel.Application.Operations;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed partial class AgentCommandJobControlGatewayServiceTests
{
    [Theory]
    [InlineData("command")]
    [InlineData("job")]
    [InlineData("control")]
    public async Task Connect_RevalidatesPresenceBeforeCandidateBecomesVisible(string gateway)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        fixture.Presence.BlockRead = 2;
        using var call = fixture.Connect();
        await call.SendHello();
        await fixture.Presence.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AssertUnavailableAsync();
        fixture.Presence.Epoch++;
        fixture.Presence.ReleaseRead.TrySetResult();

        var error = await Assert.ThrowsAsync<RpcException>(() => call.Read(CancellationToken.None));
        error.StatusCode.Should().Be(StatusCode.Aborted);
        await fixture.AssertUnavailableAsync();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("job")]
    [InlineData("control")]
    public async Task Connect_AbortsWhenExactCandidateIsReplacedDuringAdmission(string gateway)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        fixture.Presence.BlockRead = 2;
        using var call = fixture.Connect();
        await call.SendHello();
        await fixture.Presence.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.AssertUnavailableAsync();
        using var winner = fixture.Register(fixture.ConnectionId, 5);
        fixture.Presence.ReleaseRead.TrySetResult();

        var error = await Assert.ThrowsAsync<RpcException>(() => call.Read(CancellationToken.None));
        error.StatusCode.Should().Be(StatusCode.Aborted);
        winner.IsCurrent().Should().BeTrue();
    }

    [Theory]
    [InlineData("command", false)]
    [InlineData("command", true)]
    [InlineData("job", false)]
    [InlineData("job", true)]
    [InlineData("control", false)]
    [InlineData("control", true)]
    public async Task Connect_MapsRegistrationFenceToAborted_AndPreservesCurrent(string gateway, bool ambiguous)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        fixture.Presence.BlockRead = 1;
        using var call = fixture.Connect();
        await call.SendHello();
        await fixture.Presence.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var winner = fixture.Register(Guid.NewGuid(), ambiguous ? 5UL : 6UL);
        fixture.Presence.ReleaseRead.TrySetResult();

        var error = await Assert.ThrowsAsync<RpcException>(() => call.Read(CancellationToken.None));
        error.StatusCode.Should().Be(StatusCode.Aborted);
        error.Status.Detail.Should().NotContain(fixture.Client.AgentId.ToString());
        winner.IsCurrent().Should().BeTrue();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("job")]
    [InlineData("control")]
    public async Task Connect_ExactReconnectTerminatesOldRpcWhileItsInputRemainsOpen(string gateway)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        using var old = fixture.Connect();
        await old.SendHello();
        (await old.Read(CancellationToken.None)).Should().BeTrue();
        using var replacement = fixture.Connect();
        await replacement.SendHello();
        (await replacement.Read(CancellationToken.None)).Should().BeTrue();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        (await old.Read(timeout.Token)).Should().BeFalse();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("job")]
    public async Task Connect_RejectsLifecycleResumingAfterExactReplacement(string gateway)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        using var old = fixture.Connect();
        await old.SendHello();
        (await old.Read(CancellationToken.None)).Should().BeTrue();
        fixture.Presence.BlockRead = 3;
        await old.SendLifecycle();
        await fixture.Presence.ReadBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var winner = fixture.Register(fixture.ConnectionId, 5);
        fixture.Presence.ReleaseRead.TrySetResult();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RpcException>(() => old.Read(timeout.Token));
        error.StatusCode.Should().Be(StatusCode.Aborted);
        fixture.LifecycleCalls.Should().Be(0);
        winner.IsCurrent().Should().BeTrue();
    }

    [Theory]
    [InlineData("command")]
    [InlineData("job")]
    public async Task Connect_ChecksOwnershipAfterAwaitedLifecycleProcessing(string gateway)
    {
        using var fixture = await Fixture.CreateAsync(gateway);
        fixture.BlockLifecycle = true;
        using var old = fixture.Connect();
        await old.SendHello();
        (await old.Read(CancellationToken.None)).Should().BeTrue();
        await old.SendLifecycle();
        await fixture.LifecycleBlocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
        using var winner = fixture.Register(fixture.ConnectionId, 5);
        fixture.ReleaseLifecycle.TrySetResult();

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RpcException>(() => old.Read(timeout.Token));
        error.StatusCode.Should().Be(StatusCode.Aborted);
        fixture.LifecycleCalls.Should().Be(1);
        fixture.ProjectionCalls.Should().Be(0);
        winner.IsCurrent().Should().BeTrue();
    }

    private sealed record Registration(IDisposable Handle, Func<bool> IsCurrent) : IDisposable
    {
        public void Dispose() => Handle.Dispose();
    }

    private sealed record GatewayCall(Func<Task> SendHello, Func<Task> SendLifecycle,
        Func<CancellationToken, Task<bool>> Read, Action Close) : IDisposable
    {
        public void Dispose() => Close();
    }

    private sealed class Fixture(string gateway) : IDisposable
    {
        public ClientKey Client { get; } = new(73, Guid.NewGuid());
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public BlockingPresence Presence { get; private set; } = null!;
        public bool BlockLifecycle { get; set; }
        public int LifecycleCalls;
        public int ProjectionCalls;
        public TaskCompletionSource LifecycleBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseLifecycle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly AgentCommandGatewaySessionRegistry _commands = new();
        private readonly AgentJobGatewaySessionRegistry _jobs = new();
        private readonly AgentControlSessionRegistry _controls = new(TimeProvider.System);
        private IHost _host = null!;
        private GrpcChannel _channel = null!;

        public static async Task<Fixture> CreateAsync(string gateway)
        {
            var fixture = new Fixture(gateway);
            fixture.Presence = new BlockingPresence(fixture.Client, fixture.ConnectionId);
            fixture._host = await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization(options => options.AddPolicy("AgentGatewayAccess", policy => policy.RequireAuthenticatedUser()));
                    services.AddGrpc();
                    services.AddSingleton(TimeProvider.System);
                    services.AddSingleton<IClientPresenceRouter>(fixture.Presence);
                    services.AddSingleton(Stub<IAgentManagementService>((method, _) => method == nameof(IAgentManagementService.GetAsync)
                        ? Task.FromResult<AgentDetailDto?>(new(fixture.Client.TenantId, fixture.Client.AgentId, "test", true, null, DateTimeOffset.UtcNow, null, null, null, null))
                        : throw new NotSupportedException(method)));
                    services.AddSingleton<IAgentCommandGatewaySessionRegistry>(fixture._commands);
                    services.AddSingleton<IAgentJobGatewaySessionRegistry>(fixture._jobs);
                    services.AddSingleton<IAgentControlSessionRegistry>(fixture._controls);
                    services.AddSingleton(Stub<IClientCommandRouter>((method, _) => method == nameof(IClientCommandRouter.RecordAsync)
                        ? fixture.RecordCommandAsync() : throw new NotSupportedException(method)));
                    services.AddSingleton(Stub<IAkkaJobAuthorityService>((method, _) => method == nameof(IAkkaJobAuthorityService.RecordLifecycleAsync)
                        ? fixture.RecordLifecycleAsync() : throw new NotSupportedException(method)));
                    services.AddSingleton(Stub<IMcpOperatorCommandStore>(fixture.RecordProjection));
                    services.AddSingleton(Stub<IMcpOperatorTaskStore>(fixture.RecordProjection));
                    services.AddSingleton(Stub<IJobRunService>(fixture.RecordProjection));
                    services.AddSingleton(new NetRatelAkkaMigrationOptions
                    {
                        Enabled = true,
                        PresenceEnabled = true,
                        GatewayEnabled = true,
                        PresenceAuthorityEnabled = true,
                        ControlGatewayEnabled = true,
                        CommandShadowEnabled = true,
                        CommandAuthorityEnabled = true,
                        JobShadowEnabled = true,
                        JobAuthorityEnabled = true
                    });
                    services.AddSingleton(NullLogger<AgentCommandGatewayService>.Instance);
                    services.AddSingleton(NullLogger<AgentJobGatewayService>.Instance);
                    services.AddSingleton(NullLogger<AgentControlGatewayService>.Instance);
                });
                web.Configure(app =>
                {
                    app.Use(async (context, next) =>
                    {
                        var id = fixture.Client.AgentId.ToString("D");
                        context.User = new ClaimsPrincipal(new ClaimsIdentity([
                            new Claim("role", "agent"), new Claim("sub", id), new Claim("agent_id", id),
                            new Claim("tenant_id", fixture.Client.TenantId.ToString()), new Claim("scope", "netratel:connect")
                        ], "GatewayTest"));
                        await next();
                    });
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGrpcService<AgentCommandGatewayService>();
                        endpoints.MapGrpcService<AgentJobGatewayService>();
                        endpoints.MapGrpcService<AgentControlGatewayService>();
                    });
                });
            }).StartAsync();
            fixture._channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = fixture._host.GetTestServer().CreateHandler()
            });
            return fixture;
        }

        private async Task RecordLifecycleAsync()
        {
            Interlocked.Increment(ref LifecycleCalls);
            if (BlockLifecycle)
            {
                LifecycleBlocked.TrySetResult();
                // Deliberately ignores cancellation to prove ownership, independent of cancellation.
                await ReleaseLifecycle.Task;
            }
        }

        private async Task<CommandMessageResult> RecordCommandAsync()
        {
            await RecordLifecycleAsync();
            return new CommandMessageResult(new CommandKey(Client.TenantId, "command-1"), CommandMessageDisposition.Accepted,
                CommandLifecycleStatus.Accepted, 1, 1);
        }

        private object RecordProjection(string method, object?[]? arguments)
        {
            Interlocked.Increment(ref ProjectionCalls);
            throw new InvalidOperationException($"Stale lifecycle reached projection {method}.");
        }

        public Registration Register(Guid connection, ulong epoch)
        {
            if (gateway == "command")
            {
                var registration = _commands.Register(Client, connection, epoch);
                return new Registration(registration, () => registration.IsCurrent);
            }
            if (gateway == "job")
            {
                var registration = _jobs.Register(Client, connection, epoch);
                return new Registration(registration, () => registration.IsCurrent);
            }
            var control = _controls.Register(Client, connection, epoch);
            return new Registration(control, () => control.IsCurrent);
        }

        public async Task AssertUnavailableAsync()
        {
            if (gateway == "command")
                _commands.IsAvailable(Client).Should().BeFalse();
            else if (gateway == "job")
                _jobs.IsAvailable(Client).Should().BeFalse();
            else
                await Assert.ThrowsAsync<AgentControlSessionUnavailableException>(() =>
                    _controls.RequestPingAsync(Client, TimeSpan.FromSeconds(5), CancellationToken.None));
        }

        public GatewayCall Connect()
        {
            var timestamp = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow);
            if (gateway == "command")
            {
                var call = new AgentCommandGateway.AgentCommandGatewayClient(_channel).Connect();
                AgentCommandFrame Frame() => new()
                {
                    ProtocolVersion = "1.0",
                    TenantId = Client.TenantId,
                    ClientId = Client.AgentId.ToString("D"),
                    ConnectionId = ConnectionId.ToString("D"),
                    ConnectionEpoch = 5
                };
                return new GatewayCall(() => { var frame = Frame(); frame.Hello = new AgentCommandHello(); return call.RequestStream.WriteAsync(frame); },
                    () =>
                    {
                        var frame = Frame(); frame.Sequence = 1; frame.Lifecycle = new CommandLifecycleUpdate
                        {
                            CommandId = "command-1",
                            CorrelationId = "correlation-1",
                            Version = 1,
                            Sequence = 1,
                            Status = CommandShadowStatus.Accepted,
                            RequestTimestamp = timestamp,
                            StatusTimestamp = timestamp
                        }; return call.RequestStream.WriteAsync(frame);
                    }, call.ResponseStream.MoveNext, call.Dispose);
            }
            if (gateway == "job")
            {
                var call = new AgentJobGateway.AgentJobGatewayClient(_channel).Connect();
                AgentJobFrame Frame() => new()
                {
                    ProtocolVersion = "1.0",
                    TenantId = Client.TenantId,
                    ClientId = Client.AgentId.ToString("D"),
                    ConnectionId = ConnectionId.ToString("D"),
                    ConnectionEpoch = 5
                };
                return new GatewayCall(() => { var frame = Frame(); frame.Hello = new AgentJobHello(); return call.RequestStream.WriteAsync(frame); },
                    () =>
                    {
                        var frame = Frame(); frame.Sequence = 1; frame.Lifecycle = new JobLifecycleUpdate
                        {
                            JobRunId = 1,
                            JobStepId = 2,
                            JobStepRunId = 3,
                            Ordinal = 0,
                            RequestId = "request-1",
                            CorrelationId = "correlation-1",
                            Version = 1,
                            LifecycleSequence = 1,
                            Status = JobLifecycleStatus.Accepted,
                            RequestedAtUtc = timestamp,
                            StatusAtUtc = timestamp
                        }; return call.RequestStream.WriteAsync(frame);
                    }, call.ResponseStream.MoveNext, call.Dispose);
            }
            var control = new AgentControlGateway.AgentControlGatewayClient(_channel).Connect();
            return new GatewayCall(() => control.RequestStream.WriteAsync(new AgentControlFrame
            {
                ProtocolVersion = "1.0",
                TenantId = Client.TenantId,
                ClientId = Client.AgentId.ToString("D"),
                ConnectionId = ConnectionId.ToString("D"),
                ConnectionEpoch = 5,
                Hello = new AgentControlHello()
            }), () => throw new NotSupportedException(), control.ResponseStream.MoveNext, control.Dispose);
        }

        public void Dispose()
        {
            Presence.ReleaseRead.TrySetResult();
            ReleaseLifecycle.TrySetResult();
            _channel.Dispose();
            _host.Dispose();
        }
    }

    private sealed class BlockingPresence(ClientKey client, Guid connectionId) : IClientPresenceRouter
    {
        public ulong Epoch = 5;
        public int BlockRead;
        private int _reads;
        public TaskCompletionSource ReadBlocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ClientPresenceSnapshot> GetSnapshotAsync(ClientKey requestedClient, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _reads) == BlockRead)
            {
                ReadBlocked.TrySetResult();
                // Resume a stale read even after replacement cancellation to exercise the ownership check.
                await ReleaseRead.Task;
            }
            return new ClientPresenceSnapshot(client, ShadowPresenceStatus.Online, checked((long)Epoch), connectionId,
                0, DateTimeOffset.UtcNow, "test", [], null, "akka", true);
        }

        public Task<GatewayPresenceSessionStarted> StartSessionAsync(StartGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> RecordHeartbeatAsync(RecordGatewayHeartbeat message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<PresenceMessageResult> EndSessionAsync(EndGatewayPresenceSession message, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientPresenceRouteStatus> ProbeAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static T Stub<T>(Func<string, object?[]?, object> invoke) where T : class
    {
        var proxy = DispatchProxy.Create<T, StrictDependencyStub>();
        ((StrictDependencyStub)(object)proxy).Handler = invoke;
        return proxy;
    }

    public class StrictDependencyStub : DispatchProxy
    {
        public Func<string, object?[]?, object> Handler { get; set; } = (_, _) => throw new NotSupportedException();
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args);
    }
}
