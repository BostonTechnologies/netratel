using System.Text.Json;
using NetRatel.Shared;

namespace NetRatel.Application.Jobs;

/// <summary>Canonical saved execution scope; an absent historical setting remains unknown.</summary>
public static class JobExecutionEnvironment
{
    public static ClientEnvironment? FromOptions(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return null;
        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("executionEnvironment", out var value) && value.ValueKind == JsonValueKind.Number &&
                value.TryGetInt32(out var number) && Enum.IsDefined((ClientEnvironment)number))
                return (ClientEnvironment)number;
        }
        catch (JsonException) { return null; }
        return null;
    }
}
