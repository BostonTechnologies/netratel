using System.Data;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Durable ownership, expiry, and byte-wiping boundary for bounded V2 file
/// artifacts. Remote paths and bearer tokens never cross this boundary.
/// </summary>
public sealed class McpOperatorFileArtifactStore(OrchestratorDbContext db) : IMcpOperatorFileArtifactStore
{
    private readonly OrchestratorDbContext _db = db;

    public async Task<McpOperatorFileArtifact> CreateOrGetAsync(
        McpOperatorFileArtifactCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateCreate(request);

        var existing = await FindByIdempotencyAsync(request.IdempotencyId, includeContent: false, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            return SameAdmission(existing, request)
                ? ToArtifact(existing, includeContent: false)
                : throw new InvalidOperationException("The artifact idempotency admission is already bound to another collection.");
        }

        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;
        try
        {
            existing = await FindByIdempotencyAsync(request.IdempotencyId, includeContent: false, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                if (transaction is not null)
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return SameAdmission(existing, request)
                    ? ToArtifact(existing, includeContent: false)
                    : throw new InvalidOperationException("The artifact idempotency admission is already bound to another collection.");
            }

            var access = request.Access;
            var record = new McpOperatorFileArtifactRecord
            {
                Id = Guid.NewGuid(),
                TenantId = access.TenantId,
                AgentId = access.AgentId,
                Subject = access.Principal.Subject,
                ClientId = ClientId(access.Principal),
                McpResource = access.McpResource!,
                McpInstance = access.McpInstance!,
                ReadRootFingerprint = request.ReadRootFingerprint,
                FileName = request.FileName,
                SizeBytes = request.Content.Length,
                Sha256 = Sha256(request.Content),
                MimeType = request.MimeType,
                Content = request.Content,
                AcceptedAuditId = request.AcceptedAuditId,
                IdempotencyId = request.IdempotencyId,
                CreatedAtUtc = request.CreatedAtUtc,
                ExpiresAtUtc = request.ExpiresAtUtc
            };
            _db.McpOperatorFileArtifacts.Add(record);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            if (transaction is not null)
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return ToArtifact(record, includeContent: false);
        }
        catch (DbUpdateException)
        {
            if (transaction is not null)
                await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            existing = await FindByIdempotencyAsync(request.IdempotencyId, includeContent: false, cancellationToken).ConfigureAwait(false);
            if (existing is not null && SameAdmission(existing, request))
                return ToArtifact(existing, includeContent: false);
            throw;
        }
    }

    public async Task<McpOperatorFileArtifact?> GetOwnedAsync(
        Guid artifactId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        bool includeContent,
        CancellationToken cancellationToken)
    {
        if (!IsOwnerRequestValid(artifactId, tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;

        var query = _db.McpOperatorFileArtifacts.AsNoTracking().Where(candidate =>
            candidate.Id == artifactId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance);
        var record = includeContent
            ? await query.SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            : await MetadataOnly(query).SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return record is null ? null : ToArtifact(record, includeContent);
    }

    public async Task<McpOperatorFileArtifact?> CleanupOwnedAsync(
        Guid artifactId,
        int tenantId,
        Guid agentId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        if (!IsOwnerRequestValid(artifactId, tenantId, agentId, principal, mcpResource, mcpInstance))
            return null;

        var record = await _db.McpOperatorFileArtifacts.SingleOrDefaultAsync(candidate =>
            candidate.Id == artifactId && candidate.TenantId == tenantId && candidate.AgentId == agentId &&
            candidate.Subject == principal.Subject && candidate.ClientId == ClientId(principal) &&
            candidate.McpResource == mcpResource && candidate.McpInstance == mcpInstance,
            cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;

        if (record.DeletedAtUtc is null)
        {
            record.Content = [];
            record.DeletedAtUtc = now;
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        return ToArtifact(record, includeContent: false);
    }

    public async Task<int> PurgeExpiredAsync(DateTimeOffset now, int maximumCount, CancellationToken cancellationToken)
    {
        if (maximumCount is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(maximumCount));

        var expired = await _db.McpOperatorFileArtifacts
            .Where(artifact => artifact.ExpiresAtUtc <= now && artifact.DeletedAtUtc == null)
            .OrderBy(artifact => artifact.ExpiresAtUtc)
            .Take(maximumCount)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        foreach (var artifact in expired)
        {
            artifact.Content = [];
            artifact.DeletedAtUtc = now;
        }

        if (expired.Count > 0)
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return expired.Count;
    }

    private Task<McpOperatorFileArtifactRecord?> FindByIdempotencyAsync(Guid idempotencyId, bool includeContent, CancellationToken cancellationToken)
    {
        var query = _db.McpOperatorFileArtifacts.AsNoTracking().Where(candidate => candidate.IdempotencyId == idempotencyId);
        return includeContent
            ? query.SingleOrDefaultAsync(cancellationToken)
            : MetadataOnly(query).SingleOrDefaultAsync(cancellationToken);
    }

    private static IQueryable<McpOperatorFileArtifactRecord> MetadataOnly(IQueryable<McpOperatorFileArtifactRecord> query) =>
        query.Select(candidate => new McpOperatorFileArtifactRecord
        {
            Id = candidate.Id,
            TenantId = candidate.TenantId,
            AgentId = candidate.AgentId,
            Subject = candidate.Subject,
            ClientId = candidate.ClientId,
            McpResource = candidate.McpResource,
            McpInstance = candidate.McpInstance,
            ReadRootFingerprint = candidate.ReadRootFingerprint,
            FileName = candidate.FileName,
            SizeBytes = candidate.SizeBytes,
            Sha256 = candidate.Sha256,
            MimeType = candidate.MimeType,
            AcceptedAuditId = candidate.AcceptedAuditId,
            IdempotencyId = candidate.IdempotencyId,
            CreatedAtUtc = candidate.CreatedAtUtc,
            ExpiresAtUtc = candidate.ExpiresAtUtc,
            DeletedAtUtc = candidate.DeletedAtUtc
        });

    private static void ValidateCreate(McpOperatorFileArtifactCreateRequest request)
    {
        var access = request.Access;
        if (access.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) || access.Tool != "netratel_files" || access.Operation != "collect" ||
            access.TenantId <= 0 || access.AgentId == Guid.Empty ||
            string.IsNullOrWhiteSpace(access.Principal.Subject) || string.IsNullOrWhiteSpace(access.McpResource) || string.IsNullOrWhiteSpace(access.McpInstance) ||
            !IsSha256(request.ReadRootFingerprint) || !IsFileName(request.FileName) || !IsMimeType(request.MimeType) || request.Content is not { Length: > 0 and <= 512 * 1024 } ||
            request.AcceptedAuditId == Guid.Empty || request.IdempotencyId == Guid.Empty || request.CreatedAtUtc == default || request.ExpiresAtUtc <= request.CreatedAtUtc)
        {
            throw new ArgumentException("The artifact request is not an allowed, bounded V2 collection admission.", nameof(request));
        }
    }

    private static bool SameAdmission(McpOperatorFileArtifactRecord record, McpOperatorFileArtifactCreateRequest request) =>
        record.TenantId == request.Access.TenantId && record.AgentId == request.Access.AgentId &&
        record.Subject == request.Access.Principal.Subject && record.ClientId == ClientId(request.Access.Principal) &&
        record.McpResource == request.Access.McpResource && record.McpInstance == request.Access.McpInstance &&
        record.ReadRootFingerprint == request.ReadRootFingerprint && record.FileName == request.FileName &&
        record.SizeBytes == request.Content.Length && record.Sha256 == Sha256(request.Content) &&
        record.AcceptedAuditId == request.AcceptedAuditId;

    private static bool IsOwnerRequestValid(Guid artifactId, int tenantId, Guid agentId, McpOperatorPrincipal principal, string? mcpResource, string? mcpInstance) =>
        artifactId != Guid.Empty && tenantId > 0 && agentId != Guid.Empty && !string.IsNullOrWhiteSpace(principal.Subject) &&
        !string.IsNullOrWhiteSpace(mcpResource) && !string.IsNullOrWhiteSpace(mcpInstance);

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static bool IsFileName(string? value) => value is { Length: > 0 and <= 512 } && !value.Any(char.IsControl);

    private static bool IsMimeType(string? value) => value is { Length: > 0 and <= 256 } && !value.Any(char.IsControl);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;

    private static string Sha256(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

    private static McpOperatorFileArtifact ToArtifact(McpOperatorFileArtifactRecord record, bool includeContent) => new(
        record.Id,
        record.TenantId,
        record.AgentId,
        record.Subject,
        record.ClientId,
        record.McpResource,
        record.McpInstance,
        record.ReadRootFingerprint,
        record.FileName,
        record.SizeBytes,
        record.Sha256,
        record.MimeType,
        includeContent ? record.Content : null,
        record.AcceptedAuditId,
        record.IdempotencyId,
        record.CreatedAtUtc,
        record.ExpiresAtUtc,
        record.DeletedAtUtc);
}
