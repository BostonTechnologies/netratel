namespace NetRatel.Shared.Operations;

/// <summary>Closed, non-secret admission diagnostics shared by exchange and receiving authority.</summary>
public sealed record McpDelegationFailure(string Code, string Layer, bool Retryable, string Remediation)
{
    public static McpDelegationFailure? ForCode(string code) => code switch
    {
        "local_credential_invalid" => new(code, "credential", false,
            "Authenticate through the configured MCP client. Unknown credentials do not disclose lifecycle details."),
        "delegated_identity_invalid" => new(code, "delegation", false,
            "Invoke through the configured authenticated MCP host to obtain a fresh signed delegation."),
        "local_credential_expired" => new(code, "credential", false, "Ask an authorized operator to review and renew the exact credential expiry."),
        "local_credential_revoked" or "delegated_identity_revoked" => new(code, "credential", false, "Ask an authorized operator to review the revoked credential and issue approved replacement access."),
        "application_permission_denied" or "local_credential_permission_denied" or "local_credential_forbidden" => new(code, "application_permission", false,
            "Ask an authorized operator to review the required application permission for this operation and target. Credential replacement alone does not grant access."),
        "local_delegation_target_invalid" => new(code, "input", false,
            "Use the published operation target shape. Do not invent an agent ID for a tenant or control-plane operation."),
        "local_delegation_binding_mismatch" => new(code, "target", false,
            "Refresh the permitted persisted reference and exact target. Ownership details are not disclosed."),
        "local_delegation_unsupported_operation" => new(code, "catalog", false,
            "Select an operation advertised as supported for local HTTP MCP credentials."),
        "local_delegation_pairing_invalid" => new(code, "delegation", false,
            "Reconnect through the configured paired MCP host and verify its resource and instance settings."),
        "local_credential_throttled" => new(code, "dependency", true,
            "Wait for the bounded retry delay. Reconcile an uncertain mutation before retrying it."),
        "local_credential_exchange_unavailable" or "local_credential_exchange_timed_out" => new(code, "dependency", true,
            "Retry after the credential service recovers. Reconcile an uncertain mutation before retrying it."),
        "local_credential_exchange_protocol_error" => new(code, "dependency", false,
            "Check the paired service configuration and supported versions before retrying."),
        _ => null
    };
}
