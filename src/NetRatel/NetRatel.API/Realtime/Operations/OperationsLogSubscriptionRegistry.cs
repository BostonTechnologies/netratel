using System.Security.Claims;
using NetRatel.Application.Presence;

namespace NetRatel.API.Realtime.Operations;

/// <summary>
/// Bounded membership accounting for the production log hub. This stores only
/// connection and group metadata; log bodies remain in the gateway registry.
/// </summary>
public sealed class OperationsLogSubscriptionRegistry
{
    private const int MaximumConnections = 128;
    // One explorer owns a hub connection. Permit a realistic number of open
    // client explorers while keeping the process-wide ceiling bounded.
    private const int MaximumConnectionsPerOperator = 16;
    private const int MaximumSubscriptionsPerConnection = 8;
    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _operatorCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<OperationsLogSubscription, SourceState> _sourceSubscribers = [];

    public bool TryRegister(string connectionId, ClaimsPrincipal principal)
    {
        var operatorId = principal.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? principal.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(connectionId) || string.IsNullOrWhiteSpace(operatorId)) return false;

        lock (_gate)
        {
            if (_connections.ContainsKey(connectionId) || _connections.Count >= MaximumConnections ||
                _operatorCounts.GetValueOrDefault(operatorId) >= MaximumConnectionsPerOperator)
            {
                return false;
            }

            _connections.Add(connectionId, new ConnectionState(operatorId));
            _operatorCounts[operatorId] = _operatorCounts.GetValueOrDefault(operatorId) + 1;
            return true;
        }
    }

    public bool TryAdd(string connectionId, string groupName, ClientKey client, string sourceId, out bool joinGroup, out bool startFollow)
    {
        lock (_gate)
        {
            joinGroup = false;
            startFollow = false;
            if (!_connections.TryGetValue(connectionId, out var connection)) return false;
            var subscription = new OperationsLogSubscription(groupName, client, sourceId);
            if (connection.Subscriptions.Contains(subscription)) return true;
            if (connection.Subscriptions.Count >= MaximumSubscriptionsPerConnection) return false;

            joinGroup = !connection.Subscriptions.Any(existing => string.Equals(existing.GroupName, groupName, StringComparison.Ordinal));
            connection.Subscriptions.Add(subscription);
            if (!_sourceSubscribers.TryGetValue(subscription, out var sourceState))
            {
                sourceState = new SourceState();
                _sourceSubscribers.Add(subscription, sourceState);
            }

            startFollow = sourceState.SubscriberCount == 0;
            sourceState.SubscriberCount++;
            sourceState.Generation++;
            return true;
        }
    }

    public bool Remove(string connectionId, string groupName, ClientKey client, string sourceId, out bool leaveGroup, out OperationsLogFollowLease? stopFollow)
    {
        lock (_gate)
        {
            leaveGroup = false;
            stopFollow = null;
            if (!_connections.TryGetValue(connectionId, out var connection)) return false;
            var subscription = new OperationsLogSubscription(groupName, client, sourceId);
            var removed = connection.Subscriptions.Remove(subscription);
            leaveGroup = removed && !connection.Subscriptions.Any(existing => string.Equals(existing.GroupName, groupName, StringComparison.Ordinal));
            if (removed) stopFollow = RemoveSourceSubscriber(subscription);
            return removed;
        }
    }

    public IReadOnlyList<OperationsLogFollowLease> RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var connection)) return [];
            var remaining = _operatorCounts.GetValueOrDefault(connection.OperatorId) - 1;
            if (remaining <= 0) _operatorCounts.Remove(connection.OperatorId);
            else _operatorCounts[connection.OperatorId] = remaining;
            return connection.Subscriptions.Select(RemoveSourceSubscriber).OfType<OperationsLogFollowLease>().ToArray();
        }
    }

    /// <summary>
    /// Claims a delayed source stop only if no replacement subscription has
    /// arrived in the meantime. This prevents an old disconnect from stopping
    /// the follow started by a rapid source/filter handoff.
    /// </summary>
    public bool TryClaimFollowStop(OperationsLogFollowLease lease)
    {
        lock (_gate)
        {
            if (!_sourceSubscribers.TryGetValue(lease.Subscription, out var sourceState) ||
                sourceState.SubscriberCount != 0 || sourceState.Generation != lease.Generation)
            {
                return false;
            }

            _sourceSubscribers.Remove(lease.Subscription);
            return true;
        }
    }

    private OperationsLogFollowLease? RemoveSourceSubscriber(OperationsLogSubscription subscription)
    {
        if (!_sourceSubscribers.TryGetValue(subscription, out var sourceState) || sourceState.SubscriberCount <= 0) return null;

        sourceState.SubscriberCount--;
        sourceState.Generation++;
        return sourceState.SubscriberCount == 0
            ? new OperationsLogFollowLease(subscription, sourceState.Generation)
            : null;
    }

    private sealed class ConnectionState(string operatorId)
    {
        public string OperatorId { get; } = operatorId;
        public HashSet<OperationsLogSubscription> Subscriptions { get; } = [];
    }

    private sealed class SourceState
    {
        public int SubscriberCount { get; set; }
        public long Generation { get; set; }
    }
}

public sealed record OperationsLogSubscription(string GroupName, ClientKey Client, string SourceId);
public sealed record OperationsLogFollowLease(OperationsLogSubscription Subscription, long Generation);
