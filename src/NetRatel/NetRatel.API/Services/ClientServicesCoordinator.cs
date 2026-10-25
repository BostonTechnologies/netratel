using NetRatel.API.Gateway;
using NetRatel.Application.Presence;
using NetRatel.Application.Services;
using NetRatel.Shared.Contracts.Services;

namespace NetRatel.API.Services;

/// <summary>Provides no watch selection until an authorized rule source selects exact names.</summary>
public sealed class EmptyClientServiceWatchPolicySource(IClientServicesRouter router, TimeProvider timeProvider) : IClientServiceWatchPolicySource
{
    public async Task<ClientServiceWatchPolicy> GetPolicyAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var state = await router.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        var revision = Math.Max(1UL, state.WatchPolicyRevision);
        if (state.MonitoredServiceNames.Count != 0) revision = checked(revision + 1);
        return new ClientServiceWatchPolicy(client, new(revision, [],
            ClientServicesLimits.DefaultWatchIntervalSeconds, ClientServicesLimits.DefaultInventoryIntervalSeconds,
            timeProvider.GetUtcNow().Add(ClientServicesLimits.MaximumPolicyLifetime - ClientServicesLimits.PolicyClockSkewHeadroom)));
    }
}

public sealed class ClientServicesCoordinator(
    IClientServicesRouter router,
    IClientServiceWatchPolicySource policies,
    IAgentTelemetryGatewaySessionRegistry sessions,
    TimeProvider timeProvider,
    IClientPresenceRouter presence,
    IAgentCommandGatewaySessionRegistry? commands = null)
{
    private readonly object _gate = new();
    private readonly Dictionary<ClientKey, int> _viewers = [];
    private int _viewerCount;

    public async Task<ClientServicesReadModelDto> ReadAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var state = await router.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (state.Client != client) throw new InvalidOperationException("Services projection returned a different client.");
        var session = sessions.GetStatus(client);
        var connected = session.Connected && await MatchesPresenceAsync(client, session, cancellationToken).ConfigureAwait(false) &&
            sessions.GetStatus(client).RegistrationId == session.RegistrationId;
        foreach (var pending in ClientServicesRefreshLifecycle.Requests(state).Where(request => request.Outcome == ClientServicesRefreshOutcome.Pending).ToArray())
        {
            if (connected && pending.RegistrationId == session.RegistrationId && pending.ConnectionId == session.ConnectionId &&
                pending.ConnectionEpoch == session.ConnectionEpoch) continue;
            var active = await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
            // A replica without the telemetry child cannot declare another replica's request offline.
            var replaced = active.Client == client && active.IsAuthoritative &&
                (active.Status == ClientPresenceStatus.Offline || active.ConnectionEpoch > pending.ConnectionEpoch ||
                 active.ConnectionEpoch == pending.ConnectionEpoch && active.ConnectionId is { } currentConnection && currentConnection != pending.ConnectionId);
            if (!replaced && !(connected && session.RegistrationId != pending.RegistrationId)) continue;
            state = await router.UpdateRefreshAsync(new(client, pending with { Outcome = ClientServicesRefreshOutcome.Offline,
                CompletedAtUtc = timeProvider.GetUtcNow(), ErrorCode = "refresh-connection-ended" }), cancellationToken).ConfigureAwait(false);
        }
        return new(client.TenantId, client.AgentId, state.LastCompleteInventory, state.LatestAttempt,
            state.WatchedServices, state.MonitoredServiceNames, state.WatchPolicyRevision,
            connected, connected && session.SupportsServices, timeProvider.GetUtcNow(), state.Revision,
            state.LastPartialInventory, state.LatestInventoryAttempt,
            connected && commands?.SupportsServiceControl(client) == true, false, connected && session.SupportsRefreshCorrelation,
            ClientServicesRefreshLifecycle.Requests(state));
    }

    public async Task<ClientServiceWatchPolicyDto> GetPolicyAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var selected = await policies.GetPolicyAsync(client, cancellationToken).ConfigureAwait(false);
        if (selected.Client != client || !IsValidPolicy(selected.Policy, timeProvider.GetUtcNow()))
            throw new InvalidOperationException("The server services watch policy is invalid.");
        return selected.Policy;
    }

    public async Task<ClientServicesRefreshResponse> RefreshAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var status = sessions.GetStatus(client);
        if (!status.Connected || !await MatchesPresenceAsync(client, status, cancellationToken).ConfigureAwait(false))
            return new(ClientServicesRefreshStatus.Offline, null);
        if (!status.SupportsServices) return new(ClientServicesRefreshStatus.Unsupported, null);
        var policy = await GetPolicyAsync(client, cancellationToken).ConfigureAwait(false);
        await router.UpdateWatchPolicyAsync(new(client, policy), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!await MatchesPresenceAsync(client, status, cancellationToken).ConfigureAwait(false) ||
            sessions.GetStatus(client).RegistrationId != status.RegistrationId || status.RegistrationId is not Guid registrationId ||
            status.ConnectionId is not Guid connectionId || status.ConnectionEpoch is not long epoch)
            return new(ClientServicesRefreshStatus.Offline, null);

        // Reconcile a previous registration before admission; its request can never move to this connection.
        await ReadAsync(client, cancellationToken).ConfigureAwait(false);
        var now = timeProvider.GetUtcNow();
        var request = new ClientServicesRefreshRequestDto(Guid.NewGuid(), connectionId, epoch, registrationId,
            now, now + ClientServicesLimits.RefreshTimeout, status.SupportsRefreshCorrelation);
        var state = await router.UpdateRefreshAsync(new(client, request), cancellationToken).ConfigureAwait(false);
        var admitted = ClientServicesRefreshLifecycle.Requests(state).LastOrDefault()
            ?? throw new InvalidOperationException("Services refresh admission was not persisted.");
        if (admitted.RequestId != request.RequestId)
            return new(ClientServicesRefreshStatus.Throttled, admitted.RequestId,
                admitted.Outcome == ClientServicesRefreshOutcome.Pending ? admitted.DeadlineUtc : admitted.RequestedAtUtc + ClientServicesLimits.MinimumRefreshInterval,
                admitted.CorrelationSupported);

        var published = false;
        try
        {
            if (!await MatchesPresenceAsync(client, status, cancellationToken).ConfigureAwait(false) ||
                sessions.GetStatus(client).RegistrationId != registrationId)
                return new(ClientServicesRefreshStatus.Offline, request.RequestId, CorrelationSupported: request.CorrelationSupported);
            published = sessions.TryPublishServicesPolicy(client, policy with { RefreshRequestId = request.RequestId }, registrationId);
            if (published) return new(ClientServicesRefreshStatus.Requested, request.RequestId, CorrelationSupported: request.CorrelationSupported);
            var current = sessions.GetStatus(client);
            return new(current.Connected ? ClientServicesRefreshStatus.Throttled : ClientServicesRefreshStatus.Offline,
                request.RequestId, now + ClientServicesLimits.MinimumRefreshInterval, request.CorrelationSupported);
        }
        finally
        {
            if (!published)
            {
                // The admission already committed. Preserve an explicit noncollection result even if the caller disconnected.
                using var completion = new CancellationTokenSource(ClientServicesLimits.CollectionTimeout);
                var current = sessions.GetStatus(client);
                var offline = !current.Connected || current.RegistrationId != registrationId;
                await router.UpdateRefreshAsync(new(client, request with
                {
                    Outcome = offline ? ClientServicesRefreshOutcome.Offline : ClientServicesRefreshOutcome.Error,
                    CompletedAtUtc = timeProvider.GetUtcNow(),
                    ErrorCode = offline ? "refresh-connection-ended" : "refresh-dispatch-failed"
                }), completion.Token).ConfigureAwait(false);
            }
        }
    }

    public IDisposable? TryAcquireViewer(ClientKey client)
    {
        lock (_gate)
        {
            _viewers.TryGetValue(client, out var count);
            if (_viewerCount >= 128 || count >= 4) return null;
            _viewers[client] = count + 1;
            _viewerCount++;
            return new ViewerLease(() =>
            {
                lock (_gate)
                {
                    if (--_viewers[client] == 0) _viewers.Remove(client);
                    _viewerCount--;
                }
            });
        }
    }

    public static bool IsValidPolicy(ClientServiceWatchPolicyDto policy, DateTimeOffset now) =>
        ClientServicesValidation.ValidatePolicy(policy, now) &&
        policy.ServiceNames.Sum(name => System.Text.Encoding.UTF8.GetByteCount(name) + 3) <= ClientServicesLimits.MaximumChunkPayloadBytes - 512 &&
        policy.RefreshRequestId != Guid.Empty;

    private async Task<bool> MatchesPresenceAsync(ClientKey client, AgentTelemetryGatewaySessionStatus session, CancellationToken cancellationToken)
    {
        var active = await presence.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        return active.Client == client && active.IsAuthoritative && active.Status == ClientPresenceStatus.Online &&
            active.ConnectionEpoch == session.ConnectionEpoch && active.ConnectionId == session.ConnectionId &&
            session.ConnectionId is not null && session.RegistrationId is not null;
    }

    private sealed class ViewerLease(Action release) : IDisposable
    {
        private Action? _release = release;
        public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
