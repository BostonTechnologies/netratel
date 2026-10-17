using AwesomeAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using NetRatel.Application.Operations;
using NetRatel.Infrastructure.Identity;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Infrastructure.Services;
using NetRatel.Shared.Connectivity;
using Xunit;
using System.Security.Claims;

namespace NetRatel.Tests.Infrastructure;

[Collection(PostgreSqlPersistenceCollection.Name)]
public sealed class ReviewedAccessRecoveryTests(PostgreSqlPersistenceFixture postgres)
{
    [Fact]
    public async Task Reviewed_creation_recovers_after_policy_commit_and_identity_rollback_without_duplicate_credential()
    {
        var connection = await postgres.CreateDatabaseAsync();
        var identityOptions = new DbContextOptionsBuilder<NetRatelIdentityDbContext>().UseNpgsql(connection).Options;
        var policyOptions = new DbContextOptionsBuilder<OrchestratorDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options;
        var protection = new EphemeralDataProtectionProvider();
        var clock = new RecoveryClock();
        var failure = new FailAfterFirstPolicyCommit();
        IntegrationPairingStartResponse started;
        var proposal = new ReviewedMcpAccessProposal(Guid.NewGuid().ToString("D"), "synthetic reviewed client", "create", clock.GetUtcNow().AddDays(7), [],
            [NetRatelPermissions.McpDiscoveryRead], [new(0, "netratel_auth", "status"), new(0, "netratel_health", "get")]);
        await using (var identity = new NetRatelIdentityDbContext(identityOptions))
        await using (var policies = new OrchestratorDbContext(policyOptions))
        {
            await identity.Database.MigrateAsync();
            identity.Users.Add(new LocalUser { Id = "recovery-owner-local", PrincipalId = "recovery-owner", UserName = "owner", IsEnabled = true });
            identity.Users.Add(new LocalUser { Id = "recovery-approver-local", PrincipalId = "recovery-approver", UserName = "approver", IsEnabled = true });
            await identity.SaveChangesAsync();
            var credentials = new IntegrationCredentialService(identity, clock);
            var ingress = await credentials.CreateAsync("recovery-owner", new("requesting-client", IntegrationCredentialPurpose.HttpMcp, clock.GetUtcNow().AddDays(2), [], "https://netratel.example.invalid/mcp", [NetRatelPermissions.McpDiscoveryRead]));
            var pairing = Pairing(identity, policies, credentials);
            started = await pairing.StartReviewedAsync(proposal, "recovery-owner", ingress.CredentialId, "https://netratel.example.invalid", "https://netratel.example.invalid/mcp", McpOperatorEnvironment.Production);
            var review = await pairing.ReviewAsync(started.UserCode);
            await pairing.ApproveAsync(new ClaimsPrincipal(new ClaimsIdentity([new Claim("netratel_principal_id", "recovery-approver")], "test")),
                new IntegrationPairingApprovalRequest(started.UserCode, proposal.ExpiresAtUtc, [], proposal.InstancePermissions) { ReviewedProposalHash = review.ReviewedAccess!.ProposalHash });
            clock.Advance(TimeSpan.FromSeconds(5));
            var interrupted = () => pairing.PollAsync(started.DeviceCapability);
            await interrupted.Should().ThrowAsync<IOException>().WithMessage("Synthetic interruption after committed policy");
            (await policies.McpOperatorPolicies.AsNoTracking().CountAsync()).Should().Be(1, "the policy authority committed independently of the pairing transaction");
        }
        await using (var identity = new NetRatelIdentityDbContext(identityOptions))
        await using (var policies = new OrchestratorDbContext(policyOptions))
        {
            (await identity.IntegrationCredentials.AsNoTracking().CountAsync()).Should().Be(1, "the interrupted issued credential rolled back with the identity transaction");
            clock.Advance(TimeSpan.FromSeconds(10));
            var credentials = new IntegrationCredentialService(identity, clock);
            var pairing = Pairing(identity, policies, credentials);
            var resumed = await pairing.PollAsync(started.DeviceCapability);
            resumed.Status.Should().Be("issued");
            var delivery = resumed.Delivery!;
            delivery.ExpiresAtUtc.Should().Be(proposal.ExpiresAtUtc);
            (await identity.IntegrationCredentials.AsNoTracking().CountAsync()).Should().Be(2, "there is one ingress credential and one issued credential");
            var attached = await policies.McpOperatorPolicies.AsNoTracking().ToArrayAsync();
            attached.Should().HaveCount(2).And.OnlyContain(policy => policy.PrincipalSelectorValue == delivery.CredentialId);
            clock.Advance(TimeSpan.FromSeconds(10));
            var replay = await pairing.PollAsync(started.DeviceCapability);
            replay.Delivery!.CredentialId.Should().Be(delivery.CredentialId);
            replay.Delivery.Secret.Should().Be(delivery.Secret);
            (await policies.McpOperatorPolicies.AsNoTracking().CountAsync()).Should().Be(2);
        }

        IntegrationPairingService Pairing(NetRatelIdentityDbContext identity, OrchestratorDbContext policies, IntegrationCredentialService credentials) =>
            new(identity, credentials, new AllowReviewedGrants(), protection, clock, applicationDb: policies,
                effectiveAccess: new PolicyApproverAccess(), policyAdministration: new InterruptingPolicyAdministration(new McpOperatorPolicyAdministration(policies), failure),
                operatorAuthorization: new McpOperatorAuthorization(policies), hostEnvironment: new RecoveryEnvironment());
    }

    private sealed class RecoveryClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
    private sealed class RecoveryEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = "reviewed-recovery-fixture";
        public string ContentRootPath { get; set; } = "/";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
    private sealed class AllowReviewedGrants : IIntegrationCredentialGrantValidator
    {
        public Task ValidateAsync(ClaimsPrincipal principal, IntegrationCredentialCreateRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
    private sealed class PolicyApproverAccess : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken cancellationToken = default) =>
            Task.FromResult(principal.FindFirstValue("netratel_principal_id") == "recovery-approver" && permission == NetRatelPermissions.McpPolicyAdministration);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class FailAfterFirstPolicyCommit { public bool Failed { get; set; } }
    private sealed class InterruptingPolicyAdministration(IMcpOperatorPolicyAdministration inner, FailAfterFirstPolicyCommit failure) : IMcpOperatorPolicyAdministration
    {
        public async Task<McpOperatorPolicy> CreateAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken)
        {
            var created = await inner.CreateAsync(draft, actorId, cancellationToken);
            if (!failure.Failed) { failure.Failed = true; throw new IOException("Synthetic interruption after committed policy"); }
            return created;
        }
        public Task<McpOperatorPolicyPage> ListAsync(McpOperatorEnvironment? environment, int? tenantId, CancellationToken cancellationToken) => inner.ListAsync(environment, tenantId, cancellationToken);
        public Task<McpOperatorPolicy?> GetAsync(Guid policyId, CancellationToken cancellationToken) => inner.GetAsync(policyId, cancellationToken);
        public Task<IReadOnlyList<McpOperatorPolicyChangeAudit>> ListChangeAuditsAsync(int? tenantId, Guid? policyId, Guid? agentId, CancellationToken cancellationToken) => inner.ListChangeAuditsAsync(tenantId, policyId, agentId, cancellationToken);
        public Task<McpOperatorAcceptedAuditPage> ListAcceptedAuditsAsync(McpOperatorAcceptedAuditFilter filter, CancellationToken cancellationToken) => inner.ListAcceptedAuditsAsync(filter, cancellationToken);
        public Task ValidateDraftAsync(McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken) => inner.ValidateDraftAsync(draft, actorId, cancellationToken);
        public Task<McpOperatorPolicy> ReplaceAsync(Guid policyId, long expectedVersion, McpOperatorPolicyDraft draft, string actorId, CancellationToken cancellationToken) => inner.ReplaceAsync(policyId, expectedVersion, draft, actorId, cancellationToken);
        public Task<McpOperatorPolicy?> DisableAsync(Guid policyId, long expectedVersion, string actorId, CancellationToken cancellationToken) => inner.DisableAsync(policyId, expectedVersion, actorId, cancellationToken);
        public Task<McpOperatorTargetProfile?> GetTargetProfileAsync(int tenantId, Guid agentId, CancellationToken cancellationToken) => inner.GetTargetProfileAsync(tenantId, agentId, cancellationToken);
        public Task<McpOperatorTargetProfile> UpsertTargetProfileAsync(int tenantId, Guid agentId, McpOperatorTargetClassification classification, IReadOnlyCollection<string> tags, long? expectedVersion, string actorId, CancellationToken cancellationToken) => inner.UpsertTargetProfileAsync(tenantId, agentId, classification, tags, expectedVersion, actorId, cancellationToken);
    }
}
