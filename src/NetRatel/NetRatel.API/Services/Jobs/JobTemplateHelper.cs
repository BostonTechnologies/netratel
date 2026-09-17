using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using NetRatel.Shared.Contracts.Jobs;

namespace NetRatel.API.Services.Jobs;

public static class JobTemplateHelper
{
    private static readonly Regex OptionRegex = new("@option\\.([a-zA-Z0-9_-]+)@", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly JsonSerializerOptions OptionsJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public static IReadOnlyDictionary<string, object?> ParseInputs(string? inputsJson)
    {
        var dict = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        if (string.IsNullOrWhiteSpace(inputsJson))
        {
            return dict;
        }

        try
        {
            using var doc = JsonDocument.Parse(inputsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return dict;
            }

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = ConvertJsonElement(prop.Value);
            }

            // ExternalService M2M envelopes nest job parameters under "input" while keeping
            // execution metadata under "meta". Flatten "input" so runtime placeholder
            // resolution behaves the same as direct UI-triggered job runs.
            if (doc.RootElement.TryGetProperty("input", out var inputNode)
                && inputNode.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in inputNode.EnumerateObject())
                {
                    dict[prop.Name] = ConvertJsonElement(prop.Value);
                }
            }
        }
        catch
        {
            // Ignore malformed inputs and return whatever has been parsed.
        }

        return dict;
    }

    public static string? ResolvePlaceholders(string? input, IReadOnlyDictionary<string, object?> inputs)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        return OptionRegex.Replace(input, match =>
        {
            var key = match.Groups[1].Value;
            if (inputs.TryGetValue(key, out var value))
            {
                return ConvertValueToString(value);
            }

            return match.Value;
        });
    }

    public static string ConvertValueToString(object? value) => value switch
    {
        null => string.Empty,
        bool b => b ? "true" : "false",
        JsonElement je => ConvertJsonElementToString(je),
        _ => value?.ToString() ?? string.Empty
    };

    public static IReadOnlyList<JobRunOptionDto> BuildOptionsFromInputs(string? inputsJson)
    {
        var parsed = ParseInputs(inputsJson);
        return BuildOptionsFromInputs(parsed);
    }

    public static IReadOnlyList<JobRunOptionDto> BuildOptionsFromInputs(IReadOnlyDictionary<string, object?> inputs)
    {
        if (inputs.Count == 0)
        {
            return Array.Empty<JobRunOptionDto>();
        }

        var list = new List<JobRunOptionDto>(inputs.Count);
        foreach (var kvp in inputs.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (IsTransportContainerKey(kvp.Key))
            {
                continue;
            }

            var source = $"@option.{kvp.Key}@";
            list.Add(new JobRunOptionDto(kvp.Key, ConvertValueToString(kvp.Value), source));
        }

        return list;
    }

    public static IReadOnlyList<JobRunOptionDto> BuildOptions(string? optionsJson, string? inputsJson)
    {
        var parsedOptions = ParseOptionsJson(optionsJson);
        if (parsedOptions.Count > 0)
        {
            return parsedOptions;
        }

        return BuildOptionsFromInputs(inputsJson);
    }

    public static string? SerializeOptions(IReadOnlyList<JobRunOptionDto>? options)
    {
        if (options is null || options.Count == 0)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Serialize(options, OptionsJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<JobRunOptionDto> ParseOptionsJson(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson))
        {
            return Array.Empty<JobRunOptionDto>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<JobRunOptionDto>>(optionsJson, OptionsJsonOptions);
            if (parsed is { Count: > 0 })
            {
                return parsed;
            }
        }
        catch
        {
            // ignore malformed options
        }

        return Array.Empty<JobRunOptionDto>();
    }

    private static bool IsTransportContainerKey(string key)
        => string.Equals(key, "meta", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "input", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "expectedRuntimeSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "graceSeconds", StringComparison.OrdinalIgnoreCase)
           || string.Equals(key, "hardTimeoutSeconds", StringComparison.OrdinalIgnoreCase);

    private static object? ConvertJsonElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText()
    };

    private static string ConvertJsonElementToString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.String => value.GetString() ?? string.Empty,
        JsonValueKind.Number => value.GetRawText(),
        JsonValueKind.Null or JsonValueKind.Undefined => string.Empty,
        _ => value.GetRawText()
    };
}
