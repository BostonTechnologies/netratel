using System.Linq.Expressions;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Scripts;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

public sealed class ScriptService(OrchestratorDbContext db) : IScriptService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly OrchestratorDbContext _db = db;

    public async Task<IReadOnlyList<ScriptInfo>> ListAsync(CancellationToken ct = default)
        => await _db.Scripts
            .AsNoTracking()
            .OrderBy(x => x.FolderPath)
            .ThenBy(x => x.Name)
            .Select(MapScript())
            .ToListAsync(ct);

    public async Task<IReadOnlyList<ScriptInfo>> ListPageAsync(long afterId, int limit, CancellationToken ct = default)
    {
        if (afterId < 0 || limit is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(limit));
        return await _db.Scripts.AsNoTracking().Where(script => script.Id > afterId)
            .OrderBy(script => script.Id).Take(limit).Select(MapScript()).ToListAsync(ct);
    }

    public async Task<ScriptInfo?> GetAsync(ulong scriptId, CancellationToken ct = default)
        => await _db.Scripts
            .AsNoTracking()
            .Where(x => x.Id == (long)scriptId)
            .Select(MapScript())
            .FirstOrDefaultAsync(ct);

    public async Task<IReadOnlyList<ScriptParamInfo>> GetParamsAsync(ulong scriptId, CancellationToken ct = default)
        => await _db.ScriptParameters
            .AsNoTracking()
            .Where(x => x.ScriptId == (long)scriptId && _db.Scripts.Any(script => script.Id == x.ScriptId))
            .OrderBy(x => x.Id)
            .Select(MapParam())
            .ToListAsync(ct);

    public async Task<ScriptInfo> CreateAsync(CreateScriptCommand command, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var script = new ScriptDefinition
        {
            Name = command.Name.Trim(),
            FolderPath = NormalizeFolderPath(command.FolderPath),
            Description = NormalizeDescription(command.Description),
            Content = command.Content ?? string.Empty,
            ScriptType = NormalizeScriptType(command.ScriptType),
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        script.ManifestJson = ResolveManifestJson(command.ManifestRaw, script.Content);
        ReplaceParamsFromManifest(script, script.ManifestJson);

        _db.Scripts.Add(script);
        await _db.SaveChangesAsync(ct);

        return MapScript(script);
    }

    public async Task<ScriptInfo?> UpdateAsync(UpdateScriptCommand command, CancellationToken ct = default)
    {
        var script = await _db.Scripts
            .Include(x => x.Parameters)
            .FirstOrDefaultAsync(x => x.Id == (long)command.ScriptId, ct);

        if (script is null)
        {
            return null;
        }

        EnsureRevision(script, command.ExpectedSourceRevision);

        if (command.Name is not null)
        {
            script.Name = command.Name.Trim();
        }

        if (command.FolderPath is not null)
        {
            script.FolderPath = NormalizeFolderPath(command.FolderPath);
        }

        if (command.Description is not null)
        {
            script.Description = NormalizeDescription(command.Description);
        }

        if (command.Content is not null)
        {
            script.Content = command.Content;
        }

        if (command.ScriptType is not null)
        {
            script.ScriptType = NormalizeScriptType(command.ScriptType);
        }

        script.ManifestJson = ResolveManifestJson(command.ManifestRaw, script.Content);
        script.UpdatedAtUtc = DateTimeOffset.UtcNow;
        ReplaceParamsFromManifest(script, script.ManifestJson);

        await _db.SaveChangesAsync(ct);
        return MapScript(script);
    }

    public Task<ScriptInfo?> DeleteAsync(ulong scriptId, CancellationToken ct = default) => DeleteCoreAsync(scriptId, null, ct);

    public Task<ScriptInfo?> DeleteAsync(ulong scriptId, long expectedSourceRevision, CancellationToken ct = default) =>
        DeleteCoreAsync(scriptId, expectedSourceRevision, ct);

    private async Task<ScriptInfo?> DeleteCoreAsync(ulong scriptId, long? expectedSourceRevision, CancellationToken ct)
    {
        await using var transaction = await ScriptReferenceFence.BeginAsync(_db, ct);
        if (!await ScriptReferenceFence.LockActiveAsync(_db, checked((long)scriptId), ct))
            return null;
        var script = await _db.Scripts
            .Include(x => x.Parameters)
            .FirstOrDefaultAsync(x => x.Id == (long)scriptId, ct);

        if (script is null)
        {
            return null;
        }

        EnsureRevision(script, expectedSourceRevision);
        if (await _db.JobSteps.AsNoTracking().AnyAsync(step => step.ScriptId == script.Id, ct))
            throw new ScriptSourceInUseException();
        // Retain source identity, revision evidence, and references from past
        // runs. The canonical query filter hides the tombstone in all readers.
        script.DeletedAtUtc = DateTimeOffset.UtcNow;
        script.UpdatedAtUtc = script.DeletedAtUtc.Value;
        await _db.SaveChangesAsync(ct);
        if (transaction is not null) await transaction.CommitAsync(ct);
        return MapScript(script);
    }

    public Task<ScriptInfo?> ParseManifestAsync(ulong scriptId, string? manifestRaw, CancellationToken ct = default) =>
        ParseManifestCoreAsync(scriptId, manifestRaw, null, ct);

    public Task<ScriptInfo?> ParseManifestAsync(ulong scriptId, string? manifestRaw, long expectedSourceRevision, CancellationToken ct = default) =>
        ParseManifestCoreAsync(scriptId, manifestRaw, expectedSourceRevision, ct);

    private async Task<ScriptInfo?> ParseManifestCoreAsync(ulong scriptId, string? manifestRaw, long? expectedSourceRevision, CancellationToken ct)
    {
        var script = await _db.Scripts
            .Include(x => x.Parameters)
            .FirstOrDefaultAsync(x => x.Id == (long)scriptId, ct);

        if (script is null)
        {
            return null;
        }

        EnsureRevision(script, expectedSourceRevision);
        script.ManifestJson = ResolveManifestJson(manifestRaw, script.Content);
        script.UpdatedAtUtc = DateTimeOffset.UtcNow;
        ReplaceParamsFromManifest(script, script.ManifestJson);

        await _db.SaveChangesAsync(ct);
        return MapScript(script);
    }

    private static void EnsureRevision(ScriptDefinition script, long? expected)
    {
        if (expected.HasValue && (expected <= 0 || script.SourceRevision != expected.Value))
            throw new ScriptSourceConcurrencyException();
    }

    private static void ReplaceParamsFromManifest(ScriptDefinition script, string? manifestJson)
    {
        script.Parameters.Clear();
        if (string.IsNullOrWhiteSpace(manifestJson))
        {
            return;
        }

        var model = JsonSerializer.Deserialize<ManifestModel>(manifestJson, JsonOptions);
        if (model?.Params is null)
        {
            return;
        }

        foreach (var param in model.Params)
        {
            script.Parameters.Add(new ScriptParameterDefinition
            {
                Name = param.Name ?? string.Empty,
                Type = NormalizeParamType(param.Type),
                Required = param.Required,
                Default = param.Default,
                Description = param.Description,
                OptionsJson = param.Options is null ? null : JsonSerializer.Serialize(param.Options, JsonOptions)
            });
        }
    }

    private static string? ResolveManifestJson(string? manifestRaw, string content)
    {
        if (!string.IsNullOrWhiteSpace(manifestRaw))
        {
            return NormalizeJson(manifestRaw);
        }

        var rawBlock = ExtractManifestBlock(content);
        return rawBlock is null ? null : NormalizeJson(rawBlock);
    }

    private static string? ExtractManifestBlock(string? content)
    {
        if (string.IsNullOrEmpty(content))
        {
            return null;
        }

        var match = Regex.Match(
            content,
            @"^\s*[#;]\|\s*NetRatel-MANIFEST\s*$([\s\S]*?)^\s*[#;]\|\s*END\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        return match.Success ? match.Groups[1].Value.Trim() : null;
    }

    private static string? NormalizeJson(string raw)
    {
        var trimmed = raw.TrimStart();
        if (!(trimmed.StartsWith("{", StringComparison.Ordinal) || trimmed.StartsWith("[", StringComparison.Ordinal)))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(doc.RootElement, JsonOptions);
    }

    private static string NormalizeFolderPath(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return "/";
        }

        var normalized = folderPath.Replace("\\", "/", StringComparison.Ordinal).Trim();
        return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
    }

    private static string NormalizeDescription(string? description) => description?.Trim() ?? string.Empty;

    private static string NormalizeScriptType(string? scriptType)
        => string.IsNullOrWhiteSpace(scriptType) ? "PowerShell" : scriptType.Trim();

    private static string NormalizeParamType(string? type)
        => (type ?? "string").Trim().ToLowerInvariant() switch
        {
            "str" => "string",
            "boolean" => "bool",
            var value => value
        };

    private static ScriptInfo MapScript(ScriptDefinition script)
        => new(
            checked((ulong)script.Id),
            script.Name,
            script.FolderPath,
            script.Description,
            script.Content,
            script.ManifestJson,
            script.ScriptType,
            script.CreatedAtUtc,
            script.UpdatedAtUtc, script.SourceRevision);

    private static ScriptParamInfo MapParam(ScriptParameterDefinition param)
        => new(
            checked((ulong)param.Id),
            checked((ulong)param.ScriptId),
            param.Name,
            param.Type,
            param.Required,
            param.Default,
            param.Description,
            param.OptionsJson);

    private static Expression<Func<ScriptDefinition, ScriptInfo>> MapScript() =>
        script => new ScriptInfo(
            (ulong)script.Id,
            script.Name,
            script.FolderPath,
            script.Description,
            script.Content,
            script.ManifestJson,
            script.ScriptType,
            script.CreatedAtUtc,
            script.UpdatedAtUtc, script.SourceRevision);

    private static Expression<Func<ScriptParameterDefinition, ScriptParamInfo>> MapParam() =>
        param => new ScriptParamInfo(
            (ulong)param.Id,
            (ulong)param.ScriptId,
            param.Name,
            param.Type,
            param.Required,
            param.Default,
            param.Description,
            param.OptionsJson);

    private sealed class ManifestModel
    {
        [JsonPropertyName("params")]
        public List<ManifestParam>? Params { get; set; }
    }

    private sealed class ManifestParam
    {
        public string? Name { get; set; }
        public string? Type { get; set; }
        public bool Required { get; set; }
        public string? Default { get; set; }
        public string? Description { get; set; }
        public object? Options { get; set; }
    }
}
