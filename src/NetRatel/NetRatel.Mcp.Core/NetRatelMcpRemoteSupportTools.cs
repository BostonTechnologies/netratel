using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace NetRatel.Mcp.Core;

public sealed partial class NetRatelMcpOperationalTools
{
    [McpServerTool(UseStructuredContent = true), Description("Inspect exact-target Remote Support V2 presence, capabilities and inventory. Refresh requires an unchanged preview/confirmation; inspect subsequent inventory freshness and sequence to verify completion.")]
    public Task<NetRatelToolResponse> netratel_remote_support_v2(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default)
    {
        const string tool = "netratel_remote_support_v2";
        string[] operations = ["presence", "capabilities", "inventory", "refresh_inventory"];
        if (!operations.Contains(operation, StringComparer.Ordinal))
            return Task.FromResult(Unsupported(tool, operation, operations));
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId) ||
            !ContainsOnly(payload, "tenantId", "agentId", "planToken", "idempotencyKey"))
            return Task.FromResult(Invalid("A V2 operator host and exact tenantId and agentId are required."));
        var root = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/remote-support";
        if (operation != "refresh_inventory")
        {
            if (!ContainsOnly(payload, "tenantId", "agentId"))
                return Task.FromResult(Invalid("Remote Support reads accept only tenantId and agentId."));
            return GetAsync(tool, operation, operations, $"{root}/{operation}", cancellationToken);
        }
        if (!confirm)
        {
            if (!ContainsOnly(payload, "tenantId", "agentId"))
                return Task.FromResult(Invalid("Refresh preview accepts only tenantId and agentId."));
            return ExecuteAsync(tool, operation, () => client.SendAsync(HttpMethod.Post,
                $"{root}/refresh-inventory/preview", null, cancellationToken), cancellationToken);
        }
        if (!TryRequiredString(payload, "planToken", 128, out var planToken) ||
            !TryRequiredString(payload, "idempotencyKey", 128, out var idempotencyKey) ||
            !IsOpaqueCredential(planToken) || !IsOpaqueCredential(idempotencyKey))
            return Task.FromResult(Invalid("Refresh confirmation requires the opaque credentials from its preview."));
        var body = new JsonObject { ["planToken"] = planToken, ["idempotencyKey"] = idempotencyKey };
        return ExecuteAsync(tool, operation, () => client.SendAsync(HttpMethod.Post,
            $"{root}/refresh-inventory/confirm", body, cancellationToken), cancellationToken);
    }
}
