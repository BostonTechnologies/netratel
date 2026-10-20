using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Identity.Branding;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ReviewedMcpAccessTests
{
    [Fact]
    public async Task Equivalent_UUID_representations_share_owned_lookup_proposal_and_reconciliation()
    {
        await using var fixture = await Fixture.CreateAsync();
        var credentialId = Guid.Parse(fixture.Credential.CredentialId);
        var requestId = Guid.NewGuid();
        var proposal = fixture.Proposal("reconcile", credentialId.ToString("D").ToUpperInvariant()) with { RequestId = requestId.ToString("D") };
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        Assert.Equal(requestId.ToString("N"), pending.Request.Proposal.RequestId);
        Assert.Equal(credentialId.ToString("N"), pending.Request.Proposal.CredentialId);
        foreach (var format in new[] { "D", "N", "B", "P" })
        {
            var owned = await fixture.Pairing.InspectOwnedCredentialAsync("requester", credentialId.ToString(format).ToUpperInvariant());
            Assert.Equal(credentialId.ToString("N"), owned!.Id);
            var retry = await fixture.Pairing.RequestReviewedAsync(proposal with
            { RequestId = requestId.ToString(format).ToUpperInvariant(), CredentialId = credentialId.ToString(format) },
                "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
            Assert.Equal(pending.Request.ProposalHash, retry.Request.ProposalHash);
            Assert.Equal(pending.Request.UserCode, retry.Request.UserCode);
        }
        Assert.Single(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
        var conflict = await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.RequestReviewedAsync(proposal with { Name = "Different intent" },
            "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production));
        Assert.Equal("review_proposal_conflict", conflict.ReviewFailure!.Code);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        var applied = await fixture.Pairing.InspectReviewedAsync(requestId.ToString("B"), "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("consumed", applied.Request.Status);
        Assert.Equal(proposal.ExpiresAtUtc, applied.CredentialExpiresAtUtc);
        Assert.Single(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(x => x.Action == "reconciled").ToArrayAsync());
        Assert.Null(await fixture.Pairing.InspectOwnedCredentialAsync("other-owner", credentialId.ToString("D")));
        Assert.Null(await fixture.Pairing.InspectOwnedCredentialAsync("other-owner", Guid.NewGuid().ToString("D")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_UUID_binding_is_preserved_during_pending_or_approved_canonical_replay(bool alreadyApproved)
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId) with { Grants = [], Targets = [] };
        var original = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var row = await fixture.Identity.IntegrationPairingRequests.SingleAsync();
        var metadata = JsonNode.Parse(row.RequestedPermissionsJson)!.AsObject();
        // Frozen beta.23 wire shape: UUID strings were hashed verbatim. An empty target list has no catalog requirements.
        metadata["Proposal"] = JsonSerializer.SerializeToNode(proposal);
        var legacyProposalHash = IntegrationCredentialService.Hash(JsonSerializer.Serialize(new { Proposal = proposal, OperationRequirements = Array.Empty<object>() }));
        metadata["ProposalHash"] = legacyProposalHash;
        var legacyReviewHash = IntegrationCredentialService.Hash(JsonSerializer.Serialize(new
        {
            origin = Fixture.Origin, resource = Fixture.Resource, RequestingPrincipalId = "requester", RequestingCredentialId = fixture.Credential.CredentialId,
            Instance = "prod", Environment = McpOperatorEnvironment.Production, ProposalHash = legacyProposalHash,
            CredentialRevisionHash = original.Request.CredentialRevisionHash, PolicyRevisionHash = original.Request.PolicyRevisionHash
        }));
        metadata["ReviewBindingHash"] = legacyReviewHash;
        row.RequestedPermissionsJson = metadata.ToJsonString();
        await fixture.Identity.SaveChangesAsync();
        var review = await fixture.Pairing.ReviewAsync(Principal("reviewer"), original.Request.UserCode!);
        Assert.True(review.Eligibility!.CanApprove);
        var approval = fixture.Approval(original) with { ReviewedProposalHash = legacyReviewHash };
        if (alreadyApproved) await fixture.Pairing.ApproveAsync(Principal("reviewer"), approval);
        var replay = await fixture.Pairing.RequestReviewedAsync(proposal with { RequestId = Guid.Parse(proposal.RequestId).ToString("N") },
            "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        Assert.Equal(legacyReviewHash, replay.Request.ProposalHash);
        Assert.Equal(proposal.RequestId, replay.Request.Proposal.RequestId);
        var persisted = JsonNode.Parse((await fixture.Identity.IntegrationPairingRequests.SingleAsync()).RequestedPermissionsJson)!;
        Assert.Equal(legacyProposalHash, persisted["ProposalHash"]!.GetValue<string>());
        Assert.Equal(legacyReviewHash, persisted["ReviewBindingHash"]!.GetValue<string>());
        if (!alreadyApproved) await fixture.Pairing.ApproveAsync(Principal("reviewer"), approval);
        Assert.Equal("consumed", (await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true)).Request.Status);
    }

    [Theory]
    [InlineData("requestId", "not-a-uuid", "review_request_id_invalid")]
    [InlineData("credentialId", "not-a-uuid", "review_credential_id_invalid")]
    [InlineData("credentialId", "00000000-0000-0000-0000-000000000000", "review_credential_id_invalid")]
    public async Task Invalid_UUIDs_are_actionable_and_never_persist_a_proposal(string field, string value, string expectedCode)
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        proposal = field == "requestId" ? proposal with { RequestId = value } : proposal with { CredentialId = value };
        var failure = await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.RequestReviewedAsync(proposal,
            "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production));
        Assert.Equal(expectedCode, failure.ReviewFailure!.Code);
        Assert.Equal("request", failure.ReviewFailure.Stage);
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
    }

    [Theory]
    [InlineData("requester", "none", "review_approver_authority_missing")]
    [InlineData("reviewer", "approver", "review_approver_authority_missing")]
    [InlineData("reviewer", "owner", "review_owner_authority_missing")]
    [InlineData("reviewer", "credential", "review_credential_changed")]
    [InlineData("reviewer", "policy", "review_policy_changed")]
    [InlineData("reviewer", "target", "review_target_changed")]
    [InlineData("reviewer", "binding", "review_binding_changed")]
    public async Task Review_eligibility_and_actual_decision_report_current_safe_failure_without_approval(string actor, string change, string code)
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        if (change == "target")
        {
            fixture.Identity.AccessRoles.Local.Single().Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.TelemetryRead });
            await fixture.Identity.SaveChangesAsync();
            var agent = Guid.Parse("b2222222-2222-4222-8222-222222222222");
            fixture.App.Agents.Add(new Agent { Id = agent, TenantId = 17, Name = "Synthetic profile target", IsEnabled = true, Status = AgentStatus.Active });
            await fixture.App.SaveChangesAsync();
            var profile = await new McpOperatorPolicyAdministration(fixture.App).UpsertTargetProfileAsync(17, agent, McpOperatorTargetClassification.DedicatedQa, [], null, "reviewer", default);
            proposal = proposal with { Grants = [new(17, NetRatelPermissions.TelemetryRead)], Targets = [new(17, "netratel_clients", "telemetry", agent, profile.Version)] };
        }
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        Assert.True((await fixture.Pairing.ReviewAsync(Principal("reviewer"), pending.Request.UserCode!)).Eligibility!.CanApprove);
        switch (change)
        {
            case "approver": (await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "reviewer")).IsInstanceAdministrator = false; break;
            case "owner": (await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "requester")).IsEnabled = false; break;
            case "credential": (await fixture.Identity.IntegrationCredentials.SingleAsync()).Name = "Changed synthetic credential"; break;
            case "policy":
                fixture.App.McpOperatorPolicies.Add(new McpOperatorPolicyRecord { Id = Guid.NewGuid(), TenantId = 17, Environment = McpOperatorEnvironment.Production, Version = 1 });
                await fixture.App.SaveChangesAsync(); break;
            case "target":
                (await fixture.App.McpOperatorTargetProfiles.SingleAsync()).Version++;
                await fixture.App.SaveChangesAsync(); break;
            case "binding": (await fixture.Identity.IntegrationPairingRequests.SingleAsync()).Resource = "https://changed.example.test/mcp"; break;
        }
        await fixture.Identity.SaveChangesAsync();
        var review = await fixture.Pairing.ReviewAsync(Principal(actor), pending.Request.UserCode!);
        Assert.False(review.Eligibility!.CanApprove);
        Assert.Equal(code, review.Eligibility.Failure!.Code);
        Assert.Equal("lookup", review.Eligibility.Failure.Stage);
        var rejected = await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.ApproveAsync(Principal(actor), fixture.Approval(pending)));
        Assert.Equal(code, rejected.ReviewFailure!.Code);
        Assert.Equal("approval", rejected.ReviewFailure.Stage);
        Assert.Equal(IntegrationPairingState.Pending, (await fixture.Identity.IntegrationPairingRequests.SingleAsync()).State);
        Assert.Empty(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(x => x.Action == "reconciled").ToArrayAsync());
        Assert.DoesNotContain(fixture.Credential.Secret, JsonSerializer.Serialize(review.Eligibility));
        Assert.DoesNotContain(pending.Request.UserCode!, JsonSerializer.Serialize(review.Eligibility));
    }

    [Fact]
    public async Task Lookup_distinguishes_expired_and_decided_codes_from_invalid_or_unavailable_codes()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var invalid = await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.ReviewAsync(Principal("reviewer"), "invalid"));
        Assert.Equal("review_code_invalid", invalid.ReviewFailure!.Code);
        var unavailable = await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.ReviewAsync(Principal("reviewer"), "ZZZZ-ZZZZ"));
        Assert.Null(unavailable.ReviewFailure);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        var decided = await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.ReviewAsync(Principal("reviewer"), pending.Request.UserCode!));
        Assert.Equal("review_request_not_pending", decided.ReviewFailure!.Code);
        fixture.Clock.Advance(TimeSpan.FromMinutes(10));
        var expired = await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.ReviewAsync(Principal("reviewer"), pending.Request.UserCode!));
        Assert.Equal("review_request_expired", expired.ReviewFailure!.Code);
        var receipt = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        Assert.Equal("expired", receipt.Request.Status);
        Assert.Equal("review_request_expired", receipt.ReviewFailure!.Code);
        Assert.True((await fixture.Credentials.ListAsync("requester")).Single().ExpiresAtUtc > fixture.Clock.GetUtcNow());
    }

    [Fact]
    public async Task Proposed_credential_expiry_is_distinct_from_review_deadline_and_authority_loss()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("create", null) with { ExpiresAtUtc = fixture.Clock.GetUtcNow().AddSeconds(10) };
        var started = await fixture.Pairing.StartReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var receipt = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(11));
        Assert.True(started.ExpiresAtUtc > fixture.Clock.GetUtcNow());
        var review = await fixture.Pairing.ReviewAsync(Principal("reviewer"), started.UserCode);
        Assert.False(review.Eligibility!.CanApprove);
        Assert.Equal("review_credential_expiry_passed", review.Eligibility.Failure!.Code);
        var failed = await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(receipt)));
        Assert.Equal("review_credential_expiry_passed", failed.ReviewFailure!.Code);
        Assert.Single(await fixture.Identity.IntegrationCredentials.ToArrayAsync());
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
    }

    [Fact]
    public async Task Direct_reviewed_authority_rejects_unsupported_expiry_without_persisting_a_request()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("create", null) with { ExpiresAtUtc = fixture.Clock.GetUtcNow().AddDays(1).AddTicks(1) };
        var rejected = await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.StartReviewedAsync(proposal,
            "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production));
        Assert.Equal("expiryPrecision", rejected.Field);
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
    }

    [Fact]
    public async Task Exact_independent_review_admits_only_two_selected_tenant_reads_and_resume_is_idempotent()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var configured = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        Assert.Equal("current_credential_valid", configured!.AuthenticationStatus);
        Assert.Equal(new[] { 17, 29 }, configured.EffectiveAdmission.Select(x => x.Target.TenantId));
        Assert.All(configured.EffectiveAdmission, x => { Assert.True(x.ApplicationGrantConfigured); Assert.True(x.OwnerPermissionGranted); Assert.Equal("tenant_not_authorized", x.FailureCode); });
        Assert.Contains(configured.RemainingPrerequisites, x => x.Contains("Matching Production operator policies"));
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
        Assert.Empty(await fixture.Identity.IntegrationPairingRequests.ToArrayAsync());
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        Assert.All(pending.EffectiveAdmission, x => { Assert.True(x.ApplicationGrantConfigured); Assert.False(x.TenantVisible); Assert.False(x.PolicyAllowed); });
        var self = fixture.Approval(pending);
        await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.ApproveAsync(Principal("requester"), self));
        await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.ApproveAsync(Principal("reviewer"), self with { ExpiresAtUtc = self.ExpiresAtUtc.AddSeconds(1) }));
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), self);
        var approved = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("consumed", approved.Request.Status);
        Assert.Equal(proposal.ExpiresAtUtc, approved.CredentialExpiresAtUtc);
        Assert.All(approved.EffectiveAdmission, x => { Assert.True(x.TenantVisible); Assert.True(x.PolicyAllowed); Assert.Equal("not_checked", x.TargetReadiness); Assert.Null(x.FailureCode); });
        await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal(2, await fixture.App.McpOperatorPolicies.CountAsync());
        Assert.Single(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(x => x.Action == "reconciled").ToArrayAsync());
        Assert.False(await fixture.Authorization.HasTenantVisibilityAsync(McpOperatorEnvironment.Production,
            new("requester", "other-client", null, new HashSet<string>(), new HashSet<string>(), new HashSet<string>(["netratel.mcp.read"])), 17, default));
        await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "other-owner", fixture.Credential.CredentialId));
        var serialized = JsonSerializer.Serialize(approved);
        Assert.DoesNotContain(fixture.Credential.Secret, serialized);
        Assert.DoesNotContain("DeviceCapability", serialized);
        var refreshed = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        Assert.Equal(proposal.ExpiresAtUtc, refreshed!.CredentialExpiresAtUtc);
        Assert.Equal(Fixture.Resource, refreshed.Resource);
        Assert.Equal("prod", refreshed.Instance);
        Assert.All(refreshed.EffectiveAdmission, x => Assert.True(x.AuthorizationAdmitted));
        Assert.Equal(2, refreshed.MatchingPolicies.Count);
        Assert.All(refreshed.MatchingPolicies, x => { Assert.Equal("Production", x.Environment); Assert.Equal(proposal.ExpiresAtUtc, x.ExpiresAtUtc); });
        Assert.DoesNotContain(fixture.Credential.Secret, JsonSerializer.Serialize(refreshed));
        Assert.Null(await fixture.Pairing.InspectCredentialAdmissionAsync("other-owner", fixture.Credential.CredentialId, McpOperatorEnvironment.Production));
        await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId,
            McpOperatorEnvironment.Production, [new(31, "netratel_flows", "list")]));

        var role = await fixture.Identity.AccessRoles.Include(x => x.Permissions).SingleAsync();
        var permission = role.Permissions.Single(x => x.Permission == NetRatelPermissions.FlowRead);
        role.Permissions.Remove(permission); fixture.Identity.Remove(permission); await fixture.Identity.SaveChangesAsync();
        var withdrawn = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        var denied = Assert.Single(withdrawn!.EffectiveAdmission.Where(x => x.Target.TenantId == 17));
        Assert.False(denied.OwnerPermissionGranted); Assert.False(denied.AuthorizationAdmitted); Assert.Equal("owner_permission_missing", denied.FailureCode);
        var row = await fixture.Identity.IntegrationCredentials.SingleAsync();
        row.ExpiresAtUtc = fixture.Clock.GetUtcNow().AddSeconds(-1); await fixture.Identity.SaveChangesAsync();
        var expired = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        Assert.Equal("expired", expired!.CredentialState); Assert.All(expired.EffectiveAdmission, x => Assert.False(x.AuthorizationAdmitted));
        await fixture.Credentials.RevokeAsync("requester", fixture.Credential.CredentialId, "requester");
        var revoked = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        Assert.Equal("revoked", revoked!.CredentialState); Assert.All(revoked.EffectiveAdmission, x => Assert.False(x.AuthorizationAdmitted));
    }

    [Fact]
    public async Task Authorized_owner_can_approve_own_exact_proposal_with_truthful_receipt_and_actor_audit()
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "requester")).IsInstanceAdministrator = true;
        await fixture.Identity.SaveChangesAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId,
            Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var review = await fixture.Pairing.ReviewAsync(Principal("requester"), pending.Request.UserCode!);
        Assert.True(review.Eligibility!.CanApprove);
        Assert.True(review.Eligibility.OwnerSelfApproval);
        await fixture.Pairing.ApproveAsync(Principal("requester"), fixture.Approval(pending));
        var approved = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("consumed", approved.Request.Status);
        Assert.Equal("owner_self_approval", approved.Request.ApprovalMode);
        Assert.Equal(2, await fixture.App.McpOperatorPolicies.CountAsync());
        var audit = await fixture.Identity.IntegrationCredentialLifecycleEvents.SingleAsync(x => x.Action == "reconciled");
        Assert.Equal("requester", audit.OwnerPrincipalId);
        Assert.Equal("requester", audit.ActorPrincipalId);
        Assert.Contains("owner_self_approval", JsonSerializer.Serialize(approved));

        var revokeProposal = fixture.Proposal("revoke", fixture.Credential.CredentialId) with { Targets = [] };
        var revokePending = await fixture.Pairing.RequestReviewedAsync(revokeProposal, "requester", fixture.Credential.CredentialId,
            Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("requester"), fixture.Approval(revokePending));
        var revoked = await fixture.Pairing.InspectReviewedAsync(revokeProposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("consumed", revoked.Request.Status);
        Assert.Equal("owner_self_approval", revoked.Request.ApprovalMode);
        Assert.Equal("revoked", revoked.CredentialState);
        var current = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId, McpOperatorEnvironment.Production);
        Assert.Equal("revoked", current!.CredentialState);
        Assert.All(current.EffectiveAdmission, admission => Assert.False(admission.AuthorizationAdmitted));
    }

    [Theory]
    [InlineData("preview_create", "confirm_create", "create", true)]
    [InlineData("preview_create", "confirm_create", "create", false)]
    [InlineData("preview_replace", "confirm_replace", "replace", true)]
    [InlineData("preview_replace", "confirm_replace", "replace", false)]
    [InlineData("preview_disable", "confirm_disable", "disable", true)]
    [InlineData("preview_disable", "confirm_disable", "disable", false)]
    [InlineData("preview_revoke", "confirm_revoke", "revoke", true)]
    [InlineData("preview_revoke", "confirm_revoke", "revoke", false)]
    [InlineData("preview_target_profile", "confirm_target_profile", "upsert_target_profile", true)]
    [InlineData("preview_target_profile", "confirm_target_profile", "upsert_target_profile", false)]
    public async Task Reviewed_policy_mutation_phases_attach_canonical_API_permission_only_as_a_pair(
        string preview, string confirm, string canonical, bool includeConfirm)
    {
        await using var fixture = await Fixture.CreateAsync();
        (await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "requester")).IsInstanceAdministrator = true;
        await fixture.Identity.SaveChangesAsync();
        var agent = Guid.NewGuid();
        fixture.App.Agents.Add(new Agent { Id = agent, TenantId = 17, Name = "Policy test target", IsEnabled = true, Status = AgentStatus.Active });
        await fixture.App.SaveChangesAsync();
        var profile = await new McpOperatorPolicyAdministration(fixture.App).UpsertTargetProfileAsync(
            17, agent, McpOperatorTargetClassification.DedicatedQa, [], null, "requester", default);
        var baseProposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var targets = new List<ReviewedMcpAccessTarget>
        {
            new(17, "netratel_policy", preview, agent, profile.Version)
        };
        if (includeConfirm) targets.Add(new(17, "netratel_policy", confirm, agent, profile.Version));
        var proposal = baseProposal with
        {
            Grants = [new(17, NetRatelPermissions.FlowRead), new(29, NetRatelPermissions.FlowRead), new(17, NetRatelPermissions.McpPolicyAdministration)],
            Targets = targets
        };
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId,
            Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("requester"), fixture.Approval(pending));
        var applied = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("consumed", applied.Request.Status);
        Assert.Equal("owner_self_approval", applied.Request.ApprovalMode);
        await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        var policies = await fixture.App.McpOperatorPolicies.ToArrayAsync();
        Assert.Equal(includeConfirm ? 3 : 1, policies.Length);
        Assert.Equal(includeConfirm, policies.Any(x => x.Operation == "netratel_policy/" + canonical));
        Assert.All(policies, x =>
        {
            Assert.Equal(fixture.Credential.CredentialId, x.PrincipalSelectorValue);
            Assert.Equal(agent, x.AgentId);
            Assert.Equal(proposal.ExpiresAtUtc, x.ExpiresAtUtc);
        });

        var principal = new McpOperatorPrincipal("requester", fixture.Credential.CredentialId,
            fixture.Credential.CredentialId, new HashSet<string>(), new HashSet<string>(["PolicyAdministrator"]),
            new HashSet<string>(["netratel.mcp.admin"]));
        var decision = await fixture.Authorization.EvaluateAsync(new McpOperatorAccessRequest(
            McpOperatorEnvironment.Production, principal, 17, agent, profile.Classification,
            McpOperatorOperationFamily.PolicyAdministration, "netratel_policy/" + canonical,
            new HashSet<string>(["netratel.mcp.admin"]), McpOperatorConfirmationClass.PolicyAdministration,
            "corr-reviewed-policy", "request-reviewed-policy", Tool: "netratel_policy"), default);
        Assert.Equal(includeConfirm, decision.IsAllowed);
    }

    [Fact]
    public async Task Owner_self_approval_is_denied_when_policy_authority_is_withdrawn_before_resume()
    {
        await using var fixture = await Fixture.CreateAsync();
        var owner = await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "requester");
        owner.IsInstanceAdministrator = true;
        await fixture.Identity.SaveChangesAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId,
            Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("requester"), fixture.Approval(pending));
        owner.IsInstanceAdministrator = false;
        await fixture.Identity.SaveChangesAsync();
        var denied = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("denied", denied.Request.Status);
        Assert.Equal("owner_self_approval", denied.Request.ApprovalMode);
        Assert.Equal("review_approver_authority_missing", denied.ReviewFailure!.Code);
        Assert.Empty(await fixture.Identity.IntegrationCredentialLifecycleEvents.Where(x => x.Action == "reconciled").ToArrayAsync());
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
    }

    [Fact]
    public async Task Current_admission_keeps_mixed_grants_and_unsupported_samples_explicit_without_policy_side_effects()
    {
        await using var fixture = await Fixture.CreateAsync();
        var row = await fixture.Identity.IntegrationCredentials.SingleAsync();
        row.Grants.Add(new IntegrationCredentialGrant { CredentialId = row.Id, TenantId = 17, Permission = NetRatelPermissions.TelemetryRead });
        await fixture.Identity.SaveChangesAsync();
        var admission = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", row.Id, McpOperatorEnvironment.Production);
        Assert.Equal(3, admission!.ConfiguredGrants.Count);
        Assert.All(admission.EffectiveAdmission, x => { Assert.Equal("netratel_flows", x.Target.Tool); Assert.Equal("list", x.Target.Operation); });
        Assert.Equal(2, admission.EffectiveAdmission.Count);
        Assert.False(admission.TargetsTruncated);
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
        var apiCredential = await fixture.Credentials.CreateAsync("requester", new("API only", IntegrationCredentialPurpose.Api,
            fixture.Clock.GetUtcNow().AddDays(1), [new(17, NetRatelPermissions.FlowRead)]));
        Assert.Null(await fixture.Pairing.InspectCredentialAdmissionAsync("requester", apiCredential.CredentialId, McpOperatorEnvironment.Production));
        await Assert.ThrowsAsync<IntegrationCredentialGrantValidationException>(() => fixture.Pairing.InspectCredentialAdmissionAsync("requester", row.Id, McpOperatorEnvironment.Production, [null!]));
    }

    [Theory]
    [InlineData("before_issue", false)]
    [InlineData("redelivery", false)]
    [InlineData("acknowledge", false)]
    [InlineData("before_issue", true)]
    [InlineData("redelivery", true)]
    [InlineData("acknowledge", true)]
    public async Task Protected_create_authority_or_binding_loss_retains_denial_reason_without_delivery_or_reissue(string stage, bool bindingChanged)
    {
        await using var fixture = await Fixture.CreateAsync();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Integration:HttpMcp:PublicResourceUri"] = Fixture.Resource }).Build();
        using var brandingOptions = new OptionsMonitor<DeploymentBrandingOptions>(
            new OptionsFactory<DeploymentBrandingOptions>([new ConfigureOptions<DeploymentBrandingOptions>(options => options.SiteUrl = Fixture.Origin)], []),
            [], new OptionsCache<DeploymentBrandingOptions>());
        var pairing = new IntegrationPairingService(fixture.Identity, fixture.Credentials,
            new IntegrationCredentialGrantValidator(fixture.Identity, fixture.App, fixture.EffectiveAccess, fixture.Clock),
            new EphemeralDataProtectionProvider(), fixture.Clock,
            branding: new DeploymentBrandingService(fixture.Identity, brandingOptions), configuration: configuration,
            applicationDb: fixture.App, effectiveAccess: fixture.EffectiveAccess,
            policyAdministration: new McpOperatorPolicyAdministration(fixture.App), operatorAuthorization: fixture.Authorization);
        var proposal = fixture.Proposal("create", null);
        var start = await pairing.StartReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var pending = await pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        await pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        IntegrationPairingDelivery? delivery = null;
        if (stage != "before_issue")
        {
            fixture.Clock.Advance(TimeSpan.FromSeconds(5));
            delivery = (await pairing.PollAsync(start.DeviceCapability)).Delivery;
            Assert.NotNull(delivery);
        }
        var approver = await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "reviewer");
        if (bindingChanged) configuration["Integration:HttpMcp:PublicResourceUri"] = "https://changed-deployment.example.test/mcp";
        else approver.IsInstanceAdministrator = false;
        await fixture.Identity.SaveChangesAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var denied = stage == "acknowledge"
            ? await pairing.AcknowledgeAsync(start.DeviceCapability, delivery!.CredentialId)
            : await pairing.PollAsync(start.DeviceCapability);
        Assert.Equal("denied", denied.Status);
        Assert.Null(denied.Delivery);
        var receipt = await pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        Assert.Equal("denied", receipt.Request.Status);
        Assert.Equal(bindingChanged ? "review_binding_changed" : "review_approver_authority_missing", receipt.ReviewFailure!.Code);
        Assert.Equal("resume", receipt.ReviewFailure.Stage);
        Assert.Contains("new request", receipt.NextAction);
        var row = await fixture.Identity.IntegrationPairingRequests.SingleAsync();
        Assert.Null(row.ProtectedDelivery);
        Assert.Null(row.DeliveryExpiresAtUtc);
        if (delivery is not null)
            Assert.Null(await fixture.Credentials.VerifyAsync(delivery.Secret, IntegrationCredentialPurpose.HttpMcp));
        var serialized = JsonSerializer.Serialize(receipt);
        Assert.DoesNotContain(start.DeviceCapability, serialized);
        Assert.DoesNotContain(fixture.Credential.Secret, serialized);
        if (delivery is not null) Assert.DoesNotContain(delivery.Secret, serialized);

        // Restoring authority or deployment settings never reopens the denied request.
        approver.IsInstanceAdministrator = true;
        configuration["Integration:HttpMcp:PublicResourceUri"] = Fixture.Resource;
        await fixture.Identity.SaveChangesAsync();
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var retry = await pairing.PollAsync(start.DeviceCapability);
        Assert.Equal("denied", retry.Status);
        Assert.Null(retry.Delivery);
        var resumed = await pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("denied", resumed.Request.Status);
        Assert.Equal(receipt.ReviewFailure, resumed.ReviewFailure);
        Assert.Equal(delivery is null ? 1 : 2, await fixture.Identity.IntegrationCredentials.CountAsync());
    }

    [Fact]
    public async Task Protected_create_retries_keep_one_credential_exact_expiry_and_revocation_is_effective()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("create", null);
        var start = await fixture.Pairing.StartReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var retry = await fixture.Pairing.StartReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        Assert.Equal(start.DeviceCapability, retry.DeviceCapability);
        var pending = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var delivery = (await fixture.Pairing.PollAsync(start.DeviceCapability)).Delivery!;
        Assert.NotNull(delivery);
        Assert.Equal(proposal.ExpiresAtUtc, delivery.ExpiresAtUtc);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var redelivery = (await fixture.Pairing.PollAsync(start.DeviceCapability)).Delivery!;
        Assert.Equal(delivery.CredentialId, redelivery.CredentialId);
        Assert.Equal(delivery.Secret, redelivery.Secret);
        Assert.Equal(2, await fixture.Identity.IntegrationCredentials.CountAsync());
        await fixture.Pairing.AcknowledgeAsync(start.DeviceCapability, delivery.CredentialId);
        Assert.NotNull(await fixture.Credentials.VerifyAsync(delivery.Secret, IntegrationCredentialPurpose.HttpMcp));
        var revokeProposal = fixture.Proposal("revoke", delivery.CredentialId) with { Grants = [], InstancePermissions = [], Targets = [] };
        var revoke = await fixture.Pairing.RequestReviewedAsync(revokeProposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(revoke));
        var revoked = await fixture.Pairing.InspectReviewedAsync(revokeProposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("revoked", revoked.CredentialState);
        Assert.Null(await fixture.Credentials.VerifyAsync(delivery.Secret, IntegrationCredentialPurpose.HttpMcp));
        Assert.DoesNotContain(delivery.Secret, JsonSerializer.Serialize(revoked));
    }

    [Fact]
    public async Task Agent_receipt_keeps_runtime_dependent_admission_unverified_instead_of_inventing_capability()
    {
        await using var fixture = await Fixture.CreateAsync();
        var role = fixture.Identity.AccessRoles.Local.Single();
        role.Permissions.Add(new AccessRolePermission { RoleId = role.Id, Permission = NetRatelPermissions.TelemetryRead });
        await fixture.Identity.SaveChangesAsync();
        var agentId = Guid.Parse("b2222222-2222-4222-8222-222222222222");
        fixture.App.Agents.Add(new Agent { Id = agentId, TenantId = 17, Name = "Synthetic offline target", IsEnabled = true, Status = AgentStatus.Active });
        await fixture.App.SaveChangesAsync();
        var administration = new McpOperatorPolicyAdministration(fixture.App);
        var profile = await administration.UpsertTargetProfileAsync(17, agentId, McpOperatorTargetClassification.DedicatedQa, [], null, "reviewer", default);
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId) with
        { Grants = [new(17, NetRatelPermissions.TelemetryRead)], Targets = [new(17, "netratel_clients", "telemetry", agentId, profile.Version)] };
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        var receipt = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        var admission = Assert.Single(receipt.EffectiveAdmission);
        Assert.True(admission.ApplicationGrantConfigured); Assert.True(admission.OwnerPermissionGranted); Assert.True(admission.TenantVisible);
        var current = await fixture.Pairing.InspectCredentialAdmissionAsync("requester", fixture.Credential.CredentialId,
            McpOperatorEnvironment.Production, proposal.Targets);
        var currentAgent = Assert.Single(current!.EffectiveAdmission);
        Assert.Equal("conditional_runtime_unverified", currentAgent.PolicyAdmissionStatus);
        Assert.False(currentAgent.AuthorizationAdmitted);
        Assert.Null(admission.PolicyAllowed); Assert.False(admission.AuthorizationAdmitted);
        Assert.Equal("conditional_runtime_unverified", admission.PolicyAdmissionStatus);
        Assert.Equal("target_runtime_not_checked", admission.FailureCode);
        Assert.Equal("not_checked", admission.CapabilityAvailability);
        var descriptor = McpOperatorOperationCatalog.Find("netratel_clients", "telemetry")!;
        var offline = await fixture.Authorization.EvaluateAsync(new(McpOperatorEnvironment.Production,
            new("requester", fixture.Credential.CredentialId, null, new HashSet<string>(), new HashSet<string>(["Operator"]), new HashSet<string>(["netratel.mcp.observe"])),
            17, agentId, profile.Classification, descriptor.OperationFamily, "netratel_clients/telemetry", new HashSet<string>(["netratel.mcp.observe"]),
            descriptor.ConfirmationClass, "offline-control", "offline-control", TargetOnline: false, CapabilityAvailable: false), default);
        Assert.False(offline.IsAllowed);
        Assert.Equal("capability_unavailable", offline.FailureCode);
    }

    [Fact]
    public async Task Exact_expiry_is_effective_immediately_before_and_at_boundary_and_changed_context_denies()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("create", null) with { ExpiresAtUtc = fixture.Clock.GetUtcNow().AddSeconds(10), Grants = [], Targets = [] };
        var started = await fixture.Pairing.StartReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        var pending = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId);
        await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId,
            currentResource: "https://other.example.test/mcp", currentEnvironment: McpOperatorEnvironment.Production));
        await Assert.ThrowsAsync<IntegrationPairingUnavailableException>(() => fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId,
            currentResource: Fixture.Resource, currentEnvironment: McpOperatorEnvironment.Development));
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        fixture.Clock.Advance(TimeSpan.FromSeconds(5));
        var issued = (await fixture.Pairing.PollAsync(started.DeviceCapability)).Delivery!;
        await fixture.Pairing.AcknowledgeAsync(started.DeviceCapability, issued.CredentialId);
        fixture.Clock.Advance(TimeSpan.FromSeconds(5).Subtract(TimeSpan.FromTicks(1)));
        Assert.NotNull(await fixture.Credentials.VerifyCurrentAsync(issued.CredentialId, IntegrationCredentialPurpose.HttpMcp));
        fixture.Clock.Advance(TimeSpan.FromTicks(1));
        Assert.Null(await fixture.Credentials.VerifyCurrentAsync(issued.CredentialId, IntegrationCredentialPurpose.HttpMcp));
    }

    [Fact]
    public async Task Withdrawn_approver_authority_prevents_applying_the_prior_approval()
    {
        await using var fixture = await Fixture.CreateAsync();
        var proposal = fixture.Proposal("reconcile", fixture.Credential.CredentialId);
        var pending = await fixture.Pairing.RequestReviewedAsync(proposal, "requester", fixture.Credential.CredentialId, Fixture.Origin, Fixture.Resource, McpOperatorEnvironment.Production);
        await fixture.Pairing.ApproveAsync(Principal("reviewer"), fixture.Approval(pending));
        (await fixture.Identity.Users.SingleAsync(x => x.PrincipalId == "reviewer")).IsInstanceAdministrator = false;
        await fixture.Identity.SaveChangesAsync();
        var denied = await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId, resume: true);
        Assert.Equal("denied", denied.Request.Status);
        Assert.Equal("review_approver_authority_missing", denied.ReviewFailure!.Code);
        Assert.Equal("resume", denied.ReviewFailure.Stage);
        Assert.Contains("new requestId", denied.NextAction);
        Assert.Equal(denied.ReviewFailure, (await fixture.Pairing.InspectReviewedAsync(proposal.RequestId, "requester", fixture.Credential.CredentialId)).ReviewFailure);
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim("netratel_principal_id", id)], "review-test"));
    internal sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2030-03-02T10:11:12Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
    internal sealed class TransientSaveFailureInterceptor : SaveChangesInterceptor
    {
        public bool FailNextSave { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (FailNextSave)
            {
                FailNextSave = false;
                throw new TimeoutException("Synthetic persistence timeout");
            }
            return ValueTask.FromResult(result);
        }
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public const string Origin = "https://netratel.example.test";
        public const string Resource = Origin + "/mcp";
        public FrozenClock Clock { get; } = new();
        public TransientSaveFailureInterceptor PersistenceFailure { get; } = new();
        public NetRatelIdentityDbContext Identity { get; }
        public OrchestratorDbContext App { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public IEffectiveAccessService EffectiveAccess { get; private set; } = null!;
        public IntegrationCredentialService Credentials { get; private set; } = null!;
        public IntegrationPairingService Pairing { get; private set; } = null!;
        public McpOperatorAuthorization Authorization { get; private set; } = null!;
        public IntegrationCredentialSecret Credential { get; private set; } = null!;
        private Fixture()
        {
            Identity = new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString())
                .ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).AddInterceptors(PersistenceFailure).Options);
        }
        public static async Task<Fixture> CreateAsync()
        {
            var f = new Fixture();
            f.App.Tenants.AddRange(new Tenant { Id = 17, Name = "Synthetic A" }, new Tenant { Id = 29, Name = "Synthetic B" });
            await f.App.SaveChangesAsync();
            f.Identity.Users.AddRange(new LocalUser { Id = "requester", PrincipalId = "requester", UserName = "requester", IsEnabled = true },
                new LocalUser { Id = "reviewer", PrincipalId = "reviewer", UserName = "reviewer", IsEnabled = true, IsInstanceAdministrator = true });
            var role = new AccessRole { Name = "Restricted flow reader" };
            role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.FlowRead });
            role.Permissions.Add(new AccessRolePermission { Permission = NetRatelPermissions.McpDiscoveryRead });
            f.Identity.AccessRoles.Add(role);
            foreach (var tenant in new int?[] { 17, 29, null }) f.Identity.PrincipalRoleAssignments.Add(new PrincipalRoleAssignment { PrincipalId = "requester", RoleId = role.Id, TenantId = tenant });
            await f.Identity.SaveChangesAsync();
            var access = new EffectiveAccessService(f.Identity, new ConfigurationBuilder().Build());
            f.EffectiveAccess = access;
            f.Credentials = new(f.Identity, f.Clock);
            f.Authorization = new(f.App);
            f.Pairing = new(f.Identity, f.Credentials, new IntegrationCredentialGrantValidator(f.Identity, f.App, access, f.Clock),
                new EphemeralDataProtectionProvider(), f.Clock, applicationDb: f.App, effectiveAccess: access,
                policyAdministration: new McpOperatorPolicyAdministration(f.App), operatorAuthorization: f.Authorization);
            f.Credential = await f.Credentials.CreateAsync("requester", new("Campaign requester", IntegrationCredentialPurpose.HttpMcp,
                f.Clock.GetUtcNow().AddDays(60), [new(17, NetRatelPermissions.FlowRead), new(29, NetRatelPermissions.FlowRead)], Resource, [NetRatelPermissions.McpDiscoveryRead]));
            return f;
        }
        public ReviewedMcpAccessProposal Proposal(string action, string? credential) => new(Guid.NewGuid().ToString(), "Synthetic bounded client", action,
            DateTimeOffset.Parse("2030-04-01T10:11:12Z"), [new(17, NetRatelPermissions.FlowRead), new(29, NetRatelPermissions.FlowRead)], [NetRatelPermissions.McpDiscoveryRead],
            [new(17, "netratel_flows", "list"), new(29, "netratel_flows", "list")], credential);
        public IntegrationPairingApprovalRequest Approval(ReviewedMcpAccessReceipt receipt) => new(receipt.Request.UserCode!, receipt.Request.Proposal.ExpiresAtUtc,
            receipt.Request.Proposal.Grants, receipt.Request.Proposal.InstancePermissions) { ReviewedProposalHash = receipt.Request.ProposalHash };
        public async ValueTask DisposeAsync() { await Identity.DisposeAsync(); await App.DisposeAsync(); }
    }
}
