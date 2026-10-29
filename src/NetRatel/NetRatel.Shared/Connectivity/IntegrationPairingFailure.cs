namespace NetRatel.Shared.Connectivity;

/// <summary>Safe review diagnostics contain no code, capability, credential material or hidden target details.</summary>
public sealed record IntegrationPairingFailure(string Code, string Stage, string Remediation)
{
    public string Status { get; init; } = "unavailable";
    public string? CorrelationId { get; init; }
    public string NextAction => Remediation;

    public static IntegrationPairingFailure ForCode(string code, string stage) => new(code, stage, code switch
    {
        "review_code_invalid" => "Enter the eight-character verification code shown by your client; spaces around the code and its separator are optional.",
        "review_destination_unconfigured" => "The public review destination is not configured correctly. Ask the deployment administrator to check the public Web address and HTTP MCP resource configuration, then retry loading access. Your saved grants are unchanged.",
        "review_credential_unavailable" => "This HTTP MCP credential is unavailable to the signed-in owner. Refresh the credential list and reopen Details under the account that created it.",
        "review_resource_mismatch" => "This credential belongs to a different HTTP MCP resource from the configured review destination. Ask the deployment administrator to check the canonical MCP resource configuration. The credential and its saved grants have not changed.",
        "review_service_unavailable" => "The review service is temporarily unavailable. Keep the existing code and requestId; once service recovers, inspect its receipt to reconcile any decision before retrying.",
        "review_request_expired" => "The review deadline has passed. Inspect the existing request receipt, then prepare a new request with a new requestId and obtain a new authorized review.",
        "review_credential_expiry_passed" => "The exact proposed credential expiry has already passed, even if the review code is still valid. Inspect the receipt and prepare a new requestId with a future UTC expiry for authorized review.",
        "review_request_not_pending" => "This request already has a decision or is closed. Inspect its receipt and resume the same requestId to reconcile the outcome before creating another request.",
        "review_independent_approver_required" => "This server requires a separate approver for reviewed access. Inspect its release and review contract before retrying the saved request.",
        "review_approver_authority_missing" when stage == "resume" => "The prior approver no longer has current authority, so this request is denied. Inspect its receipt, resolve the operator's role assignments and prepare a new requestId for authorized review.",
        "review_approver_authority_missing" => "The approver needs current MCP policy administration and every selected durable application permission, including when approving their own request. Review current role assignments, then look up the same code again while it remains valid.",
        "review_owner_authority_missing" => "The requesting account no longer has every selected durable application permission. Ask an account administrator to review the owner's current role assignments; inspect the receipt before preparing another request.",
        "review_proposal_changed" => "The submitted decision does not match the exact reviewed proposal. Look up the original code again; a change to access, targets or expiry requires a new requestId and authorized review.",
        "review_binding_changed" => "The deployment or reviewed operation binding has changed. Inspect the original receipt, then prepare a new request against the current destination and obtain authorized review.",
        "review_credential_changed" => "The selected credential changed after this proposal was prepared. Inspect its current receipt and prepare a new requestId for the intended current state.",
        "review_policy_changed" => "The relevant policy changed after this proposal was prepared. Inspect current policy and prepare a new requestId for authorized review.",
        "review_target_changed" => "A selected target or its profile revision no longer matches the proposal. Inspect that target through the supported access tools and prepare a new requestId for authorized review.",
        "review_requester_unavailable" => "The initiating credential is no longer active for this destination. Reconnect through supported protected MCP setup and inspect retained receipts before proposing another change.",
        "review_request_id_invalid" => "Use a nonempty UUID for requestId. Standard UUID representations identify the same saved request.",
        "review_credential_id_invalid" => "Use the nonempty UUID returned by owned credential inspection. Standard UUID representations identify the same credential.",
        "review_proposal_conflict" => "This requestId is already bound to a different proposal. Inspect its existing receipt; reuse the original proposal or choose a new requestId for a new authorized review.",
        "review_protected_setup_required" => "Prepare new credential creation through the supported protected MCP setup client. Resume its non-secret requestId through MCP; no API token or credential secret belongs in tool arguments.",
        "review_request_unavailable" or "reviewed_request_unavailable" => "Check the code and configured review destination. Requests are private to their initiating credential; inspect its saved receipt before starting a new request.",
        _ => "Inspect the saved request receipt and current access before retrying. Share only the correlation reference when requesting diagnostic help."
    });

    public static bool IsKnownCode(string? code) => code is
        "review_code_invalid" or "review_destination_unconfigured" or "review_credential_unavailable" or "review_resource_mismatch" or "review_service_unavailable" or "review_request_expired" or "review_credential_expiry_passed" or "review_request_not_pending" or
        "review_independent_approver_required" or "review_approver_authority_missing" or "review_owner_authority_missing" or
        "review_proposal_changed" or "review_binding_changed" or "review_credential_changed" or "review_policy_changed" or
        "review_target_changed" or "review_requester_unavailable" or "review_request_id_invalid" or
        "review_credential_id_invalid" or "review_proposal_conflict" or "review_protected_setup_required" or "review_request_unavailable" or "reviewed_request_unavailable";
}

/// <summary>Advisory eligibility for the signed-in operator; approval repeats all authority and binding checks.</summary>
public sealed record IntegrationPairingReviewEligibility(bool CanApprove, IntegrationPairingFailure? Failure = null)
{
    public bool OwnerSelfApproval { get; init; }
}
