using System.Text.Json;
using NetRatel.Application.Requests;

namespace NetRatel.API.Services.Orchestration;

public static class ExternalServiceIngestRequestMatcher
{
    public static async Task<RequestInfo?> FindExistingAsync(
        IRequestService requests,
        NetRatelIngestRequest request,
        CancellationToken ct)
    {
        var requestTaskId = NormalizeOptional(request.RequestTaskId);
        if (requestTaskId is null)
        {
            return null;
        }

        var all = await requests.ListAsync(ct);
        return all.FirstOrDefault(x =>
            string.Equals(x.SourceSystem, "external-service.api", StringComparison.OrdinalIgnoreCase)
            && ContainsExternalServiceTaskId(x, requestTaskId));
    }

    public static bool ContainsExternalServiceTaskId(RequestInfo request, string requestTaskId)
    {
        if (JsonHasRequestTaskId(request.JobInputs, requestTaskId))
        {
            return true;
        }

        return request.Logs.Any(log => log.Contains(requestTaskId, StringComparison.OrdinalIgnoreCase));
    }

    private static bool JsonHasRequestTaskId(string? json, string requestTaskId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonHasRequestTaskId(document.RootElement, requestTaskId);
        }
        catch
        {
            return json.Contains(requestTaskId, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool JsonHasRequestTaskId(JsonElement element, string requestTaskId)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, "requestTaskId", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String
                    && string.Equals(property.Value.GetString(), requestTaskId, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (JsonHasRequestTaskId(property.Value, requestTaskId))
                {
                    return true;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (JsonHasRequestTaskId(item, requestTaskId))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
