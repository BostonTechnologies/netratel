using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Mcp.Http;

/// <summary>Protected client-only setup channel, outside the model-visible MCP protocol.</summary>
public static class McpReviewedSetupEndpoints
{
    public static void MapMcpReviewedSetup(this WebApplication app, bool localCredentialMode)
    {
        if (!localCredentialMode) return;
        app.MapPost("/mcp/setup/access/start", async (ReviewedMcpAccessProposal proposal, HttpContext http,
            IHttpClientFactory clients, McpLocalCredentialPairingService pairing, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            var authorization = http.Request.Headers.Authorization.ToString();
            if (!authorization.StartsWith("Bearer nrt_ic_", StringComparison.Ordinal)) return Results.Unauthorized();
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/mcp/local-delegation/access/start")
            { Content = JsonContent.Create(proposal) };
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
            request.Headers.TryAddWithoutValidation("X-NetRatel-Mcp-Pairing", pairing.CreateAuthenticationProof());
            using var response = await clients.CreateClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return Results.Json(new { code = "reviewed_setup_not_admitted", nextAction = "Check the caller-owned credential, exact proposal and protected client state; resume the same requestId." }, statusCode: (int)response.StatusCode);
            var started = await McpLocalCredentialExchangeResponse.ReadJsonAsync<IntegrationPairingStartResponse>(response.Content, ct).ConfigureAwait(false);
            return started is null ? Results.StatusCode(502) : Results.Ok(started);
        }).RequireAuthorization(NetRatelMcpHttpApplication.PolicyName).RequireRateLimiting(NetRatelMcpHttpApplication.RateLimitPolicy)
          .WithMetadata(new RequestSizeLimitAttribute(32768));
    }
}
