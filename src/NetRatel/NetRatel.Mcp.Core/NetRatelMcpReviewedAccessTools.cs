using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.Mcp.Core;

public sealed partial class NetRatelMcpOperationalTools
{
    private Task<NetRatelToolResponse> ReviewedAccessAsync(string operation, JsonElement? request, CancellationToken ct)
    {
        if (operation == "requests")
            return request is null ? GetAsync("netratel_access", operation, AccessOperations, "/api/v2/mcp/operator/access/requests", ct) : Task.FromResult(Invalid("requests does not accept request fields."));
        if (request is null || request.Value.ValueKind != JsonValueKind.Object) return Task.FromResult(Invalid("A bounded request object is required."));
        var value = request.Value;
        if (operation == "credential")
        {
            if (!value.TryGetProperty("credentialId", out var id) || id.ValueKind != JsonValueKind.String || !Guid.TryParse(id.GetString(), out var guid) || guid == Guid.Empty)
                return Task.FromResult(Invalid("credentialId must identify your own integration credential."));
            return GetAsync("netratel_access", operation, AccessOperations, "/api/v2/mcp/operator/access/credentials/" + guid.ToString("N"), ct);
        }
        if (!value.TryGetProperty("requestId", out var requestId) || requestId.ValueKind != JsonValueKind.String || !Guid.TryParse(requestId.GetString(), out var requestGuid) || requestGuid == Guid.Empty)
            return Task.FromResult(Invalid("requestId must be your saved non-secret request UUID."));
        var path = operation == "request" ? "/api/v2/mcp/operator/access/requests" : "/api/v2/mcp/operator/access/" + operation;
        return ExecuteAsync("netratel_access", operation, () => client.SendAsync(HttpMethod.Post, path, JsonNode.Parse(value.GetRawText()), ct), ct);
    }
}

public static partial class NetRatelMcpToolDefinitions
{
    private static RequestDefinition ReviewedAccessProposalSchema() => RequiredRequest([
        Uuid("requestId", "Durable caller-generated idempotency key. Reuse for resume; changed proposals require a new key.", true),
        RequiredString("name", "Reviewed credential name.", 128),
        RequiredEnum("action", "Reconcile or revoke an owned credential. New credential creation is prepared by the protected setup client.", "create", "reconcile", "revoke"),
        Uuid("credentialId", "Owned HTTP MCP credential identifier; omit only for a protected prepared create request.", false),
        ("expiresAtUtc", new JsonObject { ["type"] = "string", ["format"] = "date-time", ["pattern"] = "(Z|[+]00:00)$", ["maxLength"] = 40 }, true),
        ("grants", new JsonObject { ["type"] = "array", ["maxItems"] = 64, ["items"] = RequiredRequest([
            RequiredInteger("tenantId", "Exact positive tenant identifier.", 1, int.MaxValue), RequiredString("permission", "Exact application permission.", 128)
        ]).Schema }, true),
        ("instancePermissions", new JsonObject { ["type"] = "array", ["maxItems"] = 32, ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 } }, true),
        ("targets", new JsonObject { ["type"] = "array", ["maxItems"] = 32, ["items"] = RequiredRequest([
            RequiredInteger("tenantId", "Exact tenant; zero only for a catalogued control-plane operation.", 0, int.MaxValue),
            RequiredString("tool", "Exact catalogued tool.", 128), RequiredString("operation", "Exact catalogued operation.", 128),
            Uuid("agentId", "Exact persisted agent when the operation targets an agent.", false),
            ("profileRevision", new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 9007199254740991L, ["description"] = "Required current target-profile revision for an agent target." }, false)
        ]).Schema }, true)
    ]);
}
