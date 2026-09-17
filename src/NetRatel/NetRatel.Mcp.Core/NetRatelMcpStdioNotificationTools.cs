using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;

namespace NetRatel.Mcp.Core;

/// <summary>
/// Shared stdio dispatcher for notification reads and bounded mark-read.
/// Development retains its legacy direct API compatibility; Production uses
/// the independently policy-admitted HTTP operator mutation route.
/// </summary>
[McpServerToolType]
public sealed class NetRatelMcpStdioNotificationTools(NetRatelMcpOperationalTools operationalTools)
{
    private readonly INetRatelMcpApiClient? legacyClient;

    /// <summary>Compatibility constructor retained for existing stdio host registrations.</summary>
    public NetRatelMcpStdioNotificationTools(INetRatelMcpApiClient client, NetRatelMcpOperationalTools operationalTools)
        : this(operationalTools)
    {
        legacyClient = client;
    }

    [McpServerTool(UseStructuredContent = true), Description("Inspect notifications or mark bounded notification identifiers as read. Select an operation and put fields inside request.")]
    public Task<NetRatelToolResponse> netratel_notifications(
        string operation,
        JsonElement? request = null,
        bool confirm = false,
        CancellationToken cancellationToken = default)
        => operation switch
        {
            "list" or "get" or "summary" or "unread_errors" => operationalTools.netratel_notifications(operation, request, confirm, cancellationToken),
            "mark_read" when legacyClient is not null && !operationalTools.UsesProductionOperatorRoutes => LegacyMarkReadAsync(request, confirm, cancellationToken),
            "mark_read" => operationalTools.netratel_notifications(operation, request, confirm, cancellationToken),
            _ => Task.FromResult(new NetRatelToolResponse(
                false,
                "invalid_request",
                $"Unsupported netratel_notifications operation '{operation}'.",
                Error: new NetRatelToolError("unsupported_operation", false, AllowedOperations: ["list", "get", "summary", "unread_errors", "mark_read"]))
                .WithFailureContext("netratel_notifications", operation))
        };

    private async Task<NetRatelToolResponse> LegacyMarkReadAsync(JsonElement? request, bool confirm, CancellationToken cancellationToken)
    {
        var ids = ReadLegacyIds(request);
        if (ids is null)
            return Invalid("request.ids must contain from 1 through 200 non-empty notification identifiers.");
        if (!confirm)
        {
            return new NetRatelToolResponse(false, "confirmation_required", $"This operation would mark {ids.Count} notifications as read.",
                AffectedIds: ids, RequiresConfirmation: true, Confirmation: new NetRatelConfirmation("confirm", true, "mark_read", ids))
                .WithFailureContext("netratel_notifications", "mark_read");
        }

        try
        {
            var data = await legacyClient!.SendAsync(HttpMethod.Post, "/api/v1/notifications/mark-read",
                new JsonObject { ["ids"] = new JsonArray(ids.Select(id => JsonValue.Create(id)).ToArray()) }, cancellationToken).ConfigureAwait(false);
            return new NetRatelToolResponse(true, "completed", "Notifications marked read.", data, ids);
        }
        catch (NetRatelMcpApiException exception)
        {
            return new NetRatelToolResponse(false, "failed", exception.Message,
                Error: new NetRatelToolError(exception.RemoteCode ?? exception.Code, exception.Retryable, exception.StatusCode))
                .WithFailureContext("netratel_notifications", "mark_read");
        }
        catch (NetRatelMcpApiValidationException exception)
        {
            return Invalid(exception.Message);
        }
    }

    private static IReadOnlyList<string>? ReadLegacyIds(JsonElement? request)
    {
        if (request is not { ValueKind: JsonValueKind.Object } value || !value.TryGetProperty("ids", out var rawIds) || rawIds.ValueKind != JsonValueKind.Array)
            return null;
        var ids = rawIds.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()?.Trim()).Where(id => id is { Length: > 0 and <= 200 }).Cast<string>()
            .Distinct(StringComparer.Ordinal).ToArray();
        return ids.Length is > 0 and <= 200 && ids.Length == rawIds.GetArrayLength() ? ids : null;
    }

    private static NetRatelToolResponse Invalid(string summary) =>
        new(false, "invalid_request", summary, Error: new NetRatelToolError("validation_error", false));

}
