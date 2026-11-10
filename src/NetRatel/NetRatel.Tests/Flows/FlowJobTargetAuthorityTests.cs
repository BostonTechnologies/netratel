using System.Security.Claims;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NetRatel.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using NetRatel.Application.Flows;
using NetRatel.Application.Jobs;
using NetRatel.Infrastructure.Flows;
using NetRatel.Infrastructure.Identity.Authorization;
using NetRatel.Infrastructure.Persistence;
using NetRatel.Shared.Contracts.Flows;
using NetRatel.Shared.Contracts.Jobs;
using Xunit;

namespace NetRatel.Tests.Flows;

public sealed class FlowJobTargetAuthorityTests
{
    [Fact]
    public async Task Whole_target_authority_rejects_one_foreign_or_disabled_member_and_does_not_borrow_group_management_rights()
    {
        await using var db = new OrchestratorDbContext(new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var own = Guid.NewGuid();
        var foreign = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Tenants.Add(new() { Id = 7, Name = "Synthetic tenant" });
        db.Agents.AddRange(new() { Id = own, TenantId = 7, Name = "synthetic-own", CreatedAtUtc = now },
            new() { Id = foreign, TenantId = 8, Name = "synthetic-foreign", CreatedAtUtc = now });
        db.Jobs.Add(new() { Id = 41, TenantId = 7, Name = "Runtime job", CreatedAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
        var definition = new JobDefinitionInfo(41, "Runtime job", "/", null, 7, "", now, now, null)
        { Target = new() { Source = JobTargetSource.RuntimeInput, RuntimeInput = new(JobRuntimeTargetKind.ClientId, "ClientId") } };
        var identity = new ExecutionAdmissionIdentity(Guid.NewGuid(), 7, "synthetic-operator", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1);
        var service = new FlowExecutionAuthority(db, new SourceAuthority(true), new ManagePermission(), null!, TimeProvider.System);
        await service.RequireTargetsAllowedAsync(identity, definition, [own], default);
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() => service.RequireTargetsAllowedAsync(identity, definition, [own, foreign], default));
        db.Agents.Local.Single(agent => agent.Id == own).IsEnabled = false;
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() => service.RequireTargetsAllowedAsync(identity, definition, [own], default));
        db.Agents.Local.Single(agent => agent.Id == own).IsEnabled = true;
        await db.SaveChangesAsync();
        var manager = new FlowExecutionAuthority(db, new SourceAuthority(false), new ManagePermission(), null!, TimeProvider.System);
        await Assert.ThrowsAsync<ExecutionAdmissionDeniedException>(() => manager.RequireTargetsAllowedAsync(identity, definition, [own], default));
        Assert.Empty(await db.JobRuns.ToArrayAsync());
    }

    [Fact]
    public async Task Saved_OIDC_operator_facts_expire_and_integration_credentials_remain_attenuated_for_Flow_targets()
    {
        await using var identityDb = new NetRatelIdentityDbContext(new DbContextOptionsBuilder<NetRatelIdentityDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var saved = new ApplicationPrincipal { Id = "synthetic-operator", ExternalAuthorityExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(5),
            ExternalAuthorityClaimsJson = JsonSerializer.Serialize(new[] { new ExternalAuthorityClaim("roles", "Operator") }) };
        identityDb.ApplicationPrincipals.Add(saved);
        var credential = new IntegrationCredential { Id = "synthetic-credential", PublicId = "synthetic-credential", TokenPrefix = "synthetic",
            SecretHash = "synthetic-test-hash", OwnerPrincipalId = saved.Id, Purpose = IntegrationCredentialPurpose.Api, Name = "Scoped Flow credential",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10),
            Grants = [new() { CredentialId = "synthetic-credential", TenantId = 7, Permission = NetRatelPermissions.FlowExecute }] };
        identityDb.IntegrationCredentials.Add(credential);
        await identityDb.SaveChangesAsync();
        var access = new EffectiveAccessService(identityDb, new ConfigurationBuilder().Build());
        var verifier = new FlowCurrentAuthorityVerifier(identityDb, access);
        Assert.True(await verifier.AuthorizeAsync(7, new(saved.Id)));
        Assert.True(await verifier.AuthorizeAsync(7, new(saved.Id, credential.Id)));
        Assert.False(await access.AuthorizeAsync(FlowAuthorityPrincipal.Create(new(saved.Id, credential.Id)), NetRatelPermissions.JobManagement, 7));
        Assert.False(await verifier.AuthorizeAsync(8, new(saved.Id, credential.Id)));
        saved.ExternalAuthorityExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        await identityDb.SaveChangesAsync();
        Assert.False(await verifier.AuthorizeAsync(7, new(saved.Id)));
        Assert.False(await verifier.AuthorizeAsync(7, new(saved.Id, credential.Id)));
    }

    private sealed class SourceAuthority(bool allowed) : IFlowExecutionAuthorityVerifier
    {
        public Task<bool> AuthorizeAsync(int tenantId, FlowExecutionAuthorityDto authority, CancellationToken ct = default)
            => Task.FromResult(allowed && tenantId == 7);
    }
    private sealed class ManagePermission : IEffectiveAccessService
    {
        public Task<bool> AuthorizeAsync(ClaimsPrincipal principal, string permission, int? tenantId, CancellationToken ct = default)
            => Task.FromResult(tenantId == 7 && permission == NetRatelPermissions.JobManagement);
        public Task<EffectiveAccessSnapshot> GetSnapshotAsync(ClaimsPrincipal principal, int? tenantId, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task ReconcileBuiltInRolesAsync(CancellationToken ct = default) => Task.CompletedTask;
    }
}
