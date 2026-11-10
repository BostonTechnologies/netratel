using System.Globalization;
using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared;

namespace NetRatel.Infrastructure.Flows;

/// <summary>Bounded canonical discovery shares publication pin validation and never exposes executable source.</summary>
public sealed class FlowExecutionDefinitionCatalog(OrchestratorDbContext db, IFlowExecutionAuthorityVerifier verifier,
    IEffectiveAccessService access, IFlowExecutionDefinitionPins pins) : IFlowExecutionDefinitionCatalog
{
    public async Task<FlowExecutionDefinitionsPageDto> ListAsync(int tenantId, FlowExecutionAuthorityDto authority,
        FlowExecutionSourceKind sourceKind, string? search = null, string? afterId = null, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await RequireAsync(tenantId, authority, sourceKind, cancellationToken);
        if (limit is < 1 or > 50 || search?.Length > 256 || afterId is not null && !Parse(afterId, out _))
            throw new ArgumentException("The definition search or cursor exceeds its bound.");
        var after = afterId is null ? 0 : long.Parse(afterId, CultureInfo.InvariantCulture);
        var term = search?.Trim().ToLowerInvariant();
        var rows = sourceKind == FlowExecutionSourceKind.Job
            ? await db.Jobs.AsNoTracking().Where(job => job.TenantId == tenantId && job.Id > after &&
                (term == null || term == "" || job.Name.ToLower().Contains(term)))
                .OrderBy(job => job.Id).Take(limit + 1).Select(job => new Row(job.Id, job.Name, job.AgentId, null)).ToArrayAsync(cancellationToken)
            : await db.Scripts.AsNoTracking().Where(script => script.Id > after &&
                (term == null || term == "" || script.Name.ToLower().Contains(term)))
                .OrderBy(script => script.Id).Take(limit + 1)
                .Select(script => new Row(script.Id, script.Name, null, script.SourceRevision)).ToArrayAsync(cancellationToken);
        var ids = rows.Take(limit).Where(row => row.AgentId != null).Select(row => row.AgentId!.Value).Distinct().ToArray();
        var targets = await db.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && ids.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Name, agent.FriendlyName }).ToDictionaryAsync(agent => agent.Id, cancellationToken);
        return new(rows.Take(limit).Select(row => new FlowExecutionDefinitionOptionDto(sourceKind,
            new(row.Id.ToString(CultureInfo.InvariantCulture), row.Name, row.Revision?.ToString(CultureInfo.InvariantCulture)),
            row.AgentId is { } id ? targets.TryGetValue(id, out var agent)
                ? new(id, agent.FriendlyName ?? agent.Name, agent.Name) : new(id, "Unavailable target") : null)).ToArray(),
            rows.Length > limit ? rows[limit - 1].Id.ToString(CultureInfo.InvariantCulture) : null);
    }

    public async Task<FlowExecutionActionSettingsDto?> GetAsync(int tenantId, FlowExecutionAuthorityDto authority,
        FlowExecutionSourceKind sourceKind, string definitionId, CancellationToken cancellationToken = default)
    {
        await RequireAsync(tenantId, authority, sourceKind, cancellationToken);
        if (!Parse(definitionId, out var id)) throw new ArgumentException("A positive decimal definition identifier is required.");
        Guid? agentId = null;
        if (sourceKind == FlowExecutionSourceKind.Job)
        {
            var row = await db.Jobs.AsNoTracking().Where(job => job.Id == id && job.TenantId == tenantId)
                .Select(job => new { job.AgentId, job.TargetJson, job.ExecutionPolicyJson }).SingleOrDefaultAsync(cancellationToken);
            if (row is null) return null;
            agentId = row.TargetJson is null && row.ExecutionPolicyJson is null ? row.AgentId : null;
        }
        else if (!await db.Scripts.AsNoTracking().AnyAsync(script => script.Id == id, cancellationToken)) return null;
        var node = new FlowNodeDto(Guid.NewGuid(), sourceKind == FlowExecutionSourceKind.Job ? FlowNodeKind.RunJob :
            FlowNodeKind.RunLibraryScript, "Selected execution", new(0, 0))
        { ExecutionAction = new(sourceKind, new(definitionId)) { Target = agentId is { } target ? new(target) : null } };
        var capture = await pins.CaptureAsync(tenantId, authority, Guid.NewGuid(), new(2, [node], [], new()), cancellationToken);
        // Only actual publication may supply the permanent protected version reference.
        return capture.Graph.Nodes[0].ExecutionAction! with { PinnedSnapshotReference = null };
    }


    public async Task<FlowExecutionTargetsPageDto> ListTargetsAsync(int tenantId, FlowExecutionAuthorityDto authority,
        FlowExecutionSourceKind sourceKind, string? search = null, string? afterId = null, int limit = 50,
        CancellationToken cancellationToken = default)
    {
        await RequireAsync(tenantId, authority, sourceKind, cancellationToken);
        if (limit is < 1 or > 50 || search?.Length > 256 || afterId is not null && !Guid.TryParse(afterId, out _))
            throw new ArgumentException("The target search or cursor exceeds its bound.");
        var term = search?.Trim().ToLowerInvariant();
        var query = afterId is null ? db.Agents.AsQueryable() :
            db.Agents.FromSqlInterpolated($"SELECT * FROM \"Agents\" WHERE \"TenantId\" = {tenantId} AND \"Id\" > {Guid.Parse(afterId)}");
        var rows = await query.AsNoTracking().Where(agent => agent.TenantId == tenantId &&
                agent.IsEnabled && agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null &&
                agent.RevokedAtUtc == null && agent.SupersededAtUtc == null && agent.SupersededByAgentId == null &&
                (term == null || term == "" || (agent.Name ?? "").ToLower().Contains(term) ||
                    (agent.FriendlyName ?? "").ToLower().Contains(term)))
            .OrderBy(agent => agent.Id).Take(limit + 1)
            .Select(agent => new FlowExecutionTargetDto(agent.Id, agent.FriendlyName ?? agent.Name, agent.Name))
            .ToArrayAsync(cancellationToken);
        return new(rows.Take(limit).ToArray(), rows.Length > limit ? rows[limit - 1].AgentId!.Value.ToString("D") : null);
    }


    public async Task<IReadOnlyList<FlowExecutionTargetSelectionDto>> GetSelectedTargetsAsync(int tenantId,
        FlowExecutionAuthorityDto authority, FlowExecutionSourceKind sourceKind, IReadOnlyList<Guid> ids,
        CancellationToken cancellationToken = default)
    {
        await RequireAsync(tenantId, authority, sourceKind, cancellationToken);
        if (ids.Count is < 1 or > 128 || ids.Any(id => id == Guid.Empty))
            throw new ArgumentException("Selected target identifiers exceed their bound.");
        var selected = ids.Distinct().ToArray();
        var found = await db.Agents.AsNoTracking().Where(agent => agent.TenantId == tenantId && selected.Contains(agent.Id))
            .Select(agent => new { agent.Id, agent.Name, agent.FriendlyName, Eligible = agent.IsEnabled &&
                agent.Status == AgentStatus.Active && agent.DeletedAtUtc == null && agent.RevokedAtUtc == null &&
                agent.SupersededAtUtc == null && agent.SupersededByAgentId == null })
            .ToDictionaryAsync(agent => agent.Id, cancellationToken);
        return selected.Select(id => found.TryGetValue(id, out var agent)
            ? new FlowExecutionTargetSelectionDto(new(id, agent.FriendlyName ?? agent.Name, agent.Name), agent.Eligible)
            : new(new(id), false)).ToArray();
    }

    private async Task RequireAsync(int tenantId, FlowExecutionAuthorityDto authority, FlowExecutionSourceKind kind, CancellationToken ct)
    {
        if (kind is not (FlowExecutionSourceKind.Job or FlowExecutionSourceKind.Script))
            throw new NotSupportedException("Requests are lifecycle sources, not runnable library definitions.");

        if (!await verifier.AuthorizeAsync(tenantId, authority, ct) ||
            !await access.AuthorizeAsync(FlowAuthorityPrincipal.Create(authority),
                kind == FlowExecutionSourceKind.Job ? NetRatelPermissions.JobManagement : NetRatelPermissions.ScriptExecute, tenantId, ct))
            throw new ExecutionAdmissionDeniedException("Current definition access is unavailable.");
    }
    private static bool Parse(string? id, out long value) => long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out value) && value > 0;
    private sealed record Row(long Id, string Name, Guid? AgentId, long? Revision);
}
