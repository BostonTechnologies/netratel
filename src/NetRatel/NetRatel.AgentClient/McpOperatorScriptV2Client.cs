using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>
/// One bounded parameter declaration for a caller-owned V2 script revision.
/// Secret parameters identify only a target-local reference, never a value.
/// </summary>
public sealed record McpOperatorScriptParameterV2(
    string Name,
    string Type,
    bool Required,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null,
    string? SecretReference = null);

/// <summary>
/// Typed immutable content for a V2 script-library revision. The client
/// verifies <see cref="ContentHash"/> before dispatch; policy-specific limits
/// remain enforced by the API.
/// </summary>
public sealed record McpOperatorScriptDraftV2(
    string Name,
    string Description,
    string ShellType,
    string Content,
    string ContentHash,
    IReadOnlyList<McpOperatorScriptParameterV2> Parameters,
    int TimeoutSeconds,
    string WorkingDirectory,
    IReadOnlyList<int> DeclaredSideEffects,
    string? ManifestJson = null);

/// <summary>
/// A constrained V2 script-library mutation. The permitted field combination
/// is selected by the action passed to the preview or confirmation operation.
/// </summary>
public sealed record McpOperatorScriptMutationV2(
    McpOperatorScriptDraftV2? Script = null,
    long? ScriptId = null,
    long? ExpectedVersion = null,
    string? ManifestJson = null);

/// <summary>
/// The exact script revision and typed parameter values used for one V2 run.
/// </summary>
public sealed record McpOperatorScriptRunV2(
    long ScriptId,
    long Version,
    string ContentHash,
    IReadOnlyDictionary<string, string>? Parameters = null);

/// <summary>
/// Route-bound V2 script-library lifecycle for one exact tenant and agent.
/// Mutations and runs are split into preview and confirmation operations.
/// </summary>
public interface IMcpOperatorScriptV2Client
{
    Task<JsonNode?> ListAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, long scriptId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetParametersAsync(McpOperatorV2Target target, long scriptId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ValidateAsync(McpOperatorV2Target target, McpOperatorScriptDraftV2 draft, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewMutationAsync(McpOperatorV2Target target, string action, McpOperatorScriptMutationV2 mutation, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmMutationAsync(McpOperatorV2Target target, string action, McpOperatorScriptMutationV2 mutation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRunAsync(McpOperatorV2Target target, McpOperatorScriptRunV2 run, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRunAsync(McpOperatorV2Target target, McpOperatorScriptRunV2 run, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 script facade. It only addresses a persisted tenant-agent
/// pair, derives every API path, and has no legacy script-library fallback.
/// </summary>
public sealed class McpOperatorScriptV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorScriptV2Client
{
    private const int MaximumContentBytes = 64 * 1024;
    private const int MaximumManifestBytes = 16 * 1024;
    private const int MaximumTimeoutSeconds = 60 * 60;
    private const int MaximumParameters = 32;
    private const int MaximumSideEffects = 5;
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, string.Empty), cancellationToken);

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, long scriptId, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, $"/{ValidateScriptId(scriptId).ToString(CultureInfo.InvariantCulture)}"), cancellationToken);

    public Task<JsonNode?> GetParametersAsync(McpOperatorV2Target target, long scriptId, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, $"/{ValidateScriptId(scriptId).ToString(CultureInfo.InvariantCulture)}/params"), cancellationToken);

    public Task<JsonNode?> ValidateAsync(McpOperatorV2Target target, McpOperatorScriptDraftV2 draft, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/validate"), DraftBody(draft), cancellationToken);

    public Task<JsonNode?> PreviewMutationAsync(McpOperatorV2Target target, string action, McpOperatorScriptMutationV2 mutation, CancellationToken cancellationToken = default) =>
        SendMutationAsync(target, action, mutation, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmMutationAsync(McpOperatorV2Target target, string action, McpOperatorScriptMutationV2 mutation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendMutationAsync(target, action, mutation, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewRunAsync(McpOperatorV2Target target, McpOperatorScriptRunV2 run, CancellationToken cancellationToken = default) =>
        SendRunAsync(target, run, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmRunAsync(McpOperatorV2Target target, McpOperatorScriptRunV2 run, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendRunAsync(target, run, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendMutationAsync(
        McpOperatorV2Target target,
        string action,
        McpOperatorScriptMutationV2 mutation,
        bool confirmed,
        string? planToken,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var actionName = ValidateMutationAction(action);
        var body = MutationBody(actionName, mutation);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(
            HttpMethod.Post,
            Path(target, $"/{(confirmed ? "confirm" : "preview")}/{actionName}"),
            body,
            cancellationToken);
    }

    private Task<JsonNode?> SendRunAsync(
        McpOperatorV2Target target,
        McpOperatorScriptRunV2 run,
        bool confirmed,
        string? planToken,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(run);
        var scriptId = ValidateScriptId(run.ScriptId);
        var body = RunBody(run);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(
            HttpMethod.Post,
            Path(target, $"/{scriptId.ToString(CultureInfo.InvariantCulture)}/runs/{(confirmed ? "confirm" : "preview")}"),
            body,
            cancellationToken);
    }

    private static JsonObject MutationBody(string action, McpOperatorScriptMutationV2 mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);

        return action switch
        {
            "create" when mutation is { Script: not null, ScriptId: null, ExpectedVersion: null, ManifestJson: null } =>
                new JsonObject { ["script"] = DraftBody(mutation.Script) },
            "update" when mutation is { Script: not null, ScriptId: > 0, ExpectedVersion: > 0, ManifestJson: null } =>
                new JsonObject
                {
                    ["script"] = DraftBody(mutation.Script),
                    ["scriptId"] = mutation.ScriptId,
                    ["expectedVersion"] = mutation.ExpectedVersion
                },
            "parse_manifest" when mutation is { Script: null, ScriptId: > 0, ExpectedVersion: > 0, ManifestJson: not null } =>
                new JsonObject
                {
                    ["scriptId"] = mutation.ScriptId,
                    ["expectedVersion"] = mutation.ExpectedVersion,
                    ["manifestJson"] = NormalizeManifest(mutation.ManifestJson)
                },
            "delete" when mutation is { Script: null, ScriptId: > 0, ExpectedVersion: > 0, ManifestJson: null } =>
                new JsonObject
                {
                    ["scriptId"] = mutation.ScriptId,
                    ["expectedVersion"] = mutation.ExpectedVersion
                },
            _ => throw McpOperatorTaskV2Client.Invalid("script mutation")
        };
    }

    private static JsonObject DraftBody(McpOperatorScriptDraftV2 draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = NormalizeRequiredText(draft.Name, 120, "script name");
        var description = NormalizeOptionalText(draft.Description, 512, "script description") ?? string.Empty;
        var shellType = NormalizeRequiredText(draft.ShellType, 32, "script shellType").ToLowerInvariant();
        var content = draft.Content ?? throw McpOperatorTaskV2Client.Invalid("script content");
        var contentBytes = GetUtf8ByteCount(content, "script content");
        if (draft.ContentHash is not { } contentHash ||
            contentBytes is < 1 or > MaximumContentBytes ||
            !McpOperatorTaskV2Client.IsHex(contentHash, 64) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(contentHash.ToUpperInvariant()),
                Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(StrictUtf8.GetBytes(content))))))
        {
            throw McpOperatorTaskV2Client.Invalid("script content/contentHash");
        }

        var parameters = ParametersBody(draft.Parameters);
        var workingDirectory = NormalizeRequiredText(draft.WorkingDirectory, 4096, "script workingDirectory");
        if (draft.TimeoutSeconds is < 1 or > MaximumTimeoutSeconds)
            throw McpOperatorTaskV2Client.Invalid("script timeoutSeconds");

        var sideEffects = SideEffectsBody(draft.DeclaredSideEffects);
        return new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["shellType"] = shellType,
            ["content"] = content,
            ["contentHash"] = contentHash.ToUpperInvariant(),
            ["parameters"] = parameters,
            ["timeoutSeconds"] = draft.TimeoutSeconds,
            ["workingDirectory"] = workingDirectory,
            ["declaredSideEffects"] = sideEffects,
            ["manifestJson"] = NormalizeOptionalManifest(draft.ManifestJson)
        };
    }

    private static JsonArray ParametersBody(IReadOnlyList<McpOperatorScriptParameterV2>? parameters)
    {
        if (parameters is null || parameters.Count > MaximumParameters)
            throw McpOperatorTaskV2Client.Invalid("script parameters");

        var names = new HashSet<string>(StringComparer.Ordinal);
        var body = new JsonArray();
        foreach (var parameter in parameters)
        {
            ArgumentNullException.ThrowIfNull(parameter);
            var name = NormalizeIdentifier(parameter.Name, 64, "script parameter name");
            if (!names.Add(name))
                throw McpOperatorTaskV2Client.Invalid("script parameter names");

            var type = parameter.Type?.Trim().ToLowerInvariant();
            if (type is not ("string" or "integer" or "boolean" or "choice" or "secret_reference"))
                throw McpOperatorTaskV2Client.Invalid("script parameter type");

            var description = NormalizeOptionalText(parameter.Description, 256, "script parameter description");
            ValidateParameterDefault(parameter.DefaultValue);
            var options = ParameterOptionsBody(parameter.Options);
            var secretReference = parameter.SecretReference is null
                ? null
                : NormalizeIdentifier(parameter.SecretReference, 128, "script parameter secretReference");

            if ((type == "choice" && options.Count == 0) ||
                (type == "secret_reference" && (parameter.DefaultValue is not null || secretReference is null)) ||
                (type != "secret_reference" && secretReference is not null) ||
                (LooksSensitiveIdentifier(name) && (parameter.DefaultValue is not null || options.Count > 0)))
            {
                throw McpOperatorTaskV2Client.Invalid("script parameter");
            }

            body.Add(new JsonObject
            {
                ["name"] = name,
                ["type"] = type,
                ["required"] = parameter.Required,
                ["description"] = description,
                ["defaultValue"] = parameter.DefaultValue,
                ["options"] = options,
                ["secretReference"] = secretReference
            });
        }

        return body;
    }

    private static JsonArray ParameterOptionsBody(IReadOnlyList<string>? options)
    {
        if (options is null) return [];
        if (options.Count > 32)
            throw McpOperatorTaskV2Client.Invalid("script parameter options");

        var values = options
            .Select(option => NormalizeRequiredText(option, 128, "script parameter option"))
            .ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw McpOperatorTaskV2Client.Invalid("script parameter options");

        return new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray());
    }

    private static JsonArray SideEffectsBody(IReadOnlyList<int>? values)
    {
        if (values is not { Count: > 0 and <= MaximumSideEffects } ||
            values.Any(value => value is < 1 or > 5) ||
            values.Distinct().Count() != values.Count ||
            (values.Contains(1) && values.Count != 1))
        {
            throw McpOperatorTaskV2Client.Invalid("script declaredSideEffects");
        }

        return new JsonArray(values.Order().Select(value => JsonValue.Create(value)).ToArray());
    }

    private static JsonObject RunBody(McpOperatorScriptRunV2 run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Version <= 0 ||
            run.ContentHash is not { } contentHash ||
            !McpOperatorTaskV2Client.IsHex(contentHash, 64) ||
            run.Parameters is { Count: > MaximumParameters })
        {
            throw McpOperatorTaskV2Client.Invalid("script run");
        }

        var parameters = new JsonObject();
        foreach (var pair in run.Parameters ?? new Dictionary<string, string>())
        {
            if (!McpOperatorTaskV2Client.IsIdentifier(pair.Key) || !IsBoundedNonControlText(pair.Value, 1024))
                throw McpOperatorTaskV2Client.Invalid("script run parameters");

            parameters[pair.Key] = pair.Value;
        }

        return new JsonObject
        {
            ["version"] = run.Version,
            ["contentHash"] = contentHash.ToUpperInvariant(),
            ["parameters"] = parameters
        };
    }

    private static string ValidateMutationAction(string? action) =>
        action is "create" or "update" or "parse_manifest" or "delete"
            ? action
            : throw McpOperatorTaskV2Client.Invalid("script action");

    private static long ValidateScriptId(long scriptId) =>
        scriptId > 0 ? scriptId : throw McpOperatorTaskV2Client.Invalid("scriptId");

    private static string NormalizeManifest(string? manifest)
    {
        var normalized = NormalizeOptionalManifest(manifest);
        return normalized ?? throw McpOperatorTaskV2Client.Invalid("script manifestJson");
    }

    private static string? NormalizeOptionalManifest(string? manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest)) return null;
        if (GetUtf8ByteCount(manifest, "script manifestJson") > MaximumManifestBytes)
            throw McpOperatorTaskV2Client.Invalid("script manifestJson");

        try
        {
            using var document = JsonDocument.Parse(manifest);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw McpOperatorTaskV2Client.Invalid("script manifestJson");
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            throw McpOperatorTaskV2Client.Invalid("script manifestJson");
        }
    }

    private static string NormalizeRequiredText(string? value, int maximumLength, string field)
    {
        var normalized = value?.Trim();
        return IsBoundedNonControlText(normalized, maximumLength)
            ? normalized!
            : throw McpOperatorTaskV2Client.Invalid(field);
    }

    private static string? NormalizeOptionalText(string? value, int maximumLength, string field)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        return normalized.Length == 0 || IsBoundedNonControlText(normalized, maximumLength)
            ? normalized
            : throw McpOperatorTaskV2Client.Invalid(field);
    }

    private static string NormalizeIdentifier(string? value, int maximumLength, string field)
    {
        if (value is null || value.Length > maximumLength || !McpOperatorTaskV2Client.IsIdentifier(value))
            throw McpOperatorTaskV2Client.Invalid(field);

        return value;
    }

    private static bool IsBoundedNonControlText(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && value.All(character => !char.IsControl(character));

    private static void ValidateParameterDefault(string? value)
    {
        if (value is { Length: > 1024 } || value?.Any(char.IsControl) == true)
            throw McpOperatorTaskV2Client.Invalid("script parameter defaultValue");
    }

    private static int GetUtf8ByteCount(string value, string field)
    {
        try
        {
            return StrictUtf8.GetByteCount(value);
        }
        catch (EncoderFallbackException)
        {
            throw McpOperatorTaskV2Client.Invalid(field);
        }
    }

    private static bool LooksSensitiveIdentifier(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase);

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "script");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}/scripts{suffix}";
    }
}
