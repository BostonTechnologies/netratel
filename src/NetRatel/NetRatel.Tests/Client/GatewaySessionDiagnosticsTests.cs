using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using AwesomeAssertions;
using Grpc.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Gateway;
using Xunit;

namespace NetRatel.Tests.Client;

public sealed class GatewaySessionDiagnosticsTests
{
    private const string Secret = "secret-token-or-capability";

    [Theory]
    [InlineData(403, "text/html", null, true, "http-proxy-origin-rejection", "cloudflare", 300)]
    [InlineData(403, "text/html", null, false, "http-proxy-origin-rejection", "unknown", 300)]
    [InlineData(200, "application/grpc", "7", false, "grpc-rejection", "unknown", 300)]
    [InlineData(200, "application/grpc+proto", "16", false, "grpc-rejection", "unknown", 1)]
    [InlineData(200, "text/plain", null, false, "http-proxy-origin-rejection", "unknown", 1)]
    [InlineData(403, "application/grpc", "7", false, "http-proxy-origin-rejection", "unknown", 300)]
    public async Task RunAsync_ClassifiesObservedResponse_WithoutLoggingSecretsOrAdmitting(
        int status, string contentType, string? grpcStatus, bool cloudflare, string category, string edge, int retrySeconds)
    {
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var tokenService = new RecordingTokenService();
        var extensionStarts = 0;
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = $"https://user:{Secret}@gateway.example.invalid/private/{Secret}?grant={Secret}" },
            tokenService, 7, Guid.NewGuid(), "test", [],
            log: message =>
            {
                logs.Enqueue(message);
                if (message.Contains("Gateway session failed", StringComparison.Ordinal)) stopping.Cancel();
            },
            runForPresenceSession: (_, _, _) =>
            {
                Interlocked.Increment(ref extensionStarts);
                return Task.CompletedTask;
            },
            createHttpHandler: _ => new ResponseHandler((_, _) =>
            {
                var response = Response(status, contentType);
                if (grpcStatus is not null) response.Headers.Add("grpc-status", grpcStatus);
                if (cloudflare) response.Headers.Server.Add(new ProductInfoHeaderValue("cloudflare", null));
                response.Headers.Add("grpc-message", Secret);
                response.Headers.Add("Set-Cookie", Secret);
                return Task.FromResult(response);
            }), nextRandom: () => 0);

        await agent.RunAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(5));

        var failure = logs.Should().ContainSingle(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal)).Which;
        failure.Should().Contain($"category={category}").And.Contain($"edge={edge}")
            .And.Contain($"httpStatus={status}").And.Contain($"contentType={contentType}")
            .And.Contain("protocol=HTTP/2.0").And.Contain("origin=https://gateway.example.invalid")
            .And.Contain("rpc=presence/connect").And.Contain("sessionLifetime=")
            .And.Contain("lastHeartbeatAckAge=unknown").And.Contain($"Retrying in {retrySeconds}s")
            .And.Contain($"state={(retrySeconds == 300 ? "AuthenticationAttention" : "WaitingForBackend")}");
        logs.Should().OnlyContain(message => !message.Contains(Secret, StringComparison.Ordinal));
        extensionStarts.Should().Be(0);
        tokenService.Requests.Should().Be(1);
    }

    [Fact]
    public void Failure_IncompleteOrConflictingGrpcMetadata_DoesNotClaimGenuineAuthorizationRejection()
    {
        var diagnostics = new GatewaySessionDiagnostics("https://gateway.example.invalid");
        using var response = Response(200, "application/grpc");
        response.Headers.Add("grpc-status", new[] { "7", "16" });
        diagnostics.Observe(response);
        var failure = diagnostics.Failure(new RpcException(new Status(StatusCode.PermissionDenied, Secret)), TimeSpan.FromSeconds(1));

        failure.Message.Should().Contain("category=grpc-status-unverified").And.Contain("grpcStatus=PermissionDenied")
            .And.NotContain(Secret);
    }

    [Fact]
    public void RenewalFailure_RecordsSafePhaseAndExpiryDeltaWithoutArbitraryDetailOrTrailers()
    {
        var clock = new GatewayPresenceTestClock();
        var diagnostics = new GatewaySessionDiagnostics("https://gateway.example.invalid", clock);
        var cache = clock.GetUtcNow().AddMinutes(10);
        var authority = cache.AddMilliseconds(250);
        diagnostics.Renewal("ack-validation", cache, authority, authority);
        diagnostics.ProtocolFailure("renewal_authority_overextended");
        var peerCorrelation = Guid.NewGuid();
        var trailers = new Metadata { { "x-correlation-id", peerCorrelation.ToString("D") },
            { "retry-after", "900" }, { "authorization", Secret }, { "grpc-message", Secret }, { "custom-secret", Secret } };
        var failure = diagnostics.Failure(new RpcException(new Status(StatusCode.DataLoss, Secret), trailers), TimeSpan.FromSeconds(1));
        failure.Message.Should().Contain("failureOrigin=local-validation").And.Contain("renewalPhase=ack-validation")
            .And.Contain("serverMinusCacheMs=250").And.Contain("statusDetail=redacted")
            .And.Contain($"correlation:{peerCorrelation:D}").And.Contain("retryAfterSeconds:600").And.NotContain(Secret);
    }

    [Theory]
    [InlineData(StatusCode.PermissionDenied)]
    [InlineData(StatusCode.Unauthenticated)]
    public async Task RunAsync_ProtocolValidGrpcDenial_RemainsDenied(StatusCode status)
    {
        var gateway = new RepeatedlyDeniedGateway(status);
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var tokenService = new RecordingTokenService();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.example.invalid" },
            tokenService, 7, Guid.NewGuid(), "test", [],
            log: message =>
            {
                logs.Enqueue(message);
                if (message.StartsWith("Gateway session failed", StringComparison.Ordinal)) stopping.Cancel();
            },
            createHttpHandler: _ => host.GetTestServer().CreateHandler());

        await agent.RunAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(5));

        logs.Should().Contain(message => message.Contains("category=grpc-rejection", StringComparison.Ordinal) &&
            message.Contains($"grpcStatus={status}", StringComparison.Ordinal));
        logs.Should().NotContain(message => message.StartsWith("Presence admitted", StringComparison.Ordinal));
        logs.Should().OnlyContain(message => !message.Contains(Secret, StringComparison.Ordinal));
        tokenService.Requests.Should().Be(1);
    }

    [Theory]
    [InlineData("tls", "tls", 300)]
    [InlineData("reset", "http2=0x2", 1)]
    [InlineData("socket", "socket=ConnectionReset", 1)]
    [InlineData("canceled", "canceled", 1)]
    public async Task RunAsync_TypedTransportFailure_IsSanitizedAndRetried(string kind, string transport, int retrySeconds)
    {
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var tokenService = new RecordingTokenService();
        var exception = kind switch
        {
            "tls" => new HttpRequestException(HttpRequestError.SecureConnectionError, Secret, new AuthenticationException(Secret)),
            "reset" => new HttpRequestException(HttpRequestError.HttpProtocolError, Secret, new HttpProtocolException(2, Secret, null)),
            "socket" => new HttpRequestException(Secret, new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionReset)),
            _ => (Exception)new OperationCanceledException(Secret)
        };
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.example.invalid" },
            tokenService, 7, Guid.NewGuid(), "test", [],
            log: message =>
            {
                logs.Enqueue(message);
                if (message.Contains("Gateway session failed", StringComparison.Ordinal)) stopping.Cancel();
            },
            createHttpHandler: _ => new ResponseHandler((_, _) => Task.FromException<HttpResponseMessage>(exception)),
            nextRandom: () => 0);

        await agent.RunAsync(stopping.Token).WaitAsync(TimeSpan.FromSeconds(5));

        logs.Should().Contain(message => message.Contains($"transport={transport}", StringComparison.Ordinal) &&
            message.Contains($"Retrying in {retrySeconds}s", StringComparison.Ordinal) &&
            message.Contains($"state={(retrySeconds == 300 ? "AuthenticationAttention" : "WaitingForBackend")}", StringComparison.Ordinal));
        logs.Should().OnlyContain(message => !message.Contains(Secret, StringComparison.Ordinal));
        tokenService.Requests.Should().Be(1);
    }

    [Fact]
    public async Task Handler_SuccessfulStreamingResponse_IsReturnedIntactWithoutReadingOrBuffering()
    {
        var diagnostics = new GatewaySessionDiagnostics("https://gateway.example.invalid");
        using var response = Response(200, "application/grpc");
        var streamingContent = new UnreadableStreamingContent();
        streamingContent.Headers.ContentType = new MediaTypeHeaderValue("application/grpc");
        response.Content = streamingContent;
        using var handler = new GatewayHttpDiagnosticsHandler(diagnostics,
            new ResponseHandler((_, _) => Task.FromResult(response)));
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://gateway.example.invalid/netratel.gateway.v1.AgentGateway/Connect");

        var observed = await invoker.SendAsync(request, CancellationToken.None);

        observed.Should().BeSameAs(response);
        observed.Content.Should().BeSameAs(streamingContent);
        streamingContent.ReadAttempts.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ServiceShutdown_DoesNotLogFailureOrRetry()
    {
        using var stopping = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs = new ConcurrentQueue<string>();
        var tokenService = new RecordingTokenService();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.example.invalid" },
            tokenService, 7, Guid.NewGuid(), "test", [], logs.Enqueue,
            createHttpHandler: _ => new ResponseHandler(async (_, cancellation) =>
            {
                entered.TrySetResult();
                // slopwatch-ignore: SW004 Existing shutdown fixture holds HTTP work until cancellation so the test proves service shutdown does not retry.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
                throw new InvalidOperationException("unreachable");
            }));

        var run = agent.RunAsync(stopping.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stopping.Cancel();
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        logs.Should().NotContain(message => message.Contains("Gateway session failed", StringComparison.Ordinal));
        tokenService.Requests.Should().Be(1);
    }

    [Fact]
    public async Task RunAsync_RepeatedFailures_AreRateLimited_WhileRetryAndIdentityRemainUnchanged()
    {
        var gateway = new RepeatedlyDeniedGateway();
        using var host = await BuildHostAsync(gateway);
        using var stopping = new CancellationTokenSource();
        var logs = new ConcurrentQueue<string>();
        var tokenService = new RecordingTokenService();
        var agentId = Guid.NewGuid();
        var agent = new AgentGatewayPresenceClient(
            new GatewayClientOptions { Endpoint = "https://gateway.example.invalid" },
            tokenService, 7, agentId, "test", [], logs.Enqueue,
            createHttpHandler: _ => host.GetTestServer().CreateHandler(),
            nextRandom: () => 0.5);

        var run = agent.RunAsync(stopping.Token);
        try
        {
            await gateway.ThirdRequest.Task.WaitAsync(TimeSpan.FromSeconds(8));
            logs.Should().ContainSingle(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal));
            var attempts = gateway.Requests.ToArray();
            (attempts[1].At - attempts[0].At).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(900));
            (attempts[2].At - attempts[1].At).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(1900));
            attempts.Should().OnlyContain(request => request.Authorization == $"Bearer {Secret}");
            gateway.Hellos.Should().HaveCount(3).And.OnlyContain(hello => hello.ClientId == agentId.ToString("D") && hello.TenantId == 7 && hello.Sequence == 0);
            tokenService.Requests.Should().Be(3);
            logs.Should().OnlyContain(message => !message.Contains(Secret, StringComparison.Ordinal));
        }
        finally
        {
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static HttpResponseMessage Response(int status, string contentType)
    {
        var response = new HttpResponseMessage((HttpStatusCode)status)
        {
            Version = HttpVersion.Version20,
            Content = new StringContent(Secret)
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        return response;
    }

    private static async Task<IHost> BuildHostAsync(RepeatedlyDeniedGateway gateway)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddGrpc();
                services.AddSingleton(gateway);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseEndpoints(endpoints => endpoints.MapGrpcService<RepeatedlyDeniedGateway>());
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RepeatedlyDeniedGateway(StatusCode denial = StatusCode.Unavailable) : global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayBase
    {
        internal ConcurrentQueue<(DateTimeOffset At, string? Authorization)> Requests { get; } = new();
        internal ConcurrentQueue<AgentFrame> Hellos { get; } = new();
        internal TaskCompletionSource ThirdRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task Connect(IAsyncStreamReader<AgentFrame> requestStream, IServerStreamWriter<GatewayFrame> responseStream, ServerCallContext context)
        {
            await requestStream.MoveNext(context.CancellationToken);
            Hellos.Enqueue(requestStream.Current);
            Requests.Enqueue((DateTimeOffset.UtcNow, context.RequestHeaders.GetValue("authorization")));
            if (Requests.Count == 3) ThirdRequest.TrySetResult();
            throw new RpcException(new Status(denial, Secret));
        }
    }

    private sealed class ResponseHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request, cancellationToken);
    }

    private sealed class RecordingTokenService : IAgentTokenService
    {
        internal int Requests { get; private set; }
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests++;
            return Task.FromResult((Secret, DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    private sealed class UnreadableStreamingContent : HttpContent
    {
        internal int ReadAttempts { get; private set; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            ReadAttempts++;
            throw new InvalidOperationException("A diagnostic observer must not consume a streaming body.");
        }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
    }
}
