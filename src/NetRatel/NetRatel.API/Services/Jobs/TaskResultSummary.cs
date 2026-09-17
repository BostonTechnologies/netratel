using System.Text.Json;

namespace NetRatel.API.Services.Jobs;

internal static class TaskResultSummary
{
    private const string Fallback = "Agent command failed.";

    public static string Failure(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson))
        {
            return Fallback;
        }

        try
        {
            using var document = JsonDocument.Parse(resultJson);
            var root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object)
            {
                return Fallback;
            }

            foreach (var propertyName in new[] { "stderr", "error", "message", "code" })
            {
                if (!root.TryGetProperty(propertyName, out var value))
                {
                    continue;
                }

                var text = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString(),
                    JsonValueKind.Array when value.GetArrayLength() > 0 => value[0].GetString(),
                    JsonValueKind.Number => value.GetRawText(),
                    _ => null
                };
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return Bound(text);
                }
            }
        }
        catch (JsonException)
        {
            return Fallback;
        }

        return Fallback;
    }

    private static string Bound(string value)
    {
        var line = value
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(line))
        {
            return Fallback;
        }

        return line.Length <= 512 ? line : string.Concat(line.AsSpan(0, 509), "...");
    }
}
