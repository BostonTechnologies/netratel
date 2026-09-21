using System.Diagnostics;
using NetRatel.Mcp.Core;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Http;

/// <summary>Creates the gateway-only proof presented to the local API exchange.</summary>
public sealed class McpLocalCredentialPairingService(
    McpOperatorDelegationTokenService tokens,
    NetRatelMcpHostContext hostContext,
    NetRatelMcpHttpOptions options)
{
    private static readonly McpOperatorDelegationIdentity GatewayIdentity = new(
        "netratel-local-gateway", "netratel-local-gateway", null, [], [], []);

    public string CreateAuthenticationProof() => tokens.Create(GatewayIdentity, new McpOperatorDelegationRequest(
        "netratel_gateway", "authenticate", Guid.NewGuid().ToString("N"), options.PublicResourceUri.TrimEnd('/'), hostContext.Target.Instance,
        CorrelationId: Activity.Current?.TraceId.ToString()));

    public string CreateExecutionProof(string tool, string operation, int? tenantId, Guid? agentId, string? objectReference = null, bool objectTargetResolutionEnabled = false) => tokens.Create(
        GatewayIdentity,
        new McpOperatorDelegationRequest(
            tool,
            operation,
            Guid.NewGuid().ToString("N"),
            options.PublicResourceUri.TrimEnd('/'),
            hostContext.Target.Instance,
            tenantId,
            agentId,
            Activity.Current?.TraceId.ToString())
        {
            ObjectReference = objectReference,
            ObjectTargetResolutionEnabled = objectTargetResolutionEnabled
        });
}
