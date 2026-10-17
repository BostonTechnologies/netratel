using System.Globalization;
using System.Text.Json;

namespace NetRatel.Web.Components.Pages.Jobs;

/// <summary>Operational input hints and validation without changing the established run payload representation.</summary>
public static class JobParameterInput
{
    /// <summary>Forwarded by MudBlazor to the native control; operational field names stay separate from payload keys.</summary>
    public static Dictionary<string, object> OperationalAttributes(string inputId, bool secret = false) => new()
    {
        ["name"] = inputId,
        ["autocomplete"] = secret ? "new-password" : "off"
    };

    public static IReadOnlyList<string> SelectOptions(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<string>>(json) is { } options && options.All(value => !string.IsNullOrWhiteSpace(value))
                ? options : [];
        }
        catch (JsonException) { return []; }
    }

    public static string? Validate(string? type, string? value, bool required, string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(value)) return required ? "A value is required." : null;
        switch (type?.ToLowerInvariant())
        {
            case "int" when !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _):
                return "Enter a whole number.";
            case "bool" when !bool.TryParse(value, out _):
                return "Choose true or false.";
            case "json":
                try { using var document = JsonDocument.Parse(value); }
                catch (JsonException) { return "Enter valid JSON."; }
                break;
            case "select" when !SelectOptions(optionsJson).Contains(value, StringComparer.Ordinal):
                return "Choose one of the configured options.";
        }
        return null;
    }
}
