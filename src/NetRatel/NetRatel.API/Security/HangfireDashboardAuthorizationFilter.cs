using Hangfire.Dashboard;

namespace NetRatel.API.Security;

public sealed class HangfireDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var user = httpContext.User;

        if (user.Identity?.IsAuthenticated != true)
        {
            return false;
        }

        return user.IsInRole("Operator") ||
            user.Claims.Any(c =>
                (string.Equals(c.Type, "roles", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Type, "role", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Type, "groups", StringComparison.OrdinalIgnoreCase)) &&
                string.Equals(c.Value, "Operator", StringComparison.OrdinalIgnoreCase));
    }
}
