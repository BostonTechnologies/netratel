using Grpc.Core;
using NetRatel.Akka.Configuration;
using NetRatel.Application.Presence;

namespace NetRatel.API.Gateway;

/// <summary>Retires capability I/O with the authoritative owner, including across API replicas.</summary>
internal sealed class AgentGatewayAuthenticationLifetime : IAsyncDisposable
{
    private static readonly TimeSpan MaximumPollInterval = TimeSpan.FromSeconds(5);
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationTokenSource _monitorStopping = new();
    private readonly ITimer? _expiryTimer;
    private readonly TimeProvider _clock;
    private readonly object _expirySync = new();
    private DateTimeOffset? _expiry;
    private bool _expiryRetired;
    private Task _monitor = Task.CompletedTask;
    public CancellationToken Token => _lifetime.Token;

    private AgentGatewayAuthenticationLifetime(ServerCallContext context, TimeProvider clock,
        AgentGatewayAuthenticationLease? localLease, DateTimeOffset? expiry)
    {
        _clock = clock;
        _expiry = expiry;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken,
            localLease?.CompletionToken ?? CancellationToken.None);
        if (expiry is { } expires)
            _expiryTimer = clock.CreateTimer(_ => OnExpiry(), null,
                Remaining(expires), Timeout.InfiniteTimeSpan);
    }

    private TimeSpan Remaining(DateTimeOffset expires)
    {
        var remaining = expires - _clock.GetUtcNow();
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private void OnExpiry()
    {
        lock (_expirySync)
        {
            if (_expiry is { } expiry && expiry > _clock.GetUtcNow())
            {
                // Timer callbacks queued before a confirmed renewal must not
                // retire the owner after the timer has been rescheduled.
                _expiryTimer?.Change(Remaining(expiry), Timeout.InfiniteTimeSpan);
                return;
            }
            _expiryRetired = true;
        }
        _lifetime.Cancel();
    }

    public static async Task<AgentGatewayAuthenticationLifetime> AttachAsync(
        ServerCallContext context, ClientKey client, Guid connectionId, ulong connectionEpoch)
    {
        var services = context.GetHttpContext().RequestServices;
        var registry = services.GetService<AgentGatewayAuthenticationLeaseRegistry>();
        var clock = services.GetService<TimeProvider>() ?? TimeProvider.System;
        // Transport-only legacy test hosts omit the authentication registry.
        // Production always installs it, and requires actor-confirmed authority.
        if (registry is null)
            return new(context, clock, null, null);

        if (!AgentGatewayIdentityResolver.TryResolve(context.GetHttpContext().User, out var identity, out _) ||
            identity is null || identity.TenantId != client.TenantId || identity.AgentId != client.AgentId)
            throw new RpcException(new Status(StatusCode.PermissionDenied, "The capability identity does not match presence."));
        var header = context.GetHttpContext().Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "A current agent credential is required."));
        // HTTP authentication may allow clock skew. A newly admitted physical
        // stream cannot use an expired predecessor JWT to adopt renewed authority.
        await services.GetRequiredService<AgentGatewayRenewalAuthenticator>()
            .ValidateAsync(header[7..], identity, null, context.CancellationToken).ConfigureAwait(false);

        var router = services.GetRequiredService<IClientPresenceRouter>();
        var askBudget = services.GetRequiredService<NetRatelAkkaOptions>().AskTimeout;
        if (askBudget > MaximumPollInterval) askBudget = MaximumPollInterval;
        var snapshot = await ReadSnapshotAsync(router, client, askBudget, clock, context.CancellationToken).ConfigureAwait(false);
        var expiry = RequireAuthority(snapshot, client, connectionId, connectionEpoch, clock.GetUtcNow());
        var owner = new AgentGatewayAuthenticationLifetime(context, clock,
            registry.Find(client, connectionId, connectionEpoch), expiry);
        owner._monitor = owner.MonitorAsync(router, client, connectionId, connectionEpoch, expiry, askBudget, clock);
        return owner;
    }

    private async Task MonitorAsync(IClientPresenceRouter router, ClientKey client, Guid connectionId,
        ulong connectionEpoch, DateTimeOffset expiry, TimeSpan askBudget, TimeProvider clock)
    {
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(_monitorStopping.Token, Token);
        try
        {
            while (!stopping.IsCancellationRequested)
            {
                // Normal renewal has a one-minute margin. Short test leases are
                // checked more often so the same strict expiry contract applies.
                var remaining = expiry - clock.GetUtcNow();
                var interval = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks / 2,
                    TimeSpan.FromMilliseconds(100).Ticks, MaximumPollInterval.Ticks));
                await Task.Delay(interval, clock, stopping.Token).ConfigureAwait(false);
                var snapshot = await ReadSnapshotAsync(router, client, askBudget, clock, stopping.Token).ConfigureAwait(false);
                expiry = RequireAuthority(snapshot, client, connectionId, connectionEpoch, clock.GetUtcNow());
                stopping.Token.ThrowIfCancellationRequested();
                lock (_expirySync)
                {
                    if (_expiryRetired) return;
                    _expiry = expiry;
                    _expiryTimer!.Change(Remaining(expiry), Timeout.InfiniteTimeSpan);
                }
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
        catch (Exception)
        {
            // Losing the authoritative fence or being unable to verify it cannot
            // extend an authenticated terminal or file operation's authority.
            _lifetime.Cancel();
        }
    }

    private static async Task<ClientPresenceSnapshot> ReadSnapshotAsync(IClientPresenceRouter router,
        ClientKey client, TimeSpan budget, TimeProvider clock, CancellationToken cancellationToken)
    {
        var read = router.GetSnapshotAsync(client, cancellationToken);
        try { return await read.WaitAsync(budget, clock, cancellationToken).ConfigureAwait(false); }
        finally
        {
            if (!read.IsCompleted)
                _ = read.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }

    private static DateTimeOffset RequireAuthority(ClientPresenceSnapshot snapshot, ClientKey client,
        Guid connectionId, ulong connectionEpoch, DateTimeOffset now)
    {
        if (snapshot.Client != client || !snapshot.IsAuthoritative || snapshot.Status != ClientPresenceStatus.Online ||
            snapshot.ConnectionId != connectionId || snapshot.ConnectionEpoch is not > 0 ||
            (ulong)snapshot.ConnectionEpoch.Value != connectionEpoch ||
            snapshot.AuthenticationExpiresAtUtc is not { } expiry || expiry <= now)
            throw new RpcException(new Status(StatusCode.Aborted, "The presence authority is no longer active."));
        return expiry;
    }

    public async ValueTask DisposeAsync()
    {
        _monitorStopping.Cancel();
        await _monitor.ConfigureAwait(false);
        if (_expiryTimer is not null) await _expiryTimer.DisposeAsync().ConfigureAwait(false);
        _lifetime.Cancel();
        _lifetime.Dispose();
        _monitorStopping.Dispose();
    }
}
