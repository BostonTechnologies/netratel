using System.Text.Json;
using System.Text.Json.Nodes;
using NetRatel.Shared.Contracts.SoftwareInventory;
using NetRatel.Shared.Operations;

namespace NetRatel.AgentClient;

/// <summary>Common inventory client; the same managed API admission is used by HTTP and stdio MCP.</summary>
public sealed class SoftwareInventoryClient(INetRatelAgentClient client)
{
    public Task<JsonNode?> PolicyAsync(int tenant, CancellationToken ct = default) => InvokeAsync("policy", tenant, null, null, false, ct);
    public Task<JsonNode?> SetPolicyAsync(int tenant, SoftwareInventoryPolicy policy, bool confirm = false, CancellationToken ct = default)
    { policy.Validate(); return InvokeAsync("set_policy", tenant, null, policy, confirm, ct); }
    public Task<JsonNode?> StatusAsync(int tenant, Guid agent, CancellationToken ct = default) => InvokeAsync("status", tenant, agent, null, false, ct);
    public Task<JsonNode?> SnapshotAsync(int tenant, Guid agent, SoftwareInventoryQuery? query = null, CancellationToken ct = default) => InvokeAsync("snapshot", tenant, agent, query ?? new(), false, ct);
    public Task<JsonNode?> RefreshAsync(int tenant, Guid agent, bool force = true, bool confirm = false, CancellationToken ct = default) => InvokeAsync("refresh", tenant, agent, new SoftwareRefreshRequest(force), confirm, ct);
    public Task<JsonNode?> InstallationsAsync(int tenant, SoftwareInventoryQuery? query = null, CancellationToken ct = default) => InvokeAsync("installations", tenant, null, query ?? new(), false, ct);
    public Task<JsonNode?> AggregateAsync(int tenant, SoftwareInventoryQuery? query = null, CancellationToken ct = default) => InvokeAsync("aggregate", tenant, null, query ?? new(), false, ct);
    public Task<JsonNode?> ChangesAsync(int tenant, Guid? agent = null, SoftwareInventoryQuery? query = null, CancellationToken ct = default) => InvokeAsync(agent.HasValue ? "changes" : "estate_changes", tenant, agent, query ?? new(), false, ct);
    public Task<JsonNode?> ScanStatusAsync(int tenant, Guid agent, Guid collectionId, CancellationToken ct = default) => InvokeAsync("scan_status", tenant, agent, null, false, ct, collectionId);

    private Task<JsonNode?> InvokeAsync(string operation, int tenant, Guid? agent, object? data, bool confirm, CancellationToken ct, Guid? collectionId = null)
    {
        if (data is SoftwareInventoryQuery query) query.Validate();
        var request = new JsonObject { ["tenantId"] = tenant };
        if (agent.HasValue) request["agentId"] = agent.Value.ToString("D");
        if (collectionId.HasValue) request["collectionId"] = collectionId.Value.ToString("D");
        if (data is not null) request["data"] = JsonSerializer.SerializeToNode(data, data.GetType(), ManagedOperationCatalog.JsonOptions);
        var descriptor = ManagedOperationCatalog.Find("netratel_software", operation)!;
        if (!ManagedOperationCatalog.TryValidate(descriptor, JsonSerializer.SerializeToElement(request), out var error))
            throw new AgentClientValidationException(error!);
        return client.SendAsync(HttpMethod.Post, $"/api/v2/mcp/operator/application/netratel_software/{operation}",
            new JsonObject { ["request"] = request, ["confirm"] = confirm }, ct: ct);
    }
}
