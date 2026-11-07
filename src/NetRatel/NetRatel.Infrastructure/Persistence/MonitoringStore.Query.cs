using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NetRatel.Shared.Contracts.Monitoring;

namespace NetRatel.Infrastructure.Persistence;

public sealed partial class MonitoringStore
{
    public async Task<MonitoringSeriesPageDto> ReadTenantSeriesAsync(MonitoringQueryDto requested, string? cursor, CancellationToken cancellationToken)
    {
        var query = requested.Normalize();
        var last = ReadPageCursor(cursor, query, false);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var now = timeProvider.GetUtcNow();
        var matching = await SelectSeries(db, query, null, now).LongCountAsync(cancellationToken).ConfigureAwait(false);
        var rows = await SelectSeries(db, query, last, now).AsNoTracking()
            .OrderBy(row => row.RuleId).ThenBy(row => row.AgentId).ThenBy(row => row.ResourceKey)
            .Take(query.MaximumCount + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = BudgetPage(rows.Take(query.MaximumCount), ReadState);
        return new(page.Select(ReadState).ToImmutableArray(), rows.Length > page.Length && page.Length > 0
            ? WritePageCursor(query, false, page[^1].RuleId, page[^1].AgentId, page[^1].ResourceKey) : null,
            MatchingCount: matching);
    }

    private static IQueryable<MonitoringSeriesRecord> SelectSeries(OrchestratorDbContext db, MonitoringQueryDto query,
        PageCursor? last, DateTimeOffset now)
    {
        var client = query.ClientId ?? Guid.Empty;
        var severity = query.Severity is { } selectedSeverity ? (int)selectedSeverity : -1;
        var view = (int)query.View;
        var text = query.Text ?? "";
        return db.MonitoringSeries.FromSqlInterpolated($"""
            SELECT s.* FROM "MonitoringSeries" s
            LEFT JOIN "MonitoringRules" r ON r."TenantId"=s."TenantId" AND r."RuleId"=s."RuleId"
            LEFT JOIN "Agents" a ON a."TenantId"=s."TenantId" AND a."Id"=s."AgentId"
            CROSS JOIN LATERAL (SELECT
                s."EvidenceQuality"={(short)MonitoringEvidenceQuality.Unknown}
                OR COALESCE((s."StateJson" #>> ARRAY['LatestEvidence','Classification'])::integer,0)={(int)MonitoringClassification.Unknown}
                OR s."StateJson" #>> ARRAY['LatestEvidence','ObservedAtUtc'] IS NULL
                OR s."StateJson" #>> ARRAY['LatestEvidence','ReceivedAtUtc'] IS NULL
                OR (s."StateJson" #>> ARRAY['LatestEvidence','ObservedAtUtc'])::timestamptz > {now}
                OR (s."StateJson" #>> ARRAY['LatestEvidence','ReceivedAtUtc'])::timestamptz > {now}
                OR {now} - (s."StateJson" #>> ARRAY['LatestEvidence','ObservedAtUtc'])::timestamptz >
                    CASE WHEN s."AgentId"={Guid.Empty} AND s."ResourceKey"={MonitoringOfflinePresentation.ResourceKey}
                    THEN {MonitoringOfflinePresentation.SnapshotFreshness} ELSE COALESCE((r."DefinitionJson" ->> 'FreshnessBudget')::interval, interval '0') END
                OR {now} - (s."StateJson" #>> ARRAY['LatestEvidence','ReceivedAtUtc'])::timestamptz >
                    CASE WHEN s."AgentId"={Guid.Empty} AND s."ResourceKey"={MonitoringOfflinePresentation.ResourceKey}
                    THEN {MonitoringOfflinePresentation.SnapshotFreshness} ELSE COALESCE((r."DefinitionJson" ->> 'FreshnessBudget')::interval, interval '0') END
                AS unknown) evidence
            WHERE s."TenantId"={query.TenantId}
                AND ({query.ClientId == null} OR s."AgentId"={client}
                    OR COALESCE(s."StateJson" -> 'OfflineAgentIds','[]'::jsonb) @> jsonb_build_array({client.ToString("D")}::text))
                AND ({severity}=-1 OR COALESCE((s."StateJson" #>> ARRAY['Occurrence','PinnedRule','Severity'])::integer,
                    (r."DefinitionJson" ->> 'Severity')::integer)={severity})
                AND ({query.FromUtc == null} OR (s."StateJson" #>> ARRAY['Occurrence','RaisedAtUtc'])::timestamptz >= {query.FromUtc ?? DateTimeOffset.MinValue})
                AND ({query.ToExclusiveUtc == null} OR (s."StateJson" #>> ARRAY['Occurrence','RaisedAtUtc'])::timestamptz < {query.ToExclusiveUtc ?? DateTimeOffset.MaxValue})
                AND ({text}='' OR strpos(lower(concat_ws(' ', r."DefinitionJson" ->> 'Name',
                    s."StateJson" #>> ARRAY['Occurrence','PinnedRule','Name'], a."FriendlyName", a."Name", a."DeviceInfoJson",
                    s."StateJson" #>> ARRAY['Occurrence','ClientIdentity','DisplayName'], s."StateJson" #>> ARRAY['Occurrence','ClientIdentity','Hostname'],
                    s."ResourceKey", s."StateJson" #>> ARRAY['LatestEvidence','OfflineSummary','ClientNames'])), {text}) > 0)
                AND ({view}={(int)MonitoringView.All} OR (
                    NOT (s."AgentId"={Guid.Empty} AND s."ResourceKey"={MonitoringOfflinePresentation.ResourceKey}
                        AND s."Phase" IN ({(short)MonitoringPhase.Suspended},{(short)MonitoringPhase.NotApplicable}))
                    AND (s."ActiveOccurrenceId" IS NOT NULL OR s."Phase"={(short)MonitoringPhase.Pending} OR evidence.unknown)
                    AND ({view}={(int)MonitoringView.Active}
                        OR ({view}={(int)MonitoringView.Firing} AND s."ActiveOccurrenceId" IS NOT NULL AND s."Phase"={(short)MonitoringPhase.Firing})
                        OR ({view}={(int)MonitoringView.Pending} AND s."Phase"={(short)MonitoringPhase.Pending})
                        OR ({view}={(int)MonitoringView.Unknown} AND evidence.unknown)
                        OR ({view}={(int)MonitoringView.Acknowledged} AND s."Acknowledged")
                        OR ({view}={(int)MonitoringView.Suppressed} AND s."Suppressed")
                        OR ({view}={(int)MonitoringView.Offline} AND COALESCE((s."StateJson" #>> ARRAY['LatestEvidence','OfflineSummary','OfflineCount'])::integer,0)>0))))
                AND ({last == null} OR (s."RuleId", s."AgentId", s."ResourceKey") >
                    ({(last == null ? Guid.Empty : last.RuleId)}, {(last == null ? Guid.Empty : last.AgentId)}, {(last == null ? "" : last.ResourceKey)}))
            """);
    }

    public async Task<MonitoringEventPageDto> ReadTenantEventsAsync(MonitoringQueryDto requested, string? cursor, CancellationToken cancellationToken)
    {
        var query = requested.Normalize(history: true);
        var last = ReadPageCursor(cursor, query, true);
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<OrchestratorDbContext>();
        var matching = await SelectHistory(db, query, null).LongCountAsync(cancellationToken).ConfigureAwait(false);
        var candidates = await SelectHistory(db, query, last).OrderByDescending(row => row.AtUtc).ThenByDescending(row => row.ItemId)
            .Take(query.MaximumCount + 1).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var page = candidates.Take(query.MaximumCount).ToArray();
        var eventIds = page.Where(row => !row.IsAudit).Select(row => row.ItemId).ToArray();
        var auditIds = page.Where(row => row.IsAudit).Select(row => row.ItemId).ToArray();
        var events = await db.MonitoringEvents.AsNoTracking().Where(row => row.TenantId == query.TenantId && eventIds.Contains(row.EventId))
            .OrderByDescending(row => row.AtUtc).ThenByDescending(row => row.EventId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var audits = await db.MonitoringAudits.AsNoTracking().Where(row => row.TenantId == query.TenantId && auditIds.Contains(row.AuditId))
            .OrderByDescending(row => row.AtUtc).ThenByDescending(row => row.AuditId).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var items = events.Select(row => Deserialize<MonitoringEventIntent>(row.EventJson)).ToImmutableArray();
        var historyAudits = audits.Select(row => new MonitoringHistoryAuditDto(row.AuditId, row.Operation, row.EntityId, row.EntityKind,
            ReadHistoryDetail(row.DetailsJson, "EntityName"), row.OperatorId, ReadHistoryDetail(row.DetailsJson, "OperatorDisplayName"), row.Reason, row.AtUtc)).ToImmutableArray();
        var eventMap = items.ToDictionary(item => item.EventId);
        var auditMap = historyAudits.ToDictionary(item => item.AuditId);
        var bounded = BudgetPage(page, row => row.IsAudit ? (object)auditMap[row.ItemId] : eventMap[row.ItemId]);
        var retained = bounded.Select(row => row.ItemId).ToHashSet();
        return new(items.Where(item => retained.Contains(item.EventId)).ToImmutableArray(), candidates.Length > bounded.Length && bounded.Length > 0
            ? WritePageCursor(query, true, bounded[^1].ItemId, Guid.Empty, "", bounded[^1].AtUtc) : null,
            Audits: historyAudits.Where(item => retained.Contains(item.AuditId)).ToImmutableArray(), MatchingCount: matching);
    }

    private static IQueryable<HistoryRow> SelectHistory(OrchestratorDbContext db, MonitoringQueryDto query, PageCursor? last)
    {
        var client = query.ClientId ?? Guid.Empty;
        var severity = query.Severity is { } value ? (int)value : -1;
        var text = query.Text ?? "";
        return db.Database.SqlQuery<HistoryRow>($"""
            SELECT "AtUtc", "ItemId", "IsAudit" FROM (
                SELECT e."AtUtc", e."EventId" AS "ItemId", FALSE AS "IsAudit" FROM "MonitoringEvents" e
                JOIN "MonitoringOccurrences" o ON o."TenantId"=e."TenantId" AND o."OccurrenceId"=e."OccurrenceId"
                WHERE e."TenantId"={query.TenantId}
                    AND ({query.ClientId == null} OR o."AgentId"={client}
                        OR COALESCE(e."EventJson" -> 'OfflineAgentIds','[]'::jsonb) @> jsonb_build_array({client.ToString("D")}::text))
                    AND ({severity}=-1 OR (e."EventJson" #>> ARRAY['PinnedRule','Severity'])::integer={severity})
                    AND ({text}='' OR strpos(lower(concat_ws(' ', e."EventJson" #>> ARRAY['PinnedRule','Name'], o."ResourceKey",
                        e."EventJson" #>> ARRAY['ClientIdentity','DisplayName'], e."EventJson" #>> ARRAY['ClientIdentity','Hostname'],
                        e."EventJson" #>> ARRAY['Evidence','OfflineSummary','ClientNames'])), {text})>0)
                UNION ALL
                SELECT a."AtUtc", a."AuditId" AS "ItemId", TRUE AS "IsAudit" FROM "MonitoringAudits" a
                WHERE a."TenantId"={query.TenantId} AND a."EntityKind"<>'series'
                    AND {query.ClientId == null} AND {query.Severity == null}
                    AND ({text}='' OR strpos(lower(concat_ws(' ', a."Operation", a."Reason", a."DetailsJson" ->> 'EntityName',
                        a."DetailsJson" ->> 'OperatorDisplayName')), {text})>0)
            ) history
            WHERE ({query.FromUtc == null} OR "AtUtc">={query.FromUtc ?? DateTimeOffset.MinValue})
                AND ({query.ToExclusiveUtc == null} OR "AtUtc"<{query.ToExclusiveUtc ?? DateTimeOffset.MaxValue})
                AND ({last == null} OR ("AtUtc","ItemId")<({(last == null ? DateTimeOffset.MaxValue : last.AtUtc)},{(last == null ? Guid.Empty : last.RuleId)}))
            """);
    }

    private sealed record PageCursor(Guid RuleId, Guid AgentId, string ResourceKey, DateTimeOffset AtUtc);

    private static string WritePageCursor(MonitoringQueryDto query, bool history, Guid ruleId, Guid agentId, string resourceKey, DateTimeOffset atUtc = default)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write("NRMC"u8); writer.Write((byte)1); writer.Write((byte)(history ? 2 : 1)); writer.Write(query.TenantId);
            writer.Write(QueryHash(query)); writer.Write(ruleId.ToByteArray());
            if (history) writer.Write(atUtc.UtcTicks);
            else
            {
                if (resourceKey.Length > MonitoringLimits.MaximumResourceKeyLength) throw new InvalidOperationException("invalid_cursor_resource");
                writer.Write(agentId.ToByteArray()); writer.Write((ushort)resourceKey.Length);
                foreach (var character in resourceKey) writer.Write((ushort)character);
            }
        }
        var token = Convert.ToBase64String(stream.ToArray()).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        if (token.Length > MonitoringQueryLimits.MaximumCursorLength) throw new InvalidOperationException("monitoring_cursor_size_exceeded");
        return token;
    }

    private static PageCursor? ReadPageCursor(string? token, MonitoringQueryDto query, bool history)
    {
        if (token is null) return null;
        if (token.Length == 0 || token.Length > MonitoringQueryLimits.MaximumCursorLength || token.Any(char.IsControl))
            throw new ArgumentException("invalid_monitoring_cursor");
        try
        {
            var encoded = token.Replace('-', '+').Replace('_', '/');
            var bytes = Convert.FromBase64String(encoded.PadRight((encoded.Length + 3) / 4 * 4, '='));
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (!reader.ReadBytes(4).AsSpan().SequenceEqual("NRMC"u8))
                throw new ArgumentException("monitoring_cursor_restart_required");
            if (reader.ReadByte() != 1) throw new ArgumentException("monitoring_cursor_restart_required");
            if (reader.ReadByte() != (history ? 2 : 1) || reader.ReadInt32() != query.TenantId ||
                !CryptographicOperations.FixedTimeEquals(reader.ReadBytes(32), QueryHash(query)))
                throw new ArgumentException("monitoring_cursor_context_mismatch");
            var rule = new Guid(reader.ReadBytes(16));
            PageCursor cursor;
            if (history) cursor = new(rule, Guid.Empty, "", new DateTimeOffset(reader.ReadInt64(), TimeSpan.Zero));
            else
            {
                var agent = new Guid(reader.ReadBytes(16));
                var length = reader.ReadUInt16();
                if (length is 0 or > MonitoringLimits.MaximumResourceKeyLength) throw new ArgumentException("invalid_monitoring_cursor");
                var characters = new char[length];
                for (var index = 0; index < length; index++) characters[index] = (char)reader.ReadUInt16();
                if (characters.Any(char.IsControl)) throw new ArgumentException("invalid_monitoring_cursor");
                cursor = new(rule, agent, new string(characters), default);
            }
            if (stream.Position != stream.Length) throw new ArgumentException("invalid_monitoring_cursor");
            return cursor;
        }
        catch (Exception error) when (error is FormatException or EndOfStreamException or ArgumentOutOfRangeException)
        { throw new ArgumentException("invalid_monitoring_cursor", error); }
    }

    private static byte[] QueryHash(MonitoringQueryDto query) => SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(query, JsonOptions));
}
