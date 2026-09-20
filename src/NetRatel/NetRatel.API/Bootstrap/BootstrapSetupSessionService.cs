using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace NetRatel.API.Bootstrap;

/// <summary>
/// Binds a browser's setup completion request to the proof-claim operation without placing the
/// proof or any administrator secret in browser storage, URLs, or the bootstrap descriptor.
/// </summary>
public sealed class BootstrapSetupSessionService(IDataProtectionProvider protection, IConfiguration configuration)
{
    public const string CookieName = "NetRatel.Bootstrap.Setup";
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(15);
    private readonly ITimeLimitedDataProtector _protector = protection
        .CreateProtector(nameof(BootstrapSetupSessionService))
        .ToTimeLimitedDataProtector();
    private readonly bool _allowInsecureLocalhost = configuration.GetValue<bool>("Authentication:Local:AllowInsecureLocalhost");

    public void Issue(HttpContext context, Guid operationId)
    {
        var payload = _protector.Protect(JsonSerializer.Serialize(new BootstrapSetupSession(operationId)), Lifetime);
        context.Response.Cookies.Append(CookieName, payload, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            Secure = !_allowInsecureLocalhost,
            Path = "/",
            MaxAge = Lifetime
        });
    }

    public bool IsCurrent(HttpContext context, Guid operationId)
    {
        if (!context.Request.Cookies.TryGetValue(CookieName, out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        try
        {
            var session = JsonSerializer.Deserialize<BootstrapSetupSession>(_protector.Unprotect(value));
            return session is not null && CryptographicOperations.FixedTimeEquals(
                session.OperationId.ToByteArray(), operationId.ToByteArray());
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException)
        {
            return false;
        }
    }

    public void Clear(HttpContext context) => context.Response.Cookies.Delete(CookieName, new CookieOptions { Path = "/" });

    private sealed record BootstrapSetupSession(Guid OperationId);
}
