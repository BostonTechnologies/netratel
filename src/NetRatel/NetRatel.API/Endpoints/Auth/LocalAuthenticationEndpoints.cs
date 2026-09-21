using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using NetRatel.API.Security.Local;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;

namespace NetRatel.API.Endpoints.Auth;

/// <summary>Local-account operations. There is intentionally no anonymous registration or reset route.</summary>
public static class LocalAuthenticationEndpoints
{
    private const string ChallengeCookie = "NetRatel.Local.LoginChallenge";
    private static readonly TimeSpan ChallengeLifetime = TimeSpan.FromMinutes(5);

    public static IEndpointRouteBuilder MapLocalAuthenticationEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v2/local-auth").WithTags("Local authentication");

        group.MapPost("/login", async (
            [FromBody] LocalLoginRequest request,
            UserManager<LocalUser> users,
            IDataProtectionProvider protection,
            LocalAuthenticationOptions options,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (!options.SupportsLocalAccounts || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Unauthorized();
            }

            var user = await users.FindByEmailAsync(request.Email.Trim()).ConfigureAwait(false);
            if (user is null || !user.IsEnabled || await users.IsLockedOutAsync(user).ConfigureAwait(false))
            {
                return Results.Unauthorized();
            }

            if (!await users.CheckPasswordAsync(user, request.Password).ConfigureAwait(false))
            {
                await users.AccessFailedAsync(user).ConfigureAwait(false);
                return Results.Unauthorized();
            }

            if (await users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
            {
                var challenge = new LocalLoginChallenge(user.Id, user.SecurityStamp ?? string.Empty, user.AuthorizationRevision, request.RememberMe);
                var value = protection.CreateProtector(nameof(LocalLoginChallenge))
                    .ToTimeLimitedDataProtector()
                    .Protect(JsonSerializer.Serialize(challenge), ChallengeLifetime);
                context.Response.Cookies.Append(ChallengeCookie, value, ChallengeCookieOptions(options, ChallengeLifetime));
                return Results.Accepted(value: new { requiresTwoFactor = true });
            }

            await CompleteLoginAsync(context, users, user, request.RememberMe, options).ConfigureAwait(false);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("local-login");

        group.MapPost("/login/two-factor", async (
            [FromBody] CompleteTwoFactorLoginRequest request,
            UserManager<LocalUser> users,
            IDataProtectionProvider protection,
            LocalAuthenticationOptions options,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var challenge = ReadChallenge(context, protection);
            var user = challenge is null ? null : await users.FindByIdAsync(challenge.UserId).ConfigureAwait(false);
            if (!options.SupportsLocalAccounts || user is null || !user.IsEnabled || await users.IsLockedOutAsync(user).ConfigureAwait(false) ||
                !await users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false) ||
                !string.Equals(challenge!.SecurityStamp, user.SecurityStamp, StringComparison.Ordinal) ||
                challenge.AuthorizationRevision != user.AuthorizationRevision ||
                !await IsSecondFactorValidAsync(users, user, request.Code).ConfigureAwait(false))
            {
                if (user is not null)
                {
                    await users.AccessFailedAsync(user).ConfigureAwait(false);
                }

                ClearChallenge(context, options);
                return Results.Unauthorized();
            }

            ClearChallenge(context, options);
            await CompleteLoginAsync(context, users, user, challenge.RememberMe, options).ConfigureAwait(false);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("local-login");

        group.MapPost("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapGet("/me", (ClaimsPrincipal user) => Results.Ok(new LocalAccountResponse(
            user.FindFirstValue(ClaimTypes.NameIdentifier) ?? string.Empty,
            user.FindFirstValue(LocalPrincipalClaimsTransformation.PrincipalIdClaimType) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
            user.FindFirstValue(ClaimTypes.Email) ?? string.Empty)))
            .RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapPost("/change-password", async (
            [FromBody] ChangePasswordRequest request,
            UserManager<LocalUser> users,
            HttpContext context) =>
        {
            var user = await CurrentLocalUserAsync(users, context.User).ConfigureAwait(false);
            if (user is null || !user.IsEnabled)
            {
                return Results.Unauthorized();
            }

            var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                return Results.BadRequest(new { error = "password_change_failed" });
            }

            user.AuthorizationRevision++;
            await users.UpdateAsync(user).ConfigureAwait(false);
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapPost("/two-factor/setup", async (
            [FromBody] CurrentPasswordRequest request,
            UserManager<LocalUser> users,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await CurrentLocalUserAsync(users, context.User).ConfigureAwait(false);
            if (user is null || !user.IsEnabled || !await users.CheckPasswordAsync(user, request.CurrentPassword).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "authenticator_setup_failed" });
            }

            // Replacing an enrolled factor must be an explicit recovery-safe
            // transition: disable it with the current factor first. Resetting
            // here would invalidate the existing authenticator before the new
            // one has been verified and persisted.
            if (await users.GetTwoFactorEnabledAsync(user).ConfigureAwait(false))
            {
                return Results.Conflict(new { error = "two_factor_already_enabled" });
            }

            await users.ResetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            var key = await users.GetAuthenticatorKeyAsync(user).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(key))
            {
                return Results.Problem("The authenticator setup could not be started.", statusCode: StatusCodes.Status409Conflict);
            }

            var accountName = user.Email ?? user.UserName ?? user.Id;
            var issuer = "NetRatel";
            var uri = $"otpauth://totp/{Uri.EscapeDataString($"{issuer}:{accountName}")}?secret={Uri.EscapeDataString(key)}&issuer={Uri.EscapeDataString(issuer)}&digits=6";
            return Results.Ok(new AuthenticatorSetupResponse(key, uri));
        }).RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapPost("/two-factor/enable", async (
            [FromBody] TwoFactorCodeRequest request,
            UserManager<LocalUser> users,
            HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            var user = await CurrentLocalUserAsync(users, context.User).ConfigureAwait(false);
            if (user is null || !user.IsEnabled ||
                !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeCode(request.Code)).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "invalid_authenticator_code" });
            }

            await users.SetTwoFactorEnabledAsync(user, true).ConfigureAwait(false);
            var recoveryCodes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10).ConfigureAwait(false);
            await InvalidateSessionsAsync(users, user).ConfigureAwait(false);
            return Results.Ok(new RecoveryCodesResponse(recoveryCodes?.ToArray() ?? []));
        }).RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapPost("/two-factor/disable", async (
            [FromBody] DisableTwoFactorRequest request,
            UserManager<LocalUser> users,
            HttpContext context) =>
        {
            var user = await CurrentLocalUserAsync(users, context.User).ConfigureAwait(false);
            if (user is null || !user.IsEnabled || !await users.CheckPasswordAsync(user, request.CurrentPassword).ConfigureAwait(false) ||
                !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeCode(request.Code)).ConfigureAwait(false))
            {
                return Results.BadRequest(new { error = "two_factor_disable_failed" });
            }

            await users.SetTwoFactorEnabledAsync(user, false).ConfigureAwait(false);
            await InvalidateSessionsAsync(users, user).ConfigureAwait(false);
            await context.SignOutAsync(LocalAuthenticationOptions.Scheme).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization(LocalAuthenticationOptions.LocalUserPolicy);

        group.MapPost("/users", async (
            [FromBody] CreateLocalAccountRequest request,
            UserManager<LocalUser> users,
            NetRatelIdentityDbContext db) =>
        {
            if (string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrWhiteSpace(request.DisplayName))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["account"] = ["Display name and email are required."] });
            }

            var principal = new ApplicationPrincipal();
            var user = new LocalUser
            {
                UserName = request.Email.Trim(),
                Email = request.Email.Trim(),
                DisplayName = request.DisplayName.Trim(),
                PrincipalId = principal.Id,
                IsInstanceAdministrator = false
            };
            db.ApplicationPrincipals.Add(principal);
            var created = await users.CreateAsync(user).ConfigureAwait(false);
            if (!created.Succeeded)
            {
                return Results.BadRequest(new { error = "account_create_failed" });
            }

            principal.LocalUserId = user.Id;
            await db.SaveChangesAsync().ConfigureAwait(false);
            var activationToken = await users.GeneratePasswordResetTokenAsync(user).ConfigureAwait(false);
            return Results.Created($"/api/v2/local-auth/users/{user.Id}", new ActivationResponse(user.Id, user.Email!, activationToken));
        }).RequireAuthorization("InstanceAdministrator");

        group.MapPost("/activate", async (
            [FromBody] ActivateLocalAccountRequest request,
            UserManager<LocalUser> users) =>
        {
            var user = await users.FindByEmailAsync(request.Email.Trim()).ConfigureAwait(false);
            if (user is null || !user.IsEnabled)
            {
                return Results.BadRequest(new { error = "activation_failed" });
            }

            var reset = await users.ResetPasswordAsync(user, request.ActivationToken, request.NewPassword).ConfigureAwait(false);
            if (!reset.Succeeded)
            {
                return Results.BadRequest(new { error = "activation_failed" });
            }

            user.EmailConfirmed = true;
            await InvalidateSessionsAsync(users, user).ConfigureAwait(false);
            return Results.NoContent();
        }).AllowAnonymous().RequireRateLimiting("local-login");

        group.MapPost("/users/{userId}/disable", async (string userId, NetRatelIdentityDbContext db, InstanceAdministratorInvariant administrators, CancellationToken cancellationToken) =>
        {
            return await administrators.ExecuteDestructiveMutationAsync(async ct =>
            {
                var user = await db.Users.SingleOrDefaultAsync(candidate => candidate.Id == userId, ct).ConfigureAwait(false);
                if (user is null)
                    return Results.NotFound();

                var hasInstanceAdministration = user.IsInstanceAdministrator || await db.PrincipalRoleAssignments
                    .AnyAsync(assignment => assignment.PrincipalId == user.PrincipalId && assignment.TenantId == null && assignment.Role!.IsInstanceAdministratorRole, ct)
                    .ConfigureAwait(false);
                if (user.IsEnabled && hasInstanceAdministration && await administrators.ViableAdministratorCountAsync(ct).ConfigureAwait(false) <= 1)
                    return Results.Conflict(new { error = "last_instance_administrator" });

                user.IsEnabled = false;
                user.DisabledAtUtc = DateTimeOffset.UtcNow;
                user.AuthorizationRevision++;
                user.SecurityStamp = Guid.NewGuid().ToString("N");
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Results.NoContent();
            }, cancellationToken).ConfigureAwait(false);
        }).RequireAuthorization("InstanceAdministrator");

        group.MapPost("/users/{userId}/enable", async (string userId, UserManager<LocalUser> users) =>
        {
            var user = await users.FindByIdAsync(userId).ConfigureAwait(false);
            if (user is null)
            {
                return Results.NotFound();
            }

            user.IsEnabled = true;
            user.DisabledAtUtc = null;
            await InvalidateSessionsAsync(users, user).ConfigureAwait(false);
            return Results.NoContent();
        }).RequireAuthorization("InstanceAdministrator");

        return app;
    }

    private static async Task CompleteLoginAsync(HttpContext context, UserManager<LocalUser> users, LocalUser user, bool rememberMe, LocalAuthenticationOptions options)
    {
        await users.ResetAccessFailedCountAsync(user).ConfigureAwait(false);
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? user.Id : user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email ?? string.Empty),
            new Claim("auth_mode", "local"),
            new Claim("security_stamp", user.SecurityStamp ?? string.Empty),
            new Claim("authorization_revision", user.AuthorizationRevision.ToString(CultureInfo.InvariantCulture)),
            new Claim(LocalPrincipalClaimsTransformation.PrincipalIdClaimType, user.PrincipalId)
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, LocalAuthenticationOptions.Scheme, ClaimTypes.Name, ClaimTypes.Role));
        await context.SignInAsync(LocalAuthenticationOptions.Scheme, principal, new AuthenticationProperties
        {
            IsPersistent = rememberMe,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        }).ConfigureAwait(false);
    }

    private static async Task<LocalUser?> CurrentLocalUserAsync(UserManager<LocalUser> users, ClaimsPrincipal principal) =>
        principal.FindFirstValue(ClaimTypes.NameIdentifier) is { Length: > 0 } userId
            ? await users.FindByIdAsync(userId).ConfigureAwait(false)
            : null;

    private static async Task InvalidateSessionsAsync(UserManager<LocalUser> users, LocalUser user)
    {
        user.AuthorizationRevision++;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await users.UpdateAsync(user).ConfigureAwait(false);
    }

    private static async Task<bool> IsSecondFactorValidAsync(UserManager<LocalUser> users, LocalUser user, string code) =>
        await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, NormalizeCode(code)).ConfigureAwait(false)
        || (await users.RedeemTwoFactorRecoveryCodeAsync(user, code.Replace(" ", string.Empty, StringComparison.Ordinal)).ConfigureAwait(false)).Succeeded;

    private static string NormalizeCode(string code) => code.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal);

    private static LocalLoginChallenge? ReadChallenge(HttpContext context, IDataProtectionProvider protection)
    {
        if (!context.Request.Cookies.TryGetValue(ChallengeCookie, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LocalLoginChallenge>(protection.CreateProtector(nameof(LocalLoginChallenge)).ToTimeLimitedDataProtector().Unprotect(value));
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return null;
        }
    }

    private static void ClearChallenge(HttpContext context, LocalAuthenticationOptions options) => context.Response.Cookies.Delete(ChallengeCookie, ChallengeCookieOptions(options, TimeSpan.Zero));

    private static CookieOptions ChallengeCookieOptions(LocalAuthenticationOptions options, TimeSpan lifetime) => new()
    {
        HttpOnly = true,
        Secure = !options.AllowInsecureLocalhost,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = lifetime
    };

    public sealed record LocalLoginRequest(string Email, string Password, bool RememberMe = false);
    public sealed record CompleteTwoFactorLoginRequest(string Code);
    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    public sealed record CurrentPasswordRequest(string CurrentPassword);
    public sealed record TwoFactorCodeRequest(string Code);
    public sealed record DisableTwoFactorRequest(string CurrentPassword, string Code);
    public sealed record CreateLocalAccountRequest(string DisplayName, string Email);
    public sealed record ActivateLocalAccountRequest(string Email, string ActivationToken, string NewPassword);
    public sealed record LocalAccountResponse(string UserId, string PrincipalId, string DisplayName, string Email);
    public sealed record ActivationResponse(string UserId, string Email, string ActivationToken);
    public sealed record AuthenticatorSetupResponse(string SharedKey, string AuthenticatorUri);
    public sealed record RecoveryCodesResponse(IReadOnlyList<string> RecoveryCodes);
    private sealed record LocalLoginChallenge(string UserId, string SecurityStamp, long AuthorizationRevision, bool RememberMe);
}
