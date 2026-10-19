using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetRatel.Shared.Operations;

/// <summary>
/// Content-free proof of an exact confirmed request. Neither an opaque plan
/// token, idempotency capability nor business request content enters the JWT.
/// </summary>
public sealed record McpOperatorReplayBinding(string PlanTokenHash, string IdempotencyKeyHash, string PayloadHash)
{
    public bool IsValid => IsHash(PlanTokenHash) && IsHash(IdempotencyKeyHash) && IsHash(PayloadHash);

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    private static bool IsHash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
}

/// <summary>
/// The existing job confirmation payload serialization, shared by the API and
/// the HTTP MCP proof issuer. Nullable/default fields and their order preserve
/// already-issued canonical plan hashes. This is not an authorization decision.
/// </summary>
public static class McpOperatorJobConfirmationPayload
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static bool IsMutation(string action) => action is "create" or "update" or "delete" or
        "param_add" or "param_update" or "param_delete" or "step_add" or "step_update" or "step_reorder" or "step_delete";

    public static string MutationHash(string action, JsonElement request)
    {
        if (!IsMutation(action)) throw new ArgumentException("A job mutation is required.", nameof(action));
        if (!HasUnambiguousProperties(request)) throw new JsonException("A job mutation has ambiguous properties.");
        var body = request.Deserialize<MutationBody>(ReadOptions) ?? throw new JsonException("A job mutation is required.");
        return MutationHash(action, body);
    }

    public static string RunActionHash(string action, ulong runId, string? reason)
    {
        if (action is not ("cancel" or "delete")) throw new ArgumentException("A job run action is required.", nameof(action));
        return McpOperatorReplayBinding.Hash(JsonSerializer.Serialize(new { action, runId, reason = BoundedReason(reason) }));
    }

    /// <summary>
    /// Rejects invalid or ambiguous shapes before the MCP host's business
    /// adapter runs. The API also uses its already-bound typed request to
    /// check that the signed proof belongs to the actual confirmation body.
    /// </summary>
    public static bool TryCreateBinding(string tool, string operation, JsonElement request, out McpOperatorReplayBinding? binding)
    {
        binding = null;
        if (request.ValueKind != JsonValueKind.Object || !HasUnambiguousProperties(request)) return false;
        try
        {
            string? planToken;
            string? idempotencyKey;
            string payloadHash;
            if (tool == "netratel_jobs" && IsMutation(operation))
            {
                var body = request.Deserialize<MutationBody>(ReadOptions);
                if (body is null) return false;
                planToken = body.PlanToken;
                idempotencyKey = body.IdempotencyKey;
                payloadHash = MutationHash(operation, body);
            }
            else if (tool == "netratel_job_runs" && operation is "cancel" or "delete")
            {
                var body = request.Deserialize<RunActionBody>(ReadOptions);
                if (body?.JobRunId is not > 0) return false;
                planToken = body.PlanToken;
                idempotencyKey = body.IdempotencyKey;
                payloadHash = RunActionHash(operation, body.JobRunId.Value, body.Reason);
            }
            else return false;

            if (!IsOpaque(planToken) || !IsOpaque(idempotencyKey)) return false;
            binding = new(McpOperatorReplayBinding.Hash(planToken!), McpOperatorReplayBinding.Hash(idempotencyKey!), payloadHash);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static McpOperatorReplayBinding RunActionBinding(string action, ulong runId, string? reason, string planToken, string idempotencyKey) =>
        new(McpOperatorReplayBinding.Hash(planToken), McpOperatorReplayBinding.Hash(idempotencyKey), RunActionHash(action, runId, reason));

    private static string MutationHash(string action, MutationBody body) => McpOperatorReplayBinding.Hash(JsonSerializer.Serialize(new
    {
        action, body.JobId, body.ExpectedVersion, body.Job, body.ParameterId, body.Parameter, body.StepId, body.Step, body.Ordinal
    }));

    private static string BoundedReason(string? value) => string.IsNullOrWhiteSpace(value) ? "operator_requested" :
        value.Trim().Length > 128 || value.Any(char.IsControl) ? "operator_requested" : value.Trim();

    private static bool IsOpaque(string? value) => value is { Length: >= 32 and <= 128 } &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool HasUnambiguousProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
                if (!names.Add(property.Name) || !HasUnambiguousProperties(property.Value)) return false;
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                if (!HasUnambiguousProperties(item)) return false;
        }
        return true;
    }

    // The extra root fields belong to the validated MCP envelope. They never
    // enter the business hash; tenant/agent are separately signed and checked.
    private sealed record MutationBody(
        JobDraft? Job = null, long? JobId = null, long? ExpectedVersion = null,
        long? ParameterId = null, JobParameter? Parameter = null,
        long? StepId = null, JobStep? Step = null, int? Ordinal = null,
        string? PlanToken = null, string? IdempotencyKey = null, int? TenantId = null, Guid? AgentId = null);

    private sealed record JobDraft(string Name, string FolderPath, string? Description = null, string? OptionsJson = null);
    private sealed record JobParameter(string Name, string Type, bool Required, string? Description = null,
        string? DefaultValue = null, IReadOnlyList<string>? Options = null, string? SecretReference = null, long? Id = null);
    private sealed record JobStep(int? Ordinal, long ScriptId, long ScriptVersion, string ScriptContentHash,
        bool Enabled = true, long? Id = null);
    private sealed record RunActionBody(string? Reason = null, string? PlanToken = null, string? IdempotencyKey = null,
        ulong? JobRunId = null, int? TenantId = null, Guid? AgentId = null);
}
