using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Shared.Connectivity;
using NetRatel.Shared.Operations;

namespace NetRatel.Infrastructure.Identity;

public sealed partial class IntegrationPairingService
{
    private sealed record ReviewedAccessMetadata(bool ReviewedAccess, IReadOnlyList<string> Permissions,
        ReviewedMcpAccessProposal Proposal, string RequestingPrincipalId, string RequestingCredentialId,
        string Instance, McpOperatorEnvironment Environment, string PolicyRevisionHash, string ProposalHash,
        string? ApproverPrincipalId = null, bool Applied = false, string? ProtectedSetup = null, string? CredentialRevisionHash = null, string? ReviewBindingHash = null,
        IntegrationPairingFailure? ReviewFailure = null);
    private sealed record ProtectedSetupMaterial(string Code, string Capability)
    { public override string ToString() => "Protected setup material (code and capability redacted)"; }

    /// <summary>Protected client initiation. The returned device capability must never enter an MCP tool response.</summary>
    public async Task<IntegrationPairingStartResponse> StartReviewedAsync(ReviewedMcpAccessProposal proposal,
        string owner, string requestingCredentialId, string origin, string resource, McpOperatorEnvironment environment,
        CancellationToken ct = default)
    {
        RequireReviewedExpiry(proposal.ExpiresAtUtc);
        proposal = CanonicalProposal(proposal);
        if (applicationDb is null || effectiveAccess is null || policyAdministration is null || operatorAuthorization is null ||
            !Guid.TryParse(proposal.RequestId, out var requestId) || requestId == Guid.Empty ||
            proposal.Action is not ("create" or "reconcile" or "revoke") || proposal.Grants is null || proposal.InstancePermissions is null || proposal.Targets is null ||
            string.IsNullOrWhiteSpace(proposal.Name) || proposal.Name.Length > 128 || proposal.Name.Any(char.IsControl) || proposal.ExpiresAtUtc.Offset != TimeSpan.Zero || proposal.ExpiresAtUtc <= clock.GetUtcNow() || proposal.ExpiresAtUtc > clock.GetUtcNow().AddDays(365) ||
            proposal.Targets.Count > 32 || proposal.Targets.Distinct().Count() != proposal.Targets.Count ||
            proposal.Action == "create" && proposal.CredentialId is not null ||
            proposal.Action != "create" && string.IsNullOrWhiteSpace(proposal.CredentialId))
            throw new IntegrationPairingUnavailableException();
        origin = HttpHostSetupProtocol.NormalizeWebAddress(origin).TrimEnd('/');
        if (!McpResourceUri.TryNormalize(resource, out resource)) throw new IntegrationPairingUnavailableException();
        var requester = await db.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == requestingCredentialId &&
            x.OwnerPrincipalId == owner && x.Purpose == IntegrationCredentialPurpose.HttpMcp && x.Resource == resource && x.RevokedAtUtc == null, ct).ConfigureAwait(false);
        if (requester is null || requester.ExpiresAtUtc <= clock.GetUtcNow()) throw new IntegrationPairingUnavailableException();
        if (proposal.CredentialId is not null && !await db.IntegrationCredentials.AsNoTracking().AnyAsync(x => x.Id == proposal.CredentialId &&
            x.OwnerPrincipalId == owner && x.Purpose == IntegrationCredentialPurpose.HttpMcp && x.Resource == resource, ct).ConfigureAwait(false))
            throw new IntegrationPairingUnavailableException();
        var create = new IntegrationCredentialCreateRequest(proposal.Name, IntegrationCredentialPurpose.HttpMcp, proposal.ExpiresAtUtc,
            proposal.Grants.Select(x => new IntegrationCredentialGrantRequest(x.TenantId, x.Permission)).ToArray(), resource, proposal.InstancePermissions);
        if (proposal.Action != "revoke") await validator.ValidateAsync(DurablePrincipal(owner), create, ct).ConfigureAwait(false);
        await ValidateTargetsAsync(proposal, ct).ConfigureAwait(false);
        var hash = HashProposal(proposal);
        var id = requestId.ToString("N");
        var existing = await db.IntegrationPairingRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            var bound = AccessMetadata(existing);
            if (bound?.RequestingPrincipalId != owner || bound.RequestingCredentialId != requestingCredentialId || bound.Environment != environment || existing.Resource != resource || existing.PublicOrigin != origin)
                throw new IntegrationPairingUnavailableException();
            if (!SameProposal(bound, proposal)) throw ReviewValidation("review_proposal_conflict", "request", "requestId");
            if (existing.State is not (IntegrationPairingState.Pending or IntegrationPairingState.Approved or IntegrationPairingState.Issued) || (existing.State == IntegrationPairingState.Issued ? existing.DeliveryExpiresAtUtc <= clock.GetUtcNow() : existing.ExpiresAtUtc <= clock.GetUtcNow()) || bound.ProtectedSetup is null)
                throw new IntegrationPairingUnavailableException();
            var setup = JsonSerializer.Deserialize<ProtectedSetupMaterial>(protection.CreateProtector("NetRatel.IntegrationPairing.Setup.v1", existing.Id).Unprotect(bound.ProtectedSetup))!;
            return new(existing.Id, FormatCode(setup.Code), setup.Capability, existing.PublicOrigin + "/account/integration-credentials?tab=credentials", existing.PublicOrigin, existing.ExpiresAtUtc, existing.PollIntervalSeconds);
        }
        var now = clock.GetUtcNow();
        if (await db.IntegrationPairingRequests.CountAsync(x => x.ExpiresAtUtc > now &&
            (x.State == IntegrationPairingState.Pending || x.State == IntegrationPairingState.Approved || x.State == IntegrationPairingState.Issued), ct).ConfigureAwait(false) >= 4096)
            throw new IntegrationPairingUnavailableException();
        var code = string.Concat(RandomNumberGenerator.GetBytes(8).Select(value => Alphabet[value % Alphabet.Length]));
        var capability = "nrt_pair_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var metadata = new ReviewedAccessMetadata(true, proposal.Grants.Select(x => x.Permission).Concat(proposal.InstancePermissions).Distinct().ToArray(),
            proposal, owner, requestingCredentialId, environment == McpOperatorEnvironment.Production ? "prod" : "dev", environment,
            await PolicyRevisionHashAsync(proposal, environment, id, ct).ConfigureAwait(false), hash,
            ProtectedSetup: protection.CreateProtector("NetRatel.IntegrationPairing.Setup.v1", id).Protect(JsonSerializer.Serialize(new ProtectedSetupMaterial(code, capability))),
            CredentialRevisionHash: await CredentialRevisionHashAsync(proposal.CredentialId, owner, ct).ConfigureAwait(false));
        metadata = metadata with { ReviewBindingHash = ReviewHash(origin, resource!, metadata) };
        var request = new IntegrationPairingRequest
        {
            Id = id, Name = proposal.Name, PublicOrigin = origin, Purpose = IntegrationCredentialPurpose.HttpMcp, Resource = resource,
            UserCodeHash = IntegrationCredentialService.Hash(code), DeviceCapabilityHash = IntegrationCredentialService.Hash(capability),
            RequestedPermissionsJson = JsonSerializer.Serialize(metadata), CreatedAtUtc = now, ExpiresAtUtc = now.Add(RequestLifetime),
            CredentialExpiresAtUtc = proposal.ExpiresAtUtc, NextPollAtUtc = now.AddSeconds(IntegrationPairingProtocol.PollIntervalSeconds)
        };
        db.IntegrationPairingRequests.Add(request);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            if (!await db.IntegrationPairingRequests.AsNoTracking().AnyAsync(x => x.Id == id, ct).ConfigureAwait(false)) throw;
            return await StartReviewedAsync(proposal, owner, requestingCredentialId, origin, resource!, environment, ct).ConfigureAwait(false);
        }
        return new(id, FormatCode(code), capability, origin + "/account/integration-credentials?tab=credentials", origin, request.ExpiresAtUtc, request.PollIntervalSeconds);
    }

    /// <summary>MCP initiation for an existing credential; no credential-delivery capability is returned.</summary>
    public async Task<ReviewedMcpAccessReceipt> RequestReviewedAsync(ReviewedMcpAccessProposal proposal, string owner,
        string requester, string origin, string resource, McpOperatorEnvironment environment, CancellationToken ct = default)
    {
        RequireReviewedExpiry(proposal.ExpiresAtUtc);
        proposal = CanonicalProposal(proposal);
        if (branding is not null) origin = HttpHostSetupProtocol.NormalizeWebAddress((await branding.GetEffectiveAsync(ct).ConfigureAwait(false)).SiteUrl.Value).TrimEnd('/');
        var existing = await FindAccessAsync(proposal.RequestId, owner, requester, ct).ConfigureAwait(false);
        string? code = null;
        if (existing is not null)
        {
            var metadata = AccessMetadata(existing)!;
            if (metadata.Environment != environment || !McpResourceUri.Equivalent(existing.Resource, resource) || existing.PublicOrigin != HttpHostSetupProtocol.NormalizeWebAddress(origin).TrimEnd('/')) throw new IntegrationPairingUnavailableException();
            if (!SameProposal(metadata, proposal)) throw ReviewValidation("review_proposal_conflict", "request", "requestId");
        }
        else
        {
            if (proposal.Action == "create") throw ReviewValidation("review_protected_setup_required", "request", "protectedSetupRequired");
            var start = await StartReviewedAsync(proposal, owner, requester, origin, resource, environment, ct).ConfigureAwait(false);
            code = start.UserCode;
        }
        return await InspectReviewedAsync(proposal.RequestId, owner, requester, false, code, ct).ConfigureAwait(false);
    }

    public async Task<ReviewedMcpAccessReceipt> InspectReviewedAsync(string id, string owner, string requester,
        bool resume = false, string? code = null, CancellationToken ct = default, string? currentResource = null, McpOperatorEnvironment? currentEnvironment = null)
    {
        var found = await FindAccessAsync(id, owner, requester, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
        RequireCurrentAccessContext(found, currentResource, currentEnvironment);
        return await MutateByAccessIdAsync(found.Id, owner, requester, async request =>
        {
            await ExpireAsync(request, ct).ConfigureAwait(false);
            var metadata = AccessMetadata(request)!;
            if (resume && request.State == IntegrationPairingState.Approved)
            {
                if (await ReviewedAuthorityFailureAsync(request, metadata, metadata.ApproverPrincipalId, "resume", ct).ConfigureAwait(false) is { } failure)
                {
                    request.State = IntegrationPairingState.Denied;
                    request.RequestedPermissionsJson = JsonSerializer.Serialize(metadata with { ReviewFailure = failure });
                    await SaveAsync(request, ct).ConfigureAwait(false);
                }
                else if (metadata.Proposal.Action != "create")
                    await ApplyReviewedAsync(request, metadata, ct).ConfigureAwait(false);
            }
            return await ReceiptAsync(request, AccessMetadata(request)!, code, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<ReviewedMcpAccessReceipt> CancelReviewedAsync(string id, string owner, string requester, CancellationToken ct = default, string? currentResource = null, McpOperatorEnvironment? currentEnvironment = null)
    {
        var found = await FindAccessAsync(id, owner, requester, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
        RequireCurrentAccessContext(found, currentResource, currentEnvironment);
        return await MutateByAccessIdAsync(found.Id, owner, requester, async request =>
        {
            if (request.State == IntegrationPairingState.Issued && AccessMetadata(request)!.Proposal.Action == "create")
                await RevokeAndClearAsync(request, IntegrationPairingState.Cancelled, ct).ConfigureAwait(false);
            else if (request.State is IntegrationPairingState.Pending or IntegrationPairingState.Approved)
            {
                request.State = IntegrationPairingState.Cancelled;
                await SaveAsync(request, ct).ConfigureAwait(false);
            }
            return await ReceiptAsync(request, AccessMetadata(request)!, null, ct).ConfigureAwait(false);
        }, ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReviewedMcpAccessRequest>> ListReviewedAsync(string owner, string requester, CancellationToken ct = default, string? currentResource = null, McpOperatorEnvironment? currentEnvironment = null)
    {
        var records = await db.IntegrationPairingRequests.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc).Take(4096).ToArrayAsync(ct).ConfigureAwait(false);
        return records.Where(x => AccessMetadata(x) is { } m && m.RequestingPrincipalId == owner && m.RequestingCredentialId == requester && (currentResource is null || McpResourceUri.Equivalent(x.Resource, currentResource)) && (currentEnvironment is null || m.Environment == currentEnvironment))
            .Take(50).Select(x => AccessRequest(x, AccessMetadata(x)!, null)).ToArray();
    }

    public async Task<IntegrationCredentialSummary?> InspectOwnedCredentialAsync(string owner, string id, CancellationToken ct = default)
    {
        var canonicalId = CanonicalUuid(id, "credentialId");
        return (await credentials.ListAsync(owner, ct).ConfigureAwait(false)).SingleOrDefault(x => x.Id == canonicalId && x.Purpose == IntegrationCredentialPurpose.HttpMcp);
    }

    private async Task ValidateReviewedApprovalAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata,
        ClaimsPrincipal principal, IntegrationPairingApprovalRequest approval, CancellationToken ct)
    {
        RequireReviewedExpiry(approval.ExpiresAtUtc);
        if (await ReviewedAuthorityFailureAsync(request, metadata, principal.FindFirstValue("netratel_principal_id"), "approval", ct).ConfigureAwait(false) is { } failure)
            throw new IntegrationCredentialGrantValidationException("reviewedProposal", true) { ReviewFailure = failure };
        if (approval.ReviewedProposalHash != metadata.ReviewBindingHash || approval.ExpiresAtUtc != metadata.Proposal.ExpiresAtUtc || approval.Grants is null ||
            !SameGrants(approval.Grants, metadata.Proposal.Grants) ||
            !(approval.InstancePermissions ?? []).Order().SequenceEqual(metadata.Proposal.InstancePermissions.Order()))
            throw ReviewValidation("review_proposal_changed", "approval", "reviewedProposal");
    }

    private async Task<IntegrationPairingFailure?> ReviewedAuthorityFailureAsync(IntegrationPairingRequest request,
        ReviewedAccessMetadata metadata, string? approver, string stage, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(approver))
            return IntegrationPairingFailure.ForCode("review_approver_authority_missing", stage);
        if (!await IsPolicyApproverAsync(approver, ct).ConfigureAwait(false))
            return IntegrationPairingFailure.ForCode("review_approver_authority_missing", stage);
        if (metadata.ProposalHash != HashProposal(metadata.Proposal) || metadata.ReviewBindingHash != ReviewHash(request.PublicOrigin, request.Resource!, metadata) || !await BindingIsCurrentAsync(request, ct).ConfigureAwait(false))
            return IntegrationPairingFailure.ForCode("review_binding_changed", stage);
        if (metadata.Proposal.Action != "revoke" && metadata.Proposal.ExpiresAtUtc <= clock.GetUtcNow())
            return IntegrationPairingFailure.ForCode("review_credential_expiry_passed", stage);
        if (!metadata.Applied && metadata.Proposal.CredentialId is { } credentialId)
        {
            var credential = await InspectOwnedCredentialAsync(metadata.RequestingPrincipalId, credentialId, ct).ConfigureAwait(false);
            if (credential is null || metadata.CredentialRevisionHash != IntegrationCredentialService.RevisionHash(credential) ||
                metadata.Proposal.Action == "reconcile" && (credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= clock.GetUtcNow()))
                return IntegrationPairingFailure.ForCode("review_credential_changed", stage);
        }
        try
        {
            await ValidateTargetsAsync(metadata.Proposal, ct).ConfigureAwait(false);
        }
        catch (IntegrationCredentialGrantValidationException) { return IntegrationPairingFailure.ForCode("review_target_changed", stage); }
        if (await PolicyRevisionHashAsync(metadata.Proposal, metadata.Environment, request.Id, ct).ConfigureAwait(false) != metadata.PolicyRevisionHash)
            return IntegrationPairingFailure.ForCode("review_policy_changed", stage);
        if (metadata.Proposal.Action != "revoke")
        {
            var grantRequest = new IntegrationCredentialCreateRequest(request.Name, request.Purpose, metadata.Proposal.ExpiresAtUtc, metadata.Proposal.Grants.Select(x => new IntegrationCredentialGrantRequest(x.TenantId, x.Permission)).ToArray(), request.Resource, metadata.Proposal.InstancePermissions);
            try { await validator.ValidateAsync(DurablePrincipal(metadata.RequestingPrincipalId), grantRequest, ct).ConfigureAwait(false); }
            catch (IntegrationCredentialGrantValidationException) { return IntegrationPairingFailure.ForCode("review_owner_authority_missing", stage); }
            try { await validator.ValidateAsync(DurablePrincipal(approver), grantRequest, ct).ConfigureAwait(false); }
            catch (IntegrationCredentialGrantValidationException) { return IntegrationPairingFailure.ForCode("review_approver_authority_missing", stage); }
        }
        var requester = await db.IntegrationCredentials.AsNoTracking().SingleOrDefaultAsync(x => x.Id == metadata.RequestingCredentialId && x.OwnerPrincipalId == metadata.RequestingPrincipalId, ct).ConfigureAwait(false);
        return requester is null || requester.RevokedAtUtc is not null || requester.ExpiresAtUtc <= clock.GetUtcNow() || requester.Resource != request.Resource
            ? IntegrationPairingFailure.ForCode("review_requester_unavailable", stage) : null;
    }

    private async Task<bool> IsPolicyApproverAsync(string id, CancellationToken ct)
    {
        var local = await db.Users.AsNoTracking().Where(x => x.PrincipalId == id).Select(x => (bool?)x.IsEnabled).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        return local != false && effectiveAccess is not null && await effectiveAccess.AuthorizeAsync(DurablePrincipal(id), NetRatelPermissions.McpPolicyAdministration, null, ct).ConfigureAwait(false);
    }

    private async Task<bool> ApplyReviewedAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata, CancellationToken ct)
    {
        if (metadata.Applied) return true;
        if (await ReviewedAuthorityFailureAsync(request, metadata, metadata.ApproverPrincipalId, "resume", ct).ConfigureAwait(false) is { } failure)
        {
            request.State = IntegrationPairingState.Denied;
            request.RequestedPermissionsJson = JsonSerializer.Serialize(metadata with { ReviewFailure = failure });
            await SaveAsync(request, ct).ConfigureAwait(false);
            return false;
        }
        var proposal = CanonicalProposal(metadata.Proposal);
        if (proposal.Action == "create")
        {
            // Normal device polling owns issuance and protected delivery. Policy attachment is done after that exact record exists.
            return true;
        }
        var credential = await db.IntegrationCredentials.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Id == proposal.CredentialId && x.OwnerPrincipalId == metadata.RequestingPrincipalId, ct).ConfigureAwait(false)
            ?? throw new IntegrationPairingUnavailableException();
        request.CredentialId = credential.Id;
        if (proposal.Action == "revoke")
            await credentials.RevokeAsync(metadata.RequestingPrincipalId, credential.Id, metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
        else
        {
            if (credential.RevokedAtUtc is not null || credential.ExpiresAtUtc <= clock.GetUtcNow()) throw new IntegrationPairingUnavailableException();
            if (credentials is not IIntegrationCredentialReviewedLifecycle lifecycle) throw new IntegrationPairingUnavailableException();
            await lifecycle.ReconcileReviewedAsync(metadata.RequestingPrincipalId, credential.Id, CreateRequest(request), metadata.ApproverPrincipalId!, metadata.CredentialRevisionHash!, ct).ConfigureAwait(false);
            await ApplyPoliciesAsync(request, metadata, credential.Id, ct).ConfigureAwait(false);
        }
        request.RequestedPermissionsJson = JsonSerializer.Serialize(metadata with { Applied = true, ProtectedSetup = null });
        request.State = IntegrationPairingState.Consumed;
        await SaveAsync(request, ct).ConfigureAwait(false);
        return true;
    }

    private async Task ApplyPoliciesAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata, string credentialId, CancellationToken ct)
    {
        for (var index = 0; index < metadata.Proposal.Targets.Count; index++)
        {
            var target = metadata.Proposal.Targets[index];
            var reference = $"pairing:{request.Id}:{index}";
            var existing = (await policyAdministration!.ListAsync(metadata.Environment, target.TenantId, ct).ConfigureAwait(false)).Items
                .Where(x => x.AuditReference == reference).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1 || existing[0].Version != 1 || existing[0].LifecycleState != McpOperatorPolicyLifecycleState.Active || existing[0].DisabledAtUtc is not null ||
                    existing[0].PrincipalSelector.Kind != McpOperatorPrincipalSelectorKind.OAuthClientId || existing[0].PrincipalSelector.Value != credentialId ||
                    existing[0].TargetSelector.TenantId != target.TenantId || existing[0].TargetSelector.AgentId != target.AgentId ||
                    existing[0].Operation != target.Tool + "/" + target.Operation || existing[0].ExpiresAtUtc != metadata.Proposal.ExpiresAtUtc)
                    throw new IntegrationPairingUnavailableException();
                continue;
            }
            var descriptor = McpOperatorOperationCatalog.Find(target.Tool, target.Operation)!;
            var profile = target.AgentId is { } agent ? await policyAdministration.GetTargetProfileAsync(target.TenantId, agent, ct).ConfigureAwait(false) : null;
            var selector = target.AgentId is not null ? McpOperatorTargetSelectorKind.ExactAgent : target.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant;
            await policyAdministration.CreateAsync(new($"Reviewed client {request.Id[..8]} operation {index + 1}", metadata.Environment,
                McpOperatorPolicyEffect.Allow, 0, new(McpOperatorPrincipalSelectorKind.OAuthClientId, credentialId),
                new(selector, target.TenantId, target.AgentId), profile?.Classification, descriptor.OperationFamily,
                target.Tool + "/" + target.Operation, new(), metadata.Proposal.ExpiresAtUtc, metadata.Proposal.ExpiresAtUtc, reference), metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
        }

        // The policy mutation API evaluates a stable operation shared by its preview and
        // confirmation routes. Attach that internal operation only when the reviewer
        // selected both published phases for the same persisted target. The public
        // phase policies above still guard MCP dispatch independently.
        var mutationPairs = new (string Preview, string Confirm, string Canonical)[]
        {
            ("preview_create", "confirm_create", "create"),
            ("preview_replace", "confirm_replace", "replace"),
            ("preview_disable", "confirm_disable", "disable"),
            ("preview_revoke", "confirm_revoke", "revoke"),
            ("preview_target_profile", "confirm_target_profile", "upsert_target_profile")
        };
        for (var index = 0; index < metadata.Proposal.Targets.Count; index++)
        {
            var target = metadata.Proposal.Targets[index];
            if (target.Tool != "netratel_policy") continue;
            var pair = mutationPairs.FirstOrDefault(x => x.Preview == target.Operation);
            if (pair.Canonical is null || !metadata.Proposal.Targets.Any(other =>
                    other.Tool == target.Tool && other.Operation == pair.Confirm &&
                    other.TenantId == target.TenantId && other.AgentId == target.AgentId &&
                    other.ProfileRevision == target.ProfileRevision)) continue;

            var operation = target.Tool + "/" + pair.Canonical;
            var reference = $"pairing:{request.Id}:canonical:{index}";
            var existing = (await policyAdministration!.ListAsync(metadata.Environment, target.TenantId, ct).ConfigureAwait(false)).Items
                .Where(x => x.AuditReference == reference).ToArray();
            if (existing.Length > 0)
            {
                if (existing.Length != 1 || existing[0].Version != 1 || existing[0].LifecycleState != McpOperatorPolicyLifecycleState.Active || existing[0].DisabledAtUtc is not null ||
                    existing[0].PrincipalSelector.Kind != McpOperatorPrincipalSelectorKind.OAuthClientId || existing[0].PrincipalSelector.Value != credentialId ||
                    existing[0].TargetSelector.TenantId != target.TenantId || existing[0].TargetSelector.AgentId != target.AgentId ||
                    existing[0].Operation != operation || existing[0].ExpiresAtUtc != metadata.Proposal.ExpiresAtUtc)
                    throw new IntegrationPairingUnavailableException();
                continue;
            }
            var profile = target.AgentId is { } agent ? await policyAdministration.GetTargetProfileAsync(target.TenantId, agent, ct).ConfigureAwait(false) : null;
            var selector = target.AgentId is not null ? McpOperatorTargetSelectorKind.ExactAgent : target.TenantId == 0 ? McpOperatorTargetSelectorKind.ControlPlane : McpOperatorTargetSelectorKind.Tenant;
            await policyAdministration.CreateAsync(new($"Reviewed client {request.Id[..8]} policy {pair.Canonical}", metadata.Environment,
                McpOperatorPolicyEffect.Allow, 0, new(McpOperatorPrincipalSelectorKind.OAuthClientId, credentialId),
                new(selector, target.TenantId, target.AgentId), profile?.Classification, McpOperatorOperationFamily.PolicyAdministration,
                operation, new(), metadata.Proposal.ExpiresAtUtc, metadata.Proposal.ExpiresAtUtc, reference), metadata.ApproverPrincipalId!, ct).ConfigureAwait(false);
        }
    }

    private static void RequireReviewedExpiry(DateTimeOffset expiry)
    {
        if (!ReviewedMcpAccessExpiry.IsSupported(expiry)) throw new IntegrationCredentialGrantValidationException("expiryPrecision");
    }

    private async Task ValidateTargetsAsync(ReviewedMcpAccessProposal proposal, CancellationToken ct)
    {
        foreach (var target in proposal.Targets)
        {
            var operation = McpOperatorOperationCatalog.Find(target.Tool, target.Operation);
            var model = McpOperationTargetCatalog.Find(target.Tool, target.Operation)?.Model;
            var permission = McpLocalOperationAuthorizationCatalog.RequiredPermission(target.Tool, target.Operation);
            if (operation is null || permission is null || model is null ||
                target.TenantId < 0 || model == McpOperationTargetModel.NoBusinessTarget && (target.TenantId != 0 || target.AgentId is not null) ||
                model == McpOperationTargetModel.Tenant && (target.TenantId <= 0 || target.AgentId is not null) ||
                (model is McpOperationTargetModel.Agent or McpOperationTargetModel.ObjectDerived) && (target.TenantId <= 0 || target.AgentId is null || target.ProfileRevision is null) ||
                model == McpOperationTargetModel.OptionalTenant && target.AgentId is not null ||
                target.AgentId is not null && (target.TenantId <= 0 || target.ProfileRevision is null) || target.AgentId is null && target.ProfileRevision is not null)
                throw new IntegrationCredentialGrantValidationException("targets");
            foreach (var required in new[] { permission }.Concat(McpLocalOperationAuthorizationCatalog.AdditionalPermissions(target.Tool, target.Operation)))
                if (target.TenantId == 0 ? !proposal.InstancePermissions.Contains(required, StringComparer.Ordinal) : !proposal.Grants.Any(x => x.TenantId == target.TenantId && x.Permission == required))
                    throw new IntegrationCredentialGrantValidationException("operationGrants");
            if (target.AgentId is { } agent)
            {
                var profile = await policyAdministration!.GetTargetProfileAsync(target.TenantId, agent, ct).ConfigureAwait(false);
                if (profile is null || profile.Version != target.ProfileRevision) throw new IntegrationCredentialGrantValidationException("reviewedRevision");
            }
        }
    }

    private async Task<string> PolicyRevisionHashAsync(ReviewedMcpAccessProposal proposal, McpOperatorEnvironment environment, string requestId, CancellationToken ct)
    {
        var tenants = proposal.Targets.Select(x => x.TenantId).Distinct().ToArray();
        var policies = await applicationDb!.McpOperatorPolicies.AsNoTracking().Where(x => x.Environment == environment && tenants.Contains(x.TenantId))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.Version, x.LifecycleState, x.AuditReference }).ToArrayAsync(ct).ConfigureAwait(false);
        return IntegrationCredentialService.Hash(JsonSerializer.Serialize(policies.Where(x => x.AuditReference is null || !x.AuditReference.StartsWith("pairing:" + requestId + ":", StringComparison.Ordinal) || x.Version != 1 || x.LifecycleState != McpOperatorPolicyLifecycleState.Active)));
    }

    private async Task<ReviewedMcpAccessReceipt> ReceiptAsync(IntegrationPairingRequest request, ReviewedAccessMetadata metadata, string? code, CancellationToken ct)
    {
        var credentialId = request.CredentialId ?? metadata.Proposal.CredentialId;
        var credential = credentialId is null ? null : await InspectOwnedCredentialAsync(metadata.RequestingPrincipalId, credentialId, ct).ConfigureAwait(false);
        var state = credential is null ? "not_issued" : credential.RevokedAtUtc is not null ? "revoked" : credential.ExpiresAtUtc <= clock.GetUtcNow() ? "expired" : "active";
        var admissions = await EvaluateCredentialAdmissionAsync(metadata.RequestingPrincipalId, credential, state,
            request.Resource!, metadata.Instance, metadata.Environment, metadata.Proposal.Targets, request.Id, ct).ConfigureAwait(false);
        var missing = new List<string>();
        if (request.State == IntegrationPairingState.Pending) missing.Add("An authorized operator must review the exact proposal with current credential and MCP policy authority. Owner self-approval is recorded explicitly.");
        if (metadata.Proposal.Action == "create" && request.State is not (IntegrationPairingState.Consumed or IntegrationPairingState.Issued)) missing.Add("Complete the supported protected setup client; MCP never delivers credential secrets.");
        if (state != "active") missing.Add("The reviewed credential is " + state + "; configured grants alone do not establish usable access.");
        if (admissions.Any(x => !x.ApplicationGrantConfigured)) missing.Add("Configured credential grants do not include every selected operation.");
        if (admissions.Any(x => !x.OwnerPermissionGranted)) missing.Add("The credential owner no longer has every selected durable application permission; an account administrator must review current role assignments.");
        if (admissions.Any(x => !x.TenantVisible || x.PolicyAllowed == false)) missing.Add("One or more current tenant or target policies do not admit the selected operation.");
        if (admissions.Any(x => x.PolicyAdmissionStatus == "conditional_runtime_unverified")) missing.Add("Agent admission remains conditional: the pairing authority has not verified current target readiness or the operation capability. Read netratel_access target/effective for the exact target, then use the supported readiness/dispatch path.");
        missing.Add("Dispatch rechecks current owner authority, target readiness, capability, preview/confirmation and idempotency.");
        var reviewFailure = request.State == IntegrationPairingState.Expired ? IntegrationPairingFailure.ForCode("review_request_expired", "receipt") : metadata.ReviewFailure;
        return new(AccessRequest(request, metadata, code ?? SafeUserCode(request, metadata)), credentialId, credential?.ExpiresAtUtc, state,
            credential?.Grants.Select(x => new IntegrationPairingGrant(x.TenantId, x.Permission)).ToArray() ?? [], credential?.InstancePermissions ?? [], admissions, missing,
            reviewFailure?.Remediation ?? (request.State == IntegrationPairingState.Pending ? "Open the review URL and have an authorized operator enter the displayed code; then resume this same requestId." : admissions.Any(x => x.PolicyAdmissionStatus == "conditional_runtime_unverified") ? "Read netratel_access target/effective for the exact persisted target and policy, then inspect the operation readiness before dispatch. Runtime admission remains unverified in this receipt." : "Inspect each current admission result; reuse this requestId to reconcile an interrupted outcome."))
        {
            ReviewFailure = reviewFailure
        };
    }

    private string? SafeUserCode(IntegrationPairingRequest request, ReviewedAccessMetadata metadata)
    {
        if (request.State != IntegrationPairingState.Pending || metadata.ProtectedSetup is null) return null;
        try { return FormatCode(JsonSerializer.Deserialize<ProtectedSetupMaterial>(protection.CreateProtector("NetRatel.IntegrationPairing.Setup.v1", request.Id).Unprotect(metadata.ProtectedSetup))!.Code); }
        catch (CryptographicException) { return null; }
    }
    private async Task<string?> CredentialRevisionHashAsync(string? id, string owner, CancellationToken ct)
    {
        if (id is null) return null;
        var credential = await InspectOwnedCredentialAsync(owner, id, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
        return IntegrationCredentialService.RevisionHash(credential);
    }

    private async Task<T> MutateByAccessIdAsync<T>(string id, string owner, string requester, Func<IntegrationPairingRequest, Task<T>> action, CancellationToken ct)
    {
        var row = await FindAccessAsync(id, owner, requester, ct).ConfigureAwait(false) ?? throw new IntegrationPairingUnavailableException();
        // Reuse the pairing transaction/CAS boundary without accepting a bearer capability as a tool argument.
        return await MutateAsync(row.DeviceCapabilityHash, true, action, ct).ConfigureAwait(false);
    }
    private async Task<IntegrationPairingRequest?> FindAccessAsync(string id, string owner, string requester, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid) || guid == Guid.Empty) throw new IntegrationPairingUnavailableException();
        var row = await db.IntegrationPairingRequests.AsNoTracking().SingleOrDefaultAsync(x => x.Id == guid.ToString("N"), ct).ConfigureAwait(false);
        var metadata = row is null ? null : AccessMetadata(row);
        if (row is not null && (metadata?.RequestingPrincipalId != owner || metadata.RequestingCredentialId != requester)) throw new IntegrationPairingUnavailableException();
        return row;
    }
    private static void RequireCurrentAccessContext(IntegrationPairingRequest row, string? resource, McpOperatorEnvironment? environment)
    {
        if (resource is not null && !McpResourceUri.Equivalent(row.Resource, resource) || environment is not null && AccessMetadata(row)!.Environment != environment)
            throw new IntegrationPairingUnavailableException();
    }
    private static ClaimsPrincipal DurablePrincipal(string id) => new(new ClaimsIdentity([new Claim("netratel_principal_id", id)], "pairing-owner"));
    private static IntegrationCredentialGrantValidationException ReviewValidation(string code, string stage, string field) =>
        new(field) { ReviewFailure = IntegrationPairingFailure.ForCode(code, stage) };
    private static string CanonicalUuid(string value, string field)
    {
        if (!Guid.TryParse(value, out var id) || id == Guid.Empty)
            throw ReviewValidation(field == "requestId" ? "review_request_id_invalid" : "review_credential_id_invalid", "request", field);
        return id.ToString("N");
    }
    private static ReviewedMcpAccessProposal CanonicalProposal(ReviewedMcpAccessProposal proposal) => proposal with
    {
        RequestId = CanonicalUuid(proposal.RequestId, "requestId"),
        CredentialId = proposal.CredentialId is null ? null : CanonicalUuid(proposal.CredentialId, "credentialId")
    };
    private static bool SameProposal(ReviewedAccessMetadata metadata, ReviewedMcpAccessProposal proposal) =>
        // Validate the original binding before comparing canonical UUIDs. Persisted beta requests keep their
        // exact original proposal/hash so an already reviewed or approved binding is never rewritten by a retry.
        metadata.ProposalHash == HashProposal(metadata.Proposal) &&
        HashProposal(CanonicalProposal(metadata.Proposal)) == HashProposal(CanonicalProposal(proposal));
    private static string ReviewHash(string origin, string resource, ReviewedAccessMetadata metadata) => IntegrationCredentialService.Hash(JsonSerializer.Serialize(new
    { origin, resource, metadata.RequestingPrincipalId, metadata.RequestingCredentialId, metadata.Instance, metadata.Environment, metadata.ProposalHash, metadata.CredentialRevisionHash, metadata.PolicyRevisionHash }));
    private static string HashProposal(ReviewedMcpAccessProposal proposal) => IntegrationCredentialService.Hash(JsonSerializer.Serialize(new
    {
        Proposal = proposal,
        OperationRequirements = proposal.Targets.Select(target => new
        {
            target.Tool, target.Operation,
            TargetModel = McpOperationTargetCatalog.Find(target.Tool, target.Operation)?.Model,
            Permission = McpLocalOperationAuthorizationCatalog.RequiredPermission(target.Tool, target.Operation),
            AdditionalPermissions = McpLocalOperationAuthorizationCatalog.AdditionalPermissions(target.Tool, target.Operation),
            Scope = McpOperationAccessCatalog.Find(target.Tool, target.Operation)?.RequiredScope,
            Roles = McpOperationAccessCatalog.Find(target.Tool, target.Operation) is { } operation ? McpLocalOperationAuthorizationCatalog.RolesFor(operation.RequiredScope) : []
        })
    }));
    private static bool SameGrants(IReadOnlyList<IntegrationPairingGrant> left, IReadOnlyList<IntegrationPairingGrant> right) =>
        left.Select(x => (x.TenantId, x.Permission)).Order().SequenceEqual(right.Select(x => (x.TenantId, x.Permission)).Order());
    private static ReviewedAccessMetadata? AccessMetadata(IntegrationPairingRequest request)
    {
        using var json = JsonDocument.Parse(request.RequestedPermissionsJson);
        return json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty("ReviewedAccess", out var enabled) && enabled.ValueKind == JsonValueKind.True
            ? JsonSerializer.Deserialize<ReviewedAccessMetadata>(request.RequestedPermissionsJson) : null;
    }
    private static ReviewedMcpAccessRequest AccessRequest(IntegrationPairingRequest request, ReviewedAccessMetadata metadata, string? code) =>
        new(request.Id, Status(request), request.PublicOrigin + "/account/integration-credentials?tab=credentials", code, metadata.Proposal,
            request.Resource!, metadata.Instance, metadata.Environment.ToString(), metadata.RequestingPrincipalId, metadata.RequestingCredentialId, request.Revision, metadata.ReviewBindingHash!)
        { CredentialRevisionHash = metadata.CredentialRevisionHash, PolicyRevisionHash = metadata.PolicyRevisionHash,
            ApprovalMode = metadata.ApproverPrincipalId is null ? null : metadata.ApproverPrincipalId == metadata.RequestingPrincipalId ? "owner_self_approval" : "independent_review" };
}
