using NetRatel.Application.Operations;
using NetRatel.Shared.Connectivity;

namespace NetRatel.Infrastructure.Identity;

public sealed partial class IntegrationPairingService
{
    /// <summary>Browser grant editing derives immutable credential fields from the owned record.</summary>
    public async Task<ReviewedMcpAccessReceipt> RequestAccountAccessAsync(string owner, string credentialId,
        string requestId, IReadOnlyList<IntegrationPairingGrant> grants, IReadOnlyList<string> instancePermissions,
        string origin, string resource, McpOperatorEnvironment environment, CancellationToken ct = default)
    {
        if (grants is null || instancePermissions is null || grants.Count > 64 || instancePermissions.Count > 32 ||
            grants.Any(grant => grant is null))
            throw new IntegrationCredentialGrantValidationException("grants");
        var credential = await AccountAccessCredentialAsync(owner, credentialId, resource, ct).ConfigureAwait(false);
        if (credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= clock.GetUtcNow())
            throw new IntegrationPairingUnavailableException();
        var proposal = new ReviewedMcpAccessProposal(requestId, credential.Name, "reconcile", credential.ExpiresAtUtc,
            grants.OrderBy(grant => grant.TenantId).ThenBy(grant => grant.Permission, StringComparer.Ordinal).ToArray(),
            instancePermissions.Order(StringComparer.Ordinal).ToArray(), [], credential.Id);
        return await RequestReviewedAsync(proposal, owner, credential.Id, origin, resource, environment, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReviewedMcpAccessRequest>> ListAccountAccessAsync(string owner, string credentialId,
        string resource, McpOperatorEnvironment environment, CancellationToken ct = default)
    {
        var credential = await AccountAccessCredentialAsync(owner, credentialId, resource, ct).ConfigureAwait(false);
        var requests = await ListReviewedAsync(owner, credential.Id, ct, resource, environment).ConfigureAwait(false);
        return requests.Where(request => IsAccountAccessProposal(request.Proposal, credential)).ToArray();
    }

    public async Task<ReviewedMcpAccessReceipt> InspectAccountAccessAsync(string owner, string credentialId,
        string requestId, string resource, McpOperatorEnvironment environment, CancellationToken ct = default)
    {
        var request = await AccountAccessRequestAsync(owner, credentialId, requestId, resource, environment, ct).ConfigureAwait(false);
        return await InspectReviewedAsync(request.Id, owner, CanonicalUuid(credentialId, "credentialId"), ct: ct,
            currentResource: resource, currentEnvironment: environment).ConfigureAwait(false);
    }

    /// <summary>Apply the exact browser-reviewed proposal, including an independently approved pending request.</summary>
    public async Task<ReviewedMcpAccessReceipt> ApplyAccountAccessAsync(string owner, string credentialId,
        string requestId, string proposalHash, bool confirm, string resource, McpOperatorEnvironment environment,
        CancellationToken ct = default)
    {
        var request = await AccountAccessRequestAsync(owner, credentialId, requestId, resource, environment, ct).ConfigureAwait(false);
        if (!confirm || string.IsNullOrWhiteSpace(proposalHash) ||
            !string.Equals(proposalHash, AccessMetadata(request)!.ReviewBindingHash, StringComparison.Ordinal))
            throw ReviewValidation("review_proposal_changed", "apply", "proposalHash");
        var canonicalCredential = CanonicalUuid(credentialId, "credentialId");
        if (request.State == IntegrationPairingState.Pending)
            return await ApplyOwnerReviewedAsync(request.Id, proposalHash, confirm, owner, canonicalCredential, ct,
                resource, environment).ConfigureAwait(false);
        // An independent browser approval is applied through the same current-authority and CAS checks as MCP resume.
        return await InspectReviewedAsync(request.Id, owner, canonicalCredential, resume: true, ct: ct,
            currentResource: resource, currentEnvironment: environment).ConfigureAwait(false);
    }

    public async Task<ReviewedMcpAccessReceipt> CancelAccountAccessAsync(string owner, string credentialId,
        string requestId, string resource, McpOperatorEnvironment environment, CancellationToken ct = default)
    {
        var request = await AccountAccessRequestAsync(owner, credentialId, requestId, resource, environment, ct).ConfigureAwait(false);
        return await CancelReviewedAsync(request.Id, owner, CanonicalUuid(credentialId, "credentialId"), ct,
            resource, environment).ConfigureAwait(false);
    }

    private async Task<IntegrationCredentialSummary> AccountAccessCredentialAsync(string owner, string credentialId,
        string resource, CancellationToken ct)
    {
        var credential = await InspectOwnedCredentialAsync(owner, credentialId, ct).ConfigureAwait(false);
        if (credential is null) throw new IntegrationPairingUnavailableException
            { ReviewFailure = IntegrationPairingFailure.ForCode("review_credential_unavailable", "credential") };
        if (!McpResourceUri.Equivalent(credential.Resource, resource)) throw new IntegrationPairingUnavailableException
            { ReviewFailure = IntegrationPairingFailure.ForCode("review_resource_mismatch", "credential") };
        return credential;
    }

    private async Task<IntegrationPairingRequest> AccountAccessRequestAsync(string owner, string credentialId,
        string requestId, string resource, McpOperatorEnvironment environment, CancellationToken ct)
    {
        var credential = await AccountAccessCredentialAsync(owner, credentialId, resource, ct).ConfigureAwait(false);
        var request = await FindAccessAsync(requestId, owner, credential.Id, ct).ConfigureAwait(false)
            ?? throw new IntegrationPairingUnavailableException();
        RequireCurrentAccessContext(request, resource, environment);
        if (!IsAccountAccessProposal(AccessMetadata(request)!.Proposal, credential))
            throw new IntegrationPairingUnavailableException();
        return request;
    }

    private static bool IsAccountAccessProposal(ReviewedMcpAccessProposal proposal, IntegrationCredentialSummary credential) =>
        proposal.Action == "reconcile" && proposal.Targets.Count == 0 &&
        Guid.TryParse(proposal.CredentialId, out var target) && target.ToString("N") == credential.Id &&
        proposal.Name == credential.Name && proposal.ExpiresAtUtc == credential.ExpiresAtUtc;
}
