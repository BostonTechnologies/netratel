using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Agents;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class EnrollmentCodeIssueService : IEnrollmentCodeIssueService
{
    internal const int MinValidityMinutes = 5;
    internal const int MaxValidityMinutes = 72460;
    private readonly OrchestratorDbContext _db;

    public EnrollmentCodeIssueService(OrchestratorDbContext db)
    {
        _db = db;
    }

    public async Task<EnrollmentCodeIssueResult> IssueAsync(EnrollmentCodeIssueRequest request, CancellationToken ct)
    {
        if (request.TenantId <= 0)
        {
            throw new AgentAuthException(400, "TenantId must be greater than zero.");
        }

        if (request.ValidForMinutes is < MinValidityMinutes or > MaxValidityMinutes)
        {
            throw new AgentAuthException(400, $"ValidForMinutes must be between {MinValidityMinutes} and {MaxValidityMinutes}.");
        }

        if (request.MaxUses <= 0)
        {
            throw new AgentAuthException(400, "MaxUses must be greater than zero.");
        }

        var now = DateTimeOffset.UtcNow;
        var code = await GenerateUniqueCodeAsync(ct);
        var entity = new EnrollmentCode
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Code = code,
            CreatedAtUtc = now,
            CreatedBy = request.CreatedBy,
            ValidFromUtc = now,
            ValidToUtc = now.AddMinutes(request.ValidForMinutes),
            MaxUses = request.MaxUses,
            Uses = 0,
            Notes = request.Notes,
            DevelopmentMcpTargetAgentId = request.DevelopmentMcpTargetAgentId,
            DevelopmentMcpMarker = request.DevelopmentMcpMarker
        };

        _db.EnrollmentCodes.Add(entity);
        await _db.SaveChangesAsync(ct);

        return new EnrollmentCodeIssueResult(entity.Id, entity.TenantId, entity.Code, entity.CreatedAtUtc, entity.ValidToUtc);
    }

    public async Task<PrimaryClientEnrollmentIssueResult> IssueForPrimaryClientBindingAsync(
        PrimaryClientEnrollmentIssueRequest request,
        CancellationToken ct)
    {
        if (request.TenantId <= 0)
        {
            throw new AgentAuthException(400, "TenantId must be greater than zero.");
        }

        if (request.ValidForMinutes is < MinValidityMinutes or > MaxValidityMinutes)
        {
            throw new AgentAuthException(400, $"ValidForMinutes must be between {MinValidityMinutes} and {MaxValidityMinutes}.");
        }

        var identity = PrimaryClientAgentBindingService.NormalizePrimaryClientIdentity(request.PrimaryClientIdentity);
        var actor = string.IsNullOrWhiteSpace(request.CreatedBy) ? "unknown" : request.CreatedBy.Trim();
        var existing = await _db.PrimaryClientAgentBindings
            .AsNoTracking()
            .AnyAsync(binding =>
                binding.TenantId == request.TenantId &&
                binding.PrimaryClientIdentity == identity &&
                binding.Status != PrimaryClientAgentBindingStatus.Revoked,
                ct)
            .ConfigureAwait(false);
        if (existing)
        {
            throw new AgentAuthException(409, "Primary client already has an active or pending gateway-agent binding.");
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(ct).ConfigureAwait(false)
            : null;
        var now = DateTimeOffset.UtcNow;
        var code = new EnrollmentCode
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            Code = await GenerateUniqueCodeAsync(ct).ConfigureAwait(false),
            CreatedAtUtc = now,
            CreatedBy = actor,
            ValidFromUtc = now,
            ValidToUtc = now.AddMinutes(request.ValidForMinutes),
            MaxUses = 1,
            Uses = 0,
            Notes = request.Notes?.Trim()
        };
        var binding = new PrimaryClientAgentBinding
        {
            Id = Guid.NewGuid(),
            TenantId = request.TenantId,
            EnrollmentCodeId = code.Id,
            PrimaryClientIdentity = identity,
            Status = PrimaryClientAgentBindingStatus.Pending,
            CreatedAtUtc = now,
            CreatedBy = actor,
            BindingSource = "atomic-server-issued-enrollment",
            Notes = request.Notes?.Trim()
        };

        _db.EnrollmentCodes.Add(code);
        _db.PrimaryClientAgentBindings.Add(binding);
        try
        {
            await _db.SaveChangesAsync(ct).ConfigureAwait(false);
            if (transaction is not null)
            {
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
            {
                await transaction.RollbackAsync(ct).ConfigureAwait(false);
            }
            throw new AgentAuthException(409, "Primary client could not be bound because a conflicting binding was created.", "primary_client_already_bound");
        }

        return new(
            new EnrollmentCodeIssueResult(code.Id, code.TenantId, code.Code, code.CreatedAtUtc, code.ValidToUtc),
            new PrimaryClientAgentBindingDto(
                binding.Id, binding.TenantId, binding.AgentId, binding.EnrollmentCodeId,
                binding.PrimaryClientIdentity, binding.Status, binding.CreatedAtUtc,
                binding.CreatedBy, binding.BoundAtUtc, binding.BoundBy, binding.RevokedAtUtc,
                binding.RevokedBy, binding.BindingSource, binding.Notes, binding.Version));
    }

    public async Task<EnrollmentCodeIssueResult> GetActiveAsync(Guid enrollmentCodeId, int tenantId, CancellationToken ct)
    {
        var code = await _db.EnrollmentCodes.FirstOrDefaultAsync(x => x.Id == enrollmentCodeId, ct);
        if (code is null)
        {
            throw new AgentAuthException(404, "Enrollment code not found.");
        }

        if (code.TenantId != tenantId)
        {
            throw new AgentAuthException(400, "Enrollment code is not valid for the selected tenant.");
        }

        var now = DateTimeOffset.UtcNow;
        if (code.RevokedAtUtc.HasValue)
        {
            throw new AgentAuthException(400, "Enrollment code is revoked.");
        }

        if (code.ValidFromUtc > now || code.ValidToUtc <= now)
        {
            throw new AgentAuthException(400, "Enrollment code is expired or not active yet.");
        }

        if (code.MaxUses.HasValue && code.Uses >= code.MaxUses.Value)
        {
            throw new AgentAuthException(400, "Enrollment code has reached maximum uses.");
        }

        return new EnrollmentCodeIssueResult(code.Id, code.TenantId, code.Code, code.CreatedAtUtc, code.ValidToUtc);
    }

    public async Task<DevelopmentMcpEnrollmentOwnershipRecord?> GetDevelopmentMcpOwnershipAsync(
        Guid enrollmentCodeId,
        int tenantId,
        Guid targetAgentId,
        CancellationToken ct)
        => await _db.EnrollmentCodes
            .AsNoTracking()
            .Where(code =>
                code.Id == enrollmentCodeId &&
                code.TenantId == tenantId &&
                code.DevelopmentMcpTargetAgentId == targetAgentId &&
                code.DevelopmentMcpMarker != null)
            .Select(code => new DevelopmentMcpEnrollmentOwnershipRecord(
                code.Id,
                code.TenantId,
                code.DevelopmentMcpTargetAgentId!.Value,
                code.DevelopmentMcpMarker!,
                code.CreatedAtUtc,
                code.ValidToUtc,
                code.MaxUses ?? 1,
                code.Uses,
                code.RevokedAtUtc))
            .SingleOrDefaultAsync(ct)
            .ConfigureAwait(false);

    public async Task<EnrollmentCodeIssueResult> ValidateActiveCodeAsync(string enrollmentCode, int tenantId, CancellationToken ct)
    {
        if (tenantId <= 0)
        {
            throw new AgentAuthException(400, "TenantId must be greater than zero.");
        }

        if (string.IsNullOrWhiteSpace(enrollmentCode))
        {
            throw new AgentAuthException(401, "Enrollment code is required.");
        }

        var normalized = enrollmentCode.Trim().ToUpperInvariant();
        var code = await _db.EnrollmentCodes.AsNoTracking().FirstOrDefaultAsync(x => x.Code == normalized, ct);
        if (code is null)
        {
            throw new AgentAuthException(401, "Enrollment code is invalid.");
        }

        if (code.TenantId != tenantId)
        {
            throw new AgentAuthException(403, "Enrollment code is not valid for the selected tenant.");
        }

        var now = DateTimeOffset.UtcNow;
        if (code.RevokedAtUtc.HasValue)
        {
            throw new AgentAuthException(403, "Enrollment code is revoked.");
        }

        if (code.ValidFromUtc > now || code.ValidToUtc <= now)
        {
            throw new AgentAuthException(403, "Enrollment code is expired or not active yet.");
        }

        if (code.MaxUses.HasValue && code.Uses >= code.MaxUses.Value)
        {
            throw new AgentAuthException(403, "Enrollment code has reached maximum uses.");
        }

        return new EnrollmentCodeIssueResult(code.Id, code.TenantId, code.Code, code.CreatedAtUtc, code.ValidToUtc);
    }

    public async Task<IReadOnlyList<EnrollmentCodeListItem>> ListAsync(int tenantId, string? status, string? search, CancellationToken ct)
    {
        if (tenantId <= 0)
        {
            throw new AgentAuthException(400, "TenantId must be greater than zero.");
        }

        var normalizedStatus = string.IsNullOrWhiteSpace(status) ? "active" : status.Trim().ToLowerInvariant();
        if (normalizedStatus is not ("active" or "expired" or "revoked" or "all"))
        {
            throw new AgentAuthException(400, "status must be one of: active, expired, revoked, all.");
        }

        var now = DateTimeOffset.UtcNow;
        var query = _db.EnrollmentCodes
            .AsNoTracking()
            .Where(x => x.TenantId == tenantId);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToUpperInvariant();
            query = query.Where(x =>
                x.Code.ToUpper().Contains(term) ||
                (x.Notes != null && x.Notes.ToUpper().Contains(term)));
        }

        query = normalizedStatus switch
        {
            "active" => query.Where(x =>
                x.RevokedAtUtc == null &&
                x.ValidFromUtc <= now &&
                x.ValidToUtc > now &&
                (!x.MaxUses.HasValue || x.Uses < x.MaxUses.Value)),
            "expired" => query.Where(x =>
                x.RevokedAtUtc == null &&
                (x.ValidFromUtc > now || x.ValidToUtc <= now || (x.MaxUses.HasValue && x.Uses >= x.MaxUses.Value))),
            "revoked" => query.Where(x => x.RevokedAtUtc != null),
            _ => query
        };

        var items = await query
            .OrderByDescending(x => x.CreatedAtUtc)
            .Select(x => new EnrollmentCodeListItem(
                x.Id,
                x.TenantId,
                x.Code,
                x.CreatedAtUtc,
                x.ValidFromUtc,
                x.ValidToUtc,
                x.Uses,
                x.MaxUses,
                x.RevokedAtUtc,
                x.RevokedBy,
                x.Notes,
                null,
                x.RevokedAtUtc == null &&
                x.ValidFromUtc <= now &&
                x.ValidToUtc > now &&
                (!x.MaxUses.HasValue || x.Uses < x.MaxUses.Value)))
            .ToListAsync(ct);

        return items;
    }

    public async Task RevokeAsync(Guid enrollmentCodeId, int tenantId, string? actor, string? reason, CancellationToken ct)
    {
        if (tenantId <= 0)
        {
            throw new AgentAuthException(400, "TenantId must be greater than zero.");
        }

        var code = await _db.EnrollmentCodes.FirstOrDefaultAsync(x => x.Id == enrollmentCodeId, ct);
        if (code is null || code.TenantId != tenantId)
        {
            throw new AgentAuthException(404, "Enrollment code not found.");
        }

        if (code.RevokedAtUtc.HasValue)
        {
            return;
        }

        var actorValue = string.IsNullOrWhiteSpace(actor) ? "unknown" : actor.Trim();
        var reasonValue = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();

        code.RevokedAtUtc = DateTimeOffset.UtcNow;
        code.RevokedBy = string.IsNullOrWhiteSpace(reasonValue) ? actorValue : $"{actorValue} ({reasonValue})";
        await _db.SaveChangesAsync(ct);
    }

    private async Task<string> GenerateUniqueCodeAsync(CancellationToken ct)
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

        for (var attempt = 0; attempt < 10; attempt++)
        {
            var buffer = RandomNumberGenerator.GetBytes(8);
            var chars = new char[8];
            for (var i = 0; i < buffer.Length; i++)
            {
                chars[i] = alphabet[buffer[i] % alphabet.Length];
            }

            var candidate = $"ENR-{new string(chars)}";
            var exists = await _db.EnrollmentCodes.AnyAsync(x => x.Code == candidate, ct);
            if (!exists)
            {
                return candidate;
            }
        }

        throw new AgentAuthException(500, "Failed to issue a unique enrollment code.");
    }
}
