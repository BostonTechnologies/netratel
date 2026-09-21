using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using NetRatel.Web.Configuration;
using NetRatel.Web.Services.Authentication;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;

namespace NetRatel.Web.Controllers;

[AllowAnonymous]
[Route("auth")]
public class AuthController(
    IHostEnvironment environment,
    IConfiguration configuration,
    ISystemTokenService systemTokenService,
    IOptions<MachineTokenOptions> machineTokenOptions,
    IMachineTokenValidator machineTokenValidator) : Controller
{
    [HttpGet("oidc")]
    [HttpGet("azure")]
    public IActionResult OidcLogin(string? returnUrl = null)
    {
        if (string.Equals(configuration["Authentication:Mode"], "Local", StringComparison.OrdinalIgnoreCase) ||
            (string.IsNullOrWhiteSpace(configuration["Authentication:Oidc:Authority"]) &&
             string.IsNullOrWhiteSpace(configuration["Authentication:Azure:Authority"]) &&
             string.IsNullOrWhiteSpace(configuration["AzureAd:TenantId"])))
        {
            return NotFound();
        }

        returnUrl = NormalizeLocalRedirect(returnUrl, Url.Content("~/") ?? "/");
        var props = new AuthenticationProperties
        {
            RedirectUri = returnUrl,
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.Add(
                configuration.GetValue<TimeSpan?>("Authentication:Cookie:ExpireTimeSpan") ?? TimeSpan.FromHours(8))
        };
        return Challenge(props, "Oidc");
    }

    [HttpGet("/logout")]
    [HttpGet("logout")]
    public async Task<IActionResult> Logout()
    {
        if (string.Equals(User.FindFirst("auth_mode")?.Value, "local", StringComparison.OrdinalIgnoreCase))
        {
            await HttpContext.SignOutAsync("NetRatelLocal");
            return LocalRedirect(Url.Content("~/login")!);
        }

        if (string.Equals(User.FindFirst("auth_mode")?.Value, "machine_token", StringComparison.OrdinalIgnoreCase))
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return LocalRedirect(Url.Content("~/login")!);
        }

        var props = new AuthenticationProperties { RedirectUri = Url.Content("~/login") };
        return SignOut(props,
            CookieAuthenticationDefaults.AuthenticationScheme,
            "Oidc");
    }

    [HttpPost("machine-token/exchange")]
    [HttpPost("ai-agent/exchange")]
    public async Task<IActionResult> MachineTokenExchange([FromBody] MachineTokenExchangeRequest? request)
    {
        if (!machineTokenOptions.Value.Enabled)
        {
            return NotFound();
        }

        var token = request?.Token;
        if (string.IsNullOrWhiteSpace(token))
        {
            var authHeader = Request.Headers.Authorization.ToString();
            if (authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return BadRequest(new { error = "missing_token" });
        }

        var validation = await machineTokenValidator.ValidateAsync(token, HttpContext.RequestAborted);
        var returnUrl = NormalizeLocalRedirect(request?.ReturnUrl, Url.Content("~/")!);

        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            RedirectUri = returnUrl,
            ExpiresUtc = validation.ExpiresAt
        };

        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = token },
            new AuthenticationToken { Name = "expires_at", Value = validation.ExpiresAt.ToString("o") }
        ]);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, validation.Principal, properties);
        return LocalRedirect(returnUrl);
    }

    [HttpGet("development")]
    public async Task<IActionResult> DevelopmentLogin(string? returnUrl = null)
    {
        if (!environment.IsDevelopment() || !configuration.GetValue<bool>("DevelopmentOperator:Enabled"))
        {
            return NotFound();
        }

        returnUrl = NormalizeLocalRedirect(returnUrl, Url.Content("~/") ?? "/");
        var token = await systemTokenService.GetTokenAsync();
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var expiresAt = jwt.ValidTo == DateTime.MinValue
            ? DateTimeOffset.UtcNow.AddMinutes(30)
            : new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);

        var claims = jwt.Claims
            .Where(c => c.Type is not JwtRegisteredClaimNames.Exp
                and not JwtRegisteredClaimNames.Nbf
                and not JwtRegisteredClaimNames.Iat
                and not JwtRegisteredClaimNames.Aud
                and not JwtRegisteredClaimNames.Iss)
            .Select(c => new Claim(c.Type, c.Value))
            .ToList();

        var groups = claims
            .Where(c => c.Type == "groups")
            .Select(c => c.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var group in groups)
        {
            claims.Add(new Claim(ClaimTypes.Role, group));
        }

        if (claims.All(c => c.Type != ClaimTypes.Name))
        {
            var name = claims.FirstOrDefault(c => c.Type == "preferred_username")?.Value
                ?? claims.FirstOrDefault(c => c.Type == "name")?.Value
                ?? "Local Development Operator";
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            claims,
            CookieAuthenticationDefaults.AuthenticationScheme,
            "preferred_username",
            ClaimTypes.Role));

        var properties = new AuthenticationProperties
        {
            IsPersistent = true,
            RedirectUri = returnUrl,
            ExpiresUtc = expiresAt
        };

        properties.StoreTokens(
        [
            new AuthenticationToken { Name = "access_token", Value = token },
            new AuthenticationToken { Name = "expires_at", Value = expiresAt.ToString("o") }
        ]);

        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        return LocalRedirect(returnUrl);
    }

    private static string NormalizeLocalRedirect(string? value, string fallback)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !value.StartsWith("/", StringComparison.Ordinal)
            || value.StartsWith("//", StringComparison.Ordinal))
        {
            return fallback;
        }

        return value;
    }
}

public sealed record MachineTokenExchangeRequest(string? Token, string? ReturnUrl);
