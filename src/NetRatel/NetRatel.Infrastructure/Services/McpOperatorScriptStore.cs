using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Persistence;

namespace NetRatel.Infrastructure.Services;

/// <summary>
/// Explicitly-owned Production script library. This is the only operator
/// write path for source-backed ScriptDefinition content: every mutation is
/// paired with an owner, policy snapshot, accepted audit, ETag and append-only
/// revision evidence.
/// </summary>
public sealed class McpOperatorScriptStore(OrchestratorDbContext db) : IMcpOperatorScriptStore
{
    private const int MaximumContentBytes = 64 * 1024;
    private const int MaximumParameters = 32;
    private const int MaximumTimeoutSeconds = 60 * 60;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex SecretLiteral = new(
        @"(?im)(?:password|passwd|pwd|secret|api[_-]?key|access[_-]?token|auth[_-]?token)\s*[:=]\s*(?!\$(?:\{)?(?:env:)?[A-Za-z_])['""”]?[^\s'""”;]{8,}",
        RegexOptions.CultureInvariant);
    private static readonly Regex BearerTokenLiteral = new(
        @"(?i)\bbearer\s+[a-z0-9._~+/-]{16,}",
        RegexOptions.CultureInvariant);
    private readonly OrchestratorDbContext _db = db;

    public Task ValidateAsync(
        McpOperatorDecision decision,
        McpOperatorScriptDraft draft,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateDraft(decision, AcceptedAuditForValidation(decision), draft);
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<McpOperatorScriptLease>> ListOwnedAsync(
        int tenantId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (!IsOwnershipValid(tenantId, principal, mcpResource, mcpInstance))
            return [];

        var records = await _db.McpOperatorScripts.AsNoTracking()
            .Join(_db.Scripts.AsNoTracking(), record => record.ScriptId, script => script.Id, (record, script) => new { record, script })
            .Where(entry => entry.record.TenantId == tenantId &&
                            entry.record.Subject == principal.Subject &&
                            entry.record.ClientId == ClientId(principal) &&
                            entry.record.McpResource == mcpResource &&
                            entry.record.McpInstance == mcpInstance &&
                            entry.record.DeletedAtUtc == null)
            .OrderByDescending(entry => entry.record.UpdatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        return records
            .Where(entry => IsStoredScriptIntegrityValid(entry.record, entry.script))
            .Select(entry => ToLease(entry.record)).ToArray();
    }

    public async Task<(McpOperatorScriptLease Lease, string Content, string? ManifestJson)?> GetOwnedAsync(
        long scriptId,
        int tenantId,
        McpOperatorPrincipal principal,
        string mcpResource,
        string mcpInstance,
        CancellationToken cancellationToken)
    {
        if (scriptId <= 0 || !IsOwnershipValid(tenantId, principal, mcpResource, mcpInstance))
            return null;

        var result = await _db.McpOperatorScripts.AsNoTracking()
            .Join(_db.Scripts.AsNoTracking(), record => record.ScriptId, script => script.Id, (record, script) => new { record, script })
            .SingleOrDefaultAsync(entry => entry.record.ScriptId == scriptId &&
                                           entry.record.TenantId == tenantId &&
                                           entry.record.Subject == principal.Subject &&
                                           entry.record.ClientId == ClientId(principal) &&
                                           entry.record.McpResource == mcpResource &&
                                           entry.record.McpInstance == mcpInstance &&
                                           entry.record.DeletedAtUtc == null,
                cancellationToken)
            .ConfigureAwait(false);
        if (result is null)
            return null;
        if (!IsStoredScriptIntegrityValid(result.record, result.script))
            throw new McpOperatorScriptIntegrityException();
        return (ToLease(result.record), result.script.Content, result.script.ManifestJson);
    }

    public async Task<McpOperatorScriptLease> CreateAsync(
        McpOperatorScriptCreateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = ValidateDraft(request.Decision, request.AcceptedAudit, request.Draft);
        await using var transaction = _db.Database.IsRelational()
            ? await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false)
            : null;

        var script = new ScriptDefinition
        {
            Name = prepared.Name,
            FolderPath = $"/mcp-operator/{request.Decision.Request.TenantId}/",
            Description = prepared.Description,
            Content = request.Draft.Content,
            ManifestJson = prepared.ManifestJson,
            ScriptType = StoredScriptType(prepared.ShellType),
            CreatedAtUtc = request.OccurredAtUtc,
            UpdatedAtUtc = request.OccurredAtUtc
        };
        ApplyParameters(script, prepared.Parameters);
        _db.Scripts.Add(script);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var record = new McpOperatorScriptRecord
        {
            Id = Guid.NewGuid(),
            ScriptId = script.Id,
            TenantId = request.Decision.Request.TenantId,
            Subject = request.Decision.Request.Principal.Subject,
            ClientId = ClientId(request.Decision.Request.Principal),
            McpResource = request.Decision.Request.McpResource!,
            McpInstance = request.Decision.Request.McpInstance!,
            Name = prepared.Name,
            Description = prepared.Description,
            ShellType = prepared.ShellType,
            PolicyId = request.Decision.MatchingPolicyIds.Single(),
            PolicyVersion = request.Decision.SelectedPolicyVersion!.Value,
            ContentHash = prepared.ContentHash,
            ManifestHash = prepared.ManifestHash,
            ParametersJson = SerializeParameters(prepared.Parameters),
            TimeoutSeconds = prepared.TimeoutSeconds,
            WorkingDirectory = prepared.WorkingDirectory,
            DeclaredSideEffectsJson = SerializeSideEffects(prepared.DeclaredSideEffects),
            CreatedAtUtc = request.OccurredAtUtc,
            UpdatedAtUtc = request.OccurredAtUtc,
            Version = 1
        };
        _db.McpOperatorScripts.Add(record);
        _db.McpOperatorScriptVersions.Add(ToVersionRecord(record, "created", request.AcceptedAudit.AuditId, request.OccurredAtUtc));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        if (transaction is not null)
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return ToLease(record);
    }

    public async Task<McpOperatorScriptLease?> ReplaceAsync(
        McpOperatorScriptReplaceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var prepared = ValidateDraft(request.Decision, request.AcceptedAudit, request.Draft);
        if (request.ScriptId <= 0 || request.ExpectedVersion <= 0)
            throw new ArgumentException("A positive script id and expected ETag version are required.", nameof(request));

        var record = await _db.McpOperatorScripts.SingleOrDefaultAsync(candidate => candidate.ScriptId == request.ScriptId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.DeletedAtUtc is not null || !OwnedBy(record, request.Decision.Request))
            return null;
        if (record.Version != request.ExpectedVersion)
            throw new McpOperatorScriptConcurrencyException();

        var script = await _db.Scripts.Include(candidate => candidate.Parameters)
            .SingleOrDefaultAsync(candidate => candidate.Id == request.ScriptId, cancellationToken).ConfigureAwait(false);
        if (script is null)
            return null;

        if (!IsStoredScriptIntegrityValid(record, script))
            throw new McpOperatorScriptIntegrityException();

        script.Name = prepared.Name;
        script.Description = prepared.Description;
        script.Content = request.Draft.Content;
        script.ManifestJson = prepared.ManifestJson;
        script.ScriptType = StoredScriptType(prepared.ShellType);
        script.UpdatedAtUtc = request.OccurredAtUtc;
        ApplyParameters(script, prepared.Parameters);

        record.PolicyId = request.Decision.MatchingPolicyIds.Single();
        record.PolicyVersion = request.Decision.SelectedPolicyVersion!.Value;
        record.Name = prepared.Name;
        record.Description = prepared.Description;
        record.ShellType = prepared.ShellType;
        record.ContentHash = prepared.ContentHash;
        record.ManifestHash = prepared.ManifestHash;
        record.ParametersJson = SerializeParameters(prepared.Parameters);
        record.TimeoutSeconds = prepared.TimeoutSeconds;
        record.WorkingDirectory = prepared.WorkingDirectory;
        record.DeclaredSideEffectsJson = SerializeSideEffects(prepared.DeclaredSideEffects);
        record.UpdatedAtUtc = request.OccurredAtUtc;
        record.Version++;
        _db.McpOperatorScriptVersions.Add(ToVersionRecord(record, "updated", request.AcceptedAudit.AuditId, request.OccurredAtUtc));
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new McpOperatorScriptConcurrencyException();
        }
        return ToLease(record);
    }

    public async Task<McpOperatorScriptLease?> DeleteAsync(
        long scriptId,
        long expectedVersion,
        McpOperatorDecision decision,
        McpOperatorAcceptedAudit acceptedAudit,
        DateTimeOffset occurredAtUtc,
        CancellationToken cancellationToken)
    {
        ValidateDecision(decision, acceptedAudit);
        if (scriptId <= 0 || expectedVersion <= 0)
            throw new ArgumentException("A positive script id and expected ETag version are required.");

        var record = await _db.McpOperatorScripts.SingleOrDefaultAsync(candidate => candidate.ScriptId == scriptId, cancellationToken).ConfigureAwait(false);
        if (record is null || record.DeletedAtUtc is not null || !OwnedBy(record, decision.Request))
            return null;
        if (record.Version != expectedVersion)
            throw new McpOperatorScriptConcurrencyException();

        var script = await _db.Scripts.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == scriptId, cancellationToken).ConfigureAwait(false);
        if (script is null)
            return null;

        record.PolicyId = decision.MatchingPolicyIds.Single();
        record.PolicyVersion = decision.SelectedPolicyVersion!.Value;
        record.DeletedAtUtc = occurredAtUtc;
        record.UpdatedAtUtc = occurredAtUtc;
        record.Version++;
        _db.McpOperatorScriptVersions.Add(ToVersionRecord(record, "deleted", acceptedAudit.AuditId, occurredAtUtc));
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new McpOperatorScriptConcurrencyException();
        }
        return ToLease(record);
    }

    private static PreparedDraft ValidateDraft(
        McpOperatorDecision decision,
        McpOperatorAcceptedAudit acceptedAudit,
        McpOperatorScriptDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        ValidateDecision(decision, acceptedAudit);
        var constraints = decision.EffectiveConstraints!;
        var name = NormalizeText(draft.Name, 120, "name");
        var description = NormalizeText(draft.Description, 512, "description", allowEmpty: true);
        var shell = NormalizeShell(draft.ShellType);
        var content = draft.Content ?? string.Empty;
        var contentBytes = GetUtf8ByteCount(content, "content");
        if (contentBytes is < 1 or > MaximumContentBytes || contentBytes > constraints.MaxScriptBytes ||
            !IsSha256(draft.ContentHash) || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(draft.ContentHash.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(Hash(content))))
        {
            throw new ArgumentException("Script content is invalid, too large, or does not match the supplied SHA-256 hash.", nameof(draft));
        }
        if (ContainsSecretLiteral(content))
            throw new ArgumentException("Script content must use a named target-local secret reference instead of a secret literal.", nameof(draft));
        if (constraints.AllowedShells is not { Count: > 0 } || !constraints.AllowedShells.Contains(shell, StringComparer.OrdinalIgnoreCase))
            throw new ArgumentException("The script shell is not policy allowlisted.", nameof(draft));

        var timeout = draft.TimeoutSeconds;
        if (timeout is < 1 or > MaximumTimeoutSeconds || timeout > constraints.MaxCommandDurationSeconds)
            throw new ArgumentException("The script timeout is outside the reviewed policy bound.", nameof(draft));
        var workingDirectory = NormalizeWorkingDirectory(draft.WorkingDirectory);
        if (constraints.WorkingDirectories is not { Count: > 0 } || !constraints.WorkingDirectories.AllowsWorkingDirectory(workingDirectory))
            throw new ArgumentException("The script working directory is not policy allowlisted.", nameof(draft));

        var parameters = ValidateParameters(draft.Parameters);
        var sideEffects = ValidateSideEffects(draft.DeclaredSideEffects);
        var manifest = NormalizeManifest(draft.ManifestJson);
        return new PreparedDraft(name, description, shell, Hash(content), manifest, Hash(manifest ?? string.Empty), parameters, timeout, workingDirectory, sideEffects);
    }

    private static void ValidateDecision(McpOperatorDecision decision, McpOperatorAcceptedAudit acceptedAudit)
    {
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.IsAllowed || decision.Request.Environment is not (McpOperatorEnvironment.Development or McpOperatorEnvironment.Production) ||
            decision.Request.TenantId <= 0 || decision.Request.AgentId is null || decision.Request.AgentId == Guid.Empty ||
            decision.MatchingPolicyIds.Count != 1 || decision.SelectedPolicyVersion is null || decision.EffectiveConstraints is null ||
            decision.EffectiveConstraints.MaxScriptBytes is not > 0 || decision.EffectiveConstraints.MaxCommandDurationSeconds is not > 0 ||
            string.IsNullOrWhiteSpace(decision.Request.McpResource) || string.IsNullOrWhiteSpace(decision.Request.McpInstance) ||
            acceptedAudit.PolicyId != decision.MatchingPolicyIds[0] || acceptedAudit.TenantId != decision.Request.TenantId ||
            acceptedAudit.AgentId != decision.Request.AgentId ||
            !string.Equals(acceptedAudit.Subject, decision.Request.Principal.Subject, StringComparison.Ordinal))
        {
            throw new ArgumentException("The script write is not an allowed Production policy admission.");
        }
    }

    private static McpOperatorAcceptedAudit AcceptedAuditForValidation(McpOperatorDecision decision) => new(
        Guid.Empty,
        decision.MatchingPolicyIds.Single(),
        decision.Request.Environment,
        "validation",
        decision.Request.Principal.Subject,
        decision.Request.Principal.ClientId,
        decision.Request.Principal.AuthorizedParty,
        [], [], [],
        decision.Request.McpResource,
        decision.Request.McpInstance,
        decision.Request.Tool,
        decision.Request.TenantId,
        decision.Request.AgentId,
        decision.Request.OperationFamily,
        decision.Request.Operation,
        decision.Request.CorrelationId,
        decision.Request.RequestId,
        DateTimeOffset.UtcNow);

    private static IReadOnlyList<McpOperatorScriptParameter> ValidateParameters(IReadOnlyList<McpOperatorScriptParameter>? parameters)
    {
        if (parameters is null || parameters.Count > MaximumParameters)
            throw new ArgumentException("The script parameter schema is invalid.");
        var normalized = new List<McpOperatorScriptParameter>(parameters.Count);
        foreach (var parameter in parameters)
        {
            var name = NormalizeIdentifier(parameter.Name, 64, "parameter name");
            var type = (parameter.Type ?? string.Empty).Trim().ToLowerInvariant();
            if (type is not ("string" or "integer" or "boolean" or "choice" or "secret_reference"))
                throw new ArgumentException("The script parameter type is not supported.");
            var description = parameter.Description is null ? null : NormalizeText(parameter.Description, 256, "parameter description", allowEmpty: true);
            var defaultValue = parameter.DefaultValue;
            if (defaultValue is { Length: > 1024 } || defaultValue?.Any(char.IsControl) == true)
                throw new ArgumentException("The script parameter default is invalid.");
            var options = parameter.Options ?? [];
            if (options.Count > 32 || options.Any(option => string.IsNullOrWhiteSpace(option) || option.Length > 128 || option.Any(char.IsControl)) ||
                options.Distinct(StringComparer.Ordinal).Count() != options.Count)
            {
                throw new ArgumentException("The script parameter choices are invalid.");
            }
            if (type == "choice" && options.Count == 0)
                throw new ArgumentException("A choice parameter requires bounded options.");
            if (type == "secret_reference")
            {
                if (defaultValue is not null || !IsIdentifier(parameter.SecretReference, 128))
                    throw new ArgumentException("A secret parameter must contain only a named secret reference and no default value.");
            }
            else if (parameter.SecretReference is not null)
            {
                throw new ArgumentException("Only secret_reference parameters may carry a secret reference.");
            }
            else if (LooksSensitiveIdentifier(name) && (defaultValue is not null || options.Count > 0))
            {
                throw new ArgumentException("Sensitive parameters must use secret_reference without a default or inline options.");
            }
            normalized.Add(new McpOperatorScriptParameter(name, type, parameter.Required, description, defaultValue, options.ToArray(), parameter.SecretReference));
        }
        if (normalized.Select(parameter => parameter.Name).Distinct(StringComparer.Ordinal).Count() != normalized.Count)
            throw new ArgumentException("Script parameter names must be unique.");
        return normalized;
    }

    private static IReadOnlyList<McpOperatorScriptSideEffect> ValidateSideEffects(IReadOnlyList<McpOperatorScriptSideEffect>? sideEffects)
    {
        if (sideEffects is not { Count: > 0 and <= 5 } || sideEffects.Any(value => !Enum.IsDefined(value)) ||
            sideEffects.Distinct().Count() != sideEffects.Count ||
            (sideEffects.Contains(McpOperatorScriptSideEffect.ReadOnly) && sideEffects.Count != 1))
        {
            throw new ArgumentException("The script side-effect declaration is invalid.");
        }
        return sideEffects.Order().ToArray();
    }

    private static string? NormalizeManifest(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest))
            return null;
        if (GetUtf8ByteCount(manifest, "manifest") > 16 * 1024)
            throw new ArgumentException("The script manifest is too large.");
        try
        {
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw new ArgumentException("The script manifest must be a JSON object.");
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new ArgumentException("The script manifest is not valid JSON.", exception);
        }
    }

    private static void ApplyParameters(ScriptDefinition script, IReadOnlyList<McpOperatorScriptParameter> parameters)
    {
        script.Parameters.Clear();
        foreach (var parameter in parameters)
        {
            script.Parameters.Add(new ScriptParameterDefinition
            {
                Name = parameter.Name,
                Type = parameter.Type,
                Required = parameter.Required,
                Default = parameter.Type == "secret_reference" ? null : parameter.DefaultValue,
                Description = parameter.Description,
                OptionsJson = parameter.Options is { Count: > 0 } ? JsonSerializer.Serialize(parameter.Options) : null
            });
        }
    }

    private static bool OwnedBy(McpOperatorScriptRecord record, McpOperatorAccessRequest request) =>
        record.TenantId == request.TenantId &&
        record.Subject == request.Principal.Subject &&
        record.ClientId == ClientId(request.Principal) &&
        record.McpResource == request.McpResource &&
        record.McpInstance == request.McpInstance;

    private static bool IsOwnershipValid(int tenantId, McpOperatorPrincipal principal, string resource, string instance) =>
        tenantId > 0 && !string.IsNullOrWhiteSpace(principal.Subject) &&
        (principal.ClientId is null || IsSafeToken(principal.ClientId, 256)) &&
        IsSafeToken(resource, 512) && IsSafeToken(instance, 32);

    private static string NormalizeText(string? value, int maximum, string name, bool allowEmpty = false)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length > maximum || normalized.Any(char.IsControl) || (!allowEmpty && normalized.Length == 0))
            throw new ArgumentException($"The script {name} is invalid.");
        return normalized;
    }

    private static string NormalizeShell(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "sh" or "bash" => "sh",
        "powershell" or "pwsh" => "powershell",
        _ => throw new ArgumentException("The script shell is not supported.")
    };

    private static string StoredScriptType(string shell) => shell == "sh" ? "bash" : "powershell";

    private static string NormalizeWorkingDirectory(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length is 0 or > 4096 || normalized.Any(char.IsControl))
            throw new ArgumentException("The script working directory is invalid.");
        return normalized;
    }

    private static string NormalizeIdentifier(string? value, int maximum, string name)
    {
        if (!IsIdentifier(value, maximum))
            throw new ArgumentException($"The script {name} is invalid.");
        return value!;
    }

    private static bool IsIdentifier(string? value, int maximum) =>
        value is { Length: > 0 } && value.Length <= maximum &&
        (char.IsAsciiLetter(value[0]) || value[0] == '_') &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    private static int GetUtf8ByteCount(string value, string field)
    {
        try { return StrictUtf8.GetByteCount(value); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"The script {field} must be valid UTF-8 text.", exception);
        }
    }

    private static string Hash(string value)
    {
        try { return Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(value))); }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException("The script content must be valid UTF-8 text.", exception);
        }
    }

    private static bool ContainsSecretLiteral(string value) =>
        SecretLiteral.IsMatch(value) || BearerTokenLiteral.IsMatch(value);

    private static bool LooksSensitiveIdentifier(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase);

    private static string ClientId(McpOperatorPrincipal principal) => principal.ClientId ?? string.Empty;

    private static bool IsSafeToken(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');

    private static string SerializeParameters(IReadOnlyList<McpOperatorScriptParameter> parameters) => JsonSerializer.Serialize(parameters);
    private static string SerializeSideEffects(IReadOnlyList<McpOperatorScriptSideEffect> effects) => JsonSerializer.Serialize(effects);

    private static IReadOnlyList<McpOperatorScriptParameter> ReadParameters(string json)
    {
        try { return JsonSerializer.Deserialize<McpOperatorScriptParameter[]>(json) ?? []; }
        catch (JsonException) { throw new InvalidOperationException("The persisted script parameter schema is invalid."); }
    }

    private static IReadOnlyList<McpOperatorScriptSideEffect> ReadSideEffects(string json)
    {
        try { return JsonSerializer.Deserialize<McpOperatorScriptSideEffect[]>(json) ?? []; }
        catch (JsonException) { throw new InvalidOperationException("The persisted script side-effect declaration is invalid."); }
    }

    private static McpOperatorScriptVersionRecord ToVersionRecord(McpOperatorScriptRecord record, string action, Guid auditId, DateTimeOffset occurredAtUtc) => new()
    {
        Id = Guid.NewGuid(),
        ScriptRecordId = record.Id,
        ScriptId = record.ScriptId,
        ScriptVersion = record.Version,
        Action = action,
        AcceptedAuditId = auditId,
        Name = record.Name,
        Description = record.Description,
        ShellType = record.ShellType,
        ContentHash = record.ContentHash,
        ManifestHash = record.ManifestHash,
        ParametersJson = record.ParametersJson,
        TimeoutSeconds = record.TimeoutSeconds,
        WorkingDirectory = record.WorkingDirectory,
        DeclaredSideEffectsJson = record.DeclaredSideEffectsJson,
        OccurredAtUtc = occurredAtUtc
    };

    public static bool IsStoredScriptIntegrityValid(McpOperatorScriptRecord record, ScriptDefinition script) =>
        string.Equals(record.Name, script.Name, StringComparison.Ordinal) &&
        string.Equals(record.Description, script.Description ?? string.Empty, StringComparison.Ordinal) &&
        string.Equals(StoredScriptType(record.ShellType), script.ScriptType, StringComparison.Ordinal) &&
        HashMatches(record.ContentHash, script.Content) &&
        HashMatches(record.ManifestHash, script.ManifestJson ?? string.Empty);

    private static bool HashMatches(string expectedHash, string value)
    {
        try
        {
            return IsSha256(expectedHash) && CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(expectedHash.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(Hash(value)));
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static McpOperatorScriptLease ToLease(McpOperatorScriptRecord record) => new(
        record.ScriptId, record.TenantId, record.Subject, record.ClientId, record.McpResource, record.McpInstance,
        record.PolicyId, record.PolicyVersion, record.Name, record.Description, record.ShellType,
        record.ContentHash, record.ManifestHash, ReadParameters(record.ParametersJson), record.TimeoutSeconds,
        record.WorkingDirectory, ReadSideEffects(record.DeclaredSideEffectsJson), record.DeletedAtUtc is not null,
        record.CreatedAtUtc, record.UpdatedAtUtc, record.Version);

    private sealed record PreparedDraft(
        string Name,
        string Description,
        string ShellType,
        string ContentHash,
        string? ManifestJson,
        string ManifestHash,
        IReadOnlyList<McpOperatorScriptParameter> Parameters,
        int TimeoutSeconds,
        string WorkingDirectory,
        IReadOnlyList<McpOperatorScriptSideEffect> DeclaredSideEffects);
}
