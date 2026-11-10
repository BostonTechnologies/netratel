using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Jobs;
using Npgsql;

namespace NetRatel.Infrastructure.Services;

public sealed class ExecutionGroupService(OrchestratorDbContext db, IEffectiveAccessService access,
    IJobNodeDirectory directory, IEnumerable<IExecutionGroupReferenceProvider> referenceProviders, TimeProvider clock) : IExecutionGroupService
{
    public Task<ExecutionGroupPageDto> ListAsync(int tenantId, string? search, int page, int pageSize,
        ClaimsPrincipal principal, CancellationToken cancellationToken)
        => ListCoreAsync(tenantId, search, page, pageSize, principal, null, cancellationToken);
    public Task<ExecutionGroupPageDto> ListForTargetsAsync(int tenantId, string? search, int page, int pageSize,
        IReadOnlySet<Guid> authorizedAgentIds, ClaimsPrincipal principal, CancellationToken cancellationToken)
        => ListCoreAsync(tenantId, search, page, pageSize, principal, authorizedAgentIds, cancellationToken);
    private async Task<ExecutionGroupPageDto> ListCoreAsync(int tenantId, string? search, int page, int pageSize,
        ClaimsPrincipal principal, IReadOnlySet<Guid>? authorizedAgentIds, CancellationToken cancellationToken)
    {
        await RequireReadAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        RequirePage(page, pageSize);
        await using var readTransaction = authorizedAgentIds is not null && db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken).ConfigureAwait(false) : null;
        if (search?.Length > 256) throw Invalid("execution_group_search_too_large", "Search is limited to 256 characters.");
        var query = db.ExecutionGroups.AsNoTracking().Where(x => x.TenantId == tenantId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = NormalizeName(search);
            query = query.Where(x => x.NormalizedName.Contains(term));
        }
        if (authorizedAgentIds is not null)
        {
            var permitted = authorizedAgentIds.ToArray();
            query = query.Where(group => !group.Members.Any(member => !permitted.Contains(member.AgentId)));
        }
        var total = await query.CountAsync(cancellationToken).ConfigureAwait(false);
        var records = await query.OrderBy(x => x.NormalizedName).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var groupIds = records.Select(x => x.Id).ToArray();
        var members = await db.ExecutionGroupMembers.AsNoTracking().Where(x => x.TenantId == tenantId && groupIds.Contains(x.GroupId))
            .Select(x => new { x.GroupId, x.AgentId }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        var memberIds = members.Select(x => x.AgentId).ToHashSet();
        var inventory = await directory.ReadAsync(tenantId, memberIds, true, cancellationToken).ConfigureAwait(false);
        var online = inventory.Where(x => x.IsOnline).Select(x => x.AgentId).ToHashSet();
        var result = new ExecutionGroupPageDto(records.Select(record => Map(record, members.Where(x => x.GroupId == record.Id).Select(x => x.AgentId), online)).ToArray(),
            page, pageSize, total, (long)page * pageSize < total ? page + 1 : null);
        if (readTransaction is not null) await readTransaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<ExecutionGroupDetailsDto?> GetAsync(int tenantId, Guid groupId, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireReadAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        var record = await db.ExecutionGroups.AsNoTracking().Include(x => x.Members)
            .SingleOrDefaultAsync(x => x.TenantId == tenantId && x.Id == groupId, cancellationToken).ConfigureAwait(false);
        return record is null ? null : await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionGroupDetailsDto> CreateAsync(int tenantId, ExecutionGroupWriteDto request,
        ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        ValidateWrite(request, creating: true);
        return await WriteAsync(async () =>
        {
            if (!await db.Tenants.AsNoTracking().AnyAsync(x => x.Id == tenantId, cancellationToken).ConfigureAwait(false))
                throw Invalid("execution_group_tenant_unavailable", "The tenant is unavailable.", 404);
            await ValidateMembersAsync(tenantId, request.AgentIds, cancellationToken).ConfigureAwait(false);
            var now = clock.GetUtcNow();
            var record = new ExecutionGroupRecord
            {
                Id = Guid.NewGuid(), TenantId = tenantId, Name = request.Name.Trim(), NormalizedName = NormalizeName(request.Name),
                Description = TrimDescription(request.Description), Enabled = request.Enabled, Archived = request.Archived,
                CreatedAtUtc = now, UpdatedAtUtc = now, CreatedBy = Actor(principal), UpdatedBy = Actor(principal),
                Members = request.AgentIds.Select(id => new ExecutionGroupMemberRecord { TenantId = tenantId, AgentId = id }).ToList()
            };
            db.ExecutionGroups.Add(record);
            Audit(record, "created", principal);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionGroupDetailsDto?> UpdateAsync(int tenantId, Guid groupId, ExecutionGroupWriteDto request,
        ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        ValidateWrite(request, creating: false);
        return await WriteAsync(async () =>
        {
            var record = await LockAsync(tenantId, groupId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            RequireRevision(record, request.ExpectedRevision!.Value);
            var retained = record.Members.Select(x => x.AgentId).ToHashSet();
            await ValidateMembersAsync(tenantId, request.AgentIds.Where(id => !retained.Contains(id)).ToArray(), cancellationToken).ConfigureAwait(false);
            var description = TrimDescription(request.Description);
            if (record.Name == request.Name.Trim() && record.Description == description && record.Enabled == request.Enabled &&
                record.Archived == request.Archived && record.Members.Select(x => x.AgentId).ToHashSet().SetEquals(request.AgentIds))
                return await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
            record.Name = request.Name.Trim(); record.NormalizedName = NormalizeName(request.Name); record.Description = description;
            record.Enabled = request.Enabled; record.Archived = request.Archived;
            ReplaceMembers(record, request.AgentIds);
            Advance(record, principal); Audit(record, "updated", principal);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ExecutionGroupDetailsDto?> ChangeMembershipAsync(int tenantId, Guid groupId,
        ExecutionGroupMembershipWriteDto request, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        ValidateIds(request.Add); ValidateIds(request.Remove);
        if (request.Add.Intersect(request.Remove).Any()) throw Invalid("execution_group_membership_conflict", "A member cannot be added and removed in the same change.");
        return await WriteAsync(async () =>
        {
            var record = await LockAsync(tenantId, groupId, cancellationToken).ConfigureAwait(false);
            if (record is null) return null;
            RequireRevision(record, request.ExpectedRevision);
            var current = record.Members.Select(x => x.AgentId).ToHashSet();
            if (request.Add.Any(current.Contains)) throw Invalid("execution_group_duplicate_member", "An added client is already a member.");
            if (request.Remove.Any(id => !current.Contains(id))) throw Invalid("execution_group_member_unavailable", "A removed client is not a member.");
            await ValidateMembersAsync(tenantId, request.Add, cancellationToken).ConfigureAwait(false);
            current.ExceptWith(request.Remove); current.UnionWith(request.Add);
            ValidateIds(current.ToArray());
            if (request.Add.Count == 0 && request.Remove.Count == 0) return await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
            ReplaceMembers(record, current);
            Advance(record, principal); Audit(record, "membership", principal);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return await DetailsAsync(record, cancellationToken).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> DeleteAsync(int tenantId, Guid groupId, long expectedRevision, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        await RequireManageAsync(tenantId, principal, cancellationToken).ConfigureAwait(false);
        return await WriteAsync(async () =>
        {
            var record = await LockAsync(tenantId, groupId, cancellationToken).ConfigureAwait(false);
            if (record is null) return false;
            RequireRevision(record, expectedRevision);
            var references = await FindJobReferencesAsync(tenantId, groupId, cancellationToken).ConfigureAwait(false);
            foreach (var provider in referenceProviders)
                references.AddRange(await provider.FindFlowReferencesAsync(tenantId, groupId, cancellationToken).ConfigureAwait(false));
            if (references.Count > 0)
            {
                var jobRead = await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, tenantId, cancellationToken).ConfigureAwait(false);
                var flowRead = await access.AuthorizeAsync(principal, NetRatelPermissions.FlowRead, tenantId, cancellationToken).ConfigureAwait(false);
                var visible = references.Where(reference => reference.Kind == "job" ? jobRead : flowRead).ToList();
                if (visible.Count != references.Count) visible.Add(new("restricted", string.Empty, "One or more retained definitions"));
                throw new ExecutionGroupReferencedException(visible);
            }
            Audit(record, "deleted", principal); db.ExecutionGroups.Remove(record);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<List<ExecutionGroupReferenceDto>> FindJobReferencesAsync(int tenantId, Guid groupId, CancellationToken ct)
    {
        // Parsed typed JSON avoids substring matches and supports the legacy null target column.
        var groupText = groupId.ToString("D");
        var jobs = await db.Jobs.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId && x.DeletedAtUtc == null &&
            x.TargetJson != null && x.TargetJson.Contains(groupText))
            .Select(x => new { x.Id, x.Name, x.TargetJson }).ToArrayAsync(ct).ConfigureAwait(false);
        return jobs.Where(x => ReferencesGroup(x.TargetJson, groupId))
            .Select(x => new ExecutionGroupReferenceDto("job", x.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), x.Name)).ToList();
    }
    private static bool ReferencesGroup(string? json, Guid groupId)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return (root.TryGetProperty("executionGroupId", out var id) || root.TryGetProperty("ExecutionGroupId", out id)) &&
                id.ValueKind == JsonValueKind.String && id.TryGetGuid(out var actual) && actual == groupId;
        }
        catch (JsonException) { throw Invalid("execution_group_reference_unavailable", "A retained target definition cannot be inspected safely.", 409); }
    }

    private async Task<ExecutionGroupRecord?> LockAsync(int tenantId, Guid groupId, CancellationToken ct)
    {
        var query = db.Database.IsRelational()
            ? db.ExecutionGroups.FromSqlInterpolated($"SELECT * FROM \"ExecutionGroups\" WHERE \"TenantId\" = {tenantId} AND \"Id\" = {groupId} FOR UPDATE")
            : db.ExecutionGroups.Where(x => x.TenantId == tenantId && x.Id == groupId);
        return await query.AsTracking().Include(x => x.Members).SingleOrDefaultAsync(ct).ConfigureAwait(false);
    }
    private async Task ValidateMembersAsync(int tenantId, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var requested = ids.ToArray();
        var found = await db.Agents.IgnoreQueryFilters().AsNoTracking().Where(x => x.TenantId == tenantId && requested.Contains(x.Id) &&
            x.IsEnabled && x.Status == AgentStatus.Active && x.RevokedAtUtc == null && x.DeletedAtUtc == null &&
            x.SupersededAtUtc == null && x.SupersededByAgentId == null).Select(x => x.Id).ToArrayAsync(ct).ConfigureAwait(false);
        if (found.Length != ids.Count) throw Invalid("execution_group_member_unavailable", "Every member must be an enabled, current client in this tenant.");
    }
    private void ReplaceMembers(ExecutionGroupRecord record, IEnumerable<Guid> ids)
    {
        var selected = ids.ToHashSet();
        var removed = record.Members.Where(x => !selected.Contains(x.AgentId)).ToArray();
        db.ExecutionGroupMembers.RemoveRange(removed);
        foreach (var member in removed) record.Members.Remove(member);
        var current = record.Members.Select(x => x.AgentId).ToHashSet();
        foreach (var id in selected.Where(id => !current.Contains(id)))
            record.Members.Add(new() { GroupId = record.Id, TenantId = record.TenantId, AgentId = id });
    }
    private async Task<ExecutionGroupDetailsDto> DetailsAsync(ExecutionGroupRecord record, CancellationToken ct)
    {
        var ids = record.Members.Select(x => x.AgentId).ToHashSet();
        var available = (await directory.ReadAsync(record.TenantId, ids, true, ct).ConfigureAwait(false)).ToDictionary(x => x.AgentId);
        // Membership is independent of metadata visibility. A metadata-only save must
        // preserve stored IDs even when their canonical directory row is unavailable.
        var nodes = ids.Select(id => available.GetValueOrDefault(id) ?? new JobNodeInventoryDto(
                id, string.Empty, "Unavailable client", null, [], null, null, null, false, false, "agent_metadata_unavailable"))
            .OrderBy(x => JobTargetNode.SortKey(x.Hostname, x.Name, x.DisplayName), StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.AgentId).ToArray();
        return new(Map(record, ids, nodes.Where(x => x.IsOnline).Select(x => x.AgentId).ToHashSet()), nodes);
    }
    private static ExecutionGroupDto Map(ExecutionGroupRecord record, IEnumerable<Guid> members, IReadOnlySet<Guid> online)
    {
        var ids = members.ToArray(); var onlineCount = ids.Count(online.Contains);
        return new(record.Id, record.TenantId, record.Name, record.Description, record.Revision, record.Enabled, record.Archived,
            ids.Length, onlineCount, ids.Length - onlineCount, record.CreatedAtUtc, record.UpdatedAtUtc);
    }
    private void Advance(ExecutionGroupRecord record, ClaimsPrincipal principal)
    { record.Revision++; record.UpdatedAtUtc = clock.GetUtcNow(); record.UpdatedBy = Actor(principal); }
    private void Audit(ExecutionGroupRecord record, string operation, ClaimsPrincipal principal) => db.ExecutionGroupAudits.Add(new()
    {
        Id = Guid.NewGuid(), GroupId = record.Id, TenantId = record.TenantId, Actor = Actor(principal), Operation = operation,
        Revision = record.Revision, MembershipJson = JsonSerializer.Serialize(record.Members.Select(x => x.AgentId).Order().ToArray()), CreatedAtUtc = clock.GetUtcNow()
    });
    private async Task<T> WriteAsync<T>(Func<Task<T>> operation, CancellationToken ct)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
            {
                await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
                    ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
                var result = await operation().ConfigureAwait(false);
                if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
                return result;
            }).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException) { throw Invalid("execution_group_revision_conflict", "The group changed. Reload its current revision before saving.", 409); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        { throw Invalid("execution_group_name_conflict", "A group with this normalized name already exists in the tenant.", 409); }
    }
    private async Task RequireReadAsync(int tenantId, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (tenantId <= 0 || !(await access.AuthorizeAsync(principal, NetRatelPermissions.TenantAdministration, tenantId, ct).ConfigureAwait(false) ||
            await access.AuthorizeAsync(principal, NetRatelPermissions.JobManagement, tenantId, ct).ConfigureAwait(false)))
            throw Invalid("execution_group_read_denied", "Tenant group read permission is required.", 403);
    }
    private async Task RequireManageAsync(int tenantId, ClaimsPrincipal principal, CancellationToken ct)
    {
        if (tenantId <= 0 || !await access.AuthorizeAsync(principal, NetRatelPermissions.TenantAdministration, tenantId, ct).ConfigureAwait(false))
            throw Invalid("execution_group_manage_denied", "Tenant administration permission is required to manage execution groups.", 403);
    }
    private static void ValidateWrite(ExecutionGroupWriteDto request, bool creating)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Trim().Length > 200 || NormalizeName(request.Name).Length > 200)
            throw Invalid("execution_group_name_invalid", "A group name must contain 1–200 normalized characters.");
        if (request.Description?.Length > 2048) throw Invalid("execution_group_description_invalid", "Description is limited to 2048 characters.");
        if (!creating && request.ExpectedRevision is not > 0) throw Invalid("execution_group_revision_required", "The current group revision is required.");
        if (creating && request.ExpectedRevision is not null) throw Invalid("execution_group_revision_invalid", "A new group has no expected revision.");
        ValidateIds(request.AgentIds);
    }
    private static void ValidateIds(IReadOnlyList<Guid>? ids)
    {
        if (ids is null || ids.Count > JobNodeFilterEvaluator.MaximumTargets || ids.Any(id => id == Guid.Empty))
            throw Invalid("execution_group_members_invalid", "Membership requires valid stable client IDs and is limited to 10000 clients.");
        if (ids.Distinct().Count() != ids.Count) throw Invalid("execution_group_duplicate_member", "Duplicate group members are not accepted.");
    }
    private static void RequireRevision(ExecutionGroupRecord record, long expected)
    { if (expected <= 0 || record.Revision != expected) throw Invalid("execution_group_revision_conflict", "The group changed. Reload its current revision before saving.", 409); }
    public static void RequirePage(int page, int pageSize)
    { if (page is < 1 or > 1_000_000 || pageSize is < 1 or > 100) throw Invalid("target_page_invalid", "Use a one-based page and a page size from 1 to 100."); }
    public static string NormalizeName(string name) => name.Trim().Normalize(NormalizationForm.FormKC).ToUpperInvariant();
    private static string? TrimDescription(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string? Actor(ClaimsPrincipal principal) => principal.FindFirst("netratel_principal_id")?.Value ??
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? principal.FindFirst("sub")?.Value ?? principal.Identity?.Name;
    private static JobTargetValidationException Invalid(string code, string message, int status = 400) => new(code, message, status);
}

public sealed class PublishedFlowExecutionGroupReferences(OrchestratorDbContext db) : IExecutionGroupReferenceProvider
{
    public async Task<IReadOnlyList<ExecutionGroupReferenceDto>> FindFlowReferencesAsync(int tenantId, Guid groupId, CancellationToken cancellationToken)
    {
        var match = JsonSerializer.Serialize(new { nodes = new[] { new { executionAction = new { jobTarget = new { source = 1, executionGroupId = groupId } } } } });
        var versions = await db.FlowVersions.AsNoTracking().Where(x => x.TenantId == tenantId && EF.Functions.JsonContains(x.GraphJson, match))
            .Select(x => new { x.Id, x.FlowId, x.VersionNumber }).ToArrayAsync(cancellationToken).ConfigureAwait(false);
        return versions.Select(x => new ExecutionGroupReferenceDto("flow-version", x.Id.ToString("D"), $"Flow {x.FlowId:D} version {x.VersionNumber}")).ToArray();
    }
}
