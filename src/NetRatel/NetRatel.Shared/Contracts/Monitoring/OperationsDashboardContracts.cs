using System.Collections.Immutable;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>Tenant totals and a bounded page use the same persisted monitoring snapshot.</summary>
public sealed record OperationsDashboardDto(int TenantId, long OnlineClients, long OfflineClients,
    long FiringClients, long ActiveOccurrences, long PendingSeries, long UnknownSeries,
    long SuppressedOccurrences, long AlertingClients, ImmutableArray<OperationsAlertClientDto> Clients,
    int Page, int PageSize, DateTimeOffset GeneratedAtUtc);

public sealed record OperationsAlertClientDto(Guid AgentId, MonitoringClientIdentityDto Identity,
    MonitoringSeverity HighestSeverity, long ActiveOccurrences, long AcknowledgedOccurrences,
    long FiringOccurrences, long UnknownOccurrences, long SuppressedOccurrences);

public sealed record OperationsRecentJobDto(long RunId, long JobId, string JobName,
    MonitoringClientIdentityDto? ClientIdentity, JobRunStatusDto Status, DateTimeOffset StartedAtUtc);
