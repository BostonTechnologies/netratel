namespace NetRatel.Application.Scripts;

public sealed record ScriptInfo(
    ulong Id,
    string Name,
    string FolderPath,
    string Description,
    string Content,
    string? ManifestJson,
    string ScriptType,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    long SourceRevision = 1);

public sealed record ScriptParamInfo(
    ulong Id,
    ulong ScriptId,
    string Name,
    string Type,
    bool Required,
    string? Default,
    string? Description,
    string? OptionsJson);

public sealed record CreateScriptCommand(
    string Name,
    string FolderPath,
    string? Description,
    string? Content,
    string ScriptType,
    string? ManifestRaw);

public sealed record UpdateScriptCommand(
    ulong ScriptId,
    string? Name,
    string? FolderPath,
    string? Description,
    string? Content,
    string? ScriptType,
    string? ManifestRaw,
    long? ExpectedSourceRevision = null);

public sealed class ScriptSourceConcurrencyException() : InvalidOperationException("The script source revision changed; reload before editing.");

public sealed class ScriptSourceInUseException() : InvalidOperationException("The script is referenced by a job definition; remove its references before deletion.");

public interface IScriptService
{
    Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ScriptInfo>> ListPageAsync(long afterId, int limit, CancellationToken ct = default)
        => throw new NotSupportedException("Bounded canonical script reads are not supported by this implementation.");
    Task<ScriptInfo?> GetAsync(ulong scriptId, CancellationToken ct = default);
    Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong scriptId, CancellationToken ct = default);
    Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default);
    Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default);
    Task<ScriptInfo?> DeleteAsync(ulong scriptId, CancellationToken ct = default);
    Task<ScriptInfo?> DeleteAsync(ulong scriptId, long expectedSourceRevision, CancellationToken ct = default)
        => throw new NotSupportedException("Revision-fenced deletion is not supported by this implementation.");
    Task<ScriptInfo?> ParseManifestAsync(ulong scriptId, string? manifestRaw, long expectedSourceRevision, CancellationToken ct = default)
        => throw new NotSupportedException("Revision-fenced manifest updates are not supported by this implementation.");
    Task<ScriptInfo?> ParseManifestAsync(ulong scriptId, string? manifestRaw, CancellationToken ct = default);
}
