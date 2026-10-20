using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace NetRatel.Shared.Connectivity;

/// <summary>One exact reviewed business operation. Agent targets require the persisted profile revision.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessTarget(int TenantId, string Tool, string Operation, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] Guid? AgentId = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] long? ProfileRevision = null);

/// <summary>Immutable proposal; requestId is the caller's durable idempotency key, never a bearer capability.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessProposal(string RequestId, string Name, string Action,
    [property: JsonConverter(typeof(ReviewedMcpAccessExpiryJsonConverter))] DateTimeOffset ExpiresAtUtc,
    IReadOnlyList<IntegrationPairingGrant> Grants, IReadOnlyList<string> InstancePermissions,
    IReadOnlyList<ReviewedMcpAccessTarget> Targets, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CredentialId = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpAccessReference(string RequestId);

/// <summary>Explicit owner decision bound to the immutable receipt; no device capability or user code is accepted.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReviewedMcpOwnerApplyRequest(string RequestId, string ProposalHash, bool Confirm);

public sealed record ReviewedMcpAccessRequest(string RequestId, string Status, string VerificationUri, string? UserCode,
    ReviewedMcpAccessProposal Proposal, string Resource, string Instance, string Environment,
    string RequestingPrincipalId, string RequestingCredentialId, long Revision, string ProposalHash)
{
    public string? CredentialRevisionHash { get; init; }
    public string PolicyRevisionHash { get; init; } = "";
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? ApprovalMode { get; init; }
    public override string ToString() => $"Reviewed access request {RequestId} {Status} (code redacted)";
}

public sealed record ReviewedMcpAccessAdmission(ReviewedMcpAccessTarget Target, string RequiredScope,
    IReadOnlyList<string> RequiredRoles, bool ApplicationGrantConfigured, bool TenantVisible, bool? PolicyAllowed,
    string? FailureCode, IReadOnlyList<Guid> MatchingPolicies)
{
    public string PolicyAdmissionStatus { get; init; } = "evaluated";
    public bool OwnerPermissionGranted { get; init; }
    public bool AuthorizationAdmitted { get; init; }
    public string TargetReadiness { get; init; } = "not_checked";
    public string CapabilityAvailability { get; init; } = "not_checked";
    public string AdmissionKind { get; init; } = "authorization_only_dispatch_rechecks_runtime";
}

/// <summary>Safe configured, delegated and admitted facts are deliberately separate. No delivery material belongs here.</summary>
public sealed record ReviewedMcpAccessReceipt(ReviewedMcpAccessRequest Request, string? CredentialId,
    DateTimeOffset? CredentialExpiresAtUtc, string CredentialState, IReadOnlyList<IntegrationPairingGrant> ConfiguredGrants,
    IReadOnlyList<string> ConfiguredInstancePermissions, IReadOnlyList<ReviewedMcpAccessAdmission> EffectiveAdmission,
    IReadOnlyList<string> RemainingPrerequisites, string NextAction)
{
    public IntegrationPairingFailure? ReviewFailure { get; init; }
}

/// <summary>Secret-free current credential facts for explicitly named operations; never a dispatch readiness certificate.</summary>
public sealed record CredentialMcpAdmission(string CredentialId, DateTimeOffset CheckedAtUtc, string CredentialState,
    string AuthenticationStatus, string Resource, string Instance, string Environment, DateTimeOffset CredentialExpiresAtUtc,
    IReadOnlyList<IntegrationPairingGrant> ConfiguredGrants, IReadOnlyList<string> ConfiguredInstancePermissions,
    IReadOnlyList<ReviewedMcpAccessAdmission> EffectiveAdmission, IReadOnlyList<string> RemainingPrerequisites, string NextAction)
{
    public bool TargetsTruncated { get; init; }
    public IReadOnlyList<CredentialMcpPolicy> MatchingPolicies { get; init; } = [];
}

public sealed record CredentialMcpPolicy(Guid PolicyId, string Environment, int TenantId, Guid? AgentId,
    string? Operation, string Effect, DateTimeOffset? ExpiresAtUtc, DateTimeOffset? ReviewByUtc, long Version);

/// <summary>The credential authority persists UTC instants at microsecond resolution. Submitted instants are never rounded.</summary>
public static class ReviewedMcpAccessExpiry
{
    public const string JsonPattern = @"^[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(\.[0-9]{1,6})?(Z|[+]00:00)(?![\s\S])";
    private static readonly Regex Syntax = new(JsonPattern, RegexOptions.CultureInvariant);
    public static bool IsSupported(DateTimeOffset instant) => instant.Offset == TimeSpan.Zero && instant.Ticks % TimeSpan.TicksPerMicrosecond == 0;
    public static bool TryParse(string? value, out DateTimeOffset instant)
    {
        instant = default;
        // Validate the original JSON string before DateTimeOffset can discard additional fractional digits.
        return value is { Length: >= 20 and <= 32 } && Syntax.IsMatch(value) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out instant) && IsSupported(instant);
    }
    public static string Format(DateTimeOffset instant)
    {
        if (!IsSupported(instant)) throw new ArgumentOutOfRangeException(nameof(instant), "An exact UTC expiry supports at most six fractional digits.");
        return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture);
    }
}

public sealed class ReviewedMcpAccessExpiryJsonConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType == JsonTokenType.String && ReviewedMcpAccessExpiry.TryParse(reader.GetString(), out var instant)
            ? instant : throw new JsonException("expiresAtUtc must be an exact UTC instant with at most six fractional digits.");
    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(ReviewedMcpAccessExpiry.Format(value));
}

/// <summary>Checks reviewed approval JSON before date parsing while preserving the existing ordinary pairing contract.</summary>
public sealed class ReviewedMcpAccessApprovalJsonConverter : JsonConverter<IntegrationPairingApprovalRequest>
{
    private sealed record Payload(string UserCode, DateTimeOffset ExpiresAtUtc, IReadOnlyList<IntegrationPairingGrant> Grants,
        IReadOnlyList<string>? InstancePermissions = null)
    {
        public bool VerifierConsent { get; init; }
        public string? ReviewedProposalHash { get; init; }
    }
    public override IntegrationPairingApprovalRequest Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("Pairing approval must be an object.");
        bool Matches(string name, string property) => string.Equals(name, options.PropertyNamingPolicy?.ConvertName(property) ?? property,
            options.PropertyNameCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        var reviewedHash = root.EnumerateObject().Where(property => Matches(property.Name, nameof(Payload.ReviewedProposalHash))).Select(property => property.Value).LastOrDefault();
        var reviewed = reviewedHash.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined);
        if (reviewed)
        {
            var expiryProperties = root.EnumerateObject().Where(property => Matches(property.Name, nameof(Payload.ExpiresAtUtc))).ToArray();
            if (expiryProperties.Length == 0 || expiryProperties.Any(property => property.Value.ValueKind != JsonValueKind.String || !ReviewedMcpAccessExpiry.TryParse(property.Value.GetString(), out _)))
                throw new JsonException("Reviewed approval expiresAtUtc must be an exact UTC instant with at most six fractional digits.");
        }
        var payload = root.Deserialize<Payload>(options) ?? throw new JsonException("Pairing approval is required.");
        return new(payload.UserCode, payload.ExpiresAtUtc, payload.Grants, payload.InstancePermissions)
        { VerifierConsent = payload.VerifierConsent, ReviewedProposalHash = payload.ReviewedProposalHash };
    }
    public override void Write(Utf8JsonWriter writer, IntegrationPairingApprovalRequest value, JsonSerializerOptions options)
    {
        if (value.ReviewedProposalHash is not null)
            JsonSerializer.Serialize(writer, new { value.UserCode, ExpiresAtUtc = ReviewedMcpAccessExpiry.Format(value.ExpiresAtUtc), value.Grants, value.InstancePermissions, value.VerifierConsent, value.ReviewedProposalHash }, options);
        else JsonSerializer.Serialize(writer, new Payload(value.UserCode, value.ExpiresAtUtc, value.Grants, value.InstancePermissions)
        { VerifierConsent = value.VerifierConsent, ReviewedProposalHash = value.ReviewedProposalHash }, options);
    }
}
