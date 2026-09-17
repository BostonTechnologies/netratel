using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Channels;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
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

public sealed class AgentTerminalGatewayEndpointStreamTests
{
    [Theory]
    [InlineData("terminal_session_not_found", HttpStatusCode.NotFound)]
    [InlineData("terminal_session_closed", HttpStatusCode.Conflict)]
    public async Task Stream_WhenSubscriptionDisappearsBeforeSseStarts_ReturnsNormalFailureStatus(
        string code,
        HttpStatusCode expectedStatus)
    {
        const int tenantId = 984;
        const string sessionId = "terminal-stream-race-984";
        var registry = new StreamRegistry(
            CreateSession(sessionId, tenantId, Guid.NewGuid(), "opened"),
            _ => throw new TerminalGatewayActionException(code, "The terminal subscription is no longer available."));
        var endpointExceptions = new EndpointExceptionProbe();

        using var host = await BuildHostAsync(registry, endpointExceptions);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Test");

        using var response = await client.GetAsync($"/api/v2/gateway-terminal/{sessionId}/stream");

        response.StatusCode.Should().Be(expectedStatus);
        await endpointExceptions.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        endpointExceptions.Exceptions.Should().BeEmpty("the subscription check happens before the first SSE response write");
    }

    [Fact]
    public async Task Stream_WhenInitialWriteIsCancelled_HandlesBrowserDetachLocally()
    {
        const int tenantId = 984;
        const string sessionId = "terminal-stream-initial-cancel-984";
        var output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        var registry = new StreamRegistry(
            CreateSession(sessionId, tenantId, Guid.NewGuid(), "opened"),
            dispose => new GatewayTerminalOutputSubscription(output.Reader, dispose));

        using var host = await BuildHostAsync(registry, new EndpointExceptionProbe());
        using var requestAborted = new CancellationTokenSource();
        using var body = new DisconnectingResponseStream(
            throwOnWrite: 1,
            exceptionFactory: () =>
            {
                requestAborted.Cancel();
                return new OperationCanceledException(requestAborted.Token);
            });

        await FluentActions.Awaiting(() => InvokeStreamEndpointAsync(host, sessionId, requestAborted.Token, body))
            .Should().NotThrowAsync();

        body.WriteCount.Should().Be(1);
        registry.SubscriptionDisposals.Should().Be(1, "a cancelled browser stream must detach its output subscriber");
    }

    [Fact]
    public async Task Stream_WhenFinalWriteHitsPeerReset_HandlesBrowserDisconnectLocally()
    {
        const int tenantId = 984;
        const string sessionId = "terminal-stream-final-disconnect-984";
        var output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        output.Writer.TryComplete();
        var registry = new StreamRegistry(
            CreateSession(sessionId, tenantId, Guid.NewGuid(), "closed"),
            dispose => new GatewayTerminalOutputSubscription(output.Reader, dispose));

        using var host = await BuildHostAsync(registry, new EndpointExceptionProbe());
        using var body = new DisconnectingResponseStream(
            throwOnWrite: 2,
            exceptionFactory: static () => new IOException(
                "broken pipe",
                new SocketException((int)SocketError.ConnectionReset)));

        await FluentActions.Awaiting(() => InvokeStreamEndpointAsync(host, sessionId, CancellationToken.None, body))
            .Should().NotThrowAsync();

        body.WriteCount.Should().Be(2, "the connection event is written before the final terminal close event");
        registry.SubscriptionDisposals.Should().Be(1, "a disconnected browser stream must detach its output subscriber");
    }

    private static GatewayTerminalSession CreateSession(string sessionId, int tenantId, Guid agentId, string state) => new(
        sessionId,
        tenantId,
        agentId,
        Generation: 1,
        ShellType: "bash",
        Columns: 120,
        Rows: 32,
        State: state,
        CreatedAtUtc: DateTimeOffset.UtcNow,
        Authority: "test");

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

    private static Task InvokeStreamEndpointAsync(
        IHost host,
        string sessionId,
        CancellationToken requestAborted,
        Stream responseBody)
    {
        var endpoint = host.Services
            .GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/api/v2/gateway-terminal/{sessionId}/stream");
        var requestDelegate = endpoint.RequestDelegate
            ?? throw new InvalidOperationException("The terminal stream route has no request delegate.");
        var context = new DefaultHttpContext
        {
            RequestServices = host.Services,
            RequestAborted = requestAborted
        };
        context.SetEndpoint(endpoint);
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = $"/api/v2/gateway-terminal/{sessionId}/stream";
        context.Request.RouteValues["sessionId"] = sessionId;
        context.Response.Body = responseBody;
        return requestDelegate(context);
    }

    private sealed class StreamRegistry(
        GatewayTerminalSession session,
        Func<Action, GatewayTerminalOutputSubscription> subscribe) : IAgentTerminalSessionRegistry
    {
        private int _subscriptionDisposals;

        public int SubscriptionDisposals => Volatile.Read(ref _subscriptionDisposals);

        public AgentTerminalGatewayRegistration Register(ClientKey client, Guid connectionId, ulong connectionEpoch, IReadOnlyList<string> availableShells, IReadOnlyList<string>? capabilities = null) =>
            new(Channel.CreateUnbounded<GatewayTerminalFrame>().Reader, static () => { });

        public GatewayTerminalAvailability? GetAvailability(ClientKey client) => null;

        public Task<GatewayTerminalSession> OpenAsync(ClientKey client, string shellType, string? workingDirectory, int columns, int rows, CancellationToken ct) =>
            Task.FromResult(session);

        public Task SendInputAsync(string sessionId, ulong generation, ReadOnlyMemory<byte> content, CancellationToken ct) => Task.CompletedTask;

        public Task ResizeAsync(string sessionId, ulong generation, int columns, int rows, CancellationToken ct) => Task.CompletedTask;

        public Task CloseAsync(string sessionId, ulong generation, string reason, CancellationToken ct) => Task.CompletedTask;

        public GatewayTerminalOutputSubscription Subscribe(string sessionId, ulong generation)
        {
            sessionId.Should().Be(session.SessionId);
            generation.Should().Be(session.Generation);
            return subscribe(() => Interlocked.Increment(ref _subscriptionDisposals));
        }

        public GatewayTerminalSession? Get(string sessionId) =>
            string.Equals(sessionId, session.SessionId, StringComparison.Ordinal) ? session : null;

        public bool TryReceiveOpened(ClientKey client, TerminalSessionOpened opened) => false;

        public Task<bool> TryReceiveOutputAsync(ClientKey client, TerminalOutput output, CancellationToken ct) => Task.FromResult(false);

        public bool TryReceiveResizeApplied(ClientKey client, TerminalResizeApplied resize) => false;

        public bool TryReceiveClosed(ClientKey client, TerminalSessionClosed closed) => false;

        public bool TryReceiveFailed(ClientKey client, TerminalSessionFailed failed) => false;
    }

    private sealed class DisconnectingResponseStream(int throwOnWrite, Func<Exception> exceptionFactory) : Stream
    {
        private readonly MemoryStream _buffer = new();
        private int _writeCount;

        public int WriteCount => Volatile.Read(ref _writeCount);
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _buffer.Length;
        public override long Position { get => _buffer.Position; set => throw new NotSupportedException(); }

        public override void Flush() => _buffer.Flush();

        public override Task FlushAsync(CancellationToken cancellationToken) => _buffer.FlushAsync(cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count)
        {
            ThrowIfDisconnected();
            _buffer.Write(buffer, offset, count);
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            ThrowIfDisconnected();
            return _buffer.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            ThrowIfDisconnected();
            return _buffer.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _buffer.Dispose();
            }

            base.Dispose(disposing);
        }

        private void ThrowIfDisconnected()
        {
            if (Interlocked.Increment(ref _writeCount) == throwOnWrite)
            {
                throw exceptionFactory();
            }
        }
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
