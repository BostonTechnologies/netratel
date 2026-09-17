using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
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
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Akka.Configuration;
using NetRatel.API.Endpoints;
using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentTerminalGatewayEndpointWebSocketTests
{
    [Fact]
    public async Task StdinWebSocket_WhenTransportReconnectsAfterUpgrade_ClosesWithEndpointUnavailable()
    {
        const int tenantId = 984;
        const string sessionId = "terminal-reconnect-984";
        var agentId = Guid.NewGuid();
        var registry = new ReconnectingInputRegistry(sessionId, tenantId, agentId);
        var endpointExceptions = new EndpointExceptionProbe();

        using var host = await BuildHostAsync(registry, endpointExceptions);
        var webSockets = host.GetTestServer().CreateWebSocketClient();
        webSockets.ConfigureRequest = request =>
            request.Headers.Authorization = "Test";

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var socket = await webSockets.ConnectAsync(
            new Uri($"ws://localhost/api/v2/gateway-terminal/{sessionId}/stdin/ws"),
            timeout.Token);

        socket.State.Should().Be(WebSocketState.Open, "the fake terminal starts opened and accepts the WebSocket upgrade");

        await socket.SendAsync(
            Encoding.UTF8.GetBytes("printf 'reconnect-race'\\n"),
            WebSocketMessageType.Text,
            endOfMessage: true,
            timeout.Token);

        var close = await socket.ReceiveAsync(new byte[256], timeout.Token);

        close.MessageType.Should().Be(WebSocketMessageType.Close);
        socket.CloseStatus.Should().Be(WebSocketCloseStatus.EndpointUnavailable);
        socket.CloseStatusDescription.Should().Be("terminal_transport_reconnecting");
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client-close-ack", timeout.Token);
        await endpointExceptions.Completion.WaitAsync(timeout.Token);
        registry.SendInputCalls.Should().Be(1);
        endpointExceptions.Exceptions.Should().BeEmpty("the reconnect exception is converted into a WebSocket close inside the endpoint");
    }

    private static async Task<IHost> BuildHostAsync(
        IAgentTerminalSessionRegistry registry,
        EndpointExceptionProbe endpointExceptions)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthentication("Test")
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>("Test", _ => { });
                services.AddAuthorization(options =>
                    options.AddPolicy("Operator", policy => policy.RequireAuthenticatedUser()));
                services.AddSingleton(registry);
                services.AddSingleton(new NetRatelAkkaMigrationOptions
                {
                    Enabled = true,
                    PresenceEnabled = true,
                    GatewayEnabled = true,
                    TerminalGatewayEnabled = true,
                    PresenceAuthorityEnabled = true,
                    TerminalAuthorityEnabled = true
                });
            });
            web.Configure(app =>
            {
                app.UseWebSockets();
                app.Use(async (context, next) =>
                {
                    try
                    {
                        await next(context);
                    }
                    catch (Exception exception)
                    {
                        endpointExceptions.Record(exception);
                        throw;
                    }
                    finally
                    {
                        endpointExceptions.MarkCompleted();
                    }
                });
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints => endpoints.MapAgentTerminalGatewayEndpoints());
            });
        });

        return await builder.StartAsync();
    }

    private sealed class ReconnectingInputRegistry(string sessionId, int tenantId, Guid agentId) : IAgentTerminalSessionRegistry
    {
        private readonly GatewayTerminalSession _session = new(
            sessionId,
            tenantId,
            agentId,
            Generation: 1,
            ShellType: "bash",
            Columns: 120,
            Rows: 32,
            State: "opened",
            CreatedAtUtc: DateTimeOffset.UtcNow,
            Authority: "test");
        private int _sendInputCalls;

        public int SendInputCalls => Volatile.Read(ref _sendInputCalls);

        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            new(Channel.CreateUnbounded<GatewayTerminalFrame>().Reader, static () => { });

        public GatewayTerminalAvailability? GetAvailability(ClientKey client) => null;

        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) =>
            Task.FromResult(_session);

        public Task SendInputAsync(string requestedSessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct)
        {
            requestedSessionId.Should().Be(_session.SessionId);
            generation.Should().Be(_session.Generation);
            Interlocked.Increment(ref _sendInputCalls);
            return Task.FromException(new TerminalGatewayActionException(
                "terminal_transport_reconnecting",
                "The terminal transport is reconnecting."));
        }

        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) => Task.CompletedTask;

        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct) => Task.CompletedTask;

        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation) =>
            new(Channel.CreateUnbounded<ReadOnlyMemory<byte>>().Reader);

        public GatewayTerminalSession? Get(string requestedSessionId) =>
            string.Equals(requestedSessionId, _session.SessionId, StringComparison.Ordinal) ? _session : null;

        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => false;

        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => Task.FromResult(false);

        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => false;

        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => false;

        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => false;
    }

    private sealed class EndpointExceptionProbe
    {
        private readonly ConcurrentQueue<Exception> _exceptions = [];
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyCollection<Exception> Exceptions => _exceptions.ToArray();
        public Task Completion => _completion.Task;

        public void Record(Exception exception) => _exceptions.Enqueue(exception);
        public void MarkCompleted() => _completion.TrySetResult();
    }

    private sealed class TestAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "terminal-test-admin")], Scheme.Name)),
                Scheme.Name)));
    }
}
