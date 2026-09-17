using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Stores server-authorized primary-client bindings. The database constraints
/// are the cross-instance concurrency boundary; this service never derives a
/// binding from agent hello, hostname, IP address, or device metadata.
/// </summary>
public sealed class PrimaryClientAgentBindingService(OrchestratorDbContext db) : IPrimaryClientAgentBindingService
{
    public async Task<PrimaryClientAgentBindingDto?> GetByAgentAsync(int tenantId, Guid agentId, CancellationToken ct) =>
        await ActiveBindings(tenantId)
            .Where(binding => binding.AgentId == agentId)
            .Select(binding => Map(binding))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<PrimaryClientAgentBindingDto?> GetByPrimaryClientIdentityAsync(
        int tenantId,
        string primaryClientIdentity,
        CancellationToken ct)
    {
        var normalizedIdentity = NormalizePrimaryClientIdentity(primaryClientIdentity);
        return await ActiveBindings(tenantId)
            .Where(binding => binding.PrimaryClientIdentity == normalizedIdentity)
            .Select(binding => Map(binding))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PrimaryClientAgentBindingDto>> ListBoundAsync(int tenantId, CancellationToken ct) =>
        await ActiveBindings(tenantId)
            .Where(binding => binding.Status == PrimaryClientAgentBindingStatus.Bound && binding.AgentId.HasValue)
            .OrderBy(binding => binding.PrimaryClientIdentity)
            .Select(binding => Map(binding))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

    public async Task<PrimaryClientAgentBindingResult> CreatePendingAsync(
        CreatePendingPrimaryClientAgentBinding request,
        CancellationToken ct)
    {
        var normalizedIdentity = NormalizePrimaryClientIdentity(request.PrimaryClientIdentity);
        var code = await db.EnrollmentCodes
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == request.EnrollmentCodeId, ct)
            .ConfigureAwait(false);
        if (code is null)
        {
            return new(PrimaryClientAgentBindingDisposition.EnrollmentCodeNotFound, null);
        }

        if (code.TenantId != request.TenantId)
        {
            return new(PrimaryClientAgentBindingDisposition.TenantMismatch, null);
        }

        if (code.MaxUses != 1)
        {
            return new(PrimaryClientAgentBindingDisposition.EnrollmentCodeMustBeSingleUse, null);
        }

        var existingByCode = await ActiveBindings(request.TenantId)
            .Where(binding => binding.EnrollmentCodeId == request.EnrollmentCodeId)
            .Select(binding => Map(binding))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);
        if (existingByCode is not null)
        {
            return new(PrimaryClientAgentBindingDisposition.EnrollmentCodeAlreadyBound, existingByCode);
        }

        var existingByPrimary = await GetByPrimaryClientIdentityAsync(request.TenantId, normalizedIdentity, ct).ConfigureAwait(false);
        if (existingByPrimary is not null)
        {
            return new(PrimaryClientAgentBindingDisposition.PrimaryClientAlreadyBound, existingByPrimary);
        }

        var entity = new PrimaryClientAgentBinding
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            EnrollmentCodeId = request.EnrollmentCodeId,
            PrimaryClientIdentity = normalizedIdentity,
            Status = PrimaryClientAgentBindingStatus.Pending,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            CreatedBy = RequireText(request.CreatedBy, nameof(request.CreatedBy)),
            BindingSource = "server-issued-enrollment",
            Notes = NormalizeOptionalText(request.Notes)
        };

        db.PrimaryClientAgentBindings.Add(entity);
        return await SaveNewBindingAsync(entity, ct).ConfigureAwait(false);
    }

    public async Task<PrimaryClientAgentBindingResult> CreateManualRepairAsync(
        CreateManualPrimaryClientAgentBinding request,
        CancellationToken ct)
    {
        var normalizedIdentity = NormalizePrimaryClientIdentity(request.PrimaryClientIdentity);
        var agentExists = await db.Agents
            .AsNoTracking()
            .AnyAsync(agent => agent.TenantId == request.TenantId && agent.Id == request.AgentId, ct)
            .ConfigureAwait(false);
        if (!agentExists)
        {
            return new(PrimaryClientAgentBindingDisposition.AgentNotFound, null);
        }

        var existingByAgent = await GetByAgentAsync(request.TenantId, request.AgentId, ct).ConfigureAwait(false);
        if (existingByAgent is not null)
        {
            return new(
                string.Equals(existingByAgent.PrimaryClientIdentity, normalizedIdentity, StringComparison.Ordinal)
                    ? PrimaryClientAgentBindingDisposition.AlreadyBound
                    : PrimaryClientAgentBindingDisposition.AgentAlreadyBound,
                existingByAgent);
        }

        var existingByPrimary = await GetByPrimaryClientIdentityAsync(request.TenantId, normalizedIdentity, ct).ConfigureAwait(false);
        if (existingByPrimary is not null)
        {
            return new(PrimaryClientAgentBindingDisposition.PrimaryClientAlreadyBound, existingByPrimary);
        }

        var now = DateTimeOffset.UtcNow;
        var entity = new PrimaryClientAgentBinding
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            AgentId = request.AgentId,
            PrimaryClientIdentity = normalizedIdentity,
            Status = PrimaryClientAgentBindingStatus.Bound,
            CreatedAtUtc = now,
            CreatedBy = RequireText(request.CreatedBy, nameof(request.CreatedBy)),
            BoundAtUtc = now,
            BoundBy = RequireText(request.CreatedBy, nameof(request.CreatedBy)),
            BindingSource = "audited-admin-repair",
            Notes = RequireText(request.Notes, nameof(request.Notes))
        };

        db.PrimaryClientAgentBindings.Add(entity);
        return await SaveNewBindingAsync(entity, ct).ConfigureAwait(false);
    }

    public async Task<PrimaryClientAgentBindingResult> BindEnrollmentAsync(
        int tenantId,
        Guid enrollmentCodeId,
        Guid agentId,
        CancellationToken ct)
    {
        var agent = await db.Agents
            .AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Id == agentId, ct)
            .ConfigureAwait(false);
        if (agent is null)
        {
            return new(PrimaryClientAgentBindingDisposition.AgentNotFound, null);
        }

        var existingByAgent = await GetByAgentAsync(tenantId, agentId, ct).ConfigureAwait(false);
        if (existingByAgent is not null)
        {
            return new(PrimaryClientAgentBindingDisposition.AgentAlreadyBound, existingByAgent);
        }

        var binding = await db.PrimaryClientAgentBindings
            .SingleOrDefaultAsync(candidate =>
                candidate.TenantId == tenantId &&
                candidate.EnrollmentCodeId == enrollmentCodeId &&
                candidate.Status == PrimaryClientAgentBindingStatus.Pending,
                ct)
            .ConfigureAwait(false);
        if (binding is null)
        {
            return new(PrimaryClientAgentBindingDisposition.BindingNotPending, null);
        }

        binding.AgentId = agentId;
        binding.Status = PrimaryClientAgentBindingStatus.Bound;
        binding.BoundAtUtc = DateTimeOffset.UtcNow;
        binding.BoundBy = "enrollment";
        binding.Version++;

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(PrimaryClientAgentBindingDisposition.Created, Map(binding));
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await GetByAgentAsync(tenantId, agentId, ct).ConfigureAwait(false)
                ?? await GetByEnrollmentCodeAsync(tenantId, enrollmentCodeId, ct).ConfigureAwait(false);
            if (winner is null)
            {
                throw;
            }

            return new(PrimaryClientAgentBindingDisposition.AlreadyBound, winner);
        }
    }

    public async Task<bool> RevokeAsync(int tenantId, Guid bindingId, string revokedBy, string reason, CancellationToken ct)
    {
        var binding = await db.PrimaryClientAgentBindings
            .SingleOrDefaultAsync(candidate => candidate.TenantId == tenantId && candidate.Id == bindingId, ct)
            .ConfigureAwait(false);
        if (binding is null)
        {
            return false;
        }

        binding.Status = PrimaryClientAgentBindingStatus.Revoked;
        binding.RevokedAtUtc = DateTimeOffset.UtcNow;
        binding.RevokedBy = RequireText(revokedBy, nameof(revokedBy));
        binding.Notes = AppendReason(binding.Notes, RequireText(reason, nameof(reason)));
        binding.Version++;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<PrimaryClientAgentBindingDiagnosticsDto> GetDiagnosticsAsync(int tenantId, CancellationToken ct)
    {
        var agentIds = await db.Agents
            .AsNoTracking()
            .Where(agent => agent.TenantId == tenantId)
            .Select(agent => agent.Id)
            .ToArrayAsync(ct)
            .ConfigureAwait(false);
        var bindings = await db.PrimaryClientAgentBindings
            .AsNoTracking()
            .Where(binding => binding.TenantId == tenantId)
            .Select(binding => new BindingRow(
                binding.Id,
                binding.AgentId,
                binding.PrimaryClientIdentity,
                binding.Status,
                binding.EnrollmentCodeId))
            .ToArrayAsync(ct)
            .ConfigureAwait(false);

        var activeBindings = bindings.Where(binding => binding.Status != PrimaryClientAgentBindingStatus.Revoked).ToArray();
        var boundAgentIds = activeBindings
            .Where(binding => binding.Status == PrimaryClientAgentBindingStatus.Bound && binding.AgentId.HasValue)
            .Select(binding => binding.AgentId!.Value)
            .ToHashSet();

        return new(
            tenantId,
            agentIds.Length,
            boundAgentIds.Count,
            activeBindings.Count(binding => binding.Status == PrimaryClientAgentBindingStatus.Pending),
            agentIds.Where(agentId => !boundAgentIds.Contains(agentId)).OrderBy(agentId => agentId).ToArray(),
            activeBindings
                .Where(binding => binding.AgentId.HasValue)
                .GroupBy(binding => binding.AgentId!.Value)
                .Where(group => group.Count() > 1)
                .Select(group => new PrimaryClientAgentBindingConflict(
                    "ambiguous_agent_binding",
                    group.Key.ToString("D"),
                    group.Select(binding => binding.Id).ToArray()))
                .ToArray(),
            activeBindings
                .GroupBy(binding => binding.PrimaryClientIdentity, StringComparer.Ordinal)
                .Where(group => group.Count() > 1)
                .Select(group => new PrimaryClientAgentBindingConflict(
                    "conflicting_primary_client_binding",
                    group.Key,
                    group.Select(binding => binding.Id).ToArray()))
                .ToArray(),
            await db.PrimaryClientAgentBindings
                .AsNoTracking()
                .Where(binding => binding.TenantId == tenantId && binding.Status == PrimaryClientAgentBindingStatus.Revoked)
                .OrderByDescending(binding => binding.RevokedAtUtc)
                .Select(binding => Map(binding))
                .Take(100)
                .ToArrayAsync(ct)
                .ConfigureAwait(false));
    }

    public static string NormalizePrimaryClientIdentity(string value) =>
        RequireText(value, nameof(value)).ToUpperInvariant();

    private IQueryable<PrimaryClientAgentBinding> ActiveBindings(int tenantId) =>
        db.PrimaryClientAgentBindings
            .AsNoTracking()
            .Where(binding => binding.TenantId == tenantId && binding.Status != PrimaryClientAgentBindingStatus.Revoked);

    private async Task<PrimaryClientAgentBindingDto?> GetByEnrollmentCodeAsync(int tenantId, Guid enrollmentCodeId, CancellationToken ct) =>
        await ActiveBindings(tenantId)
            .Where(binding => binding.EnrollmentCodeId == enrollmentCodeId)
            .Select(binding => Map(binding))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

    private async Task<PrimaryClientAgentBindingResult> SaveNewBindingAsync(PrimaryClientAgentBinding entity, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new(PrimaryClientAgentBindingDisposition.Created, Map(entity));
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            var winner = await GetByPrimaryClientIdentityAsync(entity.TenantId, entity.PrimaryClientIdentity, ct).ConfigureAwait(false)
                ?? (entity.EnrollmentCodeId.HasValue
                    ? await GetByEnrollmentCodeAsync(entity.TenantId, entity.EnrollmentCodeId.Value, ct).ConfigureAwait(false)
                    : null)
                ?? (entity.AgentId.HasValue
                    ? await GetByAgentAsync(entity.TenantId, entity.AgentId.Value, ct).ConfigureAwait(false)
                    : null);
            if (winner is null)
            {
                throw;
            }

            return new(
                winner.PrimaryClientIdentity == entity.PrimaryClientIdentity
                    ? PrimaryClientAgentBindingDisposition.PrimaryClientAlreadyBound
                    : entity.AgentId.HasValue && winner.AgentId == entity.AgentId
                        ? PrimaryClientAgentBindingDisposition.AgentAlreadyBound
                        : PrimaryClientAgentBindingDisposition.EnrollmentCodeAlreadyBound,
                winner);
        }
    }

    private static PrimaryClientAgentBindingDto Map(PrimaryClientAgentBinding entity) => new(
        entity.Id,
        entity.TenantId,
        entity.AgentId,
        entity.EnrollmentCodeId,
        entity.PrimaryClientIdentity,
        entity.Status,
        entity.CreatedAtUtc,
        entity.CreatedBy,
        entity.BoundAtUtc,
        entity.BoundBy,
        entity.RevokedAtUtc,
        entity.RevokedBy,
        entity.BindingSource,
        entity.Notes,
        entity.Version);

    private static string RequireText(string value, string parameterName) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new ArgumentException("A value is required.", parameterName)
            : value.Trim();

    private static string? NormalizeOptionalText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string AppendReason(string? notes, string reason) =>
        string.IsNullOrWhiteSpace(notes) ? reason : $"{notes}\nRevoked: {reason}";

    private sealed record BindingRow(
        Guid Id,
        Guid? AgentId,
        string PrimaryClientIdentity,
        PrimaryClientAgentBindingStatus Status,
        Guid? EnrollmentCodeId);
}
