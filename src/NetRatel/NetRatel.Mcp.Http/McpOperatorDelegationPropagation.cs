using System.Security.Claims;
using System.Diagnostics;
using System.Text.Json;
using System.Net.Http.Headers;
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
        => Begin(CreateAssertion(user, tool, arguments));

    // Enter synchronously in the tool caller's execution context, after any
    // asynchronous exchange has completed. AsyncLocal assignments made inside
    // an awaited preparation method do not flow back to its caller.
    public IDisposable Begin(string? assertion)
        => assertion is null ? EmptyLease.Instance : context.Begin(assertion);

    private string? CreateAssertion(ClaimsPrincipal? user, string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (!options.Enabled)
            return null;

        if (user?.Identity?.IsAuthenticated is not true)
            throw new InvalidOperationException("An authenticated MCP caller is required for delegated operator identity.");

        var operation = Operation(arguments);
        var target = McpOperationTargetCatalog.Find(Token(tool), operation);
        var targetModel = target?.Model;
        return tokens.Create(
            IdentityFrom(user),
            new McpOperatorDelegationRequest(
                Token(tool),
                operation,
                Guid.NewGuid().ToString("N"),
                httpOptions.PublicResourceUri.TrimEnd('/'),
                hostContext.Target.Instance,
                TenantIdFor(targetModel, arguments),
                AgentIdFor(targetModel, arguments),
                Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"))
            {
                ObjectReference = hostContext.OperatorSurfaceEnabled ? ObjectReferenceFor(target, arguments) : null,
                ObjectTargetResolutionEnabled = hostContext.OperatorSurfaceEnabled,
                ReplayBinding = ReplayBindingFor(tool, operation, arguments)
            },
            issuedAtUtc: null);
    }

    /// <summary>
    /// Exchanges a local HTTP-MCP bearer for a one-operation delegation. The
    /// raw bearer remains confined to the dedicated exchange endpoint; the
    /// normal outbound API client receives only the returned assertion plus
    /// its existing gateway credential.
    /// </summary>
    public async Task<string?> CreateAssertionAsync(
        ClaimsPrincipal? user,
        string tool,
        IDictionary<string, JsonElement>? arguments,
        CancellationToken cancellationToken)
    {
        if (!httpOptions.LocalCredentialMode)
            return CreateAssertion(user, tool, arguments);
        if (user?.Identity?.IsAuthenticated is not true || httpContextAccessor?.HttpContext is not { } http || httpClients is null || localPairing is null)
            throw new InvalidOperationException("A local HTTP MCP credential exchange requires the active request context.");

        var authorization = http.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer nrt_ic_", StringComparison.Ordinal))
            throw new InvalidOperationException("A local HTTP MCP credential is required.");

        var operation = Operation(arguments);
        var target = McpOperationTargetCatalog.Find(Token(tool), operation);
        var correlationId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v2/mcp/local-delegation/exchange");
        request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        request.Headers.TryAddWithoutValidation("X-NetRatel-Mcp-Pairing", localPairing.CreateExecutionProof(
            tool,
            operation,
            TenantIdFor(target?.Model, arguments),
            AgentIdFor(target?.Model, arguments),
            hostContext.OperatorSurfaceEnabled ? ObjectReferenceFor(target, arguments) : null,
            hostContext.OperatorSurfaceEnabled,
            correlationId,
            ReplayBindingFor(tool, operation, arguments)));
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            using var response = await httpClients.CreateClient(McpLocalCredentialAuthenticationHandler.ApiHttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var failure = await McpLocalCredentialExchangeFailure.FromResponseAsync(response, deadline.Token).ConfigureAwait(false);
                throw new McpLocalCredentialExchangeException(failure with { CorrelationId = failure.CorrelationId ?? correlationId });
            }

            var execution = await McpLocalCredentialExchangeResponse
                .ReadJsonAsync<McpLocalExecutionResponse>(response.Content, deadline.Token).ConfigureAwait(false);
            if (execution is null || string.IsNullOrWhiteSpace(execution.Assertion))
                throw new McpLocalCredentialExchangeException(McpLocalCredentialExchangeFailure.ProtocolError with { CorrelationId = correlationId });
            return execution.Assertion;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new McpLocalCredentialExchangeException(McpLocalCredentialExchangeFailure.TimedOut with { CorrelationId = correlationId });
        }
        catch (HttpRequestException)
        {
            throw new McpLocalCredentialExchangeException(McpLocalCredentialExchangeFailure.DependencyUnavailable with { CorrelationId = correlationId });
        }
        catch (IOException)
        {
            throw new McpLocalCredentialExchangeException(McpLocalCredentialExchangeFailure.DependencyUnavailable with { CorrelationId = correlationId });
        }
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
            ? NetRatelMcpIdentifiers.TryInt32(value, 1, int.MaxValue, out var tenantId) ? tenantId : null
            : null;

    private static Guid? AgentId(IDictionary<string, JsonElement>? arguments)
        => TryRequestProperty(arguments, "agentId", out var value) || TryPolicyTargetProperty(arguments, "agentId", out value) || TryExplicitTargetProperty(arguments, "agentId", out value)
            ? value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var agentId) && agentId != Guid.Empty
                ? agentId
                : null
            : null;

    private static int? TenantIdFor(McpOperationTargetModel? targetModel, IDictionary<string, JsonElement>? arguments) =>
        targetModel == McpOperationTargetModel.NoBusinessTarget ? null : TenantId(arguments);

    private static Guid? AgentIdFor(McpOperationTargetModel? targetModel, IDictionary<string, JsonElement>? arguments) =>
        targetModel is McpOperationTargetModel.NoBusinessTarget or McpOperationTargetModel.Tenant ? null : AgentId(arguments);

    private static string? ObjectReferenceFor(McpOperationTargetDescriptor? target, IDictionary<string, JsonElement>? arguments)
    {
        if (target?.ObjectReferencePropertyName is not { } propertyName ||
            !TryRequestProperty(arguments, propertyName, out var value))
        {
            return null;
        }

        if (target.ObjectReferenceKind == McpOperationObjectReferenceKind.Policy)
            return value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var policyId) && policyId != Guid.Empty
                ? policyId.ToString("D") : null;

        return NetRatelMcpIdentifiers.TryInt64(value, 1, long.MaxValue, out var number)
            ? number.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
    }

    private static string Token(string? value)
        => string.IsNullOrWhiteSpace(value) || value.Length > 128 || value.Any(char.IsControl) ? "unknown" : value;

    private McpOperatorReplayBinding? ReplayBindingFor(string tool, string operation, IDictionary<string, JsonElement>? arguments) =>
        hostContext.OperatorSurfaceEnabled && arguments is not null &&
        arguments.TryGetValue("confirm", out var confirm) && confirm.ValueKind == JsonValueKind.True &&
        arguments.TryGetValue("request", out var request) &&
        McpOperatorJobConfirmationPayload.TryCreateBinding(tool, operation, request, out var binding) ? binding : null;

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
