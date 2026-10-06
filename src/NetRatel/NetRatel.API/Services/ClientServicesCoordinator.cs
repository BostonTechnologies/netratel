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
            timeProvider.GetUtcNow().Add(ClientServicesLimits.MaximumPolicyLifetime)));
    }
}

public sealed class ClientServicesCoordinator(
    IClientServicesRouter router,
    IClientServiceWatchPolicySource policies,
    IAgentTelemetryGatewaySessionRegistry sessions,
    TimeProvider timeProvider,
    IClientPresenceRouter presence)
{
    private readonly object _gate = new();
    private readonly Dictionary<ClientKey, (DateTimeOffset At, Guid Request)> _refreshes = [];
    private readonly Dictionary<ClientKey, int> _viewers = [];
    private int _viewerCount;

    public async Task<ClientServicesReadModelDto> ReadAsync(ClientKey client, CancellationToken cancellationToken)
    {
        var state = await router.GetSnapshotAsync(client, cancellationToken).ConfigureAwait(false);
        if (state.Client != client) throw new InvalidOperationException("Services projection returned a different client.");
        var session = sessions.GetStatus(client);
        var connected = session.Connected && await MatchesPresenceAsync(client, session, cancellationToken).ConfigureAwait(false) &&
            sessions.GetStatus(client).RegistrationId == session.RegistrationId;
        return new(client.TenantId, client.AgentId, state.LastCompleteInventory, state.LatestAttempt,
            state.WatchedServices, state.MonitoredServiceNames, state.WatchPolicyRevision,
            connected, connected && session.SupportsServices, timeProvider.GetUtcNow(), state.Revision);
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
        Guid request;
        lock (_gate)
        {
            var now = timeProvider.GetUtcNow();
            if (_refreshes.TryGetValue(client, out var previous) && now < previous.At.Add(ClientServicesLimits.MinimumRefreshInterval))
                return new(ClientServicesRefreshStatus.Throttled, previous.Request, previous.At.Add(ClientServicesLimits.MinimumRefreshInterval));
            if (_refreshes.Count >= 4096)
            {
                foreach (var key in _refreshes.Where(entry => entry.Value.At < now.AddHours(-1)).Select(entry => entry.Key).ToArray()) _refreshes.Remove(key);
                if (_refreshes.Count >= 4096) return new(ClientServicesRefreshStatus.Throttled, null, now.Add(ClientServicesLimits.MinimumRefreshInterval));
            }
            request = Guid.NewGuid();
            _refreshes[client] = (now, request);
        }
        var published = false;
        try
        {
            var policy = await GetPolicyAsync(client, cancellationToken).ConfigureAwait(false);
            await router.UpdateWatchPolicyAsync(new(client, policy), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!await MatchesPresenceAsync(client, status, cancellationToken).ConfigureAwait(false) ||
                sessions.GetStatus(client).RegistrationId != status.RegistrationId || status.RegistrationId is not Guid registrationId)
                return new(ClientServicesRefreshStatus.Offline, null);
            published = sessions.TryPublishServicesPolicy(client, policy with { RefreshRequestId = request }, registrationId);
            if (published) return new(ClientServicesRefreshStatus.Requested, request);
            var current = sessions.GetStatus(client);
            if (current.Connected && current.SupportsServices)
                return new(ClientServicesRefreshStatus.Throttled, null, timeProvider.GetUtcNow().Add(ClientServicesLimits.MinimumRefreshInterval));
            return new(current.Connected ? ClientServicesRefreshStatus.Unsupported : ClientServicesRefreshStatus.Offline, null);
        }
        finally
        {
            if (!published)
            {
                lock (_gate)
                    if (_refreshes.TryGetValue(client, out var pending) && pending.Request == request) _refreshes.Remove(client);
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
