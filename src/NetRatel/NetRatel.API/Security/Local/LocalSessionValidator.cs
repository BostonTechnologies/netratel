using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using NetRatel.Infrastructure.Identity;

namespace NetRatel.API.Security.Local;

public static class LocalSessionValidator
{
    public static bool IsValid(ClaimsPrincipal principal, LocalUser user)
    {
        return user.IsEnabled &&
            string.Equals(principal.FindFirstValue(ClaimTypes.NameIdentifier), user.Id, StringComparison.Ordinal) &&
            string.Equals(principal.FindFirstValue("security_stamp"), user.SecurityStamp, StringComparison.Ordinal) &&
            string.Equals(principal.FindFirstValue("authorization_revision"),
                user.AuthorizationRevision.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) &&
            string.Equals(principal.FindFirstValue(LocalPrincipalClaimsTransformation.PrincipalIdClaimType), user.PrincipalId, StringComparison.Ordinal);
    }
}
