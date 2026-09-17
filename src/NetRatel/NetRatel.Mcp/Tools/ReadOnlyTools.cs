using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using NetRatel.AgentClient;

namespace NetRatel.Mcp.Tools;

public sealed record NetRatelToolError(string Code, bool Retryable, int? UpstreamStatus = null, IReadOnlyList<string>? AllowedOperations = null);
public sealed record NetRatelConfirmation(string ConfirmField, bool RequiredValue, string Operation, IReadOnlyList<string> AffectedIds);
public sealed record NetRatelToolResponse(bool Success, string Status, string Summary, JsonNode? Data = null, IReadOnlyList<string>? AffectedIds = null, NetRatelToolError? Error = null, bool RequiresConfirmation = false, NetRatelConfirmation? Confirmation = null);

[McpServerToolType]
public sealed class ReadOnlyTools(INetRatelAgentClient client, AgentClientConfigurationStore configurationStore)
{
    public Task<NetRatelToolResponse> netratel_auth(string operation = "status", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("netratel_auth", operation, ["status"], _ => client.GetAsync("/api/v1/auth/ai-agent/status", true, cancellationToken), cancellationToken);

    public Task<NetRatelToolResponse> netratel_health(string operation = "get", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("netratel_health", operation, ["get"], async _ => await client.GetHealthAsync(cancellationToken).ConfigureAwait(false), cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read or safely update local NetRatel MCP configuration. show is redacted; get reads an allowlisted persisted value; set and unset require confirm:true and only permit apiBaseUrl or oidcScope. Authentication endpoint, client identity, username, and passwords are externally managed and cannot be changed or returned.")]
    public Task<NetRatelToolResponse> netratel_config(string operation = "show", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "show") return ExecuteAsync("netratel_config", operation, ["show"], _ => Task.FromResult<JsonNode?>(JsonSerializer.SerializeToNode(AgentClientConfigurationResolver.Redact(client.Configuration))), cancellationToken);
        var payload = ToObject(request);
        var key = payload?["key"]?.ToString();
        if (key is not ("apiBaseUrl" or "oidcScope")) return Task.FromResult(new NetRatelToolResponse(false, "invalid_request", "request.key must be apiBaseUrl or oidcScope.", Error: new NetRatelToolError("validation_error", false)));
        if (operation == "get")
        {
            var persistedValue = key == "apiBaseUrl" ? configurationStore.Load().ApiBaseUrl : configurationStore.Load().OidcScope;
            return Task.FromResult(new NetRatelToolResponse(true, "completed", $"Persisted {key} read.", new JsonObject { ["key"] = key, ["value"] = persistedValue }, Array.Empty<string>()));
        }
        if (operation is not ("set" or "unset")) return InvalidOperationAsync("netratel_config", operation, ["show", "get", "set", "unset"]);
        if (operation == "set" && string.IsNullOrWhiteSpace(payload?["value"]?.ToString())) return Task.FromResult(new NetRatelToolResponse(false, "invalid_request", "request.value is required for set.", Error: new NetRatelToolError("validation_error", false)));
        if (operation == "set" && key == "apiBaseUrl" && !IsHttpUrl(payload?["value"]?.ToString())) return Task.FromResult(new NetRatelToolResponse(false, "invalid_request", "request.value must be an absolute http or https API URL.", Error: new NetRatelToolError("validation_error", false)));
        var confirmation = Confirmation(operation, confirm, [key], $"This operation would {operation} persisted NetRatel configuration key {key}. It takes effect when configuration is reloaded.");
        if (confirmation is not null) return Task.FromResult(confirmation);
        var current = configurationStore.Load();
        var value = operation == "unset" ? null : payload?["value"]?.ToString();
        configurationStore.Save(key == "apiBaseUrl" ? current with { ApiBaseUrl = value } : current with { OidcScope = value });
        return Task.FromResult(new NetRatelToolResponse(true, "completed", $"Persisted NetRatel configuration {operation} completed.", new JsonObject { ["key"] = key, ["value"] = value, ["reloadRequired"] = true }, [key]));
    }

    public Task<NetRatelToolResponse> netratel_system(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var path = operation switch
        {
            "version" => "/api/v1/system/version",
            _ => null
        };
        return path is null
            ? InvalidOperationAsync("netratel_system", operation, ["version"])
            : ExecuteAsync("netratel_system", operation, [operation], _ => client.GetAsync(path, true, cancellationToken), cancellationToken);
    }

    public Task<NetRatelToolResponse> netratel_capabilities(string operation = "get", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("netratel_capabilities", operation, ["get"], _ => Task.FromResult<JsonNode?>(Capabilities()), cancellationToken);

    [Description("Legacy source-backed client-read compatibility implementation. Discovery uses NetRatel.Mcp.Core; retired V1 client CRUD, logs, and latency routes remain unavailable pending a V2 identity-mapping contract.")]
    public async Task<NetRatelToolResponse> netratel_clients(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        if (operation == "update_attempts")
        {
            var agentId = payload?["clientIdentity"]?.ToString();
            var suffix = agentId is null ? string.Empty : $"?clientIdentity={Uri.EscapeDataString(agentId)}";
            return await ExecuteAsync("netratel_clients", operation, [operation], _ =>
                client.GetAsync($"/api/v1/client-updates/attempts{suffix}", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        var id = RequiredString(request, "clientIdentity");
        if (id is null) return await InvalidRequestAsync("netratel_clients", operation, "request.clientIdentity is required.").ConfigureAwait(false);
        return operation != "telemetry"
            ? await InvalidOperationAsync("netratel_clients", operation, ["telemetry", "update_attempts"]).ConfigureAwait(false)
            : await ExecuteAsync("netratel_clients", operation, [operation], _ => client.GetAsync($"/api/v1/clients/{Uri.EscapeDataString(id)}/telemetry", true, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    [Description("Legacy job-definition read compatibility implementation. Discovery uses NetRatel.Mcp.Core; writes remain unavailable until the API can verify Development targets.")]
    public async Task<NetRatelToolResponse> netratel_jobs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        if (operation == "list") return await ExecuteAsync("netratel_jobs", operation, ["list"], _ => client.GetJobsAsync(payload, cancellationToken), cancellationToken).ConfigureAwait(false);
        var id = RequiredString(request, "jobId");
        var suffix = operation switch { "details" => "/details", "params" => "/params", "steps" => "/steps", "get" => string.Empty, _ => null };
        return suffix is null
            ? await InvalidOperationAsync("netratel_jobs", operation, ["list", "get", "details", "params", "steps"]).ConfigureAwait(false)
            : id is null
                ? await InvalidRequestAsync("netratel_jobs", operation, "request.jobId is required.").ConfigureAwait(false)
                : await ExecuteAsync("netratel_jobs", operation, [operation], _ => client.GetAsync($"/api/v1/jobs/{Uri.EscapeDataString(id)}{suffix}", true, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    [Description("Legacy job-run read compatibility implementation. Discovery uses NetRatel.Mcp.Core; lifecycle mutations remain unavailable until the API proves a Development target and caller audit.")]
    public async Task<NetRatelToolResponse> netratel_job_runs(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var payload = ToObject(request);
        if (operation == "list") return await ExecuteAsync("netratel_job_runs", operation, ["list"], _ => client.GetJobRunsAsync(payload, cancellationToken), cancellationToken).ConfigureAwait(false);
        var id = RequiredString(request, "jobRunId");
        if (operation == "get") return id is null ? await InvalidRequestAsync("netratel_job_runs", operation, "request.jobRunId is required.").ConfigureAwait(false) : await ExecuteAsync("netratel_job_runs", operation, ["get"], _ => client.GetAsync($"/api/v1/jobruns/{Uri.EscapeDataString(id)}", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        var ordinal = RequiredString(request, "ordinal");
        if (operation == "logs") return id is null || ordinal is null ? await InvalidRequestAsync("netratel_job_runs", operation, "request.jobRunId and request.ordinal are required for logs.").ConfigureAwait(false) : await ExecuteAsync("netratel_job_runs", operation, ["logs"], _ => client.GetAsync($"/api/v1/jobruns/{Uri.EscapeDataString(id)}/steps/{Uri.EscapeDataString(ordinal)}/logs", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await InvalidOperationAsync("netratel_job_runs", operation, ["list", "get", "logs"]).ConfigureAwait(false);
    }

    public Task<NetRatelToolResponse> netratel_search(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        var allowed = new[] { "tenants", "scripts", "jobs", "requests", "clients", "tasks" };
        var query = RequiredString(request, "q");
        var suffix = string.IsNullOrWhiteSpace(query) ? string.Empty : $"?q={Uri.EscapeDataString(query)}";
        return ExecuteAsync("netratel_search", operation, allowed, _ => client.GetAsync($"/api/v1/global-search/{operation}{suffix}", true, cancellationToken), cancellationToken);
    }

    public Task<NetRatelToolResponse> netratel_telemetry(string operation = "overview", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("netratel_telemetry", operation, ["overview"], _ => client.GetAsync("/api/v1/telemetry/overview", true, cancellationToken), cancellationToken);

    public async Task<NetRatelToolResponse> netratel_notifications(string operation, JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
    {
        if (operation == "list") return await ExecuteAsync("netratel_notifications", operation, ["list"], _ => client.GetAsync("/api/v1/notifications", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (operation == "summary") return await ExecuteAsync("netratel_notifications", operation, ["summary"], _ => client.GetAsync("/api/v1/notifications/summary", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (operation == "unread_errors") return await ExecuteAsync("netratel_notifications", operation, ["unread_errors"], _ => client.GetAsync("/api/v1/notifications/unread-errors", true, cancellationToken), cancellationToken).ConfigureAwait(false);
        if (operation == "mark_read")
        {
            var payload = ToObject(request);
            var ids = payload?["ids"] as JsonArray;
            if (ids is null || ids.Count == 0) return new NetRatelToolResponse(false, "invalid_request", "request.ids must contain at least one notification id.", Error: new NetRatelToolError("validation_error", false));
            var affected = ids.Select(x => x?.ToString() ?? string.Empty).Where(x => x.Length > 0).ToArray();
            var confirmation = Confirmation(operation, confirm, affected, $"This operation would mark {affected.Length} notifications as read.");
            if (confirmation is not null) return confirmation;
            try { return new NetRatelToolResponse(true, "completed", "Notifications marked read.", await client.SendAsync(HttpMethod.Post, "/api/v1/notifications/mark-read", payload, true, cancellationToken).ConfigureAwait(false), affected); }
            catch (AgentClientRemoteException ex) { return new NetRatelToolResponse(false, "failed", ex.Message, Error: new NetRatelToolError(ex.Code, ex.StatusCode >= 500, ex.StatusCode)); }
        }
        var id = RequiredString(request, "id");
        return operation != "get" ? await InvalidOperationAsync("netratel_notifications", operation, ["list", "get", "summary", "unread_errors", "mark_read"]).ConfigureAwait(false)
            : id is null ? await InvalidRequestAsync("netratel_notifications", operation, "request.id is required.").ConfigureAwait(false)
            : await ExecuteAsync("netratel_notifications", operation, ["get"], _ => client.GetAsync($"/api/v1/notifications/{Uri.EscapeDataString(id)}", true, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    public Task<NetRatelToolResponse> netratel_logs(string operation = "search", JsonElement? request = null, bool confirm = false, CancellationToken cancellationToken = default)
        => ExecuteAsync("netratel_logs", operation, ["search"], _ => client.GetAsync(WithQuery("/api/v1/ops/ai-agent/logs", ToObject(request)), true, cancellationToken), cancellationToken);

    private static async Task<NetRatelToolResponse> ExecuteAsync(string tool, string operation, IReadOnlyList<string> allowed, Func<CancellationToken, Task<JsonNode?>> action, CancellationToken ct)
    {
        if (!allowed.Contains(operation, StringComparer.Ordinal)) return await InvalidOperationAsync(tool, operation, allowed).ConfigureAwait(false);
        try
        {
            var data = await action(ct).ConfigureAwait(false);
            return new NetRatelToolResponse(true, "completed", $"{tool} {operation} completed.", data, Array.Empty<string>());
        }
        catch (AgentClientRemoteException ex)
        {
            return new NetRatelToolResponse(false, "failed", ex.Message, Error: new NetRatelToolError(ex.Code, ex.StatusCode >= 500, ex.StatusCode));
        }
        catch (AgentClientValidationException ex)
        {
            return new NetRatelToolResponse(false, "invalid_request", ex.Message, Error: new NetRatelToolError("validation_error", false));
        }
    }

    private static Task<NetRatelToolResponse> InvalidOperationAsync(string tool, string operation, IReadOnlyList<string> allowed) => Task.FromResult(new NetRatelToolResponse(false, "invalid_request", $"Unsupported {tool} operation '{operation}'.", Error: new NetRatelToolError("unsupported_operation", false, AllowedOperations: allowed)));
    private static Task<NetRatelToolResponse> InvalidRequestAsync(string tool, string operation, string summary) => Task.FromResult(new NetRatelToolResponse(false, "invalid_request", summary, Error: new NetRatelToolError("validation_error", false)));
    private static string? RequiredString(JsonElement? request, string property) => request is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
    private static JsonObject? ToObject(JsonElement? request) => request is { ValueKind: JsonValueKind.Object } value ? JsonNode.Parse(value.GetRawText())?.AsObject() : null;
    private static string WithQuery(string path, JsonObject? values)
    {
        if (values is null || values.Count == 0) return path;
        var query = values.Where(x => x.Value is not null).Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!.ToString())}");
        return string.Join('?', path, string.Join('&', query));
    }

    private static bool IsHttpUrl(string? value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http";

    private static NetRatelToolResponse? Confirmation(string operation, bool confirm, IReadOnlyList<string> affectedIds, string summary)
    {
        if (confirm) return null;
        var policy = MutationPolicy.Require(operation, summary, affectedIds.ToArray());
        return new NetRatelToolResponse(false, policy.Status, policy.Summary, AffectedIds: affectedIds, RequiresConfirmation: true, Confirmation: new NetRatelConfirmation(policy.Confirmation.ConfirmField, policy.Confirmation.RequiredValue, policy.Confirmation.Operation, policy.Confirmation.AffectedIds));
    }

    private static JsonNode Capabilities() => new JsonObject { ["server"] = "NetRatel.Mcp", ["transport"] = "stdio", ["phase"] = 5, ["tools"] = new JsonArray("netratel_auth", "netratel_health", "netratel_config", "netratel_system", "netratel_capabilities", "netratel_clients", "netratel_jobs", "netratel_job_runs", "netratel_tenants", "netratel_scripts", "netratel_tasks", "netratel_search", "netratel_telemetry", "netratel_notifications", "netratel_logs", "netratel_connectivity", "netratel_events"), ["mutationsEnabled"] = true };
}
