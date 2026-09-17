using System.Text.Json;
using System.Collections.Generic;

namespace NetRatel.Shared.Tooling;

/// <summary>
/// Renders simple string templates using values from a JSON document.
/// Placeholders use the syntax <c>${name}</c> and are replaced with the
/// corresponding value from the top level of the input JSON.
/// </summary>
public static class ToolTemplateRenderer
{
    /// <summary>
    /// Renders the provided template by replacing <c>${key}</c> placeholders
    /// with values from <paramref name="inputJson"/>.
    /// </summary>
    /// <param name="template">The template containing placeholders.</param>
    /// <param name="inputJson">A JSON object with values used during rendering.</param>
    /// <returns>The rendered string.</returns>
    public static string Render(string template, string inputJson)
    {
        if (string.IsNullOrEmpty(template))
        {
            return template;
        }

        Dictionary<string, JsonElement>? values = null;

        if (!string.IsNullOrWhiteSpace(inputJson))
        {
            values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(inputJson);
        }

        if (values == null)
        {
            return template;
        }

        foreach (var kvp in values)
        {
            var placeholder = "${" + kvp.Key + "}";
            var value = kvp.Value.ValueKind switch
            {
                JsonValueKind.String => kvp.Value.GetString() ?? string.Empty,
                JsonValueKind.Number => kvp.Value.GetRawText(),
                JsonValueKind.True => "true",
                JsonValueKind.False => "false",
                _ => kvp.Value.GetRawText()
            };

            template = template.Replace(placeholder, value);
        }

        return template;
    }
}
