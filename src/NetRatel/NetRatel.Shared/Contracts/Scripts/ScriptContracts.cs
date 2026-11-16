namespace NetRatel.Shared.Contracts.Scripts;

public sealed record ScriptDto(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    string Name,
    string? FolderPath,
    string? Description,
    string? Content,
    string? ScriptType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long SourceRevision = 1);

public sealed record CreateScriptRequest(
    string Name,
    string? FolderPath,
    string? Description,
    string? Content,
    string? ScriptType);

public sealed record UpdateScriptRequest(
    string? Name,
    string? FolderPath,
    string? Description,
    string? Content,
    string? ScriptType,
    long? ExpectedSourceRevision = null);

public sealed record ScriptSummaryDto(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    string Name,
    string? Description,
    string? ScriptType,
    string[]? Tags);

public record ScriptResponse(
    [property: System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.WriteAsString | System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] ulong Id,
    string Name,
    string FolderPath,
    string Description,
    string Content,
    string ScriptType,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long SourceRevision = 1
);
