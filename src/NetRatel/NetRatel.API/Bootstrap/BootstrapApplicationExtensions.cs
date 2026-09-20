using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using System.Threading.RateLimiting;

namespace NetRatel.API.Bootstrap;

public static class BootstrapApplicationExtensions
{
    public const string ClaimRateLimitPolicy = "bootstrap-claim";

    public static void AddBootstrapRuntime(this IServiceCollection services, BootstrapOptions options)
    {
        services.AddSingleton(options);
        services.AddSingleton<BootstrapStateStore>();
        services.AddSingleton<BootstrapLifecycleService>();
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
