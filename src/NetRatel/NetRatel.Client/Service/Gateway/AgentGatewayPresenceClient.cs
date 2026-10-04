using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Updates;
using NetRatel.Client.Service.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NetRatel.Client.Service.Gateway;

/// <summary>
/// Maintains the authenticated Akka gateway presence session.
/// It deliberately has no SpacetimeDB fallback: an unavailable or wrongly configured
/// gateway leaves the agent offline rather than silently restoring legacy ownership.
/// </summary>
public sealed class AgentGatewayPresenceClient(
    GatewayClientOptions options,
    IAgentTokenService tokenService,
    int tenantId,
    Guid agentId,
    string agentVersion,
    IReadOnlyList<string> terminalShells,
    Action<string> log,
    Func<GatewayPresenceSession, string, CancellationToken, Task>? runForPresenceSession = null,
    IAgentGatewayUpdateHandler? updateHandler = null,
    Func<Uri, GrpcChannel>? createChannel = null,
    TimeSpan? extensionShutdownTimeout = null,
    Func<Uri, HttpMessageHandler>? createHttpHandler = null,
    TimeProvider? timeProvider = null,
    Func<double>? nextRandom = null,
    Func<Metadata, CancellationToken, AsyncDuplexStreamingCall<AgentFrame, GatewayFrame>>? createCall = null)
{
    private static readonly TimeSpan DefaultExtensionShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _extensionShutdownTimeout = ResolveExtensionShutdownTimeout(extensionShutdownTimeout);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        ValidateOptions();
        var retry = new GatewayReconnectPolicy(_timeProvider, nextRandom ?? Random.Shared.NextDouble,
            TimeSpan.FromSeconds(options.PresenceStabilityThresholdSeconds));
        string? lastFailureKey = null;
        long lastFailureLog = 0;
        var suppressedFailures = 0;
        while (!stoppingToken.IsCancellationRequested)
        {
            GatewaySessionDiagnostics? diagnostics = null;
            try
            {
                var token = await DisabledAgentTokenRetry.GetAccessTokenAsync(tokenService, log, stoppingToken).ConfigureAwait(false);
                diagnostics = new GatewaySessionDiagnostics(options.Endpoint);
                retry.BeginAttempt();
                await RunSessionAsync(token.AccessToken, token.ExpiresAtUtc, diagnostics, retry.Acknowledged, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (RpcException exception) when (stoppingToken.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled)
            {
                break;
            }
            catch (AgentClientAuthException exception)
            {
                log($"Agent token acquisition failed: {exception.GetType().Name}; presence remains offline.");
                throw;
            }
            catch (Exception exception) when (exception is RpcException or HttpRequestException or IOException or OperationCanceledException)
            {
                var retryDelay = retry.FailureDelay();
                var failure = (diagnostics ?? new GatewaySessionDiagnostics(options.Endpoint)).Failure(exception, retryDelay);
                if (failure.Key != lastFailureKey || _timeProvider.GetElapsedTime(lastFailureLog) >= TimeSpan.FromSeconds(30))
                {
                    log($"{failure.Message} suppressedRepeatedFailures={suppressedFailures}.");
                    lastFailureKey = failure.Key;
                    lastFailureLog = _timeProvider.GetTimestamp();
                    suppressedFailures = 0;
                }
                else
                {
                    suppressedFailures++;
                }
                try
                {
                    await Task.Delay(retryDelay, _timeProvider, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunSessionAsync(string accessToken, DateTimeOffset expiresAtUtc,
        GatewaySessionDiagnostics diagnostics, Action acknowledged, CancellationToken stoppingToken)
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Gateway:Endpoint must be an absolute HTTPS URL.");

        using var channel = createChannel?.Invoke(endpoint) ?? GrpcChannel.ForAddress(endpoint,
            new GrpcChannelOptions
            {
                HttpHandler = new GatewayHttpDiagnosticsHandler(diagnostics,
                    createHttpHandler?.Invoke(endpoint) ?? new SocketsHttpHandler { EnableMultipleHttp2Connections = true }),
                DisposeHttpClient = true
            });
        using var owned = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var client = new global::NetRatel.AgentGateway.Contracts.V1.AgentGateway.AgentGatewayClient(channel);
        var headers = new Metadata { { "Authorization", $"Bearer {accessToken}" } };
        using var call = createCall?.Invoke(headers, owned.Token) ?? client.Connect(headers, cancellationToken: owned.Token);
        var pending = new List<Task>();
        Task? sessionTask = null;
        Task? renewalDue = null;
        DateTimeOffset renewalAtUtc = default;
        var ioBudget = TimeSpan.FromSeconds(options.PresenceBootstrapTimeoutSeconds);
        var renewalIoGrace = TimeSpan.Zero;
        long? renewalGraceStarted = null;

        Task WaitForRenewalAsync()
        {
            var delay = renewalAtUtc - _timeProvider.GetUtcNow();
            // System timers have millisecond granularity. Rounding up and checking
            // UTC again when due avoids requesting a still-cached credential.
            if (delay > TimeSpan.Zero)
                delay = TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds));
            return Task.Delay(delay > TimeSpan.Zero ? delay : TimeSpan.Zero, _timeProvider, owned.Token);
        }

        async Task<T> AwaitIoAsync<T>(Task<T> operation, TimeSpan budget, bool watchRenewal = false)
        {
            pending.Add(operation);
            var started = _timeProvider.GetTimestamp();
            TimeSpan RemainingBudget() => budget - _timeProvider.GetElapsedTime(started);
            try
            {
                while (watchRenewal && renewalDue is not null && !operation.IsCompleted)
                {
                    var remainingWait = RemainingBudget();
                    if (remainingWait <= TimeSpan.Zero) throw new TimeoutException();
                    var winner = await Task.WhenAny(operation, renewalDue).WaitAsync(remainingWait, _timeProvider, owned.Token).ConfigureAwait(false);
                    owned.Token.ThrowIfCancellationRequested();
                    if (winner == renewalDue && !operation.IsCompleted)
                    {
                        if (_timeProvider.GetUtcNow() < renewalAtUtc)
                        {
                            renewalDue = WaitForRenewalAsync();
                            continue;
                        }
                        // A due timer is a scheduling boundary, not evidence that
                        // healthy I/O is stalled. Let the one pending exchange
                        // finish within its original watchdog, one heartbeat
                        // interval and half the remaining authenticated lifetime.
                        var remaining = RemainingBudget();
                        renewalGraceStarted ??= _timeProvider.GetTimestamp();
                        var graceRemaining = renewalIoGrace - _timeProvider.GetElapsedTime(renewalGraceStarted.Value);
                        var authorityMargin = (expiresAtUtc - _timeProvider.GetUtcNow()).Ticks / 2;
                        var grace = TimeSpan.FromTicks(Math.Max(0, Math.Min(graceRemaining.Ticks,
                            Math.Min(remaining.Ticks, authorityMargin))));
                        log($"Token renewal became due while presence I/O was pending. {diagnostics.AdmissionSummary}");
                        if (grace > TimeSpan.Zero)
                        {
                            try { return await operation.WaitAsync(grace, _timeProvider, owned.Token).ConfigureAwait(false); }
                            catch (TimeoutException) { owned.Token.ThrowIfCancellationRequested(); }
                        }
                        // A cancelled MoveNext invalidates this RPC. Retire it;
                        // never start a competing reader or reuse the old call.
                        diagnostics.FailureReason("renewal_io_blocked");
                        throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Presence I/O blocked token renewal."));
                    }
                    break;
                }
                if (operation.IsCompleted) return await operation.ConfigureAwait(false);
                var remainingBudget = RemainingBudget();
                if (remainingBudget <= TimeSpan.Zero) throw new TimeoutException();
                return await operation.WaitAsync(remainingBudget, _timeProvider, owned.Token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                owned.Token.ThrowIfCancellationRequested();
                diagnostics.FailureReason("presence_io_timeout");
                throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Presence I/O exceeded its liveness budget."));
            }
            finally
            {
                if (operation.IsCompleted) pending.Remove(operation);
            }
        }

        async Task AwaitWriteAsync(AgentFrame frame, TimeSpan budget, bool watchRenewal = false)
        {
            // Cancellation reaches the physical write, rather than only its waiter.
            var operation = call.RequestStream.WriteAsync(frame, owned.Token);
            await AwaitIoAsync(CompleteWriteAsync(operation), budget, watchRenewal).ConfigureAwait(false);
        }

        try
        {
            var operationId = Guid.NewGuid();
            var hello = new ConnectHello { AgentVersion = agentVersion };
            hello.Capabilities.Add("presence");
            hello.Capabilities.Add("heartbeat-latency");
            hello.Capabilities.Add("presence-auth-renewal-v1");
            // Retain this legacy wire token verbatim; telemetry itself now uses the V2 stream.
            hello.Capabilities.Add("telemetry-shadow");
            hello.Capabilities.Add("file-gateway");
            hello.Capabilities.Add("log-gateway");
            hello.Capabilities.Add("remote-support-gateway");
            hello.Capabilities.Add("remote-support-v2-inventory");
            hello.Capabilities.Add("terminal-gateway");
            hello.TerminalCapability = new TerminalCapability { Supported = true, AvailableShells = { terminalShells } };
            updateHandler?.PopulateHello(hello);

            var bootstrapStarted = _timeProvider.GetTimestamp();
            await AwaitWriteAsync(new AgentFrame
            {
                ProtocolVersion = options.ProtocolVersion, TenantId = tenantId, ClientId = agentId.ToString("D"),
                ConnectionId = Guid.NewGuid().ToString("D"), OperationId = operationId.ToString("D"), Sequence = 0, Hello = hello
            }, ioBudget).ConfigureAwait(false);
            var remainingBootstrap = ioBudget - _timeProvider.GetElapsedTime(bootstrapStarted);
            if (remainingBootstrap <= TimeSpan.Zero ||
                !await AwaitIoAsync(call.ResponseStream.MoveNext(owned.Token), remainingBootstrap).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.Connected)
                throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before accepting the presence session."));

            var accepted = call.ResponseStream.Current;
            ValidateConnectedFrame(accepted, operationId, diagnostics);
            if (!GatewayWireProtocol.HasAkkaAuthority(accepted.Connected.PresenceAuthority))
            {
                diagnostics.ProtocolFailure("unsupported presence authority token");
                throw new RpcException(new Status(StatusCode.FailedPrecondition, "Gateway returned an unsupported presence authority token."));
            }
            var (heartbeatInterval, heartbeatTimeout) = ValidateHeartbeatPolicy(accepted.Connected, diagnostics);
            ioBudget = heartbeatTimeout;
            renewalIoGrace = heartbeatInterval;
            NotifyUpdateHandler(accepted.Connected.UpdateOffer, accepted.Connected.UpdatePolicy, confirmation: null);
            updateHandler?.OnPresenceConnected(accepted.ConnectionEpoch);
            var acceptedConnectionId = Guid.Parse(accepted.ConnectionId);
            diagnostics.Admitted(acceptedConnectionId);
            log($"Presence admitted. {diagnostics.AdmissionSummary}, connectionEpoch={accepted.ConnectionEpoch}, heartbeatInterval={heartbeatInterval.TotalSeconds:0}s.");

            var session = new GatewayPresenceSession(tenantId, agentId, accepted.ConnectionEpoch, acceptedConnectionId);
            session.SetAccessToken(accessToken);
            ulong sequence = 0;
            double? lastAcknowledgedRoundTripMs = null;
            ulong lastAcknowledgedSequence = 0;

            Task ScheduleRenewal()
            {
                renewalGraceStarted = null;
                var now = _timeProvider.GetUtcNow();
                var delay = expiresAtUtc.AddMinutes(-1) - now;
                if (delay <= TimeSpan.Zero && expiresAtUtc > now)
                    delay = TimeSpan.FromTicks(Math.Min(heartbeatInterval.Ticks, (expiresAtUtc - now).Ticks / 2));
                // Short test/negotiated leases leave half their remaining lifetime
                // for fresh authentication. Already-expired legacy fake tokens still
                // prove a first heartbeat before the reconnect path is exercised.
                renewalAtUtc = now + (delay > TimeSpan.Zero ? delay : heartbeatInterval);
                return WaitForRenewalAsync();
            }
            renewalDue = ScheduleRenewal();

            AgentFrame Envelope(Guid operation, ulong frameSequence) => new()
            {
                ProtocolVersion = options.ProtocolVersion, TenantId = tenantId, ClientId = agentId.ToString("D"),
                ConnectionEpoch = accepted.ConnectionEpoch, ConnectionId = accepted.ConnectionId,
                OperationId = operation.ToString("D"), Sequence = frameSequence
            };

            async Task SendHeartbeatAsync()
            {
                var heartbeatOperation = Guid.NewGuid();
                var heartbeatSequence = ++sequence;
                updateHandler?.OnActivationHeartbeatSent(accepted.ConnectionEpoch);
                var heartbeat = new PresenceHeartbeat { ObservedAtUtc = Timestamp.FromDateTimeOffset(_timeProvider.GetUtcNow()) };
                if (lastAcknowledgedRoundTripMs is { } roundTripMs)
                {
                    heartbeat.AcknowledgedHeartbeatRoundTripMs = roundTripMs;
                    heartbeat.AcknowledgedHeartbeatSequence = lastAcknowledgedSequence;
                }
                var started = _timeProvider.GetTimestamp();
                var frame = Envelope(heartbeatOperation, heartbeatSequence);
                frame.Heartbeat = heartbeat;
                await AwaitWriteAsync(frame, ioBudget, watchRenewal: true).ConfigureAwait(false);
                if (!await AwaitIoAsync(call.ResponseStream.MoveNext(owned.Token), ioBudget, watchRenewal: true).ConfigureAwait(false) ||
                    call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.HeartbeatAccepted)
                    throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before acknowledging the heartbeat."));

                var response = call.ResponseStream.Current;
                ValidateHeartbeatFrame(response, accepted, heartbeatOperation, heartbeatSequence, diagnostics);
                if (!GatewayWireProtocol.HasAkkaAuthority(response.HeartbeatAccepted.PresenceAuthority))
                {
                    diagnostics.ProtocolFailure("unsupported heartbeat authority token");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Gateway returned an unsupported heartbeat authority token."));
                }
                diagnostics.AcknowledgeHeartbeat();
                acknowledged();
                var measured = _timeProvider.GetElapsedTime(started).TotalMilliseconds;
                lastAcknowledgedRoundTripMs = double.IsFinite(measured) && measured >= 0 && measured <= heartbeatTimeout.TotalMilliseconds ? measured : null;
                lastAcknowledgedSequence = heartbeatSequence;
                updateHandler?.OnActivationHeartbeatAccepted(accepted.ConnectionEpoch);
                NotifyUpdateHandler(response.HeartbeatAccepted.UpdateOffer, response.HeartbeatAccepted.UpdatePolicy,
                    response.HeartbeatAccepted.UpdateConfirmation);
            }

            // Validate readiness before starting optional children or activation.
            await SendHeartbeatAsync().ConfigureAwait(false);
            sessionTask = runForPresenceSession?.Invoke(session, accessToken, owned.Token);
            while (!owned.IsCancellationRequested)
            {
                using var intervalStopping = CancellationTokenSource.CreateLinkedTokenSource(owned.Token);
                var interval = Task.Delay(heartbeatInterval, _timeProvider, intervalStopping.Token);
                await Task.WhenAny(interval, renewalDue).ConfigureAwait(false);
                owned.Token.ThrowIfCancellationRequested();
                if (renewalDue.IsCompleted)
                {
                    intervalStopping.Cancel();
                    try { await interval.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (intervalStopping.IsCancellationRequested) { }
                    if (_timeProvider.GetUtcNow() < renewalAtUtc)
                    {
                        renewalDue = WaitForRenewalAsync();
                        continue;
                    }
                    if (!accepted.Connected.SupportsAuthenticatedRenewal)
                    {
                        log($"Refreshing the gateway session before the agent token expires. {diagnostics.AdmissionSummary}");
                        return;
                    }

                    // The single presence owner acquires fresh authority, then performs
                    // one bounded request/ACK exchange; children retain their fence.
                    async Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)> AcquireRenewalTokenAsync()
                    {
                        try { return await tokenService.GetAccessTokenAsync(owned.Token).ConfigureAwait(false); }
                        catch (AgentClientAuthException exception) when (exception.StatusCode == 403 && !exception.ShouldClearCredentials &&
                            string.Equals(exception.Code, "agent_disabled", StringComparison.Ordinal))
                        {
                            // Retire authority immediately; the outer offline auth
                            // loop can wait for reversible administrative enablement.
                            throw new RpcException(new Status(StatusCode.PermissionDenied, "Agent disabled during authentication renewal."));
                        }
                    }
                    var fresh = await AwaitIoAsync(AcquireRenewalTokenAsync(), ioBudget).ConfigureAwait(false);
                    if (fresh.ExpiresAtUtc <= expiresAtUtc || fresh.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent token renewal did not extend authority."));
                    var renewalOperation = Guid.NewGuid();
                    var renewalSequence = ++sequence;
                    var frame = Envelope(renewalOperation, renewalSequence);
                    frame.Renew = new PresenceAuthRenewal { AccessToken = fresh.AccessToken };
                    await AwaitWriteAsync(frame, ioBudget).ConfigureAwait(false);
                    if (!await AwaitIoAsync(call.ResponseStream.MoveNext(owned.Token), ioBudget).ConfigureAwait(false) ||
                        call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.Renewed)
                        throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before acknowledging authentication renewal."));
                    var renewed = call.ResponseStream.Current;
                    ValidateHeartbeatFrame(renewed, accepted, renewalOperation, renewalSequence, diagnostics);
                    var serverExpiry = renewed.Renewed.ExpiresAtUtc?.ToDateTimeOffset();
                    if (!GatewayWireProtocol.HasAkkaAuthority(renewed.Renewed.PresenceAuthority) ||
                        serverExpiry is null || serverExpiry <= expiresAtUtc || serverExpiry <= _timeProvider.GetUtcNow())
                        throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid authentication renewal acknowledgement."));
                    expiresAtUtc = serverExpiry.Value;
                    session.SetAccessToken(fresh.AccessToken);
                    diagnostics.AcknowledgeHeartbeat();
                    acknowledged();
                    // Renewal ACK is not a heartbeat RTT sample.
                    lastAcknowledgedRoundTripMs = null;
                    lastAcknowledgedSequence = 0;
                    log($"Presence authentication renewed. {diagnostics.AdmissionSummary}, connectionEpoch={accepted.ConnectionEpoch}, expiresAtUtc={expiresAtUtc:O}.");
                    renewalDue = ScheduleRenewal();
                }
                else
                {
                    await interval.ConfigureAwait(false);
                    await SendHeartbeatAsync().ConfigureAwait(false);
                }
            }
        }
        catch
        {
            diagnostics.SessionFailed();
            throw;
        }
        finally
        {
            // Abort this exact physical call before any replacement is admitted.
            // A losing read is never cancelled and reused on the same RPC.
            owned.Cancel();
            call.Dispose();
            await ObserveRetiredIoAsync(pending).ConfigureAwait(false);
            await StopSessionExtensionsAsync(sessionTask, owned.Token, stoppingToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> CompleteWriteAsync(Task operation)
    {
        await operation.ConfigureAwait(false);
        return true;
    }

    private async Task ObserveRetiredIoAsync(List<Task> pending)
    {
        if (pending.Count == 0) return;
        var all = Task.WhenAll(pending);
        try
        {
            await all.WaitAsync(TimeSpan.FromSeconds(options.PresenceTeardownTimeoutSeconds), _timeProvider, CancellationToken.None).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            log("Retired presence I/O exceeded its abort join budget; late completion remains observed.");
            ObserveLateTask(all);
        }
        catch (Exception)
        {
            // Aborted reads and writes are observed here; the initiating failure is preserved.
        }
    }

    private static void ObserveLateTask(Task task)
        => _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void ValidateOptions()
    {
        if (options.PresenceBootstrapTimeoutSeconds is < 1 or > 300 || options.PresenceTeardownTimeoutSeconds is < 1 or > 30 ||
            options.PresenceStabilityThresholdSeconds is <= 60 or > 3600)
            throw new InvalidOperationException("Gateway presence budgets must be valid; the stability threshold must exceed 60 seconds.");
    }

    private static (TimeSpan Interval, TimeSpan Timeout) ValidateHeartbeatPolicy(ConnectAccepted connected, GatewaySessionDiagnostics diagnostics)
    {
        if (connected.HeartbeatIntervalSeconds is < 1 or > 300 || connected.HeartbeatTimeoutSeconds < connected.HeartbeatIntervalSeconds ||
            connected.HeartbeatTimeoutSeconds > 3060)
        {
            diagnostics.ProtocolFailure("invalid heartbeat policy");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid heartbeat policy."));
        }
        return (TimeSpan.FromSeconds(connected.HeartbeatIntervalSeconds), TimeSpan.FromSeconds(connected.HeartbeatTimeoutSeconds));
    }

    private async Task StopSessionExtensionsAsync(
        Task? sessionTask,
        CancellationToken sessionStoppingToken,
        CancellationToken applicationStoppingToken)
    {
        if (sessionTask is null) return;

        try
        {
            await sessionTask.WaitAsync(_extensionShutdownTimeout, _timeProvider, applicationStoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (applicationStoppingToken.IsCancellationRequested)
        {
            // Do not delay host shutdown while a non-presence extension is unwinding.
            log("Non-presence gateway extension shutdown was interrupted by service shutdown.");
            ObserveLateTask(sessionTask);
        }
        catch (OperationCanceledException) when (sessionStoppingToken.IsCancellationRequested)
        {
            log("Non-presence gateway session extension stopped with its presence owner.");
        }
        catch (TimeoutException)
        {
            // The extension was cancelled above. It must not prevent presence from obtaining a fresh token and re-admitting.
            log($"Non-presence gateway session extension did not stop within {_extensionShutdownTimeout.TotalSeconds:0.###}s; reconnecting presence.");
            ObserveLateTask(sessionTask);
        }
        catch (Exception exception)
        {
            // A non-authoritative extension must not take down the fenced presence stream.
            log($"Non-presence gateway session extension ended unexpectedly: {exception.GetType().Name}.");
        }
    }

    private static TimeSpan ResolveExtensionShutdownTimeout(TimeSpan? extensionShutdownTimeout)
    {
        var timeout = extensionShutdownTimeout ?? DefaultExtensionShutdownTimeout;
        return timeout > TimeSpan.Zero
            ? timeout
            : throw new ArgumentOutOfRangeException(nameof(extensionShutdownTimeout), "Extension shutdown timeout must be greater than zero.");
    }

    private void NotifyUpdateHandler(
        ClientUpdateOffer? offer,
        ClientUpdatePolicy? policy,
        UpdateActivationConfirmation? confirmation)
    {
        if (updateHandler is null) return;
        try
        {
            updateHandler.OnAcknowledgement(offer, policy, confirmation);
        }
        catch (Exception exception)
        {
            updateHandler?.RecordAcknowledgementFailure(exception);
            log($"Client update acknowledgement was ignored without affecting presence: {exception.GetType().Name}.");
        }
    }

    private void ValidateConnectedFrame(GatewayFrame frame, Guid operationId, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch == 0 || frame.Sequence != 0)
        {
            diagnostics.ProtocolFailure("invalid connect acknowledgement");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid connect acknowledgement."));
        }
    }

    private void ValidateHeartbeatFrame(GatewayFrame frame, GatewayFrame accepted, Guid operationId, ulong sequence, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != tenantId ||
            !string.Equals(frame.ClientId, agentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch != accepted.ConnectionEpoch ||
            !string.Equals(frame.ConnectionId, accepted.ConnectionId, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.Sequence != sequence)
        {
            diagnostics.ProtocolFailure("invalid heartbeat acknowledgement");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid heartbeat acknowledgement."));
        }
    }
}

public sealed record GatewayPresenceSession(
    int TenantId,
    Guid AgentId,
    ulong ConnectionEpoch,
    Guid ConnectionId)
{
    // Record copies share credentials; credentials are not part of fence equality.
    private readonly SessionCredential _credential = new();

    /// <summary>Returns the latest server-confirmed credential for an isolated child reconnect.</summary>
    public string GetAccessToken(string fallback) => Volatile.Read(ref _credential.AccessToken) ?? fallback;

    internal void SetAccessToken(string accessToken) => Volatile.Write(ref _credential.AccessToken, accessToken);

    public bool Equals(GatewayPresenceSession? other) => other is not null &&
        TenantId == other.TenantId && AgentId == other.AgentId &&
        ConnectionEpoch == other.ConnectionEpoch && ConnectionId == other.ConnectionId;

    public override int GetHashCode() => HashCode.Combine(TenantId, AgentId, ConnectionEpoch, ConnectionId);

    private sealed class SessionCredential
    {
        internal string? AccessToken;
    }
}
