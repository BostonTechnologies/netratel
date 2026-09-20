using System.Security.Claims;
using Microsoft.EntityFrameworkCore;

namespace NetRatel.Infrastructure.Identity;

public interface IApplicationPrincipalResolver
{
    Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates the durable, non-authorizing link for a token that the configured
/// OIDC handler has already validated. Email and display-name claims are never
/// consulted when resolving the link.
/// </summary>
public sealed class ApplicationPrincipalResolver(NetRatelIdentityDbContext db) : IApplicationPrincipalResolver
{
    public async Task<string?> ResolveExternalAsync(ClaimsPrincipal principal, CancellationToken cancellationToken = default)
    {
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
            return created.Id;
        }
        catch (DbUpdateException)
        {
            db.Entry(created).State = EntityState.Detached;
            return await db.ApplicationPrincipals
                .Where(candidate => candidate.ExternalIssuer == issuer && candidate.ExternalSubject == subject)
                .Select(candidate => candidate.Id)
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
        }
    }
}
