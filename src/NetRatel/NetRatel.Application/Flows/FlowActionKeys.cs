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
    {
        if (nodeId == Guid.Empty) throw new ArgumentException("A node identity is required.", nameof(nodeId));
        if (attempt is < 1 or > FlowGraphV2Limits.MaximumRecoveryAttempts) throw new ArgumentOutOfRangeException(nameof(attempt));
        var identity = string.Create(CultureInfo.InvariantCulture, $"{lease.Event.TenantId}:{Create(lease, nodeId)}:{attempt}");
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(identity)).AsSpan(0, 16));
    }
}
