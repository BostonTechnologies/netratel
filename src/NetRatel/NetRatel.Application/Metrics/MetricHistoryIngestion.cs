using NetRatel.Application.Monitoring;
using NetRatel.Application.Services;
using NetRatel.Application.Telemetry;

namespace NetRatel.Application.Metrics;

/// <summary>Best-effort ingress for already admitted evidence. Completion does not depend on history storage.</summary>
public interface IMetricHistoryIngestion
{
    void Offer(MonitoringEvidenceFence fence, TelemetrySnapshot snapshot, int payloadBytes);
    void Offer(MonitoringEvidenceFence fence, ClientServicesState state, int payloadBytes);
}
