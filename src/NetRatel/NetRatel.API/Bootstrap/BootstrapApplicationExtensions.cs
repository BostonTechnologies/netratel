using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Infrastructure.Identity;
using System.Threading.RateLimiting;

namespace NetRatel.API.Bootstrap;

public static class BootstrapApplicationExtensions
{
    public const string ClaimRateLimitPolicy = "bootstrap-claim";

    public static void AddBootstrapRuntime(this IServiceCollection services, BootstrapOptions options, IConfiguration configuration)
    {
        services.AddSingleton(options);
        var configuredKeysDir = configuration["NetRatel_KEYS_DIR"] ?? configuration["DataProtection:KeysDirectory"];
        var keyRingPath = string.IsNullOrWhiteSpace(configuredKeysDir)
            ? Path.Combine(AppContext.BaseDirectory, ".keys")
            : (Path.IsPathRooted(configuredKeysDir) ? configuredKeysDir : Path.Combine(AppContext.BaseDirectory, configuredKeysDir));
        Directory.CreateDirectory(keyRingPath);
        services.AddDataProtection()
            .PersistKeysToFileSystem(new DirectoryInfo(keyRingPath))
            .SetApplicationName(configuration["DataProtection:ApplicationName"] ?? "NetRatel-Keyring");
        services.AddSingleton<BootstrapStateStore>();
        services.AddSingleton<BootstrapLifecycleService>();
        services.Configure<IdentityOptions>(identity => CopyIdentityOptions(CreateBootstrapIdentityOptions(), identity));
        services.AddSingleton<IPasswordHasher<LocalUser>, PasswordHasher<LocalUser>>();
        services.AddSingleton<BootstrapInitializationService>();
        services.AddSingleton<BootstrapSetupSessionService>();
        services.AddRateLimiter(rateLimits => rateLimits.AddPolicy(ClaimRateLimitPolicy, context =>
        {
            var partitionKey = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            return RateLimitPartition.GetFixedWindowLimiter(partitionKey, _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
        }));
    }

    public static IdentityOptions CreateBootstrapIdentityOptions() => new()
    {
        User = { RequireUniqueEmail = true },
        Lockout =
        {
            AllowedForNewUsers = true,
            MaxFailedAccessAttempts = 5,
            DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15)
        },
        Password =
        {
            RequiredLength = 15,
            RequiredUniqueChars = 1,
            RequireDigit = false,
            RequireLowercase = false,
            RequireUppercase = false,
            RequireNonAlphanumeric = false
        }
    };

    private static void CopyIdentityOptions(IdentityOptions source, IdentityOptions target)
    {
        target.User.RequireUniqueEmail = source.User.RequireUniqueEmail;
        target.Lockout.AllowedForNewUsers = source.Lockout.AllowedForNewUsers;
        target.Lockout.MaxFailedAccessAttempts = source.Lockout.MaxFailedAccessAttempts;
        target.Lockout.DefaultLockoutTimeSpan = source.Lockout.DefaultLockoutTimeSpan;
        target.Password.RequiredLength = source.Password.RequiredLength;
        target.Password.RequiredUniqueChars = source.Password.RequiredUniqueChars;
        target.Password.RequireDigit = source.Password.RequireDigit;
        target.Password.RequireLowercase = source.Password.RequireLowercase;
        target.Password.RequireUppercase = source.Password.RequireUppercase;
        target.Password.RequireNonAlphanumeric = source.Password.RequireNonAlphanumeric;
    }

    public static void ConfigureBootstrapListener(this WebApplicationBuilder builder)
    {
        var port = builder.Configuration.GetValue<int>("NetRatel_HTTP_PORT", 9222);
        if (port is < 1 or > 65535)
        {
            throw new InvalidOperationException("NetRatel_HTTP_PORT must be between 1 and 65535.");
        }

        builder.WebHost.ConfigureKestrel(k => k.ListenAnyIP(port, listener => listener.Protocols = HttpProtocols.Http1));
    }

    public static void UseBootstrapRuntime(this WebApplication app)
    {
        // Forwarded headers are ignored unless ASP.NET Core has explicitly trusted the proxy,
        // which prevents a client-supplied X-Forwarded-* value from changing origin checks.
        app.UseForwardedHeaders(new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        });
        app.UseRateLimiter();
        app.MapBootstrapEndpoints();
    }
}
