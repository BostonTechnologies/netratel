using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts;
using NetRatel.Shared.Contracts.Monitoring;
using NetRatel.Shared.Contracts.Netclaw;

namespace NetRatel.API.Endpoints.Search;

public static partial class GlobalSearchEndpoints
{
    private static async Task<IResult> SearchAlertsAsync([FromServices] OrchestratorDbContext db, [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal, [FromServices] DatabaseCommandMetricsInterceptor metrics, [FromServices] ILoggerFactory log,
        [FromQuery] string? q, [FromQuery] int pageSize = 6, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var ids = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.MonitoringRead, ct);
        var term = NormalizeOpsTerm(q); var take = NormalizePageSize(pageSize);
        metrics.Reset(); var started = Stopwatch.GetTimestamp();
        var rows = await BuildAlertQuery(db, term, ids).Skip(OpsOffset(page, take)).Take(take).ToListAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var current = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.MonitoringRead, ct);
        rows = rows.Where(row => InScope(current, row.TenantId)).Select(row => row with { TenantName = Bound(row.TenantName, 160) ?? "", Resource = Bound(row.Resource, 160) ?? "" }).ToList();
        LogSearch(log, metrics, "alerts", term, rows.Count, rows.Count, started, elapsed, TimeSpan.Zero);
        return Results.Ok(new PagedResult<GlobalSearchAlertDto>(rows, Math.Clamp(page, 1, 4000), take, rows.Count));
    }

    private static async Task<IResult> SearchFlowsAsync([FromServices] OrchestratorDbContext db, [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal, [FromServices] DatabaseCommandMetricsInterceptor metrics, [FromServices] ILoggerFactory log,
        [FromQuery] string? q, [FromQuery] int pageSize = 6, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var ids = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.FlowRead, ct);
        var term = NormalizeOpsTerm(q); var take = NormalizePageSize(pageSize);
        metrics.Reset(); var started = Stopwatch.GetTimestamp();
        var rows = await BuildFlowQuery(db, term, ids).Skip(OpsOffset(page, take)).Take(take).ToListAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var current = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.FlowRead, ct);
        rows = rows.Where(row => InScope(current, row.TenantId)).Select(row => row with { TenantName = Bound(row.TenantName, 160) ?? "" }).ToList();
        LogSearch(log, metrics, "flows", term, rows.Count, rows.Count, started, elapsed, TimeSpan.Zero);
        return Results.Ok(new PagedResult<GlobalSearchFlowDto>(rows, Math.Clamp(page, 1, 4000), take, rows.Count));
    }

    private static async Task<IResult> SearchInvestigationsAsync([FromServices] OrchestratorDbContext db, [FromServices] IEffectiveAccessService access,
        ClaimsPrincipal principal, [FromServices] DatabaseCommandMetricsInterceptor metrics, [FromServices] ILoggerFactory log,
        [FromQuery] string? q, [FromQuery] int pageSize = 6, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        var ids = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.TelemetryRead, ct);
        var alertIds = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.MonitoringRead, ct);
        var term = NormalizeOpsTerm(q); var take = NormalizePageSize(pageSize);
        metrics.Reset(); var started = Stopwatch.GetTimestamp();
        var rows = await BuildInvestigationQuery(db, term, ids, alertIds).Skip(OpsOffset(page, take)).Take(take).ToListAsync(ct);
        var elapsed = Stopwatch.GetElapsedTime(started);
        var current = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.TelemetryRead, ct);
        var currentAlerts = await access.GetAuthorizedTenantIdsAsync(principal, NetRatelPermissions.MonitoringRead, ct);
        rows = rows.Where(row => InScope(current, row.TenantId) && (row.OccurrenceId is null || InScope(currentAlerts, row.TenantId)))
            .Select(row => row with { TenantName = Bound(row.TenantName, 160) ?? "", ClientContext = Bound(row.ClientContext, 160) }).ToList();
        LogSearch(log, metrics, "investigations", term, rows.Count, rows.Count, started, elapsed, TimeSpan.Zero);
        return Results.Ok(new PagedResult<GlobalSearchInvestigationDto>(rows, Math.Clamp(page, 1, 4000), take, rows.Count));
    }

    internal static IQueryable<GlobalSearchFlowDto> BuildFlowQuery(OrchestratorDbContext db, string? term, int[]? ids = null)
    {
        var flows = db.FlowDefinitions.AsNoTracking();
        if (ids is not null) flows = flows.Where(row => ids.Contains(row.TenantId));
        var query = from flow in flows join tenant in db.Tenants.AsNoTracking() on flow.TenantId equals tenant.Id
                    select new { Flow = flow, TenantName = tenant.Name };
        if (term is not null)
        {
            var like = OpsLike(term); var hasId = Guid.TryParse(term, out var id);
            var enabled = term.Equals("enabled", StringComparison.OrdinalIgnoreCase);
            var disabled = term.Equals("disabled", StringComparison.OrdinalIgnoreCase);
            var published = term.Equals("published", StringComparison.OrdinalIgnoreCase);
            var draft = term.Equals("draft", StringComparison.OrdinalIgnoreCase);
            query = db.Database.IsNpgsql()
                ? query.Where(row => EF.Functions.ILike(row.Flow.Name, like) || EF.Functions.ILike(row.TenantName, like) ||
                    (hasId && (row.Flow.Id == id || row.Flow.PublishedVersionId == id || db.FlowRuns.Any(run => run.TenantId == row.Flow.TenantId && run.FlowId == row.Flow.Id && run.Id == id))) ||
                    (enabled && row.Flow.Enabled) || (disabled && !row.Flow.Enabled) || (published && row.Flow.PublishedVersionNumber > 0) || (draft && row.Flow.PublishedVersionNumber == 0))
                : query.Where(row => row.Flow.Name.ToUpper().Contains(term.ToUpper()) || row.TenantName.ToUpper().Contains(term.ToUpper()) ||
                    (hasId && (row.Flow.Id == id || row.Flow.PublishedVersionId == id || db.FlowRuns.Any(run => run.TenantId == row.Flow.TenantId && run.FlowId == row.Flow.Id && run.Id == id))) ||
                    (enabled && row.Flow.Enabled) || (disabled && !row.Flow.Enabled) || (published && row.Flow.PublishedVersionNumber > 0) || (draft && row.Flow.PublishedVersionNumber == 0));
        }
        return query.OrderBy(row => row.Flow.Name).ThenBy(row => row.Flow.Id)
            .Select(row => new GlobalSearchFlowDto(row.Flow.Id, row.Flow.TenantId, row.Flow.Name,
                row.TenantName.Length > 160 ? row.TenantName.Substring(0, 160) : row.TenantName, row.Flow.Enabled, row.Flow.PublishedVersionNumber));
    }

    internal static IQueryable<GlobalSearchInvestigationDto> BuildInvestigationQuery(OrchestratorDbContext db, string? term,
        int[]? ids = null, int[]? alertIds = null)
    {
        var investigations = db.AiInvestigations.AsNoTracking();
        if (ids is not null) investigations = investigations.Where(row => ids.Contains(row.TenantId));
        if (alertIds is not null) investigations = investigations.Where(row => row.OccurrenceId == null || alertIds.Contains(row.TenantId));
        var query = from investigation in investigations join tenant in db.Tenants.AsNoTracking() on investigation.TenantId equals tenant.Id
                    select new { Investigation = investigation, TenantName = tenant.Name };
        if (term is not null)
        {
            var like = OpsLike(term); var hasId = Guid.TryParse(term, out var id);
            var hasState = Enum.TryParse<InvestigationState>(term, true, out var state) && Enum.IsDefined(state);
            var matchingAgents = db.Database.IsNpgsql() ? MatchingOpsAgents(db, like, ids) : null;
            query = db.Database.IsNpgsql()
                ? query.Where(row => EF.Functions.ILike(row.Investigation.Title, like) || EF.Functions.ILike(row.TenantName, like) ||
                    (hasId && (row.Investigation.Id == id || row.Investigation.OccurrenceId == id || row.Investigation.FlowRunId == id)) || (hasState && row.Investigation.State == state) ||
                    db.Agents.Any(agent => agent.TenantId == row.Investigation.TenantId && row.Investigation.AgentIds.Contains(agent.Id) &&
                        ((agent.FriendlyName != null && EF.Functions.ILike(agent.FriendlyName, like)) || (agent.Name != null && EF.Functions.ILike(agent.Name, like)) ||
                         agent.Tags.Any(tag => EF.Functions.ILike(tag, like)) || matchingAgents!.Contains(agent.Id))))
                : query.Where(row => row.Investigation.Title.ToUpper().Contains(term.ToUpper()) || row.TenantName.ToUpper().Contains(term.ToUpper()) ||
                    (hasId && (row.Investigation.Id == id || row.Investigation.OccurrenceId == id || row.Investigation.FlowRunId == id)) || (hasState && row.Investigation.State == state) ||
                    db.Agents.Any(agent => agent.TenantId == row.Investigation.TenantId && row.Investigation.AgentIds.Contains(agent.Id) &&
                        ((agent.FriendlyName != null && agent.FriendlyName.ToUpper().Contains(term.ToUpper())) || (agent.Name != null && agent.Name.ToUpper().Contains(term.ToUpper())) ||
                         agent.Tags.Any(tag => tag.ToUpper().Contains(term.ToUpper())) || DeviceMetadataMatches(agent.DeviceInfoJson, term))));
        }
        return query.OrderByDescending(row => row.Investigation.LastActivityUtc).ThenBy(row => row.Investigation.Id)
            .Select(row => new GlobalSearchInvestigationDto(row.Investigation.Id, row.Investigation.TenantId, row.Investigation.Title,
                row.TenantName.Length > 160 ? row.TenantName.Substring(0, 160) : row.TenantName,
                row.Investigation.State.ToString(), db.Agents.Where(agent => agent.TenantId == row.Investigation.TenantId && row.Investigation.AgentIds.Contains(agent.Id))
                    .OrderBy(agent => agent.Id).Select(agent => (agent.FriendlyName ?? agent.Name ?? "").Substring(0,
                        Math.Min(160, (agent.FriendlyName ?? agent.Name ?? "").Length))).FirstOrDefault(), row.Investigation.OccurrenceId, row.Investigation.FlowRunId));
    }

    internal static IQueryable<GlobalSearchAlertDto> BuildAlertQuery(OrchestratorDbContext db, string? term, int[]? ids = null)
    {
        if (!db.Database.IsNpgsql())
        {
            var occurrences = db.MonitoringOccurrences.AsNoTracking();
            if (ids is not null) occurrences = occurrences.Where(row => ids.Contains(row.TenantId));
            var query = from occurrence in occurrences join tenant in db.Tenants.AsNoTracking() on occurrence.TenantId equals tenant.Id
                        join agent in db.Agents.AsNoTracking() on new { occurrence.TenantId, Id = occurrence.AgentId } equals new { agent.TenantId, agent.Id } into clients
                        from agent in clients.DefaultIfEmpty()
                        select new { Occurrence = occurrence, TenantName = tenant.Name, Agent = agent };
            if (term is not null)
                query = query.Where(row => JsonText(row.Occurrence.OccurrenceJson, "PinnedRule", "Name").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.TenantName.Contains(term, StringComparison.OrdinalIgnoreCase) || row.Occurrence.ResourceKey.Contains(term, StringComparison.OrdinalIgnoreCase) ||
                    row.Occurrence.OccurrenceId.ToString().Equals(term, StringComparison.OrdinalIgnoreCase) ||
                    (row.Agent != null && ((row.Agent.FriendlyName ?? row.Agent.Name ?? "").Contains(term, StringComparison.OrdinalIgnoreCase) ||
                        row.Agent.Tags.Any(tag => tag.Contains(term, StringComparison.OrdinalIgnoreCase)) || DeviceMetadataMatches(row.Agent.DeviceInfoJson, term))) ||
                    AlertSeverity(row.Occurrence.OccurrenceJson).Equals(term, StringComparison.OrdinalIgnoreCase) ||
                    (row.Occurrence.EndedAtUtc == null ? "Firing" : "Closed").Equals(term, StringComparison.OrdinalIgnoreCase));
            return query.OrderByDescending(row => row.Occurrence.RaisedAtUtc).ThenBy(row => row.Occurrence.OccurrenceId)
                .Select(row => new GlobalSearchAlertDto(row.Occurrence.OccurrenceId, row.Occurrence.TenantId,
                    JsonText(row.Occurrence.OccurrenceJson, "PinnedRule", "Name"), row.TenantName, row.Agent == null ? JsonText(row.Occurrence.OccurrenceJson, "ClientIdentity", "DisplayName") : row.Agent.FriendlyName ?? row.Agent.Name,
                    row.Occurrence.ResourceKey, AlertSeverity(row.Occurrence.OccurrenceJson), row.Occurrence.EndedAtUtc == null ? "Firing" : "Closed"));
        }
        // Read only named presentation fields from the immutable snapshot. Execution
        // principal/credential IDs and arbitrary JSON values are never searchable.
        var unrestricted = ids is null; var scope = ids ?? []; var noTerm = term is null; var like = OpsLike(term ?? "");
        return db.Database.SqlQuery<GlobalSearchAlertDto>($"""
            SELECT o."OccurrenceId", o."TenantId", left(coalesce(o."OccurrenceJson" -> 'PinnedRule' ->> 'Name', 'Monitoring alert'), 200) AS "Title",
                left(t."Name", 160) AS "TenantName", left(coalesce(a."FriendlyName", o."OccurrenceJson" -> 'ClientIdentity' ->> 'DisplayName', a."Name", 'Monitored clients'), 160) AS "ClientName",
                left(o."ResourceKey", 160) AS "Resource", CASE o."OccurrenceJson" -> 'PinnedRule' ->> 'Severity' WHEN '0' THEN 'Information' WHEN '1' THEN 'Warning' WHEN '2' THEN 'Critical' ELSE 'Unknown' END AS "Severity",
                CASE WHEN o."EndedAtUtc" IS NOT NULL THEN 'Closed' WHEN s."Phase" = 3 THEN 'Recovering' ELSE 'Firing' END AS "State"
            FROM "MonitoringOccurrences" o JOIN "Tenants" t ON t."Id" = o."TenantId"
            LEFT JOIN "Agents" a ON a."TenantId" = o."TenantId" AND a."Id" = o."AgentId"
            LEFT JOIN "MonitoringSeries" s ON s."TenantId" = o."TenantId" AND s."RuleId" = o."RuleId" AND s."AgentId" = o."AgentId" AND s."ResourceKey" = o."ResourceKey" AND s."ActiveOccurrenceId" = o."OccurrenceId"
            WHERE ({unrestricted} OR o."TenantId" = ANY({scope})) AND ({noTerm} OR
                o."OccurrenceJson" -> 'PinnedRule' ->> 'Name' ILIKE {like} OR t."Name" ILIKE {like} OR o."ResourceKey" ILIKE {like} OR o."OccurrenceId"::text ILIKE {like} OR
                a."FriendlyName" ILIKE {like} OR a."Name" ILIKE {like} OR
                (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'hostName') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'hostName' END) ILIKE {like} OR
                (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'reportedAddress') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'reportedAddress' END) ILIKE {like} OR
                (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'ipAddress') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'ipAddress' END) ILIKE {like} OR
                o."OccurrenceJson" -> 'ClientIdentity' ->> 'DisplayName' ILIKE {like} OR o."OccurrenceJson" -> 'ClientIdentity' ->> 'Hostname' ILIKE {like} OR o."OccurrenceJson" -> 'ClientIdentity' ->> 'ReportedAddress' ILIKE {like} OR
                EXISTS (SELECT 1 FROM unnest(a."Tags") tag WHERE tag ILIKE {like}) OR
                CASE o."OccurrenceJson" -> 'PinnedRule' ->> 'Severity' WHEN '0' THEN 'Information' WHEN '1' THEN 'Warning' WHEN '2' THEN 'Critical' ELSE 'Unknown' END ILIKE {like} OR
                CASE WHEN o."EndedAtUtc" IS NOT NULL THEN 'Closed' WHEN s."Phase" = 3 THEN 'Recovering' ELSE 'Firing' END ILIKE {like})
            ORDER BY o."RaisedAtUtc" DESC, o."OccurrenceId"
            """);
    }

    private static IQueryable<Guid> MatchingOpsAgents(OrchestratorDbContext db, string like, int[]? ids)
    {
        var unrestricted = ids is null; var scope = ids ?? [];
        return db.Database.SqlQuery<Guid>($"""
        SELECT a."Id" AS "Value" FROM "Agents" a WHERE ({unrestricted} OR a."TenantId" = ANY({scope})) AND (
            (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'hostName') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'hostName' END) ILIKE {like} OR
            (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'reportedAddress') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'reportedAddress' END) ILIKE {like} OR
            (CASE WHEN jsonb_typeof((CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) -> 'ipAddress') = 'string' THEN (CASE WHEN a."DeviceInfoJson" IS JSON OBJECT THEN a."DeviceInfoJson"::jsonb ELSE 'null'::jsonb END) ->> 'ipAddress' END) ILIKE {like})
        """);
    }

    private static async Task<IResult> AlertDetailAsync(int tenantId, Guid occurrenceId, [FromServices] OrchestratorDbContext db,
        [FromServices] IEffectiveAccessService access, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (tenantId <= 0 || !await access.AuthorizeAsync(principal, NetRatelPermissions.MonitoringRead, tenantId, ct)) return Results.Forbid();
        var row = await db.MonitoringOccurrences.AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenantId && row.OccurrenceId == occurrenceId, ct);
        if (row is null) return Results.NotFound();
        if (!await access.AuthorizeAsync(principal, NetRatelPermissions.MonitoringRead, tenantId, ct)) return Results.Forbid();
        var occurrence = JsonSerializer.Deserialize<MonitoringOccurrenceDto>(row.OccurrenceJson);
        if (occurrence is null || occurrence.OccurrenceId != occurrenceId || occurrence.PinnedRule.TenantId != tenantId || occurrence.PinnedRule.RuleId != row.RuleId) return Results.NotFound();
        return Results.Ok(new MonitoringAlertDetailDto(tenantId, row.RuleId, row.AgentId, row.ResourceKey, occurrence));
    }

    private static bool InScope(int[]? ids, int tenantId) => ids is null || ids.Contains(tenantId);
    private static int OpsOffset(int page, int take) => (Math.Clamp(page, 1, 4000) - 1) * take;
    private static string OpsLike(string term) => $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
    private static string? NormalizeOpsTerm(string? term) => NormalizeTerm(term) is { } value ? value[..Math.Min(200, value.Length)] : null;
    private static string? Bound(string? value, int length) => value is null ? null : new string(value.Where(character => !char.IsControl(character)).Take(length).ToArray());
    private static bool DeviceMetadataMatches(string? json, string term)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && new[] { "hostName", "reportedAddress", "ipAddress" }.Any(field =>
                document.RootElement.TryGetProperty(field, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()?.Contains(term, StringComparison.OrdinalIgnoreCase) == true);
        }
        catch (JsonException) { return false; }
    }
    private static string JsonText(string json, string parent, string field)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.TryGetProperty(parent, out var element) && element.ValueKind == JsonValueKind.Object && element.TryGetProperty(field, out var value)
            ? value.ToString() : "";
    }
    private static string AlertSeverity(string json) => int.TryParse(JsonText(json, "PinnedRule", "Severity"), out var severity) && Enum.IsDefined(typeof(MonitoringSeverity), severity)
        ? ((MonitoringSeverity)severity).ToString() : "Unknown";
}
