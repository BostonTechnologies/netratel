using System.Collections.Generic;
using NJsonSchema;

namespace NetRatel.Shared.Tooling;

/// <summary>
/// Provides JSON schema validation helpers.
/// </summary>
public static class SchemaValidator
{
    /// <summary>
    /// Validates <paramref name="json"/> against the provided JSON schema.
    /// </summary>
    /// <param name="schemaJson">JSON schema in draft format.</param>
    /// <param name="json">JSON instance to validate.</param>
    /// <param name="errors">Collection of validation errors, if any.</param>
    /// <returns><c>true</c> when the JSON is valid; otherwise <c>false</c>.</returns>
    public static bool TryValidate(string schemaJson, string json, out ICollection<string> errors)
    {
        var schema = JsonSchema.FromJsonAsync(schemaJson).GetAwaiter().GetResult();
        var validationErrors = schema.Validate(json);
        errors = new List<string>();
        foreach (var err in validationErrors)
        {
            errors.Add(err.ToString());
        }

        return errors.Count == 0;
    }
}
