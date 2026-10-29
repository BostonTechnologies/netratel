using System.Globalization;
using NetRatel.Application.Operations;

namespace NetRatel.Web.Services.OperatorPolicy;

/// <summary>Editable fields preserve every existing bound when an exact directory grant is changed.</summary>
public sealed class OperatorPolicyDraftEditor
{
    public string Name { get; set; } = "";
    public McpOperatorEnvironment Environment { get; set; }
    public McpOperatorPolicyEffect Effect { get; set; } = McpOperatorPolicyEffect.Allow;
    public int Priority { get; set; }
    public McpOperatorPrincipalSelectorKind PrincipalKind { get; set; } = McpOperatorPrincipalSelectorKind.OAuthSubject;
    public string PrincipalValue { get; set; } = "";
    public McpOperatorTargetSelectorKind TargetKind { get; set; } = McpOperatorTargetSelectorKind.ExactAgent;
    public int TenantId { get; set; }
    public string AgentId { get; set; } = "";
    public string ClientTag { get; set; } = "";
    public McpOperatorTargetClassification? Classification { get; set; }
    public IReadOnlyCollection<McpOperatorOperationFamily> Families { get; set; } = [McpOperatorOperationFamily.Observability];
    public string Operation { get; set; } = "";
    public IReadOnlyList<string>? Operations { get; set; }
    public IReadOnlyList<Guid>? AgentIds { get; set; }
    public IReadOnlyList<string>? ClientTags { get; set; }
    public Guid? ParentPolicyId { get; set; }
    public string? IntegrationCredentialId { get; set; }
    public string? Resource { get; set; }
    public bool IsContainer { get; set; }
    public string WorkingDirectories { get; set; } = "";
    public string ReadRoots { get; set; } = "";
    public string WriteRoots { get; set; } = "";
    public string Shells { get; set; } = "";
    public string ExpiresAt { get; set; } = "";
    public string ReviewBy { get; set; } = "";
    public string AuditReference { get; set; } = "";
    public McpOperatorConstraints Constraints { get; set; } = new();

    public static OperatorPolicyDraftEditor From(McpOperatorPolicy policy) => new()
    {
        Name = policy.Name, Environment = policy.Environment, Effect = policy.Effect, Priority = policy.Priority,
        PrincipalKind = policy.PrincipalSelector.Kind, PrincipalValue = policy.PrincipalSelector.Value,
        TargetKind = policy.TargetSelector.Kind, TenantId = policy.TargetSelector.TenantId,
        AgentId = policy.TargetSelector.AgentId?.ToString("D") ?? "", ClientTag = policy.TargetSelector.ClientTag ?? "",
        Classification = policy.TargetClassification, Families = OperationFamilies.Where(family => policy.OperationFamily.HasFlag(family)).ToArray(),
        Operation = policy.Operation ?? "", WorkingDirectories = Lines(policy.Constraints.WorkingDirectories),
        ReadRoots = Lines(policy.Constraints.ReadRoots), WriteRoots = Lines(policy.Constraints.WriteRoots), Shells = Lines(policy.Constraints.AllowedShells),
        ExpiresAt = Instant(policy.ExpiresAtUtc), ReviewBy = Instant(policy.ReviewByUtc), AuditReference = policy.AuditReference ?? "",
        Operations = policy.Operations, AgentIds = policy.TargetSelector.AgentIds, ClientTags = policy.TargetSelector.ClientTags,
        ParentPolicyId = policy.ParentPolicyId, IntegrationCredentialId = policy.IntegrationCredentialId, Resource = policy.Resource, IsContainer = policy.IsContainer,
        Constraints = policy.Constraints
    };

    public McpOperatorPolicyDraft Build()
    {
        var selector = new McpOperatorTargetSelector(TargetKind,
            TargetKind is McpOperatorTargetSelectorKind.ControlPlane or McpOperatorTargetSelectorKind.DevelopmentEnvironment ? 0 : TenantId,
            TargetKind == McpOperatorTargetSelectorKind.ExactAgent ? Guid.Parse(AgentId) : null,
            TargetKind == McpOperatorTargetSelectorKind.ClientTag ? ClientTag.Trim() : null) { AgentIds = AgentIds, ClientTags = ClientTags };
        return new McpOperatorPolicyDraft(Name.Trim(), Environment, Effect, Priority,
            new(PrincipalKind, PrincipalValue.Trim()), selector, Classification,
            Families.Aggregate(McpOperatorOperationFamily.None, (value, family) => value | family), Optional(Operation),
            Constraints with { WorkingDirectories = Values(WorkingDirectories, Constraints.WorkingDirectories),
                ReadRoots = Values(ReadRoots, Constraints.ReadRoots), WriteRoots = Values(WriteRoots, Constraints.WriteRoots),
                AllowedShells = Values(Shells, Constraints.AllowedShells) },
            ParseInstant(ExpiresAt), ParseInstant(ReviewBy), Optional(AuditReference)) {
                ParentPolicyId = ParentPolicyId, IntegrationCredentialId = IntegrationCredentialId, Resource = Resource,
                Operations = Operations, IsContainer = IsContainer
            };
    }

    public static McpOperatorOperationFamily[] OperationFamilies { get; } = Enum.GetValues<McpOperatorOperationFamily>()
        .Where(family => family != McpOperatorOperationFamily.None && ((int)family & ((int)family - 1)) == 0).ToArray();
    private static string Lines(IReadOnlyList<string>? values) => values is null ? "" : string.Join('\n', values);
    private static IReadOnlyList<string>? Values(string value, IReadOnlyList<string>? previous) => string.IsNullOrWhiteSpace(value)
        ? previous is null ? null : []
        : value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal).ToArray();
    private static string? Optional(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    private static string Instant(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "";
    private static DateTimeOffset? ParseInstant(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (!(value.EndsWith('Z') || value.Length >= 6 && value[^3] == ':' && value[^6] is '+' or '-'))
            throw new FormatException("Expiry and review dates require an explicit UTC offset or Z.");
        return DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.None).ToUniversalTime();
    }
}
