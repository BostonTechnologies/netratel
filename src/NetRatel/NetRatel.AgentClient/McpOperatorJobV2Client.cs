using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Typed metadata for a caller-owned V2 job definition.</summary>
public sealed record McpOperatorJobDraftV2(
    string Name,
    string FolderPath,
    string? Description = null,
    string? OptionsJson = null);

/// <summary>
/// A bounded V2 job parameter. Secret parameters name only an opaque
/// target-local reference and never accept a plaintext default.
/// </summary>
public sealed record McpOperatorJobParameterV2(
    string Name,
    string Type,
    bool Required,
    string? Description = null,
    string? DefaultValue = null,
    IReadOnlyList<string>? Options = null,
    string? SecretReference = null);

/// <summary>
/// One V2 job step. It must identify the exact reviewed script revision that
/// the API will validate against the caller-owned script library.
/// </summary>
public sealed record McpOperatorJobStepV2(
    int? Ordinal,
    long ScriptId,
    long ScriptVersion,
    string ScriptContentHash,
    bool Enabled = true);

/// <summary>
/// The constrained mutation fields accepted by the V2 job lifecycle. The
/// action passed to the operation determines the only valid field combination.
/// </summary>
public sealed record McpOperatorJobMutationV2(
    McpOperatorJobDraftV2? Job = null,
    long? JobId = null,
    long? ExpectedVersion = null,
    long? ParameterId = null,
    McpOperatorJobParameterV2? Parameter = null,
    long? StepId = null,
    McpOperatorJobStepV2? Step = null,
    int? Ordinal = null);

/// <summary>Typed inputs used when previewing or starting one V2 job run.</summary>
public sealed record McpOperatorJobRunV2(IReadOnlyDictionary<string, string>? Inputs = null);

/// <summary>Optional bounded rationale for a V2 job-run cancellation or deletion.</summary>
public sealed record McpOperatorJobRunActionV2(string? Reason = null);

/// <summary>
/// Route-bound V2 job and job-run lifecycle for one exact tenant and agent.
/// Mutations, starts, cancellations, and retention deletion require a matching
/// preview, confirmation-plan credentials, and idempotency key.
/// </summary>
public interface IMcpOperatorJobV2Client
{
    Task<JsonNode?> ListAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetDetailsAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetParametersAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetStepsAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ValidateAsync(McpOperatorV2Target target, McpOperatorJobDraftV2 draft, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewMutationAsync(McpOperatorV2Target target, string action, McpOperatorJobMutationV2 mutation, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmMutationAsync(McpOperatorV2Target target, string action, McpOperatorJobMutationV2 mutation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListRunsAsync(McpOperatorV2Target target, long? jobId = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> QueryRunsAsync(McpOperatorV2Target target, long? jobId = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetRunAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetRunStepsAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetRunLogsAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewStartRunAsync(McpOperatorV2Target target, long jobId, McpOperatorJobRunV2 run, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmStartRunAsync(McpOperatorV2Target target, long jobId, McpOperatorJobRunV2 run, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRunActionAsync(McpOperatorV2Target target, string action, long runId, McpOperatorJobRunActionV2 request, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRunActionAsync(McpOperatorV2Target target, string action, long runId, McpOperatorJobRunActionV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Route-bound V2 job facade. It derives every API path from an exact
/// tenant-agent pair and cannot dispatch through legacy job routes.
/// </summary>
public sealed class McpOperatorJobV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorJobV2Client
{
    private const int MaximumParameters = 32;
    private const int MaximumSteps = 64;
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(Path(target, string.Empty), cancellationToken);

    public Task<JsonNode?> GetAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default) =>
        _client.GetAsync(JobPath(target, jobId), cancellationToken);

    public Task<JsonNode?> GetDetailsAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{JobPath(target, jobId)}/details", cancellationToken);

    public Task<JsonNode?> GetParametersAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{JobPath(target, jobId)}/params", cancellationToken);

    public Task<JsonNode?> GetStepsAsync(McpOperatorV2Target target, long jobId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{JobPath(target, jobId)}/steps", cancellationToken);

    public Task<JsonNode?> ValidateAsync(McpOperatorV2Target target, McpOperatorJobDraftV2 draft, CancellationToken cancellationToken = default) =>
        _client.SendAsync(HttpMethod.Post, Path(target, "/validate"), DraftBody(draft), cancellationToken);

    public Task<JsonNode?> PreviewMutationAsync(McpOperatorV2Target target, string action, McpOperatorJobMutationV2 mutation, CancellationToken cancellationToken = default) =>
        SendMutationAsync(target, action, mutation, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmMutationAsync(McpOperatorV2Target target, string action, McpOperatorJobMutationV2 mutation, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendMutationAsync(target, action, mutation, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> ListRunsAsync(McpOperatorV2Target target, long? jobId = null, CancellationToken cancellationToken = default) =>
        GetRunsAsync(target, "/runs", jobId, cancellationToken);

    public Task<JsonNode?> QueryRunsAsync(McpOperatorV2Target target, long? jobId = null, CancellationToken cancellationToken = default) =>
        GetRunsAsync(target, "/runs/query", jobId, cancellationToken);

    public Task<JsonNode?> GetRunAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default) =>
        _client.GetAsync(RunPath(target, runId), cancellationToken);

    public Task<JsonNode?> GetRunStepsAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{RunPath(target, runId)}/steps", cancellationToken);

    public Task<JsonNode?> GetRunLogsAsync(McpOperatorV2Target target, long runId, CancellationToken cancellationToken = default) =>
        _client.GetAsync($"{RunPath(target, runId)}/logs", cancellationToken);

    public Task<JsonNode?> PreviewStartRunAsync(McpOperatorV2Target target, long jobId, McpOperatorJobRunV2 run, CancellationToken cancellationToken = default) =>
        SendStartRunAsync(target, jobId, run, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmStartRunAsync(McpOperatorV2Target target, long jobId, McpOperatorJobRunV2 run, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendStartRunAsync(target, jobId, run, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewRunActionAsync(McpOperatorV2Target target, string action, long runId, McpOperatorJobRunActionV2 request, CancellationToken cancellationToken = default) =>
        SendRunActionAsync(target, action, runId, request, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmRunActionAsync(McpOperatorV2Target target, string action, long runId, McpOperatorJobRunActionV2 request, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendRunActionAsync(target, action, runId, request, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendMutationAsync(
        McpOperatorV2Target target,
        string action,
        McpOperatorJobMutationV2 mutation,
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

    private Task<JsonNode?> GetRunsAsync(McpOperatorV2Target target, string suffix, long? jobId, CancellationToken cancellationToken)
    {
        if (jobId is <= 0) throw McpOperatorTaskV2Client.Invalid("jobId");
        return _client.GetAsync(
            McpOperatorTaskV2Client.WithQuery(
                Path(target, suffix),
                ("jobId", jobId?.ToString(CultureInfo.InvariantCulture))),
            cancellationToken);
    }

    private Task<JsonNode?> SendStartRunAsync(
        McpOperatorV2Target target,
        long jobId,
        McpOperatorJobRunV2 run,
        bool confirmed,
        string? planToken,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var body = RunBody(run);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(
            HttpMethod.Post,
            $"{JobPath(target, jobId)}/runs/{(confirmed ? "confirm" : "preview")}",
            body,
            cancellationToken);
    }

    private Task<JsonNode?> SendRunActionAsync(
        McpOperatorV2Target target,
        string action,
        long runId,
        McpOperatorJobRunActionV2 request,
        bool confirmed,
        string? planToken,
        string? idempotencyKey,
        CancellationToken cancellationToken)
    {
        var actionName = action is "cancel" or "delete"
            ? action
            : throw McpOperatorTaskV2Client.Invalid("job run action");
        var body = RunActionBody(request);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(
            HttpMethod.Post,
            $"{RunPath(target, runId)}/{actionName}/{(confirmed ? "confirm" : "preview")}",
            body,
            cancellationToken);
    }

    private static JsonObject MutationBody(string action, McpOperatorJobMutationV2 mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        return action switch
        {
            "create" when mutation is
            {
                Job: not null, JobId: null, ExpectedVersion: null, ParameterId: null, Parameter: null, StepId: null, Step: null, Ordinal: null
            } => new JsonObject { ["job"] = DraftBody(mutation.Job) },
            "update" when mutation is
            {
                Job: not null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: null, Step: null, Ordinal: null
            } => JobBody(mutation, DraftBody(mutation.Job)),
            "delete" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: null, Step: null, Ordinal: null
            } => JobBody(mutation),
            "param_add" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: not null, StepId: null, Step: null, Ordinal: null
            } => JobBody(mutation, parameter: ParameterBody(mutation.Parameter)),
            "param_update" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: > 0, Parameter: not null, StepId: null, Step: null, Ordinal: null
            } => JobBody(mutation, parameter: ParameterBody(mutation.Parameter)),
            "param_delete" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: > 0, Parameter: null, StepId: null, Step: null, Ordinal: null
            } => JobBody(mutation),
            "step_add" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: null, Step: not null, Ordinal: null
            } => JobBody(mutation, step: StepBody(mutation.Step)),
            "step_update" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: > 0, Step: not null, Ordinal: null
            } => JobBody(mutation, step: StepBody(mutation.Step)),
            "step_reorder" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: > 0, Step: null, Ordinal: > 0 and <= MaximumSteps
            } => JobBody(mutation, ordinal: mutation.Ordinal),
            "step_delete" when mutation is
            {
                Job: null, JobId: > 0, ExpectedVersion: > 0, ParameterId: null, Parameter: null, StepId: > 0, Step: null, Ordinal: null
            } => JobBody(mutation),
            _ => throw McpOperatorTaskV2Client.Invalid("job mutation")
        };
    }

    private static JsonObject JobBody(
        McpOperatorJobMutationV2 mutation,
        JsonObject? job = null,
        JsonObject? parameter = null,
        JsonObject? step = null,
        int? ordinal = null)
    {
        var body = new JsonObject
        {
            ["jobId"] = mutation.JobId,
            ["expectedVersion"] = mutation.ExpectedVersion
        };
        if (job is not null) body["job"] = job;
        if (mutation.ParameterId is not null) body["parameterId"] = mutation.ParameterId;
        if (parameter is not null) body["parameter"] = parameter;
        if (mutation.StepId is not null) body["stepId"] = mutation.StepId;
        if (step is not null) body["step"] = step;
        if (ordinal is not null) body["ordinal"] = ordinal;
        return body;
    }

    private static JsonObject DraftBody(McpOperatorJobDraftV2 draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        var name = NormalizeRequiredText(draft.Name, 120, "job name");
        var folderPath = NormalizeRequiredText(draft.FolderPath, 512, "job folderPath");
        var description = NormalizeOptionalText(draft.Description, 512, "job description");
        var optionsJson = NormalizeOptionsJson(draft.OptionsJson);
        return new JsonObject
        {
            ["name"] = name,
            ["folderPath"] = folderPath,
            ["description"] = description,
            ["optionsJson"] = optionsJson
        };
    }

    private static JsonObject ParameterBody(McpOperatorJobParameterV2 parameter)
    {
        ArgumentNullException.ThrowIfNull(parameter);
        var name = NormalizeIdentifier(parameter.Name, 64, "job parameter name");
        var type = parameter.Type?.Trim().ToLowerInvariant();
        if (type is not ("string" or "integer" or "boolean" or "choice" or "secret_reference"))
            throw McpOperatorTaskV2Client.Invalid("job parameter type");

        var options = OptionsBody(parameter.Options);
        if ((type == "choice" && options.Count == 0) ||
            (type == "secret_reference" && (parameter.DefaultValue is not null || parameter.SecretReference is null)) ||
            (type != "secret_reference" && parameter.SecretReference is not null) ||
            (LooksSensitiveIdentifier(name) && (parameter.DefaultValue is not null || options.Count > 0)))
        {
            throw McpOperatorTaskV2Client.Invalid("job parameter");
        }

        if (parameter.DefaultValue is { Length: > 1024 } || parameter.DefaultValue?.Any(char.IsControl) == true)
            throw McpOperatorTaskV2Client.Invalid("job parameter defaultValue");

        var secretReference = parameter.SecretReference is null
            ? null
            : NormalizeIdentifier(parameter.SecretReference, 128, "job parameter secretReference");
        return new JsonObject
        {
            ["name"] = name,
            ["type"] = type,
            ["required"] = parameter.Required,
            ["description"] = NormalizeOptionalText(parameter.Description, 256, "job parameter description"),
            ["defaultValue"] = parameter.DefaultValue,
            ["options"] = options,
            ["secretReference"] = secretReference
        };
    }

    private static JsonArray OptionsBody(IReadOnlyList<string>? options)
    {
        if (options is null) return [];
        if (options.Count > MaximumParameters)
            throw McpOperatorTaskV2Client.Invalid("job parameter options");

        var values = options
            .Select(option => NormalizeRequiredText(option, 128, "job parameter option"))
            .ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw McpOperatorTaskV2Client.Invalid("job parameter options");
        return new JsonArray(values.Select(value => JsonValue.Create(value)).ToArray());
    }

    private static JsonObject StepBody(McpOperatorJobStepV2 step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Ordinal is < 1 or > MaximumSteps ||
            step.ScriptId <= 0 ||
            step.ScriptVersion <= 0 ||
            step.ScriptContentHash is not { } scriptContentHash ||
            !McpOperatorTaskV2Client.IsHex(scriptContentHash, 64))
        {
            throw McpOperatorTaskV2Client.Invalid("job step");
        }

        return new JsonObject
        {
            ["ordinal"] = step.Ordinal,
            ["scriptId"] = step.ScriptId,
            ["scriptVersion"] = step.ScriptVersion,
            ["scriptContentHash"] = scriptContentHash.ToUpperInvariant(),
            ["enabled"] = step.Enabled
        };
    }

    private static JsonObject RunBody(McpOperatorJobRunV2 run)
    {
        ArgumentNullException.ThrowIfNull(run);
        if (run.Inputs is { Count: > MaximumParameters })
            throw McpOperatorTaskV2Client.Invalid("job run inputs");

        var inputs = new JsonObject();
        foreach (var pair in run.Inputs ?? new Dictionary<string, string>())
        {
            if (!McpOperatorTaskV2Client.IsIdentifier(pair.Key) || !IsBoundedNonControlText(pair.Value, 1024))
                throw McpOperatorTaskV2Client.Invalid("job run inputs");
            inputs[pair.Key] = pair.Value;
        }

        return new JsonObject { ["inputs"] = inputs };
    }

    private static JsonObject RunActionBody(McpOperatorJobRunActionV2 request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Reason is not null && !IsBoundedNonControlText(request.Reason.Trim(), 128))
            throw McpOperatorTaskV2Client.Invalid("job run reason");

        return new JsonObject { ["reason"] = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim() };
    }

    private static string ValidateMutationAction(string? action) =>
        action is "create" or "update" or "delete" or "param_add" or "param_update" or "param_delete" or
            "step_add" or "step_update" or "step_reorder" or "step_delete"
            ? action
            : throw McpOperatorTaskV2Client.Invalid("job mutation action");

    private static string? NormalizeOptionsJson(string? optionsJson)
    {
        if (string.IsNullOrWhiteSpace(optionsJson)) return null;
        if (optionsJson.Length > 8 * 1024)
            throw McpOperatorTaskV2Client.Invalid("job optionsJson");

        try
        {
            using var document = JsonDocument.Parse(optionsJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
                throw McpOperatorTaskV2Client.Invalid("job optionsJson");
            return JsonSerializer.Serialize(document.RootElement);
        }
        catch (JsonException)
        {
            throw McpOperatorTaskV2Client.Invalid("job optionsJson");
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

    private static bool LooksSensitiveIdentifier(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("key", StringComparison.OrdinalIgnoreCase);

    private string JobPath(McpOperatorV2Target target, long jobId) =>
        $"{Path(target, string.Empty)}/{ValidatePositiveId(jobId, "jobId").ToString(CultureInfo.InvariantCulture)}";

    private string RunPath(McpOperatorV2Target target, long runId) =>
        $"{Path(target, string.Empty)}/runs/{ValidatePositiveId(runId, "jobRunId").ToString(CultureInfo.InvariantCulture)}";

    private static long ValidatePositiveId(long value, string field) =>
        value > 0 ? value : throw McpOperatorTaskV2Client.Invalid(field);

    private string Path(McpOperatorV2Target target, string suffix)
    {
        target.Validate();
        McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "job");
        return $"/api/v2/mcp/operator/agents/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}/jobs{suffix}";
    }
}
