using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Connectivity;
using Xunit;

namespace NetRatel.Tests.Infrastructure;

public sealed class ReviewedMcpAccessTests
{
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
        Assert.Empty(await fixture.App.McpOperatorPolicies.ToArrayAsync());
    }

    private static ClaimsPrincipal Principal(string id) => new(new ClaimsIdentity([new Claim("netratel_principal_id", id)], "review-test"));
    internal sealed class FrozenClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2030-03-02T10:11:12Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan span) => _now += span;
    }
    internal sealed class Fixture : IAsyncDisposable
    {
        public const string Origin = "https://netratel.example.test";
        public const string Resource = Origin + "/mcp";
        public FrozenClock Clock { get; } = new();
        public NetRatelIdentityDbContext Identity { get; } = new(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).ConfigureWarnings(x => x.Ignore(InMemoryEventId.TransactionIgnoredWarning)).Options);
        public OrchestratorDbContext App { get; } = new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        public IEffectiveAccessService EffectiveAccess { get; private set; } = null!;
        public IntegrationCredentialService Credentials { get; private set; } = null!;
        public IntegrationPairingService Pairing { get; private set; } = null!;
        public McpOperatorAuthorization Authorization { get; private set; } = null!;
        public IntegrationCredentialSecret Credential { get; private set; } = null!;
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
