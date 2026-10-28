using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NetRatel.Shared.Contracts.ComputerUse;

namespace NetRatel.Mcp.Core;

public sealed partial class NetRatelMcpOperationalTools
{
    private const string ComputerUseTool = "netratel_computer_use";
    private static readonly string[] ComputerUseToolOperations =
    ["capabilities", "list_windows", "open_view", "close_view", "preview_open", "open", "get", "observe", "preview_act", "act", "pause", "resume", "close"];
    private static readonly string[] ComputerUseKeys =
    ["Enter", "Tab", "Escape", "Backspace", "Delete", "Space", "ArrowUp", "ArrowDown", "ArrowLeft", "ArrowRight", "Home", "End", "PageUp", "PageDown"];

    [McpServerTool(UseStructuredContent = true), Description("Bounded, provider-neutral computer use through an exact tenant/client Windows session. Discover a window, explicitly open a read-only or control lease, observe, perform one confirmed action and inspect read-back. Screenshots are MCP image content. Unsupported, stale, refused and unverified outcomes retain their structured status.")]
    public async Task<CallToolResult> netratel_computer_use(string operation, JsonElement? request = null,
        bool confirm = false, CancellationToken cancellationToken = default)
    {
        var response = await ComputerUseAsync(operation, request, confirm, cancellationToken).ConfigureAwait(false);
        return ComputerUseProtocolResult(response);
    }

    private Task<NetRatelToolResponse> ComputerUseAsync(string operation, JsonElement? request,
        bool confirm, CancellationToken cancellationToken)
    {
        if (!ComputerUseToolOperations.Contains(operation, StringComparer.Ordinal))
            return Task.FromResult(Unsupported(ComputerUseTool, operation, ComputerUseToolOperations));
        if (!UsesProductionOperatorRoutes || !TryObject(request, out var payload) || payload is null ||
            !TryRequiredInt(payload, "tenantId", 1, int.MaxValue, out var tenantId) ||
            !TryRequiredGuid(payload, "agentId", out var agentId))
            return Task.FromResult(Invalid("Computer use requires a V2 operator host and exact tenantId and agentId."));

        var root = $"/api/v2/mcp/operator/agents/{tenantId.ToString(CultureInfo.InvariantCulture)}/{agentId:D}/computer-use";
        var body = new JsonObject();
        string path;
        var method = HttpMethod.Post;
        var expectExecutionResult = true;

        if (operation is "capabilities" or "list_windows" or "preview_open" or "open" or "open_view")
        {
            var opensLease = operation is "preview_open" or "open" or "open_view";
            string[] allowed = operation switch
            {
                "preview_open" => ["tenantId", "agentId", "target", "provider", "lifetimeSeconds", "idleTimeoutSeconds"],
                "open" => ["tenantId", "agentId", "target", "provider", "lifetimeSeconds", "idleTimeoutSeconds", "planToken", "idempotencyKey"],
                "open_view" => ["tenantId", "agentId", "target", "provider", "lifetimeSeconds", "idleTimeoutSeconds", "idempotencyKey"],
                _ => ["tenantId", "agentId", "target", "provider"]
            };
            if (!ContainsOnly(payload, allowed) || !TryComputerUseTarget(payload, opensLease, out var target) ||
                !TryRequiredString(payload, "provider", 32, out var provider) || !ComputerUseProviders.IsKnown(provider))
                return Task.FromResult(Invalid("Supply only the exact session/window target and an explicitly supported provider."));
            body["target"] = target;
            body["provider"] = provider;
            if (opensLease)
            {
                if (!TryOptionalInt(payload, "lifetimeSeconds", 30, 300, out var lifetime) ||
                    !TryOptionalInt(payload, "idleTimeoutSeconds", 15, 120, out var idle) || (idle ?? 60) > (lifetime ?? 300))
                    return Task.FromResult(Invalid("Lease lifetime must be 30–300 seconds and idle timeout 15–120 seconds, no longer than the lifetime."));
                body["lifetimeSeconds"] = lifetime ?? 300;
                body["idleTimeoutSeconds"] = idle ?? 60;
            }
            path = operation switch
            {
                "capabilities" => $"{root}/capabilities",
                "list_windows" => $"{root}/windows",
                "preview_open" => $"{root}/sessions/preview",
                "open_view" => $"{root}/sessions/view",
                _ => $"{root}/sessions/confirm"
            };
            expectExecutionResult = operation != "preview_open";
        }
        else
        {
            if (!TryRequiredGuid(payload, "sessionId", out var sessionId))
                return Task.FromResult(Invalid("Supply the caller-owned computer-use sessionId returned by open or open_view."));
            var sessionRoot = $"{root}/sessions/{sessionId:D}";
            switch (operation)
            {
                case "get":
                    if (!ContainsOnly(payload, "tenantId", "agentId", "sessionId"))
                        return Task.FromResult(Invalid("Session get accepts only tenantId, agentId and sessionId."));
                    path = sessionRoot;
                    method = HttpMethod.Get;
                    expectExecutionResult = false;
                    break;
                case "observe":
                    if (!ContainsOnly(payload, "tenantId", "agentId", "sessionId", "includeScreenshot") ||
                        !TryOptionalBoolean(payload, "includeScreenshot", out var includeScreenshot))
                        return Task.FromResult(Invalid("Observe accepts only its lease identifiers and an optional includeScreenshot boolean."));
                    body["includeScreenshot"] = includeScreenshot ?? false;
                    path = $"{sessionRoot}/observe";
                    break;
                case "preview_act":
                case "act":
                    if (!ContainsOnly(payload, operation == "act"
                            ? ["tenantId", "agentId", "sessionId", "observationId", "action", "planToken", "idempotencyKey"]
                            : ["tenantId", "agentId", "sessionId", "observationId", "action"]) ||
                        !TryRequiredString(payload, "observationId", 128, out var observationId) || !IsAccessToken(observationId) ||
                        !TryComputerUseAction(payload, out var action))
                        return Task.FromResult(Invalid("Use one bounded action and the exact fresh observationId from this lease/window."));
                    body["observationId"] = observationId;
                    body["action"] = action;
                    path = $"{sessionRoot}/actions/{(operation == "act" ? "confirm" : "preview")}";
                    expectExecutionResult = operation == "act";
                    break;
                default:
                    if (!ContainsOnly(payload, "tenantId", "agentId", "sessionId", "idempotencyKey"))
                        return Task.FromResult(Invalid("Lease controls accept only tenantId, agentId, sessionId and idempotencyKey."));
                    path = $"{sessionRoot}/{(operation == "close_view" ? "close-view" : operation)}";
                    break;
            }
        }

        if (operation is "open" or "act")
        {
            if (!TryPlanCredentials(payload))
                return Task.FromResult(Invalid("Confirmation requires the unchanged planToken and idempotencyKey from the matching preview."));
            body["planToken"] = payload["planToken"]!.DeepClone();
        }
        if (operation is "open" or "open_view" or "act" or "pause" or "resume" or "close" or "close_view")
        {
            if (!TryRequiredString(payload, "idempotencyKey", 128, out var key) || !IsOpaqueCredential(key))
                return Task.FromResult(Invalid("This lease operation requires a 32–128 character URL-safe idempotencyKey."));
            body["idempotencyKey"] = key;
        }
        if ((operation is "open" or "act" or "pause" or "resume" or "close") && !confirm)
            return Task.FromResult(new NetRatelToolResponse(false, "confirmation_required",
                "Review the exact target and action, then repeat this request with confirm:true.",
                Error: new NetRatelToolError("confirmation_required", false), RequiresConfirmation: true,
                Confirmation: new NetRatelConfirmation("confirm", true, operation, []))
                .WithFailureContext(ComputerUseTool, operation));

        return ExecuteComputerUseAsync(operation, method, path, body, expectExecutionResult, cancellationToken);
    }

    private async Task<NetRatelToolResponse> ExecuteComputerUseAsync(string operation, HttpMethod method, string path,
        JsonObject body, bool expectExecutionResult, CancellationToken cancellationToken)
    {
        var response = await ExecuteAsync(ComputerUseTool, operation,
            () => method == HttpMethod.Get ? client.GetAsync(path, cancellationToken) : client.SendAsync(method, path, body, cancellationToken),
            cancellationToken).ConfigureAwait(false);
        if (!response.Success || !expectExecutionResult)
            return response;

        if (response.Data is not JsonObject envelope || envelope["result"] is not JsonObject result ||
            result["status"] is not JsonValue statusNode || !statusNode.TryGetValue<string>(out var status))
            return response with { Success = false, Status = "failed", Summary = "The endpoint returned no valid computer-use execution outcome.",
                Error = new NetRatelToolError("cua_response_invalid", false) };

        var succeeded = status is ComputerUseStatuses.Completed or ComputerUseStatuses.Observed or ComputerUseStatuses.Verified;
        // An input delivery receipt cannot establish an application's requested effect.
        if (operation == "act" && status != ComputerUseStatuses.Verified)
            succeeded = false;
        var code = result["code"] is JsonValue codeNode && codeNode.TryGetValue<string>(out var returnedCode) &&
            returnedCode is { Length: > 0 and <= 128 } && returnedCode.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-')
            ? returnedCode : operation == "act" && !succeeded ? "cua_effect_unverified" : "cua_operation_failed";
        var summary = succeeded ? $"Computer-use {operation} returned {status}." :
            status == ComputerUseStatuses.AppliedUnverified ? "The action may have been applied. Inspect the returned read-back and reobserve before deciding the next action." :
            "Computer use did not complete with a verified successful outcome. Inspect its structured status and code.";
        return (response with
        {
            Success = succeeded,
            Status = status,
            Summary = summary,
            Error = succeeded ? null : new NetRatelToolError(code, false)
        }).WithFailureContext(ComputerUseTool, operation);
    }

    private static bool TryComputerUseTarget(JsonObject payload, bool windowRequired, out JsonObject? target)
    {
        target = null;
        if (payload["target"] is not JsonObject value ||
            !ContainsOnly(value, windowRequired ? ["windowsSessionId", "userSidHash", "processId", "windowId"] : ["windowsSessionId", "userSidHash"]) ||
            !TryRequiredInt(value, "windowsSessionId", 1, int.MaxValue, out _) ||
            !TryRequiredString(value, "userSidHash", 128, out var sid) || sid.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            return false;
        if (windowRequired && (!TryRequiredInt(value, "processId", 1, int.MaxValue, out _) ||
            !TryRequiredString(value, "windowId", 256, out var window) || window.Any(char.IsControl)))
            return false;
        target = value.DeepClone().AsObject();
        return true;
    }

    private static bool TryComputerUseAction(JsonObject payload, out JsonObject? action)
    {
        action = null;
        if (payload["action"] is not JsonObject value || !TryRequiredString(value, "kind", 16, out var kind))
            return false;
        bool Element(bool required) => (!required && !value.ContainsKey("elementId")) ||
            (TryRequiredString(value, "elementId", 256, out var element) && !element.Any(char.IsControl));
        bool Coordinates() => TryRequiredInt(value, "x", 0, 32767, out _) && TryRequiredInt(value, "y", 0, 32767, out _);
        var valid = kind switch
        {
            ComputerUseActionKinds.Invoke => ContainsOnly(value, "kind", "elementId") && Element(true),
            ComputerUseActionKinds.Click => ContainsOnly(value, "kind", "x", "y", "button") && Coordinates() &&
                (!value.ContainsKey("button") || (TryRequiredString(value, "button", 8, out var button) && button is "left" or "right" or "middle")),
            ComputerUseActionKinds.Type => ContainsOnly(value, "kind", "text", "elementId") && Element(false) &&
                value["text"] is JsonValue textNode && textNode.TryGetValue<string>(out var text) && text.Length is > 0 and <= ComputerUseContract.MaximumTextLength && !text.Contains('\0'),
            ComputerUseActionKinds.Key => ContainsOnly(value, "kind", "key") &&
                TryRequiredString(value, "key", 32, out var key) && ComputerUseKeys.Contains(key, StringComparer.Ordinal),
            ComputerUseActionKinds.Scroll => ContainsOnly(value, "kind", "x", "y", "scrollX", "scrollY") && Coordinates() &&
                TryRequiredInt(value, "scrollX", -50, 50, out var scrollX) && TryRequiredInt(value, "scrollY", -50, 50, out var scrollY) &&
                (scrollX != 0 ^ scrollY != 0),
            _ => false
        };
        if (valid) action = value.DeepClone().AsObject();
        return valid;
    }

    private static CallToolResult ComputerUseProtocolResult(NetRatelToolResponse response)
    {
        ImageContentBlock? image = null;
        if (response.Data is JsonObject envelope && envelope["result"] is JsonObject result &&
            result["observation"] is JsonObject observation && observation["screenshot"] is JsonObject screenshot &&
            screenshot["base64"] is JsonValue encoded && encoded.TryGetValue<string>(out var base64))
        {
            try
            {
                if (base64.Length > ComputerUseContract.MaximumResponseBytes ||
                    screenshot["mimeType"] is not JsonValue mimeNode || !mimeNode.TryGetValue<string>(out var mime) ||
                    mime is not ("image/png" or "image/jpeg"))
                    throw new FormatException("Unsupported screenshot encoding.");
                image = ImageContentBlock.FromBytes(Convert.FromBase64String(base64), mime);
                screenshot.Remove("base64");
                screenshot["contentBlockIndex"] = 1;
            }
            catch (FormatException)
            {
                screenshot.Remove("base64");
                response = response with { Success = false, Status = "failed", Summary = "The endpoint returned an invalid screenshot.",
                    Error = new NetRatelToolError("cua_screenshot_invalid", false) };
            }
        }
        var structured = JsonSerializer.SerializeToElement(response, JsonSerializerOptions.Web);
        return new CallToolResult
        {
            IsError = !response.Success,
            StructuredContent = structured,
            Content = image is null ? [new TextContentBlock { Text = structured.GetRawText() }] :
                [new TextContentBlock { Text = structured.GetRawText() }, image]
        };
    }
}
