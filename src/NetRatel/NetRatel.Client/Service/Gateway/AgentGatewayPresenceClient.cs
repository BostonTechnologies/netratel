using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Win32;
using NetRatel.AgentGateway.Contracts.V1;
using NetRatel.Application.ClientAuth;
using NetRatel.Client.Service.Updates;
using NetRatel.Client.Service.Auth;
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Authentication;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
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
    Func<Metadata, CancellationToken, AsyncDuplexStreamingCall<AgentFrame, GatewayFrame>>? createCall = null,
    OperationalRecoveryOptions? recoveryOptions = null,
    OperationalRecoveryStateStore? recoveryStateStore = null,
    Func<CancellationToken, Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)>>? acquireToken = null,
    Func<string, int?>? resolveTenantId = null,
    Func<CancellationToken, Task<Guid>>? resolveAgentId = null,
    Guid? pendingUpdateAttemptId = null)
{
    private static readonly TimeSpan DefaultExtensionShutdownTimeout = TimeSpan.FromSeconds(5);
    private readonly TimeSpan _extensionShutdownTimeout = ResolveExtensionShutdownTimeout(extensionShutdownTimeout);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        ValidateOptions();
        var settings = recoveryOptions ?? new OperationalRecoveryOptions();
        settings.Validate();
        var retry = new OperationalRecoveryPolicy(_timeProvider, nextRandom ?? Random.Shared.NextDouble,
            settings, recoveryStateStore);
        if (pendingUpdateAttemptId is { } updateAttempt && retry.TryConsumeUpdateAttempt(updateAttempt))
            log($"Recovery consumed the matching pending update attempt {updateAttempt:D}; outage history is retained.");
        string? lastFailureKey = null;
        long lastFailureLog = 0;
        var suppressedFailures = 0;
        NetworkAvailabilityChangedEventHandler networkRestored = (_, args) =>
        {
            if (args.IsAvailable) retry.NotifyRecoveryHint();
        };
        NetworkChange.NetworkAvailabilityChanged += networkRestored;
        PowerModeChangedEventHandler resumed = (_, args) =>
        {
            if (args.Mode == PowerModes.Resume) retry.NotifyRecoveryHint();
        };
        var powerNotifications = false;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                SystemEvents.PowerModeChanged += resumed;
                powerNotifications = true;
            }
            catch (InvalidOperationException)
            {
                // Session-zero hosts may not have a desktop event source. The
                // monotonic timer and network hint retain the same deadline.
                log("Power notification source is unavailable; operational recovery retains its timer and network notifications.");
            }
        }
        using var resume = OperatingSystem.IsWindows() ? null :
            PosixSignalRegistration.Create(PosixSignal.SIGCONT, _ => retry.NotifyRecoveryHint());
        try
        {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await retry.WaitForNextAttemptAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            retry.BeginAttempt();
            GatewaySessionDiagnostics? diagnostics = null;
            string? attemptedAccessToken = null;
            using var finiteBudget = new CancellationTokenSource(TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds), _timeProvider);
            using var attemptStopping = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, finiteBudget.Token);
            try
            {
                // One owner, one single-flight token acquisition. No nested disabled or
                // disconnected token retry loop runs behind the global deadline.
                var tokenWork = (acquireToken ?? tokenService.GetAccessTokenAsync)(attemptStopping.Token);
                (string AccessToken, DateTimeOffset ExpiresAtUtc) token;
                try { token = await tokenWork.WaitAsync(attemptStopping.Token).ConfigureAwait(false); }
                finally
                {
                    if (!tokenWork.IsCompleted)
                    {
                        attemptStopping.Cancel();
                        await ObserveRetiredIoAsync([tokenWork]).ConfigureAwait(false);
                    }
                }
                attemptedAccessToken = token.AccessToken;
                var sessionTenantId = resolveTenantId?.Invoke(token.AccessToken) ?? tenantId;
                if (sessionTenantId <= 0)
                    throw new AgentClientAuthException("The acquired agent token lacks a valid tenant binding.",
                        code: "invalid_access_token");
                var sessionAgentId = resolveAgentId is null ? agentId : await resolveAgentId(attemptStopping.Token).ConfigureAwait(false);
                diagnostics = new GatewaySessionDiagnostics(options.Endpoint, _timeProvider);
                var online = false;
                await RunSessionAsync(token.AccessToken, token.ExpiresAtUtc, sessionTenantId, sessionAgentId,
                    TimeSpan.FromSeconds(settings.AttemptTimeoutSeconds), diagnostics, heartbeatWatchdog =>
                    {
                        // The 30s budget covers finite auth/admission/first-heartbeat
                        // work only. A healthy presence stream has no finite lifetime.
                        if (!online)
                        {
                            finiteBudget.Dispose();
                            online = true;
                            log($"Operational state=Online, agentId={sessionAgentId:D}, tenantId={sessionTenantId}.");
                        }
                        if (retry.AuthoritativeHeartbeat(heartbeatWatchdog))
                            log("Authoritative heartbeats remained stable for 120s; outage retry history reset.");
                    }, currentAccessToken => attemptedAccessToken = currentAccessToken,
                    attemptStopping.Token).ConfigureAwait(false);
                stoppingToken.ThrowIfCancellationRequested();
                throw new InvalidOperationException("The operational presence owner returned unexpectedly.");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (RpcException exception) when (stoppingToken.IsCancellationRequested && exception.StatusCode == StatusCode.Cancelled) { break; }
            catch (Exception exception) when (OperationalRecoveryFailure.IsExpected(exception))
            {
                if (stoppingToken.IsCancellationRequested) break;
                if (exception is RpcException { StatusCode: StatusCode.Unauthenticated } && attemptedAccessToken is not null)
                    tokenService.InvalidateAccessToken(attemptedAccessToken);
                var attention = OperationalRecoveryFailure.RequiresAttention(exception);
                var retryAfter = (exception as AgentClientAuthException)?.RetryAfter ?? diagnostics?.RetryAfter;
                var retryDelay = retry.FailureDelay(retryAfter, attention ? TimeSpan.FromMinutes(5) : null);
                if (retry.RetryAfterCapped || (exception as AgentClientAuthException)?.RetryAfterWasCapped == true)
                    log("Retry-After exceeded the advisory ceiling; scheduled recovery wait is capped at 600s.");
                diagnostics ??= new GatewaySessionDiagnostics(options.Endpoint, _timeProvider);
                if (exception is OperationCanceledException && finiteBudget.IsCancellationRequested)
                    diagnostics.FailureReason("finite_attempt_timeout");
                var failure = diagnostics.Failure(exception, retryDelay);
                var failureKey = $"{attention}/{failure.Key}/{(exception as AgentClientAuthException)?.Code}";
                if (failureKey != lastFailureKey || _timeProvider.GetElapsedTime(lastFailureLog) >= TimeSpan.FromSeconds(30))
                {
                    log($"{failure.Message} state={(attention ? "AuthenticationAttention" : "WaitingForBackend")}, " +
                        $"endpointRole={(exception is AgentClientAuthException auth ? auth.EndpointRole.ToString() : "Gateway")}, " +
                        $"authStatus={(exception as AgentClientAuthException)?.StatusCode?.ToString() ?? "none"}, " +
                        $"authFailureKind={(exception as AgentClientAuthException)?.FailureKind.ToString() ?? "none"}, " +
                        $"servicePolicy={NativeServiceRecoveryPolicy.Status}, " +
                        $"reason={(exception as AgentClientAuthException)?.Code ?? "stream"}, " +
                        $"outageSeconds={retry.OutageDuration.TotalSeconds:0}, phase={retry.Phase}, attempt={retry.AttemptCount}, " +
                        $"suppressedRepeatedFailures={suppressedFailures}.");
                    lastFailureKey = failureKey;
                    lastFailureLog = _timeProvider.GetTimestamp();
                    suppressedFailures = 0;
                }
                else suppressedFailures++;
            }
        }
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= networkRestored;
            if (OperatingSystem.IsWindows() && powerNotifications) SystemEvents.PowerModeChanged -= resumed;
        }
    }

    private async Task RunSessionAsync(string accessToken, DateTimeOffset expiresAtUtc,
        int sessionTenantId, Guid sessionAgentId, TimeSpan finiteAttemptBudget, GatewaySessionDiagnostics diagnostics,
        Action<TimeSpan> acknowledged, Action<string> currentAccessTokenChanged, CancellationToken stoppingToken)
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
        using var authorityExpiry = _timeProvider.CreateTimer(_ => owned.Cancel(), null,
            expiresAtUtc > _timeProvider.GetUtcNow() ? expiresAtUtc - _timeProvider.GetUtcNow() : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
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
            owned.Token.ThrowIfCancellationRequested();
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
                            try
                            {
                                var graceResult = await operation.WaitAsync(grace, _timeProvider, owned.Token).ConfigureAwait(false);
                                owned.Token.ThrowIfCancellationRequested();
                                return graceResult;
                            }
                            catch (TimeoutException) { owned.Token.ThrowIfCancellationRequested(); }
                        }
                        // A cancelled MoveNext invalidates this RPC. Retire it;
                        // never start a competing reader or reuse the old call.
                        diagnostics.FailureReason("renewal_io_blocked");
                        throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Presence I/O blocked token renewal."));
                    }
                    break;
                }
                if (operation.IsCompleted)
                {
                    var completedResult = await operation.ConfigureAwait(false);
                    owned.Token.ThrowIfCancellationRequested();
                    return completedResult;
                }
                var remainingBudget = RemainingBudget();
                if (remainingBudget <= TimeSpan.Zero) throw new TimeoutException();
                var result = await operation.WaitAsync(remainingBudget, _timeProvider, owned.Token).ConfigureAwait(false);
                owned.Token.ThrowIfCancellationRequested();
                return result;
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

        async Task AwaitWriteAsync(AgentFrame frame, TimeSpan budget, bool watchRenewal = false,
            CancellationToken? operationToken = null)
        {
            // Cancellation reaches the physical write, rather than only its waiter.
            var operation = call.RequestStream.WriteAsync(frame, operationToken ?? owned.Token);
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
                ProtocolVersion = options.ProtocolVersion, TenantId = sessionTenantId, ClientId = sessionAgentId.ToString("D"),
                ConnectionId = Guid.NewGuid().ToString("D"), OperationId = operationId.ToString("D"), Sequence = 0, Hello = hello
            }, ioBudget).ConfigureAwait(false);
            var remainingBootstrap = ioBudget - _timeProvider.GetElapsedTime(bootstrapStarted);
            if (remainingBootstrap <= TimeSpan.Zero ||
                !await AwaitIoAsync(call.ResponseStream.MoveNext(owned.Token), remainingBootstrap).ConfigureAwait(false) ||
                call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.Connected)
                throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before accepting the presence session."));

            var accepted = call.ResponseStream.Current;
            ValidateConnectedFrame(accepted, operationId, sessionTenantId, sessionAgentId, diagnostics);
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

            var session = new GatewayPresenceSession(sessionTenantId, sessionAgentId, accepted.ConnectionEpoch, acceptedConnectionId);
            session.SetAccessToken(accessToken);
            ulong sequence = 0;
            double? lastAcknowledgedRoundTripMs = null;
            ulong lastAcknowledgedSequence = 0;
            var lastHeartbeatAt = _timeProvider.GetTimestamp();

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
                ProtocolVersion = options.ProtocolVersion, TenantId = sessionTenantId, ClientId = sessionAgentId.ToString("D"),
                ConnectionEpoch = accepted.ConnectionEpoch, ConnectionId = accepted.ConnectionId,
                OperationId = operation.ToString("D"), Sequence = frameSequence
            };

            async Task SendHeartbeatAsync()
            {
                if (expiresAtUtc <= _timeProvider.GetUtcNow())
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "Current presence authorization expired."));
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
                if (expiresAtUtc <= _timeProvider.GetUtcNow())
                    throw new RpcException(new Status(StatusCode.Unauthenticated, "Current presence authorization expired before heartbeat acknowledgement."));
                ValidateHeartbeatFrame(response, accepted, heartbeatOperation, heartbeatSequence, sessionTenantId, sessionAgentId, diagnostics);
                if (!GatewayWireProtocol.HasAkkaAuthority(response.HeartbeatAccepted.PresenceAuthority))
                {
                    diagnostics.ProtocolFailure("unsupported heartbeat authority token");
                    throw new RpcException(new Status(StatusCode.FailedPrecondition, "Gateway returned an unsupported heartbeat authority token."));
                }
                diagnostics.AcknowledgeHeartbeat();
                lastHeartbeatAt = _timeProvider.GetTimestamp();
                acknowledged(heartbeatTimeout);
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
            if (sessionTask is not null)
            {
                _ = sessionTask.ContinueWith(completed =>
                {
                    var failure = completed.Exception;
                    if (!owned.IsCancellationRequested && !completed.IsCanceled)
                        log($"Non-presence gateway extension owner ended; admitted presence remains live. category={failure?.GetBaseException().GetType().Name ?? "unexpected_return"}.");
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            while (!owned.IsCancellationRequested)
            {
                using var intervalStopping = CancellationTokenSource.CreateLinkedTokenSource(owned.Token);
                var remainingInterval = heartbeatInterval - _timeProvider.GetElapsedTime(lastHeartbeatAt);
                var interval = Task.Delay(remainingInterval > TimeSpan.Zero ? remainingInterval : TimeSpan.Zero,
                    _timeProvider, intervalStopping.Token);
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
                        throw new RpcException(new Status(StatusCode.Unavailable, "Presence requires fresh connection authority."));
                    }

                    // The single presence owner acquires fresh authority, then performs
                    // one bounded request/ACK exchange; children retain their fence.
                    var renewalStarted = _timeProvider.GetTimestamp();
                    var authorityRemaining = expiresAtUtc - _timeProvider.GetUtcNow();
                    var wholeRenewalBudget = TimeSpan.FromTicks(Math.Min(ioBudget.Ticks,
                        Math.Min(finiteAttemptBudget.Ticks, authorityRemaining.Ticks)));
                    if (wholeRenewalBudget <= TimeSpan.Zero)
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Current presence authorization expired before renewal."));
                    TimeSpan RemainingRenewalBudget()
                    {
                        var remaining = wholeRenewalBudget - _timeProvider.GetElapsedTime(renewalStarted);
                        if (remaining <= TimeSpan.Zero)
                            throw new RpcException(new Status(StatusCode.DeadlineExceeded, "Finite authentication renewal exceeded its whole attempt budget."));
                        return remaining;
                    }
                    using var renewalStopping = CancellationTokenSource.CreateLinkedTokenSource(owned.Token);
                    using var renewalBudget = _timeProvider.CreateTimer(_ => renewalStopping.Cancel(), null,
                        wholeRenewalBudget, Timeout.InfiniteTimeSpan);
                    Task<(string AccessToken, DateTimeOffset ExpiresAtUtc)>? renewalWork = null;
                    (string AccessToken, DateTimeOffset ExpiresAtUtc) fresh;
                    try
                    {
                        renewalWork = tokenService.GetAccessTokenAsync(renewalStopping.Token);
                        fresh = await AwaitIoAsync(renewalWork, RemainingRenewalBudget()).ConfigureAwait(false);
                    }
                    catch (Exception exception) when (
                        OperationalRecoveryFailure.IsExpected(exception) &&
                        !OperationalRecoveryFailure.RequiresAttention(exception) &&
                        expiresAtUtc > _timeProvider.GetUtcNow() && !owned.IsCancellationRequested)
                    {
                        // Proactive failure while current authenticated presence
                        // remains usable does not create an outage episode.
                        renewalAtUtc = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(Math.Min(
                            TimeSpan.FromSeconds(30).Ticks, (expiresAtUtc - _timeProvider.GetUtcNow()).Ticks / 2));
                        renewalDue = WaitForRenewalAsync();
                        log($"Proactive authentication renewal is waiting while current authority remains valid. category={exception.GetType().Name}.");
                        continue;
                    }
                    finally
                    {
                        if (renewalWork is { IsCompleted: false })
                        {
                            renewalStopping.Cancel();
                            await ObserveRetiredIoAsync([renewalWork]).ConfigureAwait(false);
                        }
                        if (renewalWork is not null) pending.Remove(renewalWork);
                    }
                    if (fresh.ExpiresAtUtc <= expiresAtUtc && expiresAtUtc > _timeProvider.GetUtcNow())
                    {
                        // The token service may retain an unexpired cached token
                        // after transient refresh failure. Do not send a redundant
                        // renewal ACK or publish new authority.
                        renewalAtUtc = _timeProvider.GetUtcNow() + TimeSpan.FromTicks(Math.Min(
                            TimeSpan.FromSeconds(30).Ticks, (expiresAtUtc - _timeProvider.GetUtcNow()).Ticks / 2));
                        renewalDue = WaitForRenewalAsync();
                        continue;
                    }
                    if (fresh.ExpiresAtUtc <= _timeProvider.GetUtcNow())
                        throw new RpcException(new Status(StatusCode.Unauthenticated, "Agent token renewal did not extend authority."));
                    try
                    {
                        var renewalOperation = Guid.NewGuid();
                        var renewalSequence = ++sequence;
                        var frame = Envelope(renewalOperation, renewalSequence);
                        frame.Renew = new PresenceAuthRenewal { AccessToken = fresh.AccessToken };
                        await AwaitWriteAsync(frame, RemainingRenewalBudget(), operationToken: renewalStopping.Token).ConfigureAwait(false);
                        if (!await AwaitIoAsync(call.ResponseStream.MoveNext(renewalStopping.Token), RemainingRenewalBudget()).ConfigureAwait(false) ||
                            call.ResponseStream.Current.PayloadCase != GatewayFrame.PayloadOneofCase.Renewed)
                            throw new RpcException(new Status(StatusCode.Unavailable, "Gateway closed before acknowledging authentication renewal."));
                        var renewed = call.ResponseStream.Current;
                        ValidateHeartbeatFrame(renewed, accepted, renewalOperation, renewalSequence, sessionTenantId, sessionAgentId, diagnostics);
                        var serverExpiry = renewed.Renewed.ExpiresAtUtc?.ToDateTimeOffset();
                        if (!GatewayWireProtocol.HasAkkaAuthority(renewed.Renewed.PresenceAuthority) ||
                            serverExpiry is null || serverExpiry <= expiresAtUtc || serverExpiry <= _timeProvider.GetUtcNow() ||
                            serverExpiry > fresh.ExpiresAtUtc)
                            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid authentication renewal acknowledgement."));
                        expiresAtUtc = serverExpiry.Value;
                        authorityExpiry.Change(expiresAtUtc - _timeProvider.GetUtcNow(), Timeout.InfiniteTimeSpan);
                        session.SetAccessToken(fresh.AccessToken);
                        currentAccessTokenChanged(fresh.AccessToken);
                        // Renewal changes credentials; only validated heartbeat progress clears outage history.
                        // Renewal ACK is not a heartbeat RTT sample.
                        lastAcknowledgedRoundTripMs = null;
                        lastAcknowledgedSequence = 0;
                        log($"Presence authentication renewed. {diagnostics.AdmissionSummary}, connectionEpoch={accepted.ConnectionEpoch}, expiresAtUtc={expiresAtUtc:O}.");
                        renewalDue = ScheduleRenewal();
                    }
                    catch (RpcException exception) when (exception.StatusCode == StatusCode.Unauthenticated)
                    {
                        // This exchange proposed fresh authority. A denial rejects
                        // that exact credential, even before its ACK can publish it.
                        tokenService.InvalidateAccessToken(fresh.AccessToken);
                        throw;
                    }

                }
                else
                {
                    await interval.ConfigureAwait(false);
                    await SendHeartbeatAsync().ConfigureAwait(false);
                }
            }
            owned.Token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested && expiresAtUtc <= _timeProvider.GetUtcNow())
        {
            diagnostics.SessionFailed();
            diagnostics.FailureReason("access_token_expired");
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Current presence authorization expired."));
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
            authorityExpiry.Dispose();
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
            log("Retired operational I/O exceeded its abort join budget; reporting native worker failure.");
            ObserveLateTask(all);
            throw new InvalidOperationException("Operational I/O did not retire after cancellation and disposal.");
        }
        catch (Exception exception) when (OperationalRecoveryFailure.IsExpected(exception))
        {
            // Aborted reads and writes are observed here; the initiating failure is preserved.
        }
    }

    private static void ObserveLateTask(Task task)
        => _ = task.ContinueWith(static completed => _ = completed.Exception,
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

    private void ValidateOptions()
    {
        if (!Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Gateway:Endpoint must be an absolute HTTPS URL.");
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

    private void ValidateConnectedFrame(GatewayFrame frame, Guid operationId, int sessionTenantId, Guid sessionAgentId, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != sessionTenantId ||
            !string.Equals(frame.ClientId, sessionAgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            !Guid.TryParse(frame.ConnectionId, out var connectionId) || connectionId == Guid.Empty ||
            !string.Equals(frame.OperationId, operationId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
            frame.ConnectionEpoch == 0 || frame.Sequence != 0)
        {
            diagnostics.ProtocolFailure("invalid connect acknowledgement");
            throw new RpcException(new Status(StatusCode.DataLoss, "Gateway returned an invalid connect acknowledgement."));
        }
    }

    private void ValidateHeartbeatFrame(GatewayFrame frame, GatewayFrame accepted, Guid operationId, ulong sequence, int sessionTenantId, Guid sessionAgentId, GatewaySessionDiagnostics diagnostics)
    {
        if (!string.Equals(frame.ProtocolVersion, options.ProtocolVersion, StringComparison.Ordinal) ||
            frame.TenantId != sessionTenantId ||
            !string.Equals(frame.ClientId, sessionAgentId.ToString("D"), StringComparison.OrdinalIgnoreCase) ||
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
