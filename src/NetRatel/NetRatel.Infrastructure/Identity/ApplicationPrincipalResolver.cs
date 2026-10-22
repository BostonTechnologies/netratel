using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Identity;

public interface IApplicationPrincipalResolver
{
    Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the durable identity link and bounded authority facts for a token that the configured
/// OIDC handler has already validated. Email and display-name claims are never
/// consulted when resolving the link.
/// </summary>
public sealed class ApplicationPrincipalResolver(NetRatelIdentityDbContext db) : IApplicationPrincipalResolver
{
    public async Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
        if (principal.HasClaim("token_use", "netratel_service") || principal.HasClaim("auth_mode", "service")) return null;
        var issuer = principal.FindFirst("iss")?.Value?.Trim();
        var subject = principal.FindFirst("sub")?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(subject))
        {
            return null;
        }

        var existing = await db.ApplicationPrincipals.SingleOrDefaultAsync(
            candidate => candidate.ExternalIssuer == issuer && candidate.ExternalSubject == subject,
            cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            await RefreshAuthorityAsync(existing.Id, principal, cancellationToken).ConfigureAwait(false);
            return existing.Id;
        }

        var created = new ApplicationPrincipal
        {
            ExternalIssuer = issuer,
            ExternalSubject = subject
        };
        db.ApplicationPrincipals.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
            var racedId = await db.ApplicationPrincipals
                .Where(candidate => candidate.ExternalIssuer == issuer && candidate.ExternalSubject == subject)
                .Select(candidate => candidate.Id)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (racedId is not null) await RefreshAuthorityAsync(racedId, principal, cancellationToken).ConfigureAwait(false);
            return racedId;
        }
        await RefreshAuthorityAsync(created.Id, principal, cancellationToken).ConfigureAwait(false);
        return created.Id;
    }

    private async Task RefreshAuthorityAsync(string id, ClaimsPrincipal principal, CancellationToken ct)
    {
        // Never persist forwarded/M2M claims or infer authority from email or a saved role name.
        var oidc = principal.Identities.FirstOrDefault(x => x.IsAuthenticated && x.AuthenticationType == "Oidc");
        if (oidc is null || !long.TryParse(oidc.FindFirst("iat")?.Value, out var issued) ||
            !long.TryParse(oidc.FindFirst("exp")?.Value, out var expires) || issued < 0 ||
            expires <= issued || expires > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) return;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(issued);
        var expiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
        var facts = oidc.Claims.Where(x => x.Type is "roles" or ClaimTypes.Role or "groups")
            .Select(x => new ExternalAuthorityClaim(x.Type, x.Value)).Distinct().Take(129).ToArray();
        // Oversized facts fail closed rather than retaining a partially selected role set.
        if (facts.Length > 128 || facts.Any(x => x.Value.Length > 512)) facts = [];
        var json = JsonSerializer.Serialize(facts);
        if (json.Length > 131072) { facts = []; json = "[]"; }
        await using var transaction = db.Database.IsRelational() && db.Database.CurrentTransaction is null
            ? await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false) : null;
        var row = db.Database.IsNpgsql()
            ? await db.ApplicationPrincipals.FromSqlInterpolated($"SELECT * FROM \"ApplicationPrincipals\" WHERE \"Id\" = {id} FOR UPDATE").SingleAsync(ct).ConfigureAwait(false)
            : await db.ApplicationPrincipals.SingleAsync(x => x.Id == id, ct).ConfigureAwait(false);
        // Refresh tracked values after acquiring the lock, so an older concurrent token cannot undo revocation.
        await db.Entry(row).ReloadAsync(ct).ConfigureAwait(false);
        if (row.ExternalIssuer != oidc.FindFirst("iss")?.Value?.Trim() ||
            row.ExternalSubject != oidc.FindFirst("sub")?.Value?.Trim()) return;
        if (row.ExternalAuthorityIssuedAtUtc is null || issuedAt >= row.ExternalAuthorityIssuedAtUtc)
        {
            if (issuedAt == row.ExternalAuthorityIssuedAtUtc && row.ExternalAuthorityClaimsJson is { } prior)
            {
                var previous = JsonSerializer.Deserialize<ExternalAuthorityClaim[]>(prior) ?? [];
                json = JsonSerializer.Serialize(facts.Intersect(previous).ToArray());
                if (row.ExternalAuthorityExpiresAtUtc is { } priorExpiry && priorExpiry < expiresAt) expiresAt = priorExpiry;
            }
            row.ExternalAuthorityClaimsJson = json;
            row.ExternalAuthorityIssuedAtUtc = issuedAt;
            row.ExternalAuthorityExpiresAtUtc = expiresAt;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        if (transaction is not null) await transaction.CommitAsync(ct).ConfigureAwait(false);
    }
}

public sealed record ExternalAuthorityClaim(string Type, string Value);
