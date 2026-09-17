using System.Globalization;
using System.Text.Json.Nodes;

namespace NetRatel.AgentClient;

/// <summary>Explicit policy environment values accepted by the V2 operator API.</summary>
public enum McpOperatorPolicyEnvironmentV2 : short
{
    Development = 1,
    Production = 2
}

/// <summary>Explicit allow or deny effect for a persisted operator policy.</summary>
public enum McpOperatorPolicyEffectV2 : short
{
    Deny = 1,
    Allow = 2
}

/// <summary>Bounded selector kinds for the delegated policy principal.</summary>
public enum McpOperatorPolicyPrincipalSelectorKindV2 : short
{
    OAuthSubject = 1,
    OAuthClientId = 2,
    OidcGroup = 3,
    MappedRole = 4,
    ServicePrincipal = 5
}

/// <summary>Bounded selector kinds for the affected control-plane target.</summary>
public enum McpOperatorPolicyTargetSelectorKindV2 : short
{
    ExactAgent = 1,
    Tenant = 2,
    ClientTag = 3,
    ControlPlane = 4,
    DevelopmentEnvironment = 5
}

/// <summary>Server-owned target classifications permitted in policy constraints.</summary>
public enum McpOperatorPolicyTargetClassificationV2 : short
{
    Unknown = 0,
    DedicatedQa = 1,
    DevelopmentSafe = 2,
    ManagedStandard = 3,
    Restricted = 4,
    CriticalInfrastructure = 5
}

/// <summary>Policy confirmation classes accepted by the operator authority.</summary>
public enum McpOperatorPolicyConfirmationClassV2 : short
{
    None = 0,
    StandardMutation = 1,
    RemoteExecution = 2,
    Destructive = 3,
    FleetWide = 4,
    CredentialIssuance = 5,
    PolicyAdministration = 6
}

/// <summary>Operation-family flags evaluated by the policy authority.</summary>
[Flags]
public enum McpOperatorPolicyOperationFamilyV2
{
    None = 0,
    Observability = 1 << 0,
    FileRead = 1 << 1,
    FileWrite = 1 << 2,
    TerminalRead = 1 << 3,
    TerminalExecute = 1 << 4,
    ScriptsRead = 1 << 5,
    ScriptsWrite = 1 << 6,
    AutomationRead = 1 << 7,
    AutomationWrite = 1 << 8,
    JobExecution = 1 << 9,
    TaskExecution = 1 << 10,
    Requests = 1 << 11,
    Onboarding = 1 << 12,
    ClientAdministration = 1 << 13,
    TenantAdministration = 1 << 14,
    Notifications = 1 << 15,
    Events = 1 << 16,
    Connectivity = 1 << 17,
    PolicyAdministration = 1 << 18
}

/// <summary>One bounded delegated principal selector.</summary>
public sealed record McpOperatorPolicyPrincipalSelectorV2(
    McpOperatorPolicyPrincipalSelectorKindV2 Kind,
    string Value);

/// <summary>One exact persisted target selector; no hostname-derived targeting is accepted.</summary>
public sealed record McpOperatorPolicyTargetSelectorV2(
    McpOperatorPolicyTargetSelectorKindV2 Kind,
    int TenantId,
    Guid? AgentId = null,
    string? ClientTag = null);

/// <summary>Optional bounded limits carried by a reviewed operator policy.</summary>
public sealed record McpOperatorPolicyConstraintsV2(
    IReadOnlyList<string>? ReadRoots = null,
    IReadOnlyList<string>? WriteRoots = null,
    IReadOnlyList<string>? AllowedShells = null,
    IReadOnlyList<string>? WorkingDirectories = null,
    int? MaxCommandDurationSeconds = null,
    int? MaxTerminalIdleSeconds = null,
    int? MaxTerminalLifetimeSeconds = null,
    int? MaxConcurrentTerminalSessions = null,
    int? MaxConcurrentCommands = null,
    int? MaxOutputBytes = null,
    int? MaxArtifactBytes = null,
    int? MaxScriptBytes = null,
    int? MaxJobTargetCount = null,
    int? MaxTaskTargetCount = null,
    int? MaxFanOut = null,
    IReadOnlyList<McpOperatorPolicyTargetClassificationV2>? AllowedTargetClassifications = null,
    int? MaxOnboardingCodeLifetimeSeconds = null,
    int? MaxOnboardingCodeUses = null,
    McpOperatorPolicyConfirmationClassV2? RequiredConfirmationClass = null,
    bool? DestructiveOperationsAllowed = null,
    bool? AllowActiveTenantDeletion = null);

/// <summary>Closed, reviewed draft accepted by the policy create and replace operations.</summary>
public sealed record McpOperatorPolicyDraftV2(
    string Name,
    McpOperatorPolicyEnvironmentV2 Environment,
    McpOperatorPolicyEffectV2 Effect,
    int Priority,
    McpOperatorPolicyPrincipalSelectorV2 PrincipalSelector,
    McpOperatorPolicyTargetSelectorV2 TargetSelector,
    McpOperatorPolicyOperationFamilyV2 OperationFamily,
    McpOperatorPolicyConstraintsV2 Constraints,
    McpOperatorPolicyTargetClassificationV2? TargetClassification = null,
    string? Operation = null,
    DateTimeOffset? ExpiresAtUtc = null,
    DateTimeOffset? ReviewByUtc = null,
    string? AuditReference = null);

/// <summary>
/// Route-bound V2 policy inspection and lifecycle. Policy mutation always uses
/// a server-issued preview plan plus its matching idempotency credential.
/// </summary>
public interface IMcpOperatorPolicyV2Client
{
    Task<JsonNode?> ListAsync(McpOperatorPolicyEnvironmentV2? environment = null, int? tenantId = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetAsync(Guid policyId, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListChangeAuditsAsync(int? tenantId = null, Guid? policyId = null, Guid? agentId = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> ListAcceptedAuditsAsync(int? tenantId = null, Guid? agentId = null, string? subject = null, int? limit = null, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetTargetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> GetMatchesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default);
    Task<JsonNode?> EvaluateAsync(McpOperatorV2Target target, string tool, string operation, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewCreateAsync(McpOperatorPolicyDraftV2 policy, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmCreateAsync(McpOperatorPolicyDraftV2 policy, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraftV2 policy, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraftV2 policy, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewDisableAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmDisableAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<JsonNode?> PreviewRevokeAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, CancellationToken cancellationToken = default);
    Task<JsonNode?> ConfirmRevokeAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default);
}

/// <summary>
/// Policy facade with only reviewed V2 endpoints. It never accepts a caller
/// supplied route or raw JSON policy payload, and it keeps drafts target-bound.
/// </summary>
public sealed class McpOperatorPolicyV2Client(INetRatelMcpOutboundClient client) : IMcpOperatorPolicyV2Client
{
    private const int KnownOperationFamilies = (1 << 19) - 1;
    private readonly INetRatelMcpOutboundClient _client = client ?? throw new ArgumentNullException(nameof(client));

    public Task<JsonNode?> ListAsync(McpOperatorPolicyEnvironmentV2? environment = null, int? tenantId = null, CancellationToken cancellationToken = default)
    {
        EnsureTarget();
        if (environment is not null) ValidateEnvironment(environment.Value);
        ValidateOptionalTenantId(tenantId);
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(BasePath,
            ("environment", environment?.ToString()),
            ("tenantId", tenantId?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> GetAsync(Guid policyId, CancellationToken cancellationToken = default)
    {
        EnsureTarget();
        return _client.GetAsync($"{BasePath}/policies/{PolicyId(policyId)}", cancellationToken);
    }

    public Task<JsonNode?> ListChangeAuditsAsync(int? tenantId = null, Guid? policyId = null, Guid? agentId = null, CancellationToken cancellationToken = default)
    {
        EnsureTarget();
        ValidateOptionalTenantId(tenantId);
        ValidateOptionalGuid(policyId, "policyId");
        ValidateOptionalGuid(agentId, "agentId");
        if (agentId is not null && tenantId is null) throw McpOperatorTaskV2Client.Invalid("accepted audit target");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery($"{BasePath}/change-audits",
            ("tenantId", tenantId?.ToString(CultureInfo.InvariantCulture)),
            ("policyId", policyId?.ToString("D")),
            ("agentId", agentId?.ToString("D"))), cancellationToken);
    }

    public Task<JsonNode?> ListAcceptedAuditsAsync(int? tenantId = null, Guid? agentId = null, string? subject = null, int? limit = null, CancellationToken cancellationToken = default)
    {
        EnsureTarget();
        ValidateOptionalTenantId(tenantId);
        ValidateOptionalGuid(agentId, "agentId");
        if (agentId is not null && tenantId is null) throw McpOperatorTaskV2Client.Invalid("accepted audit target");
        if (subject is not null && !IsSafeValue(subject, 256)) throw McpOperatorTaskV2Client.Invalid("accepted audit subject");
        if (limit is not null and (< 1 or > 250)) throw McpOperatorTaskV2Client.Invalid("accepted audit limit");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery($"{BasePath}/accepted-audits",
            ("tenantId", tenantId?.ToString(CultureInfo.InvariantCulture)),
            ("agentId", agentId?.ToString("D")),
            ("subject", subject),
            ("limit", limit?.ToString(CultureInfo.InvariantCulture))), cancellationToken);
    }

    public Task<JsonNode?> GetTargetAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(TargetPath(target, "targets"), cancellationToken);

    public Task<JsonNode?> GetMatchesAsync(McpOperatorV2Target target, CancellationToken cancellationToken = default) =>
        _client.GetAsync(TargetPath(target, "matches"), cancellationToken);

    public Task<JsonNode?> EvaluateAsync(McpOperatorV2Target target, string tool, string operation, CancellationToken cancellationToken = default)
    {
        if (!IsCatalogIdentifier(tool) || !IsCatalogIdentifier(operation)) throw McpOperatorTaskV2Client.Invalid("policy tool/operation");
        return _client.GetAsync(McpOperatorTaskV2Client.WithQuery(TargetPath(target, "evaluate"), ("tool", tool), ("operation", operation)), cancellationToken);
    }

    public Task<JsonNode?> PreviewCreateAsync(McpOperatorPolicyDraftV2 policy, CancellationToken cancellationToken = default) =>
        SendCreateAsync(policy, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmCreateAsync(McpOperatorPolicyDraftV2 policy, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendCreateAsync(policy, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraftV2 policy, CancellationToken cancellationToken = default) =>
        SendReplaceAsync(policyId, expectedVersion, policy, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraftV2 policy, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendReplaceAsync(policyId, expectedVersion, policy, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewDisableAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, CancellationToken cancellationToken = default) =>
        SendStateChangeAsync("disable", policyId, expectedVersion, target, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmDisableAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendStateChangeAsync("disable", policyId, expectedVersion, target, true, planToken, idempotencyKey, cancellationToken);

    public Task<JsonNode?> PreviewRevokeAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, CancellationToken cancellationToken = default) =>
        SendStateChangeAsync("revoke", policyId, expectedVersion, target, false, null, null, cancellationToken);

    public Task<JsonNode?> ConfirmRevokeAsync(Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, string planToken, string idempotencyKey, CancellationToken cancellationToken = default) =>
        SendStateChangeAsync("revoke", policyId, expectedVersion, target, true, planToken, idempotencyKey, cancellationToken);

    private Task<JsonNode?> SendCreateAsync(McpOperatorPolicyDraftV2 policy, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var body = new JsonObject { ["policy"] = PolicyBody(policy) };
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{BasePath}/create/{(confirmed ? "confirm" : "preview")}", body, cancellationToken);
    }

    private Task<JsonNode?> SendReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraftV2 policy, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var body = VersionedPolicyBody(policyId, expectedVersion);
        body["policy"] = PolicyBody(policy);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{BasePath}/replace/{(confirmed ? "confirm" : "preview")}", body, cancellationToken);
    }

    private Task<JsonNode?> SendStateChangeAsync(string action, Guid policyId, long expectedVersion, McpOperatorPolicyTargetSelectorV2 target, bool confirmed, string? planToken, string? idempotencyKey, CancellationToken cancellationToken)
    {
        EnsureTarget();
        var body = VersionedPolicyBody(policyId, expectedVersion);
        body["target"] = TargetSelectorBody(target);
        if (confirmed) McpOperatorTaskV2Client.AddPlan(body, planToken, idempotencyKey);
        return _client.SendAsync(HttpMethod.Post, $"{BasePath}/{action}/{(confirmed ? "confirm" : "preview")}", body, cancellationToken);
    }

    private JsonObject PolicyBody(McpOperatorPolicyDraftV2 policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        EnsureTarget();
        ValidateEnvironment(policy.Environment);
        if (!IsBoundedText(policy.Name, 160) || policy.Priority is < -10_000 or > 10_000 ||
            !Enum.IsDefined(policy.Effect) || !IsKnownOperationFamily(policy.OperationFamily) ||
            (policy.Operation is not null && !IsSafeValue(policy.Operation, 256)) ||
            (policy.AuditReference is not null && !IsBoundedText(policy.AuditReference, 512)) ||
            policy.ExpiresAtUtc is { } expiry && expiry <= DateTimeOffset.UtcNow ||
            policy.ReviewByUtc is { } review && review <= DateTimeOffset.UtcNow)
        {
            throw McpOperatorTaskV2Client.Invalid("policy draft");
        }

        if (policy.TargetSelector.Kind == McpOperatorPolicyTargetSelectorKindV2.DevelopmentEnvironment &&
            (policy.Environment != McpOperatorPolicyEnvironmentV2.Development || policy.TargetClassification is not null))
            throw McpOperatorTaskV2Client.Invalid("developmentEnvironment policy");
        var target = TargetSelectorBody(policy.TargetSelector);
        var body = new JsonObject
        {
            ["name"] = policy.Name.Trim(),
            ["environment"] = (short)policy.Environment,
            ["effect"] = (short)policy.Effect,
            ["priority"] = policy.Priority,
            ["principalSelector"] = PrincipalSelectorBody(policy.PrincipalSelector),
            ["targetSelector"] = target,
            ["operationFamily"] = (int)policy.OperationFamily,
            ["constraints"] = ConstraintsBody(policy.Constraints)
        };
        if (policy.TargetClassification is { } classification)
        {
            if (!IsTargetClassification(classification)) throw McpOperatorTaskV2Client.Invalid("policy targetClassification");
            if (policy.TargetSelector.Kind == McpOperatorPolicyTargetSelectorKindV2.ControlPlane) throw McpOperatorTaskV2Client.Invalid("policy controlPlane classification");
            body["targetClassification"] = (short)classification;
        }
        if (policy.Operation is not null) body["operation"] = policy.Operation;
        if (policy.ExpiresAtUtc is { } expiresAt) body["expiresAtUtc"] = expiresAt.ToString("O", CultureInfo.InvariantCulture);
        if (policy.ReviewByUtc is { } reviewBy) body["reviewByUtc"] = reviewBy.ToString("O", CultureInfo.InvariantCulture);
        if (policy.AuditReference is not null) body["auditReference"] = policy.AuditReference.Trim();
        return body;
    }

    private static JsonObject PrincipalSelectorBody(McpOperatorPolicyPrincipalSelectorV2 selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!Enum.IsDefined(selector.Kind) || !IsSafeValue(selector.Value, 256)) throw McpOperatorTaskV2Client.Invalid("policy principalSelector");
        return new JsonObject { ["kind"] = (short)selector.Kind, ["value"] = selector.Value.Trim() };
    }

    private static JsonObject TargetSelectorBody(McpOperatorPolicyTargetSelectorV2 target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var valid = target.Kind switch
        {
            McpOperatorPolicyTargetSelectorKindV2.ExactAgent => target.TenantId > 0 && target.AgentId is { } exactAgentId && exactAgentId != Guid.Empty && target.ClientTag is null,
            McpOperatorPolicyTargetSelectorKindV2.Tenant => target.TenantId > 0 && target.AgentId is null && target.ClientTag is null,
            McpOperatorPolicyTargetSelectorKindV2.ClientTag => target.TenantId > 0 && target.AgentId is null && IsSafeValue(target.ClientTag, 128),
            McpOperatorPolicyTargetSelectorKindV2.ControlPlane or McpOperatorPolicyTargetSelectorKindV2.DevelopmentEnvironment => target.TenantId == 0 && target.AgentId is null && target.ClientTag is null,
            _ => false
        };
        if (!valid) throw McpOperatorTaskV2Client.Invalid("policy targetSelector");

        var body = new JsonObject { ["kind"] = (short)target.Kind, ["tenantId"] = target.TenantId };
        if (target.AgentId is { } agentId) body["agentId"] = agentId.ToString("D");
        if (target.ClientTag is not null) body["clientTag"] = target.ClientTag.Trim();
        return body;
    }

    private static JsonObject ConstraintsBody(McpOperatorPolicyConstraintsV2 constraints)
    {
        ArgumentNullException.ThrowIfNull(constraints);
        ValidateValues(constraints.ReadRoots, 4096, "policy readRoots");
        ValidateValues(constraints.WriteRoots, 4096, "policy writeRoots");
        ValidateValues(constraints.AllowedShells, 64, "policy allowedShells");
        ValidateValues(constraints.WorkingDirectories, 4096, "policy workingDirectories");
        ValidatePositive(constraints.MaxCommandDurationSeconds, "policy maxCommandDurationSeconds");
        ValidatePositive(constraints.MaxTerminalIdleSeconds, "policy maxTerminalIdleSeconds");
        ValidatePositive(constraints.MaxTerminalLifetimeSeconds, "policy maxTerminalLifetimeSeconds");
        ValidatePositive(constraints.MaxConcurrentTerminalSessions, "policy maxConcurrentTerminalSessions");
        ValidatePositive(constraints.MaxConcurrentCommands, "policy maxConcurrentCommands");
        ValidatePositive(constraints.MaxOutputBytes, "policy maxOutputBytes");
        ValidatePositive(constraints.MaxArtifactBytes, "policy maxArtifactBytes");
        ValidatePositive(constraints.MaxScriptBytes, "policy maxScriptBytes");
        ValidatePositive(constraints.MaxJobTargetCount, "policy maxJobTargetCount");
        ValidatePositive(constraints.MaxTaskTargetCount, "policy maxTaskTargetCount");
        ValidatePositive(constraints.MaxFanOut, "policy maxFanOut");
        ValidatePositive(constraints.MaxOnboardingCodeLifetimeSeconds, "policy maxOnboardingCodeLifetimeSeconds");
        ValidatePositive(constraints.MaxOnboardingCodeUses, "policy maxOnboardingCodeUses");
        if (constraints.AllowedTargetClassifications?.Any(classification => !IsTargetClassification(classification)) == true ||
            constraints.RequiredConfirmationClass is { } confirmation && !Enum.IsDefined(confirmation))
        {
            throw McpOperatorTaskV2Client.Invalid("policy constraints");
        }

        var body = new JsonObject();
        AddValues(body, "readRoots", constraints.ReadRoots);
        AddValues(body, "writeRoots", constraints.WriteRoots);
        AddValues(body, "allowedShells", constraints.AllowedShells);
        AddValues(body, "workingDirectories", constraints.WorkingDirectories);
        Add(body, "maxCommandDurationSeconds", constraints.MaxCommandDurationSeconds);
        Add(body, "maxTerminalIdleSeconds", constraints.MaxTerminalIdleSeconds);
        Add(body, "maxTerminalLifetimeSeconds", constraints.MaxTerminalLifetimeSeconds);
        Add(body, "maxConcurrentTerminalSessions", constraints.MaxConcurrentTerminalSessions);
        Add(body, "maxConcurrentCommands", constraints.MaxConcurrentCommands);
        Add(body, "maxOutputBytes", constraints.MaxOutputBytes);
        Add(body, "maxArtifactBytes", constraints.MaxArtifactBytes);
        Add(body, "maxScriptBytes", constraints.MaxScriptBytes);
        Add(body, "maxJobTargetCount", constraints.MaxJobTargetCount);
        Add(body, "maxTaskTargetCount", constraints.MaxTaskTargetCount);
        Add(body, "maxFanOut", constraints.MaxFanOut);
        if (constraints.AllowedTargetClassifications is not null)
            body["allowedTargetClassifications"] = new JsonArray(constraints.AllowedTargetClassifications.Select(value => JsonValue.Create((short)value)).ToArray());
        Add(body, "maxOnboardingCodeLifetimeSeconds", constraints.MaxOnboardingCodeLifetimeSeconds);
        Add(body, "maxOnboardingCodeUses", constraints.MaxOnboardingCodeUses);
        if (constraints.RequiredConfirmationClass is { } requiredConfirmationClass)
            body["requiredConfirmationClass"] = (short)requiredConfirmationClass;
        Add(body, "destructiveOperationsAllowed", constraints.DestructiveOperationsAllowed);
        Add(body, "allowActiveTenantDeletion", constraints.AllowActiveTenantDeletion);
        return body;
    }

    private static JsonObject VersionedPolicyBody(Guid policyId, long expectedVersion)
    {
        if (policyId == Guid.Empty || expectedVersion <= 0) throw McpOperatorTaskV2Client.Invalid("policy version");
        return new JsonObject { ["policyId"] = policyId.ToString("D"), ["expectedVersion"] = expectedVersion };
    }

    private string TargetPath(McpOperatorV2Target target, string route)
    {
        target.Validate();
        EnsureTarget();
        return $"{BasePath}/{route}/{target.TenantId.ToString(CultureInfo.InvariantCulture)}/{target.AgentId:D}";
    }

    private void ValidateEnvironment(McpOperatorPolicyEnvironmentV2 environment)
    {
        if (!Enum.IsDefined(environment) ||
            (environment == McpOperatorPolicyEnvironmentV2.Development && _client.Target.Instance != "dev") ||
            (environment == McpOperatorPolicyEnvironmentV2.Production && _client.Target.Instance != "prod"))
        {
            throw McpOperatorTaskV2Client.Invalid("policy environment");
        }
    }

    private void EnsureTarget() => McpOperatorTaskV2Client.EnsureOperatorTarget(_client, "policy");

    private static void ValidateOptionalTenantId(int? value)
    {
        if (value is <= 0) throw McpOperatorTaskV2Client.Invalid("policy tenantId");
    }

    private static void ValidateOptionalGuid(Guid? value, string field)
    {
        if (value == Guid.Empty) throw McpOperatorTaskV2Client.Invalid(field);
    }

    private static void ValidatePositive(int? value, string field)
    {
        if (value is <= 0) throw McpOperatorTaskV2Client.Invalid(field);
    }

    private static void ValidateValues(IReadOnlyList<string>? values, int maximumLength, string field)
    {
        if (values is null) return;
        if (values.Count > 64 || values.Any(value => !IsBoundedText(value, maximumLength)) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw McpOperatorTaskV2Client.Invalid(field);
    }

    private static void AddValues(JsonObject body, string name, IReadOnlyList<string>? values)
    {
        if (values is not null) body[name] = new JsonArray(values.Select(value => JsonValue.Create(value.Trim())).ToArray());
    }

    private static void Add(JsonObject body, string name, int? value)
    {
        if (value is not null) body[name] = value;
    }

    private static void Add(JsonObject body, string name, bool? value)
    {
        if (value is not null) body[name] = value;
    }

    private static bool IsKnownOperationFamily(McpOperatorPolicyOperationFamilyV2 value) =>
        value != McpOperatorPolicyOperationFamilyV2.None && ((int)value & ~KnownOperationFamilies) == 0;

    private static bool IsTargetClassification(McpOperatorPolicyTargetClassificationV2 value) => value is >= McpOperatorPolicyTargetClassificationV2.DedicatedQa and <= McpOperatorPolicyTargetClassificationV2.CriticalInfrastructure;
    private static bool IsCatalogIdentifier(string? value) => value is { Length: > 0 and <= 128 } && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
    private static bool IsSafeValue(string? value, int maximumLength) => value is { Length: > 0 } && value.Length <= maximumLength && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.' or ':' or '/' or '@' or '#');
    private static bool IsBoundedText(string? value, int maximumLength) => value is { Length: > 0 } && value.Length <= maximumLength && !value.Any(char.IsControl);
    private static string PolicyId(Guid value) => value != Guid.Empty ? value.ToString("D") : throw McpOperatorTaskV2Client.Invalid("policyId");

    private const string BasePath = "/api/v2/mcp/operator/policy";
}
