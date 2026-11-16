using System.Collections.Generic;

namespace NetRatel.Shared.Contracts.Scripts
{
    public sealed record ScriptParamDto(
        [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
        [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong ScriptId,
        string Name,
        string Type,
        bool Required,
        string? Default,
        string? Description,
        string? OptionsJson
    );

    public sealed record ScriptManifestDto(
        string? Raw,
        IReadOnlyList<ScriptParamDto> Params
    );

    public sealed record ParseManifestRequest(
        string? ManifestRaw
    );

    public sealed record UpsertScriptRequest(
        [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong? Id,
        string Name,
        string FolderPath,
        string? Description,
        string Content,
        string ScriptType,
        string? ManifestRaw
    );
}
