namespace NetRatel.Shared.Operations;

/// <summary>Interpretation of verified assertion facts, never a credential entitlement inventory or a business admission decision.</summary>
public sealed record McpOperatorAssertionContext(
    string AuthenticationSource,
    string ScopeContext,
    string Tool,
    string Operation,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> Roles,
    string CredentialEntitlements,
    string BusinessAdmission)
{
    public static McpOperatorAssertionContext FromDelegation(McpOperatorDelegation delegation) => new(
        !string.IsNullOrWhiteSpace(delegation.IngressCredentialId) ? "local_http_credential" :
            !string.IsNullOrWhiteSpace(delegation.Identity.ExternalIssuer) ? "external_oauth" : "delegated_identity",
        !string.IsNullOrWhiteSpace(delegation.IngressCredentialId) ? "operation_specific" : "supplied_assertion",
        delegation.Tool,
        delegation.Operation,
        delegation.Identity.Scopes.Order(StringComparer.Ordinal).ToArray(),
        delegation.Identity.Roles.Order(StringComparer.Ordinal).ToArray(),
        "not_assessed",
        "not_checked");
}
