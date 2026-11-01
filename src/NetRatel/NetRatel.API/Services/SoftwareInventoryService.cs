using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NetRatel.Infrastructure.Clients;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.SoftwareInventory;

namespace NetRatel.API.Services;

/// <summary>Durable inventory authority. Every writer serializes on the enrolled agent's database row.</summary>
public sealed class SoftwareInventoryService(OrchestratorDbContext db, TimeProvider clock)
{
    private DateTimeOffset Now => clock.GetUtcNow();
    private static readonly TimeSpan ScanDeadline = TimeSpan.FromMinutes(5);

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
        // Hash the unmodified wire message: old durable outboxes must still receive identical replay acknowledgements.
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(upload, SoftwareInventoryWire.JsonOptions))));
        await using var transaction = await LockAgentAsync(tenant, agent, ct);
        var state = await StateAsync(tenant, agent, ct);
        var scan = await db.Set<SoftwareInventoryScanRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == upload.CollectionId, ct);
        if (scan is null) return new(upload.CollectionId, upload.Index, false, "UnknownCollection");
        if (Expired(scan))
        {
            await ExpireAsync(state, scan, ct);
            await db.SaveChangesAsync(ct);
            if (transaction is not null) await transaction.CommitAsync(ct);
            return new(upload.CollectionId, upload.Index, false, "StaleCollection");
        }
        var duplicate = await db.Set<SoftwareInventoryChunkRecord>().SingleOrDefaultAsync(row => row.TenantId == tenant && row.AgentId == agent && row.CollectionId == upload.CollectionId && row.Index == upload.Index, ct);
        if (duplicate is not null)
            return new(upload.CollectionId, upload.Index, duplicate.Hash == hash, duplicate.Hash == hash ? scan.Status : "ConflictingChunk");
        if (state.LatestScanId != upload.CollectionId || scan.Status is not ("Pending" or "Collecting"))
            return new(upload.CollectionId, upload.Index, false, "StaleCollection");
        // PostgreSQL timestamps retain microseconds, not .NET's 100ns ticks. Comparing the original wire value
        // to a fresh DbContext's persisted value rejected chunk 2 onward. Normalize both sides, not the hash.
        var detected = DatabasePrecision(upload.DetectedAtUtc);
        if (scan.TotalChunks != 0 && (scan.TotalChunks != upload.Count || scan.Complete != upload.Complete ||
            scan.Os != upload.Os || scan.DetectedAtUtc is not { } previousDetected || DatabasePrecision(previousDetected) != detected ||
            scan.ErrorsJson != JsonSerializer.Serialize(upload.Errors)))
            throw new ArgumentException("Collection metadata changed between chunks.");
        scan.TotalChunks = upload.Count;
        scan.Complete = upload.Complete;
        scan.Os = upload.Os;
        scan.DetectedAtUtc = detected;
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

    private static DateTimeOffset DatabasePrecision(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % 10, TimeSpan.Zero);

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
                   where item.TenantId == tenant && agent.DeletedAtUtc == null
                   select new InstallationRow { Item = item, Agent = agent, Client = agent.FriendlyName ?? agent.Name ?? "", LastSuccessful = state.LastSuccessfulScanUtc!.Value, Status = scan.Status };
        if (query.AgentId is { } id) rows = rows.Where(row => row.Item.AgentId == id);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim().ToLowerInvariant();
            rows = rows.Where(row => row.Item.Name.ToLower().Contains(text) || row.Item.Version.ToLower().Contains(text) ||
                row.Item.Publisher.ToLower().Contains(text) || row.Item.PackageId.ToLower().Contains(text) ||
                (row.Agent.FriendlyName != null && row.Agent.FriendlyName.ToLower().Contains(text)) ||
                (row.Agent.Name != null && row.Agent.Name.ToLower().Contains(text)) ||
                (row.Agent.DeviceInfoJson != null && row.Agent.DeviceInfoJson.ToLower().Contains(text)));
        }
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
        return new(page.Select(Installation).ToArray(), total, query.Page, query.PageSize, devices);
    }

    private static SoftwareInstallationDto Installation(InstallationRow row) => new(row.Item.AgentId, ClientLabel(row.Agent), row.Item.Identity,
        new(row.Item.Name, row.Item.Version, row.Item.Publisher, row.Item.Architecture, row.Item.PackageId, row.Item.Source,
            row.Item.InstallLocation, row.Item.InstallDate, row.Item.Scope), row.Item.Os, row.Item.DetectedAtUtc, row.LastSuccessful);

    public async IAsyncEnumerable<SoftwareInstallationDto> ExportAsync(int tenant, SoftwareInventoryQuery query,
        [EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var row in Query(tenant, query).OrderBy(row => row.Item.AgentId).ThenBy(row => row.Item.Identity)
            .AsAsyncEnumerable().WithCancellation(ct))
            yield return Installation(row);
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
        var rows = from change in db.Set<SoftwareInventoryChangeRecord>().AsNoTracking()
                   join client in db.Agents.AsNoTracking() on new { change.TenantId, Id = change.AgentId } equals new { client.TenantId, client.Id }
                   where change.TenantId == tenant && (!agent.HasValue || change.AgentId == agent) && client.DeletedAtUtc == null
                   select new { Change = change, Agent = client };
        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(row => row.Change.Id).Skip(page * size).Take(size).ToListAsync(ct);
        return new(items.Select(row => new SoftwareChangeDto(row.Change.CollectionId, row.Change.AgentId, row.Change.Kind,
            row.Change.Name, row.Change.PreviousVersion, row.Change.Version, row.Change.DetectedAtUtc, ClientLabel(row.Agent))).ToArray(), total, page, size);
    }

    private IQueryable<ClientRow> ClientRows(int tenant) =>
        from agent in db.Agents.AsNoTracking()
        join state in db.Set<SoftwareInventoryStateRecord>().AsNoTracking()
            on new { agent.TenantId, AgentId = agent.Id } equals new { state.TenantId, state.AgentId } into states
        from state in states.DefaultIfEmpty()
        join scan in db.Set<SoftwareInventoryScanRecord>().AsNoTracking()
            on new { agent.TenantId, AgentId = agent.Id, Id = state == null ? (Guid?)null : state.LatestScanId }
            equals new { scan.TenantId, scan.AgentId, Id = (Guid?)scan.CollectionId } into scans
        from scan in scans.DefaultIfEmpty()
        where agent.TenantId == tenant && agent.DeletedAtUtc == null
        select new ClientRow { Agent = agent, State = state, Scan = scan,
            InstalledEntries = db.Set<SoftwareInstallationRecord>().Count(item => item.TenantId == tenant && item.AgentId == agent.Id) };

    public async Task<SoftwareInventorySummary> SummaryAsync(int tenant, CancellationToken ct)
    {
        var rows = ClientRows(tenant);
        var freshCutoff = Now.AddMinutes(-30);
        var expiredCutoff = Now - ScanDeadline;
        var clients = await rows.CountAsync(ct);
        var inventoried = await rows.CountAsync(row => row.State != null && row.State.LastSuccessfulScanUtc != null, ct);
        var stale = await rows.CountAsync(row => row.State != null && row.State.LastSuccessfulScanUtc <= freshCutoff, ct);
        var active = await rows.CountAsync(row => row.Scan != null && (row.Scan.Status == "Pending" ||
            (row.Scan.Status == "Collecting" && row.Scan.StartedAtUtc >= expiredCutoff)), ct);
        var failed = await rows.CountAsync(row => row.Scan != null && (row.Scan.Status == "Error" || row.Scan.Status == "Partial" ||
            (row.Scan.Status == "Collecting" && row.Scan.StartedAtUtc < expiredCutoff)), ct);
        var latest = await rows.Select(row => row.State == null ? null : row.State.LastSuccessfulScanUtc).MaxAsync(ct);
        var installations = Query(tenant, new());
        return new(clients, inventoried, clients - inventoried, stale, active, failed,
            await installations.CountAsync(ct),
            await installations.Select(row => new { row.Item.Name, row.Item.Publisher, row.Item.Source }).Distinct().CountAsync(ct), latest);
    }

    public async Task<SoftwarePage<SoftwareInventoryClientDto>> ClientsAsync(int tenant, SoftwareInventoryQuery query, CancellationToken ct)
    {
        query.Validate();
        var rows = ClientRows(tenant);
        if (query.AgentId is { } id) rows = rows.Where(row => row.Agent.Id == id);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var text = query.Search.Trim().ToLowerInvariant();
            rows = rows.Where(row => (row.Agent.FriendlyName != null && row.Agent.FriendlyName.ToLower().Contains(text)) ||
                (row.Agent.Name != null && row.Agent.Name.ToLower().Contains(text)) ||
                (row.Agent.DeviceInfoJson != null && row.Agent.DeviceInfoJson.ToLower().Contains(text)));
        }
        var total = await rows.CountAsync(ct);
        var page = await rows.OrderBy(row => row.Agent.FriendlyName ?? row.Agent.Name).ThenBy(row => row.Agent.Id)
            .Skip(query.Page * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        return new(page.Select(row =>
        {
            var identity = ClientDirectoryIdentity.Parse(row.Agent.DeviceInfoJson);
            return new SoftwareInventoryClientDto(row.Agent.Id, ClientLabel(row.Agent), identity.HostName, identity.OperatingSystem,
                row.State?.LastSuccessfulScanUtc, row.Scan is null ? "NeverScanned" : Map(row.Scan).Status,
                row.InstalledEntries, row.Scan?.ReceivedChunks ?? 0, row.Scan?.TotalChunks ?? 0);
        }).ToArray(), total, query.Page, query.PageSize, total);
    }

    private static string ClientLabel(Agent agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.FriendlyName)) return agent.FriendlyName.Trim();
        var host = ClientDirectoryIdentity.Parse(agent.DeviceInfoJson).HostName;
        if (!string.IsNullOrWhiteSpace(host)) return host;
        return !string.IsNullOrWhiteSpace(agent.Name) && !Guid.TryParse(agent.Name, out _)
            ? agent.Name : "Client-" + agent.Id.ToString("N")[..8];
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

    private bool Expired(SoftwareInventoryScanRecord scan) =>
        scan is { Status: "Collecting", StartedAtUtc: { } started } && started < Now - ScanDeadline;

    private async Task ExpireAsync(SoftwareInventoryStateRecord state, SoftwareInventoryScanRecord? scan, CancellationToken ct)
    {
        if (scan is not null && Expired(scan))
        {
            scan.Status = "Error";
            scan.CompletedAtUtc = Now;
            scan.ErrorsJson = "[\"scan_expired\"]";
            if (state.LatestScanId == scan.CollectionId) state.NextDueUtc = Now.AddHours(1);
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

    // Reading status must not enqueue or mutate a scan. Surface an elapsed deadline immediately; the next
    // agent write or explicit refresh persists the terminal state under the normal agent lock.
    private SoftwareScanDto Map(SoftwareInventoryScanRecord scan) => new(scan.CollectionId, Expired(scan) ? "Error" : scan.Status,
        scan.RequestedAtUtc, scan.CompletedAtUtc, scan.ReceivedChunks, scan.TotalChunks,
        Expired(scan) ? ["scan_expired"] : JsonSerializer.Deserialize<string[]>(scan.ErrorsJson) ?? [], scan.StartedAtUtc);

    private sealed class InstallationRow
    {
        public SoftwareInstallationRecord Item { get; set; } = default!;
        public Agent Agent { get; set; } = default!;
        public string Client { get; set; } = "";
        public DateTimeOffset LastSuccessful { get; set; }
        public string Status { get; set; } = "";
    }

    private sealed class ClientRow
    {
        public Agent Agent { get; set; } = default!;
        public SoftwareInventoryStateRecord? State { get; set; }
        public SoftwareInventoryScanRecord? Scan { get; set; }
        public int InstalledEntries { get; set; }
    }
}
