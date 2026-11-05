using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NetRatel.Shared.Contracts.Flows;

namespace NetRatel.Application.Flows;

public static class FlowActionKeys
{
    public static string Create(FlowRunLease lease, Guid nodeId) =>
        $"{lease.SourceInstanceId:N}:{lease.Event.OccurrenceId:N}:{lease.Event.EventId:N}:{lease.Version.Id:N}:{nodeId:N}";

    /// <summary>Canonical execution admission identity. Transport retries retain the same logical attempt.</summary>
    public static Guid CreateExecution(FlowRunLease lease, Guid nodeId, int attempt = 1)
        => CreateExecution(lease.Event.TenantId, lease.SourceInstanceId, lease.Event.OccurrenceId,
            lease.Event.EventId, lease.Version.Id, nodeId, attempt);

    /// <summary>Checks a persisted canonical identity without constructing a worker lease.</summary>
    public static Guid CreateExecution(int tenantId, Guid sourceInstanceId, Guid occurrenceId, Guid eventId,
        Guid versionId, Guid nodeId, int attempt = 1)
    {
        if (tenantId <= 0 || sourceInstanceId == Guid.Empty || occurrenceId == Guid.Empty || eventId == Guid.Empty ||
            versionId == Guid.Empty || nodeId == Guid.Empty) throw new ArgumentException("A complete execution identity is required.");
        if (attempt is < 1 or > FlowGraphV2Limits.MaximumRecoveryAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        var identity = string.Create(CultureInfo.InvariantCulture, $"{tenantId}:{sourceInstanceId:N}:{occurrenceId:N}:{eventId:N}:{versionId:N}:{nodeId:N}:{attempt}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
