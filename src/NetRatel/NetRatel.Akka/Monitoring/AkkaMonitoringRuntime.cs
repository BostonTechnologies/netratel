using System.Collections.Immutable;
using Akka.Actor;
using Akka.Hosting;
using NetRatel.Akka.Hosting;
using NetRatel.Application.Monitoring;
using NetRatel.Application.Presence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Akka.Monitoring;

internal sealed class AkkaMonitoringRuntime(IRequiredActor<ClientMonitoringRegion> region, IMonitoringStore store, TimeSpan timeout) : IMonitoringRuntime
{
    private async Task<T> Ask<T>(IMonitoringClientMessage message, CancellationToken ct)
    { var actor = await region.GetAsync(ct).ConfigureAwait(false); return await actor.Ask<T>(message, timeout, ct).ConfigureAwait(false); }
    public Task<MonitoringEvidenceFence?> ReserveEvidenceRegistrationAsync(ClientKey client, Guid connectionId, long connectionEpoch,
        Guid registrationId, CancellationToken ct) => store.ReserveEvidenceRegistrationAsync(client, connectionId, connectionEpoch, registrationId, ct);
    public Task<MonitoringInputResult> BeginEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => Ask<MonitoringInputResult>(new BeginMonitoringStream(fence), ct);
    public Task<MonitoringInputResult> EndEvidenceStreamAsync(MonitoringEvidenceFence fence, CancellationToken ct) => Ask<MonitoringInputResult>(new EndMonitoringStream(fence), ct);
    public Task<MonitoringInputResult> RecordTelemetryAsync(MonitoringTelemetryInput input, CancellationToken ct) => Ask<MonitoringInputResult>(new RecordMonitoringTelemetry(input), ct);
    public Task<MonitoringInputResult> RecordServicesAsync(MonitoringServicesInput input, CancellationToken ct) => Ask<MonitoringInputResult>(new RecordMonitoringServices(input), ct);
    public Task<ImmutableArray<MonitoringSeriesState>> GetClientAsync(ClientKey client, CancellationToken ct) => Ask<ImmutableArray<MonitoringSeriesState>>(new GetClientMonitoring(client), ct);
    public Task<MonitoringSeriesPageDto> ReadTenantAsync(int tenant, int maximumCount, string? cursor, CancellationToken ct) => store.ReadTenantSeriesAsync(tenant, maximumCount, cursor, ct);
    public Task<MonitoringEventPageDto> ReadTenantEventsAsync(int tenant, int maximumCount, string? cursor, CancellationToken ct) => store.ReadTenantEventsAsync(tenant, maximumCount, cursor, ct);
    public Task<MonitoringSummaryDto> ReadTenantSummaryAsync(int tenant, CancellationToken ct) => store.ReadTenantSummaryAsync(tenant, ct);
    public Task<MonitoringStoreWriteResult> AcknowledgeAsync(MonitoringOperatorCommand command, CancellationToken ct) => Ask<MonitoringStoreWriteResult>(new AcknowledgeMonitoringOccurrence(command), ct);
    public Task<MonitoringStoreWriteResult> ClearAsync(MonitoringOperatorCommand command, CancellationToken ct) => Ask<MonitoringStoreWriteResult>(new ClearMonitoringOccurrence(command), ct);
    public async Task RefreshAsync(ClientKey client, CancellationToken ct) => _ = await Ask<bool>(new RefreshClientMonitoring(client), ct).ConfigureAwait(false);
}
