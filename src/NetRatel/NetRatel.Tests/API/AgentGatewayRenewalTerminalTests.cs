using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using Akka.Actor;
using Akka.Hosting;
using AwesomeAssertions;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NetRatel.Akka.Configuration;
using NetRatel.Akka.Hosting;
using NetRatel.Akka.Presence;
using NetRatel.API.Gateway;
using NetRatel.API.Services;
using NetRatel.Application.ClientAuth;
using NetRatel.Application.Presence;
using NetRatel.Application.Operations;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Terminal;
using NetRatel.Client.Services;
using Xunit;

namespace NetRatel.Tests.API;

public sealed class AgentGatewayRenewalTerminalTests(ITestOutputHelper output)
{
    [Fact]
    public async Task NativeClient_TwoSignedRenewalsPreserveShellProcessStateInputOutputAndResize()
    {
        const int tenantId = 81;
        var agentId = Guid.NewGuid();
        using var credentials = new AgentGatewayRenewalTestCredentials();
        using var host = await BuildHostAsync(credentials, new(tenantId, agentId));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        await RunContinuityAsync(host, "https://gateway.test", () => host.GetTestServer().CreateHandler(),
            credentials, tenantId, agentId, TimeSpan.FromSeconds(12), deadline.Token, output.WriteLine);
    }

    // Reused by the existing disposable HTTPS/Traefik fixture. It owns no
    // listener or additional test program and uses the supplied product host.
    internal static async Task RunContinuityAsync(IHost host, string endpoint,
        Func<HttpMessageHandler> createHttpHandler, AgentGatewayRenewalTestCredentials credentials,
        int tenantId, Guid agentId, TimeSpan duration, CancellationToken cancellationToken, Action<string>? report = null)
    {
        var registry = host.Services.GetRequiredService<IAgentTerminalSessionRegistry>();
        var router = host.Services.GetRequiredService<IClientPresenceRouter>();
        var files = host.Services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
        var key = new ClientKey(tenantId, agentId);
        var options = new GatewayClientOptions { Endpoint = endpoint };
        var tokenService = new RenewingSignedTokenService(credentials, new(tenantId, agentId), duration);
        var logs = new ConcurrentQueue<string>();
        var admitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewedTwice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var owner = new TaskCompletionSource<GatewayPresenceSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        var renewalCount = 0;
        var admissionCount = 0;
        var fileAdmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var directory = Directory.CreateTempSubdirectory("netratel-renewal-functional-");
        var filePath = Path.Combine(directory.FullName, "proof.txt");
        var expectedBytes = Encoding.UTF8.GetBytes("Authenticated gateway file continuity " + Guid.NewGuid());
        await File.WriteAllBytesAsync(filePath, expectedBytes, cancellationToken);
        void Log(string message)
        {
            logs.Enqueue(message);
            report?.Invoke(message);
            if (message.StartsWith("Terminal gateway admitted.", StringComparison.Ordinal)) admitted.TrySetResult();
            if (message.StartsWith("File gateway admitted.", StringComparison.Ordinal)) fileAdmitted.TrySetResult();
            if (message.StartsWith("Presence authentication renewed.", StringComparison.Ordinal) &&
                Interlocked.Increment(ref renewalCount) == 2) renewedTwice.TrySetResult();
        }
        using var terminalClient = new NetRatel.Client.Service.Gateway.AgentTerminalGatewayClient(options,
            new TerminalHostOptions { BackendPreference = OperatingSystem.IsWindows() ? TerminalBackendPreference.ConPty : TerminalBackendPreference.UnixPty },
            [OperatingSystem.IsWindows() ? "cmd" : "sh"], Log,
            _ => GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = createHttpHandler() }));
        var fileClient = new AgentFileGatewayClient(options, new FileSystemService(), Log,
            _ => GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = createHttpHandler() }));
        var presenceClient = new AgentGatewayPresenceClient(options, tokenService, tenantId, agentId, "renewal-functional-test", [], Log,
            runForPresenceSession: (session, token, lifetime) =>
            {
                Interlocked.Increment(ref admissionCount);
                owner.TrySetResult(session);
                return Task.WhenAll(terminalClient.RunForPresenceSessionAsync(session, token, lifetime),
                    fileClient.RunForPresenceSessionAsync(session, token, lifetime));
            }, createHttpHandler: _ => createHttpHandler());
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = presenceClient.RunAsync(stopping.Token);
        string? sessionId = null;
        ulong generation = 0;
        try
        {
            await admitted.Task.WaitAsync(cancellationToken);
            await fileAdmitted.Task.WaitAsync(cancellationToken);
            var originalOwner = await owner.Task.WaitAsync(cancellationToken);
            var availability = registry.GetAvailability(key)!;
            var registrationId = availability.RegistrationId;
            var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
            var terminal = await registry.OpenAsync(key, shell, null, 100, 30, cancellationToken);
            sessionId = terminal.SessionId;
            generation = terminal.Generation;
            while (registry.Get(sessionId)?.State is "requested" or "opening")
                await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
            registry.Get(sessionId)!.State.Should().Be("opened");
            using var output = registry.Subscribe(sessionId, generation);
            var marker = Guid.NewGuid().ToString("N");
            var initialCommand = OperatingSystem.IsWindows()
                ? $"set NETRATEL_RENEWAL_PROOF={marker}\r\n"
                : $"NETRATEL_RENEWAL_PROOF='{marker}'\n";
            await registry.SendInputAsync(sessionId, generation, Encoding.UTF8.GetBytes(initialCommand), cancellationToken);
            var originalProcess = await ProbeShellAsync(registry, output, sessionId, generation, marker, cancellationToken);
            renewalCount.Should().Be(0, "the original shell must be working before the first authenticated renewal");
            tokenService.AllowRenewal();
            await registry.ResizeAsync(sessionId, generation, 110, 35, cancellationToken);
            var until = DateTimeOffset.UtcNow + duration;
            var probeCount = 0;
            do
            {
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                var process = await ProbeShellAsync(registry, output, sessionId, generation, marker, cancellationToken);
                process.Should().Be(originalProcess, "normal authentication renewal must retain the original shell process and its state");
                await registry.ResizeAsync(sessionId, generation, 110 + (++probeCount % 2), 35 + (probeCount % 2), cancellationToken);
                await ProbeFilesAsync(files, key, directory.FullName, filePath, expectedBytes, cancellationToken);
                registry.Get(sessionId)!.State.Should().Be("opened");
                registry.Get(sessionId)!.Generation.Should().Be(generation);
                registry.GetAvailability(key)!.RegistrationId.Should().Be(registrationId);
            } while (DateTimeOffset.UtcNow < until || !renewedTwice.Task.IsCompleted);
            await renewedTwice.Task.WaitAsync(cancellationToken);
            admissionCount.Should().Be(1);
            renewalCount.Should().Be(2);
            tokenService.RequestCount.Should().Be(3);
            logs.Should().NotContain(message => message.StartsWith("Gateway session failed", StringComparison.Ordinal));
            logs.Should().NotContain(message => message.Contains("fenced stale PTY", StringComparison.Ordinal));
            var snapshot = await router.GetSnapshotAsync(key, cancellationToken);
            snapshot.Status.Should().Be(ClientPresenceStatus.Online);
            snapshot.ConnectionId.Should().Be(originalOwner.ConnectionId);
            snapshot.ConnectionEpoch.Should().Be(checked((long)originalOwner.ConnectionEpoch));
            snapshot.AuthenticationExpiresAtUtc.Should().BeAfter(DateTimeOffset.UtcNow + duration);
            await registry.CloseAsync(sessionId, generation, "renewal_test_completed", cancellationToken);
            sessionId = null;
        }
        finally
        {
            if (sessionId is not null && registry.Get(sessionId)?.State == "opened")
            {
                using var closeDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await registry.CloseAsync(sessionId, generation, "renewal_test_cleanup", closeDeadline.Token);
            }
            stopping.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(8));
            directory.Delete(recursive: true);
        }
    }

    internal static async Task ProbeFilesAsync(IAgentFileGatewaySessionRegistry files, ClientKey client,
        string directory, string path, byte[] expected, CancellationToken cancellationToken)
    {
        var listing = await files.ListAsync(client, directory, 32, cancellationToken);
        listing.Should().Contain(entry => entry.FullPath == path && !entry.IsDirectory);
        var read = await files.ReadAsync(client, path, cancellationToken);
        using var bytes = new MemoryStream();
        await foreach (var chunk in read.Chunks.ReadAllAsync(cancellationToken))
            await bytes.WriteAsync(chunk, cancellationToken);
        await read.Completion.WaitAsync(cancellationToken);
        SHA256.HashData(bytes.ToArray()).Should().Equal(SHA256.HashData(expected));
    }

    internal static async Task<string> ProbeShellAsync(IAgentTerminalSessionRegistry registry,
        GatewayTerminalOutputSubscription output, string sessionId, ulong generation, string marker, CancellationToken ct)
    {
        // No input echo contains the complete marker or Unix process ID.
        var command = OperatingSystem.IsWindows()
            ? "echo nrproof:%NETRATEL_RENEWAL_PROOF%:windows\r\n"
            : "printf 'nr%s:%s:%s\\n' 'proof' \"$NETRATEL_RENEWAL_PROOF\" \"$$\"\n";
        await registry.SendInputAsync(sessionId, generation, Encoding.UTF8.GetBytes(command), ct);
        var received = new StringBuilder();
        var pattern = $"nrproof:{Regex.Escape(marker)}:([0-9]+|windows)";
        while (true)
        {
            var chunk = await output.Reader.ReadAsync(ct);
            received.Append(Encoding.UTF8.GetString(chunk.Span));
            var match = Regex.Match(received.ToString(), pattern);
            if (match.Success) return match.Groups[1].Value;
        }
    }

    internal static void AddTerminalAndRenewalServices(IServiceCollection services, AgentGatewayRenewalTestCredentials credentials)
    {
        services.AddAuthentication("Agent").AddJwtBearer("Agent", credentials.Configure);
        services.AddSingleton<AgentGatewayRenewalAuthenticator>();
        services.AddSingleton<AgentGatewayAuthenticationLeaseRegistry>();
        services.AddSingleton<IMcpOperatorTerminalSessionStore, AgentTerminalGatewayServiceTests.RecordingTerminalSessionStore>();
        services.AddSingleton<IAgentTerminalSessionRegistry>(provider =>
            new AgentTerminalSessionRegistry(provider.GetRequiredService<TimeProvider>(), new RecordingRealtimeFanoutSink()));
        services.AddSingleton(NullLogger<AgentTerminalGatewayService>.Instance);
        services.AddSingleton<IAgentFileGatewaySessionRegistry, AgentFileGatewaySessionRegistry>();
        services.AddSingleton(NullLogger<AgentFileGatewayService>.Instance);
    }

    private static async Task<IHost> BuildHostAsync(AgentGatewayRenewalTestCredentials credentials, AuthenticatedAgentIdentity identity)
    {
        var builder = Host.CreateDefaultBuilder();
        builder.ConfigureWebHost(web =>
        {
            web.UseTestServer();
            web.ConfigureServices(services =>
            {
                services.AddRouting();
                services.AddAuthorization(options => options.AddPolicy("AgentGatewayAccess", policy =>
                    policy.RequireAuthenticatedUser().RequireAssertion(context => AgentGatewayIdentityResolver.TryResolve(context.User, out _, out _))));
                services.AddGrpc();
                var akkaOptions = new NetRatelAkkaOptions { HeartbeatIntervalSeconds = 1, MissedHeartbeatLimit = 10, HeartbeatGraceSeconds = 0 };
                services.AddSingleton(akkaOptions);
                services.AddSingleton(TimeProvider.System);
                services.AddAkka($"renewal-functional-{Guid.NewGuid():N}", (akka, _) => akka.WithActors((system, registry, _) =>
                    registry.Register<ClientPresenceRegion>(system.ActorOf(ClientPresenceRouterActor.Props(akkaOptions, ActorRefs.Nobody), "presence"))));
                services.AddSingleton<IClientPresenceRouter>(provider => new AkkaClientPresenceRouter(
                    provider.GetRequiredService<IRequiredActor<ClientPresenceRegion>>(), akkaOptions.AskTimeout));
                services.AddSingleton<NetRatel.Application.Agents.IAgentManagementService>(new RenewalAgentManagementService(identity));
                services.AddSingleton<IClientUpdateCatalog, RenewalEmptyUpdateCatalog>();
                services.AddSingleton<IClientUpdateActivationAuthority, RenewalUnusedUpdateAuthority>();
                services.AddSingleton(NullLogger<AgentGatewayService>.Instance);
                AddTerminalAndRenewalServices(services, credentials);
            });
            web.Configure(app =>
            {
                app.UseRouting();
                app.UseAuthentication();
                app.UseAuthorization();
                app.UseEndpoints(endpoints =>
                {
                    endpoints.MapGrpcService<AgentGatewayService>();
                    endpoints.MapGrpcService<AgentTerminalGatewayService>();
                    endpoints.MapGrpcService<AgentFileGatewayService>();
                });
            });
        });
        return await builder.StartAsync();
    }

    private sealed class RenewingSignedTokenService(AgentGatewayRenewalTestCredentials credentials, AuthenticatedAgentIdentity identity, TimeSpan duration) : IAgentTokenService
    {
        private int _requests;
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => Volatile.Read(ref _requests);
        public void AllowRenewal() => _ready.TrySetResult();
        public async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct)
        {
            var request = Interlocked.Increment(ref _requests);
            if (request > 1) await _ready.Task.WaitAsync(ct);
            var now = DateTimeOffset.UtcNow;
            var expiry = DateTimeOffset.FromUnixTimeSeconds(now.ToUnixTimeSeconds()) +
                (request <= 2 ? TimeSpan.FromSeconds(10) : duration + TimeSpan.FromMinutes(5));
            return (credentials.CreateToken(identity, expiry, now.AddMinutes(-1)), expiry);
        }
    }

    private sealed class RenewalEmptyUpdateCatalog : IClientUpdateCatalog
    {
        public long Revision => 0;
        public DateTimeOffset RefreshedAtUtc => DateTimeOffset.UtcNow;
        public ClientUpdateOfferSnapshot? GetOffer(int tenantId, Guid agentId, string runtimeId, string currentVersion, string channel) => null;
        public ClientUpdatePolicySnapshot GetPolicy(int tenantId, Guid agentId, long clientPolicyRevision) => new(0, false, false, false, null);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
    private sealed class RenewalUnusedUpdateAuthority : IClientUpdateActivationAuthority
    {
        public Task<ClientUpdateActivationResult> MarkReadmittedAsync(AuthenticatedAgentIdentity identity, Guid attemptId, Guid releaseId,
            string nonce, string agentVersion, Guid connectionId, long connectionEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<ClientUpdateActivationResult> ConfirmAsync(AuthenticatedAgentIdentity identity, Guid attemptId, Guid connectionId,
            long connectionEpoch, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
