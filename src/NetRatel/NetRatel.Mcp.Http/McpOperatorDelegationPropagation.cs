using System.Security.Claims;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Http;

/// <summary>
/// Converts the already-validated MCP OAuth principal into a bounded signed
/// delegation assertion for the one downstream API call chain. It deliberately
/// has no access to the incoming bearer token.
/// </summary>
public sealed class McpOperatorDelegationPropagation(
    McpOperatorDelegationOptions options,
    McpOperatorDelegationTokenService tokens,
    IMcpOperatorDelegationContext context,
    NetRatelMcpHostContext hostContext,
    NetRatelMcpHttpOptions httpOptions,
    IHttpContextAccessor? httpContextAccessor = null,
    IHttpClientFactory? httpClients = null,
    McpLocalCredentialPairingService? localPairing = null)
{
    public IDisposable Begin(ClaimsPrincipal? user, string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (!options.Enabled)
            return EmptyLease.Instance;

        if (user?.Identity?.IsAuthenticated is not true)
            throw new InvalidOperationException("An authenticated MCP caller is required for delegated operator identity.");

        var isControlPlaneTenantOperation = string.Equals(Token(tool), "netratel_tenants", StringComparison.Ordinal);
        var assertion = tokens.Create(
            IdentityFrom(user),
            new McpOperatorDelegationRequest(
                Token(tool),
                Operation(arguments),
                Guid.NewGuid().ToString("N"),
                httpOptions.PublicResourceUri.TrimEnd('/'),
                hostContext.Target.Instance,
                isControlPlaneTenantOperation ? null : TenantId(arguments),
                isControlPlaneTenantOperation ? null : AgentId(arguments),
                Activity.Current?.TraceId.ToString()),
            issuedAtUtc: null);
        return context.Begin(assertion);
    }

    /// <summary>
    /// Exchanges a local HTTP-MCP bearer for a one-operation delegation. The
    /// raw bearer remains confined to the dedicated exchange endpoint; the
    /// normal outbound API client receives only the returned assertion plus
    /// its existing gateway credential.
    /// </summary>
    public async Task<IDisposable> BeginAsync(
        ClaimsPrincipal? user,
        string tool,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (!httpOptions.LocalCredentialMode)
            return Begin(user, tool, arguments);
        if (user?.Identity?.IsAuthenticated is not true || httpContextAccessor?.HttpContext is not { } http || httpClients is null || localPairing is null)
            throw new InvalidOperationException("A local HTTP MCP credential exchange requires the active request context.");

        var authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer nrt_ic_", StringComparison.Ordinal))
            throw new InvalidOperationException("A local HTTP MCP credential is required.");

        var operation = Operation(arguments);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/mcp/local-delegation/exchange");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        request.Headers.TryAddWithoutValidation("X-NetRatel-Mcp-Pairing", localPairing.CreateExecutionProof(tool, operation, TenantId(arguments), AgentId(arguments)));
        using var response = await httpClients.CreateClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName)
            .SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Local HTTP MCP credential exchange was rejected with HTTP {(int)response.StatusCode}.");

        var execution = await response.Content.ReadFromJsonAsync<McpLocalExecutionResponse>(cancellationToken: cancellationToken).ConfigureAwait(false);
        if (execution is null || string.IsNullOrWhiteSpace(execution.Assertion))
            throw new InvalidOperationException("Local HTTP MCP credential exchange returned an invalid response.");
        return context.Begin(execution.Assertion);
    }

    private static McpOperatorDelegationIdentity IdentityFrom(ClaimsPrincipal user)
    {
        return McpOperatorDelegationIdentityFactory.TryCreate(user, out var identity)
            ? identity!
            : throw new InvalidOperationException("The authenticated MCP caller does not provide a stable delegated identity.");
    }

    private static string Operation(IDictionary<string, JsonElement>? arguments)
        => arguments is not null && arguments.TryGetValue("operation", out var operation) && operation.ValueKind == JsonValueKind.String
            ? Token(operation.GetString())
            : "default";

    private static int? TenantId(IDictionary<string, JsonElement>? arguments)
        => TryRequestProperty(arguments, "tenantId", out var value) || TryPolicyTargetProperty(arguments, "tenantId", out value) || TryExplicitTargetProperty(arguments, "tenantId", out value)
            ? value.TryGetInt32(out var tenantId) && tenantId > 0 ? tenantId : null
            : null;

    private static Guid? AgentId(IDictionary<string, JsonElement>? arguments)
        => TryRequestProperty(arguments, "agentId", out var value) || TryPolicyTargetProperty(arguments, "agentId", out value) || TryExplicitTargetProperty(arguments, "agentId", out value)
            ? value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var agentId) && agentId != Guid.Empty
                ? agentId
                : null
            : null;

    private static string Token(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? "unknown" : value;

    private static bool TryRequestProperty(IDictionary<string, JsonElement>? arguments, string name, out JsonElement value)
    {
        if (arguments is not null && arguments.TryGetValue("request", out var request) && request.ValueKind == JsonValueKind.Object &&
            request.TryGetProperty(name, out value))
            return true;

        value = default;
        return false;
    }

    private static bool TryPolicyTargetProperty(IDictionary<string, JsonElement>? arguments, string name, out JsonElement value)
    {
        if (arguments is not null && arguments.TryGetValue("request", out var request) && request.ValueKind == JsonValueKind.Object &&
            request.TryGetProperty("policy", out var policy) && policy.ValueKind == JsonValueKind.Object &&
            policy.TryGetProperty("targetSelector", out var target) && target.ValueKind == JsonValueKind.Object &&
            target.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryExplicitTargetProperty(IDictionary<string, JsonElement>? arguments, string name, out JsonElement value)
    {
        if (arguments is not null && arguments.TryGetValue("request", out var request) && request.ValueKind == JsonValueKind.Object &&
            request.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object &&
            target.TryGetProperty(name, out value))
        {
            return true;
        }

        value = default;
        return false;
    }

    private sealed class EmptyLease : IDisposable
    {
        public static EmptyLease Instance { get; } = new();

        public void Dispose()
        {
        }
    }

    private sealed record McpLocalExecutionResponse(string Assertion);
}
