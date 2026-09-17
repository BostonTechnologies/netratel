using System.Threading;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Abstractions;
using NetRatel.API.Security.M2M;
using NetRatel.Shared.Connectivity;

namespace NetRatel.API.Endpoints;

public static class AdminConnectivityEndpoints
{
    public static IEndpointRouteBuilder MapAdminConnectivityEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/v1/admin/connectivity")
            .RequireAuthorization("Operator")
            .WithTags("Connectivity");

        group.MapGet("/settings", (IM2MConnectivityService svc, CancellationToken ct) => svc.GetAsync(ct));

        group.MapPost("/tests", (IM2MConnectivityService svc, M2MConnectivityTestRequestDto dto, CancellationToken ct) => svc.TestAsync(dto, ct));

        app.MapGet("/api/v1/admin/orchestration/netratel", (
            IConfiguration cfg) =>
        {
            var m2m = cfg.GetSection("M2M").Get<M2MOptions>();

            var authority = m2m?.Authority;
            var tokenEndpoint = BuildDefaultTokenEndpoint(authority);
            var audience = m2m?.Audience;
            var scope = m2m?.Audience;
            var baseUrl = cfg["Orchestration:NetRatel:BaseUrl"] ?? cfg["NetRatelApi:BaseUrl"];

            return Results.Ok(new NetRatelOrchestrationSettingsDto(
                baseUrl,
                authority,
                audience,
                tokenEndpoint,
                scope));
        })
        .RequireAuthorization("Operator")
        .WithTags("Connectivity");

        return app;
    }

    private static string? BuildDefaultTokenEndpoint(string? authority)
    {
        if (string.IsNullOrWhiteSpace(authority))
        {
            return null;
        }

        return $"{authority.TrimEnd('/')}/connect/token";
    }
}
