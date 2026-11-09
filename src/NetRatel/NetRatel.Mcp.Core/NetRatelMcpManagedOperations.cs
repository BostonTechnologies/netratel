using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using NetRatel.Shared.Operations;

namespace NetRatel.Mcp.Core;

public sealed partial class NetRatelMcpOperationalTools
{
    [McpServerTool(UseStructuredContent = true), Description("Read tenant-scoped Windows/Linux software inventory, policy, status, scan_status, snapshot, installations, aggregate and changes. Reads use cached evidence and never scan. Resolve tenantId and agentId first. refresh requires client.manage, explicit confirm:true and data.force; poll status/scan_status for completion, including offline queued work. set_policy requires tenant.admin and confirm:true. Query data supports bounded pagination and name, version, publisher, OS, source, client and freshness/status filters.")]
    public Task<NetRatelToolResponse> netratel_software(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default) =>
        ManagedOperationAsync("netratel_software", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Connect a tenant to native Netclaw SignalR and manage durable AI investigations. Inspect connection/clients, create with a stable requestId and explicit client UUIDs, send with a stable messageId, then read get after the last event sequence actually received until reaching lastSequence. Only respond to an actual pendingApproval using its exact callId and selectedKey. Never automatically approve daemon requests. DeliveryUnknown requires reconcile, never automatic resend. Invocations require client.manage, telemetry.read and secret.use; connection changes require tenant.admin. Mutations require confirm:true.")]
    public Task<NetRatelToolResponse> netratel_ai(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default) =>
        ManagedOperationAsync("netratel_ai", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Inspect cached/current Windows and systemd services, request a fresh inventory, and control a service. Use exact tenantId, agentId and inventory serviceName. control data contains stable operationId, platform, action Start/Stop/Restart and reason. Read operation until terminal; dispatch acceptance is not success. Mutations require confirm:true.")]
    public Task<NetRatelToolResponse> netratel_services(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default) =>
        ManagedOperationAsync("netratel_services", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read tenant monitoring configuration, groups, rules, alert series and events. Save groups/rules with current expectedConfigurationRevision and resource revision 1 for new or current+1 for edits. Evaluation changes require incremented evaluationRevision and explicit resetPolicy. Inspect schema and preview targets before saving. A publishedFlowVersionId binds an approved flow and additionally requires flow.read and flow.execute; all clients additionally requires monitoring.targets.all. For a series with all-zero agentId and resourceKey clients:offline, use tenant-rule acknowledge_offline or clear_offline with ruleId and typed occurrence data, without an agentId/resource override. Other series use acknowledge/clear with their exact nonempty agentId and resourceKey. Mutations require confirm:true and their independent permissions." + NetRatel.Shared.Contracts.Monitoring.MonitoringAuthoringGuidance.Text)]
    public Task<NetRatelToolResponse> netratel_monitoring(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default) =>
        ManagedOperationAsync("netratel_monitoring", operation, request, confirm, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create and edit inert tenant flow drafts. Read template and connectors, create, edit graph with save_draft and current expectedRevision, validate/dry_run, then explicitly publish. Bind the resulting immutable version through a monitoring rule to execute on alerts; inspect runs and per-action receipts. Published actions can notify Discord/Slack, open RatelDesk incidents or start Netclaw investigations. Mutations require confirm:true and their distinct flow.edit/flow.publish permissions. Delete requires explicit user deletion intent, exact discovered tenant/flow/revision, current delegated flow.delete, Write scope and an allowing destructive AutomationWrite policy. Omit/false confirm for a metadata-only preview; confirm:true rechecks authority. Deletion works across graph generations, stops future processing and retains history under existing retention. Never guess IDs, perform automatic/bulk cleanup, or silently retry a newer revision. After transport uncertainty reconcile the exact same intent via preview before retrying.")]
    public Task<NetRatelToolResponse> netratel_flows(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default) =>
        ManagedOperationAsync("netratel_flows", operation, request, confirm, cancellationToken);

    private async Task<NetRatelToolResponse> ManagedOperationAsync(string tool, string operation,
        JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        var descriptor = ManagedOperationCatalog.Find(tool, operation);
        if (descriptor is null)
            return Unsupported(tool, operation,
                ManagedOperationCatalog.Operations.Where(item => item.Tool == tool).Select(item => item.Operation).ToArray());
        if (request is not { } payload)
            return Invalid("request is required; resolve tenant and client identifiers first.");
        if (!ManagedOperationCatalog.TryValidate(descriptor, payload, out var error))
            return Invalid(error!);
        var serializedRequest = JsonNode.Parse(payload.GetRawText());
        if (Encoding.UTF8.GetByteCount(serializedRequest!.ToJsonString()) > ManagedOperationCatalog.MaximumRequestBytes)
            return Invalid("The serialized request exceeds the managed operation size limit.");
        var body = new JsonObject
        {
            ["request"] = serializedRequest,
            ["confirm"] = confirm
        };
        var response = await ExecuteAsync(tool, operation, () => client.SendAsync(HttpMethod.Post,
            $"/api/v2/mcp/operator/application/{tool}/{operation}", body, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (tool == "netratel_flows" && operation == "delete" && confirm)
        {
            var target = new JsonObject
            {
                ["tenantId"] = payload.GetProperty("tenantId").GetInt32(),
                ["flowId"] = payload.GetProperty("flowId").GetString(),
                ["expectedRevision"] = payload.GetProperty("data").GetProperty("expectedRevision").GetInt64()
            };
            var returned = response.Data as JsonObject;
            var identityMatches = returned?["tenantId"]?.ToString() == target["tenantId"]!.ToString() &&
                Guid.TryParse(returned?["flowId"]?.ToString(), out var returnedId) && returnedId == payload.GetProperty("flowId").GetGuid();
            var committed = returned?["disposition"]?.ToString() is "0" or "1" or "Deleted" or "AlreadyDeleted";
            var revisionMatches = long.TryParse(returned?["revision"]?.ToString(), out var revision) &&
                revision > 0 && target["expectedRevision"]!.GetValue<long>() < long.MaxValue && revision == target["expectedRevision"]!.GetValue<long>() + 1;
            if (response.Success && (!identityMatches || !committed || !revisionMatches) ||
                !response.Success && response.Error?.UpstreamStatus is null or 0 or >= 500)
            {
                target["requiresReconciliation"] = true; target["automaticRetry"] = false;
                return (response with { Success = false, Status = "uncertain", Data = target, FailureContext = null,
                    Summary = "Deletion was not confirmed. Reconcile this exact tenant, flow and pre-delete revision through an authorized preview before retrying. Never substitute a newer revision.",
                    Error = new NetRatelToolError("flow_delete_transport_uncertain", false, response.Error?.UpstreamStatus) })
                    .WithFailureContext(tool, operation, target);
            }
        }
        if (!response.Success || !descriptor.Mutation || confirm)
            return response;
        if (response.Data?["requiresConfirm"] is not JsonValue value || !value.TryGetValue<bool>(out var requiresConfirm) || !requiresConfirm)
            return new(false, "failed", "The API did not return the expected authorized preview. Inspect current state before trying again.",
                Error: new NetRatelToolError("invalid_api_response", false));
        return Confirmation(operation,
            $"Current permissions and MCP policy admit {tool}/{operation}. Review the arguments and repeat the unchanged request with confirm: true. No business state was changed.",
            descriptor.Tool == "netratel_flows" && descriptor.Operation == "delete"
                ? [payload.GetProperty("tenantId").ToString(), payload.GetProperty("flowId").GetString()!]
                : descriptor.AgentTarget ? [payload.GetProperty("agentId").GetString()!] : [payload.GetProperty("tenantId").ToString()])
            with { Data = response.Data };
    }
}
