using System.IO;
using NetRatel.AgentGateway.Contracts.V1;

namespace NetRatel.Client.Service.Gateway;

/// <summary>Optional exact byte counters stay within the existing complete-envelope transport limit.</summary>
internal static class TelemetrySnapshotSerializer
{
    internal const int MaximumEnvelopeBytes = 16 * 1024 - 1;

    public static AgentTelemetryFrame CreateEnvelope(TelemetryFrame snapshot, GatewayPresenceSession session, string protocolVersion)
    {
        var envelope = new AgentTelemetryFrame
        {
            ProtocolVersion = protocolVersion, TenantId = session.TenantId, ClientId = session.AgentId.ToString("D"),
            ConnectionEpoch = session.ConnectionEpoch, ConnectionId = session.ConnectionId.ToString("D"), Sequence = snapshot.Sequence,
            Snapshot = snapshot
        };
        if (envelope.CalculateSize() <= MaximumEnvelopeBytes) return envelope;
        envelope.Snapshot = snapshot.Clone();
        while (envelope.CalculateSize() > MaximumEnvelopeBytes)
        {
            // Absent per-resource data remains Unknown. Do not turn a missing disk into recovered/free evidence.
            if (envelope.Snapshot.Networks.Count > 0) envelope.Snapshot.Networks.RemoveAt(envelope.Snapshot.Networks.Count - 1);
            else if (envelope.Snapshot.Disks.Count > 0) envelope.Snapshot.Disks.RemoveAt(envelope.Snapshot.Disks.Count - 1);
            else throw new InvalidDataException("Telemetry metadata exceeds the existing transport limit.");
        }
        return envelope;
    }
}
