using System.Text.Json;

namespace NetRatel.Shared.Contracts.Monitoring;

/// <summary>The managed CLI, HTTP MCP and stdio MCP use the same normalized HTTP query.</summary>
public static class MonitoringQueryJson
{
    public static MonitoringQueryDto Read(JsonElement request, bool history)
    {
        var query = new MonitoringQueryDto(request.GetProperty("tenantId").GetInt32(),
            ReadEnum<MonitoringView>(request, "view") ?? MonitoringView.All,
            request.TryGetProperty("clientId", out var client) ? client.GetGuid() : null,
            ReadEnum<MonitoringSeverity>(request, "severity"),
            request.TryGetProperty("text", out var text) ? text.GetString() : null,
            request.TryGetProperty("fromUtc", out var from) ? from.GetDateTimeOffset() : null,
            request.TryGetProperty("toExclusiveUtc", out var to) ? to.GetDateTimeOffset() : null,
            ReadEnum<MonitoringSort>(request, "sort"),
            request.TryGetProperty("limit", out var limit) ? limit.GetInt32() : 50);
        return query.Normalize(history);
    }

    private static T? ReadEnum<T>(JsonElement request, string name) where T : struct, Enum
    {
        if (!request.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || !Enum.TryParse<T>(value.GetString(), true, out var result) || !Enum.IsDefined(result))
            throw new ArgumentException("invalid_monitoring_query");
        return result;
    }
}
