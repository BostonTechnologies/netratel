using System.Globalization;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace NetRatel.Mcp.Http;

/// <summary>Validates the published prompt representation before reflected binding.</summary>
internal static class McpPromptArgumentValidation
{
    public static void Validate(GetPromptRequestParams parameters)
    {
        var name = parameters.Name switch
        {
            "inspect_client" => "client",
            "diagnose_job_run" => "jobRunId",
            _ => null
        };
        if (name is null) return;
        var expected = name == "client"
            ? "a nonblank string of at most 512 characters without control characters"
            : "a positive decimal-string Int64 identifier without whitespace, signs, or exponent notation";
        if (parameters.Arguments is null || !parameters.Arguments.TryGetValue(name, out var value) ||
            value.ValueKind != JsonValueKind.String || !Valid(name, value.GetString()))
            throw new McpProtocolException($"Argument '{name}' requires {expected}. Supply '{name}' in prompts/get arguments and retry.", McpErrorCode.InvalidParams);
        // Additional arguments retain the SDK's ignored-extra-argument behavior.
        // Their values cannot alter target selection or the workflow's authority.
    }

    private static bool Valid(string name, string? value) => name == "client"
        ? !string.IsNullOrWhiteSpace(value) && value.Length <= 512 && !value.Any(char.IsControl)
        : value is { Length: > 0 and <= 19 } && value.All(char.IsAsciiDigit) &&
          long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0;
}
