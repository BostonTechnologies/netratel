using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Server;
using NJsonSchema;
using NJsonSchema.Validation;

namespace NetRatel.Mcp.Core;

/// <summary>Validates the exact published operation contracts before authorization or delegation.</summary>
public sealed class NetRatelMcpInputValidator
{
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, JsonSchema>> schemas;

    public NetRatelMcpInputValidator(IReadOnlyList<McpServerTool> tools)
    {
        schemas = tools.ToDictionary(tool => tool.ProtocolTool.Name, tool =>
        {
            // The repository's schema engine implements draft 4. A singleton
            // enum has the same semantics as const, including nested selectors.
            var input = JsonNode.Parse(tool.ProtocolTool.InputSchema.GetRawText())!;
            NormalizeConstants(input);
            var schema = JsonSchema.FromJsonAsync(input.ToJsonString()).GetAwaiter().GetResult();
            return (IReadOnlyDictionary<string, JsonSchema>)schema.OneOf.ToDictionary(
                alternative => alternative.Properties["operation"].Enumeration.Single()?.ToString()
                    ?? throw new InvalidOperationException("A published operation discriminator is missing."),
                alternative => alternative,
                StringComparer.Ordinal);
        }, StringComparer.Ordinal);
    }

    public NetRatelToolResponse? Validate(string tool, IDictionary<string, JsonElement>? arguments)
    {
        if (!schemas.TryGetValue(tool, out var operations) || arguments is null ||
            !arguments.TryGetValue("operation", out var discriminator) || discriminator.ValueKind != JsonValueKind.String ||
            !operations.TryGetValue(discriminator.GetString()!, out var schema))
            return Invalid(["$.operation"]);

        ICollection<ValidationError> errors;
        try
        {
            errors = schema.Validate(JsonSerializer.Serialize(arguments));
        }
        catch (Exception exception) when (exception is OverflowException or InvalidCastException or FormatException or Newtonsoft.Json.JsonException)
        {
            // Legal JSON numbers can exceed the schema engine's numeric
            // representation. They still fail as input before any exchange.
            return Invalid(["$.request"]).WithFailureContext(tool, discriminator.GetString()!);
        }
        if (errors.Count == 0) return null;
        var paths = errors.SelectMany(FieldPaths).Distinct(StringComparer.Ordinal).Take(8).ToArray();
        return Invalid(paths.Length == 0 ? ["$"] : paths).WithFailureContext(tool, discriminator.GetString()!);
    }

    private static NetRatelToolResponse Invalid(IReadOnlyList<string> paths) => new(
        false, "invalid_request", "The tool arguments do not match the published operation contract.",
        new JsonObject { ["fieldPaths"] = new JsonArray(paths.Select(path => (JsonNode?)JsonValue.Create(path)).ToArray()) },
        Error: new NetRatelToolError("validation_error", false));

    private static IEnumerable<string> FieldPaths(ValidationError error)
    {
        if (error is ChildSchemaValidationError child)
        {
            foreach (var nested in child.Errors.Values.SelectMany(errors => errors))
                foreach (var path in FieldPaths(nested))
                    yield return path;
            yield break;
        }
        // Only paths are exposed: engine diagnostics can include caller values.
        var raw = error.Path;
        if (string.IsNullOrEmpty(raw)) raw = error.Property;
        if (string.IsNullOrEmpty(raw)) { yield return "$"; yield break; }
        var segments = raw.TrimStart('#', '/', '$', '.').Replace("[", ".", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal).Replace('/', '.').Split('.');
        var safe = segments.Take(12).Select(part => part.Length is > 0 and <= 64 &&
            part.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-') ? part : "member");
        yield return "$." + string.Join('.', safe);
    }

    private static void NormalizeConstants(JsonNode node)
    {
        if (node is JsonObject obj)
        {
            if (obj.TryGetPropertyValue("const", out var constant))
            {
                obj["enum"] = new JsonArray(constant?.DeepClone());
                obj.Remove("const");
            }
            foreach (var property in obj)
                if (property.Value is not null) NormalizeConstants(property.Value);
        }
        else if (node is JsonArray array)
            foreach (var item in array)
                if (item is not null) NormalizeConstants(item);
    }
}
