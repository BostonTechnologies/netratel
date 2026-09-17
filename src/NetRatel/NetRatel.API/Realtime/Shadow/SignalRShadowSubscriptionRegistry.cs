using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using NetRatel.Akka.Observability;

namespace NetRatel.API.Realtime.Shadow;

public interface IShadowTenantAuthorizer
{
    bool IsAuthorized(ClaimsPrincipal principal, int tenantId);
}

public sealed class ShadowTenantClaimAuthorizer(string? adminGroupId = null) : IShadowTenantAuthorizer
{
    public bool IsAuthorized(ClaimsPrincipal principal, int tenantId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (tenantId <= 0 || principal.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        if (IsOperator(principal))
        {
            return true;
        }

        return principal.FindAll("tenant_id").Any(claim =>
            int.TryParse(claim.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var allowedTenantId) &&
            allowedTenantId == tenantId);
    }

    private bool IsOperator(ClaimsPrincipal principal)
    {
        var hasRole = principal.Claims.Any(claim =>
            (claim.Type == "roles" || claim.Type == ClaimTypes.Role) &&
            string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase));

        var hasNamedGroup = principal.Claims.Any(claim =>
            claim.Type == "groups" &&
            string.Equals(claim.Value, "Operator", StringComparison.OrdinalIgnoreCase));

        var hasConfiguredGroup = !string.IsNullOrWhiteSpace(adminGroupId) && principal.Claims.Any(claim =>
            claim.Type == "groups" &&
            string.Equals(claim.Value, adminGroupId, StringComparison.OrdinalIgnoreCase));

        return hasRole || hasNamedGroup || hasConfiguredGroup;
    }
}

public sealed record SignalRShadowSubscriptionStatus(
    int ActiveConnections,
    int ActiveUsers,
    int ActiveGroups,
    int ActiveMemberships,
    ulong AcceptedConnections,
    ulong RejectedConnections,
    ulong AcceptedSubscriptions,
    ulong RejectedSubscriptions,
    ulong UnauthorizedSubscriptions,
    int ConnectionHighWaterMark,
    int GroupHighWaterMark,
    int MembershipHighWaterMark);

internal enum SubscriptionRemovalDisposition
{
    Ready = 0,
    AlreadyAbsent = 1,
    Rejected = 2
}

internal enum SubscriptionAdditionDisposition
{
    Ready = 0,
    AlreadyPresent = 1,
    Rejected = 2
}

public sealed class SignalRShadowSubscriptionRegistry(TimeProvider timeProvider)
{
    internal const int MaximumConnections = 256;
    internal const int MaximumConnectionsPerUser = 4;
    internal const int MaximumGroupsPerConnection = 16;
    internal const int MaximumMembersPerGroup = 256;
    internal const int MaximumGroups = 4_096;
    internal const int MaximumMemberships = 4_096;
    internal const int MaximumSubscriptionChangesPerMinute = 32;

    private readonly object _gate = new();
    private readonly Dictionary<string, ConnectionState> _connections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _userConnectionCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _groupMemberCounts = new(StringComparer.Ordinal);
    private ulong _acceptedConnections;
    private ulong _rejectedConnections;
    private ulong _acceptedSubscriptions;
    private ulong _rejectedSubscriptions;
    private ulong _unauthorizedSubscriptions;
    private int _connectionHighWaterMark;
    private int _groupHighWaterMark;
    private int _membershipHighWaterMark;
    private int _membershipCount;

    public bool TryRegisterConnection(string connectionId, ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        if (!IsSafeConnectionId(connectionId) || !TryResolveSubjectToken(principal, out var subjectToken))
        {
            lock (_gate)
            {
                _rejectedConnections = IncrementSaturating(_rejectedConnections);
            }

            return false;
        }

        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var existingConnection))
            {
                if (StringComparer.Ordinal.Equals(existingConnection.SubjectToken, subjectToken))
                {
                    return true;
                }

                _rejectedConnections = IncrementSaturating(_rejectedConnections);
                return false;
            }

            var userConnectionCount = _userConnectionCounts.GetValueOrDefault(subjectToken);
            if (_connections.Count >= MaximumConnections || userConnectionCount >= MaximumConnectionsPerUser)
            {
                _rejectedConnections = IncrementSaturating(_rejectedConnections);
                return false;
            }

            _connections.Add(connectionId, new ConnectionState(subjectToken, timeProvider.GetUtcNow()));
            _userConnectionCounts[subjectToken] = userConnectionCount + 1;
            _acceptedConnections = IncrementSaturating(_acceptedConnections);
            _connectionHighWaterMark = Math.Max(_connectionHighWaterMark, _connections.Count);
            return true;
        }
    }

    public bool TryAddSubscription(string connectionId, string groupName) =>
        TryBeginAddSubscription(connectionId, groupName) != SubscriptionAdditionDisposition.Rejected;

    internal SubscriptionAdditionDisposition TryBeginAddSubscription(
        string connectionId,
        string groupName)
    {
        if (!IsSafeConnectionId(connectionId) || !SignalRShadowGroupName.IsDerivedGroupName(groupName))
        {
            RecordRejectedSubscription();
            return SubscriptionAdditionDisposition.Rejected;
        }

        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var connection))
            {
                _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
                return SubscriptionAdditionDisposition.Rejected;
            }

            if (connection.Groups.Contains(groupName))
            {
                return SubscriptionAdditionDisposition.AlreadyPresent;
            }

            if (!TryConsumeSubscriptionChange(connection))
            {
                _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
                return SubscriptionAdditionDisposition.Rejected;
            }

            var existingMembers = _groupMemberCounts.GetValueOrDefault(groupName);
            var createsGroup = existingMembers == 0;
            if (connection.Groups.Count >= MaximumGroupsPerConnection ||
                _membershipCount >= MaximumMemberships ||
                existingMembers >= MaximumMembersPerGroup ||
                (createsGroup && _groupMemberCounts.Count >= MaximumGroups))
            {
                _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
                return SubscriptionAdditionDisposition.Rejected;
            }

            connection.Groups.Add(groupName);
            _groupMemberCounts[groupName] = existingMembers + 1;
            _membershipCount++;
            _acceptedSubscriptions = IncrementSaturating(_acceptedSubscriptions);
            _groupHighWaterMark = Math.Max(_groupHighWaterMark, _groupMemberCounts.Count);
            _membershipHighWaterMark = Math.Max(_membershipHighWaterMark, _membershipCount);
            return SubscriptionAdditionDisposition.Ready;
        }
    }

    internal SubscriptionRemovalDisposition TryBeginRemoveSubscription(
        string connectionId,
        string groupName)
    {
        lock (_gate)
        {
            if (!_connections.TryGetValue(connectionId, out var connection))
            {
                _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
                return SubscriptionRemovalDisposition.Rejected;
            }

            if (!connection.Groups.Contains(groupName))
            {
                return SubscriptionRemovalDisposition.AlreadyAbsent;
            }

            if (connection.PendingRemovals.Contains(groupName))
            {
                return SubscriptionRemovalDisposition.Ready;
            }

            if (!TryConsumeSubscriptionChange(connection))
            {
                _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
                return SubscriptionRemovalDisposition.Rejected;
            }

            connection.PendingRemovals.Add(groupName);
            return SubscriptionRemovalDisposition.Ready;
        }
    }

    internal void CommitRemoveSubscription(string connectionId, string groupName)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var connection) &&
                connection.PendingRemovals.Remove(groupName) &&
                connection.Groups.Remove(groupName))
            {
                RemoveGroupMembership(groupName);
            }
        }
    }

    internal void CancelRemoveSubscription(string connectionId, string groupName)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var connection) &&
                connection.PendingRemovals.Remove(groupName) && connection.SubscriptionChanges > 0)
            {
                connection.SubscriptionChanges--;
            }
        }
    }

    public void RollbackSubscription(string connectionId, string groupName)
    {
        lock (_gate)
        {
            if (_connections.TryGetValue(connectionId, out var connection) &&
                connection.Groups.Remove(groupName))
            {
                RemoveGroupMembership(groupName);
                if (connection.SubscriptionChanges > 0)
                {
                    connection.SubscriptionChanges--;
                }
            }
        }
    }

    public void RemoveConnection(string connectionId)
    {
        lock (_gate)
        {
            if (!_connections.Remove(connectionId, out var connection))
            {
                return;
            }

            foreach (var groupName in connection.Groups)
            {
                RemoveGroupMembership(groupName);
            }

            var remainingUserConnections = _userConnectionCounts[connection.SubjectToken] - 1;
            if (remainingUserConnections == 0)
            {
                _userConnectionCounts.Remove(connection.SubjectToken);
            }
            else
            {
                _userConnectionCounts[connection.SubjectToken] = remainingUserConnections;
            }
        }
    }

    public void RecordUnauthorizedSubscription()
    {
        lock (_gate)
        {
            _unauthorizedSubscriptions = IncrementSaturating(_unauthorizedSubscriptions);
            _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
        }
    }

    public SignalRShadowSubscriptionStatus GetStatus()
    {
        lock (_gate)
        {
            var status = new SignalRShadowSubscriptionStatus(
                _connections.Count,
                _userConnectionCounts.Count,
                _groupMemberCounts.Count,
                _membershipCount,
                _acceptedConnections,
                _rejectedConnections,
                _acceptedSubscriptions,
                _rejectedSubscriptions,
                _unauthorizedSubscriptions,
                _connectionHighWaterMark,
                _groupHighWaterMark,
                _membershipHighWaterMark);
            NetRatelAkkaTelemetry.SetSignalRSubscriptions(
                status.ActiveConnections,
                status.ActiveGroups,
                status.ActiveMemberships);
            return status;
        }
    }

    private bool TryConsumeSubscriptionChange(ConnectionState connection)
    {
        var now = timeProvider.GetUtcNow();
        if (now - connection.SubscriptionWindowStartedAt >= TimeSpan.FromMinutes(1))
        {
            connection.SubscriptionWindowStartedAt = now;
            connection.SubscriptionChanges = 0;
        }

        if (connection.SubscriptionChanges >= MaximumSubscriptionChangesPerMinute)
        {
            return false;
        }

        connection.SubscriptionChanges++;
        return true;
    }

    private void RemoveGroupMembership(string groupName)
    {
        var remainingMembers = _groupMemberCounts[groupName] - 1;
        if (remainingMembers == 0)
        {
            _groupMemberCounts.Remove(groupName);
        }
        else
        {
            _groupMemberCounts[groupName] = remainingMembers;
        }

        _membershipCount--;
    }

    private void RecordRejectedSubscription()
    {
        lock (_gate)
        {
            _rejectedSubscriptions = IncrementSaturating(_rejectedSubscriptions);
        }
    }

    private static bool TryResolveSubjectToken(ClaimsPrincipal principal, out string subjectToken)
    {
        if (principal.Identity?.IsAuthenticated != true)
        {
            subjectToken = string.Empty;
            return false;
        }

        var subject = principal.FindFirst("oid")?.Value ??
            principal.FindFirst("sub")?.Value ??
            principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (string.IsNullOrWhiteSpace(subject) || subject.Length > 256)
        {
            subjectToken = string.Empty;
            return false;
        }

        subjectToken = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject)))
            .ToLowerInvariant();
        return true;
    }

    private static bool IsSafeConnectionId(string? connectionId) =>
        !string.IsNullOrWhiteSpace(connectionId) && connectionId.Length <= 128 &&
        connectionId.All(character => !char.IsControl(character));

    private static ulong IncrementSaturating(ulong value) =>
        value == ulong.MaxValue ? value : value + 1;

    private sealed class ConnectionState(string subjectToken, DateTimeOffset subscriptionWindowStartedAt)
    {
        public string SubjectToken { get; } = subjectToken;

        public HashSet<string> Groups { get; } = new(StringComparer.Ordinal);

        public HashSet<string> PendingRemovals { get; } = new(StringComparer.Ordinal);

        public DateTimeOffset SubscriptionWindowStartedAt { get; set; } = subscriptionWindowStartedAt;

        public int SubscriptionChanges { get; set; }
    }
}
