using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.API.Services;

/// <summary>Durable inventory authority. Every writer serializes on the enrolled agent's database row.</summary>
public sealed class SoftwareInventoryService(OrchestratorDbContext db, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();

    public async Task<SoftwareInventoryPolicy> PolicyAsync(int tenant, CancellationToken ct)
    {
        var row = await db.Set<SoftwareInventoryPolicyRecord>().AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant, ct);
        return row is null ? new() : new(row.Enabled, row.Interval, row.Unit);
    }

    public async Task SetPolicyAsync(int tenant, SoftwareInventoryPolicy policy, CancellationToken ct)
    {
        policy.Validate();
        await using var transaction = await LockTenantAsync(tenant, ct);
        var row = await db.Set<SoftwareInventoryPolicyRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant, ct);
        if (row is null) db.Add(row = new SoftwareInventoryPolicyRecord { TenantId = tenant });
        row.Enabled = policy.Enabled;
        row.Interval = policy.Interval;
        row.Unit = policy.Unit;
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
    }

    public async Task<SoftwareInventoryStatus> StatusAsync(int tenant, Guid agent, CancellationToken ct)
    {
        var state = await db.Set<SoftwareInventoryStateRecord>().AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent, ct);
        var scan = state?.LatestScanId is { } id
            ? await db.Set<SoftwareInventoryScanRecord>().AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == id, ct) : null;
        var policy = await PolicyAsync(tenant, ct);
        return new(tenant, agent, state?.LastSuccessfulScanUtc, Due(policy, state, scan), scan is null ? null : Map(scan), policy);
    }

    public async Task<SoftwareScanDto?> ScanAsync(int tenant, Guid agent, Guid collection, CancellationToken ct)
    {
        var row = await db.Set<SoftwareInventoryScanRecord>().AsNoTracking().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == collection, ct);
        return row is null ? null : Map(row);
    }

    public async Task<SoftwareInventoryStatus> RefreshAsync(int tenant, Guid agent, bool force, CancellationToken ct)
    {
        await using (var transaction = await LockAgentAsync(tenant, agent, ct))
        {
            var state = await StateAsync(tenant, agent, ct);
            var latest = await LatestAsync(state, ct);
            await ExpireAsync(state, latest, ct);
            if (latest?.Status is not ("Pending" or "Collecting") &&
                (force || state.LastSuccessfulScanUtc is null || state.LastSuccessfulScanUtc <= Now.AddMinutes(-30)))
                CreateScan(state);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
        }
        return await StatusAsync(tenant, agent, ct);
    }

    public async Task<SoftwareScanWork> WorkAsync(int tenant, Guid agent, CancellationToken ct)
    {
        await using var transaction = await LockAgentAsync(tenant, agent, ct);
        var state = await StateAsync(tenant, agent, ct);
        var policy = await PolicyAsync(tenant, ct);
        var latest = await LatestAsync(state, ct);
        await ExpireAsync(state, latest, ct);
        var due = Due(policy, state, latest);
        if (latest?.Status is not ("Pending" or "Collecting") && policy.Enabled && (due is null || due <= Now))
            latest = CreateScan(state);
        if (latest is { Status: "Pending" })
        {
            latest.Status = "Collecting";
            latest.StartedAtUtc = Now;
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(policy, latest?.Status == "Collecting" ? latest.CollectionId : null, due);
    }

    public async Task<SoftwareChunkAcknowledgement> UploadAsync(int tenant, Guid agent, SoftwareChunkUpload upload, CancellationToken ct)
    {
        if (upload.CollectionId == Guid.Empty || upload.Count is < 1 or > SoftwareInventoryWire.MaximumChunks || upload.Index < 0 || upload.Index >= upload.Count ||
            upload.Os is not ("windows" or "linux" or "unsupported") || upload.Errors is null || upload.Errors.Count > 32 ||
            upload.Errors.Any(error => string.IsNullOrWhiteSpace(error) || error.Length > 128 || error.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('_' or '-' or '.'))) ||
            (upload.Complete && (upload.Errors.Count > 0 || upload.Os == "unsupported")))
            throw new ArgumentException("Invalid collection metadata.");
        var packages = SoftwareInventoryWire.Decode(upload.Payload);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(upload, SoftwareInventoryWire.JsonOptions))));
        await using var transaction = await LockAgentAsync(tenant, agent, ct);
        var state = await StateAsync(tenant, agent, ct);
        var scan = await db.Set<SoftwareInventoryScanRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == upload.CollectionId, ct);
        if (scan is null) return new(upload.CollectionId, upload.Index, false, "UnknownCollection");
        var duplicate = await db.Set<SoftwareInventoryChunkRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == upload.CollectionId && row.Index == upload.Index, ct);
        if (duplicate is not null)
            return new(upload.CollectionId, upload.Index, duplicate.Hash == hash, duplicate.Hash == hash ? scan.Status : "ConflictingChunk");
        if (state.LatestScanId != upload.CollectionId || scan.Status is not ("Pending" or "Collecting"))
            return new(upload.CollectionId, upload.Index, false, "StaleCollection");
        if (scan.TotalChunks != 0 && (scan.TotalChunks != upload.Count || scan.Complete != upload.Complete ||
            scan.Os != upload.Os || scan.DetectedAtUtc != upload.DetectedAtUtc || scan.ErrorsJson != JsonSerializer.Serialize(upload.Errors)))
            throw new ArgumentException("Collection metadata changed between chunks.");
        scan.TotalChunks = upload.Count;
        scan.Complete = upload.Complete;
        scan.Os = upload.Os;
        scan.DetectedAtUtc = upload.DetectedAtUtc;
        scan.ErrorsJson = JsonSerializer.Serialize(upload.Errors);
        scan.Status = "Collecting";
        scan.StartedAtUtc ??= Now;
        var chunk = new SoftwareInventoryChunkRecord { TenantId = tenant, AgentId = agent, CollectionId = upload.CollectionId,
            Index = upload.Index, Hash = hash, PackagesJson = JsonSerializer.Serialize(packages) };
        db.Add(chunk);
        scan.ReceivedChunks++;
        if (scan.ReceivedChunks == scan.TotalChunks)
        {
            var chunks = await db.Set<SoftwareInventoryChunkRecord>().Where(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == scan.CollectionId).ToListAsync(ct);
            chunks.Add(chunk);
            if (scan.Complete)
            {
                var all = chunks.OrderBy(row => row.Index).SelectMany(row => JsonSerializer.Deserialize<SoftwarePackage[]>(row.PackagesJson)!)
                    .GroupBy(package => package.Identity).ToDictionary(group => group.Key, group => group.OrderBy(package => package.Version, StringComparer.Ordinal).Last());
                await ReconcileAsync(state, scan, all, ct);
                scan.Status = "Complete";
                state.LastSuccessfulScanUtc = Now;
                state.NextDueUtc = (await PolicyAsync(tenant, ct)).NextDue(Now, agent);
            }
            else
            {
                scan.Status = packages.Length == 0 && scan.TotalChunks == 1 ? "Error" : "Partial";
                state.NextDueUtc = Now.AddHours(1);
            }
            scan.CompletedAtUtc = Now;
            // Keep hashes for acknowledgement replay, discard staged payload after reconciliation.
            foreach (var item in chunks) item.PackagesJson = "[]";
        }
        await db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return new(upload.CollectionId, upload.Index, true, scan.Status);
    }

    private async Task ReconcileAsync(SoftwareInventoryStateRecord state, SoftwareInventoryScanRecord scan,
        Dictionary<string, SoftwarePackage> packages, CancellationToken ct)
    {
        var previous = await db.Set<SoftwareInstallationRecord>().Where(row => row.TenantId == state.TenantId && row.AgentId == state.AgentId).ToDictionaryAsync(row => row.Identity, ct);
        foreach (var (identity, package) in packages)
        {
            if (!previous.Remove(identity, out var row))
            {
                row = new() { TenantId = state.TenantId, AgentId = state.AgentId, Identity = identity };
                db.Add(row);
                Change("Added", package.Name, null, package.Version);
            }
            else if (row.Version != package.Version) Change("Upgraded", package.Name, row.Version, package.Version);
            row.Name = package.Name; row.Version = package.Version; row.Publisher = package.Publisher;
            row.Architecture = package.Architecture; row.PackageId = package.PackageId; row.Source = package.Source;
            row.Scope = package.Scope; row.InstallLocation = package.InstallLocation; row.InstallDate = package.InstallDate;
            row.Os = scan.Os; row.DetectedAtUtc = scan.DetectedAtUtc!.Value;
        }
        foreach (var removed in previous.Values)
        {
            Change("Removed", removed.Name, removed.Version, null);
            db.Remove(removed);
        }
        void Change(string kind, string name, string? oldVersion, string? version) => db.Add(new SoftwareInventoryChangeRecord
        {
            TenantId = state.TenantId, AgentId = state.AgentId, CollectionId = scan.CollectionId,
            Kind = kind, Name = name, PreviousVersion = oldVersion, Version = version, DetectedAtUtc = Now
        });
    }

    private IQueryable<InstallationRow> Query(int tenant, SoftwareInventoryQuery query)
    {
        query.Validate();
        var rows = from item in db.Set<SoftwareInstallationRecord>().AsNoTracking()
                   join agent in db.Agents.AsNoTracking() on new { item.TenantId, Id = item.AgentId } equals new { agent.TenantId, agent.Id }
                   join state in db.Set<SoftwareInventoryStateRecord>().AsNoTracking() on new { item.TenantId, item.AgentId } equals new { state.TenantId, state.AgentId }
                   join scan in db.Set<SoftwareInventoryScanRecord>().AsNoTracking() on new { state.TenantId, state.AgentId, Id = state.LatestScanId } equals new { scan.TenantId, scan.AgentId, Id = (Guid?)scan.CollectionId }
                   where item.TenantId == tenant
                   select new InstallationRow { Item = item, Client = agent.FriendlyName ?? agent.Name ?? agent.Id.ToString(), LastSuccessful = state.LastSuccessfulScanUtc!.Value, Status = scan.Status };
        if (query.AgentId is { } id) rows = rows.Where(row => row.Item.AgentId == id);
        if (!string.IsNullOrWhiteSpace(query.Search)) { var text = query.Search.ToLowerInvariant(); rows = rows.Where(row => row.Item.Name.ToLower().Contains(text) || row.Item.Publisher.ToLower().Contains(text) || row.Item.PackageId.ToLower().Contains(text)); }
        if (!string.IsNullOrWhiteSpace(query.Name)) { var text = query.Name.ToLowerInvariant(); rows = rows.Where(row => row.Item.Name.ToLower().Contains(text)); }
        if (!string.IsNullOrWhiteSpace(query.Publisher)) { var text = query.Publisher.ToLowerInvariant(); rows = rows.Where(row => row.Item.Publisher.ToLower().Contains(text)); }
        if (query.Version is { Length: > 0 }) rows = rows.Where(row => row.Item.Version == query.Version);
        if (query.Source is { Length: > 0 }) rows = rows.Where(row => row.Item.Source == query.Source);
        if (query.Os is { Length: > 0 }) rows = rows.Where(row => row.Item.Os == query.Os);
        if (query.Status == "fresh") { var cutoff = Now.AddMinutes(-30); rows = rows.Where(row => row.LastSuccessful > cutoff); }
        else if (query.Status == "stale") { var cutoff = Now.AddMinutes(-30); rows = rows.Where(row => row.LastSuccessful <= cutoff); }
        else if (query.Status is not null) rows = rows.Where(row => row.Status == query.Status);
        return rows;
    }

    public async Task<SoftwarePage<SoftwareInstallationDto>> InstallationsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct)
    {
        var rows = Query(tenant, query);
        var total = await rows.CountAsync(ct);
        var devices = await rows.Select(row => row.Item.AgentId).Distinct().CountAsync(ct);
        var sorted = query.Sort switch
        {
            "version" => query.Descending ? rows.OrderByDescending(row => row.Item.Version) : rows.OrderBy(row => row.Item.Version),
            "publisher" => query.Descending ? rows.OrderByDescending(row => row.Item.Publisher) : rows.OrderBy(row => row.Item.Publisher),
            "source" => query.Descending ? rows.OrderByDescending(row => row.Item.Source) : rows.OrderBy(row => row.Item.Source),
            "client" => query.Descending ? rows.OrderByDescending(row => row.Client) : rows.OrderBy(row => row.Client),
            "detected" => query.Descending ? rows.OrderByDescending(row => row.Item.DetectedAtUtc) : rows.OrderBy(row => row.Item.DetectedAtUtc),
            _ => query.Descending ? rows.OrderByDescending(row => row.Item.Name) : rows.OrderBy(row => row.Item.Name)
        };
        var page = await sorted.ThenBy(row => row.Item.AgentId).ThenBy(row => row.Item.Identity).Skip(query.Page * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new(page.Select(row => new SoftwareInstallationDto(row.Item.AgentId, row.Client, row.Item.Identity,
            new(row.Item.Name, row.Item.Version, row.Item.Publisher, row.Item.Architecture, row.Item.PackageId, row.Item.Source, row.Item.InstallLocation, row.Item.InstallDate, row.Item.Scope),
            row.Item.Os, row.Item.DetectedAtUtc, row.LastSuccessful)).ToArray(), total, query.Page, query.PageSize, devices);
    }

    public async IAsyncEnumerable<SoftwareInstallationDto> ExportAsync(int tenant, SoftwareInventoryQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in Query(tenant, query).OrderBy(row => row.Item.AgentId).ThenBy(row => row.Item.Identity)
            .AsAsyncEnumerable().WithCancellation(ct))
            yield return new(row.Item.AgentId, row.Client, row.Item.Identity,
                new(row.Item.Name, row.Item.Version, row.Item.Publisher, row.Item.Architecture, row.Item.PackageId, row.Item.Source,
                    row.Item.InstallLocation, row.Item.InstallDate, row.Item.Scope), row.Item.Os, row.Item.DetectedAtUtc, row.LastSuccessful);
    }

    public async Task<SoftwarePage<SoftwarePrevalenceDto>> AggregateAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct)
    {
        var rows = Query(tenant, query);
        var groups = rows.GroupBy(row => new { row.Item.Name, row.Item.Version, row.Item.Publisher, row.Item.Source })
            .Select(group => new { group.Key.Name, group.Key.Version, group.Key.Publisher, group.Key.Source,
                Devices = group.Select(row => row.Item.AgentId).Distinct().Count(), Installations = group.Count() });
        var total = await groups.CountAsync(ct);
        var devices = await rows.Select(row => row.Item.AgentId).Distinct().CountAsync(ct);
        var page = await groups.OrderByDescending(row => row.Devices).ThenBy(row => row.Name).ThenBy(row => row.Version).ThenBy(row => row.Publisher).ThenBy(row => row.Source)
            .Skip(query.Page * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new(page.Select(row => new SoftwarePrevalenceDto(row.Name, row.Version, row.Publisher, row.Source, row.Devices, row.Installations)).ToArray(),
            total, query.Page, query.PageSize, devices);
    }

    public async Task<SoftwarePage<SoftwareChangeDto>> ChangesAsync(int tenant, Guid? agent, int page, int size, CancellationToken ct)
    {
        new SoftwareInventoryQuery(page, size).Validate();
        var rows = db.Set<SoftwareInventoryChangeRecord>().AsNoTracking().Where(row => row.TenantId == tenant && (!agent.HasValue || row.AgentId == agent));
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(row => row.Id).Skip(page * size).Take(size)
            .Select(row => new SoftwareChangeDto(row.CollectionId, row.AgentId, row.Kind, row.Name, row.PreviousVersion, row.Version, row.DetectedAtUtc)).ToListAsync(ct);
        return new(items, total, page, size);
    }

    private async Task<SoftwareInventoryStateRecord> StateAsync(int tenant, Guid agent, CancellationToken ct)
    {
        var state = await db.Set<SoftwareInventoryStateRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent, ct);
        if (state is null) db.Add(state = new() { TenantId = tenant, AgentId = agent });
        return state;
    }

    private Task<SoftwareInventoryScanRecord?> LatestAsync(SoftwareInventoryStateRecord state, CancellationToken ct) =>
        db.Set<SoftwareInventoryScanRecord>().SingleOrDefaultAsync(row => row.TenantId == state.TenantId && row.AgentId == state.AgentId && row.CollectionId == state.LatestScanId, ct);

    private SoftwareInventoryScanRecord CreateScan(SoftwareInventoryStateRecord state)
    {
        var scan = new SoftwareInventoryScanRecord { TenantId = state.TenantId, AgentId = state.AgentId, CollectionId = Guid.NewGuid(), RequestedAtUtc = Now };
        state.LatestScanId = scan.CollectionId;
        db.Add(scan);
        return scan;
    }

    private async Task ExpireAsync(SoftwareInventoryStateRecord state, SoftwareInventoryScanRecord? scan, CancellationToken ct)
    {
        if (scan is { Status: "Collecting", StartedAtUtc: { } started } && started < Now.AddHours(-24))
        {
            scan.Status = "Error";
            scan.CompletedAtUtc = Now;
            scan.ErrorsJson = "[\"scan_expired\"]";
            state.NextDueUtc = Now.AddHours(1);
            var staged = db.Set<SoftwareInventoryChunkRecord>().Where(row => row.TenantId == state.TenantId && row.AgentId == state.AgentId && row.CollectionId == scan.CollectionId);
            if (db.Database.IsNpgsql()) await staged.ExecuteUpdateAsync(update => update.SetProperty(row => row.PackagesJson, "[]"), ct);
            else foreach (var chunk in await staged.ToListAsync(ct)) chunk.PackagesJson = "[]";
        }
    }

    private static DateTimeOffset? Due(SoftwareInventoryPolicy policy, SoftwareInventoryStateRecord? state, SoftwareInventoryScanRecord? scan) =>
        !policy.Enabled ? null : scan?.Status == "Complete" && state?.LastSuccessfulScanUtc is { } completed
            ? policy.NextDue(completed, state.AgentId) : state?.NextDueUtc;

    private async Task<IDbContextTransaction?> LockAgentAsync(int tenant, Guid agent, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return null;
        var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Agents\" WHERE \"TenantId\"={tenant} AND \"Id\"={agent} FOR UPDATE", ct);
        return transaction;
    }

    private async Task<IDbContextTransaction?> LockTenantAsync(int tenant, CancellationToken ct)
    {
        if (!db.Database.IsNpgsql()) return null;
        var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Tenants\" WHERE \"Id\"={tenant} FOR UPDATE", ct);
        return transaction;
    }

    private static SoftwareScanDto Map(SoftwareInventoryScanRecord scan) => new(scan.CollectionId, scan.Status, scan.RequestedAtUtc,
        scan.CompletedAtUtc, scan.ReceivedChunks, scan.TotalChunks, JsonSerializer.Deserialize<string[]>(scan.ErrorsJson) ?? []);

    private sealed class InstallationRow
    {
        public SoftwareInstallationRecord Item { get; set; } = default!;
        public string Client { get; set; } = "";
        public DateTimeOffset LastSuccessful { get; set; }
        public string Status { get; set; } = "";
    }
}
