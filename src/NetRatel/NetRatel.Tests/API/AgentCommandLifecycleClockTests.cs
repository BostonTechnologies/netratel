using System.Security.Claims;
using System.Threading.Channels;
using Akka.Actor;
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
using NetRatel.Akka.Commands;
using NetRatel.Akka.Configuration;
using NetRatel.API.Gateway;
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
    [InlineData(-22)]
    [InlineData(86400)]
    public async Task Command_lifecycle_uses_server_receipt_time_when_agent_clock_is_behind_or_ahead(int skewSeconds)
    {
        await using var fixture = await ClockFixture.CreateAsync();
        using var call = await fixture.ConnectAsync();
        var acceptedAt = fixture.Clock.GetUtcNow();
        await call.RequestStream.WriteAsync(fixture.Lifecycle(1, 3, CommandShadowStatus.Accepted, fixture.RequestedAt.AddSeconds(skewSeconds)));
        var accepted = await fixture.ReadProjectionsAsync(3);
        accepted.Where(item => item.Timestamp is not null).Should().OnlyContain(item => item.Timestamp == acceptedAt);
        fixture.Clock.Now = acceptedAt.AddSeconds(2);
        await call.RequestStream.WriteAsync(fixture.Lifecycle(2, 4, CommandShadowStatus.Cancelled, fixture.RequestedAt.AddSeconds(skewSeconds + 1)));
        var cancelled = await fixture.ReadProjectionsAsync(3);
        cancelled.Should().OnlyContain(item => item.Timestamp == fixture.Clock.Now);
        var state = await fixture.StateAsync();
        state.CurrentStatus.Should().Be(CommandLifecycleStatus.Cancelled);
        state.History[^1].StatusTimestamp.Should().Be(fixture.Clock.Now);
        state.RequestTimestamp.Should().Be(fixture.RequestedAt);
        state.LastAcceptedVersion.Should().Be(4);
        state.LastAcceptedSequence.Should().Be(4);
    }

    [Fact]
    public async Task Duplicate_delivery_repairs_projections_with_original_committed_timestamp()
    {
        await using var fixture = await ClockFixture.CreateAsync();
        using var call = await fixture.ConnectAsync();
        await call.RequestStream.WriteAsync(fixture.Lifecycle(1, 3, CommandShadowStatus.Accepted, fixture.RequestedAt));
        await fixture.ReadProjectionsAsync(3);
        var completedAt = fixture.Clock.Now.AddSeconds(1);
        fixture.Clock.Now = completedAt;
        await call.RequestStream.WriteAsync(fixture.Lifecycle(2, 4, CommandShadowStatus.Cancelled, fixture.RequestedAt));
        await fixture.ReadProjectionsAsync(3);
        fixture.Clock.Now = completedAt.AddHours(1);
        await call.RequestStream.WriteAsync(fixture.Lifecycle(3, 4, CommandShadowStatus.Cancelled, fixture.RequestedAt.AddDays(1)));
        var replay = await fixture.ReadProjectionsAsync(2);
        replay.Should().OnlyContain(item => item.Timestamp == completedAt);
        fixture.JobProjections.Should().Be(2);
        (await fixture.StateAsync()).History.Should().HaveCount(4);
    }

    [Theory]
    [InlineData("request", StatusCode.FailedPrecondition)]
    [InlineData("correlation", StatusCode.FailedPrecondition)]
    [InlineData("command", StatusCode.FailedPrecondition)]
    [InlineData("agent", StatusCode.InvalidArgument)]
    [InlineData("version", StatusCode.FailedPrecondition)]
    [InlineData("sequence", StatusCode.FailedPrecondition)]
    [InlineData("transition", StatusCode.FailedPrecondition)]
    [InlineData("frame_sequence", StatusCode.InvalidArgument)]
    [InlineData("timestamp_shape", StatusCode.InvalidArgument)]
    public async Task Receipt_timestamps_do_not_weaken_identity_sequence_or_transition_fences(string defect, StatusCode expected)
    {
        await using var fixture = await ClockFixture.CreateAsync();
        using var call = await fixture.ConnectAsync();
        await call.RequestStream.WriteAsync(fixture.Lifecycle(1, 3, CommandShadowStatus.Accepted, fixture.RequestedAt.AddMinutes(-1)));
        await fixture.ReadProjectionsAsync(3);
        var frame = fixture.Lifecycle(2, 4, CommandShadowStatus.Started, fixture.RequestedAt.AddDays(1));
        switch (defect)
        {
            case "request": frame.Lifecycle.RequestTimestamp = Timestamp.FromDateTimeOffset(fixture.RequestedAt.AddMilliseconds(1)); break;
            case "correlation": frame.Lifecycle.CorrelationId = "other-request"; break;
            case "command": frame.Lifecycle.CommandId = "other-command"; break;
            case "agent": frame.ClientId = Guid.NewGuid().ToString("D"); break;
            case "version": frame.Lifecycle.Version = 3; break;
            case "sequence": frame.Lifecycle.Sequence = 3; break;
            case "transition": frame.Lifecycle.Status = CommandShadowStatus.Completed; break;
            case "frame_sequence": frame.Sequence = 1; break;
            case "timestamp_shape": frame.Lifecycle.StatusTimestamp.Nanos = -1; break;
        }
        await call.RequestStream.WriteAsync(frame);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var error = await Assert.ThrowsAsync<RpcException>(() => call.ResponseStream.MoveNext(timeout.Token));
        error.StatusCode.Should().Be(expected);
        fixture.ProjectionCount.Should().Be(3);
        var state = await fixture.StateAsync();
        state.CurrentStatus.Should().Be(CommandLifecycleStatus.Accepted);
        state.History.Should().HaveCount(3);
    }

    private sealed record ClockProjection(string Store, DateTimeOffset? Timestamp);

    private sealed class ClockFixture : IAsyncDisposable
    {
        public ClientKey Client { get; } = new(73, Guid.NewGuid());
        public Guid ConnectionId { get; } = Guid.NewGuid();
        public DateTimeOffset RequestedAt { get; } = DateTimeOffset.Parse("2026-09-06T09:15:36Z");
        public ReceiptClock Clock { get; } = new();
        public int ProjectionCount;
        public int JobProjections;
        private readonly Channel<ClockProjection> projections = Channel.CreateUnbounded<ClockProjection>();
        private readonly AgentCommandGatewaySessionRegistry sessions = new();
        private ActorSystem system = null!;
        private IActorRef actor = null!;
        private IHost host = null!;
        private GrpcChannel channel = null!;
        private CommandKey Command => new(Client.TenantId, "clock-command");

        public static async Task<ClockFixture> CreateAsync()
        {
            var fixture = new ClockFixture();
            fixture.Clock.Now = fixture.RequestedAt.AddSeconds(1);
            fixture.system = ActorSystem.Create("command-clock-" + Guid.NewGuid().ToString("N"));
            fixture.actor = fixture.system.ActorOf(CommandActor.Props(fixture.Command));
            foreach (var (status, order) in new[] { (CommandLifecycleStatus.Created, 1UL), (CommandLifecycleStatus.Dispatched, 2UL) })
                (await fixture.actor.Ask<CommandMessageResult>(new RecordCommandLifecycleEvent(new(fixture.Client,
                    fixture.Command.CommandId, "clock-correlation", fixture.RequestedAt, fixture.RequestedAt, order, order, status, "akka", true))))
                    .Disposition.Should().Be(CommandMessageDisposition.Accepted);
            fixture.host = await Host.CreateDefaultBuilder().ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization(options => options.AddPolicy("AgentGatewayAccess", policy => policy.RequireAuthenticatedUser()));
                    services.AddGrpc();
                    services.AddSingleton<TimeProvider>(fixture.Clock);
                    services.AddSingleton<IClientPresenceRouter>(new BlockingPresence(fixture.Client, fixture.ConnectionId));
                    services.AddSingleton(Stub<IAgentManagementService>((method, _) => method == nameof(IAgentManagementService.GetAsync)
                        ? Task.FromResult<AgentDetailDto?>(new(fixture.Client.TenantId, fixture.Client.AgentId, "clock-agent", true, null, fixture.RequestedAt, null, null, null, null))
                        : throw new NotSupportedException(method)));
                    services.AddSingleton<IAgentCommandGatewaySessionRegistry>(fixture.sessions);
                    services.AddSingleton(Stub<IClientCommandRouter>((method, arguments) => method == nameof(IClientCommandRouter.RecordAsync)
                        ? fixture.actor.Ask<CommandMessageResult>((RecordCommandLifecycleEvent)arguments![0]!) : throw new NotSupportedException(method)));
                    services.AddSingleton(Stub<IMcpOperatorCommandStore>((method, args) => fixture.Project("command", method, args)));
                    services.AddSingleton(Stub<IMcpOperatorTaskStore>((method, args) => fixture.Project("task", method, args)));
                    services.AddSingleton(Stub<IJobRunService>((method, args) => fixture.Project("job", method, args)));
                    services.AddSingleton(new NetRatelAkkaMigrationOptions
                    {
                        Enabled = true,
                        PresenceEnabled = true,
                        GatewayEnabled = true,
                        PresenceAuthorityEnabled = true,
                        CommandShadowEnabled = true,
                        CommandAuthorityEnabled = true
                    });
                    services.AddSingleton(NullLogger<AgentCommandGatewayService>.Instance);
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
                    app.UseEndpoints(endpoints => endpoints.MapGrpcService<AgentCommandGatewayService>());
                });
            }).StartAsync();
            fixture.channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = fixture.host.GetTestServer().CreateHandler() });
            return fixture;
        }

        public async Task<AsyncDuplexStreamingCall<AgentCommandFrame, GatewayCommandFrame>> ConnectAsync()
        {
            var call = new AgentCommandGateway.AgentCommandGatewayClient(channel).Connect();
            var hello = Frame(); hello.Hello = new AgentCommandHello();
            await call.RequestStream.WriteAsync(hello);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            (await call.ResponseStream.MoveNext(timeout.Token)).Should().BeTrue();
            return call;
        }

        public AgentCommandFrame Lifecycle(ulong frameSequence, ulong lifecycleSequence, CommandShadowStatus status, DateTimeOffset reportedAt)
        {
            var frame = Frame();
            frame.Sequence = frameSequence;
            frame.Lifecycle = new CommandLifecycleUpdate
            {
                CommandId = Command.CommandId,
                CorrelationId = "clock-correlation",
                Version = lifecycleSequence,
                Sequence = lifecycleSequence,
                Status = status,
                RequestTimestamp = Timestamp.FromDateTimeOffset(RequestedAt),
                StatusTimestamp = Timestamp.FromDateTimeOffset(reportedAt)
            };
            return frame;
        }
        private AgentCommandFrame Frame() => new()
        {
            ProtocolVersion = "1.0",
            TenantId = Client.TenantId,
            ClientId = Client.AgentId.ToString("D"),
            ConnectionId = ConnectionId.ToString("D"),
            ConnectionEpoch = 5
        };
        public Task<CommandShadowState> StateAsync() => actor.Ask<CommandShadowState>(new GetCommandShadowState(Command));
        public async Task<IReadOnlyList<ClockProjection>> ReadProjectionsAsync(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var result = new List<ClockProjection>();
            for (var index = 0; index < count; index++) result.Add(await projections.Reader.ReadAsync(timeout.Token));
            return result;
        }
        private object Project(string store, string method, object?[]? args)
        {
            DateTimeOffset? timestamp;
            if (store == "job" && method == nameof(IJobRunService.UpdateTaskActivityStatusAsync))
            {
                timestamp = ((UpdateJobTaskActivityStatusCommand)args![0]!).CompletedAtUtc;
                Interlocked.Increment(ref JobProjections);
            }
            else if (store == "command" && method == nameof(IMcpOperatorCommandStore.RecordLifecycleAsync)) timestamp = (DateTimeOffset)args![4]!;
            else if (store == "task" && method == nameof(IMcpOperatorTaskStore.RecordLifecycleAsync)) timestamp = (DateTimeOffset)args![5]!;
            else throw new NotSupportedException(method);
            Interlocked.Increment(ref ProjectionCount);
            projections.Writer.TryWrite(new(store, timestamp)).Should().BeTrue();
            return store == "job" ? Task.FromResult<JobTaskActivityInfo?>(null) : Task.CompletedTask;
        }
        public async ValueTask DisposeAsync()
        {
            channel.Dispose();
            host.Dispose();
            await system.Terminate();
        }
    }

    private sealed class ReceiptClock : TimeProvider
    {
        private long ticks;
        public DateTimeOffset Now { get => new(Interlocked.Read(ref ticks), TimeSpan.Zero); set => Interlocked.Exchange(ref ticks, value.UtcTicks); }
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
