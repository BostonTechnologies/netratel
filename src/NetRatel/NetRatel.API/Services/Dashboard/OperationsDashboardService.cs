using System.Collections.Immutable;
using System.Data;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Services.Monitoring;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.API.Services.Dashboard;

/// <summary>Reads durable monitoring and committed presence; never evaluates new alert thresholds.</summary>
public sealed class OperationsDashboardService(IMonitoringResourceAuthorizer authorization,
    IServiceScopeFactory scopes, TimeProvider clock)
{
    public async Task<OperationsDashboardDto> ReadAsync(int tenantId, int page, int pageSize,
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        if (tenantId <= 0 || page is < 0 or > 100_000 || pageSize is < 1 or > 50)
            throw new MonitoringApiException(400, "invalid_dashboard_page");
        if (!await authorization.AuthorizeAsync(user, NetRatelPermissions.MonitoringRead,
                new(tenantId, MonitoringResourceKind.Tenant), cancellationToken).ConfigureAwait(false))
            throw new MonitoringApiException(403, "monitoring_permission_required");

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var presence = await db.Database.SqlQuery<DashboardPresenceCounts>($"""
            SELECT count(*) FILTER (WHERE a."IsEnabled" AND a."Status"=0 AND a."RevokedAtUtc" IS NULL
                AND o."Active" AND o."AcceptanceGuardAtUtc" IS NOT NULL AND o."ConnectionEpoch">0
                AND o."ConnectionId" IS NOT NULL AND o."LastHeartbeatSequence">0
                AND o."AuthenticationExpiresAtUtc">GREATEST({now},CURRENT_TIMESTAMP)
                AND o."PresenceExpiresAtUtc">GREATEST({now},CURRENT_TIMESTAMP)) AS "OnlineClients",
                count(*) AS "TotalClients"
            FROM "Agents" a LEFT JOIN "ClientConnectionOwners" o
                ON o."TenantId"=a."TenantId" AND o."AgentId"=a."Id"
            WHERE a."TenantId"={tenantId} AND a."DeletedAtUtc" IS NULL AND a."SupersededAtUtc" IS NULL
            """).SingleAsync(cancellationToken).ConfigureAwait(false);
        var counts = await db.Database.SqlQuery<DashboardAlertCounts>($"""
            SELECT count(*) FILTER (WHERE "ActiveOccurrenceId" IS NOT NULL) AS "ActiveOccurrences",
                count(DISTINCT "AgentId") FILTER (WHERE "ActiveOccurrenceId" IS NOT NULL AND "Phase"={(short)MonitoringPhase.Firing}) AS "FiringClients",
                count(DISTINCT "AgentId") FILTER (WHERE "ActiveOccurrenceId" IS NOT NULL) AS "AlertingClients",
                count(*) FILTER (WHERE "Phase"={(short)MonitoringPhase.Pending}) AS "PendingSeries",
                count(*) FILTER (WHERE "EvidenceQuality"={(short)MonitoringEvidenceQuality.Unknown}) AS "UnknownSeries",
                count(*) FILTER (WHERE "ActiveOccurrenceId" IS NOT NULL AND "Suppressed") AS "SuppressedOccurrences"
            FROM "MonitoringSeries" WHERE "TenantId"={tenantId}
            """).SingleAsync(cancellationToken).ConfigureAwait(false);
        // Project only scalars from the pinned occurrence; large evidence/history payloads stay in storage.
        var rows = await db.Database.SqlQuery<DashboardAlertClientCounts>($"""
            SELECT "AgentId", max(("StateJson" -> 'Occurrence' -> 'PinnedRule' ->> 'Severity')::int) AS "HighestSeverity",
                count(*) AS "ActiveOccurrences", count(*) FILTER (WHERE "Acknowledged") AS "AcknowledgedOccurrences",
                count(*) FILTER (WHERE "Phase"={(short)MonitoringPhase.Firing}) AS "FiringOccurrences",
                count(*) FILTER (WHERE "EvidenceQuality"={(short)MonitoringEvidenceQuality.Unknown}) AS "UnknownOccurrences",
                count(*) FILTER (WHERE "Suppressed") AS "SuppressedOccurrences"
            FROM "MonitoringSeries" WHERE "TenantId"={tenantId} AND "ActiveOccurrenceId" IS NOT NULL
            GROUP BY "AgentId" ORDER BY "HighestSeverity" DESC,"ActiveOccurrences" DESC,"AgentId"
            LIMIT {pageSize} OFFSET {checked(page * pageSize)}
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var identities = await MonitoringIdentityProjection.ReadAsync(db, tenantId,
            rows.Select(row => row.AgentId), cancellationToken).ConfigureAwait(false);
        var clients = rows.Select(row => new OperationsAlertClientDto(row.AgentId, identities[row.AgentId],
            Enum.IsDefined((MonitoringSeverity)row.HighestSeverity) ? (MonitoringSeverity)row.HighestSeverity : throw new InvalidOperationException("invalid_monitoring_severity"),
            row.ActiveOccurrences, row.AcknowledgedOccurrences, row.FiringOccurrences,
            row.UnknownOccurrences, row.SuppressedOccurrences)).ToImmutableArray();
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new(tenantId, presence.OnlineClients, presence.TotalClients - presence.OnlineClients,
            counts.FiringClients, counts.ActiveOccurrences, counts.PendingSeries, counts.UnknownSeries,
            counts.SuppressedOccurrences, counts.AlertingClients, clients, page, pageSize, now);
    }
}

internal sealed class DashboardPresenceCounts
{
    public long OnlineClients { get; set; }
    public long TotalClients { get; set; }
}

internal sealed class DashboardAlertCounts
{
    public long FiringClients { get; set; }
    public long ActiveOccurrences { get; set; }
    public long AlertingClients { get; set; }
    public long PendingSeries { get; set; }
    public long UnknownSeries { get; set; }
    public long SuppressedOccurrences { get; set; }
}

internal sealed class DashboardAlertClientCounts
{
    public Guid AgentId { get; set; }
    public int HighestSeverity { get; set; }
    public long ActiveOccurrences { get; set; }
    public long AcknowledgedOccurrences { get; set; }
    public long FiringOccurrences { get; set; }
    public long UnknownOccurrences { get; set; }
    public long SuppressedOccurrences { get; set; }
}
