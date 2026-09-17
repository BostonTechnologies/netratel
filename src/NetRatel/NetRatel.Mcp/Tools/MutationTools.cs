using System.ComponentModel;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;

namespace NetRatel.Mcp.Tools;

[McpServerToolType]
public sealed class MutationTools(INetRatelAgentClient client)
{
    [McpServerTool(UseStructuredContent = true), Description("Inspect the current V2 gateway presence and Remote Support V2 capability/inventory projections. presence supports optional tenantId, search, and online filters. capabilities and inventory require tenantId and agentId. refresh_inventory requests a new client-side V2 projection and requires confirm: true. Lifecycle/media mutations are deliberately not exposed by this diagnostic tool.")]
    public async Task<NetRatelToolResponse> netratel_remote_support_v2(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        if (operation == "presence")
        {
            var query = new List<string>();
            foreach (var name in new[] { "tenantId", "search", "online" })
            {
                var value = ReadId(payload, name);
                if (!string.IsNullOrWhiteSpace(value)) query.Add($"{name}={Uri.EscapeDataString(value)}");
            }

            var path = "/api/v2/client-presence/" + (query.Count == 0 ? string.Empty : $"?{string.Join('&', query)}");
            return await ReadAsync(operation, () => client.GetAsync(path, true, cancellationToken)).ConfigureAwait(false);
        }

        var tenantId = ReadId(payload, "tenantId");
        var agentId = ReadId(payload, "agentId");
        if (!int.TryParse(tenantId, out var tenant) || tenant <= 0)
            return Invalid(operation, "request.tenantId must be a positive integer.");
        if (!Guid.TryParse(agentId, out var agent))
            return Invalid(operation, "request.agentId must be a GUID.");

        var root = $"/api/v2/agents/{tenant}/{agent:D}/remote-support/v2";
        if (operation is "capabilities" or "inventory")
            return await ReadAsync(operation, () => client.GetAsync($"{root}/{(operation == "capabilities" ? "capabilities" : "inventory")}", true, cancellationToken)).ConfigureAwait(false);
        if (operation != "refresh_inventory")
            return Invalid(operation, "Supported operations are presence, capabilities, inventory, and refresh_inventory.");

        var affected = new[] { $"{tenant}/{agent:D}" };
        var confirmation = Confirm(operation, confirm, affected, $"This operation would request a fresh Remote Support V2 capability and target inventory projection for agent {agent:D}.");
        if (confirmation is not null) return confirmation;
        try
        {
            var data = await client.SendAsync(HttpMethod.Post, $"{root}/inventory/refresh", null, true, cancellationToken).ConfigureAwait(false);
            return new NetRatelToolResponse(true, "accepted", "Remote Support V2 inventory refresh requested.", data, affected);
        }
        catch (AgentClientRemoteException ex)
        {
            return new NetRatelToolResponse(false, "failed", ex.Message, Error: new NetRatelToolError(ex.Code, ex.StatusCode >= 500, ex.StatusCode));
        }
    }

    public async Task<NetRatelToolResponse> netratel_connectivity(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "settings") return await ReadAsync(operation, () => client.GetAsync("/api/v1/admin/connectivity/settings", true, cancellationToken)).ConfigureAwait(false);
        if (operation == "netratel") return await ReadAsync(operation, () => client.GetAsync("/api/v1/admin/orchestration/netratel", true, cancellationToken)).ConfigureAwait(false);
        return Invalid(operation, "Supported operations are settings and netratel.");
    }

    public async Task<NetRatelToolResponse> netratel_events(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        if (operation == "list") return await ReadAsync(operation, () => client.GetAsync("/api/v1/events", true, cancellationToken)).ConfigureAwait(false);
        var id = ReadId(payload, "eventId");
        if (operation == "get") return id is null ? Invalid(operation, "request.eventId is required.") : await ReadAsync(operation, () => client.GetAsync($"/api/v1/events/{Uri.EscapeDataString(id)}", true, cancellationToken)).ConfigureAwait(false);
        return Invalid(operation, "Supported operations are list and get.");
    }

    private static async Task<NetRatelToolResponse> ReadAsync(string operation, Func<Task<JsonNode?>> action)
    {
        try { return new NetRatelToolResponse(true, "completed", $"netratel_tenants {operation} completed.", await action().ConfigureAwait(false)); }
        catch (AgentClientRemoteException ex) { return new NetRatelToolResponse(false, "failed", ex.Message, Error: new NetRatelToolError(ex.Code, ex.StatusCode >= 500, ex.StatusCode)); }
    }

    private static NetRatelToolResponse Invalid(string operation, string summary) => new(false, "invalid_request", summary, Error: new NetRatelToolError("validation_error", false));
    private static NetRatelToolResponse? Confirm(string operation, bool confirm, IReadOnlyList<string> affectedIds, string summary)
    {
        if (confirm) return null;
        var policy = MutationPolicy.Require(operation, summary, affectedIds.ToArray());
        return new NetRatelToolResponse(policy.Success, policy.Status, policy.Summary, AffectedIds: affectedIds, RequiresConfirmation: true, Confirmation: new NetRatelConfirmation(policy.Confirmation.ConfirmField, policy.Confirmation.RequiredValue, policy.Confirmation.Operation, policy.Confirmation.AffectedIds));
    }

    private static JsonObject? ToObject(JsonElement? request) => request is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText())?.AsObject() : null;
    private static string? ReadId(JsonObject? request, string name) => request?[name]?.ToString();
}
