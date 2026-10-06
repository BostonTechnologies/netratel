using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
using AwesomeAssertions;
using Grpc.Net.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NetRatel.API.Gateway;
using NetRatel.Application.ClientAuth;
using NetRatel.Application.Presence;
using NetRatel.Client.Service.Gateway;
using NetRatel.Client.Service.Terminal;
using NetRatel.Client.Services;

namespace NetRatel.Tests.API;

/// <summary>Functional negative/recovery probes inside the existing disposable hosted ingress lane.</summary>
internal static class GatewayFunctionalInterruptionProbe
{
    internal static Task RunLateTerminalDefaultDeadlineAsync(IHost host, string endpoint,
        Func<HttpMessageHandler> createHttpHandler, AgentGatewayRenewalTestCredentials credentials,
        int tenantId, Guid agentId, CancellationToken cancellationToken, Action<string>? report = null) =>
        RunAsync(host, endpoint, createHttpHandler, credentials, tenantId, agentId,
            lateCapabilities: true, interruptPresence: null, cancellationToken, report);

    internal static Task RunControlledInterruptionAsync(IHost host, string endpoint,
        Func<HttpMessageHandler> createHttpHandler, AgentGatewayRenewalTestCredentials credentials,
        int tenantId, Guid agentId, Action interruptPresence, CancellationToken cancellationToken,
        Action<string>? report = null) =>
        RunAsync(host, endpoint, createHttpHandler, credentials, tenantId, agentId,
            lateCapabilities: false, interruptPresence, cancellationToken, report);

    private static async Task RunAsync(IHost host, string endpoint, Func<HttpMessageHandler> createHttpHandler,
        AgentGatewayRenewalTestCredentials credentials, int tenantId, Guid agentId,
        bool lateCapabilities, Action? interruptPresence, CancellationToken cancellationToken, Action<string>? report)
    {
        var terminals = host.Services.GetRequiredService<IAgentTerminalSessionRegistry>();
        var files = host.Services.GetRequiredService<IAgentFileGatewaySessionRegistry>();
        var router = host.Services.GetRequiredService<IClientPresenceRouter>();
        var key = new ClientKey(tenantId, agentId);
        var options = new GatewayClientOptions { Endpoint = endpoint };
        var logs = new ConcurrentQueue<string>();
        var failures = 0;
        var owners = Channel.CreateBounded<GatewayPresenceSession>(4);
        var firstFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerCount = 0;
        var started = Stopwatch.GetTimestamp();
        void Log(string message)
        {
            if (logs.Count < 64) logs.Enqueue(message);
            report?.Invoke(message);
            if (message.StartsWith("Gateway session failed:", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref failures);
                firstFailure.TrySetResult();
            }
        }
        var shell = OperatingSystem.IsWindows() ? "cmd" : "sh";
        using var terminalClient = new NetRatel.Client.Service.Gateway.AgentTerminalGatewayClient(options,
            new TerminalHostOptions
            {
                BackendPreference = OperatingSystem.IsWindows() ? TerminalBackendPreference.ConPty : TerminalBackendPreference.UnixPty
            }, [shell], Log,
            _ => GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = createHttpHandler() }));
        var fileClient = new AgentFileGatewayClient(options, new FileSystemService(), Log,
            _ => GrpcChannel.ForAddress(endpoint, new GrpcChannelOptions { HttpHandler = createHttpHandler() }));
        var tokenService = new SignedTokenService(credentials, new(tenantId, agentId));
        var presenceClient = new AgentGatewayPresenceClient(options, tokenService, tenantId, agentId,
            "interruption-functional-test", [], Log, runForPresenceSession: async (owner, accessToken, lifetime) =>
            {
                var ordinal = Interlocked.Increment(ref ownerCount);
                owners.Writer.TryWrite(owner).Should().BeTrue("this probe allows only bounded owner transitions");
                if (lateCapabilities && ordinal == 1)
                    await Task.Delay(TimeSpan.FromSeconds(54), lifetime);
                await Task.WhenAll(terminalClient.RunForPresenceSessionAsync(owner, accessToken, lifetime),
                    fileClient.RunForPresenceSessionAsync(owner, accessToken, lifetime));
            }, createHttpHandler: _ => createHttpHandler());
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var run = presenceClient.RunAsync(stopping.Token);
        var directory = Directory.CreateTempSubdirectory("netratel-interruption-functional-");
        var path = Path.Combine(directory.FullName, "proof.txt");
        var bytes = Encoding.UTF8.GetBytes("Controlled gateway interruption " + Guid.NewGuid());
        await File.WriteAllBytesAsync(path, bytes, cancellationToken);
        string? liveSessionId = null;
        ulong liveGeneration = 0;
        try
        {
            var originalOwner = await owners.Reader.ReadAsync(cancellationToken);
            if (lateCapabilities) await Task.Delay(TimeSpan.FromSeconds(54), cancellationToken);
            using var operationBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            operationBudget.CancelAfter(TimeSpan.FromSeconds(15));
            await WaitForReadinessAsync(terminals, files, key, originalOwner, operationBudget.Token);
            var originalTerminal = await OpenAndProbeAsync(terminals, files, key, shell,
                directory.FullName, path, bytes, operationBudget.Token);
            liveSessionId = originalTerminal.SessionId;
            liveGeneration = originalTerminal.Generation;
            var terminalOpenedAt = Stopwatch.GetElapsedTime(started);

            if (lateCapabilities)
            {
                terminalOpenedAt.TotalSeconds.Should().BeInRange(53, 59.9,
                    "the actual terminal must be usable just before the original presence request reaches its old 60-second boundary");
                await firstFailure.Task.WaitAsync(cancellationToken);
                var failureAge = Stopwatch.GetElapsedTime(started);
                failureAge.TotalSeconds.Should().BeInRange(55, 85);
                // Stop the native owner's one recovery loop once the negative
                // profile has produced its expected initiating failure.
                stopping.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(8));
                using var retirementBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                retirementBudget.CancelAfter(TimeSpan.FromSeconds(10));
                await WaitUntilAsync(() => terminals.GetAvailability(key) is not { } availability ||
                    availability.ConnectionId != originalOwner.ConnectionId, retirementBudget.Token);
                var outcome = terminals.Get(originalTerminal.SessionId);
                (outcome is null || outcome.State != "opened").Should().BeTrue(
                    "the old terminal cannot remain ready after its presence owner has failed");
                ownerCount.Should().Be(1);
                failures.Should().Be(1);
                report?.Invoke($"functional.default-deadline terminalOpenedSeconds={terminalOpenedAt.TotalSeconds:F1} parentFailureSeconds={failureAge.TotalSeconds:F1} terminalOutcome={outcome?.State ?? "removed"} capability=unavailable initiatingFailures={failures}");
                liveSessionId = null;
                return;
            }

            var heartbeatInterval = TimeSpan.FromSeconds(host.Services.GetRequiredService<NetRatel.Akka.Configuration.NetRatelAkkaOptions>().HeartbeatIntervalSeconds);
            using var recoveryBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            // An idle native owner detects a reset at its next heartbeat. Give
            // this phase its own negotiated detection + configured cleanup,
            // retry/admission budget, rather than consuming the initial probe's
            // budget before the deliberate interruption has even happened.
            recoveryBudget.CancelAfter(heartbeatInterval + TimeSpan.FromSeconds(
                options.PresenceTeardownTimeoutSeconds + options.PresenceBootstrapTimeoutSeconds + 2));
            var interruptedAt = Stopwatch.GetTimestamp();
            interruptPresence!();
            await firstFailure.Task.WaitAsync(recoveryBudget.Token);
            var detectionAge = Stopwatch.GetElapsedTime(interruptedAt);
            detectionAge.Should().BeLessThan(heartbeatInterval + TimeSpan.FromSeconds(options.PresenceTeardownTimeoutSeconds + 2));
            var successor = await owners.Reader.ReadAsync(recoveryBudget.Token);
            successor.ConnectionId.Should().NotBe(originalOwner.ConnectionId);
            successor.ConnectionEpoch.Should().BeGreaterThan(originalOwner.ConnectionEpoch);
            await WaitForReadinessAsync(terminals, files, key, successor, recoveryBudget.Token);
            var snapshot = await router.GetSnapshotAsync(key, recoveryBudget.Token);
            snapshot.Status.Should().Be(ClientPresenceStatus.Online);
            snapshot.ConnectionId.Should().Be(successor.ConnectionId);
            snapshot.ConnectionEpoch.Should().Be(checked((long)successor.ConnectionEpoch));
            var interruptedTerminal = terminals.Get(originalTerminal.SessionId);
            interruptedTerminal.Should().NotBeNull();
            interruptedTerminal!.State.Should().Be("failed");
            interruptedTerminal.FailureCode.Should().Be("terminal_presence_fence_replaced");
            // The old input has already produced observed output. Recovery
            // issues fresh read/list requests and explicitly opens a new shell;
            // it never resends old terminal input or claims PTY continuity.
            var replacement = await OpenAndProbeAsync(terminals, files, key, shell,
                directory.FullName, path, bytes, recoveryBudget.Token);
            replacement.SessionId.Should().NotBe(originalTerminal.SessionId);
            liveSessionId = replacement.SessionId;
            liveGeneration = replacement.Generation;
            ownerCount.Should().Be(2);
            failures.Should().Be(1);
            logs.Should().NotContain(message => message.StartsWith("Agent token acquisition failed", StringComparison.Ordinal));
            report?.Invoke($"functional.controlled-interruption resetDetectedSeconds={detectionAge.TotalSeconds:F1} initiatingFailures={failures} presenceOwners={ownerCount} oldTerminalOutcome={interruptedTerminal.State} oldTerminalReason={interruptedTerminal.FailureCode} replacementTerminal=explicitly-opened fileListingAndIntegrity=passed");
        }
        finally
        {
            try
            {
                if (liveSessionId is not null && terminals.Get(liveSessionId)?.State == "opened")
                {
                    using var closeBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await terminals.CloseAsync(liveSessionId, liveGeneration, "interruption_test_cleanup", closeBudget.Token);
                }
            }
            finally
            {
                stopping.Cancel();
                await run.WaitAsync(TimeSpan.FromSeconds(8));
                directory.Delete(recursive: true);
            }
        }
    }

    private static async Task<GatewayTerminalSession> OpenAndProbeAsync(IAgentTerminalSessionRegistry terminals,
        IAgentFileGatewaySessionRegistry files, ClientKey key, string shell, string directory,
        string path, byte[] bytes, CancellationToken cancellationToken)
    {
        var terminal = await terminals.OpenAsync(key, shell, null, 100, 30, cancellationToken);
        await WaitUntilAsync(() => terminals.Get(terminal.SessionId)?.State == "opened", cancellationToken);
        using var output = terminals.Subscribe(terminal.SessionId, terminal.Generation);
        var marker = Guid.NewGuid().ToString("N");
        var initialize = OperatingSystem.IsWindows()
            ? $"set NETRATEL_RENEWAL_PROOF={marker}\r\n" : $"NETRATEL_RENEWAL_PROOF='{marker}'\n";
        await terminals.SendInputAsync(terminal.SessionId, terminal.Generation, Encoding.UTF8.GetBytes(initialize), cancellationToken);
        await AgentGatewayRenewalTerminalTests.ProbeShellAsync(terminals, output, terminal.SessionId,
            terminal.Generation, marker, cancellationToken);
        await terminals.ResizeAsync(terminal.SessionId, terminal.Generation, 110, 35, cancellationToken);
        await AgentGatewayRenewalTerminalTests.ProbeFilesAsync(files, key, directory, path, bytes, cancellationToken);
        return terminal;
    }

    private static Task WaitForReadinessAsync(IAgentTerminalSessionRegistry terminals,
        IAgentFileGatewaySessionRegistry files, ClientKey key, GatewayPresenceSession owner, CancellationToken cancellationToken) =>
        WaitUntilAsync(() => terminals.GetAvailability(key) is { } terminal &&
            terminal.ConnectionId == owner.ConnectionId && terminal.ConnectionEpoch == owner.ConnectionEpoch &&
            files.GetAvailability(key) is { } file && file.ConnectionId == owner.ConnectionId &&
            file.ConnectionEpoch == owner.ConnectionEpoch, cancellationToken);

    private static async Task WaitUntilAsync(Func<bool> ready, CancellationToken cancellationToken)
    {
        while (!ready()) await Task.Delay(TimeSpan.FromMilliseconds(20), cancellationToken);
    }

    private sealed class SignedTokenService(AgentGatewayRenewalTestCredentials credentials,
        AuthenticatedAgentIdentity identity) : IAgentTokenService
    {
        private readonly DateTimeOffset _expiry = DateTimeOffset.UtcNow.AddHours(2);
        public Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> GetAccessTokenAsync(CancellationToken ct) =>
            Task.FromResult((credentials.CreateToken(identity, _expiry, DateTimeOffset.UtcNow.AddMinutes(-1)), _expiry));
    }
}
