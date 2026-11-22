using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace NetRatel.ServiceDefaults;

public static class AgentAuthenticationRequestLimits
{
    public const int MaximumBodyBytes = 24 * 1024;

    public static PartitionedRateLimiter<HttpContext> CreateRateLimiter() =>
        PartitionedRateLimiter.Create<HttpContext, (string Route, string Source)>(context =>
        {
            var route = MatchRoute(context.Request);
            if (route is null)
                return RateLimitPartition.GetNoLimiter(("", ""));

            var address = context.Connection.RemoteIpAddress?.MapToIPv6().ToString() ?? "unknown";
            return RateLimitPartition.GetTokenBucketLimiter((route, address), _ => new TokenBucketRateLimiterOptions
            {
                AutoReplenishment = true,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                TokensPerPeriod = 10,
                TokenLimit = 120,
                QueueLimit = 0
            });
        });

    public static void UseAgentAuthenticationRequestLimits(this IApplicationBuilder app)
    {
        app.Use(async (context, next) =>
        {
            if (MatchRoute(context.Request) is null)
            {
                await next(context);
                return;
            }

            if (context.Request.ContentLength > MaximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit &&
                (limit.MaxRequestBodySize is null || limit.MaxRequestBodySize > MaximumBodyBytes))
                limit.MaxRequestBodySize = MaximumBodyBytes;

            try
            {
                await next(context);
            }
            catch (BadHttpRequestException exception) when (
                exception.StatusCode == StatusCodes.Status413PayloadTooLarge && !context.Response.HasStarted)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            }
        });
    }

    private static string? MatchRoute(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method))
            return null;

        var path = request.Path.Value?.TrimEnd('/');
        if (string.Equals(path, "/api/v1/agents/enroll", StringComparison.OrdinalIgnoreCase))
            return "enroll";
        return string.Equals(path, "/api/v1/agents/token", StringComparison.OrdinalIgnoreCase) ? "token" : null;
    }
}
